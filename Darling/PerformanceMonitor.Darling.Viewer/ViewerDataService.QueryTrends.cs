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

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// One point on a Performance-Trends chart — the viewer copy of Lite's <c>QueryTrendPoint</c>
/// (LocalDataService.QueryStats.cs). <see cref="Value"/> is the per-second rate the chart plots
/// (elapsed ms/sec for the duration trends, executions/sec for the execution-count trend);
/// <see cref="ExecutionCount"/> carries the executions/sec rate the duration trends also compute
/// (unused by the execution-count trend). CollectionTime is naive UTC — the chart converts it
/// through <see cref="ViewerTimeHelper.ForDisplay"/>.
/// </summary>
public sealed class QueryTrendPoint
{
    public DateTime CollectionTime { get; set; }
    public double Value { get; set; }
    public long ExecutionCount { get; set; }
}

public sealed partial class ViewerDataService
{
    /* The four Performance-Trends reads — Lite's Get{Query,Procedure,ExecutionCount}DurationTrendAsync
       (LocalDataService.QueryStats.cs:1029-1168) + GetQueryStoreDurationTrendAsync
       (LocalDataService.QueryStore.cs:582) ported to Postgres. The SQL is byte-identical to Lite's
       apart from the table name (Lite reads the v_* views; the viewer reads the base tables, matching
       W1f-1's Top-Queries read) — every operator Lite uses (date_trunc('second', …), the
       LAG() OVER (ORDER BY collection_time) per-interval rate, extract(epoch FROM interval),
       CAST(… AS double precision), the positional $1/$2/$3 placeholders) is native Postgres, so no
       dialect rewrite is needed here (unlike the heatmap's time_bucket/ARG_MAX). The per-snapshot
       rate = summed delta over the seconds since the previous snapshot; the first row's LAG is NULL,
       so interval_seconds is NULL and the CASE yields 0 (Lite's behaviour). Summed bigint deltas come
       back as Postgres numeric, so the reads go through Convert tolerantly.
       $1 server_id, $2 window start, $3 window end (naive UTC). */

    /// <summary>Query-stats duration trend: elapsed ms/sec + executions/sec per collection snapshot.</summary>
    public const string QueryDurationTrendSql = """
        WITH raw AS
        (
            SELECT
                collection_time,
                SUM(delta_elapsed_time) / 1000.0 AS total_elapsed_ms,
                SUM(delta_execution_count) AS total_executions,
                extract(epoch FROM (date_trunc('second', collection_time) - date_trunc('second', LAG(collection_time) OVER (ORDER BY collection_time)))) AS interval_seconds
            FROM query_stats
            WHERE server_id = $1
            AND   collection_time >= $2
            AND   collection_time <= $3
            AND   ($4::text[] IS NULL OR database_name = ANY($4))
            GROUP BY collection_time
        )
        SELECT
            collection_time,
            CASE WHEN interval_seconds > 0 THEN total_elapsed_ms / interval_seconds ELSE 0 END AS elapsed_ms_per_second,
            CASE WHEN interval_seconds > 0 THEN CAST(total_executions AS DOUBLE PRECISION) / interval_seconds ELSE 0 END AS executions_per_second
        FROM raw
        ORDER BY collection_time
        """;

    /// <summary>Procedure-stats duration trend: elapsed ms/sec + executions/sec per collection snapshot.</summary>
    public const string ProcedureDurationTrendSql = """
        WITH raw AS
        (
            SELECT
                collection_time,
                SUM(delta_elapsed_time) / 1000.0 AS total_elapsed_ms,
                SUM(delta_execution_count) AS total_executions,
                extract(epoch FROM (date_trunc('second', collection_time) - date_trunc('second', LAG(collection_time) OVER (ORDER BY collection_time)))) AS interval_seconds
            FROM procedure_stats
            WHERE server_id = $1
            AND   collection_time >= $2
            AND   collection_time <= $3
            AND   ($4::text[] IS NULL OR database_name = ANY($4))
            GROUP BY collection_time
        )
        SELECT
            collection_time,
            CASE WHEN interval_seconds > 0 THEN total_elapsed_ms / interval_seconds ELSE 0 END AS elapsed_ms_per_second,
            CASE WHEN interval_seconds > 0 THEN CAST(total_executions AS DOUBLE PRECISION) / interval_seconds ELSE 0 END AS executions_per_second
        FROM raw
        ORDER BY collection_time
        """;

