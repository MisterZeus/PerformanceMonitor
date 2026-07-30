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

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// Per-item execution-timeline reads backing the slicer overlay-on-select (#683): selecting a Top Queries /
/// Top Procedures / Query Store row overlays THAT item's activity curve on its sub-tab's slicer. Ports Lite's
/// <c>ServerTab.Grids.cs</c> overlay feed (GetQueryStatsHistoryAsync / GetProcedureStatsHistoryAsync /
/// GetQueryStoreHistoryAsync, filtered to one item) to Postgres. One deviation from Lite: Darling's
/// <c>delta_*</c> columns are already per-collection-cycle deltas (Lite's history rows carry cumulative
/// values it diffs row-over-row), so the per-interval magnitude is read directly — no C# differencing. Query
/// Store keeps its per-execution averages, scaled by <c>execution_count</c> to a per-interval total the way
/// the Query Store slicer aggregate does.
/// </summary>
public sealed partial class ViewerDataService
{
    /// <summary>One point on an item's execution timeline: the collection cycle's per-interval magnitudes
    /// in the same units the slicer's sort-driven metric overlay uses (ms for CPU/elapsed, raw counts for
    /// reads/writes).</summary>
    public sealed record ItemTimelinePoint(
        DateTime CollectionTime, double CpuMs, double ElapsedMs, double Reads, double Writes, double PhysicalReads);

    public const string QueryStatsItemTimelineSql = """
        SELECT
            collection_time,
            COALESCE(delta_worker_time, 0) / 1000.0 AS cpu_ms,
            COALESCE(delta_elapsed_time, 0) / 1000.0 AS elapsed_ms,
            COALESCE(delta_logical_reads, 0) AS reads,
            COALESCE(delta_logical_writes, 0) AS writes,
            COALESCE(delta_physical_reads, 0) AS physical_reads
        FROM query_stats
        WHERE server_id = $1
        AND   database_name = $2
        AND   query_hash = $3
        AND   collection_time >= $4
        AND   collection_time <= $5
        ORDER BY collection_time
        """;

    /// <summary>The selected Top-Queries row's per-interval execution timeline over the window.</summary>
    public async Task<List<ItemTimelinePoint>> GetQueryStatsItemTimelineAsync(
        int serverId, string databaseName, string queryHash, DateTime startUtc, DateTime endUtc,
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(QueryStatsItemTimelineSql);
        AddItemWindowParameters(command, serverId, databaseName, queryHash, startUtc, endUtc);
        return await ReadItemTimelineAsync(command, cancellationToken);
    }

    public const string ProcStatsItemTimelineSql = """
        SELECT
            collection_time,
            COALESCE(delta_worker_time, 0) / 1000.0 AS cpu_ms,
            COALESCE(delta_elapsed_time, 0) / 1000.0 AS elapsed_ms,
            COALESCE(delta_logical_reads, 0) AS reads,
            COALESCE(delta_logical_writes, 0) AS writes,
            COALESCE(delta_physical_reads, 0) AS physical_reads
        FROM procedure_stats
        WHERE server_id = $1
        AND   database_name = $2
        AND   schema_name = $3
        AND   object_name = $4
        AND   collection_time >= $5
        AND   collection_time <= $6
        ORDER BY collection_time
        """;

