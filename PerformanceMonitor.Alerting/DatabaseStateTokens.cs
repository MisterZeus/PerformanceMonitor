/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using PerformanceMonitor.Notifications;

namespace PerformanceMonitor.Alerting;

/// <summary>
/// The canonical <c>sys.databases.state_desc</c> tokens the database-state alert reasons about, so
/// the engine, both apps' stores, and their override editors reference one spelling each (a typo'd
/// "RECOVERY_PENDING" would silently never match). The alert is baseline-deviation: a database
/// alerts when its current state differs from its expected (auto-seeded baseline or user override)
/// state, so these tokens populate the override editor's dropdown rather than a global checklist.
/// The severity split (<see cref="SeverityFor"/>) is fixed here rather than per-app: a SUSPECT /
/// RECOVERY_PENDING / EMERGENCY database is a genuine integrity problem (CRITICAL), while OFFLINE /
/// RESTORING / RECOVERING are operationally-normal-or-transient (WARNING).
/// </summary>
public static class DatabaseStateTokens
{
    public const string Online = "ONLINE";
    public const string Offline = "OFFLINE";
    public const string Restoring = "RESTORING";
    public const string Recovering = "RECOVERING";
    public const string RecoveryPending = "RECOVERY_PENDING";
    public const string Suspect = "SUSPECT";
    public const string Emergency = "EMERGENCY";

    /// <summary>
    /// A synthetic effective-state token, not a raw <c>sys.databases.state_desc</c>: the stores compose it
    /// for a read-only log-shipping secondary (<c>is_in_standby = 1</c>, which otherwise reports ONLINE but
    /// flips through RESTORING on every log restore), so such a database baselines as a single stable STANDBY
    /// rather than churning. Non-critical.
    /// </summary>
    public const string Standby = "STANDBY";

    /// <summary>
    /// Sentinel <c>expected_state</c> value meaning "never alert on this database's state" — the
    /// user's per-database opt-out. Deliberately not a real <c>state_desc</c> (parenthesised,
    /// lower-case) so it can never collide with a genuine state.
    /// </summary>
    public const string Ignore = "(ignore)";

    /// <summary>The metric name every database-state alert fires under (the AlertSeverity map key).</summary>
    public const string MetricName = "Database State";

    /// <summary>
    /// The fixed severity for a given <c>state_desc</c>: CRITICAL for the integrity-failure states
    /// (SUSPECT / RECOVERY_PENDING / EMERGENCY), WARNING for everything else. Case-insensitive.
    /// </summary>
    public static AlertSeverityLevel SeverityFor(string? stateDesc) =>
        (stateDesc?.Trim().ToUpperInvariant()) switch
        {
            Suspect or RecoveryPending or Emergency => AlertSeverityLevel.Critical,
            _ => AlertSeverityLevel.Warning
        };

    /// <summary>A human-friendly rendering of a state token (RECOVERY_PENDING → "RECOVERY PENDING").</summary>
    public static string Humanize(string? stateDesc) =>
        string.IsNullOrWhiteSpace(stateDesc) ? "UNKNOWN" : stateDesc.Trim().Replace('_', ' ');
}
