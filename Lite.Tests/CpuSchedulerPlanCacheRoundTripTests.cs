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
using Lite.Tests.Helpers;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Services;
using PerformanceMonitorLite.Tests;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Real-DuckDB round-trip pins for the #1262 cpu_scheduler_stats + plan_cache_stats collectors. The
/// collector-definition tests pin PayloadColumns/WritePayload against a recording writer, but nothing
/// else proves the Lite DuckDB table's physical column layout stays aligned with WritePayload — the
/// appender writes BY POSITION, so a table whose column count/order drifts from (4 prefix +
/// WritePayload) fails at EndRow(). This exercises the exact append path
/// RemoteCollectorService.RunCollectorDefinitionAsync uses (CreateAppender → prefix → WritePayload →
/// EndRow) against a freshly initialized schema, then reads representative columns back. It also
/// specifically proves the DuckDB appender round-trips the DECIMAL(38,2) averages/percent these two
/// tables introduce (new to Lite's schema, HUGEINT-backed) plus the BOOLEAN pressure warnings and a
/// nullable column left NULL.
/// </summary>
public sealed class CpuSchedulerPlanCacheRoundTripTests : IClassFixture<SharedDuckDbFixture>
{
    private readonly DuckDbInitializer _duckDb;

    public CpuSchedulerPlanCacheRoundTripTests(SharedDuckDbFixture fixture)
    {
        fixture.ResetData();
        _duckDb = fixture.DuckDb;
    }

    [Fact]
    public async Task CpuSchedulerStats_AppendRoundTrips_Decimal38_Boolean_AndNullableColumns()
    {
        /* runnable_request_count / total_request_count / runnable_percent left NULL (empty-request
           snapshot) to prove the nullable appends round-trip too. */
        var row = new CpuSchedulerStatsCollector.Row(512, 8, 8, 3, 0, 40, 1.50m, 5, 0, 0, 0, null, null, null,
            true, false, false, false, 16777216, 8388608, "Available physical memory is high",
            false, 1, 1, 0, false);

        using var connection = await AppendOneRowAsync(CpuSchedulerStatsCollector.Instance, row);

        Assert.Equal(1.50m, (decimal)await ScalarAsync(connection, "cpu_scheduler_stats", "avg_runnable_tasks_count"));
        Assert.Equal(0L, (long)await ScalarAsync(connection, "cpu_scheduler_stats", "total_work_queue_count"));
        Assert.True((bool)await ScalarAsync(connection, "cpu_scheduler_stats", "worker_thread_exhaustion_warning"));
        Assert.Equal("Available physical memory is high", (string)await ScalarAsync(connection, "cpu_scheduler_stats", "system_memory_state_desc"));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "cpu_scheduler_stats", "runnable_percent"));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "cpu_scheduler_stats", "runnable_request_count"));
    }

    [Fact]
    public async Task PlanCacheStats_AppendRoundTrips_Decimal38_AndNullableTimestamp()
    {
        var row = new PlanCacheStatsCollector.Row("Compiled Plan", "Adhoc", 1000, 50, 800, 40, 200, 10, 42.75m, 51, null);

        using var connection = await AppendOneRowAsync(PlanCacheStatsCollector.Instance, row);

        Assert.Equal(42.75m, (decimal)await ScalarAsync(connection, "plan_cache_stats", "avg_use_count"));
        Assert.Equal("Compiled Plan", (string)await ScalarAsync(connection, "plan_cache_stats", "cacheobjtype"));
        Assert.Equal(1000, (int)await ScalarAsync(connection, "plan_cache_stats", "total_plans"));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "plan_cache_stats", "oldest_plan_create_time"));
    }

    /// <summary>
    /// Appends one row through the exact runner path (prefix columns + WritePayload + EndRow) against a
    /// freshly initialized DuckDB and returns the open connection. EndRow throwing here would mean the
    /// Schema.cs table column count/order drifted from WritePayload; an appender that rejected the
    /// DECIMAL(38,2) value would throw at AppendValue.
    /// </summary>
    private async Task<DuckDBConnection> AppendOneRowAsync<TRow>(ICollectorDefinition<TRow> definition, TRow row)
    {
        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        var connection = _duckDb.CreateConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        using (var appender = connection.CreateAppender(definition.TargetTable))
        {
            var appenderRow = appender.CreateRow();
            if (definition.IncludesCollectionId)
            {
                appenderRow.AppendValue(1L);
            }
            appenderRow.AppendValue(context.CollectionTime).AppendValue(42).AppendValue("s");

            var writer = new AppenderCollectorRowWriter { CurrentRow = appenderRow };
            definition.WritePayload(row, writer, context);
            appenderRow.EndRow();   /* throws if the appended column count != table column count */
        }

        return connection;
    }

    private static async Task<object> ScalarAsync(DuckDBConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM {table} LIMIT 1";
        return (await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
