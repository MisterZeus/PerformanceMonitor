/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using PerformanceMonitor.Darling.Storage;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The viewer's cached answer to "which retention rollups does this store have?" — the availability input the
/// #1661 tier routing must consult before choosing a relation (#1664). The rollups are runtime TimescaleDB
/// setup, so a plain-PostgreSQL store never has them; routing an old window there by age alone threw 42P01 in
/// front of the user, when raw (which plain PG never drops) held the complete answer all along.
/// </summary>
public sealed partial class ViewerDataService
{
    private RollupAvailability _rollups;
    private bool _rollupsProbed;
    private DateTime _rollupProbeAtUtc;

    /// <summary>
    /// While the store reports no (or partial) rollups, re-probe at most this often — so a service upgrade
    /// that creates the continuous aggregates mid-viewer-session converges without a restart. Once every
    /// rollup exists the answer is cached for the viewer's lifetime (a created CAGG is never dropped outside
    /// the service's own reshape sweep, which recreates it in the same sweep).
    /// </summary>
    private static readonly TimeSpan RollupReprobeInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The store's rollup availability, probed lazily and cached (<see cref="RollupReprobeInterval"/>). A
    /// failed probe answers <see cref="RollupAvailability.None"/> — raw always exists, so "route everything
    /// to raw" is the never-wrong fallback. Benignly racy: concurrent tab loads may probe twice; the probe is
    /// a single catalog lookup and last-write-wins caches the same answer.
    /// </summary>
    internal async ValueTask<RollupAvailability> GetRollupAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (_rollupsProbed
            && (_rollups.AllPresent || DateTime.UtcNow - _rollupProbeAtUtc < RollupReprobeInterval))
        {
            return _rollups;
        }

        try
        {
            _rollups = await TimescaleSupport.DetectRollupsAsync(_dataSource, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* A store hiccup mid-probe must not take the tab down — raw is the safe answer, and the
               re-probe interval retries soon. */
            _rollups = RollupAvailability.None;
            _ = ex;
        }

        _rollupsProbed = true;
        _rollupProbeAtUtc = DateTime.UtcNow;
        return _rollups;
    }
}
