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
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins <see cref="ComposeSourceRouter"/>: source selection by the window's oldest-point AGE (not display grain,
/// not window span), the CAGG dimension-coverage gate (post-reshape), and the margin-below-retention invariant.
/// Pure decision logic — no DB — so it runs ungated.
/// </summary>
public sealed class ComposeSourceRouterTests
{
    private static readonly DateTime Now = new(2026, 7, 23, 12, 0, 0, DateTimeKind.Unspecified);

    private static ComposeMeasure Measure(string key) =>
        MeasureCatalog.Measures.First(m => string.Equals(m.Key, key, StringComparison.Ordinal));

    private static PanelPlan Plan(
        string measureKey,
        PanelMode mode = PanelMode.TimeSeries,
        IReadOnlyList<ComposeDimension>? groupBy = null) =>
        new()
        {
            Measure = Measure(measureKey),
            Unit = "ms",
            Mode = mode,
            Filters = Array.Empty<ComposeFilter>(),
            GroupBy = groupBy ?? Array.Empty<ComposeDimension>(),
            Viz = "line",
        };

    [Fact]
    public void RecentWindow_RoutesRaw()
    {
        /* oldest point 2 days old — inside the 3-day raw route max → raw. */
        var route = ComposeSourceRouter.Resolve(Plan("query_worker_us"), Now, Now.AddDays(-2), RollupAvailability.All, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Raw, route.Tier);
        Assert.False(route.IsCagg);
        Assert.Null(route.CaggRelation);
    }

    [Fact]
    public void MidWindow_RoutesHourlyCagg()
    {
        var route = ComposeSourceRouter.Resolve(Plan("query_worker_us"), Now, Now.AddDays(-10), RollupAvailability.All, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Hourly, route.Tier);
        Assert.Equal("query_stats_hourly", route.CaggRelation);
    }

    [Fact]
    public void OldWindow_RoutesDailyCagg()
    {
        var route = ComposeSourceRouter.Resolve(Plan("query_worker_us"), Now, Now.AddDays(-40), RollupAvailability.All, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Daily, route.Tier);
        Assert.Equal("query_stats_daily", route.CaggRelation);
    }

    [Fact]
    public void HistoricalWindow_RoutesByAge_NotWindowSpan()
    {
        /* A 5-day-SPAN window that is 30→25 days OLD must route by age (30d → daily), NOT by span (5d → hourly):
           the hourly chunks for that range were already dropped, so span-based routing would return empty. */
        var route = ComposeSourceRouter.Resolve(Plan("query_worker_us"), Now, Now.AddDays(-30), RollupAvailability.All, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Daily, route.Tier);
    }

    [Fact]
    public void RankedMode_RoutesByAge_WithNoDisplayGrain()
    {
        /* Ranked panels resolve no display grain at all — the v1-killer case. Age-based routing still works:
           a 40-day "top N" reaches the daily CAGG instead of truncating at raw's 4 days. */
        var route = ComposeSourceRouter.Resolve(Plan("query_worker_us", PanelMode.Ranked), Now, Now.AddDays(-40), RollupAvailability.All, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Daily, route.Tier);
    }

    [Fact]
    public void NoCaggTable_AlwaysRaw()
    {
        /* wait_stats has no CAGG → raw even for a 40-day window (routing is a no-op for the ~30 non-CAGG tables). */
        var route = ComposeSourceRouter.Resolve(Plan("wait_time_ms"), Now, Now.AddDays(-40), RollupAvailability.All, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Raw, route.Tier);
    }

    [Fact]
    public void ObjectNameDimension_NowRoutes_ViaModuleMap()
    {
        /* query_stats object_name is a #1568 module join, but now coverable on the CAGG via module_map (the CAGG
           carries sql_handle) → it routes; the compiler joins module_map for the attribution. */
        var objectName = MeasureCatalog.Dimension("query_stats", "object_name")!;
        var plan = Plan("query_worker_us", groupBy: new[] { objectName });
        Assert.True(plan.UsesModuleJoin);
        var route = ComposeSourceRouter.Resolve(plan, Now, Now.AddDays(-40), RollupAvailability.All, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Daily, route.Tier);
        Assert.Equal("query_stats_daily", route.CaggRelation);
    }

    [Fact]
    public void CoveredDimension_QueryHash_Routes()
    {
        var queryHash = MeasureCatalog.Dimension("query_stats", "query_hash")!;
        var route = ComposeSourceRouter.Resolve(Plan("query_worker_us", groupBy: new[] { queryHash }), Now, Now.AddDays(-10), RollupAvailability.All, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Hourly, route.Tier);
    }

    [Fact]
    public void ServerDimension_IsUniversallyCovered()
    {
        var server = MeasureCatalog.ServerDimension("query_stats");
        var route = ComposeSourceRouter.Resolve(Plan("query_worker_us", groupBy: new[] { server }), Now, Now.AddDays(-10), RollupAvailability.All, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Hourly, route.Tier);
    }

