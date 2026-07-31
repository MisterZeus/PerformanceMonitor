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
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Perfmon inner tab — a COPY of Lite's perfmon picker + chart (ServerTab.Pickers.cs lines
/// 396-593 plus the <c>_defaultPerfmonCounters</c> seed at line 26), reads rewired to Postgres and
/// windowed on the per-server toolbar's settable range (preset or custom From/To). The picker behaviors are
/// preserved verbatim: the pack ComboBox sourced from the SHARED <see cref="PerfmonPacks"/> (the same
/// packs Lite/Dashboard use), the General-Throughput default selection, the 12-counter cap on pack
/// fills / "Select All", the checked-to-top ordering, the search filter, "Clear All" clearing only the
/// FILTERED visible set, the reentrancy guard on programmatic checkbox writes, and the
/// generation-guarded batched chart update with its 12-series cap plotting <c>DeltaValue</c>. Series
/// ride the shared cycling <see cref="ChartPalette"/> colors (via <c>SeriesColors</c>, declared in
/// ViewerServerTab.Charts.cs) and the <see cref="ChartStyle.StyleScatter"/> line polish, and the hover
/// helper carries Lite's empty unit. Lite's per-chart drill-down context menu is intentionally NOT
/// ported (the viewer has no drill-down surfaces yet).
/// </summary>
public partial class ViewerServerTab
{
    /* The default counter set is the shared "General Throughput" pack (mirrors Lite's
       _defaultPerfmonCounters). */
    private static readonly HashSet<string> _defaultPerfmonCounters = new(
        PerfmonPacks.Packs["General Throughput"],
        StringComparer.OrdinalIgnoreCase);

    private List<SelectableItem> _perfmonCounterItems = new();
    private bool _isUpdatingPerfmonSelection;
    private int _perfmonPickerGen;
    private ChartHoverHelper? _perfmonHover;

    /// <summary>
    /// The Perfmon tab load (mirrors Lite's <c>RefreshPerfmonAsync</c>): fetch the distinct counters
    /// over the toolbar's settable window, (re)populate the picker preserving the current selection, and
    /// redraw. The hover helper is created lazily on first load so the picker's chart carries the same
    /// tooltip behavior Lite's does.
    /// </summary>
    private async Task LoadPerfmonAsync()
    {
        _perfmonHover ??= new ChartHoverHelper(PerfmonChart, "");

        var (startUtc, endUtc) = GetWindowUtc();
        var counters = await _dataService.GetDistinctPerfmonCountersAsync(_server.ServerId, startUtc, endUtc);
        PopulatePerfmonPicker(counters);
        await UpdatePerfmonChartFromPickerAsync();
    }

    private void PopulatePerfmonPicker(List<string> counters)
    {
        /* Initialize pack ComboBox once */
        if (PerfmonPackCombo.Items.Count == 0)
        {
            PerfmonPackCombo.ItemsSource = PerfmonPacks.PackNames;
            PerfmonPackCombo.SelectedItem = "General Throughput";
        }

        var previouslySelected = new HashSet<string>(_perfmonCounterItems.Where(i => i.IsSelected).Select(i => i.DisplayName));
        _perfmonCounterItems = counters.Select(c => new SelectableItem
        {
            DisplayName = c,
            IsSelected = previouslySelected.Contains(c)
                || (previouslySelected.Count == 0 && _defaultPerfmonCounters.Contains(c))
        }).ToList();
        RefreshPerfmonListOrder();
    }

