/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Alerting;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// Darling's <see cref="IAlertReadAdapter"/> (Phase-5 slice B): the seven collected alert feeds
/// read from Postgres — each query is Lite's DuckDB read ported dialect-for-dialect against the
/// same table names and columns (the generated PG schema mirrors Lite's — see PgSchemaGenerator),
/// with three deliberate adjustments:
/// (1) no <c>v_</c> views exist here — the raw tables are queried directly;
/// (2) Lite's long-running-query read uses DuckDB's bare <c>NOW()</c>; the PG twin binds a
///     parameterized naive-UTC now instead (the poison-wait read's parameterization style),
///     because a bare <c>now()</c> is <c>timestamptz</c> and comparing it against the naive-UTC
///     <c>timestamp</c> columns would resolve in the server's time zone;
/// (3) DuckDB's <c>N'...'</c> literals become plain <c>'...'</c> (Postgres has no N-prefix).
/// All bound timestamps are naive-UTC Kind-Unspecified, matching the COPY writer's storage
/// discipline. The blocking read reproduces Lite's XE-preferred + DMV-fallback merge via the
/// shared <see cref="BlockedProcessReportMerge"/>.
/// <para>
/// <c>serverKey</c> is the deterministic storage-name hash rendered as a string (the engine's
/// identity across the Phase-5 seams), parsed back to the <c>server_id</c> int here.
/// </para>
/// </summary>
public sealed class DarlingAlertReadAdapter : IAlertReadAdapter
{
    private readonly NpgsqlDataSource _postgres;
    private readonly Func<int, int>? _runningJobsCadenceMinutes;

    /// <param name="runningJobsCadenceMinutes">
    /// Resolves a server's EFFECTIVE running_jobs collection cadence (minutes) for the #1812
    /// snapshot-freshness bound — the worker supplies its own schedule resolution (the same
    /// <c>StoreConfigProvider.ResolveSchedule</c> the sweep runs on). Null (test call sites) or a
    /// non-positive answer falls back to the shared <see cref="CollectorScheduleDefaults"/> cadence.
    /// </param>
    public DarlingAlertReadAdapter(NpgsqlDataSource postgres, Func<int, int>? runningJobsCadenceMinutes = null)
    {
        _postgres = postgres ?? throw new ArgumentNullException(nameof(postgres));
        _runningJobsCadenceMinutes = runningJobsCadenceMinutes;
    }

    /* ---------------- blocking (XE-preferred + DMV fallback) ---------------- */

    /// <summary>
    /// The XE blocked-process-report read — Lite's query with the column list trimmed to the
    /// shared alert row's fields (same WHERE / ORDER BY event_time DESC / LIMIT 200 semantics).
    /// $1 server_id, $2 window start, $3 window end (naive UTC).
    /// </summary>
    public const string BlockedProcessReportsSql = @"
SELECT
    event_time,
    database_name,
    blocked_spid,
    blocking_spid,
    wait_time_ms,
    lock_mode,
    blocked_sql_text,
    blocking_sql_text,
    blocked_process_report_xml,
    contentious_object
FROM blocked_process_reports
WHERE server_id = $1
AND   collection_time >= $2
AND   collection_time <= $3
ORDER BY event_time DESC
LIMIT 200";

    /// <summary>
    /// The always-on DMV blocking-snapshot fallback read — Lite's query trimmed the same way
    /// (dmv_blocking_snapshots has no report XML column). Same parameters as
    /// <see cref="BlockedProcessReportsSql"/>.
    /// </summary>
    public const string DmvBlockingSnapshotsSql = @"
SELECT
    event_time,
    database_name,
    blocked_spid,
    blocking_spid,
    wait_time_ms,
    lock_mode,
    blocked_sql_text,
    blocking_sql_text,
    contentious_object
FROM dmv_blocking_snapshots
WHERE server_id = $1
AND   collection_time >= $2
AND   collection_time <= $3
ORDER BY event_time DESC
LIMIT 200";

