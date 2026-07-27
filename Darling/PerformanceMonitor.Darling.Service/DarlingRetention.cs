/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Storage;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// Daily retention purge for the Darling Postgres store. The extension-free baseline is
/// DELETE-based and works on any Postgres; when the worker detected TimescaleDB
/// (<c>timescaleAvailable</c> — see TimescaleSupport in Darling.Storage) the collector tables
/// purge via hypertable <c>drop_chunks</c> instead, which detaches whole expired chunks in O(1)
/// instead of scanning rows. collection_log — a hypertable since V23, though converted directly by the
/// V23 migration rather than the catalog loop — purges the SAME way (drop_chunks with a DELETE fallback for
/// a plain-PostgreSQL store). config_alert_log and config.config_command stay DELETE-based either way
/// (never converted — plain config-side registry tables), as do the analysis tables
/// (PgFindingStore.CleanupOldFindingsAsync owns those). Retention horizons are the shared
/// per-collector <see cref="CollectorScheduleDefaults"/> (identity-pinned to Lite's
/// ScheduleManager table), so both SKUs keep the same data horizons out of the box. NOTE: Lite
/// archives expired rows to parquet before deleting (ArchiveService); Darling deliberately
/// purges without archiving — with Timescale, the compression policy on old chunks IS the
/// archival tier (compressed chunks stay queryable), and the plain-PG story remains
/// purge-without-archive for now.
/// <para>Every sweep writes one AUDITABLE run-record to collection_log under a fleet-sentinel server_id
/// (<see cref="DarlingObservability.FleetServerId"/>, never a real server) — SUCCESS, WARNING (some
/// tables failed their statement, already logged + isolated), or ERROR — so a stalled or partial purge is
/// visible in the collection log, not just the service log. The DELETE path drains each table in
/// one-day time slices (<see cref="TimeSlicedDeleteSql"/> — the compressed-chunk-safe successor to the
/// Dashboard's DELETE TOP idiom); collection_log is kept 2x the base data-retention window so a
/// run-record outlives the metric rows it explains.</para>
/// </summary>
public static class DarlingRetention
{
    /// <summary>
    /// The base data-retention window the collection_log horizon is a multiple of. 30 days matches the
    /// dominant collector <see cref="CollectorScheduleDefaults"/> horizon and the Dashboard's
    /// <c>@effective_retention_days</c> default (config.data_retention).
    /// </summary>
    internal const int DataRetentionBaseDays = 30;

    /// <summary>
    /// collection_log isn't a collector, so it has no <see cref="CollectorScheduleDefaults"/> entry to carry
    /// its horizon. It is kept at 2x the base window (mirrors the Dashboard's <c>retention_date x2</c> rule)
    /// so a collector run-record survives long enough to diagnose WHY a collector failed AFTER its metric
    /// rows have aged out — a 30-day metric row and its failure log would otherwise expire together, erasing
    /// the evidence. Effectively 60 days.
    /// </summary>
    internal const int CollectionLogRetentionDays = DataRetentionBaseDays * 2;

    /// <summary>
    /// config_alert_log (the fired-alert history: what alerted + delivery status, read by the viewer Alert
    /// History tab and the get_alert_history MCP tool) is a plain <c>config</c>-schema registry table — NOT a
    /// collector (so it has no <see cref="CollectorScheduleDefaults"/> horizon) and NOT a hypertable (so it
    /// purges via batched DELETE, never drop_chunks). It is INSERT-only (PgAlertHistoryStore) with no other
    /// purge path, so without a horizon it grows unbounded — the same class as the #1471 findings-cleanup gap.
    /// Kept 90 days (a quarter): alert history is low-volume and a valuable audit trail, so the horizon is
    /// generous, but it is BOUNDED. No operator setting governs this today (config_alert_settings carries the
    /// cooldown / lookback knobs, not an alert-history horizon), so this constant is the single source of truth.
    /// </summary>
    internal const int AlertHistoryRetentionDays = 90;

