/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Database;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// Lite's delta calculator: the shared <see cref="CollectorDeltaCalculator"/> core (baseline /
/// counter-reset / gap-policy semantics live there, identical across SKUs) plus the DuckDB
/// seeding that lets the first collection after an app restart produce accurate deltas instead
/// of returning 0 for everything.
/// </summary>
public class DeltaCalculator : CollectorDeltaCalculator
{
    /* The four seed queries, mirrored verbatim in Darling's DarlingDeltaCalculator — deliberately
       written in the shared dialect (the (server_id, collection_time) row-value latest-row form, and
       the reused $1 positional placeholder, both run on either engine as-is). Held as constants so
       LiteDeltaSeederTests can pin the same shape DarlingDeltaSeederTests pins on the other side.

       $1 is CollectorDeltaCalculator.SeedCutoff(), bound on BOTH the outer read and the inner MAX():
       either one left unbounded reads the whole table. #1772 was the Postgres half of that on a
       276 GB store, where the unbounded form timed out and restart continuity silently degraded to
       first-cycle-zero deltas; Lite carries the bound too because the seed is one shared design and a
       long-lived local store grows the same shape of scan. */

    public const string WaitStatsSeedSql = @"
SELECT server_id, wait_type, waiting_tasks_count, wait_time_ms, signal_wait_time_ms, collection_time
FROM wait_stats
WHERE collection_time >= $1
AND   (server_id, collection_time) IN (
    SELECT server_id, MAX(collection_time) FROM wait_stats WHERE collection_time >= $1 GROUP BY server_id
)";

    public const string FileIoStatsSeedSql = @"
SELECT server_id, database_name, file_name,
       num_of_reads, num_of_writes, read_bytes, write_bytes,
       io_stall_read_ms, io_stall_write_ms,
       io_stall_queued_read_ms, io_stall_queued_write_ms,
       collection_time
FROM file_io_stats
WHERE collection_time >= $1
AND   (server_id, collection_time) IN (
    SELECT server_id, MAX(collection_time) FROM file_io_stats WHERE collection_time >= $1 GROUP BY server_id
)";

    public const string PerfmonStatsSeedSql = @"
SELECT server_id, object_name, counter_name, instance_name, cntr_value, collection_time
FROM perfmon_stats
WHERE collection_time >= $1
AND   (server_id, collection_time) IN (
    SELECT server_id, MAX(collection_time) FROM perfmon_stats WHERE collection_time >= $1 GROUP BY server_id
)";

    /* Also SELECTs collection_time, which the other three always did. Without it the memory-grant
       baselines seeded with a null timestamp, which disarms the gap policy for exactly the two
       counters where a stale baseline shows as a fabricated spike (grant timeouts and forced grants
       are monotonic). Inside the bounded window the row is fresh anyway; carrying the timestamp is
       what makes that a guarantee instead of an assumption. */
    public const string MemoryGrantStatsSeedSql = @"
SELECT server_id, pool_id, resource_semaphore_id, timeout_error_count, forced_grant_count, collection_time
FROM memory_grant_stats
WHERE collection_time >= $1
AND   (server_id, collection_time) IN (
    SELECT server_id, MAX(collection_time) FROM memory_grant_stats WHERE collection_time >= $1 GROUP BY server_id
)";

    private readonly ILogger? _logger;