    public async Task<List<BlockedProcessAlertRow>> GetRecentBlockedProcessReportsAsync(
        string serverKey, int hoursBack, CancellationToken cancellationToken = default)
    {
        var serverId = ParseServerKey(serverKey);
        var (startTime, endTime) = Window(hoursBack);

        var items = new List<BlockedProcessAlertRow>();
        var dmvItems = new List<BlockedProcessAlertRow>();

        await using (var connection = await _postgres.OpenConnectionAsync(cancellationToken))
        {
            using (var command = new NpgsqlCommand(BlockedProcessReportsSql, connection))
            {
                command.Parameters.AddWithValue(serverId);
                command.Parameters.AddWithValue(startTime);
                command.Parameters.AddWithValue(endTime);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    items.Add(new BlockedProcessAlertRow
                    {
                        EventTime = reader.IsDBNull(0) ? null : reader.GetDateTime(0),
                        DatabaseName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        BlockedSpid = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                        BlockingSpid = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                        WaitTimeMs = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                        LockMode = reader.IsDBNull(5) ? "" : reader.GetString(5),
                        BlockedSqlText = reader.IsDBNull(6) ? "" : reader.GetString(6),
                        BlockingSqlText = reader.IsDBNull(7) ? "" : reader.GetString(7),
                        BlockedProcessReportXml = reader.IsDBNull(8) ? "" : reader.GetString(8),
                        ContentiousObject = reader.IsDBNull(9) ? "" : reader.GetString(9)
                    });
                }
            }

            using (var command = new NpgsqlCommand(DmvBlockingSnapshotsSql, connection))
            {
                command.Parameters.AddWithValue(serverId);
                command.Parameters.AddWithValue(startTime);
                command.Parameters.AddWithValue(endTime);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    dmvItems.Add(new BlockedProcessAlertRow
                    {
                        EventTime = reader.IsDBNull(0) ? null : reader.GetDateTime(0),
                        DatabaseName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        BlockedSpid = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                        BlockingSpid = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                        WaitTimeMs = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                        LockMode = reader.IsDBNull(5) ? "" : reader.GetString(5),
                        BlockedSqlText = reader.IsDBNull(6) ? "" : reader.GetString(6),
                        BlockingSqlText = reader.IsDBNull(7) ? "" : reader.GetString(7),
                        ContentiousObject = reader.IsDBNull(8) ? "" : reader.GetString(8),
                        Source = BlockedProcessAlertRow.DmvSnapshotSource
                    });
                }
            }
        }

        /* Lite's XE-preferred fallback semantics, verbatim via the shared merge: keep all BPR
           rows; append a DMV row only where no BPR covers the same SPID pair in the same minute;
           re-cap to the 200 newest. */
        BlockedProcessReportMerge.AppendDmvFallbackRows(items, dmvItems);

        return items;
    }

    /* ---------------- deadlocks ---------------- */

    /// <summary>Lite's deadlock read (column list trimmed to the shared alert row's fields).</summary>
    public const string DeadlocksSql = @"
SELECT
    victim_process_id,
    victim_sql_text,
    deadlock_graph_xml
FROM deadlocks
WHERE server_id = $1
AND   collection_time >= $2
AND   collection_time <= $3
ORDER BY deadlock_time DESC
LIMIT 50";

    public async Task<List<DeadlockAlertRow>> GetRecentDeadlocksAsync(
        string serverKey, int hoursBack, CancellationToken cancellationToken = default)
    {
        var serverId = ParseServerKey(serverKey);
        var (startTime, endTime) = Window(hoursBack);

        var items = new List<DeadlockAlertRow>();
        await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
        using var command = new NpgsqlCommand(DeadlocksSql, connection);
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(startTime);
        command.Parameters.AddWithValue(endTime);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new DeadlockAlertRow
            {
                VictimProcessId = reader.IsDBNull(0) ? "" : reader.GetString(0),
                VictimSqlText = reader.IsDBNull(1) ? "" : reader.GetString(1),
                DeadlockGraphXml = reader.IsDBNull(2) ? "" : reader.GetString(2)
            });
        }

        return items;
    }

    /* ---------------- poison waits ---------------- */

    /// <summary>
    /// Lite's poison-wait read verbatim (wait_stats table instead of the v_ view). $2 is the
    /// parameterized naive-UTC "now minus 10 minutes" — the parameterization style the
    /// long-running-query twin copies.
    /// </summary>
    public const string PoisonWaitsSql = @"
SELECT
    wait_type,
    delta_wait_time_ms AS delta_ms,
    delta_waiting_tasks AS delta_tasks,
    CASE WHEN delta_waiting_tasks > 0
    THEN CAST(delta_wait_time_ms AS DOUBLE PRECISION) / delta_waiting_tasks
    ELSE 0 END AS avg_ms_per_wait
