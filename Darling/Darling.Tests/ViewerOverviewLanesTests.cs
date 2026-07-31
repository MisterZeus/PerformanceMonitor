/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using PerformanceMonitor.Analysis.Baselines;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Analysis;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the two W1d Overview-lanes reads that don't already exist from earlier waves — the single-line
/// TOTAL wait trend and the memory trend — against the Darling store contract (no live Postgres). The
/// lanes' other four feeds (raw CPU from W1a; blocking/deadlock/file-IO from W1c) keep their own wave's
/// pins. This wave replaces wave-4's Overview trend reads (CpuTrendSql / WaitCategoryTrendSql /
/// RollUpWaitCategories, whose <c>ViewerTrendsTests</c> were removed): the lanes plot raw CPU (W1a's
/// read) and a single total-wait line rather than an averaged CPU trend + per-category wait roll-up, so
/// those reads and their unit tests no longer exist. The shared <c>ChartPalette.WaitCategory</c>
/// classifier the roll-up exercised is still covered by the plan-viewer wait list and its own tests.
/// </summary>
public sealed class ViewerOverviewLanesSqlTests
{
    [Fact]
    public void TotalWaitTrendSql_SumsAllTypesPerCollection_PerSecondFromLagInterval()
    {
        Assert.Contains("FROM v_wait_stats", ViewerDataService.TotalWaitTrendSql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", ViewerDataService.TotalWaitTrendSql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", ViewerDataService.TotalWaitTrendSql, StringComparison.Ordinal);
        Assert.Contains("collection_time <= $3", ViewerDataService.TotalWaitTrendSql, StringComparison.Ordinal);

        /* SUM across ALL wait types per collection (the single-line total), the LAG-derived collection
           interval, and the per-second division with the delta CAST to double for the typed reader. */
        Assert.Contains("SUM(delta_wait_time_ms)", ViewerDataService.TotalWaitTrendSql, StringComparison.Ordinal);
        Assert.Contains("LAG(collection_time)", ViewerDataService.TotalWaitTrendSql, StringComparison.Ordinal);
        Assert.Contains("CAST(total_delta_ms AS double precision) / interval_seconds", ViewerDataService.TotalWaitTrendSql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY collection_time", ViewerDataService.TotalWaitTrendSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY collection_time", ViewerDataService.TotalWaitTrendSql, StringComparison.Ordinal);
    }

