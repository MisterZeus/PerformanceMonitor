/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lite.Tests.Helpers;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the parity contract of the extracted database_size_stats definition: the two-site
/// exclusion splice (cursor + outer SELECT, same parameters referenced twice), the server-side
/// FILEPROPERTY staging batch, the Azure per-database variant, and the 19-column payload.
/// </summary>
public sealed class DatabaseSizeCollectorDefinitionTests
{
    private static readonly RecordingCollectorDeltaCalculator s_deltas = new();

    [Fact]
    public void BuildQuery_OnPrem_SplicesExclusionAtBothSites_ParamsOnce()
    {
        var plan = DatabaseSizeStatsCollector.Instance.BuildQuery(new CollectorContext
        {
            ServerId = 42,
            ServerName = "test-server",
            CollectionTime = DateTime.UtcNow,
            Deltas = s_deltas,
            ExcludedDatabases = new[] { "SO" },
        });

        Assert.Equal(2, Regex.Matches(plan.Text, Regex.Escape("AND d.name NOT IN (@excl_db_0)")).Count);
        Assert.DoesNotContain("/*EXCLUSION_FILTER", plan.Text, StringComparison.Ordinal);
        Assert.Contains("CURSOR LOCAL FAST_FORWARD", plan.Text, StringComparison.Ordinal);
        Assert.Contains("FILEPROPERTY(df.name, N''''SpaceUsed'''')", plan.Text, StringComparison.Ordinal);
        Assert.Contains("sys.dm_os_volume_stats", plan.Text, StringComparison.Ordinal);
        Assert.Equal("SO", Assert.Single(plan.Parameters).Value);
    }

    [Fact]
    public void AzureDmvPermissionHint_ExplainsError300_OnAzureOnly()
    {
        /* The other half of #1631: TrudAX asked whether waiting_tasks "really requires master DB
           access". It does not — and the raw error is what makes it look like it does, because Azure
           phrases a service-objective limit as a permission denied on 'master'. */
        var azure300 = PerformanceMonitor.Common.AzureDmvPermissionHint.For(300, isAzureSqlDb: true);

        Assert.NotEmpty(azure300);
        Assert.Contains("SERVICE OBJECTIVE", azure300, StringComparison.Ordinal);
        Assert.Contains("elastic pool", azure300, StringComparison.Ordinal);
        Assert.Contains("##MS_ServerStateReader##", azure300, StringComparison.Ordinal);
        Assert.Contains("VIEW DATABASE STATE", azure300, StringComparison.Ordinal);

        /* On-prem 300 IS a missing grant, and saying otherwise there would send people down the wrong
           path entirely — so the hint is Azure-only. */
        Assert.Empty(PerformanceMonitor.Common.AzureDmvPermissionHint.For(300, isAzureSqlDb: false));

        /* And it is scoped to the one error it explains. */
        Assert.Empty(PerformanceMonitor.Common.AzureDmvPermissionHint.For(229, isAzureSqlDb: true));
        Assert.Empty(PerformanceMonitor.Common.AzureDmvPermissionHint.For(40615, isAzureSqlDb: true));
    }

    [Fact]
    public void BuildQuery_Azure_IsDatabaseScoped_AndNeverEnumerates()
    {
        var plan = DatabaseSizeStatsCollector.Instance.BuildQuery(CollectorTestContext.Make(s_deltas, isAzureSqlDb: true));

        /* #1631: NOT per-database on Azure any more. RunsPerDatabase=true made the host enumerate
           databases first, and that enumeration connects to master — the one database an Azure login
           admitted by a DATABASE-level firewall rule cannot open (error 40615). The enumeration bought
           nothing: the query is database-scoped and the connection already points at the right database. */
        Assert.False(DatabaseSizeStatsCollector.Instance.RunsPerDatabase(new CollectorTargetInfo { IsAzureSqlDb = true }));
        Assert.False(DatabaseSizeStatsCollector.Instance.RunsPerDatabase(new CollectorTargetInfo()));

        Assert.Contains("FROM sys.database_files AS df", plan.Text, StringComparison.Ordinal);
        Assert.Empty(plan.Parameters);

        /* Nothing on the Azure path may reach outside the connected database. sys.master_files is not
           documented for Azure SQL DB at all, dm_os_volume_stats is SQL Server only, and
           dm_db_file_space_usage carries the same Basic/S0/S1/elastic-pool trap that broke the DMV in
           the sibling half of this issue — so it must not creep in as an "obvious" improvement. */
        foreach (var forbidden in new[] { "master_files", "dm_os_volume_stats", "dm_db_file_space_usage", "sys.databases" })
        {
            Assert.DoesNotContain(forbidden, plan.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PayloadColumns_MatchSchemaOrder_19Columns()
    {
        var names = DatabaseSizeStatsCollector.Instance.PayloadColumns.Select(c => c.Name).ToArray();
        Assert.Equal(19, names.Length);
        Assert.Equal("database_name", names[0]);
        Assert.Equal("vlf_count", names[18]);
    }

    [Fact]
    public async Task ReadAndWrite_RoundTrip_WithPercentGrowthBitConversion()
    {
        using var reader = new FakeCollectorDataReader(
            new object[]
            {
                "SO", 7, 2, "LOG", "SO_log", @"D:\so_log.ldf", 8192.00m, 512.25m,
                DBNull.Value, -1m, "FULL", 160, "ONLINE", @"D:\", 476837.12m, 100000.50m,
                1, 10, 42,
            });

        var rows = await DatabaseSizeStatsCollector.Instance.ReadAsync(reader, CollectorTestContext.Make(s_deltas), CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.True(row.IsPercentGrowth);         /* bit arrives as int 1 → true */
        Assert.Null(row.AutoGrowthMb);            /* percent growth → NULL auto_growth_mb */
        Assert.Equal(42, row.VlfCount);

        var writer = new RecordingCollectorRowWriter();
        DatabaseSizeStatsCollector.Instance.WritePayload(row, writer, CollectorTestContext.Make(s_deltas));
        Assert.Equal(19, writer.Values.Count);
        Assert.Equal("SO", writer.Values[0]);
        Assert.Equal(true, writer.Values[16]);
        Assert.Equal(42, writer.Values[18]);
    }
}
