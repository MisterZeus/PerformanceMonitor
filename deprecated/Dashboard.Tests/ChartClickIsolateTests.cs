/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using PerformanceMonitor.Ui;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// Tests the pure, app-agnostic logic behind the shared chart click-to-isolate mechanic
/// (<see cref="ChartHoverHelper"/>): toggle transitions, the dim-vs-full visual decision, and faithful
/// per-series restore for fill vs line-only charts. Isolate dims the other series and leaves the Y axis
/// untouched (no auto-fit). Mirror of Lite.Tests for parity.
/// </summary>
public class ChartClickIsolateTests
{
    // ── NextIsolate: toggle transitions ──────────────────────────────────────────────────────

    [Fact]
    public void NextIsolate_FromNothing_IsolatesClicked()
    {
        Assert.Equal("CXPACKET", ChartHoverHelper.NextIsolate(null, "CXPACKET"));
    }

    [Fact]
    public void NextIsolate_ClickingIsolatedSeries_TogglesOff()
    {
        Assert.Null(ChartHoverHelper.NextIsolate("CXPACKET", "CXPACKET"));
    }

    [Fact]
    public void NextIsolate_ClickingDifferentSeries_SwitchesTarget()
    {
        Assert.Equal("WRITELOG", ChartHoverHelper.NextIsolate("CXPACKET", "WRITELOG"));
    }

    [Fact]
    public void NextIsolate_IsCaseSensitive_DifferentCaseIsADifferentSeries()
    {
        // Labels are exact series identifiers; a case difference is a different series, not a toggle-off.
        Assert.Equal("cxpacket", ChartHoverHelper.NextIsolate("CXPACKET", "cxpacket"));
    }

    // ── ResolveSeriesVisual: dim vs full decision ────────────────────────────────────────────

    [Fact]
    public void ResolveSeriesVisual_NothingIsolated_EverySeriesIsFull()
    {
        var v = ChartHoverHelper.ResolveSeriesVisual(null, "AnySeries");
        Assert.False(v.Dim);
        Assert.True(v.FillRibbon);
    }

    [Fact]
    public void ResolveSeriesVisual_TargetSeries_IsFull()
    {
        var v = ChartHoverHelper.ResolveSeriesVisual("WRITELOG", "WRITELOG");
        Assert.False(v.Dim);
        Assert.True(v.FillRibbon);
    }

    [Fact]
    public void ResolveSeriesVisual_NonTargetSeries_IsDimmedWithNoFill()
    {
        var v = ChartHoverHelper.ResolveSeriesVisual("WRITELOG", "CXPACKET");
        Assert.True(v.Dim);
        Assert.False(v.FillRibbon);                 // the gradient ribbon is dropped while dimmed
        Assert.Equal(ChartHoverHelper.DimAlpha, v.LineAlpha);
    }

    [Fact]
    public void DimAlpha_IsFaintButVisible()
    {
        Assert.Equal((byte)40, ChartHoverHelper.DimAlpha);
        Assert.Equal((byte)40, ChartHoverHelper.IsolateVisual.Dimmed.LineAlpha);
        Assert.True(ChartHoverHelper.IsolateVisual.Full.FillRibbon);
        Assert.False(ChartHoverHelper.IsolateVisual.Full.Dim);
    }

    // ── RestoreSeriesVisual: faithful restore for line-only AND fill charts (regression) ─────────

    [Fact]
    public void RestoreSeriesVisual_LineOnlyChart_StaysLineOnly_NoPhantomMarkersOrFill()
    {
        // CollectorDuration / trend charts build line-only (MarkerSize 0, no fill) and never call
        // StyleScatter. Restore must NOT re-run StyleScatter — that would add density markers + a fill.
        var plot = new ScottPlot.Plot();
        var sc = plot.Add.Scatter(new double[] { 1, 2, 3 }, new double[] { 1, 2, 3 });
        var identity = ScottPlot.Color.FromHex("#4E79A7");
        sc.Color = identity;
        sc.LineWidth = 1.5f;
        sc.MarkerSize = 0;
        sc.FillY = false;
        var entry = new ChartHoverHelper.SeriesEntry(sc, "Collector", identity,
            sc.LineColor, sc.LineWidth, sc.MarkerSize, sc.FillY);

        sc.Color = identity.WithAlpha(ChartHoverHelper.DimAlpha);   // simulate a dim
        ChartHoverHelper.RestoreSeriesVisual(entry);

        Assert.Equal(0f, sc.MarkerSize);             // no phantom density markers
        Assert.False(sc.FillY);                      // no phantom fill ribbon
        Assert.Equal(1.5f, sc.LineWidth);            // original width preserved
    }

    [Fact]
    public void RestoreSeriesVisual_FillChart_RebuildsTheStyleScatterLook()
    {
        // A StyleScatter'd fill chart restores via StyleScatter, which rebuilds the gradient from the
        // unchanged data — reproducing the original look (isolate never spans a re-render).
        var plot = new ScottPlot.Plot();
        var sc = plot.Add.Scatter(new double[] { 1, 2, 3 }, new double[] { 0, 5, 10 });
        var identity = ScottPlot.Color.FromHex("#4E79A7");
        sc.Color = identity;
        ChartStyle.StyleScatter(sc);
        var entry = new ChartHoverHelper.SeriesEntry(sc, "Wait", identity,
            sc.LineColor, sc.LineWidth, sc.MarkerSize, sc.FillY);
        Assert.True(entry.OrigFillY);                // StyleScatter set FillY=true (data has a range)

        sc.Color = identity.WithAlpha(ChartHoverHelper.DimAlpha);
        sc.FillY = false;                            // simulate a dim
        ChartHoverHelper.RestoreSeriesVisual(entry);

        Assert.True(sc.FillY);                       // fill ribbon rebuilt
        Assert.Equal(2f, sc.LineWidth);              // StyleScatter's signature line width
    }
}
