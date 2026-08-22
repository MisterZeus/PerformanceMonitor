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

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// Service-side reads for the blocking-incident and deadlock per-minute trend MCP tools
/// (<see cref="DarlingMcpBlockingTools"/> get_blocking_trend / get_deadlock_trend). The SQL is reproduced
/// verbatim from the viewer's Blocking-Trends charts (<c>ViewerDataService.BlockingTrends.cs</c>), which are
/// Lite's <c>GetBlockingTrendAsync</c> / <c>GetDeadlockTrendAsync</c> ported to Postgres. Both are STORED
/// reads (no live monitored-server hit) sharing a <c>(bucket timestamp, COUNT(*))</c> shape, so one reader
/// maps both; COUNT(*) is <c>bigint</c> in Postgres, read via GetInt64 and narrowed to the point's int.
/// Public-const SQL so Darling.Tests pin the dialect (the XE-preferred + DMV-fallback union, the deadlock
/// bucket-on-deadlock_time) without a live Postgres.
/// </summary>
internal static class DarlingBlockingTrendReader
{
    /// <summary>One incident-count-per-minute bucket (mirror of the viewer's <c>BlockingTrendPoint</c>).</summary>
    public sealed record BlockingTrendReadPoint(DateTime Time, int Count);

    /// <summary>
    /// Blocking-incident count per minute — the viewer's <c>BlockingTrendSql</c>. XE blocked-process reports
    /// (<c>v_blocked_process_reports</c>) are the primary source, bucketed on <c>event_time</c>; the always-on
    /// DMV snapshot (<c>v_dmv_blocking_snapshots</c>) is appended only when the XE source has no rows in the
    /// window (<c>WHERE NOT EXISTS</c>), so a server with both sources never double-counts. $1 server_id,
    /// $2 window start, $3 window end (naive UTC).
    /// </summary>
    public const string BlockingTrendSql = """
        WITH bpr AS (
            SELECT DATE_TRUNC('minute', event_time) AS bucket, COUNT(*) AS incident_count
            FROM v_blocked_process_reports
            WHERE server_id = $1 AND event_time >= $2 AND event_time <= $3
            GROUP BY DATE_TRUNC('minute', event_time)
        ),
        dmv AS (
            SELECT DATE_TRUNC('minute', event_time) AS bucket, COUNT(*) AS incident_count
            FROM v_dmv_blocking_snapshots
            WHERE server_id = $1 AND event_time >= $2 AND event_time <= $3
            GROUP BY DATE_TRUNC('minute', event_time)
        )
        SELECT bucket, incident_count FROM bpr
        UNION ALL
        SELECT bucket, incident_count FROM dmv WHERE NOT EXISTS (SELECT 1 FROM bpr)
        ORDER BY bucket
        """;

    /// <summary>
    /// Deadlock count per minute — the viewer's <c>DeadlockTrendSql</c>. Buckets on the deadlock's own
    /// <c>deadlock_time</c> while windowing on the collection prefix. Reads <c>v_deadlocks</c>. $1 server_id,
    /// $2 window start, $3 window end (naive UTC).
    /// </summary>
    public const string DeadlockTrendSql = """
        SELECT
            bucket,
            deadlock_count
        FROM (
            SELECT
                DATE_TRUNC('minute', deadlock_time) AS bucket,
                COUNT(*) AS deadlock_count
            FROM v_deadlocks
            WHERE server_id = $1
            AND   collection_time >= $2
            AND   collection_time <= $3
            GROUP BY DATE_TRUNC('minute', deadlock_time)
        ) sub
        ORDER BY bucket
        """;