    /// <summary>
    /// config.config_command (the imperative command queue the Viewer/MCP/CLI enqueue into and the service
    /// executes) keeps its TERMINAL rows this long. Unlike config_alert_log this is not an audit surface
    /// anyone reads — nothing in the viewer or the MCP tools queries command HISTORY; a caller polls its own
    /// command_id for a terminal result and moves on — so the retained rows exist purely for post-hoc "what did
    /// the service actually do" forensics, which is worth exactly as long as the metric data they would be
    /// correlated against (<see cref="DataRetentionBaseDays"/>). It is also higher-volume than alert history:
    /// every viewer live-plan / actual-plan / active-queries fetch enqueues a row, which is why it gets the
    /// base window rather than the alert log's generous 90 days.
    /// </summary>
    internal const int CommandHistoryRetentionDays = DataRetentionBaseDays;

    /// <summary>
    /// The terminal-status filter for the command purge — the two states
    /// <c>ViewerDataService.IsTerminal</c> recognizes, which are also the only two
    /// <c>DarlingCommandExecutor</c> ever writes (its report path and its stale-command reaper). A
    /// <c>pending</c> or <c>in_progress</c> row is NEVER purged no matter how old: deleting a live command
    /// would strand the caller polling it, and an ancient pending row means an operator queued something the
    /// service has not run yet — the reaper's job, not retention's.
    /// </summary>
    internal const string TerminalCommandStatuses = "status IN ('succeeded', 'failed')";

    /* Each one-day slice is bounded work (see TimeSlicedDeleteSql); the generous 300s per-slice command
       timeout (well above Npgsql's 30s default) is belt-and-suspenders for a slow disk. Slicing is also what
       keeps a large first purge from ever hitting a timeout at all — a single unbounded DELETE could roll
       back on a long backlog and never catch up (retried tomorrow with a day MORE to delete). */
    private const int DeleteTimeoutSeconds = 300;

