/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using PerformanceMonitor.Darling.Service.Mcp;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the ADR persistent version store MCP slice (#2029). The surface is exactly <c>get_pvs_stats</c>
/// (static, [McpServerToolType], Task&lt;string&gt;) with an optional server_name and the trend opt-in knob;
/// both reads are Postgres-dialect over <c>v_pvs_stats</c> and mirror the Viewer's FinOps reads. The two
/// SEMANTIC pins: the snapshot read takes exactly the NEWEST collection (one row per database, biggest
/// version store first), and the trend read is bounded to the TOP-5 databases at that newest collection with
/// percent-of-database computed PER POINT from the same row's data-file denominator — the #2018 chart's
/// exact story, so no surface can disagree with another.
/// </summary>
public sealed class DarlingMcpPvsToolsTests
{
    private static MethodInfo[] ToolMethods() => typeof(DarlingMcpPvsTools)
        .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
        .ToArray();

    [Fact]
    public void ToolSurface_ExactlyGetPvsStats()
    {
        var toolMethods = ToolMethods();
        var names = toolMethods
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name)
            .ToArray();

        Assert.Equal(new[] { "get_pvs_stats" }, names);
        Assert.NotNull(typeof(DarlingMcpPvsTools).GetCustomAttribute<McpServerToolTypeAttribute>());
        Assert.All(toolMethods, m => Assert.True(m.IsStatic, $"{m.Name} must be static for WithGeminiCompatibleTools"));
        Assert.All(toolMethods, m => Assert.True(m.ReturnType == typeof(Task<string>), $"{m.Name} must return Task<string>"));
    }

    [Fact]
    public void ParamContract_ServerNameOptional_TrendOptInDefaultsOff()
    {
        var method = ToolMethods().Single();
        var mcpParams = method.GetParameters()
            .Where(p => p.GetCustomAttribute<DescriptionAttribute>() is not null)
            .Select(p => (p.Name, p.HasDefaultValue, p.DefaultValue))
            .ToArray();

        Assert.Equal(new[] { "server_name", "trend_hours_back" }, mcpParams.Select(p => p.Name).ToArray());
        Assert.True(mcpParams.Single(p => p.Name == "server_name").HasDefaultValue, "server_name must be optional");

        /* The trend is an OPT-IN: 0 means snapshot-only, so a default call stays one cheap read. */
        Assert.Equal(0, mcpParams.Single(p => p.Name == "trend_hours_back").DefaultValue);
    }

    [Fact]
    public void PvsStatsLatestSql_NewestCollectionOnly_BiggestVersionStoreFirst()
    {
        var sql = DarlingPvsReader.PvsStatsLatestSql;

        Assert.Contains("FROM v_pvs_stats", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", sql, StringComparison.Ordinal);

        /* The snapshot pin: exactly the newest collection — a window scan would blend captures and
           double-count databases. */
        Assert.Contains("collection_time = (", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT MAX(collection_time)", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY persistent_version_store_size_mb DESC NULLS LAST, database_name", sql, StringComparison.Ordinal);

        /* The grid's columns ride along — the denominator the pct is computed from must be present. */
        Assert.Contains("database_data_size_mb", sql, StringComparison.Ordinal);
        Assert.Contains("current_aborted_transaction_count", sql, StringComparison.Ordinal);
        Assert.Contains("aborted_version_cleaner_start_time", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PvsTrendSql_Top5AtNewestCollection_PctPerPointFromSameRow()
    {
        var sql = DarlingPvsReader.PvsTrendSql;

        /* The #2018 chart's exact read: top-5 by PVS size AT THE NEWEST COLLECTION (the databases whose
           growth story matters), then every stored point in the window for just those. */
        Assert.Contains("WITH top_dbs AS", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 5", sql, StringComparison.Ordinal);
        Assert.Contains("JOIN top_dbs t ON t.database_name = p.database_name", sql, StringComparison.Ordinal);

        /* Percent-of-database computed per POINT from the same row's denominator — the exact ratio the
           grid shows, so the two surfaces cannot disagree. */
        Assert.Contains("p.persistent_version_store_size_mb / p.database_data_size_mb * 100.0", sql, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN p.database_data_size_mb > 0", sql, StringComparison.Ordinal);

        Assert.Contains("p.collection_time >= $2", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY p.database_name, p.collection_time", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(DarlingPvsReader.PvsStatsLatestSql))]
    [InlineData(nameof(DarlingPvsReader.PvsTrendSql))]
    public void Reads_ArePostgresDialect_NoTsqlIsms(string sqlName)
    {
        var sql = (string)typeof(DarlingPvsReader).GetField(sqlName, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("N'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("getdate", sql.ToLowerInvariant());
        Assert.DoesNotContain("[", sql, StringComparison.Ordinal);
    }
}
