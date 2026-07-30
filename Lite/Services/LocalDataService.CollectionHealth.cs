/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Common;

namespace PerformanceMonitorLite.Services;

public partial class LocalDataService
{
    /// <summary>
    /// #1591: how many DISTINCT collectors were permission-denied in the last 7 days — the badge count for the
    /// Collection Health tab header.
    ///
    /// <para>Deliberately its own narrow COUNT rather than reusing <c>GetCollectionHealthAsync</c>: that one is
    /// per-collector and only runs when its tab is selected, which is exactly why a permission problem stayed
    /// invisible until someone thought to look. This runs on every refresh alongside the alert-count badge, so a
    /// denied collector is discoverable from any tab. Counts collectors, not rows, so one collector failing every
    /// cycle for a week reads as "1" rather than a meaningless four-figure number.</para>
    /// </summary>
    public async Task<int> GetPermissionDeniedCollectorCountAsync(int serverId)
    {
        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COUNT(DISTINCT collector_name)
FROM v_collection_log
WHERE server_id = $1
AND   collection_time >= $2
AND   status = 'PERMISSIONS'";

        command.Parameters.Add(new DuckDBParameter { Value = serverId });
        command.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow.AddDays(-7) });

        var scalar = await command.ExecuteScalarAsync();
        return scalar is null or DBNull ? 0 : Convert.ToInt32(scalar);
    }

    /// <summary>
    /// Gets collection health summary for all collectors on a server.
    /// </summary>
    public async Task<List<CollectorHealthRow>> GetCollectionHealthAsync(int serverId)
    {
        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
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
    SUM(CASE WHEN status = 'YIELDED' THEN 1 ELSE 0 END) AS yield_count,
    -- #1837: the note a SUCCEEDING run can leave behind (an enumeration that yielded 0 items, items
    -- whose enumeration probe failed). Gated on SUCCESS specifically, rather than on every non-failure status:
    -- the runners attach a note only to the SUCCESS write, and the looser complement would drag
    -- SESSION_MISSING and CANCELLED messages into a column whose whole claim is that it is NOT an
    -- error. MAX ignores NULLs, so a collector whose runs left no note reads blank. Display text only:
    -- no band, no count, no threshold reads it, and a legitimately empty target (no user databases, no
    -- AGs) stays HEALTHY exactly as before.
    MAX(CASE WHEN status = 'SUCCESS' THEN error_message END) AS last_note,
    -- How many of the window's runs carried one. note_count = total_runs is the persistently-empty
    -- signal the operator is actually looking for: EVERY run this week came back with nothing.
    COUNT(CASE WHEN status = 'SUCCESS' THEN error_message END) AS note_count