    /// <summary>
    /// Purges every collector table past its shared <see cref="CollectorScheduleDefaults"/>
    /// RetentionDays, plus collection_log past <see cref="CollectionLogRetentionDays"/>,
    /// config_alert_log past <see cref="AlertHistoryRetentionDays"/>, and terminal
    /// config.config_command rows past <see cref="CommandHistoryRetentionDays"/>.
    /// When <paramref name="timescaleAvailable"/> (the worker's startup detection), the
    /// collector tables purge via <c>drop_chunks</c> (<see cref="DropChunksSqlFor"/>) with a
    /// per-table DELETE fallback so a table that failed hypertable conversion still honors its
    /// horizon; when false, the extension-free DELETE path runs unchanged. collection_log is
    /// DELETE-based either way. Failure-isolated per table: one failed statement is logged as a
    /// warning and the sweep continues (that table is retried on the next purge). Safe on a
    /// fresh/empty store — a purge that matches nothing removes nothing. Returns a coarse
    /// activity count: rows deleted by the DELETE paths plus whole chunks dropped by
    /// drop_chunks (Timescale doesn't report per-row counts for dropped chunks).
    /// </summary>
    /// <param name="retentionDaysFor">
    /// Optional resolver for a collector's effective retention horizon (control-plane fleet-wide overrides
    /// layered on <see cref="CollectorScheduleDefaults"/>). Null (or a value it does not override) uses the
    /// shared default. A per-server override cannot apply here — the purge is per shared table, not per server.
    /// The on-demand <c>purge_now</c> command passes a <c>_ =&gt; customDays</c> resolver for its custom-N mode.
    /// </param>
    /// <returns>
    /// A <see cref="PurgeSummary"/>: how many tables were touched and the coarse activity count (DELETE rows
    /// plus dropped chunks). The daily caller discards it; the on-demand <c>purge_now</c> command reports it.
    /// </returns>
    public static async Task<PurgeSummary> PurgeAsync(
        NpgsqlDataSource postgres, bool timescaleAvailable, ILogger? logger, CancellationToken cancellationToken,
        Func<string, int>? retentionDaysFor = null)
    {
        var sw = Stopwatch.StartNew();
        var tablesPurged = 0;
        var totalRowsDeleted = 0;
        var totalChunksDropped = 0;
        var tablesFailed = 0;

        /* Naive-UTC storage: Npgsql 6+ rejects Kind=Utc against `timestamp` — see PgCollectorRowWriter. */
        var utcNow = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        try
        {
            foreach (var definition in CollectorCatalog.All)
            {
                if (!CollectorScheduleDefaults.All.TryGetValue(definition.Name, out var schedule))
                {
                    /* Impossible while the retention coverage test holds — belt-and-suspenders so schedule
                       drift degrades to a loud warning (and a WARNING run-record) instead of killing the sweep. */
                    logger?.LogWarning("Retention purge: no schedule entry for '{Collector}' — {Table} was not purged",
                        definition.Name, definition.TargetTable);
                    tablesFailed++;
                    continue;
                }

                /* Clamp at the destructive sink (belt-and-suspenders with the resolver + the V17 CHECK): a
                   retention of 0/negative would flip the cutoff into the present/future and drop_chunks /
                   DELETE the entire table. Never purge with a horizon under 1 day. */
                var retentionDays = Math.Max(1, retentionDaysFor?.Invoke(definition.Name) ?? schedule.RetentionDays);

                if (timescaleAvailable)
                {
                    var dropped = await DropChunksOneAsync(
                        postgres, definition.TargetTable, DropChunksSqlFor(definition, retentionDays),
                        logger, cancellationToken);
                    if (dropped is not null)
                    {
                        tablesPurged++;
                        totalChunksDropped += dropped.Value;
                        continue;
                    }

                    /* drop_chunks failed (warned) — most likely this one table failed hypertable
                       conversion and is still plain. Fall back to the extension-free DELETE so the
                       table still honors its horizon instead of growing unbounded. */
                }

                var deleted = await PurgeOneAsync(
                    postgres, definition.TargetTable, DeleteSqlFor(definition),
                    utcNow.AddDays(-retentionDays), logger, cancellationToken);
                if (deleted is not null)
                {
                    tablesPurged++;
                    totalRowsDeleted += deleted.Value;
                }
                else
                {
                    tablesFailed++;
                }
            }

            /* #1767 payload dimensions. query_text_dim / query_plan_dim are plain tables holding one copy
               of each distinct query text / plan XML; the fact rows carry only a digest. They must be
               bounded or they re-create the very problem they solve — ~23 MB/hour of distinct plans on the
               measured field instance is ~200 GB/year if nothing ever expires.

               The obvious sweep (delete dim rows no live fact references) is an anti-join against two
               hypertables per dim row and is not affordable at this size. Instead the write path stamps
               last_seen on every cycle that references a digest, so last_seen is never older than the newest
               fact row pointing at it, and the GC is an index range scan on last_seen — run through the SAME
               time-sliced DELETE every sibling purge uses, rather than one unbounded statement. That matters
               most on the FIRST sweep after an upgrade, which is the one with a whole retention window of
               expired content to clear: unsliced, that is a single transaction holding a lock and generating
               WAL proportional to the entire backlog, which is exactly what the slicing exists to bound.

               The horizon is the WIDEST effective fact retention of the two tables — resolved through the
               same resolver the fact purge above uses, so a raised per-collector override can never outlive
               the dims and orphan a reader — plus a margin covering the two ways a fact can outlive its
               nominal horizon: drop_chunks only drops a chunk once its WHOLE range is past the cutoff (up to
               one ChunkIntervalDays of extra rows), and the upsert refreshes last_seen at most hourly. */
            var widestFactRetentionDays = 1;
            foreach (var definition in CollectorCatalog.All)
            {
                if (PayloadDimensions.ForTable(definition.TargetTable).Count == 0)
                {
                    continue;
                }

                var factRetentionDays = Math.Max(1, retentionDaysFor?.Invoke(definition.Name)
                    ?? (CollectorScheduleDefaults.All.TryGetValue(definition.Name, out var dimSchedule)
                        ? dimSchedule.RetentionDays
                        : 1));
                widestFactRetentionDays = Math.Max(widestFactRetentionDays, factRetentionDays);
            }

            var dimensionCutoff = utcNow.AddDays(-(widestFactRetentionDays + TimescaleSupport.ChunkIntervalDays + 1));
            foreach (var dimTable in PayloadDimensions.DimTables)
            {
                var dimDeleted = await PurgeOneAsync(
                    postgres, dimTable, TimeSlicedDeleteSql(dimTable, PayloadDimensions.LastSeenColumn),
                    dimensionCutoff, logger, cancellationToken);
                if (dimDeleted is not null)
                {
                    tablesPurged++;
                    totalRowsDeleted += dimDeleted.Value;
                }
                else
                {
                    tablesFailed++;
                }
            }

            /* collection_log retention. Since V23 it is a TimescaleDB hypertable (converted DIRECTLY by the V23
               migration — it is NOT in CollectorCatalog.All, so the loop above skips it), so with Timescale it
               purges via drop_chunks in O(1) — no DELETE churn — at its own 2x horizon (CollectionLogRetentionDays).
               On plain PostgreSQL, or if its hypertable conversion failed, it falls back to the batched DELETE so
               the horizon is ALWAYS honored. Failure-isolated like every sibling. The FleetServerId=0 retention
               run-record sentinel is a genuine collection_log row and lives in a chunk normally. */
            var logPurged = false;
            if (timescaleAvailable)
            {
                var droppedLog = await DropChunksOneAsync(
                    postgres, "collection_log", DropChunksSqlFor("collection_log", CollectionLogRetentionDays),
                    logger, cancellationToken);
                if (droppedLog is not null)
                {
                    tablesPurged++;
                    totalChunksDropped += droppedLog.Value;
                    logPurged = true;
                }

                /* drop_chunks failed (warned) — most likely collection_log's conversion failed and it is still
                   plain. Fall through to the extension-free DELETE so it still honors its horizon. */
            }

            if (!logPurged)
            {
                var logDeleted = await PurgeOneAsync(
                    postgres, "collection_log", TimeSlicedDeleteSql("collection_log", "collection_time"),
                    utcNow.AddDays(-CollectionLogRetentionDays), logger, cancellationToken);
                if (logDeleted is not null)
                {
                    tablesPurged++;
                    totalRowsDeleted += logDeleted.Value;
                }
                else
                {
                    tablesFailed++;
                }
            }

            /* config_alert_log (the fired-alert history) is a plain config-schema registry table, never a
               hypertable, so it purges via the same batched DELETE as collection_log — on its alert_time
               column, at the AlertHistoryRetentionDays horizon. INSERT-only (PgAlertHistoryStore) with no
               other purge path, so without this it grows unbounded (the #1471 findings-cleanup class of bug).
               Failure-isolated like every sibling: a failed statement is warned + counted, the sweep goes on. */
            var alertLogDeleted = await PurgeOneAsync(
                postgres, "config_alert_log", TimeSlicedDeleteSql("config_alert_log", "alert_time"),
                utcNow.AddDays(-AlertHistoryRetentionDays), logger, cancellationToken);
            if (alertLogDeleted is not null)
            {
                tablesPurged++;
                totalRowsDeleted += alertLogDeleted.Value;
            }
            else
            {
                tablesFailed++;
            }

            /* config.config_command (the imperative command queue) — the backstop the viewer's per-command
               cleanup already ASSUMED existed ("the service-side purge is the backstop",
               ViewerDataService.RunTestConnectAsync) but which nothing implemented (#1651). The viewer deletes
               its own row for exactly four self-cleaning flows, best-effort with the exception swallowed; every
               other command type (pause/resume, snapshot_now, analyze_now, purge_now, the enable/firewall
               verbs, collector toggles, anything from MCP or the CLI) left a terminal row and its result_json
               behind forever, as did those four whenever the delete failed or the viewer died mid-poll.
               SCHEMA-QUALIFIED deliberately: unlike collection_log / config_alert_log (created bare, so they
               live in `collect` under search_path = collect, config, public), this table really is in `config`,
               and a bare name here would resolve to a nonexistent collect.config_command — a purge that fails
               every night into a warning nobody reads. Keyed on created_at (NOT NULL, so no row can slip past
               the horizon by never being stamped) and filtered to terminal rows, never a live command.
               Failure-isolated like every sibling. */
            var commandsDeleted = await PurgeOneAsync(
                postgres, "config.config_command",
                TimeSlicedDeleteSql("config.config_command", "created_at", TerminalCommandStatuses),
                utcNow.AddDays(-CommandHistoryRetentionDays), logger, cancellationToken);
            if (commandsDeleted is not null)
            {
                tablesPurged++;
                totalRowsDeleted += commandsDeleted.Value;
            }
            else
            {
                tablesFailed++;
            }

            var summary = new PurgeSummary(tablesPurged, totalRowsDeleted, totalChunksDropped);
            logger?.LogInformation(
                "Retention purge: {Tables} table(s) purged, {Rows} row(s) deleted, {Chunks} chunk(s) dropped, {Failed} failed, {ElapsedMs}ms",
                tablesPurged, totalRowsDeleted, totalChunksDropped, tablesFailed, sw.ElapsedMilliseconds);

            /* Auditable run-record: a clean sweep writes SUCCESS, a sweep where one or more tables failed
               their statement writes WARNING (the per-table failures were already logged + isolated above).
               Fleet-wide, so it lands under the sentinel server_id (DarlingObservability.LogRetentionRunAsync),
               which is failure-isolated and never breaks the loop. */
            var (status, message) = BuildRunRecordSummary(tablesPurged, totalRowsDeleted, totalChunksDropped, tablesFailed);
            await DarlingObservability.LogRetentionRunAsync(
                postgres, status, summary.TotalPurged, sw.ElapsedMilliseconds, message, logger, cancellationToken);

            return summary;
        }
        catch (OperationCanceledException)
        {
            /* Shutdown/cancellation — propagate exactly like the per-table helpers (no ERROR run-record; the
               purge simply didn't finish and retries on the next daily tick). */
            throw;
        }
        catch (Exception ex)
        {
            /* The per-table helpers isolate their own failures, so reaching here means something unexpected
               escaped the loop. Record an ERROR run-record for the audit trail and return what we managed to
               purge — PurgeAsync must never throw at the daily caller (it is not wrapped there), so a broken
               purge surfaces as an auditable ERROR row, not a crashed collection loop. */
            logger?.LogError("Retention purge failed: {Message}", ex.Message);
            await DarlingObservability.LogRetentionRunAsync(
                postgres, "ERROR", totalRowsDeleted + totalChunksDropped, sw.ElapsedMilliseconds, ex.Message, logger, cancellationToken);
            return new PurgeSummary(tablesPurged, totalRowsDeleted, totalChunksDropped);
        }
    }

