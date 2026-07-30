/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PerformanceMonitor.Collectors;

/// <summary>Per-run outcome of the enumeration driver: rows written and the summed SQL/storage slice times.</summary>
public readonly record struct EnumeratedRunResult(int Rows, long SqlMs, long StorageMs);

/// <summary>
/// The shared control-flow driver for the enumeration collectors' per-item loop (#1556). Both hosts
/// (Lite → DuckDB, Darling → Postgres) ran a byte-identical per-item loop that accumulated EVERY
/// database's rows into one list before a single write — the shape that let one 24-server query_store
/// cycle balloon to 13GB. Extracting the loop here does two things at once: it FLUSHES each item's
/// batch before reading the next (so peak memory is one database's rows, not the fleet's), and it
/// removes the duplicate that let the same defect live in two runners.
///
/// <para>
/// The driver owns only the control flow — iteration, cancellation, the per-item catch SHAPE, the
/// per-item flush, and the interleaved SQL/storage timing. Everything app-specific stays in the
/// caller's delegates: the SQL connection and per-item query (readItem), the storage engine
/// (writeBatch), the host store's per-database watermark read and its 24h clamp (perItemWatermark),
/// and the log text / display name (onItemComplete / onItemError). This is the seam the plan required:
/// no app or collector semantics leak into the shared loop.
/// </para>
/// </summary>
public static class EnumeratedCollectorDriver
{
    /// <summary>
    /// What both hosts put on the collection_log row when an enumerated collector's enumeration query
    /// returned NO items — so the driver never even runs. That cycle records SUCCESS with 0 rows, which
    /// on its own is indistinguishable from a healthy collector whose databases were simply quiet; it is
    /// equally the shape of query_store enumerating zero Query-Store-enabled databases, or
    /// index_object_stats being filtered down to nothing. The status deliberately stays SUCCESS (this is
    /// not a failure, and #1837's health-banding design is the larger fix); this message is the
    /// fixed, greppable breadcrumb that says WHY the row is empty. Shared so the two runners cannot
    /// drift on the wording the operator greps for.
    /// </summary>
    public const string EmptyEnumerationMessage = "enumeration yielded 0 items - nothing to collect this cycle";

    /// <summary>
    /// Runs the per-item loop: for each item, (optionally) refresh its per-database watermark, read its
    /// rows, surface the cap/byte-budget WARNING, then flush that batch before moving on.
    /// </summary>
    /// <param name="items">The enumerated items (database names), already listed by the caller.</param>
    /// <param name="perItemWatermark">
    /// Refreshes <see cref="CollectorContext.Watermark"/> for this item before its query is built — the
    /// per-database watermark read plus its clamp. Null when the definition has no per-database watermark
    /// (the single server-wide watermark already sits on the context).
    /// </param>
    /// <param name="readItem">
    /// The SQL phase: builds the per-item query, runs it, and materializes the batch. Returns a non-null
    /// (possibly empty) list. Its wall time is summed into <see cref="EnumeratedRunResult.SqlMs"/>.
    /// </param>
    /// <param name="writeBatch">
    /// The storage phase: writes ONE item's batch to the host store. Skipped for an empty batch. Its wall
    /// time is summed into <see cref="EnumeratedRunResult.StorageMs"/>. A flush failure PROPAGATES —
    /// storage failure is systemic, and batches already flushed stay committed (commit-1..N-1 on abort).
    /// </param>
    /// <param name="onItemComplete">Per-item completion hook (item, batch count, SQL ms, storage ms),
    /// invoked after a successful read AND its flush (#1565: the hosts log a per-database line from this,
    /// so a burst on one database is visible instead of blending into the per-server total; they also
    /// surface the row-cap / byte-budget warning here — the context truncation signal persists until the
    /// next item's read resets it, so reading it post-flush is equivalent).</param>
    /// <param name="onItemError">Per-item skip log, invoked when one item fails (offline DB, timeout, permissions).</param>
    public static async Task<EnumeratedRunResult> RunAsync<TRow>(
        IReadOnlyList<string> items,
        Func<string, CancellationToken, Task>? perItemWatermark,
        Func<string, CancellationToken, Task<List<TRow>>> readItem,
        Func<List<TRow>, CancellationToken, Task> writeBatch,
        Action<string, int, long, long> onItemComplete,
        Action<string, Exception> onItemError,
        CancellationToken cancellationToken)
    {
        var totalRows = 0;
        long sqlMs = 0;
        long storageMs = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<TRow>? batch = null;
            long itemSqlMs = 0;
            var sqlSlice = Stopwatch.StartNew();
            try
            {
                /* Per-database watermark refresh (query_store): its cutoff — including the 24h catch-up
                   clamp — is computed HERE, inside the loop, so each database's commit advances only its
                   own watermark and an abort loses no other database's intervals. */
                if (perItemWatermark is not null)
                {
                    await perItemWatermark(item, cancellationToken);
                }

                batch = await readItem(item, cancellationToken);
            }
            catch (OutOfMemoryException)
            {
                /* OOM is filtered OUT of the per-item skip below and rethrown: it is fatal to this run,
                   not a routine one-database skip. There is no cross-item accumulator to clear — the
                   per-item batch is a local that unwinds with this frame — so filter+rethrow is the whole
                   handler; the host classifies the run ERROR. */
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                /* One item failing is routine (an offline/mid-restore database, a permissions oddity, a
                   timeout) — skip it and keep collecting the rest, matching the original per-item loop.
                   OCE and OOM deliberately propagate (they are not per-item faults). */
                onItemError(item, ex);
            }
            finally
            {
                itemSqlMs = sqlSlice.ElapsedMilliseconds;
                sqlMs += itemSqlMs;
            }

            /* A null batch means the read faulted and was skipped above; a successful read is a non-null
               (possibly empty) list. */
            if (batch is null)
            {
                continue;
            }

            /* Empty batch: no COPY/appender opened (rows_collected = Σ non-empty batch counts). */
            long itemStorageMs = 0;
            if (batch.Count > 0)
            {
                var storageSlice = Stopwatch.StartNew();
                await writeBatch(batch, cancellationToken);
                itemStorageMs = storageSlice.ElapsedMilliseconds;
                storageMs += itemStorageMs;
                totalRows += batch.Count;
            }

            /* Completion hook AFTER the flush so it carries both per-item slices (#1565). The context
               truncation signal is still this item's — the next read resets it. */
            onItemComplete(item, batch.Count, itemSqlMs, itemStorageMs);
        }

        return new EnumeratedRunResult(totalRows, sqlMs, storageMs);
    }
}
