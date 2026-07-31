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

        /// <summary>
        /// What the history grids render in place of a number for a <see cref="IsStateOnly"/> metric
        /// that stored the 0 sentinel (#1846). An em dash — the typographic "no value here", and
        /// distinct from a hyphen, which reads as a minus sign in a numeric column.
        /// </summary>
        public const string StateOnlyDisplay = "—";

        /// <summary>
        /// True when the metric's "current value" is a STATE, not a measurement — so the double stored
        /// in the NOT NULL <c>current_value</c>/<c>threshold_value</c> columns is a sentinel rather than
        /// data, and rendering it as "0.00" invents a number the alert never had (#1846).
        ///
        /// <para>The write side is unchanged and stays 0: producers hand the history stores a display
        /// STRING, and <c>AlertValueParser.ParseOrDefault</c> yields 0 for any text carrying no digits
        /// ("PRIMARY", "DISCONNECTED", "resumed", "caught up", "Online", "resolved"). This predicate is
        /// the read-side counterpart, and callers apply it ONLY to a stored 0 — a nonzero value on one of
        /// these metrics means some future producer started passing a real numeric, and that number is
        /// shown rather than hidden behind a dash.</para>
        ///
        /// <para>That 0-gate is load-bearing, not belt-and-braces, because the parser scans to the first
        /// digit ANYWHERE in the text rather than requiring a leading number. So a few of the metrics
        /// listed here do not reliably store 0 at all: "AG Sync Fell Behind" spells the lag seconds or
        /// the redo-queue KB into its prose, and any of them can pick a digit out of an object name
        /// ("SQL01", "Sales2024"). Those rows keep rendering whatever was parsed. Membership here is
        /// therefore a statement about what the metric MEANS, and the gate decides case by case what a
        /// given row actually gets — which is the right split, since neither half can be judged alone.</para>
        ///
        /// <para>Two families, both verified at their fire sites rather than assumed:</para>
        /// <list type="bullet">
        /// <item><b>Every resolution notice</b>, via <see cref="IsResolution"/>. Darling's
        /// <c>BuildResolutionRecord</c> hardcodes <c>CurrentValueText: "resolved"</c> and
        /// <c>ThresholdValueText: ""</c> with both numerics null, for BOTH its own self-alert recoveries
        /// (Collection Resumed, Capture Restored, Agent Restarted, Store Disk Pressure Resolved,
        /// Compression Job Recovered) and the shared engine's resolution callback (CPU Resolved, Blocking
        /// Cleared, Blocking Wait Cleared, Deadlocks Cleared, Poison Waits Cleared, Long-Running Queries
        /// Cleared, tempdb Space Resolved, Volume Free Space Resolved, Long-Running Jobs Cleared). Reusing
        /// the classifier instead of listing those fourteen is deliberate: a fifteenth resolution metric
        /// gets the right rendering for free, which is the drift this class exists to stop. It also
        /// absorbs four of the AG/connection metrics below — "Server Restored", "AG Replica Reconnected",
        /// "AG Sync Recovered" and "AG Data Movement Resumed" are resolutions by name.</item>
        /// <item><b>The five actionable state metrics</b> enumerated here, whose value is a role, a
        /// connection state, a suspend reason or a prose explanation.</item>
        /// </list>
        ///
        /// <para>Deliberately NOT included: "Collection Stopped", "Store Disk Pressure" and "Store
        /// Runtime Upgrade" carry text that DOES contain a number, so they store nonzero and are out of
        /// scope by the stored-0 gate anyway — but "Store Disk Pressure" is the one that must never be
        /// added, because its parsed value is percent-free and a genuine 0 means a full volume.</para>
        /// </summary>
        public static bool IsStateOnly(string? metricName)
        {
            if (string.IsNullOrEmpty(metricName))
                return false;

            if (IsResolution(metricName))
                return true;

            return metricName switch
            {
                /* Role desc ("PRIMARY"/"SECONDARY"), connected state ("DISCONNECTED"), the
                   suspend_reason_desc, and JudgeSync's prose reason ("Availability Group '…': database …"),
                   respectively — none of them a measurement. */
                "AG Failover"
                    or "AG Replica Disconnected"
                    or "AG Sync Fell Behind"
                    or "AG Database Suspended"
                    /* The connection-loss reason string ("Login timeout", "Network error", …). */
                    or "Server Unreachable" => true,
                _ => false,
            };
        }
    }
}