    /// <summary>
    /// The status + human message for the auditable run-record of a completed sweep (not the exception path,
    /// which writes a literal ERROR): SUCCESS when every table purged cleanly, WARNING when
    /// <paramref name="tablesFailed"/> &gt; 0 (some table's statement failed — already logged + isolated).
    /// Pure so the SUCCESS/WARNING branch and the message text are unit-testable without a live store.
    /// </summary>
    internal static (string Status, string Message) BuildRunRecordSummary(
        int tablesPurged, int totalRowsDeleted, int totalChunksDropped, int tablesFailed)
    {
        var status = tablesFailed == 0 ? "SUCCESS" : "WARNING";
        var message = tablesFailed == 0
            ? $"Purged {tablesPurged.ToString(CultureInfo.InvariantCulture)} table(s): {totalRowsDeleted.ToString(CultureInfo.InvariantCulture)} row(s) deleted, {totalChunksDropped.ToString(CultureInfo.InvariantCulture)} chunk(s) dropped"
            : $"Purged {tablesPurged.ToString(CultureInfo.InvariantCulture)} table(s), {tablesFailed.ToString(CultureInfo.InvariantCulture)} failed (see prior warnings): {totalRowsDeleted.ToString(CultureInfo.InvariantCulture)} row(s) deleted, {totalChunksDropped.ToString(CultureInfo.InvariantCulture)} chunk(s) dropped";
        return (status, message);
    }

