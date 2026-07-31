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
using System.Text.Json;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using PerformanceMonitor.Analysis;
using PerformanceMonitorLite.Analysis;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Tests;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Real-DuckDB round-trip pins for #1850: the two ANALYSIS-layer Query Store dedups must key on
/// <c>replica_role</c>, like the read side already does (#1845).
///
/// <para>On a SQL Server 2022+ Availability Group with Query Store for secondary replicas enabled, the
/// primary holds ONE shared Query Store carrying every replica's rows, and
/// <c>sys.query_store_runtime_stats</c> is keyed by (plan_id, interval, execution_type, replica_group).
/// Two rows differing only in <c>replica_role</c> are distinct legitimate work. The old partition —
/// (database_name, query_id, plan_id, runtime_stats_interval_id, first_execution_time) — is narrower
/// than that identity, so the <c>rn = 1</c> filter did not de-duplicate them: it DISCARDED one
/// replica's row. An under-count, and a silent one.</para>
///
/// <para>The seed is built so the old key fails LOUDLY rather than subtly. Both replicas run the same
/// two plans over the same two intervals, but the secondary is always collected one second later, so
/// under the old partition the secondary's row always wins <c>ORDER BY collection_time DESC</c> and the
/// PRIMARY — the replica with the 12x regression — vanishes entirely. The old key therefore reports one
/// offender at 3x; the truth is two offenders and a worst case of 12x. Watched RED at exactly those two
/// numbers before the fix.</para>
///
/// <para>Zero impact off an AG: <c>replica_role</c> is NULL on every standalone server, every non-AG
/// server and everything below SQL Server 2022, which is why the joins downstream use
/// <c>IS NOT DISTINCT FROM</c> rather than <c>=</c> — an equi-join on a NULL column matches nothing and
/// would have silently disabled plan-regression detection for almost every install. The
/// <see cref="NonAgServer_NullReplicaRole_StillDetectsTheRegression"/> arm is that guard.</para>
/// </summary>
public sealed class QueryStoreReplicaSplitAnalysisTests : IClassFixture<SharedDuckDbFixture>, IDisposable
{
    private const int ServerId = 8850;
    private const string ServerName = "AgPrimarySrv";
    private const string Db = "AgDb";
    private const long QueryId = 101;

    private const string GoodPlanHash = "0xGOODPLAN";
    private const string BadPlanHash = "0xBADPLAN";

    /* Per-execution CPU. The bad plan costs 12x the good one on the PRIMARY and only 3x on the
       SECONDARY, so which replica survives the dedup is visible in the worst-factor number itself. */
    private const long GoodCpuUs = 100_000;
    private const long BadCpuUsPrimary = 1_200_000;
    private const long BadCpuUsSecondary = 300_000;

    private readonly DuckDbInitializer _duckDb;
    private DuckDBConnection? _seedConn;
    private long _nextId = 1;

    public QueryStoreReplicaSplitAnalysisTests(SharedDuckDbFixture fixture)
    {
        fixture.ResetData();
        _duckDb = fixture.DuckDb;
    }

    public void Dispose() => _seedConn?.Dispose();