    /// <summary>
    /// Query Store duration trend: execution_count·avg_duration_us → ms/sec + executions/sec.
    ///
    /// <para>KNOWN OVERSTATEMENT, deliberately still here (#1841 tier 2), and the one Query Store
    /// aggregate the per-interval dedup was NOT applied to. The three trends above sum per-cycle DELTA
    /// columns; Query Store has none, so its cumulative per-interval snapshots — re-fetched every cycle
    /// while the interval stays open — make an interval that reached 40 executions charge 10, then 25,
    /// then 40 to three successive points, overstating the area under the curve.</para>
    ///
    /// <para>Deduping to the latest snapshot per interval fixes the magnitude but destroys the series:
    /// it keeps ONE row per interval, at the collection where that interval closed, and Query Store's
    /// default INTERVAL_LENGTH_MINUTES is 60 against a 5-minute cadence — so every query's twelve
    /// snapshots collapse onto one collection_time (Query Store interval boundaries are globally
    /// aligned, so they collapse together) and a 1-hour window renders a SINGLE point, valued 0 because
    /// the LAG has no predecessor. Placing the work when it actually ran needs first_execution_time,
    /// which is the monitored server's LOCAL wall clock while this axis is UTC — trading a magnitude
    /// bug for a timezone bug. Tier 2 owns both halves. Mirrors Lite's GetQueryStoreDurationTrendAsync.</para>
    /// </summary>
    public const string QueryStoreDurationTrendSql = """
        WITH raw AS
        (
            SELECT
                collection_time,
                SUM(execution_count * avg_duration_us / 1000.0) AS total_duration_ms,
                SUM(execution_count) AS total_executions,
                extract(epoch FROM (date_trunc('second', collection_time) - date_trunc('second', LAG(collection_time) OVER (ORDER BY collection_time)))) AS interval_seconds
            FROM query_store_stats
            WHERE server_id = $1
            AND   collection_time >= $2
            AND   collection_time <= $3
            AND   ($4::text[] IS NULL OR database_name = ANY($4))
            GROUP BY collection_time
        )
        SELECT
            collection_time,
            CASE WHEN interval_seconds > 0 THEN total_duration_ms / interval_seconds ELSE 0 END AS duration_ms_per_second,
            CASE WHEN interval_seconds > 0 THEN CAST(total_executions AS DOUBLE PRECISION) / interval_seconds ELSE 0 END AS executions_per_second
        FROM raw
        ORDER BY collection_time
        """;

    /// <summary>Execution-count trend: executions/sec per collection snapshot from query_stats.</summary>
    public const string ExecutionCountTrendSql = """
        WITH raw AS
        (
            SELECT
                collection_time,
                SUM(delta_execution_count) AS total_executions,
                extract(epoch FROM (date_trunc('second', collection_time) - date_trunc('second', LAG(collection_time) OVER (ORDER BY collection_time)))) AS interval_seconds
            FROM query_stats
            WHERE server_id = $1
            AND   collection_time >= $2
            AND   collection_time <= $3
            AND   ($4::text[] IS NULL OR database_name = ANY($4))
            GROUP BY collection_time
        )
        SELECT
            collection_time,
            CASE WHEN interval_seconds > 0 THEN CAST(total_executions AS DOUBLE PRECISION) / interval_seconds ELSE 0 END AS executions_per_second
        FROM raw
        ORDER BY collection_time
        """;

    /// <summary>Query-stats duration trend over [<paramref name="startUtc"/>, <paramref name="endUtc"/>].</summary>
    public Task<List<QueryTrendPoint>> GetQueryDurationTrendAsync(
        int serverId, DateTime startUtc, DateTime endUtc, IReadOnlyList<string>? databaseNames = null, CancellationToken cancellationToken = default)
        => ReadDurationTrendAsync(QueryDurationTrendSql, serverId, startUtc, endUtc, databaseNames, cancellationToken);

    /// <summary>Procedure-stats duration trend over the window.</summary>
    public Task<List<QueryTrendPoint>> GetProcedureDurationTrendAsync(
        int serverId, DateTime startUtc, DateTime endUtc, IReadOnlyList<string>? databaseNames = null, CancellationToken cancellationToken = default)
        => ReadDurationTrendAsync(ProcedureDurationTrendSql, serverId, startUtc, endUtc, databaseNames, cancellationToken);

    /// <summary>Query Store duration trend over the window.</summary>
    public Task<List<QueryTrendPoint>> GetQueryStoreDurationTrendAsync(
        int serverId, DateTime startUtc, DateTime endUtc, IReadOnlyList<string>? databaseNames = null, CancellationToken cancellationToken = default)
        => ReadDurationTrendAsync(QueryStoreDurationTrendSql, serverId, startUtc, endUtc, databaseNames, cancellationToken);

    /// <summary>
    /// Shared reader for the three duration trends (same column shape: collection_time,
    /// value/sec, executions/sec). Value = column 1; ExecutionCount = column 2 truncated to long
    /// (Lite's <c>(long)ToDouble(…)</c>).
    /// </summary>
    private async Task<List<QueryTrendPoint>> ReadDurationTrendAsync(
        string sql, int serverId, DateTime startUtc, DateTime endUtc, IReadOnlyList<string>? databaseNames, CancellationToken cancellationToken)
    {
        var items = new List<QueryTrendPoint>();

        await using var command = _dataSource.CreateCommand(sql);
        AddServerWindowParameters(command, serverId, startUtc, endUtc);
        command.Parameters.Add(DatabaseFilterParameter(databaseNames));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new QueryTrendPoint
            {
                CollectionTime = reader.GetDateTime(0),
                Value = reader.IsDBNull(1) ? 0 : Convert.ToDouble(reader.GetValue(1)),
                ExecutionCount = reader.IsDBNull(2) ? 0 : (long)Convert.ToDouble(reader.GetValue(2)),
            });
        }

        return items;
    }

    /// <summary>Execution-count trend over the window (single value column; no ExecutionCount).</summary>
    public async Task<List<QueryTrendPoint>> GetExecutionCountTrendAsync(
        int serverId, DateTime startUtc, DateTime endUtc, IReadOnlyList<string>? databaseNames = null, CancellationToken cancellationToken = default)
    {
        var items = new List<QueryTrendPoint>();

        await using var command = _dataSource.CreateCommand(ExecutionCountTrendSql);
        AddServerWindowParameters(command, serverId, startUtc, endUtc);
        command.Parameters.Add(DatabaseFilterParameter(databaseNames));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new QueryTrendPoint
            {
                CollectionTime = reader.GetDateTime(0),
                Value = reader.IsDBNull(1) ? 0 : Convert.ToDouble(reader.GetValue(1)),
            });
        }

        return items;
    }
}
