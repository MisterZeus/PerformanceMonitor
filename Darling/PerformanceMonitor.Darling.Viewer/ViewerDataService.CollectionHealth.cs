/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Collection Health tab's three ported reads (W1i), copied from Lite's
/// <c>LocalDataService.CollectionHealth.cs</c> and run on the <c>v_collection_log</c> passthrough view.
/// This REPLACES the shell's placeholder Collection Health read (the DISTINCT-ON latest-run-per-collector
/// query and its simple <c>CollectorHealthRow</c> record, both removed from <c>ViewerDataService.cs</c>)
/// with Lite's rich 7-day aggregate: per-collector run/success/error counts, average duration, last
/// success / last run / last error timestamps, and the <see cref="CollectorHealthRow.HealthStatus"/>
/// banding. The SQL is byte-portable between DuckDB and Postgres (positional params, plain aggregates),
/// so only the parameter binding differs: window <see cref="DateTime"/>s go in with
/// <c>DateTimeKind.Unspecified</c> (the naive-UTC store convention), and the SUM/COUNT/AVG results are
/// read type-agnostically (Postgres returns <c>bigint</c> for the counts and <c>numeric</c> for AVG,
/// where DuckDB returned HUGEINT/DECIMAL) — the same intent as Lite's <c>ToInt64</c>/<c>ToDouble</c>
/// helpers, minus the DuckDB BigInteger case Postgres never produces.
/// <para>
/// The <b>Health Summary</b> aggregate keeps Lite's fixed 7-day horizon (its staleness banding needs a
/// stable window regardless of the toolbar). The <b>Collection Log</b> read (feeding the log grid + the
/// Duration Trends chart) diverges from Lite in ONE deliberate way: it bounds <c>collection_time</c> on
/// BOTH sides so the per-server toolbar's custom From/To is honored EXACTLY — Lite (and this file's first
/// port) took a single now-relative <c>hoursBack</c> lower bound, which rounded a custom range to a
/// hours-back-from-now span.
/// </para>
/// </summary>
public sealed partial class ViewerDataService
{
    /// <summary>
    /// Lite's 7-day per-collector health aggregate (<c>GetCollectionHealthAsync</c>) verbatim: one row
    /// per collector over the trailing window, with the SKIPPED-counts-as-a-healthy-run rule baked into
    /// the last-success MAX and the PERMISSIONS bucket split out for the NO_PERMISSIONS banding. $1
    /// server_id, $2 window start (naive UTC).
    /// </summary>
    public const string CollectionHealthSql = """
        SELECT
            collector_name,
            COUNT(*) AS total_runs,
            SUM(CASE WHEN status = 'SUCCESS' THEN 1 ELSE 0 END) AS success_count,
            SUM(CASE WHEN status = 'ERROR' THEN 1 ELSE 0 END) AS error_count,
            AVG(duration_ms) AS avg_duration_ms,
            -- SKIPPED counts as a healthy run (dedup / version-gated collectors no-op without being stale)
            MAX(CASE WHEN status IN ('SUCCESS', 'SKIPPED') THEN collection_time END) AS last_success_time,
            MAX(collection_time) AS last_run_time,
            MAX(CASE WHEN status IN ('ERROR', 'PERMISSIONS') THEN error_message END) AS last_error,
            MAX(CASE WHEN status IN ('ERROR', 'PERMISSIONS') THEN collection_time END) AS last_error_time,
            SUM(CASE WHEN status = 'PERMISSIONS' THEN 1 ELSE 0 END) AS permission_denied_count,
            -- YIELDED = the 1s LOCK_TIMEOUT guard fired (#1805): deliberate, benign for collection,
            -- counted apart from errors because clustering here is a signal about the TARGET's lock
            -- contention rather than a monitoring fault.
            SUM(CASE WHEN status = 'YIELDED' THEN 1 ELSE 0 END) AS yield_count
        FROM v_collection_log
        WHERE server_id = $1
        AND   collection_time >= $2
        GROUP BY collector_name
        ORDER BY collector_name
        """;

    /// <summary>
    /// #1591: how many DISTINCT collectors were permission-denied in the window — the badge count for the
    /// Collection Health tab header. Lite's twin is
    /// <c>LocalDataService.GetPermissionDeniedCollectorCountAsync</c>.
    ///
    /// <para>Its own narrow COUNT rather than a reuse of <see cref="CollectionHealthSql"/>: that one is
    /// per-collector and only runs when its tab is selected, which is exactly why a permission problem stayed
    /// invisible until someone thought to look. Counts collectors, not rows, so one collector failing every cycle
    /// for a week reads as "1" rather than a meaningless four-figure number.</para>
    /// </summary>
    public const string PermissionDeniedCollectorCountSql = """
        SELECT COUNT(DISTINCT collector_name)
        FROM v_collection_log
        WHERE server_id = $1
        AND   collection_time >= $2
        AND   status = 'PERMISSIONS'
        """;