    /* The analysis window. Plan regression deliberately windows on last_execution_time over the
       14 days BEFORE TimeRangeStart (the days-old "best plan" baseline has to be in range), so the
       good plan's last execution sits 5 days back and the bad plan's at the end of the window. */
    private static readonly DateTime PeriodEnd =
        DateTime.SpecifyKind(new DateTime(DateTime.UtcNow.Ticks - (DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Unspecified);
    private static readonly DateTime PeriodStart = PeriodEnd.AddHours(-4);

    private static AnalysisContext Context() => new()
    {
        ServerId = ServerId,
        ServerName = ServerName,
        TimeRangeStart = PeriodStart,
        TimeRangeEnd = PeriodEnd,
    };

    private async Task<DuckDBConnection> SeedConnectionAsync()
    {
        if (_seedConn is null)
        {
            _seedConn = _duckDb.CreateConnection();
            await _seedConn.OpenAsync();
        }
        return _seedConn;
    }

    private async Task SeedAsync(
        DateTime collectionTime,
        long planId,
        string planHash,
        long intervalId,
        DateTime firstExecutionTime,
        DateTime lastExecutionTime,
        long avgCpuUs,
        string? replicaRole)
    {
        using var readLock = _duckDb.AcquireReadLock();
        var connection = await SeedConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO query_store_stats
    (collection_id, collection_time, server_id, server_name, database_name,
     query_id, plan_id, execution_type_desc, first_execution_time, last_execution_time,
     query_text, query_hash, execution_count, avg_cpu_time_us, avg_duration_us,
     query_plan_hash, is_forced_plan, force_failure_count,
     runtime_stats_interval_id, interval_start_time_utc, replica_role)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21)";
        cmd.Parameters.Add(new DuckDBParameter { Value = _nextId++ });
        cmd.Parameters.Add(new DuckDBParameter { Value = collectionTime });
        cmd.Parameters.Add(new DuckDBParameter { Value = ServerId });
        cmd.Parameters.Add(new DuckDBParameter { Value = ServerName });
        cmd.Parameters.Add(new DuckDBParameter { Value = Db });
        cmd.Parameters.Add(new DuckDBParameter { Value = QueryId });
        cmd.Parameters.Add(new DuckDBParameter { Value = planId });
        cmd.Parameters.Add(new DuckDBParameter { Value = "Regular" });
        cmd.Parameters.Add(new DuckDBParameter { Value = firstExecutionTime });
        cmd.Parameters.Add(new DuckDBParameter { Value = lastExecutionTime });
        cmd.Parameters.Add(new DuckDBParameter { Value = "SELECT * FROM dbo.Orders WHERE CustomerId = @id" });
        cmd.Parameters.Add(new DuckDBParameter { Value = "0xREGRESSQH" });
        /* 100 executions per (plan, replica) collection — comfortably past the HAVING SUM(execs) >= 25
           floor even after the dedup collapses the repeat collections down to one row. */
        cmd.Parameters.Add(new DuckDBParameter { Value = 100L });
        cmd.Parameters.Add(new DuckDBParameter { Value = avgCpuUs });
        /* Duration tracks CPU so GREATEST(cpu ratio, duration ratio) is unambiguous. */
        cmd.Parameters.Add(new DuckDBParameter { Value = avgCpuUs + 20_000 });
        cmd.Parameters.Add(new DuckDBParameter { Value = planHash });
        cmd.Parameters.Add(new DuckDBParameter { Value = false });
        cmd.Parameters.Add(new DuckDBParameter { Value = 0L });
        cmd.Parameters.Add(new DuckDBParameter { Value = intervalId });
        cmd.Parameters.Add(new DuckDBParameter { Value = firstExecutionTime });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)replicaRole ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// One query, two plans, two replicas, each interval collected TWICE — the shape a shared AG Query
    /// Store actually produces. The SECONDARY is always collected one second after the PRIMARY, which is
    /// what makes the old narrower key drop the PRIMARY specifically.
    /// </summary>
    private async Task SeedTwoReplicasAsync()
    {
        foreach (var (role, badCpu, offsetSeconds) in new[]
                 {
                     ("PRIMARY", BadCpuUsPrimary, 0),
                     ("SECONDARY", BadCpuUsSecondary, 1),
                 })
        {
            await SeedOneReplicaAsync(role, badCpu, offsetSeconds);
        }
    }

    private async Task SeedOneReplicaAsync(string? role, long badCpuUs, int offsetSeconds)
    {
        /* Plan 1, interval 1: the cheap baseline plan, last run 5 days ago. */
        var goodFirstExec = PeriodStart.AddDays(-6);
        var goodLastExec = PeriodStart.AddDays(-5);
        /* Plan 2, interval 2: the regressed plan, still running at the end of the window. */
        var badFirstExec = PeriodStart.AddDays(-1);
        var badLastExec = PeriodEnd;

        /* Two collections of each interval — the incremental re-collection the dedup exists to fold. */
        for (var collection = 0; collection < 2; collection++)
        {
            var collectionTime = PeriodEnd.AddMinutes(-10 + collection).AddSeconds(offsetSeconds);

            await SeedAsync(collectionTime, planId: 1, GoodPlanHash, intervalId: 1,
                goodFirstExec, goodLastExec, GoodCpuUs, role);
            await SeedAsync(collectionTime, planId: 2, BadPlanHash, intervalId: 2,
                badFirstExec, badLastExec, badCpuUs, role);
        }
    }

    private async Task<Fact?> CollectPlanRegressionFactAsync()
    {
        var facts = await new DuckDbFactCollector(_duckDb).CollectFactsAsync(Context());
        return facts.FirstOrDefault(f => f.Key == "PLAN_REGRESSION");
    }

    private async Task<List<JsonElement>> CollectRegressedQueriesDrillDownAsync()
    {
        var finding = new AnalysisFinding
        {
            RootFactKey = "PLAN_REGRESSION",
            StoryPath = "PLAN_REGRESSION",
            /* Past the 0.5 display gate in EnrichFindingsAsync — below it the expensive drill-downs are
               skipped wholesale and this collector never runs at all. */
            Severity = 1.0,
        };

        await new DrillDownCollector(_duckDb).EnrichFindingsAsync([finding], Context());

        if (finding.DrillDown is null || !finding.DrillDown.TryGetValue("regressed_queries", out var raw))
            return [];

        return [.. JsonSerializer.SerializeToElement(raw).EnumerateArray()];
    }

    [Fact]
    public async Task PlanRegressionFact_KeepsBothReplicas_NotJustTheLastCollected()
    {
        await SeedTwoReplicasAsync();

        var fact = await CollectPlanRegressionFactAsync();

        Assert.NotNull(fact);

        /* RED under the old key: 1. The primary's rows lost ORDER BY collection_time DESC to the
           secondary's and were discarded, so only one replica's regression was ever counted. */
        Assert.Equal(2.0, fact!.Metadata["offender_count"]);

        /* RED under the old key: 3. Dropping the primary did not merely lose a row — it reported the
           SECONDARY's much milder regression as the server's worst, understating the real problem by
           4x. This is the under-count being worse than a double-count, in one number. */
        Assert.Equal(12.0, fact.Metadata["worst_regression_factor"], precision: 1);
    }

    [Fact]
    public async Task RegressedQueriesDrillDown_ReturnsARowPerReplica_CarryingTheRole()
    {
        await SeedTwoReplicasAsync();

        var rows = await CollectRegressedQueriesDrillDownAsync();

        /* RED under the old key: 1 row. */
        Assert.Equal(2, rows.Count);

        var byRole = rows.ToDictionary(
            r => r.GetProperty("replica_role").GetString()!,
            r => r.GetProperty("regression_factor").GetDouble());

        /* The row shape carries the role so the operator can tell WHICH replica regressed — without it
           two rows for the same query_id would be indistinguishable noise. */
        Assert.Equal(["PRIMARY", "SECONDARY"], byRole.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(12.0, byRole["PRIMARY"], precision: 1);
        Assert.Equal(3.0, byRole["SECONDARY"], precision: 1);
    }

    [Fact]
    public async Task NonAgServer_NullReplicaRole_StillDetectsTheRegression()
    {
        /* The regression guard for the 99% case. replica_role is NULL on every standalone server, every
           non-AG server and everything below SQL Server 2022. Widening the dedup key and the downstream
           grouping is only safe because NULLs group together in PARTITION BY / GROUP BY and because the
           self-join uses IS NOT DISTINCT FROM — an equi-join would be UNKNOWN for every one of these
           rows and silently return nothing at all. */
        await SeedOneReplicaAsync(role: null, BadCpuUsPrimary, offsetSeconds: 0);

        var fact = await CollectPlanRegressionFactAsync();

        Assert.NotNull(fact);
        Assert.Equal(1.0, fact!.Metadata["offender_count"]);
        Assert.Equal(12.0, fact.Metadata["worst_regression_factor"], precision: 1);

        var rows = await CollectRegressedQueriesDrillDownAsync();
        Assert.Single(rows);
        /* NULL reads back as the empty string, not as a literal "NULL" or a dropped property. */
        Assert.Equal("", rows[0].GetProperty("replica_role").GetString());
    }

    [Fact]
    public async Task RepeatCollectionsOfOneInterval_AreStillCollapsed_PerReplica()
    {
        /* The wider key must not un-fix #1841: within ONE replica, the same interval collected twice is
           still ONE interval. Seeding a third and fourth collection of both intervals changes nothing —
           if the dedup had been weakened, the doubled execution_count would move the weighted averages
           and the regression factor with them. */
        await SeedTwoReplicasAsync();
        await SeedOneReplicaAsync("PRIMARY", BadCpuUsPrimary, offsetSeconds: 10);
        await SeedOneReplicaAsync("SECONDARY", BadCpuUsSecondary, offsetSeconds: 11);

        var fact = await CollectPlanRegressionFactAsync();

        Assert.NotNull(fact);
        Assert.Equal(2.0, fact!.Metadata["offender_count"]);
        Assert.Equal(12.0, fact.Metadata["worst_regression_factor"], precision: 1);
    }
}