FROM v_collection_log
WHERE server_id = $1
AND   collection_time >= $2
GROUP BY collector_name
ORDER BY collector_name";

        command.Parameters.Add(new DuckDBParameter { Value = serverId });
        command.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow.AddDays(-7) });

        var items = new List<CollectorHealthRow>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new CollectorHealthRow
            {
                CollectorName = reader.GetString(0),
                TotalRuns = reader.IsDBNull(1) ? 0 : ToInt64(reader.GetValue(1)),
                SuccessCount = reader.IsDBNull(2) ? 0 : ToInt64(reader.GetValue(2)),
                ErrorCount = reader.IsDBNull(3) ? 0 : ToInt64(reader.GetValue(3)),
                AvgDurationMs = reader.IsDBNull(4) ? 0 : ToDouble(reader.GetValue(4)),
                LastSuccessTime = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                LastRunTime = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
                LastErrorTime = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                PermissionDeniedCount = reader.IsDBNull(9) ? 0 : ToInt64(reader.GetValue(9)),
                YieldCount = reader.IsDBNull(10) ? 0 : ToInt64(reader.GetValue(10)),
                LastNote = reader.IsDBNull(11) ? null : reader.GetString(11),
                NoteCount = reader.IsDBNull(12) ? 0 : ToInt64(reader.GetValue(12))
            });
        }

        return items;
    }

    /// <summary>
    /// Gets recent collection log entries for a server, most recent first, bounded to the tab's
    /// settable window. A preset ends "now" (<paramref name="hoursBack"/> from now); a custom range
    /// (<paramref name="fromDate"/>/<paramref name="toDate"/>, both already server-time) bounds
    /// <c>collection_time</c> on BOTH sides EXACTLY via <see cref="GetTimeRange"/> — mirroring how
    /// <see cref="GetWaitStatsAsync"/> windows its read. The old single now-relative lower bound ignored
    /// the custom To, rounding a custom range to a hours-back-from-now span.
    /// </summary>
    public async Task<List<CollectionLogRow>> GetRecentCollectionLogAsync(int serverId, int hoursBack = 4, DateTime? fromDate = null, DateTime? toDate = null, int maxRows = 500)
    {
        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();

        var (startTime, endTime) = GetTimeRange(hoursBack, fromDate, toDate);

        command.CommandText = @"
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
LIMIT $4";

        command.Parameters.Add(new DuckDBParameter { Value = serverId });
        command.Parameters.Add(new DuckDBParameter { Value = startTime });
        command.Parameters.Add(new DuckDBParameter { Value = endTime });
        command.Parameters.Add(new DuckDBParameter { Value = maxRows });

        var items = new List<CollectionLogRow>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new CollectionLogRow
            {
                CollectorName = reader.GetString(0),
                CollectionTime = reader.GetDateTime(1),
                DurationMs = reader.IsDBNull(2) ? null : (int?)Convert.ToInt32(reader.GetValue(2)),
                SqlDurationMs = reader.IsDBNull(3) ? null : (int?)Convert.ToInt32(reader.GetValue(3)),
                DuckDbDurationMs = reader.IsDBNull(4) ? null : (int?)Convert.ToInt32(reader.GetValue(4)),
                RowsCollected = reader.IsDBNull(5) ? null : (int?)Convert.ToInt32(reader.GetValue(5)),
                Status = reader.GetString(6),
                ErrorMessage = reader.IsDBNull(7) ? null : reader.GetString(7),
                ServerName = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }

        return items;
    }

    /// <summary>
    /// Gets collection log entries for a specific collector on a server.
    /// </summary>
    public async Task<List<CollectionLogRow>> GetCollectionLogByCollectorAsync(int serverId, string collectorName, int hoursBack = 168)
    {
        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
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
ORDER BY collection_time DESC";

        command.Parameters.Add(new DuckDBParameter { Value = serverId });
        command.Parameters.Add(new DuckDBParameter { Value = collectorName });
        command.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow.AddHours(-hoursBack) });

        var items = new List<CollectionLogRow>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new CollectionLogRow
            {
                CollectorName = reader.GetString(0),
                CollectionTime = reader.GetDateTime(1),
                DurationMs = reader.IsDBNull(2) ? null : (int?)Convert.ToInt32(reader.GetValue(2)),
                SqlDurationMs = reader.IsDBNull(3) ? null : (int?)Convert.ToInt32(reader.GetValue(3)),
                DuckDbDurationMs = reader.IsDBNull(4) ? null : (int?)Convert.ToInt32(reader.GetValue(4)),
                RowsCollected = reader.IsDBNull(5) ? null : (int?)Convert.ToInt32(reader.GetValue(5)),
                Status = reader.GetString(6),
                ErrorMessage = reader.IsDBNull(7) ? null : reader.GetString(7),
                ServerName = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }

        return items;
    }
}

public class CollectionLogRow
{
    public string CollectorName { get; set; } = "";
    public string? ServerName { get; set; }
    public DateTime CollectionTime { get; set; }
    public int? DurationMs { get; set; }
    public int? SqlDurationMs { get; set; }
    public int? DuckDbDurationMs { get; set; }
    public int? RowsCollected { get; set; }
    public string Status { get; set; } = "";
    public string? ErrorMessage { get; set; }

