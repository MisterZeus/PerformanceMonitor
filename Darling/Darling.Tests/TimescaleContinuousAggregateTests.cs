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
/// Pins the hourly continuous-aggregate definitions (the query_stats / procedure_stats query-acceleration
/// rollups) so the tested SQL shape can never silently drift. These are idempotent RUNTIME setup in
/// <see cref="TimescaleSupport"/> (created in the worker's TimescaleDB block), NOT a versioned migration, so
/// there is no schema-version change to pin. Query acceleration only — not retention.
/// </summary>
public sealed class TimescaleContinuousAggregateTests
{
    [Fact]
    public void QueryStatsHourly_IsAContinuousAggregate_HourBucketed_GroupedByComposerDimensions()
    {
        var sql = TimescaleSupport.CreateQueryStatsHourlySql;

        Assert.Contains("CREATE MATERIALIZED VIEW IF NOT EXISTS collect.query_stats_hourly", sql, StringComparison.Ordinal);
        Assert.Contains("WITH (timescaledb.continuous)", sql, StringComparison.Ordinal);
        Assert.Contains("time_bucket('1 hour', collection_time) AS bucket", sql, StringComparison.Ordinal);
        Assert.Contains("FROM collect.query_stats", sql, StringComparison.Ordinal);
        /* Grouped by the composer's query_stats dimensions, in order, so a panel points here with no remapping. */
        Assert.Contains("GROUP BY server_id, server_name, database_name, query_hash, bucket", sql, StringComparison.Ordinal);

        /* SUM/MIN/MAX on each per-interval delta (so avg composes at query time as sum/count) + a sample_count;
           deliberately NO materialized average (it would not re-aggregate correctly). */
        foreach (var col in new[] { "delta_worker_time", "delta_elapsed_time", "delta_execution_count" })
        {
            Assert.Contains("sum(" + col + ")", sql, StringComparison.Ordinal);
            Assert.Contains("min(" + col + ")", sql, StringComparison.Ordinal);
            Assert.Contains("max(" + col + ")", sql, StringComparison.Ordinal);
        }

        Assert.Contains("count(*) AS sample_count", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("avg(", sql, StringComparison.Ordinal);

        /* WITH NO DATA (no startup backfill), and real-time aggregation left ON (no materialized_only) so the
           view is correct to query for any window immediately. */
        Assert.Contains("WITH NO DATA", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("materialized_only", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcedureStatsHourly_MirrorsQueryStats_GroupedByObjectName()
    {
        var sql = TimescaleSupport.CreateProcedureStatsHourlySql;

        Assert.Contains("CREATE MATERIALIZED VIEW IF NOT EXISTS collect.procedure_stats_hourly", sql, StringComparison.Ordinal);
        Assert.Contains("WITH (timescaledb.continuous)", sql, StringComparison.Ordinal);
        Assert.Contains("FROM collect.procedure_stats", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY server_id, server_name, database_name, object_name, bucket", sql, StringComparison.Ordinal);
        Assert.Contains("count(*) AS sample_count", sql, StringComparison.Ordinal);
        Assert.Contains("WITH NO DATA", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuousAggregatePolicy_IsTheConservativeHourlyShape_Idempotent()
    {
        var sql = TimescaleSupport.AddContinuousAggregatePolicySql(TimescaleSupport.QueryStatsHourlyView);

        Assert.Contains("add_continuous_aggregate_policy('collect.query_stats_hourly'", sql, StringComparison.Ordinal);
        Assert.Contains("start_offset => INTERVAL '3 days'", sql, StringComparison.Ordinal);
        Assert.Contains("end_offset => INTERVAL '1 hour'", sql, StringComparison.Ordinal);
        Assert.Contains("schedule_interval => INTERVAL '1 hour'", sql, StringComparison.Ordinal);
        Assert.Contains("if_not_exists => true", sql, StringComparison.Ordinal);
    }
}
