/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PerformanceMonitorDashboard.Helpers;
using PerformanceMonitorDashboard.Models;
using PerformanceMonitorDashboard.Services;
using PerformanceMonitor.PlanAnalysis;
using PerformanceMonitor.Ui;

namespace PerformanceMonitorDashboard.Controls
{
    public partial class QueryPerformanceContent : UserControl
    {
        private void CopyCell_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyCell(sender);

        private void CopyRow_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyRow(sender);

        private void CopyAllRows_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyAllRows(sender);

        private void CopyReproScript_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem || menuItem.Parent is not ContextMenu contextMenu) return;

            var dataGrid = TabHelpers.FindDataGridFromContextMenu(contextMenu);
            if (dataGrid?.SelectedItem == null) return;

            var item = dataGrid.SelectedItem;
            string? queryText = null;
            string? databaseName = null;
            string? planXml = null;
            string source = "Query";

            /* Extract data based on item type */
            switch (item)
            {
                case QuerySnapshotItem qs:
                    queryText = qs.QueryText;
                    databaseName = qs.DatabaseName;
                    planXml = qs.QueryPlan;
                    source = "Active Queries";
                    break;
                case QueryStatsItem qst:
                    queryText = qst.QueryText;
                    databaseName = qst.DatabaseName;
                    planXml = qst.QueryPlanXml;
                    source = "Query Stats";
                    break;
                case QueryStoreItem qsi:
                    queryText = qsi.QueryText;
                    databaseName = qsi.DatabaseName;
                    planXml = qsi.QueryPlanXml;
                    source = "Query Store";
                    break;
                case ProcedureStatsItem ps:
                    queryText = ps.ObjectName;
                    databaseName = ps.DatabaseName;
                    planXml = null; /* Procedures don't have plan XML in the model */
                    source = "Procedure Stats";
                    break;
                default:
                    MessageBox.Show("Copy Repro Script is not available for this data type.", "Not Available", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
            }

            if (string.IsNullOrWhiteSpace(queryText))
            {
                MessageBox.Show("No query text available for this row.", "No Query Text", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var script = ReproScriptBuilder.BuildReproScript(queryText, databaseName, planXml, isolationLevel: null, source);

            try
            {
                Clipboard.SetDataObject(script, false);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy to clipboard: {ex.Message}", "Clipboard Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportToCsv_Click(object sender, RoutedEventArgs e) =>
            DataGridExport.ExportToCsv(sender, "query_performance", TabHelpers.CsvSeparator);
    }
}