FROM wait_stats
WHERE server_id = $1
AND wait_type IN ('THREADPOOL', 'RESOURCE_SEMAPHORE', 'RESOURCE_SEMAPHORE_QUERY_COMPILE')
AND delta_waiting_tasks > 0
AND collection_time >= $2
ORDER BY collection_time DESC
LIMIT 3";

    public async Task<List<PoisonWaitDelta>> GetPoisonWaitDeltasAsync(
        string serverKey, double thresholdMs, CancellationToken cancellationToken = default)
    {
        var serverId = ParseServerKey(serverKey);

        var items = new List<PoisonWaitDelta>();
        await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
        using var command = new NpgsqlCommand(PoisonWaitsSql, connection);
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(NaiveUtcNow().AddMinutes(-10));

        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new PoisonWaitDelta
                {
                    WaitType = reader.GetString(0),
                    DeltaMs = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                    DeltaTasks = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    AvgMsPerWait = reader.IsDBNull(3) ? 0 : reader.GetDouble(3)
                });
            }
        }

        /* Fetch-then-filter, exactly like Lite's loop: the 3-row window is selected before
           thresholding (see the IAlertReadAdapter contract). */
        return items.FindAll(w => w.AvgMsPerWait >= thresholdMs);
    }

    /* ---------------- long-running queries ---------------- */

    /// <summary>
    /// Lite's long-running-query read with the two PG dialect adjustments: DuckDB's bare
    /// <c>NOW() - INTERVAL '10 MINUTES'</c> becomes the parameterized naive-UTC $4 (a bare
    /// <c>now()</c> is timestamptz — wrong basis against naive-UTC timestamp columns), and the
    /// N'' literals in the opt-out filters (<c>{0}</c> placeholder) lose their N prefix.
    /// $1 server_id, $2 elapsed-ms threshold, $3 max results, $4 staleness floor.
    /// </summary>
    public const string LongRunningQueriesSqlTemplate = @"
SELECT
    r.session_id,
    r.database_name,
    SUBSTRING(r.query_text, 1, 300) AS query_text,
    r.total_elapsed_time_ms / 1000 AS elapsed_seconds,
    r.cpu_time_ms,
    r.reads,
    r.writes,
    r.wait_type,
    r.blocking_session_id,
    r.query_hash,
    r.program_name
FROM query_snapshots AS r
WHERE r.server_id = $1
    AND r.collection_time = (SELECT MAX(vqs.collection_time) FROM query_snapshots AS vqs WHERE vqs.server_id = $1)
    AND r.collection_time >= $4
    AND r.session_id > 50
    {0}
    AND r.total_elapsed_time_ms >= $2
