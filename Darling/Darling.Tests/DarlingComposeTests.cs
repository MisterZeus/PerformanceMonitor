/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The ungated (no-live-store) contract for the Custom Views v2 backend (#1563): the hand-authored measure
/// catalog pinned to the real collector columns (drift = Erik's #1 pain), the compose spec validator (the
/// write-time + compile authority), the SQL compiler's safety invariants (catalog-only identifiers,
/// schema-qualified collect.*, every value bound, archetype-gated aggs, window-bounded #1568 join, DoS ceiling),
/// the ValidateDefinition v1/v2 dispatch, the /api/catalog v2 body, and the statement_timeout provisioning +
/// loopback-comment scrub. The live compile-and-run round-trip is exercised against a real store elsewhere; this
/// pins the pure, no-DB contract.
/// </summary>
public sealed class DarlingComposeTests
{
    /* ─────────────────────────── catalog pins (drift-proofing) ─────────────────────────── */

    private static Dictionary<string, HashSet<string>> PayloadColumnsByTable() =>
        CollectorCatalog.All.ToDictionary(
            c => c.TargetTable,
            c => c.PayloadColumns.Select(p => p.Name).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

    [Fact]
    public void EveryMeasureSource_IsARealCollectorTable()
    {
        var tables = CollectorCatalog.All.Select(c => c.TargetTable).ToHashSet(StringComparer.Ordinal);
        foreach (var measure in MeasureCatalog.Measures)
        {
            Assert.True(tables.Contains(measure.SourceTable),
                $"measure '{measure.Key}' names source '{measure.SourceTable}', which is not a collector table.");
        }
    }

    [Fact]
    public void NoMeasureSource_IsAConfigTable()
    {
        /* The structural config-exclusion guarantee (design §3): a composed query can only ever name a collect
           collector table, so it can never read a config control-plane table (hostnames, SMTP recipients, …).
           Every collector TargetTable is a collect-schema table; none begins with the config_ prefix. */
        foreach (var measure in MeasureCatalog.Measures)
        {
            Assert.False(measure.SourceTable.StartsWith("config", StringComparison.Ordinal),
                $"measure '{measure.Key}' resolves to a config table '{measure.SourceTable}'.");
        }
    }

    [Fact]
    public void EveryScalarMeasureColumn_IsARealPayloadColumn()
    {
        var payload = PayloadColumnsByTable();
        foreach (var measure in MeasureCatalog.Measures.Where(m => m.Kind == MeasureKind.Scalar))
        {
            var columns = payload[measure.SourceTable];
            Assert.True(measure.Column is not null && columns.Contains(measure.Column),
                $"measure '{measure.Key}' column '{measure.Column}' is not a payload column of '{measure.SourceTable}'.");

            /* A cumulative counter's delta column (what the aggregate actually operates on) must be real too. */
            if (measure.Archetype == MeasureArchetype.Cumulative)
            {
                Assert.True(measure.DeltaColumn is not null && columns.Contains(measure.DeltaColumn),
                    $"cumulative measure '{measure.Key}' delta column '{measure.DeltaColumn}' is not a payload column of '{measure.SourceTable}'.");
            }
        }
    }

    [Fact]
    public void EveryRatioMeasure_ReferencesRealSameSourceScalars()
    {
        /* Sum/Avg ratios divide two scalar measures; Weighted ratios instead reference RAW source columns
           (WeightedValueColumn + WeightColumn), pinned separately by WeightedRatio_Columns_AreRealPayloadColumns. */
        foreach (var ratio in MeasureCatalog.Measures.Where(m => m.Kind == MeasureKind.Ratio && m.RatioMode != MeasureRatioMode.Weighted))
        {
            var numerator = MeasureCatalog.Measure(ratio.NumeratorKey);
            var denominator = MeasureCatalog.Measure(ratio.DenominatorKey);
            Assert.True(numerator is not null, $"ratio '{ratio.Key}' numerator '{ratio.NumeratorKey}' is not a measure.");
            Assert.True(denominator is not null, $"ratio '{ratio.Key}' denominator '{ratio.DenominatorKey}' is not a measure.");
            Assert.Equal(MeasureKind.Scalar, numerator!.Kind);
            Assert.Equal(MeasureKind.Scalar, denominator!.Kind);
            Assert.Equal(ratio.SourceTable, numerator.SourceTable);
            Assert.Equal(ratio.SourceTable, denominator.SourceTable);
        }
    }

    [Fact]
    public void WeightedRatio_Columns_AreRealPayloadColumns()
    {
        var payload = PayloadColumnsByTable();
        var weighted = MeasureCatalog.Measures
            .Where(m => m.Kind == MeasureKind.Ratio && m.RatioMode == MeasureRatioMode.Weighted).ToList();
        Assert.NotEmpty(weighted);
        foreach (var ratio in weighted)
        {
            var columns = payload[ratio.SourceTable];
            /* A Weighted ratio's two operands are raw columns of its own source (the pre-aggregated average and
               its execution weight), so both must be real payload columns — the compiler emits them directly. */
            Assert.True(ratio.WeightedValueColumn is not null && columns.Contains(ratio.WeightedValueColumn),
                $"weighted ratio '{ratio.Key}' value column '{ratio.WeightedValueColumn}' is not a payload column of '{ratio.SourceTable}'.");
            Assert.True(ratio.WeightColumn is not null && columns.Contains(ratio.WeightColumn),
                $"weighted ratio '{ratio.Key}' weight column '{ratio.WeightColumn}' is not a payload column of '{ratio.SourceTable}'.");
            /* Weighted ratios do NOT use the numerator/denominator measure operands. */
            Assert.Null(ratio.NumeratorKey);
            Assert.Null(ratio.DenominatorKey);
        }
    }

    [Fact]
    public void EveryDimensionColumn_IsARealPayloadColumn_ExceptTheModuleJoin()
    {
        var payload = PayloadColumnsByTable();
        foreach (var dimension in MeasureCatalog.Dimensions)
        {
            /* The #1568 object_name is stitched from procedure_stats, not a column of the fact source. */
            var table = dimension.ViaModuleJoin ? "procedure_stats" : dimension.SourceTable;
            Assert.True(payload[table].Contains(dimension.Column),
                $"dimension '{dimension.SourceTable}.{dimension.Name}' column '{dimension.Column}' is not a payload column of '{table}'.");
        }
    }

    [Fact]
    public void EveryMeasureAllowedDimension_IsADeclaredDimensionOfItsSource()
    {
        foreach (var measure in MeasureCatalog.Measures)
        {
            foreach (var dimensionName in measure.AllowedDimensions)
            {
                Assert.True(MeasureCatalog.Dimension(measure.SourceTable, dimensionName) is not null,
                    $"measure '{measure.Key}' allows dimension '{dimensionName}', which is not declared for source '{measure.SourceTable}'.");
            }
        }
    }

    [Fact]
    public void Gauges_AreNeverSummable()
    {
        /* The grain-trap guard: a gauge has no aggregation delta column and never offers SUM. */
        foreach (var gauge in MeasureCatalog.Measures.Where(m => m.Archetype == MeasureArchetype.Gauge))
        {
            Assert.Null(gauge.AggregationColumn);
            Assert.DoesNotContain(ComposeAggregate.Sum, gauge.ValidAggs);
        }
    }

    [Fact]
    public void PercentileCont_IsOfferedOnlyByPerEventMeasures()
    {
        foreach (var measure in MeasureCatalog.Measures)
        {
            if (measure.ValidAggs.Contains(ComposeAggregate.PercentileCont))
            {
                Assert.Equal(MeasureArchetype.PerEvent, measure.Archetype);
            }
        }
    }

    [Fact]
    public void TheSlice_ProvesEveryArchetype()
    {
        var archetypes = MeasureCatalog.Measures.Where(m => m.Kind == MeasureKind.Scalar).Select(m => m.Archetype).ToHashSet();
        Assert.Contains(MeasureArchetype.Cumulative, archetypes);
        Assert.Contains(MeasureArchetype.Delta, archetypes);
        Assert.Contains(MeasureArchetype.Gauge, archetypes);
        Assert.Contains(MeasureArchetype.PerEvent, archetypes);
        Assert.Contains(MeasureCatalog.Measures, m => m.Kind == MeasureKind.Ratio);
    }

    /* ─────────────────────────── the compose spec validator ─────────────────────────── */

    private static JsonObject PanelJson(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static PanelPlan ValidPlan(string json, params string[] declaredVariables)
    {
        var (plan, error) = ComposeSpec.TryParsePanel(PanelJson(json), declaredVariables);
        Assert.True(error is null, error);
        Assert.NotNull(plan);
        return plan!;
    }

    private static string RejectReason(string json, params string[] declaredVariables)
    {
        var (plan, error) = ComposeSpec.TryParsePanel(PanelJson(json), declaredVariables);
        Assert.Null(plan);
        Assert.NotNull(error);
        return error!;
    }

    [Fact]
    public void TryParsePanel_AcceptsAWellFormedScalarTimeSeries()
    {
        var plan = ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"minute\",\"viz\":\"line\",\"groupBy\":[\"wait_type\"]}");
        Assert.Equal(PanelMode.TimeSeries, plan.Mode);
        Assert.Equal(ComposeAggregate.Sum, plan.Aggregate);
        Assert.Single(plan.GroupBy);
    }

    [Fact]
    public void TryParsePanel_AcceptsARankedBar()
    {
        var plan = ValidPlan("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"table\"}");
        Assert.Equal(PanelMode.Ranked, plan.Mode);
        Assert.Equal(10, plan.TopN);
    }

    [Fact]
    public void TryParsePanel_AcceptsARatio()
    {
        var plan = ValidPlan("{\"source\":\"wait_stats\",\"ratio\":\"signal_wait_pct\",\"timeBucket\":\"minute\",\"viz\":\"line\"}");
        Assert.Equal(MeasureKind.Ratio, plan.Measure.Kind);
        Assert.Equal("percent", plan.Unit);
    }

    [Fact]
    public void TryParsePanel_AcceptsAModuleNameLikeFilter_OnAVariable()
    {
        var plan = ValidPlan(
            "{\"source\":\"procedure_stats\",\"measure\":\"proc_elapsed_us\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\"," +
            "\"filters\":[{\"dimension\":\"object_name\",\"op\":\"like\",\"value\":\"dbo.usp_Payment%\"},{\"dimension\":\"database_name\",\"op\":\"eq\",\"value\":\"$db\"}]}",
            "db");
        Assert.Equal(2, plan.Filters.Count);
        Assert.True(plan.Filters[1].Value.IsVariable);
    }

    [Theory]
    [InlineData("{\"source\":\"nope\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"table\"}", "unknown source")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"nope\",\"aggregate\":\"sum\",\"viz\":\"table\"}", "unknown measure")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"viz\":\"table\"}", "is not on source")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"signal_wait_pct\",\"aggregate\":\"sum\",\"viz\":\"table\"}", "reference it as 'ratio'")]
    [InlineData("{\"source\":\"wait_stats\",\"ratio\":\"wait_time_ms\",\"viz\":\"table\"}", "reference it as 'measure'")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"viz\":\"table\"}", "missing 'aggregate'")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"nope\",\"viz\":\"table\"}", "unknown aggregate")]
    /* SUM on a gauge — the grain-trap the archetype gate blocks. */
    [InlineData("{\"source\":\"cpu_utilization_stats\",\"measure\":\"sqlserver_cpu_utilization\",\"aggregate\":\"sum\",\"viz\":\"table\"}", "not valid for measure")]
    /* percentile on a non-per-event measure. */
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"percentile_cont\",\"viz\":\"table\"}", "not valid for measure")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"unit\":\"gb\",\"viz\":\"table\"}", "not valid for measure")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"nope\"}", "unknown or missing viz")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"nope\",\"viz\":\"line\"}", "unknown timeBucket")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"minute\",\"topN\":5,\"viz\":\"line\"}", "cannot set both")]
    /* like on a non-likeable dimension (query_hash). */
    [InlineData("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"viz\":\"table\",\"filters\":[{\"dimension\":\"query_hash\",\"op\":\"like\",\"value\":\"x\"}]}", "not allowed on dimension")]
    /* a dimension the measure does not allow. */
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"table\",\"groupBy\":[\"database_name\"]}", "not allowed for measure")]
    public void TryParsePanel_RejectsOffCatalog_NamingTheReason(string json, string expectedFragment)
    {
        var reason = RejectReason(json);
        Assert.Contains(expectedFragment, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParsePanel_RejectsAnUndeclaredVariable()
    {
        var reason = RejectReason(
            "{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"table\",\"filters\":[{\"dimension\":\"wait_type\",\"op\":\"eq\",\"value\":\"$undeclared\"}]}");
        Assert.Contains("undeclared variable", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParsePanel_RejectsTooManyFilters()
    {
        var filters = string.Join(",", Enumerable.Repeat("{\"dimension\":\"wait_type\",\"op\":\"eq\",\"value\":\"x\"}", ComposeLimits.MaxFilters + 1));
        var reason = RejectReason("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"table\",\"filters\":[" + filters + "]}");
        Assert.Contains("maximum", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseVariables_RejectsUnknownDimension_AndDuplicates()
    {
        Assert.NotNull(ComposeSpec.ParseVariables(JsonNode.Parse("[{\"name\":\"x\",\"dimension\":\"nope\"}]")).Error);
        Assert.NotNull(ComposeSpec.ParseVariables(JsonNode.Parse("[{\"name\":\"x\",\"dimension\":\"server\"},{\"name\":\"x\",\"dimension\":\"database_name\"}]")).Error);
        Assert.Null(ComposeSpec.ParseVariables(JsonNode.Parse("[{\"name\":\"srv\",\"dimension\":\"server\"}]")).Error);
    }

    [Theory]
    [InlineData("{\"hours\":24}", true)]
    [InlineData("{\"hours\":0}", false)]
    [InlineData("{\"hours\":100000}", false)]
    public void ParseRange_BoundsHours(string json, bool valid)
    {
        var (_, error) = ComposeSpec.ParseRange(JsonNode.Parse(json));
        Assert.Equal(valid, error is null);
    }

    /* ─────────────────────────── the SQL compiler (safety invariants) ─────────────────────────── */

    private static readonly DateTime WindowStart = new(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc);

    private static string Compile(PanelPlan plan, IReadOnlyList<string>? servers = null, IReadOnlyDictionary<string, string?>? variables = null)
    {
        var (compiled, error) = ComposeCompiler.Compile(
            plan, new ComposeRunContext(servers, WindowStart, WindowEnd, variables ?? ComposeRunContext.NoVariables));
        Assert.True(error is null, error);
        Assert.NotNull(compiled);
        return compiled!.Sql;
    }

    [Fact]
    public void Compile_QualifiesCollect_AndNeverConfig()
    {
        var sql = Compile(ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("collect.wait_stats", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("config.", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_BindsTheWindowAndServerScope_NoRawValues()
    {
        var sql = Compile(ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"), new[] { "PROD-01" });
        Assert.Contains("$1", sql, StringComparison.Ordinal);   /* window start */
        Assert.Contains("$2", sql, StringComparison.Ordinal);   /* window end */
        Assert.Contains("server_name = ANY($3)", sql, StringComparison.Ordinal);
        /* The server name is a bound parameter, never interpolated. */
        Assert.DoesNotContain("PROD-01", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_BindsFilterValues_NeverInterpolatesThem()
    {
        var sql = Compile(ValidPlan(
            "{\"source\":\"procedure_stats\",\"measure\":\"proc_elapsed_us\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\"," +
            "\"filters\":[{\"dimension\":\"object_name\",\"op\":\"like\",\"value\":\"dbo.usp_Payment%\"}]}"));
        Assert.Contains("LIKE $", sql, StringComparison.Ordinal);
        /* The LIKE pattern is a bound parameter, never in the SQL text. */
        Assert.DoesNotContain("usp_Payment", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_EqFilter_IsABoundArray()
    {
        var sql = Compile(ValidPlan(
            "{\"source\":\"file_io_stats\",\"measure\":\"file_read_bytes\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"," +
            "\"filters\":[{\"dimension\":\"database_name\",\"op\":\"eq\",\"value\":[\"Sales\",\"HR\"]}]}"));
        Assert.Contains("= ANY($", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Sales", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_Ratio_IsSumOverNullifSum()
    {
        var sql = Compile(ValidPlan("{\"source\":\"wait_stats\",\"ratio\":\"signal_wait_pct\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("NULLIF(SUM(", sql, StringComparison.Ordinal);
        Assert.Contains("delta_signal_wait_time_ms", sql, StringComparison.Ordinal);
        Assert.Contains("delta_wait_time_ms", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_Percentile_OnlyRendersForPerEvent()
    {
        var sql = Compile(ValidPlan("{\"source\":\"long_query_completions\",\"measure\":\"lqc_duration_us\",\"aggregate\":\"percentile_cont\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("percentile_cont(0.95)", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ComposeTimeBucket.None)]
    [InlineData(ComposeTimeBucket.Minute)]
    [InlineData(ComposeTimeBucket.Hour)]
    [InlineData(ComposeTimeBucket.Day)]
    public void ResolveBucket_LeavesNonAutoUnchanged(ComposeTimeBucket bucket)
    {
        Assert.Equal(bucket, MeasureCatalog.ResolveBucket(bucket, 3600d));
    }

    [Fact]
    public void ResolveBucket_Auto_PicksGrainFromWindow()
    {
        Assert.Equal(ComposeTimeBucket.Minute, MeasureCatalog.ResolveBucket(ComposeTimeBucket.Auto, 2d * 86_400d));       // 2 days -> minute
        Assert.Equal(ComposeTimeBucket.Hour, MeasureCatalog.ResolveBucket(ComposeTimeBucket.Auto, 2d * 86_400d + 1d));   // just over 2 days -> hour
        Assert.Equal(ComposeTimeBucket.Hour, MeasureCatalog.ResolveBucket(ComposeTimeBucket.Auto, 60d * 86_400d));       // 60 days -> hour
        Assert.Equal(ComposeTimeBucket.Day, MeasureCatalog.ResolveBucket(ComposeTimeBucket.Auto, 60d * 86_400d + 1d));   // over 60 days -> day
    }

    [Theory]
    [InlineData(6, "date_trunc('minute'")]      // 6h window -> minute grain
    [InlineData(24 * 10, "date_trunc('hour'")]  // 10 days -> hour grain
    [InlineData(24 * 90, "date_trunc('day'")]   // 90 days -> day grain
    public void Compile_AutoBucket_ResolvesGrainFromWindow(int windowHours, string expectedTrunc)
    {
        var plan = ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"auto\",\"viz\":\"line\"}");
        var end = WindowEnd;
        var start = end.AddHours(-windowHours);
        var (compiled, error) = ComposeCompiler.Compile(plan, new ComposeRunContext(null, start, end, ComposeRunContext.NoVariables));
        Assert.True(error is null, error);
        Assert.Contains(expectedTrunc, compiled!.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_UnitConversion_ScalesMicrosecondsToMilliseconds()
    {
        /* proc_elapsed_us: native µs, default ms — the aggregate is divided by the 1000 family factor. */
        var sql = Compile(ValidPlan("{\"source\":\"procedure_stats\",\"measure\":\"proc_elapsed_us\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("/ 1000.0", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_ModuleJoin_IsEmittedAndWindowBounded_ForTheDerivedObject()
    {
        var sql = Compile(ValidPlan(
            "{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"object_name\"],\"viz\":\"bar\"}"), new[] { "PROD-01" });
        Assert.Contains("procedure_stats", sql, StringComparison.Ordinal);
        Assert.Contains("ROW_NUMBER()", sql, StringComparison.Ordinal);
        /* Per-server attribution — a handle reused across servers attributes per server, not globally. */
        Assert.Contains("PARTITION BY server_name, sql_handle", sql, StringComparison.Ordinal);
        /* The #1568 stitch is bounded by the SAME window ($1/$2) and scoped to the same server set ($3). */
        Assert.Contains("collection_time >= $1", sql, StringComparison.Ordinal);
        Assert.Contains("collection_time <= $2", sql, StringComparison.Ordinal);
        Assert.Contains("server_name = ANY($3)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_NoModuleJoin_WhenNoDerivedObjectUsed()
    {
        var sql = Compile(ValidPlan("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"table\"}"));
        Assert.DoesNotContain("ROW_NUMBER()", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_RankedShape_OrdersByValueDescWithBoundLimit()
    {
        var sql = Compile(ValidPlan("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"table\"}"));
        Assert.Contains("ORDER BY value DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_RejectsTooFineABucketOverTooWideAWindow()
    {
        var plan = ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"minute\",\"viz\":\"line\"}");
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddDays(60); /* 60 days of minutes >> MaxBuckets */
        var (compiled, error) = ComposeCompiler.Compile(plan, new ComposeRunContext(null, start, end, ComposeRunContext.NoVariables));
        Assert.Null(compiled);
        Assert.Contains("points", error!, StringComparison.OrdinalIgnoreCase);
    }

    /* ─────────────── CAGG read-routing (age-based source selection) ─────────────── */

    private static (ComposeCompiled? Compiled, string? Error) CompileAged(string planJson, int daysOld, string[]? servers = null)
    {
        var plan = ValidPlan(planJson);
        var end = WindowEnd;                 /* EndUtc serves as "now" in the compiler */
        var start = end.AddDays(-daysOld);
        return ComposeCompiler.Compile(plan, new ComposeRunContext(servers, start, end, ComposeRunContext.NoVariables));
    }

    [Fact]
    public void Compile_OldWindow_RoutesQueryStatsToHourlyCagg_RemappingColumns()
    {
        var (compiled, error) = CompileAged(
            "{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}", daysOld: 10);
        Assert.True(error is null, error);
        var sql = compiled!.Sql;
        Assert.Contains("FROM collect.query_stats_hourly AS f", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(f.worker_time_sum) AS double precision)", sql, StringComparison.Ordinal);
        Assert.Contains("date_trunc('hour', f.bucket)", sql, StringComparison.Ordinal);  /* the CAGG time column */
        Assert.DoesNotContain("delta_worker_time", sql, StringComparison.Ordinal);       /* not the raw column */
    }

    [Fact]
    public void Compile_VeryOldWindow_RoutesToDailyCagg_AndCoarsensDisplayGrainToDay()
    {
        /* hour requested, but 40 days old -> daily CAGG, and the grain clamps up to day (can't render finer). */
        var (compiled, error) = CompileAged(
            "{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}", daysOld: 40);
        Assert.True(error is null, error);
        Assert.Contains("FROM collect.query_stats_daily AS f", compiled!.Sql, StringComparison.Ordinal);
        Assert.Contains("date_trunc('day', f.bucket)", compiled.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_OldWindow_AvgRemapsToSumOverSampleCount()
    {
        var (compiled, error) = CompileAged(
            "{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\"}", daysOld: 10);
        Assert.True(error is null, error);
        Assert.Contains("CAST(SUM(f.worker_time_sum) AS double precision) / NULLIF(SUM(f.sample_count), 0)", compiled!.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_OldWindow_SumRatio_RemapsBothOperands()
    {
        var (compiled, error) = CompileAged(
            "{\"source\":\"query_stats\",\"ratio\":\"query_avg_elapsed_us\",\"timeBucket\":\"hour\",\"viz\":\"line\"}", daysOld: 10);
        Assert.True(error is null, error);
        Assert.Contains("SUM(f.elapsed_time_sum) AS double precision) / NULLIF(SUM(f.execution_count_sum), 0)", compiled!.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_OldWindow_QueryStore_WeightedRatio_RoutesToHourlyCagg_RemapsWeightedMean()
    {
        /* QS has no daily CAGG, so even 40 days old routes to the hourly rollup; the weighted mean remaps to the
           reshaped weighted-sum over execution_count_sum (exact, not an avg-of-avgs). */
        var (compiled, error) = CompileAged(
            "{\"source\":\"query_store_stats\",\"ratio\":\"qs_avg_duration_us\",\"timeBucket\":\"hour\",\"viz\":\"line\"}", daysOld: 40);
        Assert.True(error is null, error);
        Assert.Contains("FROM collect.query_store_stats_hourly AS f", compiled!.Sql, StringComparison.Ordinal);
        Assert.Contains("SUM(f.duration_us_weighted_sum) AS double precision) / NULLIF(SUM(f.execution_count_sum), 0)", compiled.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_OldWindow_NonCaggTable_StaysRaw()
    {
        var (compiled, _) = CompileAged(
            "{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}", daysOld: 40);
        Assert.Contains("FROM collect.wait_stats AS f", compiled!.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("_hourly", compiled.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("_daily", compiled.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_OldWindow_ObjectName_RoutesToCagg_JoinsModuleMap()
    {
        /* object_name on query_stats now routes to the CAGG (40d -> daily) and joins the retained module_map for
           attribution, instead of the window-bounded procedure_stats #1568 CTE. */
        var (compiled, error) = CompileAged(
            "{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"object_name\"],\"viz\":\"bar\"}",
            daysOld: 40, servers: new[] { "PROD-01" });
        Assert.True(error is null, error);
        Assert.Contains("FROM collect.query_stats_daily AS f", compiled!.Sql, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN collect.module_map AS m ON m.sql_handle = f.sql_handle AND m.server_name = f.server_name", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains("m.object_name", compiled.Sql, StringComparison.Ordinal);        /* attribution from the map */
        Assert.DoesNotContain("ROW_NUMBER()", compiled.Sql, StringComparison.Ordinal);   /* not the raw #1568 CTE */
    }

    [Fact]
    public void Compile_RecentWindow_QueryStats_StaysRaw()
    {
        /* The 6-hour helper window is well inside the raw horizon -> raw columns, byte-for-byte as before. */
        var sql = Compile(ValidPlan("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("FROM collect.query_stats AS f", sql, StringComparison.Ordinal);
        Assert.Contains("delta_worker_time", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("worker_time_sum", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_ResolvesAVariableToABoundValue()
    {
        var plan = ValidPlan(
            "{\"source\":\"file_io_stats\",\"measure\":\"file_read_bytes\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"," +
            "\"filters\":[{\"dimension\":\"database_name\",\"op\":\"eq\",\"value\":\"$db\"}]}",
            "db");
        var sql = Compile(plan, variables: new Dictionary<string, string?>(StringComparer.Ordinal) { ["db"] = "Sales" });
        Assert.Contains("= ANY($", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Sales", sql, StringComparison.Ordinal);
    }

    /* ─────────────────────────── ValidateDefinition: v1/v2 dispatch ─────────────────────────── */

    [Fact]
    public void ValidateDefinition_AcceptsAComposedPanel()
    {
        var ok = DarlingWebEndpoints.ValidateDefinition(
            "{\"panels\":[{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}]}");
        Assert.True(ok.IsValid, ok.Error);
    }

    [Fact]
    public void ValidateDefinition_AcceptsAMixedV1AndV2Definition()
    {
        var ok = DarlingWebEndpoints.ValidateDefinition(
            "{\"variables\":[{\"name\":\"db\",\"dimension\":\"database_name\"}]," +
            "\"panels\":[{\"read\":\"get_wait_stats\",\"viz\":\"table\"}," +
            "{\"source\":\"procedure_stats\",\"measure\":\"proc_elapsed_us\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\"," +
            "\"filters\":[{\"dimension\":\"database_name\",\"op\":\"eq\",\"value\":\"$db\"}]}]}");
        Assert.True(ok.IsValid, ok.Error);
    }

    [Fact]
    public void ValidateDefinition_RejectsABadComposedPanel_NamingThePanel()
    {
        var result = DarlingWebEndpoints.ValidateDefinition(
            "{\"panels\":[{\"source\":\"wait_stats\",\"measure\":\"nope\",\"aggregate\":\"sum\",\"viz\":\"table\"}]}");
        Assert.False(result.IsValid);
        Assert.Contains("panel 0", result.Error!, StringComparison.Ordinal);
        Assert.Contains("unknown measure", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDefinition_RejectsAComposedPanelWithAnUndeclaredVariable()
    {
        var result = DarlingWebEndpoints.ValidateDefinition(
            "{\"panels\":[{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"table\"," +
            "\"filters\":[{\"dimension\":\"wait_type\",\"op\":\"eq\",\"value\":\"$nope\"}]}]}");
        Assert.False(result.IsValid);
        Assert.Contains("undeclared variable", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDefinition_RejectsBadViewVariables()
    {
        var result = DarlingWebEndpoints.ValidateDefinition(
            "{\"variables\":[{\"name\":\"x\",\"dimension\":\"nope\"}],\"panels\":[{\"read\":\"get_wait_stats\",\"viz\":\"table\"}]}");
        Assert.False(result.IsValid);
        Assert.Contains("dimension", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    /* ─────────────────────────── /api/catalog v2 ─────────────────────────── */

    [Fact]
    public void Catalog_CarriesTheComposeSection()
    {
        var catalog = DarlingWebEndpoints.BuildCatalogNode();

        /* v1 keys stay intact. */
        Assert.NotNull(catalog["reads"]);
        Assert.NotNull(catalog["viz"]);

        var compose = Assert.IsType<JsonObject>(catalog["compose"]);
        var measures = Assert.IsType<JsonArray>(compose["measures"]);
        Assert.Equal(MeasureCatalog.Measures.Count, measures.Count);
        Assert.NotNull(compose["dimensions"]);
        Assert.NotNull(compose["unitFamilies"]);
        Assert.NotNull(compose["aggregates"]);
        Assert.NotNull(compose["timeBuckets"]);
        Assert.NotNull(compose["filterOps"]);
    }

    /* ─────────────────────────── DoS backstop + loopback scrub (provisioning) ─────────────────────────── */

    [Fact]
    public void Provisioning_SetsStatementTimeout_OnViewerAndMcp_NotAdmin()
    {
        var sql = DarlingManagedRoles.BuildProvisioningSql("AdminPassword01", "ViewerPassword02", "McpPassword03");
        Assert.Contains($"ALTER ROLE viewer SET statement_timeout = '{ComposeLimits.StatementTimeout}';", sql, StringComparison.Ordinal);
        Assert.Contains($"ALTER ROLE mcp    SET statement_timeout = '{ComposeLimits.StatementTimeout}';", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER ROLE admin  SET statement_timeout", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Provisioning_HasNoStaleLoopbackComment()
    {
        var sql = DarlingManagedRoles.BuildProvisioningSql("AdminPassword01", "ViewerPassword02", "McpPassword03");
        Assert.DoesNotContain("LOOPBACK-ONLY", sql, StringComparison.Ordinal);
    }

    /* ─────────────────────────── fleet / multi-server (§2b + Flow B) ─────────────────────────── */

    [Fact]
    public void Compile_Fleet_HasNoServerPredicate_WhenNoServerScope()
    {
        var sql = Compile(ValidPlan("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"bar\"}"));
        Assert.DoesNotContain("server_name = ANY", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_MultiServer_BindsServerNameArray_NeverInterpolated()
    {
        var sql = Compile(
            ValidPlan("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"bar\"}"),
            new[] { "PROD-01", "PROD-02" });
        Assert.Contains("server_name = ANY($", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("PROD-01", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("PROD-02", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_GroupByServer_SelectsServerName_OnAServerGrainMeasure()
    {
        /* Flow B's grain-trap alternative: "top servers by CPU" — group a server-grain gauge by the universal
           server dimension across the fleet. */
        var sql = Compile(ValidPlan("{\"source\":\"cpu_utilization_stats\",\"measure\":\"sqlserver_cpu_utilization\",\"aggregate\":\"avg\",\"topN\":5,\"groupBy\":[\"server\"],\"viz\":\"bar\"}"));
        Assert.Contains("server_name AS server", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParsePanel_AllowsServer_AsFilterAndGroupBy_OnEveryMeasure()
    {
        /* server is universal even on a measure with NO declared dimensions (cpu is server-grain). */
        var plan = ValidPlan(
            "{\"source\":\"cpu_utilization_stats\",\"measure\":\"sqlserver_cpu_utilization\",\"aggregate\":\"avg\",\"topN\":5,\"groupBy\":[\"server\"],\"viz\":\"bar\"," +
            "\"filters\":[{\"dimension\":\"server\",\"op\":\"eq\",\"value\":[\"A\",\"B\"]}]}");
        Assert.Single(plan.GroupBy);
        Assert.Single(plan.Filters);
    }

    /* ─────────────────────────── viz ↔ mode coherence (§4) ─────────────────────────── */

    [Theory]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"pie\"}", "not a time series")]
    [InlineData("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"line\"}", "ranked")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"stacked\"}", "needs a group-by")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"line\"}", "single value")]
    [InlineData("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"viz\":\"bar\"}", "needs a group-by")]
    /* D2: stacked-bar behaves exactly like stacked — a time-series viz that needs a group-by, rejected for ranked/scalar. */
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"stacked-bar\"}", "needs a group-by")]
    [InlineData("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"stacked-bar\"}", "ranked")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"stacked-bar\"}", "single value")]
    public void TryParsePanel_RejectsIncoherentVizForMode(string json, string expectedFragment)
    {
        Assert.Contains(expectedFragment, RejectReason(json), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"area\"}")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"groupBy\":[\"wait_type\"],\"viz\":\"stacked\"}")]
    [InlineData("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"pie\"}")]
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"stat\"}")]
    /* D2: stacked-bar accepted for a grouped time series, exactly like stacked. */
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"groupBy\":[\"wait_type\"],\"viz\":\"stacked-bar\"}")]
    public void TryParsePanel_AcceptsCoherentVizForMode(string json)
    {
        var (plan, error) = ComposeSpec.TryParsePanel(PanelJson(json), Array.Empty<string>());
        Assert.True(error is null, error);
        Assert.NotNull(plan);
    }

    /* ─────────────────────────── acceptance flows compile end-to-end ─────────────────────────── */

    [Fact]
    public void FlowA_AvgProcedureElapsed_LikePattern_OnServer_ValidatesAndCompiles()
    {
        /* Acceptance Flow A: "avg procedure elapsed LIKE 'dbo.usp_Payment%' on $server, 24h → line." The panel
           validates against the catalog + declared $server, then compiles to a schema-qualified, fully-bound,
           time-bucketed, server-scoped query. */
        var panel =
            "{\"source\":\"procedure_stats\",\"measure\":\"proc_elapsed_us\",\"aggregate\":\"avg\"," +
            "\"timeBucket\":\"hour\",\"viz\":\"line\"," +
            "\"filters\":[{\"dimension\":\"object_name\",\"op\":\"like\",\"value\":\"dbo.usp_Payment%\"}]}";

        var (plan, error) = ComposeSpec.TryParsePanel(PanelJson(panel), new[] { "srv" });
        Assert.True(error is null, error);

        var sql = Compile(plan!, new[] { "PROD-01" }); /* $server resolved to one server */
        Assert.Contains("collect.procedure_stats", sql, StringComparison.Ordinal);
        Assert.Contains("date_trunc('hour'", sql, StringComparison.Ordinal);
        Assert.Contains("LIKE $", sql, StringComparison.Ordinal);
        Assert.Contains("server_name = ANY($", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("usp_Payment", sql, StringComparison.Ordinal); /* the pattern is bound, not interpolated */
        Assert.DoesNotContain("config.", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void FlowB_TopDatabasesByCpu_AcrossTheFleet_ValidatesAndCompiles()
    {
        /* Acceptance Flow B: "top-10 databases by CPU across the fleet → bar." No server scope = whole fleet
           (no server predicate), grouped by database, ranked, bounded by a LIMIT. */
        var panel =
            "{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\"," +
            "\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"bar\"}";

        var plan = ValidPlan(panel);
        var sql = Compile(plan); /* no server scope => whole fleet */
        Assert.Contains("collect.query_stats", sql, StringComparison.Ordinal);
        Assert.Contains("f.database_name", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY value DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("server_name = ANY", sql, StringComparison.Ordinal); /* fleet-wide, no server filter */
        Assert.DoesNotContain("config.", sql, StringComparison.Ordinal);
    }

    /* ─────────────────────────── B3 full catalog fill ─────────────────────────── */

    [Fact]
    public void B3_Catalog_CoversTheExpectedSources()
    {
        /* The B3 fill: every remaining collector table with composable measures is in the catalog (config
           snapshots + query_store are deliberately excluded — see the source comments). */
        var sources = MeasureCatalog.Measures.Select(m => m.SourceTable).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
        {
            "latch_stats", "spinlock_stats", "cpu_scheduler_stats", "plan_cache_stats", "memory_stats",
            "memory_clerks", "memory_grant_stats", "tempdb_stats", "database_size_stats", "index_object_stats",
            "session_stats", "session_summary_stats", "waiting_tasks", "query_snapshots",
            "blocked_process_reports", "dmv_blocking_snapshots", "deadlocks", "system_health_events",
            "default_trace_events", "running_jobs", "job_history", "perfmon_stats", "query_store_stats",
        })
        {
            Assert.True(sources.Contains(expected), $"catalog is missing a measure for '{expected}'.");
        }
    }

    [Fact]
    public void B3_CountOnlyEventMeasure_CompilesToCountStar()
    {
        var sql = Compile(ValidPlan("{\"source\":\"deadlocks\",\"measure\":\"deadlock_count\",\"aggregate\":\"count\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("collect.deadlocks", sql, StringComparison.Ordinal);
        Assert.Contains("COUNT(*)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("config.", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void B3_ServerGrainGauge_CompilesAndGroupsByServer()
    {
        /* A server-grain gauge (memory) grouped by the universal server dimension = "top servers by memory". */
        var sql = Compile(ValidPlan("{\"source\":\"memory_stats\",\"measure\":\"mem_total_server_mb\",\"aggregate\":\"avg\",\"topN\":5,\"groupBy\":[\"server\"],\"viz\":\"bar\"}"));
        Assert.Contains("collect.memory_stats", sql, StringComparison.Ordinal);
        Assert.Contains("AVG(", sql, StringComparison.Ordinal);
        Assert.Contains("server_name AS server", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void B3_PerEventMeasure_SupportsCountAndPercentile()
    {
        /* blocked_process_reports: COUNT of reports, and a true p95 block duration (per-event rows exist). */
        var count = Compile(ValidPlan("{\"source\":\"blocked_process_reports\",\"measure\":\"bpr_wait_time_ms\",\"aggregate\":\"count\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("COUNT(*)", count, StringComparison.Ordinal);

        var p95 = Compile(ValidPlan("{\"source\":\"blocked_process_reports\",\"measure\":\"bpr_wait_time_ms\",\"aggregate\":\"percentile_cont\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("percentile_cont(0.95)", p95, StringComparison.Ordinal);
    }

    [Fact]
    public void B3_AvgPerExecutionRatio_CompilesToWeightedSumOverSum()
    {
        /* "avg procedure duration per execution" — the execution-WEIGHTED ratio SUM(elapsed)/SUM(execs), NOT an
           avg-of-avgs. Proves the bread-and-butter same-source ratio (design §2c). */
        var sql = Compile(ValidPlan("{\"source\":\"procedure_stats\",\"ratio\":\"proc_avg_elapsed_us\",\"timeBucket\":\"hour\",\"viz\":\"line\"}"));
        Assert.Contains("collect.procedure_stats", sql, StringComparison.Ordinal);
        Assert.Contains("NULLIF(SUM(", sql, StringComparison.Ordinal);
        Assert.Contains("delta_elapsed_time", sql, StringComparison.Ordinal);
        Assert.Contains("delta_execution_count", sql, StringComparison.Ordinal);
    }

    /* ─────────────────────────── D1: gauge-operand (Avg) ratios (#1563 follow-ups) ─────────────────────────── */

    [Fact]
    public void AvgRatio_Operands_AreBothGaugesOnTheSameSource()
    {
        var avgRatios = MeasureCatalog.Measures
            .Where(m => m.Kind == MeasureKind.Ratio && m.RatioMode == MeasureRatioMode.Avg).ToList();
        Assert.NotEmpty(avgRatios);
        foreach (var ratio in avgRatios)
        {
            var numerator = MeasureCatalog.Measure(ratio.NumeratorKey)!;
            var denominator = MeasureCatalog.Measure(ratio.DenominatorKey)!;
            /* an Avg ratio divides two point-in-time gauges (their AggregationColumn is null — never summable). */
            Assert.Equal(MeasureArchetype.Gauge, numerator.Archetype);
            Assert.Equal(MeasureArchetype.Gauge, denominator.Archetype);
            Assert.Equal(ratio.SourceTable, numerator.SourceTable);
            Assert.Equal(ratio.SourceTable, denominator.SourceTable);
        }
    }

    [Fact]
    public void SumRatio_Operands_HaveANonNullAggregationColumn()
    {
        var sumRatios = MeasureCatalog.Measures
            .Where(m => m.Kind == MeasureKind.Ratio && m.RatioMode == MeasureRatioMode.Sum).ToList();
        Assert.NotEmpty(sumRatios);
        foreach (var ratio in sumRatios)
        {
            var numerator = MeasureCatalog.Measure(ratio.NumeratorKey)!;
            var denominator = MeasureCatalog.Measure(ratio.DenominatorKey)!;
            /* a Sum ratio sums a cumulative/delta column — both operands must have a summable column. */
            Assert.NotNull(numerator.AggregationColumn);
            Assert.NotNull(denominator.AggregationColumn);
        }
    }

    [Theory]
    [InlineData("{\"source\":\"memory_grant_stats\",\"ratio\":\"grant_used_pct\",\"timeBucket\":\"hour\",\"viz\":\"line\"}", "used_memory_mb", "granted_memory_mb")]
    [InlineData("{\"source\":\"plan_cache_stats\",\"ratio\":\"single_use_plan_pct\",\"timeBucket\":\"hour\",\"viz\":\"line\"}", "single_use_plans", "total_plans")]
    public void Compile_GaugeRatio_IsAvgOverNullifAvg_ScaledToPercent(string json, string numeratorColumn, string denominatorColumn)
    {
        var sql = Compile(ValidPlan(json));
        Assert.Contains($"AVG(f.{numeratorColumn})", sql, StringComparison.Ordinal);
        Assert.Contains($"NULLIF(AVG(f.{denominatorColumn}), 0)", sql, StringComparison.Ordinal);
        /* fraction family: native 'ratio' displayed as 'percent' => × 100. */
        Assert.Contains("* 100.0", sql, StringComparison.Ordinal);
        /* a gauge ratio never uses the Sum form (gauges are never summable). */
        Assert.DoesNotContain("SUM(", sql, StringComparison.Ordinal);
    }

    /* ─────────────── L1: Query Store execution-weighted average ratios (Weighted mode) ─────────────── */

    [Theory]
    [InlineData("qs_avg_duration_us", "avg_duration_us")]
    [InlineData("qs_avg_cpu_us", "avg_cpu_time_us")]
    public void Compile_WeightedRatio_IsProductSumOverNullifSumOfWeight(string ratioKey, string valueColumn)
    {
        /* The execution-weighted average of a pre-aggregated per-interval average:
           SUM(value * execution_count) / NULLIF(SUM(execution_count), 0) — not an avg-of-avgs (design §2). */
        var sql = Compile(ValidPlan($"{{\"source\":\"query_store_stats\",\"ratio\":\"{ratioKey}\",\"timeBucket\":\"hour\",\"viz\":\"line\"}}"));
        Assert.Contains($"SUM(f.{valueColumn} * f.execution_count)", sql, StringComparison.Ordinal);
        Assert.Contains("NULLIF(SUM(f.execution_count), 0)", sql, StringComparison.Ordinal);
        Assert.Contains("collect.query_store_stats", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("config.", sql, StringComparison.Ordinal);
        /* native µs displayed as the default ms — the same /1000 family conversion the other duration ratios use. */
        Assert.Contains("/ 1000.0", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void WeightedQsRatios_ValidateAsRatios_AndRejectMeasureReference()
    {
        foreach (var key in new[] { "qs_avg_duration_us", "qs_avg_cpu_us" })
        {
            var plan = ValidPlan($"{{\"source\":\"query_store_stats\",\"ratio\":\"{key}\",\"timeBucket\":\"hour\",\"viz\":\"line\"}}");
            Assert.Equal(MeasureKind.Ratio, plan.Measure.Kind);
            Assert.Equal(MeasureRatioMode.Weighted, plan.Measure.RatioMode);
            Assert.Equal("ms", plan.Unit);

            /* A ratio referenced via 'measure' (with an aggregate) is rejected — it must be referenced as 'ratio'. */
            var reason = RejectReason($"{{\"source\":\"query_store_stats\",\"measure\":\"{key}\",\"aggregate\":\"sum\",\"viz\":\"table\"}}");
            Assert.Contains("reference it as 'ratio'", reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    /* ─────────────────────────── D3: per-panel thresholds (render-only) ─────────────────────────── */

    [Fact]
    public void TryParsePanel_AcceptsThresholds_AndCarriesThemOnThePlan()
    {
        var plan = ValidPlan("{\"source\":\"cpu_utilization_stats\",\"measure\":\"sqlserver_cpu_utilization\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"thresholds\":[80,90]}");
        Assert.Equal(new[] { 80.0, 90.0 }, plan.Thresholds);
    }

    [Fact]
    public void TryParsePanel_DefaultsThresholdsToEmpty_WhenAbsent()
    {
        var plan = ValidPlan("{\"source\":\"cpu_utilization_stats\",\"measure\":\"sqlserver_cpu_utilization\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\"}");
        Assert.Empty(plan.Thresholds);
    }

    [Theory]
    [InlineData("{\"source\":\"cpu_utilization_stats\",\"measure\":\"sqlserver_cpu_utilization\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"thresholds\":[1,2,3,4,5]}", "maximum")]
    [InlineData("{\"source\":\"cpu_utilization_stats\",\"measure\":\"sqlserver_cpu_utilization\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"thresholds\":[\"abc\"]}", "finite number")]
    [InlineData("{\"source\":\"cpu_utilization_stats\",\"measure\":\"sqlserver_cpu_utilization\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"thresholds\":42}", "must be an array")]
    public void TryParsePanel_RejectsBadThresholds(string json, string expectedFragment)
    {
        Assert.Contains(expectedFragment, RejectReason(json), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParsePanel_RejectsNonFiniteThreshold()
    {
        /* NaN/Infinity can't appear in JSON text, so build the node directly to prove the finite guard. */
        var panel = PanelJson("{\"source\":\"cpu_utilization_stats\",\"measure\":\"sqlserver_cpu_utilization\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\"}");
        panel["thresholds"] = new JsonArray(JsonValue.Create(80.0), JsonValue.Create(double.NaN));
        var (plan, error) = ComposeSpec.TryParsePanel(panel, Array.Empty<string>());
        Assert.Null(plan);
        Assert.Contains("finite number", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_NeverEmitsThresholds_TheyAreRenderOnly()
    {
        var sql = Compile(ValidPlan("{\"source\":\"cpu_utilization_stats\",\"measure\":\"sqlserver_cpu_utilization\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"thresholds\":[80,90]}"));
        Assert.DoesNotContain("80", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("90", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateDefinition_AcceptsAComposedPanelWithThresholds()
    {
        var ok = DarlingWebEndpoints.ValidateDefinition(
            "{\"panels\":[{\"source\":\"cpu_utilization_stats\",\"measure\":\"sqlserver_cpu_utilization\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"thresholds\":[80,90]}]}");
        Assert.True(ok.IsValid, ok.Error);
    }

    /* ─────────────────────────── D4: per-measure availability (appliesTo) ─────────────────────────── */

    [Fact]
    public void Catalog_Measures_CarryAppliesTo_ReflectingTheCollectorGate()
    {
        var compose = Assert.IsType<JsonObject>(DarlingWebEndpoints.BuildCatalogNode()["compose"]);
        var measures = Assert.IsType<JsonArray>(compose["measures"]);

        JsonObject AppliesToFor(string measureKey)
        {
            var measure = measures.Cast<JsonObject>().Single(m => m!["key"]!.GetValue<string>() == measureKey);
            return Assert.IsType<JsonObject>(measure["appliesTo"]);
        }

        /* An all-targets collector (wait_stats) is universally available and needs no msdb. */
        var wait = AppliesToFor("wait_time_ms");
        Assert.True(wait["onPrem"]!.GetValue<bool>());
        Assert.True(wait["azureSqlDb"]!.GetValue<bool>());
        Assert.True(wait["azureMi"]!.GetValue<bool>());
        Assert.True(wait["awsRds"]!.GetValue<bool>());
        Assert.False(wait["needsMsdb"]!.GetValue<bool>());

        /* A SQL-Agent collector (running_jobs) needs msdb and is gated off Azure SQL DB + AWS RDS. */
        var job = AppliesToFor("runningjob_current_duration_s");
        Assert.True(job["onPrem"]!.GetValue<bool>());
        Assert.False(job["azureSqlDb"]!.GetValue<bool>());
        Assert.True(job["azureMi"]!.GetValue<bool>());
        Assert.False(job["awsRds"]!.GetValue<bool>());
        Assert.True(job["needsMsdb"]!.GetValue<bool>());
    }

    /* ─────────────────────────── D5: event annotations (catalog pins) ─────────────────────────── */

    [Fact]
    public void EveryAnnotationSource_IsARealCollectorTable()
    {
        var tables = CollectorCatalog.All.Select(c => c.TargetTable).ToHashSet(StringComparer.Ordinal);
        foreach (var source in MeasureCatalog.AnnotationSources)
        {
            Assert.True(tables.Contains(source.SourceTable),
                $"annotation source '{source.Key}' names table '{source.SourceTable}', which is not a collector table.");
        }
    }

    [Fact]
    public void EveryAnnotationSourceColumns_AreRealPayloadColumns()
    {
        /* Same drift-proofing as the measure column pins: an annotation's event-time + label columns must be
           real payload columns of its collector, so the compiler emits only real, schema-qualified identifiers. */
        var payload = PayloadColumnsByTable();
        foreach (var source in MeasureCatalog.AnnotationSources)
        {
            var columns = payload[source.SourceTable];
            Assert.True(columns.Contains(source.TimeColumn),
                $"annotation source '{source.Key}' time column '{source.TimeColumn}' is not a payload column of '{source.SourceTable}'.");
            Assert.True(columns.Contains(source.LabelColumn),
                $"annotation source '{source.Key}' label column '{source.LabelColumn}' is not a payload column of '{source.SourceTable}'.");
        }
    }

    [Fact]
    public void NoAnnotationSource_IsAConfigTable()
    {
        /* The same structural config-exclusion guarantee the measures carry: an annotation query can only ever
           name a collect collector table, never a config control-plane table. */
        foreach (var source in MeasureCatalog.AnnotationSources)
        {
            Assert.False(source.SourceTable.StartsWith("config", StringComparison.Ordinal),
                $"annotation source '{source.Key}' resolves to a config table '{source.SourceTable}'.");
        }
    }

    /* ─────────────────────────── D5: the spec validator ─────────────────────────── */

    [Fact]
    public void TryParsePanel_AcceptsAnnotations_OnATimeSeries_AndCarriesThemOnThePlan()
    {
        var plan = ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"annotations\":[\"deadlocks\",\"blocked_process_reports\"]}");
        Assert.Equal(new[] { "deadlocks", "blocked_process_reports" }, plan.Annotations.Select(a => a.Key));
    }

    [Fact]
    public void TryParsePanel_DefaultsAnnotationsToEmpty_WhenAbsent()
    {
        var plan = ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}");
        Assert.Empty(plan.Annotations);
    }

    [Theory]
    /* an unknown annotation-source key. */
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"annotations\":[\"nope\"]}", "unknown source")]
    /* annotations on a ranked panel — a marker overlay needs a time axis. */
    [InlineData("{\"source\":\"query_stats\",\"measure\":\"query_worker_us\",\"aggregate\":\"sum\",\"topN\":10,\"groupBy\":[\"database_name\"],\"viz\":\"bar\",\"annotations\":[\"deadlocks\"]}", "time-series")]
    /* annotations on a scalar panel — likewise no time axis. */
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"stat\",\"annotations\":[\"deadlocks\"]}", "time-series")]
    /* the same source listed twice. */
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"annotations\":[\"deadlocks\",\"deadlocks\"]}", "more than once")]
    /* not an array. */
    [InlineData("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"annotations\":\"deadlocks\"}", "must be an array")]
    public void TryParsePanel_RejectsBadAnnotations_NamingTheReason(string json, string expectedFragment)
    {
        Assert.Contains(expectedFragment, RejectReason(json), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParsePanel_RejectsTooManyAnnotations()
    {
        /* All five distinct sources exceeds the cap of four. */
        var reason = RejectReason("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"," +
            "\"annotations\":[\"deadlocks\",\"blocked_process_reports\",\"long_query_completions\",\"default_trace_events\",\"system_health_events\"]}");
        Assert.Contains("maximum", reason, StringComparison.OrdinalIgnoreCase);
    }

    /* ─────────────────────────── D5: the annotation compiler (safety invariants) ─────────────────────────── */

    private static IReadOnlyList<(string Source, ComposeCompiled Compiled)> CompileAnnotations(
        PanelPlan plan, IReadOnlyList<string>? servers = null) =>
        ComposeCompiler.CompileAnnotations(plan, new ComposeRunContext(servers, WindowStart, WindowEnd, ComposeRunContext.NoVariables));

    [Fact]
    public void CompileAnnotations_ReturnsEmpty_WhenNoneRequested()
    {
        var plan = ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}");
        Assert.Empty(CompileAnnotations(plan));
    }

    [Fact]
    public void CompileAnnotations_EmitsSchemaQualifiedCollect_WindowAndServerBound_Capped()
    {
        var plan = ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"annotations\":[\"deadlocks\"]}");
        var (source, one) = Assert.Single(CompileAnnotations(plan, new[] { "PROD-01" }));
        Assert.Equal("deadlocks", source);

        var sql = one.Sql;
        Assert.Contains("collect.deadlocks", sql, StringComparison.Ordinal);
        Assert.Contains("f.deadlock_time AS ts", sql, StringComparison.Ordinal);
        Assert.Contains("f.database_name AS label", sql, StringComparison.Ordinal);
        /* window + server scope are the SAME bound parameters the measure query uses ($1/$2 window, $3 server[]). */
        Assert.Contains("f.deadlock_time >= $1", sql, StringComparison.Ordinal);
        Assert.Contains("f.deadlock_time <= $2", sql, StringComparison.Ordinal);
        Assert.Contains("server_name = ANY($3)", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY ts", sql, StringComparison.Ordinal);
        Assert.Contains($"LIMIT {ComposeLimits.MaxAnnotationEvents}", sql, StringComparison.Ordinal);
        /* catalog-only: never a config table, and the server name is bound, never interpolated. */
        Assert.DoesNotContain("config.", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("PROD-01", sql, StringComparison.Ordinal);
        Assert.Equal(3, one.Parameters.Count); /* every value bound: $1 start, $2 end, $3 server[] */
    }

    [Fact]
    public void CompileAnnotations_HasNoServerPredicate_ForTheWholeFleet()
    {
        var plan = ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"annotations\":[\"deadlocks\"]}");
        var (_, one) = Assert.Single(CompileAnnotations(plan));
        Assert.DoesNotContain("server_name = ANY", one.Sql, StringComparison.Ordinal);
        Assert.Equal(2, one.Parameters.Count); /* $1 start, $2 end — no server predicate on the fleet */
    }

    [Fact]
    public void CompileAnnotations_CompilesOneQueryPerSource_InOrder()
    {
        var plan = ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"annotations\":[\"deadlocks\",\"system_health_events\"]}");
        Assert.Equal(new[] { "deadlocks", "system_health_events" }, CompileAnnotations(plan).Select(c => c.Source));
    }

    [Theory]
    [InlineData("deadlocks", "collect.deadlocks", "f.deadlock_time AS ts", "f.database_name AS label")]
    [InlineData("blocked_process_reports", "collect.blocked_process_reports", "f.event_time AS ts", "f.contentious_object AS label")]
    [InlineData("long_query_completions", "collect.long_query_completions", "f.event_time AS ts", "f.object_name AS label")]
    [InlineData("default_trace_events", "collect.default_trace_events", "f.event_time AS ts", "f.event_name AS label")]
    [InlineData("system_health_events", "collect.system_health_events", "f.event_time AS ts", "f.event_type AS label")]
    public void CompileAnnotations_EachSource_SelectsItsCatalogTimeAndLabelColumns(string key, string table, string tsExpr, string labelExpr)
    {
        var plan = ValidPlan("{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"annotations\":[\"" + key + "\"]}");
        var (_, one) = Assert.Single(CompileAnnotations(plan));
        Assert.Contains(table, one.Sql, StringComparison.Ordinal);
        Assert.Contains(tsExpr, one.Sql, StringComparison.Ordinal);
        Assert.Contains(labelExpr, one.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("config.", one.Sql, StringComparison.Ordinal);
    }

    /* ─────────────────────────── D5: /api/catalog + ValidateDefinition ─────────────────────────── */

    [Fact]
    public void Catalog_CarriesAnnotationSources()
    {
        var compose = Assert.IsType<JsonObject>(DarlingWebEndpoints.BuildCatalogNode()["compose"]);
        var sources = Assert.IsType<JsonArray>(compose["annotationSources"]);
        Assert.Equal(MeasureCatalog.AnnotationSources.Count, sources.Count);

        var deadlocks = sources.Cast<JsonObject>().Single(s => s!["key"]!.GetValue<string>() == "deadlocks");
        Assert.False(string.IsNullOrEmpty(deadlocks["displayName"]!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(deadlocks["category"]!.GetValue<string>()));
        /* appliesTo parity with measures (D4) so the composer can grey a source a target can't collect. */
        Assert.NotNull(Assert.IsType<JsonObject>(deadlocks["appliesTo"])["onPrem"]);
    }

    [Fact]
    public void ValidateDefinition_AcceptsAComposedPanelWithAnnotations()
    {
        var ok = DarlingWebEndpoints.ValidateDefinition(
            "{\"panels\":[{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"annotations\":[\"deadlocks\",\"system_health_events\"]}]}");
        Assert.True(ok.IsValid, ok.Error);
    }

    [Fact]
    public void ValidateDefinition_RejectsAComposedPanelWithABadAnnotation()
    {
        var result = DarlingWebEndpoints.ValidateDefinition(
            "{\"panels\":[{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\",\"annotations\":[\"nope\"]}]}");
        Assert.False(result.IsValid);
        Assert.Contains("unknown source", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    /* ─────────────────────────── D7: notebook mode (kind dispatch) ─────────────────────────── */

    [Fact]
    public void ValidateDefinition_AcceptsANotebook_WithMarkdownAndPanelCells()
    {
        var ok = DarlingWebEndpoints.ValidateDefinition(
            "{\"kind\":\"notebook\",\"cells\":[" +
            "{\"type\":\"markdown\",\"text\":\"# Wait analysis\\nSome prose about the panel below.\"}," +
            "{\"type\":\"panel\",\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}]}");
        Assert.True(ok.IsValid, ok.Error);
    }

    [Fact]
    public void ValidateDefinition_AcceptsAMarkdownOnlyNotebook()
    {
        var ok = DarlingWebEndpoints.ValidateDefinition(
            "{\"kind\":\"notebook\",\"cells\":[{\"type\":\"markdown\",\"text\":\"just prose, no panels\"}]}");
        Assert.True(ok.IsValid, ok.Error);
    }

    [Fact]
    public void ValidateDefinition_AcceptsANotebook_WithVariablesRange_AndAVariableFilterPanelCell()
    {
        /* A notebook's view-level variables + range validate through the SAME authorities a dashboard uses, and a
           panel cell's $var filter resolves against those declared variables. */
        var ok = DarlingWebEndpoints.ValidateDefinition(
            "{\"kind\":\"notebook\"," +
            "\"variables\":[{\"name\":\"db\",\"dimension\":\"database_name\"}]," +
            "\"range\":{\"hours\":24}," +
            "\"cells\":[" +
            "{\"type\":\"markdown\",\"text\":\"scoped to $db\"}," +
            "{\"type\":\"panel\",\"source\":\"procedure_stats\",\"measure\":\"proc_elapsed_us\",\"aggregate\":\"avg\",\"timeBucket\":\"hour\",\"viz\":\"line\"," +
            "\"filters\":[{\"dimension\":\"database_name\",\"op\":\"eq\",\"value\":\"$db\"}]}]}");
        Assert.True(ok.IsValid, ok.Error);
    }

    [Theory]
    /* cells missing entirely / not an array. */
    [InlineData("{\"kind\":\"notebook\"}", "must be an array")]
    [InlineData("{\"kind\":\"notebook\",\"cells\":\"nope\"}", "must be an array")]
    /* an empty cells array. */
    [InlineData("{\"kind\":\"notebook\",\"cells\":[]}", "at least one")]
    /* a cell that is not an object. */
    [InlineData("{\"kind\":\"notebook\",\"cells\":[\"nope\"]}", "must be an object")]
    /* an unknown cell type. */
    [InlineData("{\"kind\":\"notebook\",\"cells\":[{\"type\":\"chart\",\"text\":\"x\"}]}", "unknown type")]
    /* a cell with no type at all. */
    [InlineData("{\"kind\":\"notebook\",\"cells\":[{\"text\":\"x\"}]}", "unknown type")]
    /* a markdown cell missing 'text'. */
    [InlineData("{\"kind\":\"notebook\",\"cells\":[{\"type\":\"markdown\"}]}", "string 'text'")]
    /* a markdown cell whose 'text' is present but not a string. */
    [InlineData("{\"kind\":\"notebook\",\"cells\":[{\"type\":\"markdown\",\"text\":42}]}", "string 'text'")]
    public void ValidateDefinition_RejectsBadNotebook_NamingTheReason(string json, string expectedFragment)
    {
        var result = DarlingWebEndpoints.ValidateDefinition(json);
        Assert.False(result.IsValid);
        Assert.Contains(expectedFragment, result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDefinition_RejectsANotebook_WithAnInvalidPanelCell_NamingTheCell()
    {
        /* A panel cell routes through the EXACT ComposeSpec.TryParsePanel authority a dashboard's composed panel
           uses, so an off-catalog measure is caught and the offending cell is named by index. */
        var result = DarlingWebEndpoints.ValidateDefinition(
            "{\"kind\":\"notebook\",\"cells\":[" +
            "{\"type\":\"markdown\",\"text\":\"intro\"}," +
            "{\"type\":\"panel\",\"source\":\"wait_stats\",\"measure\":\"nope\",\"aggregate\":\"sum\",\"viz\":\"table\"}]}");
        Assert.False(result.IsValid);
        Assert.Contains("cell 1", result.Error!, StringComparison.Ordinal);
        Assert.Contains("unknown measure", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDefinition_RejectsANotebookPanelCell_WithAnUndeclaredVariable()
    {
        /* The panel cell's $var must reference a variable the notebook declared — the same allowlist a dashboard
           composed panel enforces. */
        var result = DarlingWebEndpoints.ValidateDefinition(
            "{\"kind\":\"notebook\",\"cells\":[" +
            "{\"type\":\"panel\",\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"viz\":\"table\"," +
            "\"filters\":[{\"dimension\":\"wait_type\",\"op\":\"eq\",\"value\":\"$nope\"}]}]}");
        Assert.False(result.IsValid);
        Assert.Contains("undeclared variable", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDefinition_RejectsANotebook_WithBadViewVariables()
    {
        var result = DarlingWebEndpoints.ValidateDefinition(
            "{\"kind\":\"notebook\",\"variables\":[{\"name\":\"x\",\"dimension\":\"nope\"}]," +
            "\"cells\":[{\"type\":\"markdown\",\"text\":\"x\"}]}");
        Assert.False(result.IsValid);
        Assert.Contains("dimension", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDefinition_RejectsANotebook_WithBadRange()
    {
        var result = DarlingWebEndpoints.ValidateDefinition(
            "{\"kind\":\"notebook\",\"range\":{\"hours\":0}," +
            "\"cells\":[{\"type\":\"markdown\",\"text\":\"x\"}]}");
        Assert.False(result.IsValid);
        Assert.Contains("range.hours", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDefinition_RejectsANotebook_WithOversizeMarkdownText()
    {
        /* One markdown cell over the per-cell byte cap, while the whole doc stays under the 128 KB definition cap
           — so this pins the per-cell bound specifically, not the doc bound. */
        var bigText = new string('a', DarlingWebEndpoints.MaxMarkdownCellBytes + 1);
        var notebook = new JsonObject
        {
            ["kind"] = "notebook",
            ["cells"] = new JsonArray(new JsonObject { ["type"] = "markdown", ["text"] = bigText }),
        };
        var result = DarlingWebEndpoints.ValidateDefinition(notebook.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains("maximum size", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDefinition_RejectsANotebook_WithTooManyCells()
    {
        var cells = new JsonArray();
        for (var i = 0; i < DarlingWebEndpoints.MaxNotebookCells + 1; i++)
        {
            cells.Add(new JsonObject { ["type"] = "markdown", ["text"] = "x" });
        }

        var notebook = new JsonObject { ["kind"] = "notebook", ["cells"] = cells };
        var result = DarlingWebEndpoints.ValidateDefinition(notebook.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains("maximum", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDefinition_StillAcceptsADashboard_AfterTheKindDispatch()
    {
        /* Regression: the D7 kind dispatch leaves the dashboard path untouched — a plain {panels} dashboard (no
           kind) and an explicit "kind":"dashboard" both validate through the same unchanged panels path. */
        var noKind = DarlingWebEndpoints.ValidateDefinition(
            "{\"panels\":[{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}]}");
        Assert.True(noKind.IsValid, noKind.Error);

        var explicitDashboard = DarlingWebEndpoints.ValidateDefinition(
            "{\"kind\":\"dashboard\",\"panels\":[{\"source\":\"wait_stats\",\"measure\":\"wait_time_ms\",\"aggregate\":\"sum\",\"timeBucket\":\"hour\",\"viz\":\"line\"}]}");
        Assert.True(explicitDashboard.IsValid, explicitDashboard.Error);
    }

    [Fact]
    public void ValidateDefinition_RejectsACellsDoc_SentWithoutTheNotebookKind()
    {
        /* Without "kind":"notebook", a cells-only doc is just a dashboard missing its panels — rejected, so a
           notebook can never be silently mistaken for (or stored as) an empty dashboard. */
        var result = DarlingWebEndpoints.ValidateDefinition(
            "{\"cells\":[{\"type\":\"markdown\",\"text\":\"x\"}]}");
        Assert.False(result.IsValid);
        Assert.Contains("panels", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    /* ─────────────────────────── D7: list-summary kind (badge/route) ─────────────────────────── */

    [Theory]
    [InlineData("notebook", "notebook")]
    [InlineData("dashboard", "dashboard")]
    /* absent kind (a legacy/plain dashboard) defaults to a concrete "dashboard" on the wire. */
    [InlineData(null, "dashboard")]
    public void BuildSummariesNode_EmitsAConcreteKind_DefaultingDashboard(string? storedKind, string expectedWireKind)
    {
        var summary = new CustomViewSummary(
            Id: 7, Name: "v", Description: null, Version: 1, UpdatedAt: DateTime.UnixEpoch, UpdatedBy: "web", Kind: storedKind);

        var node = DarlingWebEndpoints.BuildSummariesNode(new[] { summary });

        Assert.Equal(1, node.Count);
        var one = Assert.IsType<JsonObject>(node[0]);
        Assert.Equal(expectedWireKind, one["kind"]!.GetValue<string>());
    }

    /* ─────────────────────────── D7: seed-template drift guard ─────────────────────────── */

    [Fact]
    public void ValidateDefinition_AcceptsEverySeedNotebookTemplate()
    {
        /* Drift guard for the frontend's NOTEBOOK_TEMPLATES (wwwroot/js/notebook.js): each template's PANEL cells
           are mirrored here verbatim (the markdown prose is abbreviated — ValidateDefinition only length-checks a
           cell's text), so if a future MeasureCatalog change renames/removes a measure, dimension, or annotation
           source a seed template relies on, THIS fails loudly instead of the template silently 400ing in the UI.
           If a template's panels are added to or restructured, mirror the change here. */
        var templates = new (string Name, string Definition)[]
        {
            ("blocking-rca",
                "{\"kind\":\"notebook\",\"cells\":[" +
                "{\"type\":\"markdown\",\"text\":\"# Blocking-chain RCA\"}," +
                "{\"type\":\"markdown\",\"text\":\"## 1. Blocking over time\"}," +
                "{\"type\":\"panel\",\"source\":\"blocked_process_reports\",\"viz\":\"line\",\"timeBucket\":\"hour\",\"title\":\"Blocked-process reports over time\",\"measure\":\"bpr_wait_time_ms\",\"aggregate\":\"count\",\"unit\":\"count\",\"annotations\":[\"deadlocks\"]}," +
                "{\"type\":\"markdown\",\"text\":\"## 2. Most-contended objects\"}," +
                "{\"type\":\"panel\",\"source\":\"blocked_process_reports\",\"viz\":\"bar\",\"topN\":10,\"title\":\"Most-blocked objects\",\"groupBy\":[\"contentious_object\"],\"measure\":\"bpr_wait_time_ms\",\"aggregate\":\"count\",\"unit\":\"count\"}," +
                "{\"type\":\"markdown\",\"text\":\"## 3. Blocking by database\"}," +
                "{\"type\":\"panel\",\"source\":\"blocked_process_reports\",\"viz\":\"bar\",\"topN\":10,\"title\":\"Blocking by database\",\"groupBy\":[\"database_name\"],\"measure\":\"bpr_wait_time_ms\",\"aggregate\":\"count\",\"unit\":\"count\"}," +
                "{\"type\":\"markdown\",\"text\":\"## 4. Lock modes involved\"}," +
                "{\"type\":\"panel\",\"source\":\"blocked_process_reports\",\"viz\":\"pie\",\"topN\":8,\"title\":\"Lock modes\",\"groupBy\":[\"lock_mode\"],\"measure\":\"bpr_wait_time_ms\",\"aggregate\":\"count\",\"unit\":\"count\"}," +
                "{\"type\":\"markdown\",\"text\":\"Next steps\"}]}"),

            ("deadlock-postmortem",
                "{\"kind\":\"notebook\",\"cells\":[" +
                "{\"type\":\"markdown\",\"text\":\"# Deadlock post-mortem\"}," +
                "{\"type\":\"markdown\",\"text\":\"## Deadlocks over time\"}," +
                "{\"type\":\"panel\",\"source\":\"deadlocks\",\"viz\":\"line\",\"timeBucket\":\"hour\",\"title\":\"Deadlocks over time\",\"measure\":\"deadlock_count\",\"aggregate\":\"count\",\"unit\":\"count\",\"annotations\":[\"blocked_process_reports\"]}," +
                "{\"type\":\"markdown\",\"text\":\"## Deadlocks by database\"}," +
                "{\"type\":\"panel\",\"source\":\"deadlocks\",\"viz\":\"bar\",\"topN\":10,\"title\":\"Deadlocks by database\",\"groupBy\":[\"database_name\"],\"measure\":\"deadlock_count\",\"aggregate\":\"count\",\"unit\":\"count\"}," +
                "{\"type\":\"markdown\",\"text\":\"## Correlated blocking\"}," +
                "{\"type\":\"panel\",\"source\":\"blocked_process_reports\",\"viz\":\"line\",\"timeBucket\":\"hour\",\"title\":\"Blocked-process reports (deadlock markers)\",\"measure\":\"bpr_wait_time_ms\",\"aggregate\":\"count\",\"unit\":\"count\",\"annotations\":[\"deadlocks\"]}," +
                "{\"type\":\"markdown\",\"text\":\"Reading the deadlock graph\"}]}"),

            ("what-changed",
                "{\"kind\":\"notebook\",\"cells\":[" +
                "{\"type\":\"markdown\",\"text\":\"# What changed at 2am?\"}," +
                "{\"type\":\"markdown\",\"text\":\"## Default-trace events over time\"}," +
                "{\"type\":\"panel\",\"source\":\"default_trace_events\",\"viz\":\"line\",\"timeBucket\":\"hour\",\"title\":\"Default-trace events over time\",\"measure\":\"deftrace_duration_us\",\"aggregate\":\"count\",\"unit\":\"count\",\"annotations\":[\"system_health_events\",\"deadlocks\"]}," +
                "{\"type\":\"markdown\",\"text\":\"## Events by type\"}," +
                "{\"type\":\"panel\",\"source\":\"default_trace_events\",\"viz\":\"bar\",\"topN\":10,\"title\":\"Default-trace events by type\",\"groupBy\":[\"event_name\"],\"measure\":\"deftrace_duration_us\",\"aggregate\":\"count\",\"unit\":\"count\"}," +
                "{\"type\":\"markdown\",\"text\":\"## Events by database\"}," +
                "{\"type\":\"panel\",\"source\":\"default_trace_events\",\"viz\":\"bar\",\"topN\":10,\"title\":\"Default-trace events by database\",\"groupBy\":[\"database_name\"],\"measure\":\"deftrace_duration_us\",\"aggregate\":\"count\",\"unit\":\"count\"}," +
                "{\"type\":\"markdown\",\"text\":\"## Correlate with CPU\"}," +
                "{\"type\":\"panel\",\"source\":\"cpu_utilization_stats\",\"viz\":\"line\",\"timeBucket\":\"hour\",\"title\":\"SQL Server CPU % (event markers)\",\"measure\":\"sqlserver_cpu_utilization\",\"aggregate\":\"avg\",\"unit\":\"percent\",\"annotations\":[\"default_trace_events\",\"system_health_events\"]}," +
                "{\"type\":\"markdown\",\"text\":\"Where to look next\"}]}"),

            ("memory-oom",
                "{\"kind\":\"notebook\",\"cells\":[" +
                "{\"type\":\"markdown\",\"text\":\"# Memory / OOM walkthrough\"}," +
                "{\"type\":\"markdown\",\"text\":\"## Server memory over time\"}," +
                "{\"type\":\"panel\",\"source\":\"memory_stats\",\"viz\":\"line\",\"timeBucket\":\"hour\",\"title\":\"Total server memory\",\"measure\":\"mem_total_server_mb\",\"aggregate\":\"avg\",\"unit\":\"mb\"}," +
                "{\"type\":\"markdown\",\"text\":\"## Query-memory grants\"}," +
                "{\"type\":\"panel\",\"source\":\"memory_grant_stats\",\"viz\":\"line\",\"timeBucket\":\"hour\",\"title\":\"Granted query memory\",\"measure\":\"grant_granted_mb\",\"aggregate\":\"avg\",\"unit\":\"mb\"}," +
                "{\"type\":\"panel\",\"source\":\"memory_grant_stats\",\"viz\":\"line\",\"timeBucket\":\"hour\",\"title\":\"Query-memory grant used %\",\"ratio\":\"grant_used_pct\",\"unit\":\"percent\",\"thresholds\":[90]}," +
                "{\"type\":\"markdown\",\"text\":\"## Grant pressure\"}," +
                "{\"type\":\"panel\",\"source\":\"memory_grant_stats\",\"viz\":\"line\",\"timeBucket\":\"hour\",\"title\":\"Memory-grant waiters (peak)\",\"measure\":\"grant_waiters\",\"aggregate\":\"max\",\"unit\":\"count\"}," +
                "{\"type\":\"panel\",\"source\":\"memory_grant_stats\",\"viz\":\"line\",\"timeBucket\":\"hour\",\"title\":\"Memory-grant timeouts\",\"measure\":\"grant_timeouts\",\"aggregate\":\"sum\",\"unit\":\"count\"}," +
                "{\"type\":\"markdown\",\"text\":\"## Top memory clerks\"}," +
                "{\"type\":\"panel\",\"source\":\"memory_clerks\",\"viz\":\"bar\",\"topN\":10,\"title\":\"Top memory clerks\",\"groupBy\":[\"clerk_type\"],\"measure\":\"clerk_memory_mb\",\"aggregate\":\"max\",\"unit\":\"mb\"}," +
                "{\"type\":\"markdown\",\"text\":\"RESOURCE_SEMAPHORE notes\"}]}"),
        };

        foreach (var (name, definition) in templates)
        {
            var result = DarlingWebEndpoints.ValidateDefinition(definition);
            Assert.True(result.IsValid, $"seed template '{name}' failed validation: {result.Error}");
        }
    }
}
