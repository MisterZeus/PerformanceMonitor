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

    private static TimeSpan ParseDays(string interval)
    {
        var days = int.Parse(interval.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        return TimeSpan.FromDays(days);
    }
}