    /// <summary>Blocking-incident-per-minute buckets for one server over the window.</summary>
    public static Task<List<BlockingTrendReadPoint>> GetBlockingTrendAsync(
        NpgsqlDataSource postgres, int serverId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
        => ReadCountTrendAsync(postgres, BlockingTrendSql, serverId, startUtc, endUtc, cancellationToken);

    /// <summary>Deadlock-per-minute buckets for one server over the window.</summary>
    public static Task<List<BlockingTrendReadPoint>> GetDeadlockTrendAsync(
        NpgsqlDataSource postgres, int serverId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
        => ReadCountTrendAsync(postgres, DeadlockTrendSql, serverId, startUtc, endUtc, cancellationToken);

    /// <summary>The blocking and deadlock trends share a (bucket timestamp, COUNT(*)) shape, so one reader
    /// maps both. COUNT(*) is bigint in Postgres, read via GetInt64 and narrowed to the point's int.</summary>
    private static async Task<List<BlockingTrendReadPoint>> ReadCountTrendAsync(
        NpgsqlDataSource postgres, string sql, int serverId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken)
    {
        var items = new List<BlockingTrendReadPoint>();
        await using var command = postgres.CreateCommand(sql);
        DarlingMcpReadParameters.AddWindow(command, serverId, startUtc, endUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new BlockingTrendReadPoint(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? 0 : (int)reader.GetInt64(1)));
        }

        return items;
    }

    /* ───────────────── the denominator an empty trend needs ───────────────── */

    /// <summary>
    /// One collector's SUCCESSFUL run count inside a window, with the first and last of those runs.
    /// <para>Both trends read EDGE tables — rows exist only where an event happened — so "no rows" is a
    /// capture that found nothing and a capture that never ran, wearing the same face. Neither table can
    /// tell them apart; <c>collection_log</c> can, because a collector that ran and stored nothing still
    /// records a SUCCESS with zero rows. Same reasoning as
    /// <c>DarlingPgBlockingReader.PgBlockingCaptureCounts</c>, applied to the SQL Server side.</para>
    /// </summary>
    public sealed record CaptureCount(string CollectorName, long Runs, DateTime? FirstRunAt, DateTime? LastRunAt);

    /// <summary>
    /// Successful runs per blocking collector inside the window. BOTH capture paths, deliberately: the trend
    /// above unions <c>v_blocked_process_reports</c> with <c>v_dmv_blocking_snapshots</c>, so counting one of
    /// them would report "never captured" for a server capturing perfectly well through the other — the wrong
    /// branch in exactly the case this exists to get right. Only SUCCESS counts as having looked; a PERMISSIONS
    /// or ERROR row is a collector that did not see the window either. $1 server_id, $2/$3 window (naive UTC).
    /// </summary>
    public const string BlockingCaptureCountsSql = """
        SELECT
            collector_name,
            COUNT(*),
            MIN(collection_time),
            MAX(collection_time)
        FROM v_collection_log
        WHERE server_id = $1
        AND   collection_time >= $2
        AND   collection_time <= $3
        AND   status = 'SUCCESS'
        AND   collector_name IN ('blocked_process_report', 'dmv_blocking_snapshot')
        GROUP BY collector_name
        ORDER BY collector_name
        """;

    /// <summary>
    /// Successful runs of the deadlock collector inside the window. One capture path here, not two — deadlocks
    /// come only from the <c>deadlocks</c> collector's system_health read, and there is no DMV fallback to
    /// count. $1 server_id, $2/$3 window (naive UTC).
    /// </summary>
    public const string DeadlockCaptureCountsSql = """
        SELECT
            collector_name,
            COUNT(*),
            MIN(collection_time),
            MAX(collection_time)
        FROM v_collection_log
        WHERE server_id = $1
        AND   collection_time >= $2
        AND   collection_time <= $3
        AND   status = 'SUCCESS'
        AND   collector_name = 'deadlocks'
        GROUP BY collector_name
        ORDER BY collector_name
        """;

    /// <summary>
    /// Whether either blocking collector has EVER run successfully for this server, ignoring any window.
    /// <para>Asked ONLY when the window count came back zero, and only to pick which sentence is true: a
    /// server whose collectors have run before has a GAP in this window (widen it, or go look at collection
    /// health), while one that has never run them is not collecting blocking at all. Both are "not an
    /// all-clear" and they want different next moves. LIMIT 1, so it stops at the first row.</para>
    /// </summary>
    public const string HasAnyBlockingCaptureSql = """
        SELECT 1
        FROM v_collection_log
        WHERE server_id = $1
        AND   status = 'SUCCESS'
        AND   collector_name IN ('blocked_process_report', 'dmv_blocking_snapshot')
        LIMIT 1
        """;

    /// <summary>Whether the deadlock collector has EVER run successfully for this server. See
    /// <see cref="HasAnyBlockingCaptureSql"/> for why the question is asked at all.</summary>
    public const string HasAnyDeadlockCaptureSql = """
        SELECT 1
        FROM v_collection_log
        WHERE server_id = $1
        AND   status = 'SUCCESS'
        AND   collector_name = 'deadlocks'
        LIMIT 1
        """;

    /// <summary>Runs <see cref="BlockingCaptureCountsSql"/>.</summary>
    public static Task<List<CaptureCount>> GetBlockingCaptureCountsAsync(
        NpgsqlDataSource postgres, int serverId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
        => ReadCaptureCountsAsync(postgres, BlockingCaptureCountsSql, serverId, startUtc, endUtc, cancellationToken);

    /// <summary>Runs <see cref="DeadlockCaptureCountsSql"/>.</summary>
    public static Task<List<CaptureCount>> GetDeadlockCaptureCountsAsync(
        NpgsqlDataSource postgres, int serverId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
        => ReadCaptureCountsAsync(postgres, DeadlockCaptureCountsSql, serverId, startUtc, endUtc, cancellationToken);

    /// <summary>Runs <see cref="HasAnyBlockingCaptureSql"/>.</summary>
    public static Task<bool> HasAnyBlockingCaptureAsync(
        NpgsqlDataSource postgres, int serverId, CancellationToken cancellationToken = default)
        => HasAnyCaptureAsync(postgres, HasAnyBlockingCaptureSql, serverId, cancellationToken);

    /// <summary>Runs <see cref="HasAnyDeadlockCaptureSql"/>.</summary>
    public static Task<bool> HasAnyDeadlockCaptureAsync(
        NpgsqlDataSource postgres, int serverId, CancellationToken cancellationToken = default)
        => HasAnyCaptureAsync(postgres, HasAnyDeadlockCaptureSql, serverId, cancellationToken);

    /// <summary>Both capture-count reads share a (collector_name, COUNT(*), MIN, MAX) shape, so one mapper
    /// serves them. COUNT(*) is bigint in Postgres.</summary>
    private static async Task<List<CaptureCount>> ReadCaptureCountsAsync(
        NpgsqlDataSource postgres, string sql, int serverId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken)
    {
        var items = new List<CaptureCount>();
        await using var command = postgres.CreateCommand(sql);
        DarlingMcpReadParameters.AddWindow(command, serverId, startUtc, endUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new CaptureCount(
                reader.GetString(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3)));
        }

        return items;
    }

    /// <summary>Both existence probes share one shape: a scalar that is null when no row qualifies.</summary>
    private static async Task<bool> HasAnyCaptureAsync(
        NpgsqlDataSource postgres, string sql, int serverId, CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(sql);
        DarlingMcpReadParameters.AddInt(command, serverId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
