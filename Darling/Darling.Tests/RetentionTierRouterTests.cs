/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1661: the tier decision is shared by the composer and the viewer's built-in tabs, which live in projects that
/// cannot see each other. These pin the shared rules and — more importantly — that the composer still delegates
/// here rather than keeping a second copy of the thresholds, which is how the two would drift into disagreeing
/// about which tier still retains a window.
/// </summary>
public sealed class RetentionTierRouterTests
{
    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    /* Inside the raw horizon — raw still holds it. */
    [InlineData(0, RetentionTier.Raw)]
    [InlineData(1, RetentionTier.Raw)]
    [InlineData(3, RetentionTier.Raw)]
    /* Past raw's route margin, inside hourly retention. */
    [InlineData(4, RetentionTier.Hourly)]
    [InlineData(20, RetentionTier.Hourly)]
    /* Past hourly's route margin — only the indefinitely-kept daily CAGG still has it. */
    [InlineData(21, RetentionTier.Daily)]
    [InlineData(400, RetentionTier.Daily)]
    public void Resolve_PicksTierByAgeOfOldestPoint(int ageDays, RetentionTier expected) =>
        Assert.Equal(expected, RetentionTierRouter.Resolve(Now, Now.AddDays(-ageDays)));

    /// <summary>
    /// A window starting now (or in the future, from clock skew) must not fall through to a rollup.
    /// </summary>
    [Fact]
    public void Resolve_FutureOrPresentWindow_IsRaw()
    {
        Assert.Equal(RetentionTier.Raw, RetentionTierRouter.Resolve(Now, Now));
        Assert.Equal(RetentionTier.Raw, RetentionTierRouter.Resolve(Now, Now.AddHours(1)));
    }

    /// <summary>
    /// The route thresholds must stay strictly INSIDE the retention horizons they protect. If a threshold ever
    /// reached its horizon, a window could route to a tier whose oldest chunk had already been dropped and come
    /// back silently short — the #1661 failure mode, one tier up.
    /// </summary>
    [Fact]
    public void RouteThresholds_StayInsideTheirRetentionHorizons()
    {
        Assert.True(
            RetentionTierRouter.RawMaxAge < ParseDays(TimescaleSupport.RawRetentionInterval),
            $"Raw route margin ({RetentionTierRouter.RawMaxAge}) must stay under raw retention ({TimescaleSupport.RawRetentionInterval}).");

        Assert.True(
            RetentionTierRouter.HourlyMaxAge < ParseDays(TimescaleSupport.HourlyRetentionInterval),
            $"Hourly route margin ({RetentionTierRouter.HourlyMaxAge}) must stay under hourly retention ({TimescaleSupport.HourlyRetentionInterval}).");
    }

    /// <summary>
    /// Per-row text (query_text / query_plan) exists only in raw — no rollup carries it. A reader projecting text
    /// must clamp, and must be able to tell that it clamped so it can say so instead of silently returning a
    /// shorter window than the user asked for.
    /// </summary>
    [Fact]
    public void ClampToTextHorizon_SignalsWhenTheRequestExceedsWhatTextCovers()
    {
        var (withinStart, withinClamped) = RetentionTierRouter.ClampToTextHorizon(Now, Now.AddDays(-1));
        Assert.Equal(Now.AddDays(-1), withinStart);
        Assert.False(withinClamped);

        var (clampedStart, clamped) = RetentionTierRouter.ClampToTextHorizon(Now, Now.AddDays(-30));
        Assert.True(clamped);
        Assert.Equal(RetentionTierRouter.OldestTextInstant(Now), clampedStart);
        Assert.Equal(Now - RetentionTierRouter.RawTextHorizon, clampedStart);
    }

    /// <summary>
    /// The composer must not re-declare the thresholds. Its public constants exist for its own callers and tests,
    /// but they have to BE the shared values.
    /// </summary>
    [Fact]
    public void ComposerDelegatesToTheSharedThresholds()
    {
        Assert.Equal(RetentionTierRouter.RawMaxAge, ComposeSourceRouter.RawRouteMaxAge);
        Assert.Equal(RetentionTierRouter.HourlyMaxAge, ComposeSourceRouter.HourlyRouteMaxAge);
    }

