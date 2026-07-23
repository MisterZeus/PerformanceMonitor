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
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins <see cref="ComposeCaggValueMapper"/> — the raw-delta → CAGG-column rewrite, the single place a wrong column
/// would mean WRONG DATA on a panel (not just a truncation), so every emission is asserted exactly. Pure string
/// production, no DB — ungated.
/// </summary>
public sealed class ComposeCaggValueMapperTests
{
    private static ComposeMeasure Measure(string key) =>
        MeasureCatalog.Measures.First(m => string.Equals(m.Key, key, StringComparison.Ordinal));

    private static PanelPlan Plan(string measureKey, ComposeAggregate aggregate = ComposeAggregate.Sum) =>
        new()
        {
            Measure = Measure(measureKey),
            Aggregate = aggregate,
            Unit = "ms",
            Filters = Array.Empty<ComposeFilter>(),
            GroupBy = Array.Empty<ComposeDimension>(),
            Viz = "line",
        };

    /* ---------------- CanRemap gate ---------------- */

    [Theory]
    [InlineData("query_worker_us", ComposeAggregate.Sum, true)]
    [InlineData("query_worker_us", ComposeAggregate.Avg, true)]
    [InlineData("query_worker_us", ComposeAggregate.Min, true)]
    [InlineData("query_worker_us", ComposeAggregate.Max, true)]
    [InlineData("proc_executions", ComposeAggregate.Sum, true)]
    [InlineData("query_avg_elapsed_us", ComposeAggregate.Sum, true)]  /* Sum-ratio */
    [InlineData("proc_avg_cpu_us", ComposeAggregate.Sum, true)]       /* Sum-ratio */
    [InlineData("wait_time_ms", ComposeAggregate.Sum, false)]         /* no CAGG */
    [InlineData("qs_executions", ComposeAggregate.Sum, false)]        /* Query Store — deferred */
    [InlineData("qs_avg_duration_us", ComposeAggregate.Sum, false)]   /* QS weighted — deferred */
    [InlineData("sqlserver_cpu_utilization", ComposeAggregate.Avg, false)] /* gauge, no CAGG */
    public void CanRemap_GatesToDeltaTablesOnly(string measureKey, ComposeAggregate aggregate, bool expected) =>
        Assert.Equal(expected, ComposeCaggValueMapper.CanRemap(Measure(measureKey), aggregate));

    /* ---------------- scalar remap (query_worker_us => worker_time_*) ---------------- */

    [Fact]
    public void Scalar_Sum_ReadsSumColumn()
    {
        Assert.Equal(
            "CAST(SUM(f.worker_time_sum) AS double precision)",
            ComposeCaggValueMapper.BuildCaggNativeExpr(Plan("query_worker_us", ComposeAggregate.Sum)));
    }

    [Fact]
    public void Scalar_Min_ReadsMinColumn()
    {
        Assert.Equal(
            "CAST(MIN(f.worker_time_min) AS double precision)",
            ComposeCaggValueMapper.BuildCaggNativeExpr(Plan("query_worker_us", ComposeAggregate.Min)));
    }

    [Fact]
    public void Scalar_Max_ReadsMaxColumn()
    {
        Assert.Equal(
            "CAST(MAX(f.worker_time_max) AS double precision)",
            ComposeCaggValueMapper.BuildCaggNativeExpr(Plan("query_worker_us", ComposeAggregate.Max)));
    }

    [Fact]
    public void Scalar_Avg_IsSumOverSampleCount_NotAvgOfSums()
    {
        /* The exact reconstruction: AVG(delta) == SUM(x_sum)/SUM(sample_count), never AVG(x_sum). */
        Assert.Equal(
            "(CAST(SUM(f.worker_time_sum) AS double precision) / NULLIF(SUM(f.sample_count), 0))",
            ComposeCaggValueMapper.BuildCaggNativeExpr(Plan("query_worker_us", ComposeAggregate.Avg)));
    }

    [Fact]
    public void Scalar_ProcedureStats_MapsSameConvention()
    {
        Assert.Equal(
            "CAST(SUM(f.elapsed_time_sum) AS double precision)",
            ComposeCaggValueMapper.BuildCaggNativeExpr(Plan("proc_elapsed_us", ComposeAggregate.Sum)));
    }

    /* ---------------- Sum-ratio remap (both operands => *_sum) ---------------- */

    [Fact]
    public void SumRatio_MapsBothOperandsToSumColumns()
    {
        /* query_avg_elapsed_us = query_elapsed_us (delta_elapsed_time) / query_executions (delta_execution_count). */
        Assert.Equal(
            "(CAST(SUM(f.elapsed_time_sum) AS double precision) / NULLIF(SUM(f.execution_count_sum), 0))",
            ComposeCaggValueMapper.BuildCaggNativeExpr(Plan("query_avg_elapsed_us")));
    }

    [Fact]
    public void SumRatio_Cpu_MapsWorkerOverExecutions()
    {
        Assert.Equal(
            "(CAST(SUM(f.worker_time_sum) AS double precision) / NULLIF(SUM(f.execution_count_sum), 0))",
            ComposeCaggValueMapper.BuildCaggNativeExpr(Plan("proc_avg_cpu_us")));
    }
}