    [Fact]
    public void ProcedureStats_SchemaName_NowRoutes()
    {
        /* schema_name was added to the procedure_stats CAGG in the reshape (#1624) → it now routes. */
        var schemaName = MeasureCatalog.Dimension("procedure_stats", "schema_name")!;
        var route = ComposeSourceRouter.Resolve(Plan("proc_worker_us", groupBy: new[] { schemaName }), Now, Now.AddDays(-10), RollupAvailability.All, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Hourly, route.Tier);
        Assert.Equal("procedure_stats_hourly", route.CaggRelation);
    }

    [Fact]
    public void QueryStore_RoutesByComposerDims()
    {
        /* query_store_stats routes by module_name/query_hash (the reshaped composer dims). */
        var route = ComposeSourceRouter.Resolve(Plan("qs_executions"), Now, Now.AddDays(-10), RollupAvailability.All, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Hourly, route.Tier);
        Assert.Equal("query_store_stats_hourly", route.CaggRelation);
    }

    [Fact]
    public void QueryStore_OldWindow_RoutesToDailyCagg()
    {
        /* query_store_stats_daily now exists → a 40-day window routes to it, not the 21d-capped hourly. */
        var route = ComposeSourceRouter.Resolve(Plan("qs_executions"), Now, Now.AddDays(-40), RollupAvailability.All, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Daily, route.Tier);
        Assert.Equal("query_store_stats_daily", route.CaggRelation);
    }

    [Fact]
    public void RouteThresholds_StayBelowRetentionHorizons()
    {
        /* The safety invariant: a route max must be strictly inside its retention horizon, so a drop lagging the
           boundary can never leave the chosen tier missing its oldest chunk. Raw kept 4d, hourly CAGGs kept 21d. */
        Assert.True(ComposeSourceRouter.RawRouteMaxAge < TimeSpan.FromDays(4));
        Assert.True(ComposeSourceRouter.HourlyRouteMaxAge < TimeSpan.FromDays(21));
    }

    /* ── #1665: availability gates the age decision — route to what the store HAS ── */

    /// <summary>
    /// The 42P01 shape: a plain-PostgreSQL store has no rollups at all, so every window — hourly-age or
    /// daily-age — must stay on raw, which plain PG never drops and which therefore holds the complete answer.
    /// Before #1665 the router chose <c>query_stats_hourly</c>/<c>_daily</c> by age alone and the compiled
    /// panel failed at run time against the missing relation.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(40)]
    public void OldWindow_NoRollupsInStore_RoutesRaw(int ageDays)
    {
        var route = ComposeSourceRouter.Resolve(
            Plan("query_worker_us"), Now, Now.AddDays(-ageDays), RollupAvailability.None, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Raw, route.Tier);
    }

    /// <summary>
    /// The flags are per TABLE: a failure-isolated ensure sweep can build one table's pair and not
    /// another's, and only the panels reading the missing pair may degrade. query_stats' hourly view gone →
    /// its panel falls to raw; a procedure_stats panel on the same store still routes to ITS hourly view.
    /// </summary>
    [Fact]
    public void HourlyMissing_DegradesOnlyTheTableThatLostIt()
    {
        var partial = RollupAvailability.All with { QueryGrainHourly = false };

        var queryRoute = ComposeSourceRouter.Resolve(Plan("query_worker_us"), Now, Now.AddDays(-10), partial, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Raw, queryRoute.Tier);

        var procedureRoute = ComposeSourceRouter.Resolve(Plan("proc_elapsed_us"), Now, Now.AddDays(-10), partial, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Hourly, procedureRoute.Tier);
        Assert.Equal("procedure_stats_hourly", procedureRoute.CaggRelation);

        var qsPartial = RollupAvailability.All with { QueryStoreGrainHourly = false };
        var qsRoute = ComposeSourceRouter.Resolve(Plan("qs_executions"), Now, Now.AddDays(-10), qsPartial, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Raw, qsRoute.Tier);
    }

    /// <summary>Daily-age window, daily view missing but hourly present: fall to the hourly view (capped at
    /// its 21-day horizon) — the same ladder the built-in tabs use, better than raw's 4 days.</summary>
    [Fact]
    public void DailyAgeWindow_DailyMissing_FallsToHourly()
    {
        var partial = RollupAvailability.All with { QueryGrainDaily = false };
        var route = ComposeSourceRouter.Resolve(Plan("query_worker_us"), Now, Now.AddDays(-40), partial, RollupCoverage.Unknown);
        Assert.Equal(ComposeSourceTier.Hourly, route.Tier);
        Assert.Equal("query_stats_hourly", route.CaggRelation);
    }
}