    /* ── #1664: availability gates the age decision — plain PostgreSQL has no rollups ── */

    /// <summary>
    /// Age is only half the decision: the rollups are runtime TimescaleDB setup, so a plain-PostgreSQL store
    /// never has them — and never drops raw either, so raw is both available AND complete there. Routing an
    /// old window to a rollup that does not exist threw 42P01 in front of the user (the gated-live catch on
    /// #1661's first cut). Degrade mirrors the composer: daily-age with no daily view falls to hourly
    /// (capped); a missing hourly view falls to raw.
    /// </summary>
    [Theory]
    /* A TimescaleDB store with every rollup — the age decision is unchanged. */
    [InlineData(1, true, true, RetentionTier.Raw)]
    [InlineData(10, true, true, RetentionTier.Hourly)]
    [InlineData(30, true, true, RetentionTier.Daily)]
    /* Plain PostgreSQL — no rollups, raw holds full history: every window is raw. */
    [InlineData(10, false, false, RetentionTier.Raw)]
    [InlineData(30, false, false, RetentionTier.Raw)]
    /* Partial builds (failure-isolated setup): daily missing → hourly, mirroring
       ComposeSourceRouter's DailyView-is-null rule; hourly missing → raw, the safe floor. */
    [InlineData(30, true, false, RetentionTier.Hourly)]
    [InlineData(10, false, true, RetentionTier.Raw)]
    [InlineData(30, false, true, RetentionTier.Daily)]
    public void Resolve_WithAvailability_DegradesToWhatTheStoreActuallyHas(
        int ageDays, bool hourlyAvailable, bool dailyAvailable, RetentionTier expected) =>
        Assert.Equal(expected, RetentionTierRouter.Resolve(Now, Now.AddDays(-ageDays), hourlyAvailable, dailyAvailable));

    /// <summary>
    /// The probe must ask about exactly the views the routed readers can choose — a renamed or added rollup
    /// that the probe does not cover would route on a stale answer.
    /// </summary>
    [Fact]
    public void RollupProbe_CoversEveryViewTheRoutedReadersUse()
    {
        foreach (var view in new[]
        {
            TimescaleSupport.QueryStatsHourlyView,
            TimescaleSupport.QueryStatsDailyView,
            TimescaleSupport.QueryStatsDbHourlyView,
            TimescaleSupport.QueryStatsDbDailyView,
        })
        {
            Assert.Contains($"to_regclass('collect.{view}')", TimescaleSupport.RollupProbeSql, StringComparison.Ordinal);
        }
    }

