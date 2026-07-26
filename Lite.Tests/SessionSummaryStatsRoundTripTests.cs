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
/// Real-DuckDB round-trip pin for the #1262 session_summary_stats collector. The definition test pins
/// PayloadColumns/WritePayload against a recording writer, but nothing else proves the Lite DuckDB
/// table's physical column layout stays aligned with WritePayload — the appender writes BY POSITION,
/// so a table whose column count/order drifts from (4 prefix + WritePayload) fails at EndRow(). This
/// exercises the exact append path RemoteCollectorService.RunCollectorDefinitionAsync uses
/// (CreateAppender -> prefix -> WritePayload -> EndRow) against a freshly initialized schema, then
/// reads representative columns back, including the nullable top_* columns left NULL.
/// </summary>
public sealed class SessionSummaryStatsRoundTripTests : IClassFixture<SharedDuckDbFixture>
{
    private readonly DuckDbInitializer _duckDb;

    public SessionSummaryStatsRoundTripTests(SharedDuckDbFixture fixture)
    {
        fixture.ResetData();
        _duckDb = fixture.DuckDb;
    }

    [Fact]
    public async Task SessionSummaryStats_AppendRoundTrips_Counts_AndNullableTopColumns()
    {
        /* top_application_name / top_application_connections / top_host_name / top_host_connections
           left NULL (a quiet server with no qualifying user session) to prove the nullable appends
           round-trip too. */
        var row = new SessionSummaryStatsCollector.Row(42, 3, 30, 5, 1, 8, 2, 4, null, null, null, null);

        using var connection = await AppendOneRowAsync(SessionSummaryStatsCollector.Instance, row);

        Assert.Equal(42, (int)await ScalarAsync(connection, "session_summary_stats", "total_sessions"));
        Assert.Equal(8, (int)await ScalarAsync(connection, "session_summary_stats", "idle_sessions_over_30min"));
        Assert.Equal(2, (int)await ScalarAsync(connection, "session_summary_stats", "sessions_waiting_for_memory"));
        Assert.Equal(4, (int)await ScalarAsync(connection, "session_summary_stats", "databases_with_connections"));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "session_summary_stats", "top_application_name"));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "session_summary_stats", "top_host_connections"));
    }

    [Fact]
    public async Task SessionSummaryStats_AppendRoundTrips_PopulatedTopColumns()
    {
        var row = new SessionSummaryStatsCollector.Row(100, 5, 90, 3, 2, 12, 0, 6, "MyApp", 20, "WEB01", 15);

        using var connection = await AppendOneRowAsync(SessionSummaryStatsCollector.Instance, row);

        Assert.Equal("MyApp", (string)await ScalarAsync(connection, "session_summary_stats", "top_application_name"));
        Assert.Equal(20, (int)await ScalarAsync(connection, "session_summary_stats", "top_application_connections"));
        Assert.Equal("WEB01", (string)await ScalarAsync(connection, "session_summary_stats", "top_host_name"));
        Assert.Equal(15, (int)await ScalarAsync(connection, "session_summary_stats", "top_host_connections"));
    }

    /// <summary>
    /// Appends one row through the exact runner path (prefix columns + WritePayload + EndRow) against a
    /// freshly initialized DuckDB and returns the open connection. EndRow throwing here would mean the
    /// Schema.cs table column count/order drifted from WritePayload.
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
