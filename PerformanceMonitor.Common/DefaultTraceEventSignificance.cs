/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

namespace PerformanceMonitor.Common;

/// <summary>The System-Events category a collected Default Trace event maps to.</summary>
public enum DefaultTraceEventCategory
{
    /// <summary>Data/Log file auto-grow or auto-shrink (the collector already gated these to &gt; 1 s — a stall).</summary>
    AutoGrowShrink,

    /// <summary>An error-log write (the one category with a severity floor in the significant set).</summary>
    ErrorLog,

    /// <summary>Schema DDL — Object:Created / Object:Altered / Object:Deleted.</summary>
    SchemaChange,

    /// <summary>Security audit — audit-change / DBCC / alter-trace events.</summary>
    SecurityAudit,

    /// <summary>Server Memory Change.</summary>
    MemoryChange,

    /// <summary>Any collected event that matches none of the known categories (should not occur — the
    /// collector's WHERE only admits the categories above).</summary>
    Other,
}

/// <summary>
/// The significant-set gate for Default Trace events, shared by BOTH consumers so they can never drift:
/// the viewer's <c>SystemEventSignificance</c> (which feeds the System Events surface) delegates here, and
/// the headless MCP <c>get_default_trace_events</c> tool calls it directly. Kept in
/// <c>PerformanceMonitor.Common</c> (pure, dependency-free, unit-testable) precisely because the MCP host
/// must not reference the WPF viewer project — so unlike the system_health significance (a viewer class
/// with a hand-mirrored service twin), this NEW gate is authored once, in one place.
///
/// <para>The collector's own curated WHERE is the primary filter (auto-grow/shrink already gated to &gt; 1 s,
/// tempdb / <c>_WA_</c> auto-stats DDL excluded, config-change events dropped so the config-snapshot diff
/// owns them). The ONE additional refinement the significant set adds is a severity floor on ErrorLog: the
/// default trace records every error-log write, but only a SEVERE one belongs on the System Events surface,
/// so <see cref="SevereErrorLogMinSeverity"/> drops the routine informational writes. Every other collected
/// category is significant as-collected (the collector already made that call).</para>
/// </summary>
public static class DefaultTraceEventSignificance
{
    /// <summary>
    /// Minimum ErrorLog severity to count as a significant server event (16 — the always-on "severe" floor
    /// SQL Server and sp_HealthParser use; 11-15 are user-correctable, 16 is the general error boundary).
    /// A default-trace ErrorLog event with NO severity (a system message carrying no severity value) is
    /// kept rather than silently dropped — see <see cref="IsSignificant(DefaultTraceEventCategory, int?)"/>.
    /// </summary>
    public const int SevereErrorLogMinSeverity = 16;

    /// <summary>
    /// Maps a trace event name (<c>sys.trace_events.name</c>) to its System-Events category, using the SAME
    /// patterns the collector's WHERE admits. OrdinalIgnoreCase throughout — the auto-grow/shrink names are
    /// matched by substring (they read "Data File Auto Grow", "Log File Auto Shrink", …), the rest by exact
    /// name.
    /// </summary>
    public static DefaultTraceEventCategory Classify(string? eventName)
    {
        if (string.IsNullOrEmpty(eventName))
        {
            return DefaultTraceEventCategory.Other;
        }

        if (Contains(eventName, "Server Memory Change"))
        {
            return DefaultTraceEventCategory.MemoryChange;
        }

        if (Contains(eventName, "Auto Grow") || Contains(eventName, "Auto Shrink"))
        {
            return DefaultTraceEventCategory.AutoGrowShrink;
        }

        if (Equals(eventName, "ErrorLog"))
        {
            return DefaultTraceEventCategory.ErrorLog;
        }

        if (Equals(eventName, "Object:Created")
            || Equals(eventName, "Object:Altered")
            || Equals(eventName, "Object:Deleted"))
        {
            return DefaultTraceEventCategory.SchemaChange;
        }

        if (Equals(eventName, "Audit Change Audit Event")
            || Equals(eventName, "Audit DBCC Event")
            || Equals(eventName, "Audit Server Alter Trace Event"))
        {
            return DefaultTraceEventCategory.SecurityAudit;
        }

        return DefaultTraceEventCategory.Other;
    }

    /// <summary>
    /// Whether a collected event belongs on the significant System Events set. ErrorLog is gated by severity
    /// (<see cref="SevereErrorLogMinSeverity"/>, or a null severity kept); every other category — which the
    /// collector's WHERE already curated — is significant as collected. <see cref="DefaultTraceEventCategory.Other"/>
    /// is treated as significant too (a collected row we can't categorize still passed the collector's gate, so
    /// it is surfaced rather than silently dropped).
    /// </summary>
    public static bool IsSignificant(DefaultTraceEventCategory category, int? severity) =>
        category != DefaultTraceEventCategory.ErrorLog
            || severity is null
            || severity.Value >= SevereErrorLogMinSeverity;

    /// <summary>Convenience overload — classifies <paramref name="eventName"/> then applies the severity gate.</summary>
    public static bool IsSignificant(string? eventName, int? severity) =>
        IsSignificant(Classify(eventName), severity);

    private static bool Contains(string value, string token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool Equals(string value, string other) =>
        string.Equals(value, other, StringComparison.OrdinalIgnoreCase);
}
