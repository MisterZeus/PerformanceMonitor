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
/// Pins the continuous-aggregate + retention-tier SQL so the tested shape can never silently drift: the three
/// HOURLY rollups (query_stats / procedure_stats / query_store_stats), the two HIERARCHICAL daily rollups (sourced
/// from the hourly CAGGs, not raw), the per-cadence refresh policies, and the tiered retention (raw 4d, hourly
/// CAGGs 21d, daily kept indefinitely). All idempotent RUNTIME setup in <see cref="TimescaleSupport"/> (the
/// worker's TimescaleDB block), NOT a versioned migration, so there is no schema-version change to pin.
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
        Assert.Contains("GROUP BY server_id, server_name, database_name, query_hash, sql_handle, bucket", sql, StringComparison.Ordinal);

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
    public void ProcedureStatsHourly_GroupedBySchemaAndObject()
    {
        var sql = TimescaleSupport.CreateProcedureStatsHourlySql;

        Assert.Contains("CREATE MATERIALIZED VIEW IF NOT EXISTS collect.procedure_stats_hourly", sql, StringComparison.Ordinal);
        Assert.Contains("WITH (timescaledb.continuous)", sql, StringComparison.Ordinal);
        Assert.Contains("FROM collect.procedure_stats", sql, StringComparison.Ordinal);
        /* schema_name + object_name — both composer dimensions (a panel by schema_name alone re-aggregates). */
        Assert.Contains("GROUP BY server_id, server_name, database_name, schema_name, object_name, bucket", sql, StringComparison.Ordinal);
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

    [Fact]
    public void QueryStoreStatsHourly_GroupedByComposerDims_CarriesWeightedSums()
    {
        var sql = TimescaleSupport.CreateQueryStoreStatsHourlySql;

        Assert.Contains("CREATE MATERIALIZED VIEW IF NOT EXISTS collect.query_store_stats_hourly", sql, StringComparison.Ordinal);
        Assert.Contains("WITH (timescaledb.continuous)", sql, StringComparison.Ordinal);
        Assert.Contains("time_bucket('1 hour', collection_time) AS bucket", sql, StringComparison.Ordinal);
        Assert.Contains("FROM collect.query_store_stats", sql, StringComparison.Ordinal);
        /* The COMPOSER's QS dimensions (module_name / query_hash) so a composed QS panel can route here — NOT
           Query Store's own query_id / plan_id, which the composer never exposes. */
        Assert.Contains("GROUP BY server_id, server_name, database_name, module_name, query_hash, bucket", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("query_id", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("plan_id", sql, StringComparison.Ordinal);
        /* Execution-WEIGHTED sums so the composer's weighted mean = duration_us_weighted_sum / execution_count_sum
           composes EXACTLY (avg*count = the interval total, summed = the true total) — never an avg-of-avgs. */
        Assert.Contains("sum(execution_count) AS execution_count_sum", sql, StringComparison.Ordinal);
        Assert.Contains("sum(avg_duration_us::double precision * execution_count) AS duration_us_weighted_sum", sql, StringComparison.Ordinal);
        Assert.Contains("sum(avg_cpu_time_us::double precision * execution_count) AS cpu_us_weighted_sum", sql, StringComparison.Ordinal);
        Assert.Contains("max(max_duration_us) AS max_duration_us_max", sql, StringComparison.Ordinal);
        Assert.Contains("max(max_cpu_time_us) AS max_cpu_time_us_max", sql, StringComparison.Ordinal);
        /* The imprecise avg-of-avgs shape is gone. */
        Assert.DoesNotContain("avg(avg_duration_us)", sql, StringComparison.Ordinal);
        Assert.Contains("count(*) AS sample_count", sql, StringComparison.Ordinal);
        Assert.Contains("WITH NO DATA", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryStoreStatsDaily_IsHierarchical_FromQsHourly_SameColumnsAsHourly()
    {
        var sql = TimescaleSupport.CreateQueryStoreStatsDailySql;

        Assert.Contains("CREATE MATERIALIZED VIEW IF NOT EXISTS collect.query_store_stats_daily", sql, StringComparison.Ordinal);
        Assert.Contains("FROM collect.query_store_stats_hourly", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY server_id, server_name, database_name, module_name, query_hash, time_bucket('1 day', bucket)", sql, StringComparison.Ordinal);
        /* Same column NAMES as the hourly (so ComposeCaggValueMapper reads both unchanged): SUM the weighted sums,
           MAX the peaks, SUM executions + sample_count. */
        Assert.Contains("sum(duration_us_weighted_sum) AS duration_us_weighted_sum", sql, StringComparison.Ordinal);
        Assert.Contains("sum(cpu_us_weighted_sum) AS cpu_us_weighted_sum", sql, StringComparison.Ordinal);
        Assert.Contains("sum(execution_count_sum) AS execution_count_sum", sql, StringComparison.Ordinal);
        Assert.Contains("max(max_duration_us_max) AS max_duration_us_max", sql, StringComparison.Ordinal);
        Assert.Contains("max(max_cpu_time_us_max) AS max_cpu_time_us_max", sql, StringComparison.Ordinal);
        Assert.Contains("sum(sample_count) AS sample_count", sql, StringComparison.Ordinal);
        Assert.Contains("WITH NO DATA", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryStatsDaily_IsHierarchical_SourcedFromHourlyCagg_GroupedByExplicitDayBucket()
    {
        var sql = TimescaleSupport.CreateQueryStatsDailySql;

        Assert.Contains("CREATE MATERIALIZED VIEW IF NOT EXISTS collect.query_stats_daily", sql, StringComparison.Ordinal);
        Assert.Contains("WITH (timescaledb.continuous)", sql, StringComparison.Ordinal);
        /* HIERARCHICAL: sourced from the hourly CAGG, NOT raw. */
        Assert.Contains("FROM collect.query_stats_hourly", sql, StringComparison.Ordinal);
        Assert.Contains("time_bucket('1 day', bucket) AS bucket", sql, StringComparison.Ordinal);
        /* GROUP BY uses the explicit time_bucket EXPRESSION, never the bare `bucket` alias: a bare alias binds to
           the hourly source column under Postgres's input-column-wins rule and would group by hour, not day. */
        Assert.Contains("GROUP BY server_id, server_name, database_name, query_hash, sql_handle, time_bucket('1 day', bucket)", sql, StringComparison.Ordinal);
        /* Re-aggregates the hourly rollup: SUM of sums, MIN of mins, MAX of maxes, SUM of the sample_counts. */
        Assert.Contains("sum(worker_time_sum) AS worker_time_sum", sql, StringComparison.Ordinal);
        Assert.Contains("min(worker_time_min) AS worker_time_min", sql, StringComparison.Ordinal);
        Assert.Contains("max(worker_time_max) AS worker_time_max", sql, StringComparison.Ordinal);
        Assert.Contains("sum(sample_count) AS sample_count", sql, StringComparison.Ordinal);
        Assert.Contains("WITH NO DATA", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcedureStatsDaily_MirrorsQueryStatsDaily_FromProcedureHourly_ByObjectName()
    {
        var sql = TimescaleSupport.CreateProcedureStatsDailySql;

        Assert.Contains("CREATE MATERIALIZED VIEW IF NOT EXISTS collect.procedure_stats_daily", sql, StringComparison.Ordinal);
        Assert.Contains("FROM collect.procedure_stats_hourly", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY server_id, server_name, database_name, schema_name, object_name, time_bucket('1 day', bucket)", sql, StringComparison.Ordinal);
        Assert.Contains("sum(sample_count) AS sample_count", sql, StringComparison.Ordinal);
        Assert.Contains("WITH NO DATA", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DailyPolicy_UsesOneDayEndOffsetAndSchedule_KeepsThreeDayStart()
    {
        var sql = TimescaleSupport.AddContinuousAggregatePolicySql(TimescaleSupport.QueryStatsDailyView, "1 day", "1 day");

        Assert.Contains("add_continuous_aggregate_policy('collect.query_stats_daily'", sql, StringComparison.Ordinal);
        Assert.Contains("start_offset => INTERVAL '3 days'", sql, StringComparison.Ordinal);
        Assert.Contains("end_offset => INTERVAL '1 day'", sql, StringComparison.Ordinal);
        Assert.Contains("schedule_interval => INTERVAL '1 day'", sql, StringComparison.Ordinal);
        Assert.Contains("if_not_exists => true", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RetentionPolicy_IsIdempotentChunkDrop()
    {
        var sql = TimescaleSupport.AddRetentionPolicySql("query_stats", TimescaleSupport.RawRetentionInterval);

        Assert.Contains("add_retention_policy('collect.query_stats'", sql, StringComparison.Ordinal);
        Assert.Contains("drop_after => INTERVAL '4 days'", sql, StringComparison.Ordinal);
        Assert.Contains("if_not_exists => true", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RetentionTiers_RawFourDays_HourlyTwentyOneDays_StayPastTheNextRefreshWindow()
    {
        /* The buffers the whole tiering rests on: raw's 4d stays past the hourly CAGG's 3d refresh start; the
           hourly CAGGs' 21d stays past the daily CAGG's 3d refresh start. Either dropping below 3 days would let a
           drop outrun the aggregate meant to preserve that history. */
        Assert.Equal("4 days", TimescaleSupport.RawRetentionInterval);
        Assert.Equal("21 days", TimescaleSupport.HourlyRetentionInterval);
    }
}