    public DeltaCalculator(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Seeds the delta cache from DuckDB so that the first collection after restart
    /// can produce accurate deltas instead of returning 0 for everything.
    /// </summary>
    public async Task SeedFromDatabaseAsync(DuckDbInitializer duckDb)
    {
        try
        {
            using var connection = duckDb.CreateConnection();
            await connection.OpenAsync();

            /* One cutoff for all four reads, so they describe the same instant. */
            var cutoff = SeedCutoff();

            await SeedWaitStatsAsync(connection, cutoff);
            await SeedFileIoStatsAsync(connection, cutoff);
            await SeedPerfmonStatsAsync(connection, cutoff);
            await SeedMemoryGrantStatsAsync(connection, cutoff);

            _logger?.LogInformation(
                "Delta calculator seeded from database (baselines from the last {Minutes} minutes)",
                (int)SeedLookback.TotalMinutes);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to seed delta calculator from database, first collection will return 0 deltas");
        }
    }

    private async Task SeedWaitStatsAsync(DuckDBConnection connection, DateTime cutoff)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = WaitStatsSeedSql;
        cmd.Parameters.Add(new DuckDBParameter { Value = cutoff });
        using var reader = await cmd.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            var serverId = reader.GetInt32(0);
            var waitType = reader.GetString(1);
            var ts = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);
            Seed(serverId, "wait_stats_tasks", waitType, reader.GetInt64(2), ts);
            Seed(serverId, "wait_stats_time", waitType, reader.GetInt64(3), ts);
            Seed(serverId, "wait_stats_signal", waitType, reader.GetInt64(4), ts);
            count++;
        }
        if (count > 0) _logger?.LogDebug("Seeded {Count} wait_stats baseline rows", count);
    }

    private async Task SeedFileIoStatsAsync(DuckDBConnection connection, DateTime cutoff)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = FileIoStatsSeedSql;
        cmd.Parameters.Add(new DuckDBParameter { Value = cutoff });
        using var reader = await cmd.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            var serverId = reader.GetInt32(0);
            var dbName = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var fileName = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var deltaKey = $"{dbName}|{fileName}";
            var ts = reader.IsDBNull(11) ? (DateTime?)null : reader.GetDateTime(11);
            Seed(serverId, "file_io_reads", deltaKey, reader.IsDBNull(3) ? 0 : reader.GetInt64(3), ts);
            Seed(serverId, "file_io_writes", deltaKey, reader.IsDBNull(4) ? 0 : reader.GetInt64(4), ts);
            Seed(serverId, "file_io_read_bytes", deltaKey, reader.IsDBNull(5) ? 0 : reader.GetInt64(5), ts);
            Seed(serverId, "file_io_write_bytes", deltaKey, reader.IsDBNull(6) ? 0 : reader.GetInt64(6), ts);
            Seed(serverId, "file_io_stall_read", deltaKey, reader.IsDBNull(7) ? 0 : reader.GetInt64(7), ts);
            Seed(serverId, "file_io_stall_write", deltaKey, reader.IsDBNull(8) ? 0 : reader.GetInt64(8), ts);
            Seed(serverId, "file_io_stall_queued_read", deltaKey, reader.IsDBNull(9) ? 0 : reader.GetInt64(9), ts);
            Seed(serverId, "file_io_stall_queued_write", deltaKey, reader.IsDBNull(10) ? 0 : reader.GetInt64(10), ts);
            count++;
        }
        if (count > 0) _logger?.LogDebug("Seeded {Count} file_io_stats baseline rows", count);
    }

    private async Task SeedPerfmonStatsAsync(DuckDBConnection connection, DateTime cutoff)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = PerfmonStatsSeedSql;
        cmd.Parameters.Add(new DuckDBParameter { Value = cutoff });
        using var reader = await cmd.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            var serverId = reader.GetInt32(0);
            var objectName = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var counter = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var instance = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var ts = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);
            Seed(serverId, "perfmon", $"{objectName}|{counter}|{instance}", reader.GetInt64(4), ts);
            count++;
        }
        if (count > 0) _logger?.LogDebug("Seeded {Count} perfmon_stats baseline rows", count);
    }

    private async Task SeedMemoryGrantStatsAsync(DuckDBConnection connection, DateTime cutoff)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = MemoryGrantStatsSeedSql;
            cmd.Parameters.Add(new DuckDBParameter { Value = cutoff });
            using var reader = await cmd.ExecuteReaderAsync();
            var count = 0;
            while (await reader.ReadAsync())
            {
                var serverId = reader.GetInt32(0);
                var poolId = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                var semaphoreId = reader.IsDBNull(2) ? (short)0 : reader.GetInt16(2);
                var deltaKey = $"{poolId}_{semaphoreId}";
                var ts = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);
                Seed(serverId, "memory_grants_timeouts", deltaKey, reader.IsDBNull(3) ? 0 : reader.GetInt64(3), ts);
                Seed(serverId, "memory_grants_forced", deltaKey, reader.IsDBNull(4) ? 0 : reader.GetInt64(4), ts);
                count++;
            }
            if (count > 0) _logger?.LogDebug("Seeded {Count} memory_grant_stats baseline rows", count);
        }
        catch
        {
            /* Table may not exist on first run after schema migration */
        }
    }
}