    private static TimeSpan ParseDays(string interval)
    {
        var days = int.Parse(interval.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        return TimeSpan.FromDays(days);
    }

    /* ── #1661: the Daily Summary reader actually routes ── */

    /// <summary>
    /// Raw must return the frozen constant byte-for-byte — the recent-window path is unchanged.
    /// </summary>
    [Fact]
    public void DailySummary_RawTier_ReturnsTheConstantUnchanged() =>
        Assert.Equal(
            ViewerDataService.DailySummaryRangeSql,
            ViewerDataService.DailySummaryRangeSqlFor(RetentionTier.Raw),
            StringComparer.Ordinal);

    /// <summary>
    /// A rollup tier must swap the query-count CTE onto the matching CAGG and switch its time column to
    /// <c>bucket</c>, while leaving every other CTE alone — the wait / deadlock / blocking / CPU / collection /
    /// memory / alert sources all read tables on the 30-day default retention and must keep reading raw.
    /// </summary>
    [Theory]
    [InlineData(RetentionTier.Hourly, "query_stats_hourly")]
    [InlineData(RetentionTier.Daily, "query_stats_daily")]
    public void DailySummary_RollupTier_RoutesOnlyTheQueryCte(RetentionTier tier, string expectedRelation)
    {
        var sql = ViewerDataService.DailySummaryRangeSqlFor(tier);

        Assert.Contains($"FROM collect.{expectedRelation}", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT date_trunc('day', bucket) AS d, COUNT(DISTINCT query_hash) AS c", sql, StringComparison.Ordinal);

        /* The routed CTE no longer reads the raw passthrough view... */
        Assert.DoesNotContain("FROM v_query_stats", sql, StringComparison.Ordinal);

        /* ...but the untouched sources still do. */
        foreach (var untouched in new[] { "FROM v_wait_stats", "FROM v_deadlocks", "FROM v_cpu_utilization_stats", "FROM v_collection_log", "FROM v_memory_pressure_events" })
        {
            Assert.Contains(untouched, sql, StringComparison.Ordinal);
        }

        /* Parameter positions are unchanged, so the caller binds the same three values on every tier. */
        Assert.Contains("WHERE server_id = $1 AND bucket >= $2 AND bucket < $3", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// COUNT(DISTINCT query_hash) is only exact over a rollup because query_hash is one of the CAGG's GROUP BY
    /// columns. If that ever stops being true the routing silently starts under-counting, so pin it.
    /// </summary>
    [Fact]
    public void QueryStatsCagg_GroupsByQueryHash_WhichIsWhatMakesTheDistinctCountExact() =>
        Assert.Contains("GROUP BY server_id, server_name, database_name, query_hash", TimescaleSupport.CreateQueryStatsHourlySql, StringComparison.Ordinal);

    /* ── #1661: the FinOps aggregate readers route, and their drift guards are exercised ── */

    /// <summary>
    /// Each routed FinOps query throws if its raw fragment no longer matches the SQL. That guard is only
    /// protective if something calls it, so these exercise every tier of every routed reader — otherwise a
    /// drifted fragment would surface as a runtime exception in front of a user instead of a failed build.
    /// </summary>
    [Theory]
    [InlineData(RetentionTier.Hourly)]
    [InlineData(RetentionTier.Daily)]
    public void FinOpsReaders_RouteWithoutTrippingTheirDriftGuards(RetentionTier tier)
    {
        var usage = ViewerDataService.DatabaseResourceUsageSqlFor(tier);
        var byTotal = ViewerDataService.TopResourceConsumersByTotalSqlFor(tier);
        var byAvg = ViewerDataService.TopResourceConsumersByAvgSqlFor(tier);

        foreach (var sql in new[] { usage, byTotal, byAvg })
        {
            /* Routed off the raw passthrough, onto a rollup, with the rollup's time column. */
            Assert.DoesNotContain("FROM v_query_stats", sql, StringComparison.Ordinal);
            Assert.Contains("AND   bucket >= $2", sql, StringComparison.Ordinal);

            /* The rollups pre-filter NULL worker time, so the reader must not re-apply a delta_ predicate. */
            Assert.DoesNotContain("delta_worker_time IS NOT NULL", sql, StringComparison.Ordinal);
        }

        /* Only the database-grain reader needs the I/O rollup; the two top-consumer queries want CPU and
           executions only, which the query-grain aggregate already carries. */
        var dbGrain = tier == RetentionTier.Hourly
            ? TimescaleSupport.QueryStatsDbHourlyView
            : TimescaleSupport.QueryStatsDbDailyView;
        var queryGrain = tier == RetentionTier.Hourly
            ? TimescaleSupport.QueryStatsHourlyView
            : TimescaleSupport.QueryStatsDailyView;

        Assert.Contains($"FROM collect.{dbGrain}", usage, StringComparison.Ordinal);
        Assert.Contains("SUM(logical_reads_sum)", usage, StringComparison.Ordinal);

        Assert.Contains($"FROM collect.{queryGrain}", byTotal, StringComparison.Ordinal);
        Assert.Contains($"FROM collect.{queryGrain}", byAvg, StringComparison.Ordinal);
    }

    /// <summary>Raw must return each constant untouched — the recent-window path is unchanged.</summary>
    [Fact]
    public void FinOpsReaders_RawTier_ReturnTheConstantsUnchanged()
    {
        Assert.Equal(ViewerDataService.DatabaseResourceUsageSql, ViewerDataService.DatabaseResourceUsageSqlFor(RetentionTier.Raw), StringComparer.Ordinal);
        Assert.Equal(ViewerDataService.TopResourceConsumersByTotalSql, ViewerDataService.TopResourceConsumersByTotalSqlFor(RetentionTier.Raw), StringComparer.Ordinal);
        Assert.Equal(ViewerDataService.TopResourceConsumersByAvgSql, ViewerDataService.TopResourceConsumersByAvgSqlFor(RetentionTier.Raw), StringComparer.Ordinal);
    }
}