    /// <summary>
    /// The batched purge statement for one collector table — deletes expired rows one time slice at a time
    /// on the definition's own prefix time column ("collection_time" almost everywhere; the config snapshots
    /// purge on "capture_time"), executed in a loop until the table is drained (<see cref="PurgeOneAsync"/>).
    /// Table and column names come from the shared catalog constants, never from user input, so
    /// interpolation is safe here — the same reasoning as the runner's watermark read
    /// (DarlingCollectorRunner.GetLastCollectedTimeAsync).
    /// </summary>
    internal static string DeleteSqlFor(ICollectorSchemaInfo schema)
        => TimeSlicedDeleteSql(schema.TargetTable, schema.PrefixTimeColumnName);

    /// <summary>
    /// The batched purge statement: delete the OLDEST one-day slice of expired rows per execution —
    /// <c>WHERE {col} &lt; $1 AND {col} in [min expired, min expired + 1 day)</c> — repeated until a slice
    /// deletes nothing. This replaced a <c>ctid IN (SELECT ctid … LIMIT 10000)</c> row-cap idiom (#1564):
    /// reading the <c>ctid</c> system column through TimescaleDB's transparent decompression is unsupported
    /// ("transparent decompression only supports tableoid system column" — reproduced on the pinned 2.28.1),
    /// so the moment ANY in-range chunk was compressed the whole statement errored and the table silently
    /// kept its expired rows. A plain time-range predicate instead rides TimescaleDB's DML decompression,
    /// which IS supported on compressed chunks — and is a no-op cost on plain tables.
    /// <para>The slice width doubles as the work bound: one day of arrival volume per statement — exactly
    /// what a steady-state daily purge deletes in total, so a long backlog is drained in day-sized units the
    /// store already sustains daily (the old 10k row cap served the same goal; a day is also precisely one
    /// chunk on a hypertable, <see cref="TimescaleSupport.ChunkIntervalDays"/>). Both <c>min({col})</c>
    /// subqueries evaluate against the same statement snapshot, so they are always equal; when no expired
    /// rows remain, <c>min</c> is NULL, every comparison is unknown, zero rows delete, and the drain loop
    /// stops. In production the hypertables purge via <c>drop_chunks</c>, so this path runs on plain tables
    /// (a plain-PostgreSQL store, config_alert_log) or as the fallback when a hypertable's
    /// <c>drop_chunks</c> failed — where compressed chunks are LIKELY, which is what makes the
    /// compressed-safe shape load-bearing. <c>$1</c> is bound once and referenced by all three positions.
    /// Table/column come from catalog constants (never user input), so interpolation is safe.</para>
    /// <para><paramref name="extraPredicate"/> narrows WHICH rows are eligible — currently only
    /// <see cref="TerminalCommandStatuses"/>, so the command purge cannot touch a live command. It is
    /// applied to the DELETE <b>and to both <c>min()</c> subqueries</b>, which is load-bearing, not cosmetic:
    /// with the predicate on the DELETE alone, a slice anchored on an INELIGIBLE row's timestamp would
    /// delete zero rows, and the drain loop's "a slice that clears nothing means we are done" termination
    /// would stop the purge with older eligible rows still in the table. Like the table and column it is a
    /// compile-time constant, never user input.</para>
    /// </summary>
    internal static string TimeSlicedDeleteSql(string table, string timeColumn, string? extraPredicate = null)
    {
        var and = extraPredicate is null ? string.Empty : $" AND {extraPredicate}";
        var expired = $"{timeColumn} < $1{and}";

        return $"DELETE FROM {table} WHERE {expired}"
             + $" AND {timeColumn} >= (SELECT min({timeColumn}) FROM {table} WHERE {expired})"
             + $" AND {timeColumn} < (SELECT min({timeColumn}) FROM {table} WHERE {expired}) + INTERVAL '{TimescaleSupport.ChunkIntervalDays} days'";
    }

