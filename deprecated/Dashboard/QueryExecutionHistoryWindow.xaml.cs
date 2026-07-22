/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using PerformanceMonitorDashboard.Helpers;
using PerformanceMonitorDashboard.Models;
using PerformanceMonitorDashboard.Services;
using ScottPlot;
using PerformanceMonitor.Ui;
using PerformanceMonitor.PlanAnalysis;
using PerformanceMonitor.Common;

namespace PerformanceMonitorDashboard
{
    public partial class QueryExecutionHistoryWindow : Window
    {
        private readonly DatabaseService _databaseService;
        private readonly string _databaseName;
        private readonly long _queryId;
        private readonly string _sourceType;
        private readonly int _hoursBack;
        private readonly DateTime? _fromDate;
        private readonly DateTime? _toDate;
        private readonly string? _queryText;
        private readonly PlanNavigationController _planActions;
        private List<QueryExecutionHistoryItem> _historyData = new();
        private ScottPlot.IPanel? _legendPanel;
        private ChartHoverHelper? _chartHover;

        // Filter state
        private readonly DataGridFilterManager<QueryExecutionHistoryItem> _filterManager;
        private Popup? _filterPopup;
        private ColumnFilterPopup? _filterPopupContent;

        public QueryExecutionHistoryWindow(
            DatabaseService databaseService,
            string databaseName,
            long queryId,
            string sourceType = "Query Store",
            int hoursBack = 24,
            DateTime? fromDate = null,
            DateTime? toDate = null,
            string? queryText = null)
        {
            InitializeComponent();

            _databaseService = databaseService;
            _databaseName = databaseName;
            _queryId = queryId;
            _sourceType = sourceType;
            _hoursBack = hoursBack;
            _fromDate = fromDate;
            _toDate = toDate;
            _queryText = queryText;

            _planActions = new PlanNavigationController(
                this,
                (xml, label, qt) => PlanViewerWindow.ShowPlanAsync(this, xml, label, qt),
                (db, qt, est, iso, ct) => ActualPlanExecutor.ExecuteForActualPlanAsync(
                    _databaseService.ConnectionString, db, qt, est, iso, isAzureSqlDb: false, timeoutSeconds: 0, ct),
                "the monitored server");

            _filterManager = new DataGridFilterManager<QueryExecutionHistoryItem>(HistoryDataGrid);
            DataGridFilterColumns.AddFilterButtons(HistoryDataGrid, Filter_Click);
            _filterManager.UpdateFilterButtonStyles();

            QueryIdentifierText.Text = $"Query Execution History: Query {queryId} in [{databaseName}]";

            ApplyThemeToChart();
            Loaded += QueryExecutionHistoryWindow_Loaded;
            ThemeManager.ThemeChanged += OnThemeChanged;
            Closed += (s, e) => ThemeManager.ThemeChanged -= OnThemeChanged;
        }

        private void ApplyThemeToChart()
        {
            Helpers.TabHelpers.ApplyThemeToChart(HistoryChart);
        }

        private void OnThemeChanged(string _)
        {
            ApplyThemeToChart();
            HistoryChart.Refresh();
        }

