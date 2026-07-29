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
/// Real-DuckDB round-trip pin for the #1262 system_health_events collector. The definition test pins
/// PayloadColumns/WritePayload against a recording writer, but nothing else proves the Lite DuckDB
/// table's physical column layout stays aligned with WritePayload — the appender writes BY POSITION,
/// so a table whose column count/order drifts from (4 prefix + WritePayload) fails at EndRow(). This
/// exercises the exact append path RemoteCollectorService.RunCollectorDefinitionAsync uses
/// (CreateAppender -> prefix -> WritePayload -> EndRow) against a freshly initialized schema, then
/// reads the raw event columns back, including a NULL event row.
/// </summary>
public sealed class SystemHealthEventsRoundTripTests : IClassFixture<SharedDuckDbFixture>
{
    private readonly DuckDbInitializer _duckDb;

    public SystemHealthEventsRoundTripTests(SharedDuckDbFixture fixture)
    {
        fixture.ResetData();
        _duckDb = fixture.DuckDb;
    }

    [Fact]
    public async Task SystemHealthEvents_AppendRoundTrips_RawXml()
    {
        var eventTime = new DateTime(2026, 7, 5, 11, 58, 0, DateTimeKind.Utc);
        const string rawXml = "<event name=\"memory_broker_ring_buffer_recorded\" package=\"sqlserver\"><data name=\"broker\"><value>1</value></data></event>";
        var row = new SystemHealthEventsCollector.Row
        {
            EventTime = eventTime,
            EventType = "memory_broker_ring_buffer_recorded",
            EventXml = rawXml,
        };

        using var connection = await AppendOneRowAsync(SystemHealthEventsCollector.Instance, row);

        Assert.Equal(eventTime, (DateTime)await ScalarAsync(connection, "system_health_events", "event_time"));
        Assert.Equal("memory_broker_ring_buffer_recorded", (string)await ScalarAsync(connection, "system_health_events", "event_type"));
        Assert.Equal(rawXml, (string)await ScalarAsync(connection, "system_health_events", "event_xml"));
    }

    [Fact]
    public async Task SystemHealthEvents_AppendRoundTrips_NullEventRow()
    {
        /* An all-NULL event row (defensive: the shred yielded no timestamp/name/xml) must still
           round-trip through the nullable columns. */
        var row = new SystemHealthEventsCollector.Row { EventTime = null, EventType = null, EventXml = null };

        using var connection = await AppendOneRowAsync(SystemHealthEventsCollector.Instance, row);

        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "system_health_events", "event_type"));
        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "system_health_events", "event_xml"));
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