    /// <summary>
    /// The Timescale purge statement for one collector table — <c>drop_chunks</c> detaches every
    /// chunk wholly older than the horizon (validated live on TimescaleDB 2.28.1; the partition
    /// column is implicit in the hypertable's dimension, so no time column appears here). An
    /// accepted coarseness: drop_chunks only drops WHOLE chunks, so rows inside a
    /// partially-expired chunk survive until the entire chunk ages past the horizon (with Darling's
    /// 1-day chunk interval — <see cref="!:TimescaleSupport.ChunkIntervalDays"/> — up to ~1 day of grace) —
    /// the trade for a metadata-only purge that never scans or rewrites rows. RetentionDays comes from the shared
    /// <see cref="CollectorScheduleDefaults"/> constants, never from user input, so
    /// interpolation is safe here — the same reasoning as <see cref="DeleteSqlFor"/>.
    /// </summary>
    internal static string DropChunksSqlFor(ICollectorSchemaInfo schema, int retentionDays)
        => DropChunksSqlFor(schema.TargetTable, retentionDays);

    /// <summary>
    /// The <c>drop_chunks</c> statement for a hypertable by raw table name — the collection_log path (a
    /// hypertable since V23 but outside the collector catalog, so it has no <see cref="ICollectorSchemaInfo"/>).
    /// Same shape as the schema overload; the table name comes from a compile-time constant, never user input,
    /// so interpolation is safe.
    /// </summary>
    internal static string DropChunksSqlFor(string table, int retentionDays)
        => $"SELECT drop_chunks('{table}', older_than => make_interval(days => {retentionDays}))";

