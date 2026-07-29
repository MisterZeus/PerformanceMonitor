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
    public partial class ProcedureHistoryWindow : Window
    {
        private readonly DatabaseService _databaseService;
        private readonly string _databaseName;
        private readonly int _objectId;
        private readonly string _objectName;
        private readonly string? _schemaName;
        private readonly string? _procedureName;
        private readonly int _hoursBack;
        private readonly DateTime? _fromDate;
        private readonly DateTime? _toDate;
        private readonly PlanNavigationController _planActions;
        private List<ProcedureExecutionHistoryItem> _historyData = new();
        private ChartHoverHelper? _chartHover;

        // Filter state
        private readonly DataGridFilterManager<ProcedureExecutionHistoryItem> _filterManager;
        private Popup? _filterPopup;
        private ColumnFilterPopup? _filterPopupContent;

        public ProcedureHistoryWindow(
            DatabaseService databaseService,
            string databaseName,
            int objectId,
            string objectName,
            int hoursBack = 24,
            DateTime? fromDate = null,
            DateTime? toDate = null,
            string? schemaName = null,
            string? procedureName = null)
        {
            InitializeComponent();

            _databaseService = databaseService;
            _databaseName = databaseName;
            _objectId = objectId;
            _objectName = objectName;
            _schemaName = schemaName;
            _procedureName = procedureName;
            _hoursBack = hoursBack;
            _fromDate = fromDate;
            _toDate = toDate;

            _planActions = new PlanNavigationController(
                this,
                (xml, label, qt) => PlanViewerWindow.ShowPlanAsync(this, xml, label, qt),
                (db, qt, est, iso, ct) => ActualPlanExecutor.ExecuteForActualPlanAsync(
                    _databaseService.ConnectionString, db, qt, est, iso, isAzureSqlDb: false, timeoutSeconds: 0, ct),
                "the monitored server");

            _filterManager = new DataGridFilterManager<ProcedureExecutionHistoryItem>(HistoryDataGrid);
            DataGridFilterColumns.AddFilterButtons(HistoryDataGrid, Filter_Click);
            _filterManager.UpdateFilterButtonStyles();

            ProcedureIdentifierText.Text = $"Procedure Execution History: {objectName} in [{databaseName}]";

            ApplyThemeToChart();
            Loaded += ProcedureHistoryWindow_Loaded;
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

        private async void ProcedureHistoryWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadHistoryAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load procedure execution history:\n\n{ex.Message}",
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
                _historyData = !string.IsNullOrEmpty(_schemaName) && !string.IsNullOrEmpty(_procedureName)
                    ? await _databaseService.GetProcedureStatsHistoryAsync(_databaseName, _schemaName, _procedureName, _hoursBack, _fromDate, _toDate)
                    : await _databaseService.GetProcedureStatsHistoryAsync(_databaseName, _objectId, _hoursBack, _fromDate, _toDate);

                // Compute per-interval executions/spills. DMV counters are cumulative and reset on
                // plan eviction, so we walk oldest→newest, detecting lifetime boundaries by CachedTime.
                // Data arrives sorted by CollectionTime DESC, so walk from end to start.
                // (Spills have no SQL-side delta column, hence the client-side walk.)
                for (int i = _historyData.Count - 1; i >= 0; i--)
                {
                    var item = _historyData[i];
                    var olderItem = i == _historyData.Count - 1 ? null : _historyData[i + 1];
                    if (olderItem == null || item.CachedTime != olderItem.CachedTime)
                    {
                        item.IntervalExecutions = item.ExecutionCount;
                        item.IntervalSpills = item.TotalSpills ?? 0;
                    }
                    else
                    {
                        item.IntervalExecutions = Math.Max(0, item.ExecutionCount - olderItem.ExecutionCount);
                        item.IntervalSpills = Math.Max(0, (item.TotalSpills ?? 0) - (olderItem.TotalSpills ?? 0));
                    }
                }

                _filterManager.UpdateData(_historyData);

                if (_historyData.Count > 0)
                {
                    var totalExecutions = _historyData.Sum(h => h.IntervalExecutions);
                    var avgCpu = _historyData.Where(h => h.AvgWorkerTimeMs.HasValue).Select(h => h.AvgWorkerTimeMs ?? 0).DefaultIfEmpty(0).Average();
                    var avgDuration = _historyData.Where(h => h.AvgElapsedTimeMs.HasValue).Select(h => h.AvgElapsedTimeMs ?? 0).DefaultIfEmpty(0).Average();
                    var firstSample = _historyData.Min(h => h.CollectionTime);
                    var lastSample = _historyData.Max(h => h.CollectionTime);

                    SummaryText.Text = string.Format(CultureInfo.CurrentCulture,
                        "Samples: {0} | First: {1:yyyy-MM-dd HH:mm} | Last: {2:yyyy-MM-dd HH:mm} | Executions: {3:N0} | Avg CPU: {4:N2} ms | Avg Duration: {5:N2} ms",
                        _historyData.Count, firstSample, lastSample, totalExecutions, avgCpu, avgDuration);

                    UpdateChart();
                }
                else
                {
                    SummaryText.Text = "No historical data found for this procedure.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load procedure execution history:\n\n{ex.Message}",
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

            HistoryChart.Plot.Clear();

            var selectedItem = MetricSelector.SelectedItem as ComboBoxItem;
            var metricTag = selectedItem?.Tag?.ToString() ?? "AvgElapsedTimeMs";
            var metricLabel = selectedItem?.Content?.ToString() ?? "Avg Duration (ms)";

            var orderedData = _historyData.OrderBy(h => h.CollectionTime).ToList();

            var dates = orderedData.Select(h => h.CollectionTime.ToOADate()).ToArray();
            var values = orderedData.Select(h => GetMetricValue(h, metricTag)).ToArray();

            var color = ScottPlot.Color.FromHex(ChartPalette.SeriesColor("MetricTrend"));
            var scatter = HistoryChart.Plot.Add.Scatter(dates, values);
            scatter.Color = color;
            ChartStyle.StyleScatter(scatter);

            HistoryChart.Plot.Axes.DateTimeTicksBottomDateChange();
            Helpers.TabHelpers.ReapplyAxisColors(HistoryChart);
            HistoryChart.Plot.YLabel(metricLabel);
            HistoryChart.Plot.XLabel("Collection Time");

            // Hover tooltip
            var unit = metricTag.Contains("Ms") ? "ms" : "";
            if (_chartHover == null)
                _chartHover = new ChartHoverHelper(HistoryChart, unit);
            else
                _chartHover.Unit = unit;
            _chartHover.Clear();
            _chartHover.Add(scatter, metricLabel);

            HistoryChart.Refresh();
        }

        private static double GetMetricValue(ProcedureExecutionHistoryItem item, string metricTag)
        {
            return metricTag switch
            {
                "AvgElapsedTimeMs" => item.AvgElapsedTimeMs ?? 0,
                "AvgWorkerTimeMs" => item.AvgWorkerTimeMs ?? 0,
                "AvgLogicalReads" => item.AvgLogicalReads ?? 0,
                "AvgLogicalWrites" => item.AvgLogicalWrites ?? 0,
                "AvgPhysicalReads" => item.AvgPhysicalReads ?? 0,
                "AvgSpills" => item.AvgSpills ?? 0,
                "IntervalExecutions" => item.IntervalExecutions,
                _ => item.AvgElapsedTimeMs ?? 0
            };
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void DownloadPlan_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is ProcedureExecutionHistoryItem item)
            {
                try
                {
                    button.IsEnabled = false;
                    button.Content = "Loading...";

                    var planXml = await _databaseService.GetProcedureStatsPlanXmlByCollectionIdAsync(item.CollectionId);

                    if (string.IsNullOrWhiteSpace(planXml))
                    {
                        MessageBox.Show("No query plan available for this collection.", "No Plan", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    var timestamp = item.CollectionTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                    var safeObjectName = string.Join("_", _objectName.Split(Path.GetInvalidFileNameChars()));
                    var defaultFileName = $"procedure_history_{safeObjectName}_{timestamp}.sqlplan";

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
            DataGridExport.ExportToCsv(sender, "procedure_history", TabHelpers.CsvSeparator);

        #endregion

        #region Plan Actions

        // Procedures are View Plan only — re-executing a proc without parameter values is unsafe,
        // matching the main Procedure grid (no Get Actual case in its switch).
        private async void ViewPlan_Click(object sender, RoutedEventArgs e)
        {
            if (GetHistoryItem(sender) is not { } item) return;
            await _planActions.ViewPlanAsync(
                () => _databaseService.GetProcedureStatsPlanXmlByCollectionIdAsync(item.CollectionId),
                $"Est Plan - {_objectName}", queryText: null);
        }

        private static ProcedureExecutionHistoryItem? GetHistoryItem(object sender)
        {
            if (sender is MenuItem menuItem && menuItem.Parent is ContextMenu contextMenu)
            {
                var dataGrid = TabHelpers.FindDataGridFromContextMenu(contextMenu);
                return (dataGrid?.CurrentCell.Item ?? dataGrid?.SelectedItem) as ProcedureExecutionHistoryItem;
            }
            return null;
        }

        #endregion
    }
}
