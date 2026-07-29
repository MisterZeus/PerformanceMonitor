/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lite.Tests.Helpers;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the parity contract of the extracted database_scoped_config definition — the first
/// enumeration-shape collector: database-list selection (AG-primary filter on-prem, plain list
/// on Azure), the [db].sys.sp_executesql per-database query with bracket escaping, and the
/// 4-column payload.
/// </summary>
public sealed class DatabaseScopedConfigCollectorDefinitionTests
{
    private static readonly RecordingCollectorDeltaCalculator s_deltas = new();

    [Fact]
    public void EnumerationQuery_OnPrem_FiltersToAgPrimaries_AndSplicesExclusions()
    {
        var plan = DatabaseScopedConfigCollector.Instance.BuildEnumerationQuery(new CollectorContext
        {
            ServerId = 42,
            ServerName = "test-server",
            CollectionTime = DateTime.UtcNow,
            Deltas = s_deltas,
            ExcludedDatabases = new[] { "SO" },
        });

        Assert.NotNull(plan);
        Assert.Contains("sys.dm_hadr_database_replica_states", plan!.Text, StringComparison.Ordinal);
        Assert.Contains("is_primary_replica = 1", plan.Text, StringComparison.Ordinal);
        /* #1823: a least-privilege login without per-db access must be filtered out up front, the
           same self-skip index_object_stats and database_size_stats already do. */
        Assert.Contains("HAS_DBACCESS(d.name) = 1", plan.Text, StringComparison.Ordinal);
        Assert.Contains("AND d.name NOT IN (@excl_db_0)", plan.Text, StringComparison.Ordinal);
        Assert.Equal("SO", Assert.Single(plan.Parameters).Value);
    }

    [Fact]
    public void EnumerationQuery_Azure_ListsAllOnline_NoAgFilter()
    {
        var plan = DatabaseScopedConfigCollector.Instance.BuildEnumerationQuery(
            CollectorTestContext.Make(s_deltas, isAzureSqlDb: true));

        Assert.NotNull(plan);
        Assert.DoesNotContain("dm_hadr_database_replica_states", plan!.Text, StringComparison.Ordinal);
        Assert.Contains("state_desc = N'ONLINE'", plan.Text, StringComparison.Ordinal);
        /* The asymmetry is deliberate: from master on Azure SQL DB, HAS_DBACCESS() returns 0 for
           every user database, so the on-prem filter here would silently enumerate nothing. */
        Assert.DoesNotContain("HAS_DBACCESS", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PerItemQuery_EscapesClosingBrackets_InDatabaseNames()
    {
        var plan = DatabaseScopedConfigCollector.Instance.BuildPerItemQuery("we]rd db", CollectorTestContext.Make(s_deltas));

        Assert.Contains("EXECUTE [we]]rd db].sys.sp_executesql", plan.Text, StringComparison.Ordinal);
        Assert.Contains("sys.database_scoped_configurations", plan.Text, StringComparison.Ordinal);
        Assert.Empty(plan.Parameters);
    }

    [Fact]
    public async Task ReadItemAsync_AccumulatesAcrossItems_TaggedWithDatabase()
    {
        var rows = new List<DatabaseScopedConfigCollector.Row>();
        var context = CollectorTestContext.Make(s_deltas);

        using (var reader = new FakeCollectorDataReader(new object[] { "MAXDOP", "8", "0" }))
        {
            await DatabaseScopedConfigCollector.Instance.ReadItemAsync("db1", reader, rows, context, CancellationToken.None);
        }

        using (var reader = new FakeCollectorDataReader(new object[] { "MAXDOP", "4", DBNull.Value }))
        {
            await DatabaseScopedConfigCollector.Instance.ReadItemAsync("db2", reader, rows, context, CancellationToken.None);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(new DatabaseScopedConfigCollector.Row("db1", "MAXDOP", "8", "0"), rows[0]);
        Assert.Equal(new DatabaseScopedConfigCollector.Row("db2", "MAXDOP", "4", null), rows[1]);
    }

    [Fact]
    public void PayloadColumns_MatchSchema_AndWriteOrder()
    {
        Assert.Equal(
            new[] { "database_name", "configuration_name", "value", "value_for_secondary" },
            DatabaseScopedConfigCollector.Instance.PayloadColumns.Select(c => c.Name).ToArray());

        var writer = new RecordingCollectorRowWriter();
        DatabaseScopedConfigCollector.Instance.WritePayload(
            new DatabaseScopedConfigCollector.Row("db1", "MAXDOP", "8", null), writer, CollectorTestContext.Make(s_deltas));
        Assert.Equal(new object?[] { "db1", "MAXDOP", "8", null }, writer.Values);
    }
}