    private void PerfmonPack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_perfmonCounterItems == null || _perfmonCounterItems.Count == 0) return;
        if (PerfmonPackCombo.SelectedItem is not string pack) return;

        _isUpdatingPerfmonSelection = true;

        /* Clear search so all counters are visible */
        if (PerfmonSearchBox != null)
            PerfmonSearchBox.Text = "";

        /* Uncheck everything first */
        foreach (var item in _perfmonCounterItems)
            item.IsSelected = false;

        if (pack == PerfmonPacks.AllCounters)
        {
            /* "All Counters" selects the General Throughput defaults */
            foreach (var item in _perfmonCounterItems)
            {
                if (_defaultPerfmonCounters.Contains(item.DisplayName))
                    item.IsSelected = true;
            }
        }
        else if (PerfmonPacks.Packs.TryGetValue(pack, out var packCounters))
        {
            var packSet = new HashSet<string>(packCounters, StringComparer.OrdinalIgnoreCase);
            int count = 0;
            foreach (var item in _perfmonCounterItems)
            {
                if (count >= 12) break;
                if (packSet.Contains(item.DisplayName))
                {
                    item.IsSelected = true;
                    count++;
                }
            }
        }

        _isUpdatingPerfmonSelection = false;
        RefreshPerfmonListOrder();
        _ = UpdatePerfmonChartFromPickerAsync();
    }

    private void RefreshPerfmonListOrder()
    {
        if (_perfmonCounterItems == null) return;
        _perfmonCounterItems = _perfmonCounterItems
            .OrderByDescending(x => x.IsSelected)
            .ThenBy(x => _perfmonCounterItems.IndexOf(x))
            .ToList();
        ApplyPerfmonFilter();
    }

    private void ApplyPerfmonFilter()
    {
        var search = PerfmonSearchBox?.Text?.Trim() ?? "";
        PerfmonCountersList.ItemsSource = null;
        if (string.IsNullOrEmpty(search))
            PerfmonCountersList.ItemsSource = _perfmonCounterItems;
        else
            PerfmonCountersList.ItemsSource = _perfmonCounterItems.Where(i => i.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void PerfmonSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyPerfmonFilter();

    private void PerfmonSelectAll_Click(object sender, RoutedEventArgs e)
    {
        _isUpdatingPerfmonSelection = true;
        var visible = (PerfmonCountersList.ItemsSource as IEnumerable<SelectableItem>)?.ToList() ?? _perfmonCounterItems;
        int count = visible.Count(i => i.IsSelected);
        foreach (var item in visible)
        {
            if (!item.IsSelected && count < 12)
            {
                item.IsSelected = true;
                count++;
            }
        }
        _isUpdatingPerfmonSelection = false;
        RefreshPerfmonListOrder();
        _ = UpdatePerfmonChartFromPickerAsync();
    }

    private void PerfmonClearAll_Click(object sender, RoutedEventArgs e)
    {
        _isUpdatingPerfmonSelection = true;
        var visible = (PerfmonCountersList.ItemsSource as IEnumerable<SelectableItem>)?.ToList() ?? _perfmonCounterItems;
        foreach (var item in visible) item.IsSelected = false;
        _isUpdatingPerfmonSelection = false;
        RefreshPerfmonListOrder();
        _ = UpdatePerfmonChartFromPickerAsync();
    }

    private void PerfmonCounter_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingPerfmonSelection) return;
        RefreshPerfmonListOrder();
        _ = UpdatePerfmonChartFromPickerAsync();
    }

    private async Task UpdatePerfmonChartFromPickerAsync()
    {
        /* Bump a generation on entry; after each (now genuinely async) query, bail if a newer
           invocation has started — rapid checkbox toggling otherwise interleaves two runs and
           double-plots the series. */
        var gen = ++_perfmonPickerGen;
        try
        {
            var selected = _perfmonCounterItems.Where(i => i.IsSelected).Take(12).ToList();

            ClearChart(PerfmonChart);
            _perfmonHover?.Clear();
            ApplyTheme(PerfmonChart);

            if (selected.Count == 0) { PerfmonChart.Refresh(); return; }

            /* The per-server toolbar's settable window (preset or custom From/To). The store is naive-UTC;
               display converts via ViewerTimeHelper.ForDisplay. */
            var (startUtc, endUtc) = GetWindowUtc();
            double globalMax = 0;

            // Batched fetch: one query for all selected counters (mirrors Lite's batched read).
            var trendsByCounter = await _dataService.GetPerfmonTrendsByCountersAsync(
                _server.ServerId, selected.Select(s => s.DisplayName).ToList(), startUtc, endUtc);
            if (gen != _perfmonPickerGen) return;

            for (int i = 0; i < selected.Count; i++)
            {
                if (!trendsByCounter.TryGetValue(selected[i].DisplayName, out var trend) || trend.Count == 0) continue;

                var times = trend.Select(t => ViewerTimeHelper.ForDisplay(t.CollectionTime).ToOADate()).ToArray();
                var values = trend.Select(t => (double)t.DeltaValue).ToArray();

                var plot = PerfmonChart.Plot.Add.TimeSeries(times, values);
                plot.LegendText = selected[i].DisplayName;
                plot.Color = ScottPlot.Color.FromHex(SeriesColors[i % SeriesColors.Length]);
                ChartStyle.StyleScatter(plot);
                _perfmonHover?.Add(plot, selected[i].DisplayName);

                if (values.Length > 0) globalMax = Math.Max(globalMax, values.Max());
            }

            PerfmonChart.Plot.Axes.DateTimeTicksBottomDateChange();
            var rangeStart = ViewerTimeHelper.ForDisplay(startUtc);
            var rangeEnd = ViewerTimeHelper.ForDisplay(endUtc);
            PerfmonChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
            ReapplyAxisColors(PerfmonChart);
            PerfmonChart.Plot.YLabel("Value");
            SetChartYLimitsWithLegendPadding(PerfmonChart, 0, globalMax > 0 ? globalMax : 100);
            ShowChartLegend(PerfmonChart);
            PerfmonChart.Refresh();
        }
        catch
        {
            /* Ignore chart update errors */
        }
    }

    /// <summary>
    /// Tears down the perfmon hover helper (mirrors Lite's <c>DisposeChartHelpers</c>) so the tooltip
    /// popup and its chart event handlers don't outlive a closed server tab. Called from the tab's
    /// single Dispose path in ViewerServerTab.Charts.cs.
    /// </summary>
    public void DisposePerfmonHelpers()
    {
        _perfmonHover?.Dispose();
    }
}