    /// <summary>Runs <see cref="PermissionDeniedCollectorCountSql"/> over the same 7-day window the health grid uses.</summary>
    public async Task<int> GetPermissionDeniedCollectorCountAsync(int serverId, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(PermissionDeniedCollectorCountSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-7), DateTimeKind.Unspecified) });

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null or DBNull ? 0 : Convert.ToInt32(scalar);
    }

    /// <summary>
    /// The fleet-cumulative variant of <see cref="CollectionHealthSql"/>: the same 10-column per-collector
    /// aggregate but across ALL enabled monitored servers (GROUP BY server_id, collector_name — one row per
    /// server/collector pair), for the status bar's aggregate-view total (mirrors Lite's cumulative
    /// GetHealthSummary(null)). Scoped to enabled servers so a removed server's aged-out rows don't read as
    /// erroring. $1 window start (naive UTC).
    /// </summary>
    public const string FleetCollectionHealthSql = """
        SELECT
            collector_name,
            COUNT(*) AS total_runs,
            SUM(CASE WHEN status = 'SUCCESS' THEN 1 ELSE 0 END) AS success_count,
            SUM(CASE WHEN status = 'ERROR' THEN 1 ELSE 0 END) AS error_count,
            AVG(duration_ms) AS avg_duration_ms,
            MAX(CASE WHEN status IN ('SUCCESS', 'SKIPPED') THEN collection_time END) AS last_success_time,
            MAX(collection_time) AS last_run_time,
            MAX(CASE WHEN status IN ('ERROR', 'PERMISSIONS') THEN error_message END) AS last_error,
            MAX(CASE WHEN status IN ('ERROR', 'PERMISSIONS') THEN collection_time END) AS last_error_time,
            SUM(CASE WHEN status = 'PERMISSIONS' THEN 1 ELSE 0 END) AS permission_denied_count,
            SUM(CASE WHEN status = 'YIELDED' THEN 1 ELSE 0 END) AS yield_count
        FROM v_collection_log
        WHERE collection_time >= $1
        AND   server_id IN (SELECT server_id FROM config_monitored_servers WHERE is_enabled)
        GROUP BY server_id, collector_name
        """;

    /// <summary>
    /// The Collection Log sub-tab's recent-log read, adapted from Lite's <c>GetRecentCollectionLogAsync</c>
    /// to honor the per-server toolbar's settable window EXACTLY: the collection_log rows for one server
    /// between the window's start and end bounds, newest first, capped. Where Lite (and the shell's first
    /// port) passed a single now-relative <c>hoursBack</c> lower bound — so a custom From/To rounded to a
    /// hours-back-from-now span — this bounds <c>collection_time</c> on BOTH sides, matching how the Wait
    /// Stats / Blocking tabs window their reads. $1 server_id, $2 window start, $3 window end (all naive
    /// UTC), $4 row cap.
    /// </summary>
    public const string RecentCollectionLogSql = """
        SELECT
            collector_name,
            collection_time,
            duration_ms,
            sql_duration_ms,
            duckdb_duration_ms,
            rows_collected,
            status,
            error_message,
            server_name
        FROM v_collection_log
        WHERE server_id = $1
        AND   collection_time >= $2
        AND   collection_time <= $3
        ORDER BY collection_time DESC
        LIMIT $4
        """;

    /// <summary>
    /// Lite's per-collector drill read (<c>GetCollectionLogByCollectorAsync</c>) verbatim: every
    /// collection_log row for one collector on one server since the window start, newest first. Feeds
    /// the CollectionLogWindow the Health Summary grid opens on double-click. $1 server_id, $2
    /// collector_name, $3 window start (naive UTC).
    /// </summary>
    public const string CollectionLogByCollectorSql = """
        SELECT
            collector_name,
            collection_time,
            duration_ms,
            sql_duration_ms,
            duckdb_duration_ms,
            rows_collected,
            status,
            error_message,
            server_name
        FROM v_collection_log
        WHERE server_id = $1
        AND   collector_name = $2
        AND   collection_time >= $3
        ORDER BY collection_time DESC
        """;

    /// <summary>
    /// Per-collector health summary for one server over the trailing 7 days. Copied from Lite's
    /// <c>GetCollectionHealthAsync</c> — same window, same columns, HealthStatus computed on the row.
    /// </summary>
    public async Task<List<CollectorHealthRow>> GetCollectionHealthAsync(int serverId, CancellationToken cancellationToken = default)
    {
        var items = new List<CollectorHealthRow>();

        await using var command = _dataSource.CreateCommand(CollectionHealthSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-7), DateTimeKind.Unspecified),
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapHealthRow(reader));
        }

        return items;
    }

    /// <summary>
    /// Fleet-cumulative per-collector health across ALL enabled monitored servers over the trailing 7 days —
    /// the status bar's aggregate-view total (Overview / Alert History / FinOps / Recommendations), where Lite
    /// shows a cumulative count rather than one server's. ONE query (<see cref="FleetCollectionHealthSql"/>)
    /// regardless of fleet size; each row is a (server, collector) pair carrying its own
    /// <see cref="CollectorHealthRow.HealthStatus"/>, so the caller counts total + FAILING exactly as per-server.
    /// </summary>
    public async Task<List<CollectorHealthRow>> GetFleetCollectionHealthAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<CollectorHealthRow>();

        await using var command = _dataSource.CreateCommand(FleetCollectionHealthSql);
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-7), DateTimeKind.Unspecified),
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapHealthRow(reader));
        }

        return items;
    }

    /// <summary>Maps one row of the shared 11-column health projection (per-server or fleet) to a <see cref="CollectorHealthRow"/>.</summary>
    private static CollectorHealthRow MapHealthRow(NpgsqlDataReader reader) => new()
    {
        CollectorName = reader.GetString(0),
        TotalRuns = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1)),
        SuccessCount = reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetValue(2)),
        ErrorCount = reader.IsDBNull(3) ? 0 : Convert.ToInt64(reader.GetValue(3)),
        AvgDurationMs = reader.IsDBNull(4) ? 0 : Convert.ToDouble(reader.GetValue(4)),
        LastSuccessTime = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
        LastRunTime = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
        LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
        LastErrorTime = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
        PermissionDeniedCount = reader.IsDBNull(9) ? 0 : Convert.ToInt64(reader.GetValue(9)),
        YieldCount = reader.IsDBNull(10) ? 0 : Convert.ToInt64(reader.GetValue(10)),
    };

    /// <summary>
    /// Collection_log entries for one server between the window's naive-UTC start/end bounds, most recent
    /// first (500-row cap). Feeds the Collection Log sub-tab grid and the Duration Trends chart. The window
    /// is the per-server toolbar's settable range (<c>GetWindowUtc</c>): a preset ends "now", a custom
    /// From/To bounds EXACTLY — unlike the old hours-back read, a custom range no longer rounds to a
    /// hours-back-from-now span. Mirrors how <see cref="GetDistinctWaitTypesAsync"/> windows its read.
    /// </summary>
    public async Task<List<CollectionLogRow>> GetRecentCollectionLogAsync(int serverId, DateTime startUtc, DateTime endUtc, int maxRows = 500, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(RecentCollectionLogSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(startUtc, DateTimeKind.Unspecified),
        });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(endUtc, DateTimeKind.Unspecified),
        });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = maxRows });

        return await ReadCollectionLogAsync(command, cancellationToken);
    }

    /// <summary>
    /// Collection_log entries for a specific collector on one server, most recent first. Copied from
    /// Lite's <c>GetCollectionLogByCollectorAsync</c> (default 168-hour / 7-day window). Feeds the
    /// CollectionLogWindow drill.
    /// </summary>
    public async Task<List<CollectionLogRow>> GetCollectionLogByCollectorAsync(int serverId, string collectorName, int hoursBack = 168, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(CollectionLogByCollectorSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = collectorName });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-hoursBack), DateTimeKind.Unspecified),
        });

        return await ReadCollectionLogAsync(command, cancellationToken);
    }

    /// <summary>Shared reader for the two collection-log projections (identical column list).</summary>
    private static async Task<List<CollectionLogRow>> ReadCollectionLogAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var items = new List<CollectionLogRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new CollectionLogRow
            {
                CollectorName = reader.GetString(0),
                CollectionTime = reader.GetDateTime(1),
                DurationMs = reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2)),
                SqlDurationMs = reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3)),
                DuckDbDurationMs = reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4)),
                RowsCollected = reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5)),
                Status = reader.GetString(6),
                ErrorMessage = reader.IsDBNull(7) ? null : reader.GetString(7),
                ServerName = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }

        return items;
    }
}