        private async void QueryExecutionHistoryWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadHistoryAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load query execution history:\n\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private async Task LoadHistoryAsync()
        {
            try
            {
                if (_sourceType == "Query Store")
                {
                    _historyData = await _databaseService.GetQueryStoreHistoryAsync(_databaseName, _queryId, _hoursBack, _fromDate, _toDate);
                }
                else
                {
                    _historyData = new List<QueryExecutionHistoryItem>();
                }

                _filterManager.UpdateData(_historyData);

                if (_historyData.Count > 0)
                {
                    var totalExecutions = _historyData.Sum(h => h.CountExecutions);
                    var avgDuration = _historyData.Average(h => h.AvgDurationMs);
                    var avgCpu = _historyData.Average(h => h.AvgCpuTimeMs);
                    var avgReads = _historyData.Average(h => (double)h.AvgLogicalReads);
                    var firstSample = _historyData.Min(h => h.CollectionTime);
                    var lastSample = _historyData.Max(h => h.CollectionTime);

                    SummaryText.Text = string.Format(CultureInfo.CurrentCulture,
                        "Samples: {0} | First: {1:yyyy-MM-dd HH:mm} | Last: {2:yyyy-MM-dd HH:mm} | Total Executions: {3:N0} | Avg Duration: {4:N2} ms | Avg CPU: {5:N2} ms | Avg Reads: {6:N0}",
                        _historyData.Count, firstSample, lastSample, totalExecutions, avgDuration, avgCpu, avgReads);

                    UpdateChart();
                }
                else
                {
                    SummaryText.Text = "No historical data found for this query.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load query execution history:\n\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private void MetricSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_historyData.Count > 0)
            {
                UpdateChart();
            }
        }

        private void UpdateChart()
        {
            if (_historyData == null || _historyData.Count == 0)
                return;

            if (_legendPanel != null)
            {
                HistoryChart.Plot.Axes.Remove(_legendPanel);
                _legendPanel = null;
            }
            HistoryChart.Plot.Clear();

            var selectedItem = MetricSelector.SelectedItem as ComboBoxItem;
            var metricTag = selectedItem?.Tag?.ToString() ?? "AvgDurationMs";
            var metricLabel = selectedItem?.Content?.ToString() ?? "Avg Duration (ms)";

            // Group data by plan_id
            var planGroups = _historyData
                .GroupBy(h => h.PlanId)
                .OrderBy(g => g.Key)
                .ToList();

            // Color palette for different plans — shared cross-app cycling palette
            var colors = ChartPalette.CyclingPalette.Select(ScottPlot.Color.FromHex).ToArray();

            int colorIndex = 0;
            var scatterSeries = new List<(ScottPlot.Plottables.Scatter Scatter, string Label)>();

            foreach (var planGroup in planGroups)
            {
                var orderedData = planGroup.OrderBy(h => h.CollectionTime).ToList();

                if (orderedData.Count == 0)
                    continue;

                var dates = orderedData.Select(h => h.CollectionTime.ToOADate()).ToArray();
                var values = orderedData.Select(h => GetMetricValue(h, metricTag)).ToArray();

                var color = colors[colorIndex % colors.Length];
                var scatter = HistoryChart.Plot.Add.Scatter(dates, values);
                scatter.Color = color;
                ChartStyle.StyleScatter(scatter);
                var label = $"Plan {planGroup.Key}";
                scatter.LegendText = label;

                scatterSeries.Add((scatter, label));
                colorIndex++;
            }

            HistoryChart.Plot.Axes.DateTimeTicksBottomDateChange();
            Helpers.TabHelpers.ReapplyAxisColors(HistoryChart);
            HistoryChart.Plot.YLabel(metricLabel);
            HistoryChart.Plot.XLabel("Collection Time");
            _legendPanel = HistoryChart.Plot.ShowLegend(ScottPlot.Edge.Bottom);
            HistoryChart.Plot.Legend.FontSize = 12;

            // Hover tooltip
            var unit = metricTag.Contains("Ms") ? "ms" : "";
            if (_chartHover == null)
                _chartHover = new ChartHoverHelper(HistoryChart, unit);
            else
                _chartHover.Unit = unit;
            _chartHover.Clear();
            foreach (var (s, l) in scatterSeries)
                _chartHover.Add(s, l);

            // Update legend text
            ChartLegendText.Text = planGroups.Count > 1
                ? $"{planGroups.Count} different plans detected"
                : "Single plan";

            HistoryChart.Refresh();
        }

        private static double GetMetricValue(QueryExecutionHistoryItem item, string metricTag)
        {
            return metricTag switch
            {
                "AvgDurationMs" => item.AvgDurationMs,
                "AvgCpuTimeMs" => item.AvgCpuTimeMs,
                "AvgLogicalReads" => item.AvgLogicalReads,
                "AvgLogicalWrites" => item.AvgLogicalWrites,
                "AvgPhysicalReads" => item.AvgPhysicalReads,
                "AvgMemoryMb" => item.AvgMemoryMb,
                "AvgRowcount" => item.AvgRowcount,
                "CountExecutions" => item.CountExecutions,
                _ => item.AvgDurationMs
            };
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void DownloadPlan_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is QueryExecutionHistoryItem item)
            {
                try
                {
                    button.IsEnabled = false;
                    button.Content = "Loading...";

                    var planXml = await _databaseService.GetQueryStorePlanXmlByCollectionIdAsync(item.CollectionId);

                    if (string.IsNullOrWhiteSpace(planXml))
                    {
                        MessageBox.Show("No query plan available for this collection.", "No Plan", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    var timestamp = item.CollectionTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                    var defaultFileName = $"querystore_history_{_queryId}_{timestamp}.sqlplan";

                    var saveFileDialog = new SaveFileDialog
                    {
                        FileName = defaultFileName,
                        Filter = "SQL Plan files (*.sqlplan)|*.sqlplan|XML files (*.xml)|*.xml|All files (*.*)|*.*",
                        DefaultExt = ".sqlplan"
                    };

                    if (saveFileDialog.ShowDialog() == true)
                    {
                        File.WriteAllText(saveFileDialog.FileName, planXml);
                        MessageBox.Show($"Query plan saved to:\n{saveFileDialog.FileName}", "Plan Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to download query plan:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    button.IsEnabled = true;
                    button.Content = "Download Plan";
                }
            }
        }

        #region Column Filter Popup

        private void Filter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string columnName) return;

            if (_filterPopup == null)
            {
                _filterPopupContent = new ColumnFilterPopup();
                _filterPopupContent.FilterApplied += FilterPopup_FilterApplied;
                _filterPopupContent.FilterCleared += FilterPopup_FilterCleared;

                _filterPopup = new Popup
                {
                    Child = _filterPopupContent,
                    StaysOpen = false,
                    Placement = PlacementMode.Bottom,
                    AllowsTransparency = true
                };
            }

            _filterManager.Filters.TryGetValue(columnName, out var existingFilter);
            _filterPopupContent!.Initialize(columnName, existingFilter);

            _filterPopup.PlacementTarget = button;
            _filterPopup.IsOpen = true;
        }

        private void FilterPopup_FilterApplied(object? sender, FilterAppliedEventArgs e)
        {
            if (_filterPopup != null) _filterPopup.IsOpen = false;
            _filterManager.SetFilter(e.FilterState);
        }

        private void FilterPopup_FilterCleared(object? sender, EventArgs e)
        {
            if (_filterPopup != null) _filterPopup.IsOpen = false;
        }

        #endregion

        #region Context Menu Handlers

        private void CopyCell_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyCell(sender);

        private void CopyRow_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyRow(sender);

        private void CopyAllRows_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyAllRows(sender);

        private void ExportToCsv_Click(object sender, RoutedEventArgs e) =>
            DataGridExport.ExportToCsv(sender, "query_execution_history", TabHelpers.CsvSeparator);

        #endregion

        #region Plan Actions

        private async void ViewPlan_Click(object sender, RoutedEventArgs e)
        {
            if (GetHistoryItem(sender) is not { } item) return;
            await _planActions.ViewPlanAsync(
                () => _databaseService.GetQueryStorePlanXmlByCollectionIdAsync(item.CollectionId),
                $"Est Plan - QS {_queryId}", _queryText);
        }

        private async void GetActualPlan_Click(object sender, RoutedEventArgs e)
        {
            await _planActions.GetActualPlanAsync(_queryText, _databaseName, $"Actual Plan - QS {_queryId}");
        }

        private static QueryExecutionHistoryItem? GetHistoryItem(object sender)
        {
            if (sender is MenuItem menuItem && menuItem.Parent is ContextMenu contextMenu)
            {
                var dataGrid = TabHelpers.FindDataGridFromContextMenu(contextMenu);
                return (dataGrid?.CurrentCell.Item ?? dataGrid?.SelectedItem) as QueryExecutionHistoryItem;
            }
            return null;
        }

        #endregion
    }
}