ORDER BY r.total_elapsed_time_ms DESC
LIMIT $3";

    /// <summary>The five opt-out noise filters — Lite's clauses with the N'' prefixes dropped.</summary>
    /* sp_server_diagnostics (the AG/FCI health-check session) usually sits in SP_SERVER_DIAGNOSTICS_SLEEP, but it
       also does Extended Events work, so it can be captured in a different wait (e.g. PREEMPTIVE_XE_GETTARGETSTATE)
       where the wait-type match alone misses it and the Long-Running Query alert fires anyway. The query-text
       match (case-insensitive, NULL-safe) catches it regardless of the wait it happens to be in at capture time. */
    public const string SpServerDiagnosticsFilter =
        "AND r.wait_type NOT LIKE '%SP_SERVER_DIAGNOSTICS%'\n    AND (r.query_text IS NULL OR r.query_text NOT ILIKE '%sp_server_diagnostics%')";
    public const string WaitForFilter = "AND r.wait_type NOT IN ('WAITFOR', 'BROKER_RECEIVE_WAITFOR')";
    public const string BackupsFilter = "AND r.wait_type NOT IN ('BACKUPTHREAD', 'BACKUPIO')";
    public const string MiscWaitsFilter = "AND r.wait_type NOT IN ('XE_LIVE_TARGET_TVF')";
    public const string CdcFilter = "AND COALESCE(r.is_cdc_capture, FALSE) = FALSE";

    public async Task<List<LongRunningQueryInfo>> GetLongRunningQueriesAsync(
        string serverKey,
        int thresholdMinutes,
        int maxResults,
        bool excludeSpServerDiagnostics,
        bool excludeWaitFor,
        bool excludeBackups,
        bool excludeMiscWaits,
        bool excludeCdc,
        IReadOnlyList<string> excludedDatabases,
        CancellationToken cancellationToken = default)
    {
        var serverId = ParseServerKey(serverKey);
        var thresholdMs = (long)thresholdMinutes * 60 * 1000;
        maxResults = Math.Clamp(maxResults, 1, 1000);

        var filters = string.Join("\n    ", new[]
        {
            excludeSpServerDiagnostics ? SpServerDiagnosticsFilter : "",
            excludeWaitFor ? WaitForFilter : "",
            excludeBackups ? BackupsFilter : "",
            excludeMiscWaits ? MiscWaitsFilter : "",
            excludeCdc ? CdcFilter : ""
        }.Where(f => f.Length > 0));

        var sql = LongRunningQueriesSqlTemplate.Replace("{0}", filters);

        var items = new List<LongRunningQueryInfo>();
        await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(thresholdMs);
        command.Parameters.AddWithValue(maxResults);
        command.Parameters.AddWithValue(NaiveUtcNow().AddMinutes(-10));

        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new LongRunningQueryInfo
                {
                    SessionId = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    DatabaseName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    QueryText = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    ElapsedSeconds = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    CpuTimeMs = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    Reads = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                    Writes = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                    WaitType = reader.IsDBNull(7) ? null : reader.GetString(7),
                    BlockingSessionId = reader.IsDBNull(8) ? null : (int?)reader.GetInt32(8),
                    QueryHash = reader.IsDBNull(9) ? null : reader.GetString(9),
                    ProgramName = reader.IsDBNull(10) ? "" : reader.GetString(10)
                });
            }
        }

        if (excludedDatabases is { Count: > 0 })
        {
            items = items
                .Where(q => string.IsNullOrEmpty(q.DatabaseName) ||
                    !excludedDatabases.Any(e =>
                        string.Equals(e, q.DatabaseName, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        return items;
    }

    /* ---------------- volume free space ---------------- */

    /// <summary>Lite's per-volume free-space read verbatim (database_size_stats table).</summary>
    public const string VolumeFreeSpaceSql = @"
SELECT
    volume_mount_point,
    MAX(volume_total_mb) AS volume_total_mb,
    MIN(volume_free_mb) AS volume_free_mb
FROM database_size_stats
WHERE server_id = $1
AND   collection_time = (
    SELECT MAX(collection_time)
    FROM database_size_stats
    WHERE server_id = $1
)
AND   volume_mount_point IS NOT NULL
AND   volume_total_mb > 0
GROUP BY volume_mount_point
ORDER BY MIN(volume_free_mb) / MAX(volume_total_mb)";

    public async Task<List<VolumeFreeSpaceInfo>> GetVolumeFreeSpaceAsync(
        string serverKey, CancellationToken cancellationToken = default)
    {
        var serverId = ParseServerKey(serverKey);

        var items = new List<VolumeFreeSpaceInfo>();
        await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
        using var command = new NpgsqlCommand(VolumeFreeSpaceSql, connection);
        command.Parameters.AddWithValue(serverId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new VolumeFreeSpaceInfo
            {
                MountPoint = reader.IsDBNull(0) ? "" : reader.GetString(0),
                TotalMb = reader.IsDBNull(1) ? 0 : ToDouble(reader.GetValue(1)),
                FreeMb = reader.IsDBNull(2) ? 0 : ToDouble(reader.GetValue(2))
            });
        }

        return items;
    }

    /* ---------------- tempdb ---------------- */

    /// <summary>Lite's latest-tempdb-snapshot read verbatim (tempdb_stats table).</summary>
    public const string TempDbSpaceSql = @"
SELECT
    total_reserved_mb,
    unallocated_mb,
    user_object_reserved_mb,
    internal_object_reserved_mb,
    version_store_reserved_mb,
    top_session_tempdb_mb,
    top_session_id
FROM tempdb_stats
WHERE server_id = $1
ORDER BY collection_time DESC
LIMIT 1";

    public async Task<TempDbSpaceInfo?> GetTempDbSpaceAsync(
        string serverKey, CancellationToken cancellationToken = default)
    {
        var serverId = ParseServerKey(serverKey);

        await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
        using var command = new NpgsqlCommand(TempDbSpaceSql, connection);
        command.Parameters.AddWithValue(serverId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new TempDbSpaceInfo
            {
                TotalReservedMb = reader.IsDBNull(0) ? 0 : ToDouble(reader.GetValue(0)),
                UnallocatedMb = reader.IsDBNull(1) ? 0 : ToDouble(reader.GetValue(1)),
                UserObjectReservedMb = reader.IsDBNull(2) ? 0 : ToDouble(reader.GetValue(2)),
                InternalObjectReservedMb = reader.IsDBNull(3) ? 0 : ToDouble(reader.GetValue(3)),
                VersionStoreReservedMb = reader.IsDBNull(4) ? 0 : ToDouble(reader.GetValue(4)),
                TopConsumerMb = reader.IsDBNull(5) ? 0 : ToDouble(reader.GetValue(5)),
                TopConsumerSessionId = reader.IsDBNull(6) ? 0 : reader.GetInt32(6)
            };
        }

        return null;
    }

    /* ---------------- anomalous jobs ---------------- */

    /// <summary>
    /// Lite's anomalous-jobs read verbatim (running_jobs table). $2 is the threshold percent
    /// (multiplier x 100, as numeric — percent_of_average is numeric(10,1)).
    /// </summary>
    public const string AnomalousJobsSql = @"
SELECT
    job_name,
    job_id,
    current_duration_seconds,
    avg_duration_seconds,
    p95_duration_seconds,
    percent_of_average,
    start_time
FROM running_jobs
WHERE server_id = $1
AND collection_time = (SELECT MAX(collection_time) FROM running_jobs WHERE server_id = $1)
AND avg_duration_seconds >= 60
AND percent_of_average >= $2
ORDER BY percent_of_average DESC
LIMIT 5";

    public async Task<AnomalousJobsResult> GetAnomalousJobsAsync(
        string serverKey, int multiplier, CancellationToken cancellationToken = default)
    {
        var serverId = ParseServerKey(serverKey);
        var thresholdPercent = (decimal)(multiplier * 100);

        var items = new List<AnomalousJobInfo>();
        await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);

        /* #1812: the latest snapshot is only evidence when fresh — a stopped collector, missed cycles,
           or lost msdb access leaves a stale "latest" that would otherwise read as NOW, and the engine's
           per-run cooldown key expires each pass, so a stale snapshot re-fires the same historical run
           every cooldown, forever. Same rule as Lite's adapter; parity is the point. */
        using (var snapshotProbe = new NpgsqlCommand(
            "SELECT MAX(collection_time) FROM running_jobs WHERE server_id = $1", connection))
        {
            snapshotProbe.Parameters.AddWithValue(serverId);
            var snapshot = await snapshotProbe.ExecuteScalarAsync(cancellationToken);
            var cadence = ResolveRunningJobsCadence(serverId);
            if (snapshot is not DateTime snapshotTime
                || DateTime.UtcNow - snapshotTime > AnomalousJobsResult.MaxSnapshotAge(cadence))
            {
                return AnomalousJobsResult.Stale;
            }
        }

        using var command = new NpgsqlCommand(AnomalousJobsSql, connection);
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(thresholdPercent);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AnomalousJobInfo
            {
                JobName = reader.GetString(0),
                JobId = reader.IsDBNull(1) ? "" : reader.GetString(1),
                CurrentDurationSeconds = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                AvgDurationSeconds = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                P95DurationSeconds = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                PercentOfAverage = reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                StartTime = reader.IsDBNull(6) ? DateTime.MinValue : reader.GetDateTime(6)
            });
        }

        return new AnomalousJobsResult(SnapshotIsFresh: true, items);
    }

    private int ResolveRunningJobsCadence(int serverId)
    {
        var resolved = _runningJobsCadenceMinutes?.Invoke(serverId) ?? 0;
        if (resolved > 0)
        {
            return resolved;
        }

        return PerformanceMonitor.Collectors.CollectorScheduleDefaults.All.TryGetValue("running_jobs", out var schedule)
            ? schedule.FrequencyMinutes
            : 2;
    }

    /* ---------------- helpers ---------------- */

    /// <summary>Naive-UTC now, Kind-Unspecified — the product's PG timestamp discipline.</summary>
    private static DateTime NaiveUtcNow() =>
        DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    /// <summary>The (start, end) collection_time window Lite's GetTimeRange produces for hoursBack.</summary>
    private static (DateTime StartTime, DateTime EndTime) Window(int hoursBack)
    {
        var endTime = NaiveUtcNow();
        return (endTime.AddHours(-hoursBack), endTime);
    }

    private static int ParseServerKey(string serverKey) =>
        int.Parse(serverKey, CultureInfo.InvariantCulture);

    /// <summary>numeric(p,s) columns read back as decimal — coerce like Lite's ToDouble.</summary>
    private static double ToDouble(object value) =>
        Convert.ToDouble(value, CultureInfo.InvariantCulture);
}
