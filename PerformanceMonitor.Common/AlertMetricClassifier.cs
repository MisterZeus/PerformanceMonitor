/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

namespace PerformanceMonitor.Common
{
    /// <summary>
    /// The single, app-agnostic source of truth for classifying an alert-history row by its metric
    /// NAME. Used by both apps' Alert History grids (resolved / critical / warning row styling) and
    /// by the Dashboard sidebar Alert badge count.
    ///
    /// Alert classification across this codebase is metric-name based — there is no structural "kind"
    /// field on a row — so this centralizes a string convention that was previously duplicated inline
    /// in Dashboard's AlertsHistoryContent and Lite's AlertHistoryRow, and had drifted: both copies
    /// recognized only the "Cleared"/"Resolved" resolution suffixes and missed "Restored", even though
    /// the UI legend documents "Server Restored" as a resolved/green state (#1225).
    /// </summary>
    public static class AlertMetricClassifier
    {
        /// <summary>
        /// True when the metric name denotes a resolution / good-news notice — a condition that
        /// previously alerted has cleared — rather than an actionable alert. Recognizes every
        /// resolution suffix the alert engines emit: "&#8230; Cleared", "&#8230; Resolved",
        /// "&#8230; Restored" (e.g. Blocking Cleared, CPU Resolved, Capture Restored, Server Restored),
        /// plus "&#8230; Resumed", "&#8230; Restarted", "&#8230; Recovered" and "&#8230; Reconnected".
        ///
        /// Those last four were the same #1225 drift one layer down: Darling's self-alert recoveries have
        /// been emitting "Collection Resumed", "Agent Restarted" and "Compression Job Recovered" — genuine
        /// resolution rows, written by the very same <c>RecordResolutionAsync</c> path as the recognized
        /// "Capture Restored" — and every one of them was landing in the history grids styled as a live
        /// actionable alert, because the suffix list had never caught up with the alerts. The AG family
        /// (#991) adds "AG Replica Reconnected", "AG Sync Recovered" and "AG Data Movement Resumed", so
        /// the list is completed here rather than adding a fifth unrecognized suffix.
        ///
        /// No actionable metric name in either app contains any of these words, so widening the match
        /// cannot turn a real alert green.
        /// </summary>
        public static bool IsResolution(string? metricName)
        {
            if (string.IsNullOrEmpty(metricName))
                return false;

            return metricName.Contains("Cleared", StringComparison.Ordinal)
                || metricName.Contains("Resolved", StringComparison.Ordinal)
                || metricName.Contains("Restored", StringComparison.Ordinal)
                || metricName.Contains("Resumed", StringComparison.Ordinal)
                || metricName.Contains("Restarted", StringComparison.Ordinal)
                || metricName.Contains("Recovered", StringComparison.Ordinal)
                || metricName.Contains("Reconnected", StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the metric name denotes a critical-severity alert (deadlock or poison wait),
        /// used for row emphasis in the history grids. Mirrors the long-standing inline convention.
        /// </summary>
        public static bool IsCritical(string? metricName)
        {
            if (string.IsNullOrEmpty(metricName))
                return false;

            return metricName.Contains("Deadlock", StringComparison.Ordinal)
                || metricName.Contains("Poison", StringComparison.Ordinal);
        }

        /// <summary>
        /// True for an ordinary (warning-severity) alert: actionable, neither a resolution notice
        /// nor critical.
        /// </summary>
        public static bool IsWarning(string? metricName) =>
            !IsResolution(metricName) && !IsCritical(metricName);
    }
}
