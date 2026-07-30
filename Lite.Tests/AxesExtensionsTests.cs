/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using PerformanceMonitor.Ui;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the shared chart-axis label formatter's display-mode conversion (#1831). This was the ONE
/// render surface that skipped <see cref="UiTimeContext.ConvertForDisplay"/> — grids, slicers,
/// tooltips, and the crosshair all converted — so every chart's bottom axis showed server time no
/// matter what the toggle said, and nothing anywhere tested the formatter.
/// </summary>
public sealed class AxesExtensionsTests
{
    /// <summary>The formatter under test, extracted the way production reaches it.</summary>
    private static Func<DateTime, string> BuildFormatter()
    {
        var plot = new ScottPlot.Plot();
        plot.Axes.DateTimeTicksBottomDateChange();
        var gen = Assert.IsType<ScottPlot.TickGenerators.DateTimeAutomatic>(plot.Axes.Bottom.TickGenerator);
        Assert.NotNull(gen.LabelFormatter);
        return gen.LabelFormatter!;
    }

    [Fact]
    public void LabelFormatter_ConvertsThroughUiTimeContext()
    {
        var original = UiTimeContext.ConvertForDisplay;
        try
        {
            /* A Lite/Dashboard-style wiring: display mode shifts server time by +1h. */
            UiTimeContext.ConvertForDisplay = t => t.AddHours(1);
            var formatter = BuildFormatter();

            var serverTime = new DateTime(2026, 1, 15, 10, 0, 0);
            var label = formatter(serverTime);

            var culture = CultureInfo.CurrentCulture;
            Assert.Contains(serverTime.AddHours(1).ToString("t", culture), label, StringComparison.Ordinal);
            Assert.DoesNotContain(serverTime.ToString("t", culture), label, StringComparison.Ordinal);
        }
        finally
        {
            UiTimeContext.ConvertForDisplay = original;
        }
    }

    [Fact]
    public void LabelFormatter_IdentityHook_LeavesTheValueAlone()
    {
        /* The Darling Viewer pre-converts plotted X and leaves the hook at identity — the
           formatter must be a no-op there, or the Viewer double-converts. */
        var original = UiTimeContext.ConvertForDisplay;
        try
        {
            UiTimeContext.ConvertForDisplay = static t => t;
            var formatter = BuildFormatter();

            var plotted = new DateTime(2026, 1, 15, 10, 0, 0);
            var label = formatter(plotted);

            Assert.Contains(plotted.ToString("t", CultureInfo.CurrentCulture), label, StringComparison.Ordinal);
        }
        finally
        {
            UiTimeContext.ConvertForDisplay = original;
        }
    }

    [Fact]
    public void LabelFormatter_PrintsTheDateLine_OnDisplayDateChanges()
    {
        /* The date-change line must key on the DISPLAY date: a +2h mode near midnight moves the
           date boundary, and the label's date line has to follow the converted value. */
        var original = UiTimeContext.ConvertForDisplay;
        try
        {
            UiTimeContext.ConvertForDisplay = t => t.AddHours(2);
            var formatter = BuildFormatter();

            /* 23:00 server = 01:00 display NEXT day; the second tick crosses no display-date
               boundary relative to the first, so it is time-only. */
            var first = formatter(new DateTime(2026, 1, 15, 23, 0, 0));
            var second = formatter(new DateTime(2026, 1, 15, 23, 30, 0));

            Assert.Contains("\n", first, StringComparison.Ordinal);      /* first tick: date line */
            Assert.DoesNotContain("\n", second, StringComparison.Ordinal); /* same display date */
        }
        finally
        {
            UiTimeContext.ConvertForDisplay = original;
        }
    }
}