    [Fact]
    public void TotalWaitTrendSql_DropsLitesIgnoredWaitExclusionClause()
    {
        /* Lite splices an "AND wait_type NOT IN (...)" per-user ignored-wait exclusion; the viewer has
           no per-user ignore config, so the ported read carries no NOT IN clause (matches W1b). */
        Assert.DoesNotContain("NOT IN", ViewerDataService.TotalWaitTrendSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MemoryTrendSql_CastsAllFourMbMetricsToDouble_OverTheWindow()
    {
        Assert.Contains("FROM v_memory_stats", ViewerDataService.MemoryTrendSql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", ViewerDataService.MemoryTrendSql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", ViewerDataService.MemoryTrendSql, StringComparison.Ordinal);
        Assert.Contains("collection_time <= $3", ViewerDataService.MemoryTrendSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY collection_time", ViewerDataService.MemoryTrendSql, StringComparison.Ordinal);

        /* Every MB column is numeric(18,2) in the store; without the cast the typed GetDouble reader
           throws (the same numeric→double reason the CPU/File-IO reads cast). */
        foreach (var column in new[] { "total_server_memory_mb", "target_server_memory_mb", "buffer_pool_mb", "plan_cache_mb" })
        {
            Assert.Contains($"CAST({column} AS double precision)", ViewerDataService.MemoryTrendSql, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(nameof(ViewerDataService.TotalWaitTrendSql))]
    [InlineData(nameof(ViewerDataService.MemoryTrendSql))]
    public void TrendSql_PgDialect_PositionalParams_NoBareNow_NoNLiterals(string which)
    {
        var sql = which == nameof(ViewerDataService.TotalWaitTrendSql)
            ? ViewerDataService.TotalWaitTrendSql
            : ViewerDataService.MemoryTrendSql;

        Assert.DoesNotContain("now(", sql.ToLowerInvariant());
        Assert.DoesNotContain("N'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
        Assert.Contains("$1", sql, StringComparison.Ordinal);
        Assert.Contains("$2", sql, StringComparison.Ordinal);
        Assert.Contains("$3", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TotalWaitTrendSql_ReadsColumnsThatExistInTheGeneratedWaitTable()
    {
        Assert.Equal("wait_stats", WaitStatsCollector.Instance.TargetTable);

        var ddl = PgSchemaGenerator.CreateTable(WaitStatsCollector.Instance);
        foreach (var column in new[] { "collection_time", "delta_wait_time_ms" })
        {
            Assert.Contains(column, ddl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MemoryTrendSql_ReadsColumnsThatExistInTheGeneratedMemoryTable()
    {
        Assert.Equal("memory_stats", MemoryStatsCollector.Instance.TargetTable);

        var ddl = PgSchemaGenerator.CreateTable(MemoryStatsCollector.Instance);
        foreach (var column in new[] { "collection_time", "total_server_memory_mb", "target_server_memory_mb", "buffer_pool_mb", "plan_cache_mb" })
        {
            Assert.Contains(column, ddl, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// Gated (DARLING_TEST_PG) live round-trips for the W1d Overview-lanes reads: the total-wait per-second
/// rate (SUM across all types divided by the LAG-derived collection interval, first collection reading 0
/// on a NULL interval), the four-MB memory read (numeric→double), and the per-lane baseline lookup
/// (graceful <see cref="BaselineBucket.Empty"/> on no history — the baseline COMPUTATION itself is
/// covered by the Analysis suite; this pins the viewer's delegation). Shares the serialized
/// "live-postgres" collection; uses negative sentinel server_ids and cleans up in finally.
/// </summary>
[Collection("live-postgres")]
public sealed class ViewerOverviewLanesLivePostgresTests
{
    private const int WaitServerId = -949494;
    private const string WaitServerName = "viewer-lanes-wait-e2e";

    private const int MemoryServerId = -959595;
    private const string MemoryServerName = "viewer-lanes-memory-e2e";

    private const int BaselineServerId = -969696;

    [Fact]
    public async Task TotalWaitTrend_SumsAcrossTypes_PerSecondFromLagInterval_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live total-wait-trend test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteWaitRowsAsync(connection, TestContext.Current.CancellationToken);

        await using var viewer = new ViewerDataService(connectionString!);

        var bodySucceeded = false;
        try
        {
            var t1 = TruncateToSeconds(DateTime.UtcNow.AddMinutes(-10));
            var t2 = t1.AddSeconds(60);

            /* Two collections, two wait types each. The first collection has no prior LAG (interval
               NULL → per-second rate 0); the second is 60 seconds later, so its total delta divides by
               60. Totals sum ACROSS types: t2 = (60 + 120) / 60 = 3.0 ms/sec. */
            await InsertWaitRowAsync(connection, 1, t1, "WAIT_A", 100);
            await InsertWaitRowAsync(connection, 1, t1, "WAIT_B", 200);
            await InsertWaitRowAsync(connection, 2, t2, "WAIT_A", 60);
            await InsertWaitRowAsync(connection, 2, t2, "WAIT_B", 120);

            var points = await viewer.GetTotalWaitTrendAsync(WaitServerId, t1.AddMinutes(-1), t2.AddMinutes(1));

            Assert.Equal(2, points.Count);

            /* Ordered by collection_time; first collection's NULL interval reads as 0. */
            Assert.Equal(t1.Ticks, points[0].CollectionTime.Ticks);
            Assert.Equal(0.0, points[0].WaitTimeMsPerSecond, precision: 3);

            Assert.Equal(t2.Ticks, points[1].CollectionTime.Ticks);
            Assert.Equal(3.0, points[1].WaitTimeMsPerSecond, precision: 3);

            bodySucceeded = true;
        }
        finally
        {
            await LiveStoreCleanup.RunAsync(connectionString!, bodySucceeded, async (cleanup, cleanupCt) =>
                await DeleteWaitRowsAsync(cleanup, cleanupCt));
        }
    }

    [Fact]
    public async Task MemoryTrend_ReadsFourMbMetricsAsDouble_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live memory-trend test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteMemoryRowsAsync(connection, TestContext.Current.CancellationToken);

        await using var viewer = new ViewerDataService(connectionString!);

        var bodySucceeded = false;
        try
        {
            var t1 = TruncateToSeconds(DateTime.UtcNow.AddMinutes(-10));
            var t2 = t1.AddMinutes(5);

            /* Fractional buffer-pool value proves numeric(18,2) → double preserves the cents. */
            await InsertMemoryRowAsync(connection, 1, t1, total: 1000.00m, target: 2000.00m, buffer: 800.50m, planCache: 150.00m);
            await InsertMemoryRowAsync(connection, 2, t2, total: 1100.00m, target: 2000.00m, buffer: 850.25m, planCache: 160.00m);

            var points = await viewer.GetMemoryTrendAsync(MemoryServerId, t1.AddMinutes(-1), t2.AddMinutes(1));

            Assert.Equal(2, points.Count);

            /* Ordered by collection_time; every metric reads as a double via the SQL CAST. */
            Assert.Equal(t1.Ticks, points[0].CollectionTime.Ticks);
            Assert.Equal(1000.0, points[0].TotalServerMemoryMb, precision: 2);
            Assert.Equal(2000.0, points[0].TargetServerMemoryMb, precision: 2);
            Assert.Equal(800.50, points[0].BufferPoolMb, precision: 2);
            Assert.Equal(150.0, points[0].PlanCacheMb, precision: 2);

            Assert.Equal(t2.Ticks, points[1].CollectionTime.Ticks);
            Assert.Equal(850.25, points[1].BufferPoolMb, precision: 2);

            bodySucceeded = true;
        }
        finally
        {
            await LiveStoreCleanup.RunAsync(connectionString!, bodySucceeded, async (cleanup, cleanupCt) =>
                await DeleteMemoryRowsAsync(cleanup, cleanupCt));
        }
    }

    [Fact]
    public async Task BaselineForLane_NoHistory_ReturnsEmptyBucket_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live baseline-lane test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);

        await using var viewer = new ViewerDataService(connectionString!);

        /* A sentinel server with no CPU history: the CPU baseline query runs against the real schema
           (v_cpu_utilization_stats), returns no rows, and the lane read hands back Empty (SampleCount 0)
           so the lane renderer's guard skips the band. Proves the viewer's PgBaselineProvider wiring. */
        var baseline = await viewer.GetBaselineForLaneAsync(BaselineServerId, MetricNames.Cpu, DateTime.UtcNow);

        Assert.Equal(0, baseline.SampleCount);
    }

    private static async Task InsertWaitRowAsync(
        NpgsqlConnection connection, long collectionId, DateTime collectionTimeUtc, string waitType, long deltaWaitTimeMs)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO wait_stats
    (collection_id, collection_time, server_id, server_name, wait_type,
     waiting_tasks_count, wait_time_ms, signal_wait_time_ms,
     delta_waiting_tasks, delta_wait_time_ms, delta_signal_wait_time_ms)
VALUES ($1, $2, $3, $4, $5, 0, 0, 0, 0, $6, 0)", connection);
        command.Parameters.AddWithValue(collectionId);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(WaitServerId);
        command.Parameters.AddWithValue(WaitServerName);
        command.Parameters.AddWithValue(waitType);
        command.Parameters.AddWithValue(deltaWaitTimeMs);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertMemoryRowAsync(
        NpgsqlConnection connection, long collectionId, DateTime collectionTimeUtc,
        decimal total, decimal target, decimal buffer, decimal planCache)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO memory_stats
    (collection_id, collection_time, server_id, server_name,
     target_server_memory_mb, total_server_memory_mb, buffer_pool_mb, plan_cache_mb)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8)", connection);
        command.Parameters.AddWithValue(collectionId);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(MemoryServerId);
        command.Parameters.AddWithValue(MemoryServerName);
        command.Parameters.Add(new NpgsqlParameter { Value = target, NpgsqlDbType = NpgsqlDbType.Numeric });
        command.Parameters.Add(new NpgsqlParameter { Value = total, NpgsqlDbType = NpgsqlDbType.Numeric });
        command.Parameters.Add(new NpgsqlParameter { Value = buffer, NpgsqlDbType = NpgsqlDbType.Numeric });
        command.Parameters.Add(new NpgsqlParameter { Value = planCache, NpgsqlDbType = NpgsqlDbType.Numeric });
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        DateTime.SpecifyKind(new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Unspecified);

    private static async Task DeleteWaitRowsAsync(NpgsqlConnection connection, System.Threading.CancellationToken ct)
    {
        using var cleanup = new NpgsqlCommand(
            $"DELETE FROM wait_stats WHERE server_id = {WaitServerId};", connection);
        await cleanup.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteMemoryRowsAsync(NpgsqlConnection connection, System.Threading.CancellationToken ct)
    {
        using var cleanup = new NpgsqlCommand(
            $"DELETE FROM memory_stats WHERE server_id = {MemoryServerId};", connection);
        await cleanup.ExecuteNonQueryAsync(ct);
    }
}
