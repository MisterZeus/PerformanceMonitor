/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Notifications;

/// <summary>
/// Decides whether a "Version Store (PVS)" alert should (re-)fire for a server, so a standing
/// breach does not re-notify — and re-record an alert-history row — every cooldown (#1984).
/// <para>
/// A large PVS is a sustained condition twice over: while cleanup is pinned the store only grows,
/// and measured on a live rig the allocated space stays dedicated even after the pinning
/// transaction clears and the cleaner runs — so a plain per-cooldown level check would re-fire for
/// hours after the incident. Same rule as <see cref="LowDiskAlertGate"/> with the direction
/// flipped (PVS worsens by RISING): notify on a NEW or WORSENING breach, stay quiet at an
/// unchanged level.
/// </para>
/// </summary>
public static class PvsAlertGate
{
    /// <summary>
    /// Minimum rise, in PVS-percent-of-database points above the last-alerted level, required to
    /// re-alert. Wider than the low-disk margin (1.0) for cause: pvs_stats collects HOURLY, so
    /// each sweep sees an hour of movement, and version stores that are growing at all typically
    /// move whole points per hour — a sub-5-point drift is a level, not a trend.
    /// </summary>
    public const double DefaultWorseningMarginPercent = 5.0;

    /// <summary>
    /// Returns true when a PVS alert should fire this cycle.
    /// </summary>
    /// <param name="currentWorstPvsPercent">
    /// PVS percent-of-database of the worst (highest) breached database this cycle.
    /// </param>
    /// <param name="lastAlertedPvsPercent">
    /// Percent captured when the alert last fired for this server, or <c>null</c> when there is no
    /// active PVS alert (a fresh breach — the caller clears its watermark when the condition
    /// resolves).
    /// </param>
    /// <param name="worseningMarginPercent">
    /// Required worsening, in percentage points; defaults to <see cref="DefaultWorseningMarginPercent"/>.
    /// </param>
    public static bool ShouldAlert(
        double currentWorstPvsPercent,
        double? lastAlertedPvsPercent,
        double worseningMarginPercent = DefaultWorseningMarginPercent)
    {
        if (lastAlertedPvsPercent is null)
        {
            return true;
        }

        return currentWorstPvsPercent >= lastAlertedPvsPercent.Value + worseningMarginPercent;
    }
}
