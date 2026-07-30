/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

namespace PerformanceMonitor.Alerting;

/// <summary>
/// The CURRENT total blocked wait time for a server (#1839) — the sum of <c>wait_time_ms</c> across
/// every row of the LATEST <c>dmv_blocking_snapshots</c> snapshot, with that snapshot's identity and
/// evidence quality attached.
/// <para>
/// Deliberately a SNAPSHOT sum, not a rolling-window sum: the reported need is "how much aggregate
/// blocked wait is happening RIGHT NOW", so the alert is level-triggered on one snapshot's total and
/// clears when the next snapshot drops below the threshold. Summing a window would instead accumulate
/// historical blocking that has already ended and could never clear inside the window.
/// </para>
/// <para>
/// <paramref name="SnapshotIsFresh"/> is FALSE when the newest snapshot is older than
/// <see cref="MaxSnapshotAge"/> — the #1812 rule the running-jobs read established, for the same
/// reason: a stopped collector, missed cycles, or lost DMV access leaves a "latest" snapshot that
/// would otherwise read as NOW, and a level-triggered alert on frozen rows would hold itself active
/// and re-fire every cooldown forever. Unlike <see cref="AnomalousJobsResult"/> — which must not
/// fabricate a resolution off staleness because its rows are per-run edges — a stale snapshot here
/// RESOLVES an active alert: the engine has no current evidence that the server is still blocked, and
/// leaving a level-triggered condition latched on data of unknown age is the worse failure. The
/// numbers are still carried on a stale result so a host can log what it declined to act on.
/// </para>
/// </summary>
/// <param name="SnapshotTime">The latest snapshot's collection time (naive UTC, as stored).</param>
/// <param name="TotalWaitMs">Sum of <c>wait_time_ms</c> over that snapshot's rows.</param>
/// <param name="BlockedSessionCount">Distinct blocked SPIDs in that snapshot — the alert text's "across N blocked session(s)".</param>
/// <param name="SnapshotIsFresh">False when the snapshot is older than <see cref="MaxSnapshotAge"/>; see remarks.</param>
public sealed record CurrentBlockingWaitResult(
    DateTime SnapshotTime,
    long TotalWaitMs,
    int BlockedSessionCount,
    bool SnapshotIsFresh)
{
    /// <summary>
    /// How old the newest blocking snapshot may be and still count as CURRENT — the
    /// <see cref="AnomalousJobsResult.MaxSnapshotAge"/> rule reused verbatim (three missed collection
    /// cycles at the server's effective cadence, floored at 10 minutes) so the two freshness bounds
    /// cannot drift apart. The shipped <c>dmv_blocking_snapshot</c> cadence is 1 minute, so the floor
    /// is what applies by default.
    /// </summary>
    public static TimeSpan MaxSnapshotAge(int cadenceMinutes) =>
        AnomalousJobsResult.MaxSnapshotAge(cadenceMinutes);

    /// <summary>Total blocked wait expressed in SECONDS — what the alert compares and reports.</summary>
    public double TotalWaitSeconds => TotalWaitMs / 1000.0;
}
