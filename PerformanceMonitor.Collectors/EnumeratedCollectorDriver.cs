/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PerformanceMonitor.Collectors;

/// <summary>Per-run outcome of the enumeration driver: rows written and the summed SQL/storage slice times.</summary>
public readonly record struct EnumeratedRunResult(int Rows, long SqlMs, long StorageMs);

/// <summary>
/// One item the enumeration query could not PROBE (#1837): the enumeration reached the item but the
/// per-item eligibility check failed — a database mid-restore, a login that cannot enter it, a
/// cross-database reference the target rejected. Distinct from a per-item COLLECTION failure, which the
/// driver's own <c>onItemError</c> already reports: this one happens before the item ever reaches the
/// driver, so without this contract it can only vanish.
/// </summary>
public readonly record struct EnumerationProbeFailure(string Item, string Error);

/// <summary>
/// What the shared enumeration read produced: the item list both runners iterate, the items whose probe
/// failed, and the collection-log note (null on the ordinary path) the host attaches to the run's row.
/// </summary>
public sealed record EnumerationOutcome(
    IReadOnlyList<string> Items,
    IReadOnlyList<EnumerationProbeFailure> ProbeFailures,
    string? Note);

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
    /// The collection-log note for an enumeration that reported PROBE failures (#1837), <c>{0}</c> = how
    /// many items failed. Deliberately a count and a pointer rather than the errors themselves: the note
    /// column is a one-line summary read at a glance in Collection Health, and one unlucky server can fail
    /// to probe hundreds of databases. The per-item text goes to the app log (capped at
    /// <see cref="MaxLoggedProbeFailures"/> lines), which is what "see the app log" means.
    /// </summary>
    public const string ProbeFailureNoteFormat = "{0} item(s) failed their enumeration probe - see the app log for the per-item errors";

    /// <summary>
    /// How many per-item probe failures each host writes to the app log before collapsing the rest into the
    /// count. A server whose login cannot enter ANY database fails every database's probe, and neither host
    /// should turn that into hundreds of log lines per cycle.
    /// </summary>
    public const int MaxLoggedProbeFailures = 5;

    /// <summary>
    /// The per-item app-log line for one probe failure, written by both hosts (at most
    /// <see cref="MaxLoggedProbeFailures"/> of them per cycle). Shared as a const so the wording an
    /// operator greps for is identical in Lite and Darling, and so each host's logging call keeps a
    /// constant message template.
    /// </summary>
    public const string ProbeFailureLogTemplate =
        "{Collector} on '{Server}': enumeration could not probe [{Item}] - {Error}";

    /// <summary>
    /// The one line that closes out a capped probe-failure burst, so the suppressed remainder is never
    /// silent — the failure mode this whole contract exists to end.
    /// </summary>
    public const string ProbeFailureOverflowLogTemplate =
        "{Collector} on '{Server}': {Total} item(s) failed their enumeration probe; {Suppressed} beyond the first {Shown} not logged.";

    /// <summary>
    /// The item name reported for a malformed probe-failure result set — see
    /// <see cref="ReadEnumerationAsync"/>. Not a database name, so it cannot collide with one.
    /// </summary>
    public const string ContractViolationItem = "(enumeration)";

    /// <summary>
    /// What a probe-failure result set with the wrong shape reports. It is turned into a probe failure
    /// rather than thrown so that a bad enumeration surfaces through the very mechanism this contract
    /// exists for — a note in Collection Health plus an app-log line — instead of failing an otherwise
    /// working collection cycle.
    /// </summary>
    public const string ContractViolationError =
        "the enumeration's second result set must be (item_name, error_text); probe failures were not read";

    /// <summary>Stand-in for a probe failure whose error column came back NULL — the failure still counts.</summary>
    public const string NoErrorText = "(no error text)";

    /// <summary><see cref="ProbeFailureNoteFormat"/> parsed once (CA1863) — the const stays the greppable, pinnable text.</summary>
    private static readonly CompositeFormat s_probeFailureNote = CompositeFormat.Parse(ProbeFailureNoteFormat);

    /// <summary>
    /// Reads an enumeration query's result: the item list, then the OPTIONAL SECOND RESULT SET of
    /// (item_name, error_text) rows describing items the enumeration could not probe (#1837).
    ///
    /// <para>
    /// The second result set exists because probe failures cannot ride the first one: the first result set
    /// IS the item list both runners consume as database names, so anything added to it would be collected
    /// from. Before this contract, the on-prem query_store enumeration swallowed every per-database probe
    /// failure in an empty CATCH and reported the survivors — a login that could not enter a single
    /// database produced zero items, one SUCCESS row, and no evidence anywhere.
    /// </para>
    ///
    /// <para>
    /// An enumeration that returns ONE result set behaves exactly as before: no second set means no probe
    /// failures, no note, nothing logged. Shared between the hosts so the item read, the failure read, and
    /// the note WORDING cannot drift — the reason #1556 moved the per-item loop here in the first place.
    /// </para>
    /// </summary>
    /// <param name="reader">An open reader positioned on the enumeration's first result set.</param>
    public static async Task<EnumerationOutcome> ReadEnumerationAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var items = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(reader.GetString(0));
        }

        var probeFailures = await ReadProbeFailuresAsync(reader, cancellationToken);
        return new EnumerationOutcome(items, probeFailures, BuildNote(items.Count == 0, probeFailures.Count));
    }

    /// <summary>
    /// Composes the collection-log note for one enumeration: the empty-enumeration breadcrumb, the
    /// probe-failure summary, both (all probes failed, so nothing was enumerable), or null for the
    /// ordinary path. The single place either note text is built, so the two hosts write the same string
    /// and an operator can grep for one wording.
    /// </summary>
    public static string? BuildNote(bool enumerationWasEmpty, int probeFailureCount)
    {
        var probeNote = probeFailureCount > 0
            ? string.Format(CultureInfo.InvariantCulture, s_probeFailureNote, probeFailureCount)
            : null;

        return (enumerationWasEmpty, probeNote) switch
        {
            (true, null) => EmptyEnumerationMessage,
            (true, not null) => $"{EmptyEnumerationMessage}; {probeNote}",
            (false, _) => probeNote,
        };
    }

    /// <summary>
    /// Advances to the optional second result set and reads its (item_name, error_text) rows. No second
    /// result set — the shape every enumeration had before #1837 — returns an empty list.
    /// </summary>
    private static async Task<IReadOnlyList<EnumerationProbeFailure>> ReadProbeFailuresAsync(
        DbDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken))
        {
            return Array.Empty<EnumerationProbeFailure>();
        }

        /* A second result set that is not (item_name, error_text) is a first-party SQL defect. Report it
           AS a probe failure instead of throwing: this contract exists so silent enumeration problems
           become visible, and killing the collection cycle to say so would trade one invisible defect for
           a loud unrelated one. */
        if (reader.FieldCount < 2)
        {
            return new[] { new EnumerationProbeFailure(ContractViolationItem, ContractViolationError) };
        }

        var failures = new List<EnumerationProbeFailure>();
        while (await reader.ReadAsync(cancellationToken))
        {
            /* GetValue().ToString() rather than GetString(): a second result set with the right column
               COUNT but a non-string column would throw InvalidCastException out of here and fail the
               whole collection cycle — the outcome the arity check above deliberately avoids. Read
               loosely and the malformed set still reports itself as a probe failure. */
            var item = reader.IsDBNull(0) ? ContractViolationItem : reader.GetValue(0).ToString() ?? ContractViolationItem;
            var error = reader.IsDBNull(1) ? NoErrorText : reader.GetValue(1).ToString() ?? NoErrorText;
            failures.Add(new EnumerationProbeFailure(item, error));
        }

        return failures;
    }

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
