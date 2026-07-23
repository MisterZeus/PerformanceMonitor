/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the retained sql_handle→module map SQL (the join source for object_name on OLD query_stats CAGG windows).
/// Ungated — pure SQL-shape assertions.
/// </summary>
public sealed class DarlingModuleMapTests
{
    [Fact]
    public void CreateTable_HasCompositePkAndAttributionColumns()
    {
        var sql = DarlingModuleMap.CreateTableSql;

        Assert.Contains("CREATE TABLE IF NOT EXISTS collect.module_map", sql, StringComparison.Ordinal);
        /* (server_name, sql_handle) identity — a handle reused across servers attributes per server. */
        Assert.Contains("PRIMARY KEY (server_name, sql_handle)", sql, StringComparison.Ordinal);
        foreach (var col in new[] { "server_name", "sql_handle", "database_name", "schema_name", "object_name", "last_seen" })
        {
            Assert.Contains(col, sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Refresh_UpsertsLatestPerHandle_FromRecentProcedureStats_Accumulating()
    {
        var sql = DarlingModuleMap.RefreshSql;

        Assert.Contains("INSERT INTO collect.module_map", sql, StringComparison.Ordinal);
        Assert.Contains("FROM collect.procedure_stats", sql, StringComparison.Ordinal);
        /* the latest row per (server, handle) */
        Assert.Contains("DISTINCT ON (server_name, sql_handle)", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY server_name, sql_handle, collection_time DESC", sql, StringComparison.Ordinal);
        /* bounded read, comfortably inside procedure_stats' 4-day raw retention */
        Assert.Contains("collection_time >= now() - interval '2 days'", sql, StringComparison.Ordinal);
        Assert.Contains("sql_handle IS NOT NULL", sql, StringComparison.Ordinal);
        /* accumulate: upsert, and only ever advance — a stale run can't regress a fresher attribution */
        Assert.Contains("ON CONFLICT (server_name, sql_handle) DO UPDATE SET", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE module_map.last_seen IS NULL OR EXCLUDED.last_seen >= module_map.last_seen", sql, StringComparison.Ordinal);
    }
}
