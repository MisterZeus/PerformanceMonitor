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
using System.Windows.Data;
using Microsoft.Win32;
using PerformanceMonitorDashboard.Helpers;
using PerformanceMonitorDashboard.Models;
using PerformanceMonitorDashboard.Services;
using PerformanceMonitor.Ui;

namespace PerformanceMonitorDashboard.Controls
{
    public partial class MemoryContent : UserControl
    {
        #region Context Menu Handlers

        private void CopyCell_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyCell(sender);

        private void CopyRow_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyRow(sender);

        private void CopyAllRows_Click(object sender, RoutedEventArgs e) => DataGridExport.CopyAllRows(sender);

        private void ExportToCsv_Click(object sender, RoutedEventArgs e) =>
            DataGridExport.ExportToCsv(sender, "memory", TabHelpers.CsvSeparator);

        #endregion
    }
}