    /// <summary>
    /// One table's drop_chunks; returns the number of chunks dropped, or null when it failed
    /// (warned; the caller falls back to DELETE for that table). drop_chunks returns one row per
    /// dropped chunk, so the count comes from reading the result set.
    /// </summary>
    private static async Task<int?> DropChunksOneAsync(
        NpgsqlDataSource postgres,
        string tableName,
        string dropChunksSql,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(dropChunksSql, connection) { CommandTimeout = DeleteTimeoutSeconds };

            var chunksDropped = 0;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                chunksDropped++;
            }

            return chunksDropped;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* Failure-isolated per table — warned here, then the caller's DELETE fallback runs. */
            logger?.LogWarning("Retention purge (drop_chunks) failed for {Table} — falling back to DELETE: {Message}",
                tableName, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// One table's batched DELETE: re-executes <paramref name="deleteSql"/> (a <see cref="TimeSlicedDeleteSql"/>
    /// statement clearing the oldest one-day slice of expired rows) until a slice deletes nothing — i.e. the
    /// table is drained. Returns the total rows deleted across all slices, or null when it failed (warned,
    /// sweep continues). Slicing bounds lock/WAL/dead-tuple growth on a large first purge; a small
    /// steady-state purge finishes in one slice. The connection and command (with its single bound cutoff
    /// parameter) are reused across the loop.
    /// </summary>
    private static async Task<int?> PurgeOneAsync(
        NpgsqlDataSource postgres,
        string tableName,
        string deleteSql,
        DateTime cutoff,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await postgres.OpenConnectionAsync(cancellationToken);

            /* Deleting a whole expired slice from a COMPRESSED chunk (the drop_chunks-failed fallback)
               decompresses every affected segment, and TimescaleDB caps that at 100k tuples per DML
               transaction by default — a rail against accidental bulk decompression, which a retention
               purge deliberately is. Lift it for this connection only. On a store without the extension
               the qualified name is accepted as a placeholder GUC, so this is safe everywhere. */
            using (var lift = new NpgsqlCommand(
                "SET timescaledb.max_tuples_decompressed_per_dml_transaction = 0", connection))
            {
                await lift.ExecuteNonQueryAsync(cancellationToken);
            }

            using var command = new NpgsqlCommand(deleteSql, connection) { CommandTimeout = DeleteTimeoutSeconds };
            command.Parameters.AddWithValue(cutoff);

            /* batchSize 1: the time-sliced statement has no row cap, so "fewer than the cap" degenerates
               to "deleted zero rows" — a slice that clears anything means older slices may remain. */
            return await DrainBatchesAsync(ct => command.ExecuteNonQueryAsync(ct), batchSize: 1, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* Failure-isolated per table — one stuck DELETE must not stop the sweep. */
            logger?.LogWarning("Retention purge failed for {Table}: {Message}", tableName, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Repeatedly runs <paramref name="executeBatch"/> (one capped batched-DELETE execution, returning its
    /// rows-affected) and sums the total, stopping when a batch clears fewer than <paramref name="batchSize"/>
    /// rows — i.e. no expired rows remain and the table is drained. A full-cap batch means there may be more,
    /// so it goes again; an exact multiple of the cap terminates on the following empty batch. Pure over the
    /// injected executor so the loop-again + termination is unit-testable without a live store.
    /// </summary>
    internal static async Task<int> DrainBatchesAsync(
        Func<CancellationToken, Task<int>> executeBatch, int batchSize, CancellationToken cancellationToken)
    {
        var totalDeleted = 0;
        while (true)
        {
            var deleted = await executeBatch(cancellationToken);
            totalDeleted += deleted;

            if (deleted < batchSize)
            {
                break;
            }
        }

        return totalDeleted;
    }
}

/// <summary>
/// The outcome of one <see cref="DarlingRetention.PurgeAsync"/> sweep: how many tables were touched
/// (<paramref name="TablesPurged"/>) and the coarse activity count split into DELETE rows
/// (<paramref name="RowsDeleted"/>) and dropped Timescale chunks (<paramref name="ChunksDropped"/> —
/// drop_chunks doesn't report per-row counts). <see cref="TotalPurged"/> is the single headline number the
/// daily log and the on-demand <c>purge_now</c> result report.
/// </summary>
public readonly record struct PurgeSummary(int TablesPurged, int RowsDeleted, int ChunksDropped)
{
    /// <summary>Rows deleted plus whole chunks dropped — the coarse "how much did this purge remove" count.</summary>
    public int TotalPurged => RowsDeleted + ChunksDropped;
}