/// <summary>
/// One row of the Collection Log grid / drill window — a single collector run's outcome. Copied
/// VERBATIM from Lite's <c>CollectionLogRow</c> (LocalDataService.CollectionHealth.cs): every display
/// property is a pure format of stored values, and <see cref="CollectionTimeFormatted"/> routes the
/// store's naive-UTC collection_time through <see cref="ViewerTimeHelper.ForDisplay"/> — the viewer's
/// mode-aware Server/Local/UTC conversion every other Darling timestamp also uses.
/// <see cref="DuckDbDurationMs"/> keeps its store column name (<c>duckdb_duration_ms</c>)
/// but in the Darling store that column records the POSTGRES write phase — the Collection Log grid
/// labels it "Store (ms)".
/// </summary>
public class CollectionLogRow
{
    public string CollectorName { get; set; } = "";
    public string? ServerName { get; set; }
    public DateTime CollectionTime { get; set; }
    public int? DurationMs { get; set; }
    public int? SqlDurationMs { get; set; }

    /// <summary>Stored under the legacy <c>duckdb_duration_ms</c> column name; in the Darling store it
    /// carries the Postgres storage-phase milliseconds. Surfaced as the grid's "Store (ms)" column.</summary>
    public int? DuckDbDurationMs { get; set; }
    public int? RowsCollected { get; set; }
    public string Status { get; set; } = "";
    public string? ErrorMessage { get; set; }