    /// <summary>The selected Top-Procedures row's per-interval execution timeline over the window.</summary>
    public async Task<List<ItemTimelinePoint>> GetProcStatsItemTimelineAsync(
        int serverId, string databaseName, string schemaName, string objectName, DateTime startUtc, DateTime endUtc,
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(ProcStatsItemTimelineSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = databaseName ?? "" });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = schemaName ?? "" });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = objectName ?? "" });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.SpecifyKind(startUtc, DateTimeKind.Unspecified) });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.SpecifyKind(endUtc, DateTimeKind.Unspecified) });
        return await ReadItemTimelineAsync(command, cancellationToken);
    }

    public const string QueryStoreItemTimelineSql = """
        WITH deduped AS (
            /* LOAD-BEARING (correctness, not just perf) — #1841. The rows are CUMULATIVE per-interval
               snapshots and the collector re-fetches the OPEN interval every cycle, so an un-deduped
               projection draws one interval as a rising staircase of avg_* x execution_count products
               that are restatements of the same work, not new work.

               Two reasons this read needs it even though it has no SUM. (1) This series is drawn OVER the
               Query Store slicer bars, which ARE deduped (QueryStoreSlicerSql) — leaving the overlay raw
               would make the overlay disagree with the bars it annotates. (2) The WHERE narrows to
               query_id + plan_id but NOT to an interval, so one collection cycle can return several
               intervals for the same plan; the reader appends those as separate points at the SAME
               x-coordinate. Deduping per interval collapses the restatements while keeping genuinely
               distinct intervals as their own points. */
            SELECT
                collection_time,
                execution_count,
                avg_cpu_time_us,
                avg_duration_us,
                avg_logical_io_reads,
                avg_logical_io_writes,
                avg_physical_io_reads,
                ROW_NUMBER() OVER
                (
                    PARTITION BY database_name, query_id, plan_id, runtime_stats_interval_id, first_execution_time, execution_type_desc, replica_role
                    ORDER BY collection_time DESC
                ) AS rn
            FROM query_store_stats
            WHERE server_id = $1
            AND   database_name = $2
            AND   query_id = $3
            AND   plan_id = $4
            AND   collection_time >= $5
            AND   collection_time <= $6
        )
        SELECT
            collection_time,
            COALESCE(CAST(avg_cpu_time_us AS double precision) * execution_count, 0) / 1000.0 AS cpu_ms,
            COALESCE(CAST(avg_duration_us AS double precision) * execution_count, 0) / 1000.0 AS elapsed_ms,
            COALESCE(CAST(avg_logical_io_reads AS double precision) * execution_count, 0) AS reads,
            COALESCE(CAST(avg_logical_io_writes AS double precision) * execution_count, 0) AS writes,
            COALESCE(CAST(avg_physical_io_reads AS double precision) * execution_count, 0) AS physical_reads
        FROM deduped
        WHERE rn = 1
        ORDER BY collection_time
        """;

    /// <summary>The selected Query Store row's per-interval execution timeline (avg × exec count) over the window.</summary>
    public async Task<List<ItemTimelinePoint>> GetQueryStoreItemTimelineAsync(
        int serverId, string databaseName, long queryId, long planId, DateTime startUtc, DateTime endUtc,
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(QueryStoreItemTimelineSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = databaseName ?? "" });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = queryId });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = planId });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.SpecifyKind(startUtc, DateTimeKind.Unspecified) });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.SpecifyKind(endUtc, DateTimeKind.Unspecified) });
        return await ReadItemTimelineAsync(command, cancellationToken);
    }

    /// <summary>$1 server_id, $2 database_name, $3 query_hash, $4 start, $5 end (window naive UTC).</summary>
    private static void AddItemWindowParameters(
        NpgsqlCommand command, int serverId, string databaseName, string queryHash, DateTime startUtc, DateTime endUtc)
    {
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = databaseName ?? "" });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = queryHash ?? "" });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.SpecifyKind(startUtc, DateTimeKind.Unspecified) });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.SpecifyKind(endUtc, DateTimeKind.Unspecified) });
    }

    private static async Task<List<ItemTimelinePoint>> ReadItemTimelineAsync(
        NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var points = new List<ItemTimelinePoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            points.Add(new ItemTimelinePoint(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? 0 : Convert.ToDouble(reader.GetValue(1)),
                reader.IsDBNull(2) ? 0 : Convert.ToDouble(reader.GetValue(2)),
                reader.IsDBNull(3) ? 0 : Convert.ToDouble(reader.GetValue(3)),
                reader.IsDBNull(4) ? 0 : Convert.ToDouble(reader.GetValue(4)),
                reader.IsDBNull(5) ? 0 : Convert.ToDouble(reader.GetValue(5))));
        }
        return points;
    }
}
