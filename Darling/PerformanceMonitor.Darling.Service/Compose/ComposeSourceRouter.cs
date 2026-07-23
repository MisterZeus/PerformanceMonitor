/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;

namespace PerformanceMonitor.Darling.Service;

/// <summary>Which physical relation a composed panel reads: the raw collector table, or one of its
/// continuous-aggregate rollups.</summary>
public enum ComposeSourceTier
{
    /// <summary>The raw per-sweep collector table (<c>collect.&lt;table&gt;</c>).</summary>
    Raw,

    /// <summary>The hourly continuous aggregate (<c>collect.&lt;table&gt;_hourly</c>).</summary>
    Hourly,

    /// <summary>The daily continuous aggregate (<c>collect.&lt;table&gt;_daily</c>).</summary>
    Daily,
}

/// <summary>
/// The routing decision for one panel: which tier to read and the relation/time-column that implies. The raw tier
/// carries no relation (the compiler keeps its existing <c>SourceTable</c> + prefix-time-column path); a CAGG tier
/// names the rollup view, whose time column is always the <c>bucket</c> the CAGG produced.
/// </summary>
public sealed record ComposeRoute(ComposeSourceTier Tier, string? CaggRelation)
{
    /// <summary>The raw route — the compiler's unchanged behaviour.</summary>
    public static readonly ComposeRoute Raw = new(ComposeSourceTier.Raw, null);

    public bool IsCagg => Tier != ComposeSourceTier.Raw;

    /// <summary>Every CAGG's time dimension is the <c>time_bucket(...) AS bucket</c> column.</summary>
    public const string CaggTimeColumn = "bucket";
}

/// <summary>One raw table's continuous-aggregate coverage: its hourly (and optional daily) rollup view names and
/// the dimensions those rollups are grouped by — the set a panel's group-by/filter dimensions must be a subset of
/// to route (the universal <c>server</c> dimension always qualifies, since server_id/server_name lead every CAGG's
/// GROUP BY).</summary>
public sealed record ComposeCaggInfo(
    string RawTable,
    string HourlyView,
    string? DailyView,
    IReadOnlySet<string> Dimensions)
{
    public bool Covers(string dimensionName) =>
        string.Equals(dimensionName, MeasureCatalog.ServerDimensionName, StringComparison.Ordinal)
        || Dimensions.Contains(dimensionName);
}

/// <summary>
/// The catalog of which raw tables have CAGGs, keyed by raw table name — the composer-dimension shape after the
/// CAGG reshape (query_store_stats regrouped to module_name/query_hash; procedure_stats carrying schema_name).
/// Dimensions here are the CAGGs' GROUP BY columns (minus the universal server prefix); a panel routes only when
/// its dimensions are covered (see <see cref="ComposeCaggInfo.Covers"/>). query_stats' <c>object_name</c> is
/// deliberately absent: it is a #1568 module join (ViaModuleJoin), not a CAGG column, so those panels stay raw.
/// query_store_stats has no daily CAGG yet (deferred until a writable-QS primary supplies real data).
/// </summary>
public static class ComposeCaggCatalog
{
    private static readonly Dictionary<string, ComposeCaggInfo> s_byTable = new(StringComparer.Ordinal)
    {
        ["query_stats"] = new(
            "query_stats", "query_stats_hourly", "query_stats_daily",
            new HashSet<string>(StringComparer.Ordinal) { "database_name", "query_hash" }),

        ["procedure_stats"] = new(
            "procedure_stats", "procedure_stats_hourly", "procedure_stats_daily",
            new HashSet<string>(StringComparer.Ordinal) { "database_name", "schema_name", "object_name" }),

        ["query_store_stats"] = new(
            "query_store_stats", "query_store_stats_hourly", DailyView: null,
            new HashSet<string>(StringComparer.Ordinal) { "database_name", "module_name", "query_hash" }),
    };

    /// <summary>The CAGG coverage for <paramref name="rawTable"/>, or null if it has no continuous aggregate.</summary>
    public static ComposeCaggInfo? For(string rawTable) =>
        s_byTable.TryGetValue(rawTable, out var info) ? info : null;
}

/// <summary>
/// Chooses the physical source tier for a composed panel by the AGE of the window's oldest point, never by the
/// display grain (an explicit bucket, or the Ranked/Scalar modes that resolve no grain at all, would otherwise
/// route wrong). Retention drops chunks by ACTUAL now, so the oldest point's age is measured from a caller-supplied
/// <c>nowUtc</c> (deterministic for tests), not from the window's end — a purely historical window ("30 to 25 days
/// ago") must reach the tier that still retains it or the query returns empty rows.
///
/// <para>Route thresholds sit a margin BELOW each retention horizon (raw kept 4d → route ≤3d; hourly kept 21d →
/// route ≤20d), so a drop lagging the boundary (1-day chunk granularity + the 3-day CAGG refresh) can never leave
/// the chosen tier missing the oldest chunk. The margin is pinned as a test invariant against the retention
/// constants. A whole window routes to the single tier its OLDEST point needs — uniform coarsening, no cross-tier
/// union (a future optimization); real-time aggregation on the CAGG stitches the still-filling recent edge.</para>
/// </summary>
public static class ComposeSourceRouter
{
    /// <summary>Raw is chosen only for windows whose oldest point is within this age — a day inside the 4-day raw
    /// retention, so raw never routes to an about-to-drop chunk.</summary>
    public static readonly TimeSpan RawRouteMaxAge = TimeSpan.FromDays(3);

    /// <summary>The hourly CAGG is chosen up to this age — a day inside the 21-day hourly retention; older windows
    /// fall to the daily CAGG (or stay on the hourly, capped, when no daily CAGG exists yet).</summary>
    public static readonly TimeSpan HourlyRouteMaxAge = TimeSpan.FromDays(20);

    /// <summary>
    /// The tier for <paramref name="plan"/> given the window's oldest point (<paramref name="windowStartUtc"/>)
    /// relative to <paramref name="nowUtc"/>. Falls through to <see cref="ComposeRoute.Raw"/> — the compiler's
    /// unchanged path — whenever the table has no CAGG, the panel uses the #1568 module join (object_name), any
    /// grouped/filtered dimension is outside the CAGG's coverage, or the window is recent enough that raw still
    /// holds it.
    /// </summary>
    public static ComposeRoute Resolve(PanelPlan plan, DateTime nowUtc, DateTime windowStartUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var cagg = ComposeCaggCatalog.For(plan.Measure.SourceTable);
        if (cagg is null)
        {
            return ComposeRoute.Raw;
        }

        /* object_name on query_stats is stitched from procedure_stats read-time (#1568) — not a CAGG column. */
        if (plan.UsesModuleJoin)
        {
            return ComposeRoute.Raw;
        }

        /* Every grouped/filtered dimension must live in the CAGG's GROUP BY, else the rollup can't reproduce it. */
        foreach (var dimension in plan.GroupBy)
        {
            if (!cagg.Covers(dimension.Name))
            {
                return ComposeRoute.Raw;
            }
        }

        foreach (var filter in plan.Filters)
        {
            if (!cagg.Covers(filter.Dimension.Name))
            {
                return ComposeRoute.Raw;
            }
        }

        var oldestAge = nowUtc - windowStartUtc;
        if (oldestAge <= RawRouteMaxAge)
        {
            return ComposeRoute.Raw;
        }

        if (oldestAge <= HourlyRouteMaxAge || cagg.DailyView is null)
        {
            return new ComposeRoute(ComposeSourceTier.Hourly, cagg.HourlyView);
        }

        return new ComposeRoute(ComposeSourceTier.Daily, cagg.DailyView);
    }
}
