/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Storage;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// The compose runner's cached answer to "which retention rollups does this store have?" (#1665 — the
/// composer's arm of the #1664 availability guard), plus the "partial window, and says so" notice for the
/// routes that availability (or the value-remap/dimension-coverage gates) forced below what the window's age
/// wanted. Caching mirrors the viewer's <c>GetRollupAvailabilityAsync</c> semantics exactly: once every
/// rollup exists the answer is cached for the store's lifetime (a created CAGG is never dropped outside the
/// service's own reshape sweep); while the store reports none/partial, re-probe at most every
/// <see cref="ReprobeInterval"/> so an ensure sweep finishing mid-session converges without a restart; a
/// failed probe answers <see cref="RollupAvailability.None"/> — raw always exists, so "route everything to
/// raw" is the never-wrong fallback. Keyed per data source (not a bare static) so gated-live tests spinning
/// several stores in one process can never bleed one store's shape into another's.
/// </summary>
internal static class ComposeStoreAvailability
{
    private sealed class Entry
    {
        public RollupAvailability Rollups;
        public bool Probed;
        public DateTime ProbedAtUtc;
    }

    private static readonly ConditionalWeakTable<NpgsqlDataSource, Entry> s_entries = new();

    /// <summary>While the store reports no (or partial) rollups, re-probe at most this often — the same
    /// convergence interval the viewer uses.</summary>
    internal static readonly TimeSpan ReprobeInterval = TimeSpan.FromMinutes(5);

    /// <summary>The store's rollup availability, probed lazily and cached per data source. Benignly racy:
    /// concurrent panel runs may probe twice; the probe is a single catalog lookup and last-write-wins
    /// caches the same answer.</summary>
    internal static async ValueTask<RollupAvailability> GetRollupsAsync(NpgsqlDataSource postgres, CancellationToken cancellationToken)
    {
        var entry = s_entries.GetOrCreateValue(postgres);

        lock (entry)
        {
            if (entry.Probed
                && (entry.Rollups.AllPresent || DateTime.UtcNow - entry.ProbedAtUtc < ReprobeInterval))
            {
                return entry.Rollups;
            }
        }

        RollupAvailability rollups;
        try
        {
            rollups = await TimescaleSupport.DetectRollupsAsync(postgres, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* A store hiccup mid-probe must not fail the panel — raw is the safe answer, and the re-probe
               interval retries soon. */
            rollups = RollupAvailability.None;
            _ = ex;
        }

        lock (entry)
        {
            entry.Rollups = rollups;
            entry.Probed = true;
            entry.ProbedAtUtc = DateTime.UtcNow;
        }

        return rollups;
    }

    /// <summary>
    /// The caller-facing "partial window" notice for a compiled panel, or null when the route can honestly
    /// serve the whole window. Fires when the route landed on a tier whose RETENTION cannot reach the
    /// window's start on a retention-active store: raw keeps ~<see cref="TimescaleSupport.RawRetentionInterval"/>,
    /// the hourly CAGG ~<see cref="TimescaleSupport.HourlyRetentionInterval"/>, the daily CAGG everything.
    /// Deliberately states the FACT (older points are missing) without guessing the cause — the same raw
    /// route is reached by a missing rollup, a value expression the CAGG can't remap, or an uncovered
    /// dimension, and all of them truncate identically. On a store with no rollups at all
    /// (<see cref="RollupAvailability.None"/> — plain PostgreSQL, or a failed probe) there is no notice:
    /// without the extension no retention policy ever drops raw, so raw holds the complete answer (#1665).
    /// Scoped to the TIERED tables (<see cref="ComposeCaggCatalog"/>): every other source reaches Raw via
    /// the no-CAGG early return and lives on the 30-day collector purge, not the 4-day tier — a 7-day
    /// wait_stats panel is complete on raw, and a notice there would be a false alarm.
    /// </summary>
    internal static string? BuildRetentionNotice(string sourceTable, ComposeRoute route, DateTime windowStartUtc, DateTime nowUtc, RollupAvailability rollups)
    {
        if (rollups == RollupAvailability.None || ComposeCaggCatalog.For(sourceTable) is null)
        {
            return null;
        }

        TimeSpan retained;
        string tierName;
        switch (route.Tier)
        {
            case ComposeSourceTier.Raw:
                retained = TimescaleSupport.RawRetentionSpan;
                tierName = "raw";
                break;
            case ComposeSourceTier.Hourly:
                retained = TimescaleSupport.HourlyRetentionSpan;
                tierName = "hourly rollup";
                break;
            default: /* Daily — kept indefinitely; every window fits. */
                return null;
        }

        if (windowStartUtc >= nowUtc - retained)
        {
            return null;
        }

        var windowDays = (nowUtc - windowStartUtc).TotalDays;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"partial window: this panel read the {tierName} tier, which this store keeps for about {retained.TotalDays:0} days, but the requested window starts {windowDays:0.#} days back — older points are not included.");
    }
}