    public string CollectionTimeFormatted => ViewerTimeHelper.ForDisplay(CollectionTime).ToString("g");

    public string DurationFormatted => DurationMs.HasValue
        ? (DurationMs.Value < 1000 ? $"{DurationMs.Value} ms" : $"{DurationMs.Value / 1000.0:F1} s")
        : "";

    public string SqlDurationFormatted => SqlDurationMs.HasValue ? $"{SqlDurationMs.Value} ms" : "";

    public string DuckDbDurationFormatted => DuckDbDurationMs.HasValue ? $"{DuckDbDurationMs.Value} ms" : "";
}

/// <summary>
/// One Collection Health "Health Summary" grid row — a collector's 7-day roll-up with its health band.
/// Copied VERBATIM from Lite's rich <c>CollectorHealthRow</c> (LocalDataService.CollectionHealth.cs);
/// it REPLACES the shell's placeholder <c>CollectorHealthRow</c> record (a single latest-run snapshot).
/// Every property is a pure computation over the aggregate — <see cref="HealthStatus"/> delegates to the
/// shared <see cref="CollectorHealthClassifier"/> in PerformanceMonitor.Common (#1573), so Lite, this
/// viewer, and the service band identically and cannot drift; it resolves the collector's cadence from the
/// shared <see cref="CollectorScheduleDefaults"/> so a healthy DAILY collector is no longer flagged
/// stale/failing on the frequent-collector thresholds. The <see cref="DateTime.UtcNow"/> arithmetic in
/// <see cref="HoursSinceLastSuccess"/> is correct against the store's naive-UTC timestamps because both
/// sides are UTC instants (tick subtraction ignores Kind), matching Lite.
/// </summary>
public class CollectorHealthRow
{
    public string CollectorName { get; set; } = "";
    public long TotalRuns { get; set; }
    public long SuccessCount { get; set; }
    public long ErrorCount { get; set; }
    public double AvgDurationMs { get; set; }
    public DateTime? LastSuccessTime { get; set; }
    public DateTime? LastRunTime { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorTime { get; set; }
    public long PermissionDeniedCount { get; set; }
    /// <summary>1s lock-timeout yields (#1805) — deliberate, benign, counted apart from errors.</summary>
    public long YieldCount { get; set; }

    public double FailureRatePercent => TotalRuns > 0 ? (double)ErrorCount / TotalRuns * 100 : 0;
    public double HoursSinceLastSuccess => LastSuccessTime.HasValue
        ? (DateTime.UtcNow - LastSuccessTime.Value).TotalHours
        : 999;

    /// <summary>The collector's default cadence from the shared <see cref="CollectorScheduleDefaults"/>
    /// (0 for an on-load or unknown collector — both fall to the floor thresholds). The banding uses the
    /// shipped default, not any per-install override: the viewer has no cheap per-collector effective
    /// frequency at the row level, and using the same default across all three surfaces keeps them in parity.</summary>
    private int FrequencyMinutes =>
        CollectorScheduleDefaults.All.TryGetValue(CollectorName, out var schedule) ? schedule.FrequencyMinutes : 0;

    public string HealthStatus => CollectorHealthClassifier.Classify(
        TotalRuns, SuccessCount, ErrorCount, PermissionDeniedCount,
        HoursSinceLastSuccess, FrequencyMinutes, CollectorHealthClassifier.IsOnLoadCollector(CollectorName));

    public string AvgDurationFormatted => AvgDurationMs < 1000
        ? $"{AvgDurationMs:F0} ms"
        : $"{AvgDurationMs / 1000:F1} s";

    public string LastSuccessFormatted => LastSuccessTime.HasValue
        ? ViewerTimeHelper.ForDisplay(LastSuccessTime.Value).ToString("g")
        : "Never";

    public string LastRunFormatted => LastRunTime.HasValue
        ? ViewerTimeHelper.ForDisplay(LastRunTime.Value).ToString("g")
        : "Never";

    public string LastErrorFormatted => LastErrorTime.HasValue
        ? ViewerTimeHelper.ForDisplay(LastErrorTime.Value).ToString("g")
        : "";
}
