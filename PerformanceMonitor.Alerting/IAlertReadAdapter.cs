/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PerformanceMonitor.Alerting;

/// <summary>
/// The per-store read surface behind the Phase-5 shared alert engine (slice B) — every collected
/// feed the engine sweep evaluates, so the engine itself (slice D) is store-free. Each method
/// mirrors the semantics and filters of Lite's alert-loop reads EXACTLY (windows, LIMITs, wait-type
/// lists, latest-snapshot-only scoping); implementations over other stores (Darling's Postgres)
/// must reproduce them query-for-query so the same server state produces the same alerts.
/// <para>
/// <c>serverKey</c> is the engine's stable per-server identity, matching
/// <see cref="IAlertStateStore"/>'s convention (Lite/Darling: the deterministic storage-name hash
/// rendered as a string; Dashboard, if it ever converges: its own id) — the engine carries ONE
/// identity type across all three seams.
/// </para>
/// <para>
/// The failed-jobs feed is deliberately NOT part of this adapter: failure outcomes are not in any
/// collected table — hosts run <see cref="FailedJobsQuery"/> live against the monitored server's
/// msdb at alert-check time, on their own connections with their own permission gating (see the
/// <see cref="FailedJobsQuery"/> remarks). A collected-store read surface cannot serve it.
/// </para>
/// </summary>
public interface IAlertReadAdapter
{
    /// <summary>
    /// Recent blocked-process events for the blocking alert, newest first, capped at 200.
    /// CONTRACT: the result INCLUDES the always-on DMV blocking-snapshot fallback rows, merged with
    /// XE blocked-process-report rows preferred — a DMV row appears only where no BPR row covers
    /// the same (blocked, blocker) SPID pair within the same minute (AWS RDS / unset
    /// blocked-process threshold capture nothing via XE). Implementations reproduce Lite's merge
    /// via <see cref="BlockedProcessReportMerge.AppendDmvFallbackRows"/>; DMV rows carry
    /// <see cref="BlockedProcessAlertRow.DmvSnapshotSource"/> and have no report XML.
    /// </summary>
    Task<List<BlockedProcessAlertRow>> GetRecentBlockedProcessReportsAsync(
        string serverKey, int hoursBack, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recent deadlock events for the deadlock alert, newest (by deadlock time) first, capped
    /// at 50. Excluded-database filtering happens in the builder
    /// (<see cref="AlertContextBuilders.IsDeadlockExcluded"/> parses the graph XML), not here.
    /// </summary>
    Task<List<DeadlockAlertRow>> GetRecentDeadlocksAsync(
        string serverKey, int hoursBack, CancellationToken cancellationToken = default);

    /// <summary>
    /// The poison-wait deltas at or above <paramref name="thresholdMs"/> average ms/wait. Mirrors
    /// Lite's read exactly: the newest wait_stats rows (max 3, collected within the last 10
    /// minutes) for THREADPOOL / RESOURCE_SEMAPHORE / RESOURCE_SEMAPHORE_QUERY_COMPILE with
    /// delta_waiting_tasks &gt; 0, THEN the threshold filter applied client-side — the row window
    /// is selected BEFORE thresholding, so a sub-threshold poison wait still occupies its slot,
    /// exactly like the pre-extraction loop's fetch-then-FindAll.
    /// </summary>
    Task<List<PoisonWaitDelta>> GetPoisonWaitDeltasAsync(
        string serverKey, double thresholdMs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Currently-running queries over <paramref name="thresholdMinutes"/> elapsed, longest first,
    /// from the LATEST collection snapshot only (and only if that snapshot is under 10 minutes
    /// old — a stale store must not alert). Mirrors Lite's read exactly: user sessions only
    /// (session_id &gt; 50), the five opt-out noise filters, capped at
    /// <paramref name="maxResults"/> (clamped 1–1000), then rows in
    /// <paramref name="excludedDatabases"/> dropped client-side (case-insensitive; rows with no
    /// database name always pass) — the loop's post-fetch exclusion, moved behind the seam.
    /// </summary>
    Task<List<LongRunningQueryInfo>> GetLongRunningQueriesAsync(
        string serverKey,
        int thresholdMinutes,
        int maxResults,
        bool excludeSpServerDiagnostics,
        bool excludeWaitFor,
        bool excludeBackups,
        bool excludeMiscWaits,
        bool excludeCdc,
        IReadOnlyList<string> excludedDatabases,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The latest free space per distinct volume (mount point), worst (lowest free %) first, for
    /// the low-disk alert. Files on the same volume share one row (MAX total / MIN free); volumes
    /// with no mount point are excluded, so Azure SQL DB yields an empty list. Threshold
    /// evaluation stays engine-side (<see cref="AlertContextBuilders.GetBreachedVolumes"/>).
    /// </summary>
    Task<List<VolumeFreeSpaceInfo>> GetVolumeFreeSpaceAsync(
        string serverKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// The latest tempdb space snapshot, or null when the store has none for this server.
    /// Threshold evaluation (UsedPercent) stays engine-side.
    /// </summary>
    Task<TempDbSpaceInfo?> GetTempDbSpaceAsync(
        string serverKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Currently-running Agent jobs whose duration is at least <paramref name="multiplier"/>x
    /// their historical average, worst (highest % of average) first, capped at 5, from the LATEST
    /// running_jobs snapshot only. Jobs averaging under 60 seconds are excluded (noise floor) —
    /// mirroring Lite's read exactly.
    ///
    /// <para>#1812: the latest snapshot is only evidence when it is FRESH. Implementations compare
    /// MAX(collection_time) against <see cref="AnomalousJobsResult.MaxSnapshotAge"/> at the server's
    /// effective running_jobs cadence, and return <see cref="AnomalousJobsResult.Stale"/> (no rows,
    /// not fresh) when the snapshot is older — a stopped collector, missed cycles, lost msdb access,
    /// or a store reset must not let a historical snapshot read as NOW.</para>
    /// </summary>
    Task<AnomalousJobsResult> GetAnomalousJobsAsync(
        string serverKey, int multiplier, CancellationToken cancellationToken = default);
}
