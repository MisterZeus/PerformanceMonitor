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
/// Pins the parity contract of the extracted query_stats definition: the full row-identity delta
/// key (sql_handle:start:end:plan_handle — the multi-statement cross-contamination fix), the
/// interval-captured worker delta feeding sample_interval_seconds, the two query variants, and
/// the 50-column payload with the query_plan_xml placeholder.
/// </summary>
public sealed class QueryStatsCollectorDefinitionTests
{
    private static readonly RecordingCollectorDeltaCalculator s_deltas = new();

    [Fact]
    public void BuildQuery_Standard_JoinsPlanAttributes_WithExclusions()
    {
        var plan = QueryStatsCollector.Instance.BuildQuery(new CollectorContext
        {
            ServerId = 42,
            ServerName = "test-server",
            CollectionTime = DateTime.UtcNow,
            Deltas = s_deltas,
            ExcludedDatabases = new[] { "SO" },
        });

        Assert.Contains("sys.dm_exec_plan_attributes", plan.Text, StringComparison.Ordinal);
        Assert.Contains("AND d.name NOT IN (@excl_db_0)", plan.Text, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE N'%PerformanceMonitorLite%'", plan.Text, StringComparison.Ordinal);
        Assert.Equal("SO", Assert.Single(plan.Parameters).Value);

        AssertAppliesRunAgainstSurvivorsOnly(plan.Text);
    }

    [Fact]
    public void BuildQuery_Azure_SkipsPlanAttributes_RunsPerDatabase()
    {
        var plan = QueryStatsCollector.Instance.BuildQuery(CollectorTestContext.Make(s_deltas, isAzureSqlDb: true));

        Assert.DoesNotContain("dm_exec_plan_attributes", plan.Text, StringComparison.Ordinal);
        Assert.Contains("database_name = DB_NAME()", plan.Text, StringComparison.Ordinal);
        Assert.True(QueryStatsCollector.Instance.RunsPerDatabase(new CollectorTargetInfo { IsAzureSqlDb = true }));
        Assert.Empty(plan.Parameters);

        AssertAppliesRunAgainstSurvivorsOnly(plan.Text);
    }

    [Fact]
    public void AppliesTo_VersionGate_SkipsPreSql2016OnPrem_ButNotAzureOrUnknown()
    {
        /* Version gate collapsed from Lite's IsCollectorSupported into the shared AppliesTo (so Darling gates
           too). On-prem/RDS require v13+ (2016); a 2014 box lacks columns this reads. */
        Assert.False(QueryStatsCollector.Instance.AppliesTo(new CollectorTargetInfo { SqlMajorVersion = 12 }));
        Assert.True(QueryStatsCollector.Instance.AppliesTo(new CollectorTargetInfo { SqlMajorVersion = 13 }));
        Assert.True(QueryStatsCollector.Instance.AppliesTo(new CollectorTargetInfo { SqlMajorVersion = 16 }));
        /* Unknown (0) assumes newest; Azure SQL DB / MI report a low ProductMajorVersion but support the DMV. */
        Assert.True(QueryStatsCollector.Instance.AppliesTo(new CollectorTargetInfo { SqlMajorVersion = 0 }));
        Assert.True(QueryStatsCollector.Instance.AppliesTo(new CollectorTargetInfo { IsAzureSqlDb = true, SqlMajorVersion = 12 }));
        Assert.True(QueryStatsCollector.Instance.AppliesTo(new CollectorTargetInfo { IsAzureManagedInstance = true, SqlMajorVersion = 12 }));
    }

    [Fact]
    public void BuildQuery_PlanCaptureOffByDefault_NoPlanClauses_LiteParity()
    {
        /* Lite never sets CapturePlanXml: the flag defaults false, so neither the plan SELECT column
           nor the dm_exec_text_query_plan APPLY appears and the placeholders erase without a trace —
           the SQL is byte-identical to the no-plan form Lite has always shipped. */
        var standard = QueryStatsCollector.Instance.BuildQuery(MakeContext());
        var azure = QueryStatsCollector.Instance.BuildQuery(MakeContext(isAzureSqlDb: true));

        foreach (var text in new[] { standard.Text, azure.Text })
        {
            Assert.DoesNotContain("query_plan_xml", text, StringComparison.Ordinal);
            Assert.DoesNotContain("dm_exec_text_query_plan", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/*PLAN_SELECT*/", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/*PLAN_APPLY*/", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuildQuery_PlanCaptureOn_Standard_MirrorsDashboardTextQueryPlan()
    {
        /* Darling: mirrors install/08_collect_query_stats.sql's @collect_plan path — the
           statement-level plan from sys.dm_exec_text_query_plan keyed on the same plan_handle +
           statement offsets (the text DMV, so large/deep plans still return). */
        var plan = QueryStatsCollector.Instance.BuildQuery(MakeContext(capturePlanXml: true));

        Assert.Contains("query_plan_xml = tqp.query_plan", plan.Text, StringComparison.Ordinal);
        Assert.Contains(
            "sys.dm_exec_text_query_plan(qs.plan_handle,qs.statement_start_offset,qs.statement_end_offset)AStqp",
            Collapse(plan.Text), StringComparison.Ordinal);
        /* The on-prem plan_attributes join and the row-selection semantics are untouched. */
        Assert.Contains("sys.dm_exec_plan_attributes", plan.Text, StringComparison.Ordinal);
        Assert.Contains("TOP (200)", plan.Text, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", plan.Text, StringComparison.Ordinal);

        AssertAppliesRunAgainstSurvivorsOnly(plan.Text);
    }

    /// <summary>
    /// #1959: the text apply and the (Darling-only) plan render must sit OUTSIDE the ranked derived
    /// table - i.e., AFTER ") AS qs" closes it - so they run against at most the inner TOP's
    /// survivors. A field plan showed the render below the TOP executing 2,434 times to keep 200
    /// rows (81% of the sweep, 30-second timeout misses on big caches); this ordering IS the fix,
    /// so it is pinned structurally, not hoped for.
    /// </summary>
    private static void AssertAppliesRunAgainstSurvivorsOnly(string sql)
    {
        var collapsed = Collapse(sql);
        var derivedClose = collapsed.IndexOf(")ASqs", StringComparison.Ordinal);
        Assert.True(derivedClose > 0, "the ranked derived table ') AS qs' is missing - the rank-first shape was removed");

        Assert.True(
            collapsed.IndexOf("dm_exec_sql_text", StringComparison.Ordinal) > derivedClose,
            "dm_exec_sql_text moved back inside the ranked derived table - below the TOP");

        var planRender = collapsed.IndexOf("dm_exec_text_query_plan", StringComparison.Ordinal);
        if (planRender >= 0)
        {
            Assert.True(planRender > derivedClose,
                "dm_exec_text_query_plan moved back inside the ranked derived table - below the TOP");
        }

        Assert.True(
            collapsed.IndexOf("NOTLIKE", StringComparison.Ordinal) > derivedClose,
            "the self-filter moved back inside the ranked derived table - the inner TOP's headroom exists because it runs post-ranking");
        Assert.Contains("TOP(300)", collapsed, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildQuery_PlanCaptureOn_Azure_AlsoCapturesPlanWithoutPlanAttributes()
    {
        var plan = QueryStatsCollector.Instance.BuildQuery(MakeContext(isAzureSqlDb: true, capturePlanXml: true));

        Assert.Contains("query_plan_xml = tqp.query_plan", plan.Text, StringComparison.Ordinal);
        Assert.Contains(
            "sys.dm_exec_text_query_plan(qs.plan_handle,qs.statement_start_offset,qs.statement_end_offset)AStqp",
            Collapse(plan.Text), StringComparison.Ordinal);
        /* Azure SQL DB still skips plan_attributes (dbid=1 for all plans there). */
        Assert.DoesNotContain("dm_exec_plan_attributes", plan.Text, StringComparison.Ordinal);
        Assert.Contains("database_name = DB_NAME()", plan.Text, StringComparison.Ordinal);

        AssertAppliesRunAgainstSurvivorsOnly(plan.Text);
    }

    [Fact]
    public async Task ReadAsync_WritePayload_PlanCaptureOn_CapturesTrailingPlanColumn()
    {
        var deltas = new RecordingCollectorDeltaCalculator();
        var context = MakeContext(deltas: deltas, capturePlanXml: true);

        /* Flag on = the SELECT carries the trailing query_plan_xml column at ordinal 42. */
        var row43 = new object[43];
        row43[0] = "SO"; row43[1] = "0xQH"; row43[2] = "0xQPH";
        row43[3] = new DateTime(2026, 7, 2, 1, 0, 0, DateTimeKind.Utc);
        row43[4] = new DateTime(2026, 7, 2, 2, 0, 0, DateTimeKind.Utc);
        for (int i = 5; i < 36; i++) row43[i] = (long)i;
        row43[22] = 4L; row43[23] = 8L;
        row43[36] = "0xSH"; row43[37] = "0xPH"; row43[38] = "SELECT 1";
        row43[39] = 3L; row43[40] = 66; row43[41] = 512;
        row43[42] = "<ShowPlanXML>captured</ShowPlanXML>";

        using var reader = new FakeCollectorDataReader(row43);
        var rows = await QueryStatsCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        var writer = new RecordingCollectorRowWriter();
        QueryStatsCollector.Instance.WritePayload(Assert.Single(rows), writer, context);

        Assert.Equal(50, writer.Values.Count);
        Assert.Equal("<ShowPlanXML>captured</ShowPlanXML>", writer.Values[37]);   /* query_plan_xml payload slot */
    }

    private static string Collapse(string sql) => Regex.Replace(sql, @"\s+", "");

    private static CollectorContext MakeContext(
        ICollectorDeltaCalculator? deltas = null,
        bool isAzureSqlDb = false,
        bool capturePlanXml = false)
        => new()
        {
            ServerId = 42,
            ServerName = "test-server",
            CollectionTime = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc),
            Deltas = deltas ?? s_deltas,
            Target = new CollectorTargetInfo { IsAzureSqlDb = isAzureSqlDb },
            CapturePlanXml = capturePlanXml,
        };

    [Fact]
    public void BuildQuery_HandlesConvertToVarchar130_HashesStayVarchar64()
    {
        /* plan_handle/sql_handle are varbinary(64); style-1 hex is '0x' + 128 = 130 chars. The old
           CONVERT(varchar(64), ...) truncated the handle to ~31 bytes, so dm_exec_query_plan rejected the
           stored value and Fetch Live Plan failed for every query-grid row (and the by-sql_handle path).
           query_hash/query_plan_hash are binary(8) (18 chars) and correctly stay varchar(64). Both the
           on-prem and Azure builds share SelectColumnsText, so this guards both. */
        foreach (var text in new[]
        {
            Collapse(QueryStatsCollector.Instance.BuildQuery(MakeContext()).Text),
            Collapse(QueryStatsCollector.Instance.BuildQuery(MakeContext(isAzureSqlDb: true)).Text),
        })
        {
            Assert.Contains("sql_handle=CONVERT(varchar(130),qs.sql_handle,1)", text, StringComparison.Ordinal);
            Assert.Contains("plan_handle=CONVERT(varchar(130),qs.plan_handle,1)", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CONVERT(varchar(64),qs.sql_handle,1)", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CONVERT(varchar(64),qs.plan_handle,1)", text, StringComparison.Ordinal);
            Assert.Contains("query_hash=CONVERT(varchar(64),qs.query_hash,1)", text, StringComparison.Ordinal);
            Assert.Contains("query_plan_hash=CONVERT(varchar(64),qs.query_plan_hash,1)", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PayloadColumns_MatchSchemaOrder_50Columns()
    {
        var names = QueryStatsCollector.Instance.PayloadColumns.Select(c => c.Name).ToArray();
        Assert.Equal(50, names.Length);
        Assert.Equal("database_name", names[0]);
        Assert.Equal("query_plan_xml", names[37]);
        Assert.Equal("sample_interval_seconds", names[49]);
    }

    [Fact]
    public async Task WritePayload_PinsFullRowIdentityDeltaKey_AndIntervalCapture()
    {
        var deltas = new RecordingCollectorDeltaCalculator();
        var context = CollectorTestContext.Make(deltas);

        var row42 = new object[42];
        row42[0] = "SO"; row42[1] = "0xQH"; row42[2] = "0xQPH";
        row42[3] = new DateTime(2026, 7, 2, 1, 0, 0, DateTimeKind.Utc);
        row42[4] = new DateTime(2026, 7, 2, 2, 0, 0, DateTimeKind.Utc);
        for (int i = 5; i < 36; i++) row42[i] = (long)i;
        row42[22] = 4L; row42[23] = 8L;   /* dop as long via GetValue */
        row42[36] = "0xSH"; row42[37] = "0xPH"; row42[38] = "SELECT 1";
        row42[39] = 3L; row42[40] = 66; row42[41] = 512;

        using var reader = new FakeCollectorDataReader(row42);
        var rows = await QueryStatsCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        var writer = new RecordingCollectorRowWriter();
        QueryStatsCollector.Instance.WritePayload(Assert.Single(rows), writer, context);

        Assert.Equal(50, writer.Values.Count);
        Assert.Null(writer.Values[37]);                                   /* query_plan_xml placeholder */
        Assert.Equal(0, writer.Values[49]);                               /* interval from recording fake */
        Assert.Equal(8, deltas.Calls.Count);
        Assert.All(deltas.Calls, c => Assert.Equal("0xSH:66:512:0xPH", c.Key));
        Assert.Equal(
            new[] { "query_stats_exec", "query_stats_worker", "query_stats_elapsed", "query_stats_reads", "query_stats_writes", "query_stats_phys_reads", "query_stats_rows", "query_stats_spills" },
            deltas.Calls.Select(c => c.Group).ToArray());
    }
}
