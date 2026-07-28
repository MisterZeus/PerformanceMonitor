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

        /* WITH NO DATA — no startup backfill; materializing history is the --backfill-rollups operator op. */
        Assert.Contains("WITH NO DATA", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1759: every query-acceleration rollup stays MATERIALIZED-ONLY, and this pin now carries the opposite
    /// meaning it used to. It was written asserting "real-time aggregation left ON (no materialized_only)" —
    /// false twice over. TimescaleDB 2.13+ defaults the option to TRUE, so naming nothing means real-time
    /// aggregation is OFF (the runtime is pinned at 2.28.1); and turning it on would not surface un-materialized
    /// history anyway, because the watermark is a hard partition rather than a fallback.
    ///
    /// <para>What makes the pin LOAD-BEARING rather than merely accurate: <c>RollupCoverageProbeSql</c> and
    /// <c>RetentionArmSafetySql</c> both read <c>min(bucket)</c> to mean "the oldest bucket this rollup has
    /// MATERIALIZED". Adding <c>materialized_only = false</c> would union the raw branch in, so an EMPTY
    /// materialization would report RAW's oldest row as the rollup's floor. The router would then believe a
    /// rollup covers history it has not materialized (serving silence), and — far worse — the arming gate would
    /// arm a raw purge over history no rollup holds. The nine BASELINE aggregates deliberately do set the
    /// option; they are leaves whose arming coverage is the tier itself, so the same union cannot mislead a
    /// cross-tier gate.</para>
    /// </summary>
    [Fact]
    public void QueryAccelerationRollups_StayMaterializedOnly_BecauseCoverageAndArmingReadMinBucket()
    {
        foreach (var sql in new[]
        {
            TimescaleSupport.CreateQueryStatsHourlySql,
            TimescaleSupport.CreateQueryStatsDailySql,
            TimescaleSupport.CreateQueryStatsDbHourlySql,
            TimescaleSupport.CreateQueryStatsDbDailySql,
            TimescaleSupport.CreateProcedureStatsHourlySql,
            TimescaleSupport.CreateProcedureStatsDailySql,
            TimescaleSupport.CreateQueryStoreStatsHourlySql,
            TimescaleSupport.CreateQueryStoreStatsDailySql,
        })
        {
            Assert.DoesNotContain("materialized_only", sql, StringComparison.Ordinal);
        }

        /* The probe and the arming gate must keep reading the SAME expression off the SAME relation, or
           "covered" would mean two different things to the router and to the purge. */
        var probe = TimescaleSupport.RollupCoverageProbeSql(RollupAvailability.All);
        var arming = TimescaleSupport.RetentionArmSafetySql(
            "query_stats", "collection_time", TimescaleSupport.QueryStatsHourlyView);

        Assert.Contains($"min(bucket) FROM collect.{TimescaleSupport.QueryStatsHourlyView}", probe, StringComparison.Ordinal);
        Assert.Contains($"min(bucket) FROM collect.{TimescaleSupport.QueryStatsHourlyView}", arming, StringComparison.Ordinal);
        Assert.Contains("min(collection_time) FROM collect.query_stats", probe, StringComparison.Ordinal);
        Assert.Contains("min(collection_time) FROM collect.query_stats", arming, StringComparison.Ordinal);
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

    /* ── #1661: the per-database rollup carrying the I/O sums no other aggregate has ── */

    /// <summary>
    /// FinOps' database-grain workload view sums delta_logical_reads / delta_physical_reads /
    /// delta_logical_writes, and NO other continuous aggregate carries any of them — the others were built to the
    /// composer's measure set, which never exposed I/O. Without this rollup that view cannot route past the raw
    /// horizon at all.
    /// </summary>
    [Fact]
    public void QueryStatsDbHourly_CarriesTheIoSumsNoOtherAggregateHas()
    {
        var sql = TimescaleSupport.CreateQueryStatsDbHourlySql;

        Assert.Contains("CREATE MATERIALIZED VIEW IF NOT EXISTS collect.query_stats_db_hourly", sql, StringComparison.Ordinal);
        Assert.Contains("WITH (timescaledb.continuous)", sql, StringComparison.Ordinal);
        Assert.Contains("time_bucket('1 hour', collection_time) AS bucket", sql, StringComparison.Ordinal);
        Assert.Contains("FROM collect.query_stats", sql, StringComparison.Ordinal);

        foreach (var column in new[] { "delta_logical_reads", "delta_physical_reads", "delta_logical_writes", "delta_worker_time", "delta_execution_count" })
        {
            Assert.Contains("sum(" + column + ")", sql, StringComparison.Ordinal);
        }

        Assert.Contains("count(*) AS sample_count", sql, StringComparison.Ordinal);

        /* The I/O columns must exist HERE and nowhere else — if they are ever added to the query-grain aggregate
           this rollup becomes redundant, and that should be a deliberate decision, not a silent duplication. */
        foreach (var io in new[] { "delta_logical_reads", "delta_physical_reads", "delta_logical_writes" })
        {
            Assert.DoesNotContain(io, TimescaleSupport.CreateQueryStatsHourlySql, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Grouped by database_name and deliberately NOT query_hash. That coarser grain is what keeps it small despite
    /// carrying more columns than the query-grain aggregate — one row per database per hour, not one per query.
    /// </summary>
    [Fact]
    public void QueryStatsDbHourly_IsDatabaseGrain_NotQueryGrain()
    {
        var sql = TimescaleSupport.CreateQueryStatsDbHourlySql;

        Assert.Contains("GROUP BY server_id, server_name, database_name, bucket", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("query_hash", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The daily sibling must be HIERARCHICAL — sourced from the hourly rollup, never from raw. Sourcing a daily
    /// aggregate from raw would make it collapse to whatever raw still retains (4 days), which is the entire
    /// failure this tiering exists to prevent.
    /// </summary>
    [Fact]
    public void QueryStatsDbDaily_IsHierarchical_SourcedFromTheHourlyRollup()
    {
        var sql = TimescaleSupport.CreateQueryStatsDbDailySql;

        Assert.Contains("CREATE MATERIALIZED VIEW IF NOT EXISTS collect.query_stats_db_daily", sql, StringComparison.Ordinal);
        Assert.Contains("FROM collect.query_stats_db_hourly", sql, StringComparison.Ordinal);
        Assert.Contains("time_bucket('1 day', bucket) AS bucket", sql, StringComparison.Ordinal);

        /* Re-aggregates the hourly SUMS, never the raw deltas. */
        foreach (var column in new[] { "logical_reads_sum", "physical_reads_sum", "logical_writes_sum", "worker_time_sum", "execution_count_sum" })
        {
            Assert.Contains("sum(" + column + ")", sql, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The rollup filters out rows the FinOps view itself excludes (delta_worker_time IS NULL), so the
    /// pre-aggregated totals match what the raw query would have produced rather than including
    /// never-counted rows.
    /// </summary>
    [Fact]
    public void QueryStatsDbHourly_ExcludesNullWorkerTime_MatchingTheReadersFilter() =>
        Assert.Contains("WHERE delta_worker_time IS NOT NULL", TimescaleSupport.CreateQueryStatsDbHourlySql, StringComparison.Ordinal);

    /* -- #1680: retention policies are created paused, and only armed when provably safe -- */

    /// <summary>
    /// TimescaleDB runs a new retention policy's first check IMMEDIATELY at creation, not on its next interval.
    /// A policy created live therefore drops before any external session can pause it - there is no window to
    /// win, and on a field store that cost two days of history permanently. Creation must be paused.
    /// </summary>
    [Fact]
    public void RetentionPolicies_AreCreatedPaused()
    {
        var sql = TimescaleSupport.AddRetentionPolicySql("query_stats", "4 days");

        Assert.Contains("add_retention_policy('collect.query_stats'", sql, StringComparison.Ordinal);
        Assert.Contains("if_not_exists => true", sql, StringComparison.Ordinal);

        /* The pause is a SEPARATE statement run in the same transaction, targeted by job id. */
        Assert.Contains("alter_job($1::integer, scheduled => false)", TimescaleSupport.PauseJobSql, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1705: <c>add_retention_policy</c> accepts no <c>scheduled</c> argument on ANY TimescaleDB 2.x — the
    /// parameter belongs to <c>add_job</c> / <c>alter_job</c>. Passing it made this statement fail with 42883 on
    /// every store, and the per-policy catch downgraded that to a warning, so retention silently stopped existing
    /// fleet-wide. The old pin asserted the string contained <c>scheduled =&gt; false</c> and therefore passed
    /// against SQL no version could parse; this asserts the opposite, so the bug cannot come back the same way.
    /// </summary>
    [Fact]
    public void AddRetentionPolicy_NeverPassesScheduled_ItIsNotAnArgumentOfThatFunction()
    {
        var sql = TimescaleSupport.AddRetentionPolicySql("query_stats", "4 days");

        Assert.DoesNotContain("scheduled", sql, StringComparison.OrdinalIgnoreCase);

        /* The accepted 2.28.1 signature is (regclass, "any", boolean, interval, timestamptz, text, interval);
           these are the only named arguments this statement may use. */
        foreach (var illegal in new[] { "scheduled =>", "paused =>", "enabled =>" })
        {
            Assert.DoesNotContain(illegal, sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Arming is a separate statement, targeted at the retention job for one relation. It must filter by
    /// proc_name AND the hypertable, so it can never arm some other policy - or every policy - by accident.
    /// </summary>
    [Fact]
    public void ArmRetentionPolicy_TargetsExactlyOneRelationsRetentionJob()
    {
        var sql = TimescaleSupport.ArmRetentionPolicySql("query_stats");

        Assert.Contains("scheduled => true", sql, StringComparison.Ordinal);
        Assert.Contains("proc_name = 'policy_retention'", sql, StringComparison.Ordinal);
        Assert.Contains("hypertable_name = 'query_stats'", sql, StringComparison.Ordinal);
        Assert.Contains("hypertable_schema = 'collect'", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The safety probe compares the source's oldest row against the coverage tier's oldest bucket. Raw tables
    /// key on collection_time, rollups on bucket - reading the wrong column would compare against nothing and
    /// silently decide it was safe to arm.
    /// </summary>
    [Theory]
    [InlineData("query_stats", "collection_time", "query_stats_hourly")]
    [InlineData("query_stats_hourly", "bucket", "query_stats_daily")]
    public void RetentionArmSafetySql_ComparesSourceOldestToCoverageOldest(string relation, string timeColumn, string coverage)
    {
        var sql = TimescaleSupport.RetentionArmSafetySql(relation, timeColumn, coverage);

        Assert.Contains($"min({timeColumn}) FROM collect.{relation}", sql, StringComparison.Ordinal);
        Assert.Contains($"min(bucket) FROM collect.{coverage}", sql, StringComparison.Ordinal);
        Assert.Contains("AS source_oldest", sql, StringComparison.Ordinal);
        Assert.Contains("AS coverage_oldest", sql, StringComparison.Ordinal);
    }
}