    public string CollectionTimeFormatted => CollectionTime.ToLocalTime().ToString("g");

    public string DurationFormatted => DurationMs.HasValue
        ? (DurationMs.Value < 1000 ? $"{DurationMs.Value} ms" : $"{DurationMs.Value / 1000.0:F1} s")
        : "";

    public string SqlDurationFormatted => SqlDurationMs.HasValue ? $"{SqlDurationMs.Value} ms" : "";

    public string DuckDbDurationFormatted => DuckDbDurationMs.HasValue ? $"{DuckDbDurationMs.Value} ms" : "";
}

/// <summary>
/// One Collection Health grid row — a collector's 7-day roll-up with its health band.
/// <see cref="HealthStatus"/> delegates to the shared <see cref="CollectorHealthClassifier"/> in
/// PerformanceMonitor.Common (#1573), so Lite, the Darling viewer, and the service band identically and
/// cannot drift; it resolves the collector's cadence from the shared <see cref="CollectorScheduleDefaults"/>
/// so a healthy DAILY collector is no longer flagged stale/failing on the frequent-collector thresholds.
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

    /// <summary>
    /// The note a non-failing run left behind (#1837): an enumeration that yielded 0 items, items whose
    /// enumeration probe failed. Null for the ordinary run, which is why the column reads blank for a
    /// plainly healthy collector. Informational only — see <see cref="NoteFormatted"/>.
    /// </summary>
    public string? LastNote { get; set; }

    /// <summary>How many of <see cref="TotalRuns"/> carried a <see cref="LastNote"/>.</summary>
    public long NoteCount { get; set; }

    public double FailureRatePercent => TotalRuns > 0 ? (double)ErrorCount / TotalRuns * 100 : 0;
    public double HoursSinceLastSuccess => LastSuccessTime.HasValue
        ? (DateTime.UtcNow - LastSuccessTime.Value).TotalHours
        : 999;

    /// <summary>The collector's default cadence from the shared <see cref="CollectorScheduleDefaults"/>
    /// (0 for an on-load or unknown collector — both fall to the floor thresholds). The banding uses the
    /// shipped default, not the per-install ScheduleManager override, so all three surfaces stay in parity.</summary>
    private int FrequencyMinutes =>
        CollectorScheduleDefaults.All.TryGetValue(CollectorName, out var schedule) ? schedule.FrequencyMinutes : 0;

    public string HealthStatus => CollectorHealthClassifier.Classify(
        TotalRuns, SuccessCount, ErrorCount, PermissionDeniedCount,
        HoursSinceLastSuccess, FrequencyMinutes, CollectorHealthClassifier.IsOnLoadCollector(CollectorName));

    public string AvgDurationFormatted => AvgDurationMs < 1000
        ? $"{AvgDurationMs:F0} ms"
        : $"{AvgDurationMs / 1000:F1} s";

    public string LastSuccessFormatted => LastSuccessTime.HasValue
        ? LastSuccessTime.Value.ToLocalTime().ToString("g")
        : "Never";

    public string LastRunFormatted => LastRunTime.HasValue
        ? LastRunTime.Value.ToLocalTime().ToString("g")
        : "Never";

    public string LastErrorFormatted => LastErrorTime.HasValue
        ? LastErrorTime.Value.ToLocalTime().ToString("g")
        : "";

    /// <summary>
    /// The informational note plus its "all N runs" / "N of M runs" qualifier (#1837), or blank. Shared
    /// with the Darling Viewer through <see cref="CollectorHealthClassifier.FormatCollectionNote"/> so the
    /// two apps' health grids read identically. Never feeds <see cref="HealthStatus"/>.
    /// </summary>
    public string NoteFormatted => CollectorHealthClassifier.FormatCollectionNote(LastNote, NoteCount, TotalRuns);
}

