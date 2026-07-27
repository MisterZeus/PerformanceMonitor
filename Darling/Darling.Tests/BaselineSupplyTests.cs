/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using PerformanceMonitor.Analysis.Baselines;
using PerformanceMonitor.Darling.Analysis;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The baseline supply tier (#1757). The bug these guard is not a slow query — it is that tiered retention
/// shrank raw to 4 days while the baseline asks for 30, so on every tiered store the anomaly thresholds were
/// being computed from a fraction of their intended history with no error raised. The timeout that got it
/// reported was the loud phase a store passes through on its way to that silence.
///
/// <para>What is pinned here is the INVARIANT — "same statistic, different supply" — not the SQL shape. The
/// shape assertions live with the QUALIFY-rewrite tests; these check the relationships that make the swap
/// legitimate and that a later change cannot quietly break.</para>
/// </summary>
public class BaselineSupplyTests
{
    /// <summary>
    /// THE RELATION THAT MAKES #1757 UNABLE TO RETURN. If baseline-tier retention ever drops below the
    /// baseline window, the supply is shorter than the question again and the degradation is silent — exactly
    /// the failure mode being fixed. Storage cannot reference Analysis, so this cross-assembly relation has
    /// no compile-time home; it lives here because Darling.Tests references both.
    /// </summary>
    [Fact]
    public void BaselineRetention_CoversTheBaselineWindow_OrNumber1757Returns()
    {
        Assert.True(
            TimescaleSupport.BaselineRetentionSpan >= TimeSpan.FromDays(BaselineMath.BaselineWindowDays),
            $"baseline-tier retention ({TimescaleSupport.BaselineRetentionSpan.TotalDays}d) must cover the " +
            $"baseline window ({BaselineMath.BaselineWindowDays}d) — below it, baselines silently degrade");

        /* The string and the TimeSpan are two statements of one fact; drift between them would arm a policy
           at a horizon the pin above never checked. */
        Assert.Equal(
            TimescaleSupport.BaselineRetentionInterval,
            $"{TimescaleSupport.BaselineRetentionSpan.TotalDays:0} days");
    }

    /// <summary>
    /// Every baseline aggregate groups by time_bucket AND collection_time. The hourly bucket is purely a
    /// partitioning/retention key; collection_time carries the GRAIN. Dropping the second group column is the
    /// tempting "simplification" that would silently change the unit of observation from one collection
    /// snapshot to one hour — a different statistic at a different scale, whose STDDEV_SAMP cannot be
    /// reconstructed at all because the hourly tier stores no sum-of-squares.
    /// </summary>
    [Fact]
    public void EveryBaselineAggregate_PreservesCollectionGrain_NotJustTheHourlyBucket()
    {
        Assert.Equal(9, TimescaleSupport.BaselineAggregates.Length);

        foreach (var (createSql, view) in TimescaleSupport.BaselineAggregates)
        {
            Assert.Contains("time_bucket('1 hour', collection_time) AS bucket", createSql, StringComparison.Ordinal);
            Assert.Contains("GROUP BY server_id, bucket, collection_time", createSql, StringComparison.Ordinal);
            Assert.Contains("collect." + view, createSql, StringComparison.Ordinal);
            /* Real-time aggregation is opted INTO explicitly. TimescaleDB 2.13+ disables it by default, and
               these views are created WITH NO DATA behind a policy that only refreshes the trailing 3 days —
               without both this flag and the startup backfill they would answer a 30-day question with 3 days
               of supply, which is worse than the raw horizon the tier exists to escape. */
            Assert.Contains("timescaledb.continuous, timescaledb.materialized_only = false", createSql, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// THE PRECONDITION FOR PER-TABLE (RATHER THAN PER-FAMILY) AGGREGATES: two families sharing a source must
    /// share its row-level filters, or one family's filter baked into the shared aggregate silently poisons
    /// the other's supply. Two sources are shared today — wait_stats (WaitStats + WaitMsPerSec) and
    /// blocked_process_reports (Blocking + BlockingPerMinute) — and this asserts the filters still match.
    /// A future family-specific filter on a shared source must split the aggregate, not narrow it.
    /// </summary>
    [Fact]
    public void SharedSourceFamilies_ShareTheirRowLevelFilters_OrTheAggregateCannotServeBoth()
    {
        /* wait_stats: both families filter on non-negative deltas and nothing else. */
        var waitStats = PgBaselineProvider.GetBaselineQuery(MetricNames.WaitStats)!;
        var waitRate = PgBaselineProvider.GetBaselineQuery(MetricNames.WaitMsPerSec)!;
        Assert.Contains("FROM wait_stats_baseline", waitStats, StringComparison.Ordinal);
        Assert.Contains("FROM wait_stats_baseline", waitRate, StringComparison.Ordinal);
        Assert.Contains("delta_wait_time_ms >= 0", TimescaleSupport.CreateWaitStatsBaselineSql, StringComparison.Ordinal);

        /* blocked_process_reports: neither family filters, so the shared aggregate carries no WHERE at all.
           A WHERE appearing here later means one family narrowed the other's supply. */
        var blocking = PgBaselineProvider.GetBaselineQuery(MetricNames.Blocking)!;
        var blockingPerMinute = PgBaselineProvider.GetBaselineQuery(MetricNames.BlockingPerMinute)!;
        Assert.Contains("FROM blocked_process_baseline", blocking, StringComparison.Ordinal);
        Assert.Contains("FROM blocked_process_baseline", blockingPerMinute, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE", TimescaleSupport.CreateBlockedProcessBaselineSql, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE MUTATION CHECK the whole change exists for: no baseline family may read a raw <c>v_*</c>
    /// passthrough. Point one back at raw — the state dev was in — and this goes red. It scans the live SQL
    /// only: the QUALIFY-rewrite doc comments quote Lite's DuckDB originals verbatim and legitimately name
    /// the raw views, so comment lines are stripped before the check rather than the check being weakened.
    /// </summary>
    [Fact]
    public void NoBaselineFamily_StillReadsRawHistory()
    {
        var families = new[]
        {
            MetricNames.Cpu, MetricNames.BatchRequests, MetricNames.WaitStats, MetricNames.SessionCount,
            MetricNames.QueryDuration, MetricNames.IoLatency, MetricNames.Blocking, MetricNames.Deadlock,
            MetricNames.Memory, MetricNames.WaitMsPerSec, MetricNames.BlockingPerMinute,
        };

        foreach (var family in families)
        {
            var sql = PgBaselineProvider.GetBaselineQuery(family);
            Assert.NotNull(sql);

            var live = string.Join('\n', sql!
                .Split('\n')
                .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));

            Assert.False(
                live.Contains("FROM v_", StringComparison.Ordinal),
                $"{family} still reads a raw passthrough — on a tiered store that is 4 days of supply for a " +
                $"{BaselineMath.BaselineWindowDays}-day window, which is #1757");
        }
    }

    /// <summary>
    /// The eleven families are served by the nine aggregates and nothing else — a family pointed at a view
    /// that no longer exists would fail at runtime against a real store, which no unit test would catch.
    /// </summary>
    [Fact]
    public void EveryFamily_ReadsOneOfTheNineBaselineAggregates()
    {
        var views = TimescaleSupport.BaselineAggregates.Select(a => a.View).ToArray();
        var families = new[]
        {
            MetricNames.Cpu, MetricNames.BatchRequests, MetricNames.WaitStats, MetricNames.SessionCount,
            MetricNames.QueryDuration, MetricNames.IoLatency, MetricNames.Blocking, MetricNames.Deadlock,
            MetricNames.Memory, MetricNames.WaitMsPerSec, MetricNames.BlockingPerMinute,
        };

        foreach (var family in families)
        {
            var sql = PgBaselineProvider.GetBaselineQuery(family)!;
            Assert.True(
                views.Any(v => sql.Contains("FROM " + v, StringComparison.Ordinal)),
                $"{family} does not read any known baseline aggregate");
        }
    }

    /// <summary>
    /// <c>refresh_continuous_aggregate</c>'s window bounds are declared <c>"any"</c>. An untyped literal or an
    /// uncast parameter leaves PostgreSQL with no type to resolve the polymorphic argument against, so the
    /// casts are load-bearing rather than decoration — and the bound parameter keeps a timestamp out of string
    /// interpolation. The forced form is the 4-argument 2.18+ signature; the tunables that the published API
    /// page shows as named arguments are NOT parameters of this procedure, so their absence here is correct.
    /// </summary>
    [Fact]
    public void RefreshSql_BindsAndCastsItsBounds_BecauseTheParametersArePolymorphic()
    {
        var plain = TimescaleSupport.RefreshContinuousAggregateSql(TimescaleSupport.CpuBaselineView);
        Assert.Contains("$1::timestamp", plain, StringComparison.Ordinal);
        Assert.Contains("NULL::timestamp", plain, StringComparison.Ordinal);
        Assert.Contains("'collect.cpu_utilization_baseline'::regclass", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("buckets_per_batch", plain, StringComparison.Ordinal);

        /* force is the 4th positional argument, and only ever set deliberately. */
        Assert.EndsWith("NULL::timestamp)", plain, StringComparison.Ordinal);
        var forced = TimescaleSupport.RefreshContinuousAggregateSql(TimescaleSupport.CpuBaselineView, force: true);
        Assert.EndsWith("NULL::timestamp, true)", forced, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE BACKFILL MUST NOT BLOCK STARTUP. It is a bulk materialization whose cost scales with whatever
    /// history the store already had, and the composer tuning, the delta re-seed and the collection loop are
    /// all sequenced after the TimescaleDB block — so an <c>await</c> at the launch site takes a restarted
    /// service dark for the backfill's whole duration, precisely when an operator is most likely restarting
    /// it. That regression compiles, passes every functional test, and is invisible except on a big store,
    /// which is why it is pinned at the source. It must still be AWAITED at the shutdown drain: launched and
    /// never observed is a different bug.
    /// </summary>
    [Fact]
    public void BaselineBackfill_IsLaunchedNotAwaited_ButStillDrainedOnShutdown()
    {
        var worker = ReadWorkerSource();

        Assert.Contains("baselineBackfill = RunBaselineBackfillAsync(", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("await RunBaselineBackfillAsync(", worker, StringComparison.Ordinal);
        Assert.Contains("await baselineBackfill;", worker, StringComparison.Ordinal);
    }

    private static string ReadWorkerSource([CallerFilePath] string thisFile = "")
    {
        /* Locate the repo from this file, the AlertFiringLogTests idiom - no build-output copying. */
        var relative = Path.Combine("Darling", "PerformanceMonitor.Darling.Service", "DarlingWorker.cs");
        var dir = Path.GetDirectoryName(thisFile)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, relative)))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
