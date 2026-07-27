/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitor.Alerting;

/// <summary>
/// The Phase-5 shared alert engine (slice D): Lite's <c>MainWindow.CheckPerformanceAlerts</c>
/// TRANSPLANTED behind the three seams — <see cref="IAlertEngineSettings"/> (thresholds),
/// <see cref="IAlertReadAdapter"/> (collected feeds), <see cref="IAlertStateStore"/>
/// (restart-surviving watermarks) — with every fired alert emitted through
/// <see cref="IAlertDeliverer"/>. Same alert order, same gating order per alert
/// (enabled flag → data fetch → threshold compare → edge-trigger state → mute check → cooldown →
/// deliver → record state), same edge-trigger/cooldown/mute semantics, line-cited per check.
/// The headless Darling service is the first consumer; Lite forwards in a later slice.
/// <para>
/// Deliberate NON-transplants (UI-coupled Lite behavior that stays app-side):
/// tray toast RENDERING and the <c>_trayService</c> null gate (the per-metric toast BODY ships as
/// <see cref="AlertOutcome.ShortMessage"/> because it needs per-row data the other display fields
/// don't carry); the server-tab badge flags (#754/#749 <c>_badgeLowDisk</c>/<c>_badgeFailedJob</c>
/// + acknowledgement clearing — the two standing conditions they derive from are surfaced on the
/// returned <see cref="AlertSweepResult"/>); the #1141 Summary-vs-Per-event delivery split (an
/// <see cref="IAlertDeliverer"/> concern per its contract); and the #1236 per-server delivery-mode
/// override (same seam). Lite's tray-only "Resolved"/"Cleared" toasts surface through the optional
/// resolution callback (<see cref="AlertResolution"/>) with Lite's exact strings — they never
/// touch the deliverer because Lite records no history row for them. Line citations per check
/// refer to the pre-forwarding Lite loop (the transplant source, retrievable from git history).
/// </para>
/// <para>
/// Two documented adaptations of the store reads: (1) Lite's loop received precomputed rolling
/// blocking/deadlock counts from its overview summary query; the engine derives them from ONE
/// adapter fetch instead (blocking keeps Lite's XE-preferred count semantics — the XE row count,
/// falling back to the merged count when zero XE rows — and the fetched rows then serve the
/// excluded-database recount and the fired alert's context, so the numbers can't disagree within
/// a sweep). Counts therefore inherit the adapter caps (200/50) and the deadlock read's
/// collection-time window. (2) When the blocking/deadlock fetch itself fails, the engine skips
/// that check for the sweep (state untouched) — mirroring the try/catch-and-move-on shape of
/// Lite's other checks — rather than running the gate against a fabricated zero count, which
/// would reset the watermark and later re-fire.
/// </para>
/// <para>
/// THREAD-SAFETY: the engine is a long-lived singleton per host. Evaluations for the SAME server
/// are serialized internally (per-key gate), so a host that overlaps sweeps cannot interleave one
/// server's state updates; DIFFERENT servers may evaluate concurrently (all state lives in
/// concurrent dictionaries). Hosts should still call sequentially per server — the gate is a
/// guarantee, not an invitation.
/// </para>
/// </summary>
public sealed class AlertEngine
{
    /* The persisted-watermark row keys (#1145) — Lite's MainWindow.xaml.cs:111-112 constants,
       shared so Lite's existing config_edge_trigger_watermarks rows seed this engine unchanged. */
    public const string BlockingWatermarkMetric = "Blocking Detected";
    public const string DeadlockWatermarkMetric = "Deadlocks Detected";

    private readonly IAlertEngineSettings _settings;
    private readonly IAlertReadAdapter _readAdapter;
    private readonly IAlertStateStore _stateStore;
    private readonly IAlertDeliverer _deliverer;
    private readonly Func<AlertMuteContext, bool> _isAlertMuted;
    private readonly Func<string, int, CancellationToken, Task<List<FailedJobInfo>>>? _failedJobsFetcher;
    private readonly Func<AlertResolution, CancellationToken, Task>? _resolutionCallback;
    private readonly ILogger? _logger;
    private readonly Func<DateTime> _utcNow;

    /* Per-serverKey evaluation gate: serializes EvaluateServerAsync for the SAME server. */
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _serverGates = new();

    /* One-time per-serverKey watermark seeding from the state store (#1145) — the per-key twin of
       Lite's bulk SeedEdgeTriggerWatermarksAsync (MainWindow.xaml.cs:1563). */
    private readonly ConcurrentDictionary<string, bool> _seededServerKeys = new();

    /* Cooldown timestamps — Lite's MainWindow.xaml.cs:56-63,90 dictionaries, keyed serverKey.
       In-memory only, exactly like Lite (the restart protection is the persisted watermarks plus
       the deliverer's own email/webhook cooldown seeds, not these). */
    private readonly ConcurrentDictionary<string, DateTime> _lastCpuAlert = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastBlockingAlert = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastDeadlockAlert = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastPoisonWaitAlert = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastLongRunningQueryAlert = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastTempDbSpaceAlert = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastLowDiskAlert = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastFailedJobAlert = new();

    /* Keyed per job *run* ({serverKey}:{jobId}:{startTime:O}) so it grows without bound; stale
       entries are pruned each pass — Lite's MainWindow.xaml.cs:63 + AlertEngine.cs:564-573. */
    private readonly ConcurrentDictionary<string, DateTime> _lastLongRunningJobAlert = new();

    /* Active-condition flags driving the resolved/cleared transitions —
       Lite's MainWindow.xaml.cs:78-89. */
    private readonly ConcurrentDictionary<string, bool> _activeCpuAlert = new();
    private readonly ConcurrentDictionary<string, bool> _activeBlockingAlert = new();
    private readonly ConcurrentDictionary<string, bool> _activeDeadlockAlert = new();
    private readonly ConcurrentDictionary<string, bool> _activePoisonWaitAlert = new();
    private readonly ConcurrentDictionary<string, bool> _activeLongRunningQueryAlert = new();
    private readonly ConcurrentDictionary<string, bool> _activeTempDbSpaceAlert = new();
    private readonly ConcurrentDictionary<string, bool> _activeLowDiskAlert = new();
    private readonly ConcurrentDictionary<string, bool> _activeLongRunningJobAlert = new();

    /* Worst free-% captured at the last low-disk alert (#754 follow-up) — Lite's
       MainWindow.xaml.cs:88; gated by LowDiskAlertGate; removed on resolve. */
    private readonly ConcurrentDictionary<string, double> _lastAlertedLowDiskPercent = new();

    /* Rolling-count edge-trigger watermarks (#1091) — Lite's MainWindow.xaml.cs:103-104;
       persisted through IAlertStateStore on change (#1145). */
    private readonly ConcurrentDictionary<string, int> _lastAlertedBlockingCount = new();
    private readonly ConcurrentDictionary<string, int> _lastAlertedDeadlockCount = new();

    /* Newest already-alerted failed-job run time (SERVER-LOCAL) — Lite's MainWindow.xaml.cs:96;
       persisted through IAlertStateStore on change (#1145 parity). */
    private readonly ConcurrentDictionary<string, DateTime> _lastAlertedFailedJobTime = new();

    /// <param name="settings">Live threshold surface — read every sweep, never cached.</param>
    /// <param name="readAdapter">The collected alert feeds (slice B seam).</param>
    /// <param name="stateStore">Restart-surviving watermark persistence (#1145).</param>
    /// <param name="deliverer">Record-and-send seam — the engine never touches SMTP/history itself.</param>
    /// <param name="isAlertMuted">
    /// Mute check — Lite/Darling pass <c>MuteRuleService.IsAlertMuted</c>. A muted alert is still
    /// delivered to the deliverer (flagged <see cref="AlertOutcome.Muted"/>) so the host records
    /// it without sending, exactly Lite's flow.
    /// </param>
    /// <param name="failedJobsFetcher">
    /// The live msdb failed-jobs feed (serverKey, lookbackMinutes, ct) — NOT a collected read, so
    /// it stays host-supplied: hosts run <see cref="FailedJobsQuery"/> on their own connections
    /// and degrade failures to an empty list. Null disables the failed-jobs check entirely.
    /// </param>
    /// <param name="resolutionCallback">
    /// Optional condition-recovered hook (see <see cref="AlertResolution"/>). Null = resolutions
    /// are tracked but not reported (state transitions still occur).
    /// </param>
    /// <param name="logger">Optional diagnostics logger.</param>
    /// <param name="utcNow">Test seam for the cooldown clock; production leaves it null (UtcNow).</param>
    public AlertEngine(
        IAlertEngineSettings settings,
        IAlertReadAdapter readAdapter,
        IAlertStateStore stateStore,
        IAlertDeliverer deliverer,
        Func<AlertMuteContext, bool> isAlertMuted,
        Func<string, int, CancellationToken, Task<List<FailedJobInfo>>>? failedJobsFetcher = null,
        Func<AlertResolution, CancellationToken, Task>? resolutionCallback = null,
        ILogger? logger = null,
        Func<DateTime>? utcNow = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _readAdapter = readAdapter ?? throw new ArgumentNullException(nameof(readAdapter));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _deliverer = deliverer ?? throw new ArgumentNullException(nameof(deliverer));
        _isAlertMuted = isAlertMuted ?? throw new ArgumentNullException(nameof(isAlertMuted));
        _failedJobsFetcher = failedJobsFetcher;
        _resolutionCallback = resolutionCallback;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Runs one full alert sweep for one server — Lite's <c>CheckPerformanceAlerts(summary)</c>.
    /// Per-server serialized (see class remarks). Channel/store failures never escape (the
    /// deliverer and state store contracts absorb them; per-check fetch failures are logged and
    /// skip that check for the sweep); only cancellation propagates. Returns what the sweep
    /// OBSERVED (see <see cref="AlertSweepResult"/>) so interactive hosts can drive their
    /// standing-condition badges; headless hosts ignore the result.
    /// </summary>
    public async Task<AlertSweepResult> EvaluateServerAsync(AlertServerSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        /* Master switch — Lite's AlertEngine.cs:38 (the _trayService null gate is UI-only).
           NotEvaluated mirrors Lite's early return: the host leaves badge state untouched. */
        if (!_settings.AlertsEnabled)
        {
            return AlertSweepResult.NotEvaluated;
        }

        var gate = _serverGates.GetOrAdd(snapshot.ServerKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await EvaluateCoreAsync(snapshot, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AlertSweepResult> EvaluateCoreAsync(AlertServerSnapshot snapshot, CancellationToken ct)
    {
        var key = snapshot.ServerKey;
        var serverName = snapshot.ServerName;
        var now = _utcNow();                                                        /* Lite AlertEngine.cs:41 */
        var alertCooldown = TimeSpan.FromMinutes(_settings.CooldownMinutes);        /* :57 */
        bool suppressed = snapshot.Suppressed;                                      /* :60 (suppressPopups) */

        await EnsureWatermarksSeededAsync(key, ct);

        await CheckCpuAsync(snapshot, key, serverName, now, alertCooldown, suppressed, ct);
        await CheckBlockingAsync(key, serverName, now, alertCooldown, suppressed, ct);
        await CheckDeadlocksAsync(key, serverName, now, alertCooldown, suppressed, ct);
        await CheckPoisonWaitsAsync(key, serverName, now, alertCooldown, suppressed, ct);
        await CheckLongRunningQueriesAsync(key, serverName, now, alertCooldown, suppressed, ct);
        await CheckTempDbSpaceAsync(key, serverName, now, alertCooldown, suppressed, ct);
        bool lowDiskConditionPresent = await CheckLowDiskAsync(key, serverName, now, alertCooldown, suppressed, ct);
        await CheckAnomalousJobsAsync(key, serverName, now, alertCooldown, suppressed, ct);
        bool failedJobConditionPresent = await CheckFailedJobsAsync(snapshot, key, serverName, now, alertCooldown, suppressed, ct);

        return new AlertSweepResult(true, lowDiskConditionPresent, failedJobConditionPresent);
    }

    /* ---------------- watermark seeding (#1145) ---------------- */

    /// <summary>
    /// Per-key twin of Lite's startup <c>SeedEdgeTriggerWatermarksAsync</c>
    /// (MainWindow.xaml.cs:1563-1594): loads the persisted blocking/deadlock count watermarks and
    /// the failed-job time watermark before this server's first sweep, so a host restart doesn't
    /// re-fire (and re-post webhooks for) events still lingering in the rolling window. Seeded
    /// once per key; a seed failure logs and proceeds unseeded, exactly like Lite.
    /// </summary>
    private async Task EnsureWatermarksSeededAsync(string key, CancellationToken ct)
    {
        if (_seededServerKeys.ContainsKey(key))
        {
            return;
        }

        try
        {
            var blocking = await _stateStore.LoadEdgeTriggerWatermarkAsync(key, BlockingWatermarkMetric);
            if (blocking.HasValue)
            {
                _lastAlertedBlockingCount[key] = blocking.Value;
            }

            var deadlock = await _stateStore.LoadEdgeTriggerWatermarkAsync(key, DeadlockWatermarkMetric);
            if (deadlock.HasValue)
            {
                _lastAlertedDeadlockCount[key] = deadlock.Value;
            }

            var failedJob = await _stateStore.LoadFailedJobWatermarkAsync(key);
            if (failedJob.HasValue)
            {
                _lastAlertedFailedJobTime[key] = failedJob.Value;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("Failed to seed edge-trigger watermarks for {ServerKey}: {Message}", key, ex.Message);
        }

        _seededServerKeys[key] = true;
    }

    /* ---------------- CPU (Lite AlertEngine.cs:62-114) ---------------- */

    private async Task CheckCpuAsync(
        AlertServerSnapshot snapshot, string key, string serverName,
        DateTime now, TimeSpan alertCooldown, bool suppressed, CancellationToken ct)
    {
        /* Mode selection INSIDE the engine — ServerSummaryItem.CpuPercentForAlert semantics
           (Lite LocalDataService.Overview.cs:143-144): Total → TotalCpuPercent ?? CpuPercent;
           SqlOnly → CpuPercent. */
        var alertCpuValue = _settings.CpuAlertMode == CpuAlertMode.TotalServer
            ? (snapshot.TotalCpuPercent ?? snapshot.SqlCpuPercent)
            : snapshot.SqlCpuPercent;
        string cpuMetricLabel = _settings.CpuAlertMode == CpuAlertMode.TotalServer ? "Total CPU" : "SQL CPU"; /* :64 */
        bool cpuExceeded = _settings.CpuEnabled
            && alertCpuValue.HasValue
            && alertCpuValue.Value >= _settings.CpuThresholdPercent;                /* :65-67 */

        if (cpuExceeded)
        {
            _activeCpuAlert[key] = true;                                            /* :71 */
            if (!suppressed && CooldownElapsed(_lastCpuAlert, key, now, alertCooldown)) /* :72 */
            {
                var muteCtx = new AlertMuteContext { ServerName = serverName, MetricName = "High CPU" }; /* :74 */
                bool isMuted = _isAlertMuted(muteCtx);                              /* :75 */
                _lastCpuAlert[key] = now;                                           /* :76 — stamped even when muted */

                var cpuDetailText = $"  {cpuMetricLabel}: {alertCpuValue:F0}%\n  Threshold: {_settings.CpuThresholdPercent}%"; /* :89 */

                /* :91-98 — CPU passes no context and no numerics, exactly Lite.
                   ShortMessage = the toast body of :84 minus the server-name prefix. */
                await FireAsync(new AlertOutcome(
                    key, serverName, "High CPU",
                    $"{alertCpuValue:F0}% ({cpuMetricLabel})",
                    $"{_settings.CpuThresholdPercent}%",
                    Context: null, DetailText: cpuDetailText,
                    NumericCurrentValue: null, NumericThresholdValue: null,
                    Muted: isMuted, Severity: null,
                    ShortMessage: $"{cpuMetricLabel} at {alertCpuValue:F0}% (threshold: {_settings.CpuThresholdPercent}%)"), ct);
            }
        }
        else if (_activeCpuAlert.TryGetValue(key, out var wasCpu) && wasCpu)        /* :101 */
        {
            _activeCpuAlert[key] = false;                                           /* :103 */
            /* :107 — resolve announced only while the alert is still enabled and unsuppressed
               (disabling flips cpuExceeded false; neither means CPU actually recovered). */
            if (!suppressed && _settings.CpuEnabled)
            {
                await NotifyResolutionAsync(new AlertResolution(
                    key, serverName, "High CPU",
                    "CPU Resolved",                                                 /* :110 */
                    $"{serverName}: {cpuMetricLabel} back to {alertCpuValue:F0}%"), ct); /* :111 */
            }
        }
    }

    /* ---------------- blocking (Lite AlertEngine.cs:116-194) ---------------- */

    private async Task CheckBlockingAsync(
        string key, string serverName, DateTime now, TimeSpan alertCooldown, bool suppressed, CancellationToken ct)
    {
        List<BlockedProcessAlertRow>? blockingRows = null;
        int effectiveBlockingCount = 0;

        if (_settings.BlockingEnabled)
        {
            try
            {
                /* ONE fetch serves the rolling count, the excluded-database recount (:118-133),
                   and the fired alert's context (:172) — see class remarks adaptation (1). */
                blockingRows = await _readAdapter.GetRecentBlockedProcessReportsAsync(key, hoursBack: 1, ct);

                /* Lite's overview count semantics (LocalDataService.Overview.cs:74-77): prefer the
                   XE blocked-process-report count; fall back to the DMV snapshot count when the XE
                   count is zero (AWS RDS / unset blocked-process threshold). The merged adapter
                   list contains all XE rows plus only uncovered DMV rows, so when no XE row exists
                   the merged count IS the DMV count. */
                int xeCount = blockingRows.Count(r => r.Source == BlockedProcessAlertRow.XeReportSource);
                effectiveBlockingCount = xeCount > 0 ? xeCount : blockingRows.Count;

                /* :118-127 — with excluded databases configured and the raw count at/over the
                   threshold, recount only rows outside the excluded set (no-database rows pass). */
                if (_settings.ExcludedDatabases.Count > 0
                    && effectiveBlockingCount >= _settings.BlockingCountThreshold)
                {
                    effectiveBlockingCount = blockingRows
                        .Count(r => string.IsNullOrEmpty(r.DatabaseName) ||
                            !_settings.ExcludedDatabases.Any(e =>
                                string.Equals(e, r.DatabaseName, StringComparison.OrdinalIgnoreCase)));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                /* :129-132 shape — log and skip this check for the sweep (class remarks
                   adaptation (2)): never run the gate on a fabricated zero count. */
                _logger?.LogError("Failed to check blocking for {Server}: {Message}", serverName, ex.Message);
                return;
            }
        }

        /* Edge-trigger the rolling 1-hour count (#1091) — :135-150. */
        int blockingWatermark = _lastAlertedBlockingCount.TryGetValue(key, out var labc) ? labc : 0; /* :138 */
        bool blockingCooldownElapsed = CooldownElapsed(_lastBlockingAlert, key, now, alertCooldown); /* :139 */
        var blockingDecision = _settings.BlockingEnabled
            ? RollingCountAlertGate.Evaluate(effectiveBlockingCount, _settings.BlockingCountThreshold, blockingWatermark, blockingCooldownElapsed, suppressed)
            : new RollingCountAlertGate.Decision(false, false, 0);                  /* :140-142 */
        _lastAlertedBlockingCount[key] = blockingDecision.Watermark;                /* :143 */
        if (blockingDecision.Watermark != blockingWatermark)                        /* :147 — persist on change (#1145) */
        {
            await _stateStore.SaveEdgeTriggerWatermarkAsync(key, BlockingWatermarkMetric, blockingDecision.Watermark); /* :149 */
        }

        bool wasBlockingActive = _activeBlockingAlert.TryGetValue(key, out var wasBlocking) && wasBlocking; /* :152 */
        _activeBlockingAlert[key] = blockingDecision.Active;                        /* :153 */

        if (blockingDecision.Fire)                                                  /* :155 */
        {
            var muteCtx = new AlertMuteContext { ServerName = serverName, MetricName = "Blocking Detected" }; /* :157 */
            bool isMuted = _isAlertMuted(muteCtx);                                  /* :158 */
            _lastBlockingAlert[key] = now;                                          /* :159 */

            /* :172-173 — Lite's BuildBlockingContextAsync refetches the same rows; the engine
               reuses this sweep's fetch (identical query/window). */
            var blockingContext = AlertContextBuilders.BuildBlockingContext(serverName, blockingRows, _settings.ExcludedDatabases);
            var detailText = AlertContextBuilders.ContextToDetailText(blockingContext);

            /* :175-183 — SendDetectedAlertAsync's #1141/#1236 delivery-mode fan-out is an
               IAlertDeliverer concern; the engine emits one outcome. No numerics, exactly Lite.
               ShortMessage = the toast body of :167. */
            await FireAsync(new AlertOutcome(
                key, serverName, "Blocking Detected",
                effectiveBlockingCount.ToString(),
                _settings.BlockingCountThreshold.ToString(),
                blockingContext, detailText,
                NumericCurrentValue: null, NumericThresholdValue: null,
                Muted: isMuted, Severity: blockingContext?.SeverityOverride,
                ShortMessage: $"{effectiveBlockingCount} blocking session(s)"), ct);
        }
        else if (!blockingDecision.Active && wasBlockingActive)                     /* :185 */
        {
            if (!suppressed && _settings.BlockingEnabled)                           /* :187 */
            {
                await NotifyResolutionAsync(new AlertResolution(
                    key, serverName, "Blocking Detected",
                    "Blocking Cleared",                                             /* :190 */
                    $"{serverName}: No active blocking"), ct);                      /* :191 */
            }
        }
    }

    /* ---------------- deadlocks (Lite AlertEngine.cs:196-271) ---------------- */

    private async Task CheckDeadlocksAsync(
        string key, string serverName, DateTime now, TimeSpan alertCooldown, bool suppressed, CancellationToken ct)
    {
        List<DeadlockAlertRow>? deadlockRows = null;
        int effectiveDeadlockCount = 0;

        if (_settings.DeadlockEnabled)
        {
            try
            {
                /* ONE fetch serves the rolling count, the excluded-database recount (:198-211),
                   and the fired alert's context (:249) — class remarks adaptation (1). */
                deadlockRows = await _readAdapter.GetRecentDeadlocksAsync(key, hoursBack: 1, ct);
                effectiveDeadlockCount = deadlockRows.Count;

                /* :198-205 — recount excluding deadlocks whose processes ALL ran in excluded
                   databases (graph-XML parse via the shared IsDeadlockExcluded). */
                if (_settings.ExcludedDatabases.Count > 0
                    && effectiveDeadlockCount >= _settings.DeadlockCountThreshold)
                {
                    effectiveDeadlockCount = deadlockRows
                        .Count(r => !AlertContextBuilders.IsDeadlockExcluded(r, _settings.ExcludedDatabases));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                /* :207-210 shape — log and skip (class remarks adaptation (2)). */
                _logger?.LogError("Failed to check deadlocks for {Server}: {Message}", serverName, ex.Message);
                return;
            }
        }

        /* Edge-trigger the rolling 1-hour count (#1091) — :213-227. */
        int deadlockWatermark = _lastAlertedDeadlockCount.TryGetValue(key, out var ladc) ? ladc : 0; /* :216 */
        bool deadlockCooldownElapsed = CooldownElapsed(_lastDeadlockAlert, key, now, alertCooldown); /* :217 */
        var deadlockDecision = _settings.DeadlockEnabled
            ? RollingCountAlertGate.Evaluate(effectiveDeadlockCount, _settings.DeadlockCountThreshold, deadlockWatermark, deadlockCooldownElapsed, suppressed)
            : new RollingCountAlertGate.Decision(false, false, 0);                  /* :218-220 */
        _lastAlertedDeadlockCount[key] = deadlockDecision.Watermark;                /* :221 */
        if (deadlockDecision.Watermark != deadlockWatermark)                        /* :224 — persist on change (#1145) */
        {
            await _stateStore.SaveEdgeTriggerWatermarkAsync(key, DeadlockWatermarkMetric, deadlockDecision.Watermark); /* :226 */
        }

        bool wasDeadlockActive = _activeDeadlockAlert.TryGetValue(key, out var wasDeadlock) && wasDeadlock; /* :229 */
        _activeDeadlockAlert[key] = deadlockDecision.Active;                        /* :230 */

        if (deadlockDecision.Fire)                                                  /* :232 */
        {
            var muteCtx = new AlertMuteContext { ServerName = serverName, MetricName = "Deadlocks Detected" }; /* :234 */
            bool isMuted = _isAlertMuted(muteCtx);                                  /* :235 */
            _lastDeadlockAlert[key] = now;                                          /* :236 */

            /* :249-250 — context from this sweep's fetch. */
            var deadlockContext = AlertContextBuilders.BuildDeadlockContext(serverName, deadlockRows, _settings.ExcludedDatabases);
            var detailText = AlertContextBuilders.ContextToDetailText(deadlockContext);

            /* :252-260 — no numerics, exactly Lite. ShortMessage = the toast body of :244. */
            await FireAsync(new AlertOutcome(
                key, serverName, "Deadlocks Detected",
                effectiveDeadlockCount.ToString(),
                _settings.DeadlockCountThreshold.ToString(),
                deadlockContext, detailText,
                NumericCurrentValue: null, NumericThresholdValue: null,
                Muted: isMuted, Severity: deadlockContext?.SeverityOverride,
                ShortMessage: $"{effectiveDeadlockCount} deadlock(s) in the last hour"), ct);
        }
        else if (!deadlockDecision.Active && wasDeadlockActive)                     /* :262 */
        {
            if (!suppressed && _settings.DeadlockEnabled)                           /* :264 */
            {
                await NotifyResolutionAsync(new AlertResolution(
                    key, serverName, "Deadlocks Detected",
                    "Deadlocks Cleared",                                            /* :267 */
                    $"{serverName}: No deadlocks in the last hour"), ct);           /* :268 */
            }
        }
    }

    /* ---------------- poison waits (Lite AlertEngine.cs:273-339) ---------------- */

    private async Task CheckPoisonWaitsAsync(
        string key, string serverName, DateTime now, TimeSpan alertCooldown, bool suppressed, CancellationToken ct)
    {
        if (!_settings.PoisonWaitEnabled)                                           /* :274 */
        {
            return;
        }

        try
        {
            var triggered = await _readAdapter.GetPoisonWaitDeltasAsync(key, _settings.PoisonWaitThresholdMs, ct); /* :278 */

            if (triggered.Count > 0)
            {
                _activePoisonWaitAlert[key] = true;                                 /* :282 */
                if (!suppressed && CooldownElapsed(_lastPoisonWaitAlert, key, now, alertCooldown)) /* :283 */
                {
                    var worst = triggered[0];                                       /* :285 */
                    var allWaitNames = string.Join(", ", triggered.ConvertAll(w => $"{w.WaitType} ({w.AvgMsPerWait:F0}ms)")); /* :286 */

                    /* :288-293 — mute keys on the worst (highest avg ms/wait) triggered wait type;
                       same documented limitation as Lite. */
                    var muteCtx = new AlertMuteContext { ServerName = serverName, MetricName = "Poison Wait", WaitType = worst.WaitType };
                    bool isMuted = _isAlertMuted(muteCtx);
                    _lastPoisonWaitAlert[key] = now;                                /* :294 */

                    var poisonContext = AlertContextBuilders.BuildPoisonWaitContext(triggered); /* :307 */
                    var detailText = AlertContextBuilders.ContextToDetailText(poisonContext);   /* :308 */

                    /* :310-320. ShortMessage = the toast body of :302. */
                    await FireAsync(new AlertOutcome(
                        key, serverName, "Poison Wait",
                        allWaitNames,
                        $"{_settings.PoisonWaitThresholdMs}ms avg",
                        poisonContext, detailText,
                        NumericCurrentValue: worst.AvgMsPerWait,
                        NumericThresholdValue: _settings.PoisonWaitThresholdMs,
                        Muted: isMuted, Severity: poisonContext?.SeverityOverride,
                        ShortMessage: $"{worst.WaitType} avg {worst.AvgMsPerWait:F0}ms/wait"), ct);
                }
            }
            else if (_activePoisonWaitAlert.TryGetValue(key, out var wasPoisonWait) && wasPoisonWait) /* :323 */
            {
                _activePoisonWaitAlert[key] = false;                                /* :325 */
                if (!suppressed)                                                    /* :326 */
                {
                    await NotifyResolutionAsync(new AlertResolution(
                        key, serverName, "Poison Wait",
                        "Poison Waits Cleared",                                     /* :329 */
                        $"{serverName}: Poison wait avg below threshold"), ct);     /* :330 */
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("Failed to check poison waits for {Server}: {Message}", serverName, ex.Message); /* :337 */
        }
    }

    /* ---------------- long-running queries (Lite AlertEngine.cs:341-411) ---------------- */

    private async Task CheckLongRunningQueriesAsync(
        string key, string serverName, DateTime now, TimeSpan alertCooldown, bool suppressed, CancellationToken ct)
    {
        if (!_settings.LongRunningQueryEnabled)                                     /* :342 */
        {
            return;
        }

        try
        {
            var longRunning = await _readAdapter.GetLongRunningQueriesAsync(       /* :346 */
                key,
                _settings.LongRunningQueryThresholdMinutes,
                _settings.LongRunningQueryMaxResults,
                _settings.LongRunningQueryExcludeSpServerDiagnostics,
                _settings.LongRunningQueryExcludeWaitFor,
                _settings.LongRunningQueryExcludeBackups,
                _settings.LongRunningQueryExcludeMiscWaits,
                _settings.LongRunningQueryExcludeCdc,
                _settings.ExcludedDatabases,
                ct);

            if (longRunning.Count > 0)
            {
                _activeLongRunningQueryAlert[key] = true;                           /* :350 */
                if (!suppressed && CooldownElapsed(_lastLongRunningQueryAlert, key, now, alertCooldown)) /* :351 */
                {
                    var worst = longRunning[0];                                     /* :353 */
                    var elapsedMinutes = worst.ElapsedSeconds / 60;                 /* :354 — integer division, exactly Lite */
                    /* :355-356 — the query-text preview feeds ShortMessage (the toast body). */
                    var preview = AlertContextBuilders.TruncateText(worst.QueryText, 80);
                    var previewSuffix = string.IsNullOrEmpty(preview) ? "" : $" — {preview}";

                    var muteCtx = new AlertMuteContext                              /* :358-364 */
                    {
                        ServerName = serverName,
                        MetricName = "Long-Running Query",
                        DatabaseName = worst.DatabaseName,
                        QueryText = worst.QueryText
                    };
                    bool isMuted = _isAlertMuted(muteCtx);                          /* :365 */
                    _lastLongRunningQueryAlert[key] = now;                          /* :366 */

                    var lrqContext = AlertContextBuilders.BuildLongRunningQueryContext(serverName, longRunning); /* :379 */
                    var detailText = AlertContextBuilders.ContextToDetailText(lrqContext);                       /* :380 */

                    /* :382-392. ShortMessage = the toast body of :374. */
                    await FireAsync(new AlertOutcome(
                        key, serverName, "Long-Running Query",
                        $"{longRunning.Count} query(s), longest {elapsedMinutes}m",
                        $"{_settings.LongRunningQueryThresholdMinutes}m",
                        lrqContext, detailText,
                        NumericCurrentValue: elapsedMinutes,
                        NumericThresholdValue: _settings.LongRunningQueryThresholdMinutes,
                        Muted: isMuted, Severity: lrqContext?.SeverityOverride,
                        ShortMessage: $"Session #{worst.SessionId} running {elapsedMinutes}m{previewSuffix}"), ct);
                }
            }
            else if (_activeLongRunningQueryAlert.TryGetValue(key, out var wasLongRunning) && wasLongRunning) /* :395 */
            {
                _activeLongRunningQueryAlert[key] = false;                          /* :397 */
                if (!suppressed)                                                    /* :398 */
                {
                    await NotifyResolutionAsync(new AlertResolution(
                        key, serverName, "Long-Running Query",
                        "Long-Running Queries Cleared",                             /* :401 */
                        $"{serverName}: No queries over threshold"), ct);           /* :402 */
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("Failed to check long-running queries for {Server}: {Message}", serverName, ex.Message); /* :409 */
        }
    }

    /* ---------------- tempdb space (Lite AlertEngine.cs:413-473) ---------------- */

    private async Task CheckTempDbSpaceAsync(
        string key, string serverName, DateTime now, TimeSpan alertCooldown, bool suppressed, CancellationToken ct)
    {
        if (!_settings.TempDbSpaceEnabled)                                          /* :414 */
        {
            return;
        }

        try
        {
            var tempDb = await _readAdapter.GetTempDbSpaceAsync(key, ct);           /* :418 */

            if (tempDb != null && tempDb.UsedPercent >= _settings.TempDbSpaceThresholdPercent) /* :420 */
            {
                _activeTempDbSpaceAlert[key] = true;                                /* :422 */
                if (!suppressed && CooldownElapsed(_lastTempDbSpaceAlert, key, now, alertCooldown)) /* :423 */
                {
                    var muteCtx = new AlertMuteContext { ServerName = serverName, MetricName = "tempdb Space" }; /* :425 */
                    bool isMuted = _isAlertMuted(muteCtx);                          /* :426 */
                    _lastTempDbSpaceAlert[key] = now;                               /* :427 */

                    var tempDbContext = AlertContextBuilders.BuildTempDbSpaceContext(tempDb); /* :440 */
                    var detailText = AlertContextBuilders.ContextToDetailText(tempDbContext); /* :441 */

                    /* :443-453. ShortMessage = the toast body of :435. */
                    await FireAsync(new AlertOutcome(
                        key, serverName, "tempdb Space",
                        $"{tempDb.UsedPercent:F0}% used ({tempDb.TotalReservedMb:F0} MB)",
                        $"{_settings.TempDbSpaceThresholdPercent}%",
                        tempDbContext, detailText,
                        NumericCurrentValue: tempDb.UsedPercent,
                        NumericThresholdValue: _settings.TempDbSpaceThresholdPercent,
                        Muted: isMuted, Severity: tempDbContext?.SeverityOverride,
                        ShortMessage: $"tempdb {tempDb.UsedPercent:F0}% used"), ct);
                }
            }
            else if (_activeTempDbSpaceAlert.TryGetValue(key, out var wasTempDb) && wasTempDb) /* :456 */
            {
                _activeTempDbSpaceAlert[key] = false;                               /* :458 */
                if (!suppressed)                                                    /* :459 */
                {
                    var pct = tempDb != null ? $"{tempDb.UsedPercent:F0}%" : "N/A"; /* :461 */
                    await NotifyResolutionAsync(new AlertResolution(
                        key, serverName, "tempdb Space",
                        "tempdb Space Resolved",                                    /* :463 */
                        $"{serverName}: tempdb usage back to {pct}"), ct);          /* :464 */
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("Failed to check TempDB space for {Server}: {Message}", serverName, ex.Message); /* :471 */
        }
    }

    /* ---------------- volume free space (Lite AlertEngine.cs:475-555) ---------------- */

    /// <returns>
    /// True when at least one volume is breached this sweep — the standing condition Lite's #754
    /// tab badge derives from (:487 <c>curBadgeLowDisk</c>), computed BEFORE the worsening/cooldown/
    /// suppression gates. False when the check is disabled or the read failed.
    /// </returns>
    private async Task<bool> CheckLowDiskAsync(
        string key, string serverName, DateTime now, TimeSpan alertCooldown, bool suppressed, CancellationToken ct)
    {
        if (!_settings.LowDiskEnabled)                                              /* :476 */
        {
            return false;
        }

        bool conditionPresent = false;
        try
        {
            var volumes = await _readAdapter.GetVolumeFreeSpaceAsync(key, ct);      /* :480 */
            var breached = AlertContextBuilders.GetBreachedVolumes(volumes, _settings.LowDiskThresholdPercent, _settings.LowDiskThresholdGb); /* :481 */
            conditionPresent = breached.Count > 0;                                  /* :487 — feeds the sweep result */

            if (breached.Count > 0)
            {
                var worst = breached[0];                                            /* :489 */
                _activeLowDiskAlert[key] = true;                                    /* :490 */
                double? lastLowDiskPercent =
                    _lastAlertedLowDiskPercent.TryGetValue(key, out var lowDiskPct) ? lowDiskPct : (double?)null; /* :491-492 */
                /* :493-497 — #754 follow-up: notify only on a fresh or worsening breach. */
                if (!suppressed
                    && LowDiskAlertGate.ShouldAlert(worst.FreePercent, lastLowDiskPercent)
                    && CooldownElapsed(_lastLowDiskAlert, key, now, alertCooldown))
                {
                    var muteCtx = new AlertMuteContext { ServerName = serverName, MetricName = "Volume Free Space" }; /* :499 */
                    bool isMuted = _isAlertMuted(muteCtx);                          /* :500 */
                    _lastLowDiskAlert[key] = now;                                   /* :501 */
                    _lastAlertedLowDiskPercent[key] = worst.FreePercent;            /* :502 */

                    var lowDiskContext = AlertContextBuilders.BuildVolumeFreeSpaceContext(serverName, breached); /* :515 */
                    /* :516-522 — #1136: grade WARNING normally, CRITICAL when critically low. */
                    if (lowDiskContext is not null && LowDiskAlertGate.IsCriticallyLow(worst.FreePercent, worst.FreeGb))
                    {
                        lowDiskContext.SeverityOverride = AlertSeverityLevel.Critical;
                    }
                    var detailText = AlertContextBuilders.ContextToDetailText(lowDiskContext); /* :523 */

                    /* :525-535. ShortMessage = the toast body of :510. */
                    await FireAsync(new AlertOutcome(
                        key, serverName, "Volume Free Space",
                        $"{worst.MountPoint} {worst.FreePercent:F0}% free ({worst.FreeGb:F1} GB)",
                        AlertContextBuilders.FormatLowDiskThreshold(_settings.LowDiskThresholdPercent, _settings.LowDiskThresholdGb),
                        lowDiskContext, detailText,
                        NumericCurrentValue: worst.FreePercent,
                        NumericThresholdValue: _settings.LowDiskThresholdPercent,
                        Muted: isMuted, Severity: lowDiskContext?.SeverityOverride,
                        ShortMessage: $"{worst.MountPoint} {worst.FreePercent:F0}% free ({worst.FreeGb:F1} GB)"), ct);
                }
            }
            else if (_activeLowDiskAlert.TryGetValue(key, out var wasLowDisk) && wasLowDisk) /* :538 */
            {
                _activeLowDiskAlert[key] = false;                                   /* :540 */
                _lastAlertedLowDiskPercent.TryRemove(key, out _);                   /* :541 */
                if (!suppressed)                                                    /* :542 */
                {
                    await NotifyResolutionAsync(new AlertResolution(
                        key, serverName, "Volume Free Space",
                        "Volume Free Space Resolved",                               /* :545 */
                        $"{serverName}: All volumes back above threshold"), ct);    /* :546 */
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("Failed to check volume free space for {Server}: {Message}", serverName, ex.Message); /* :553 */
        }

        return conditionPresent;
    }

    /* ---------------- anomalous Agent jobs (Lite AlertEngine.cs:557-632) ---------------- */

    private async Task CheckAnomalousJobsAsync(
        string key, string serverName, DateTime now, TimeSpan alertCooldown, bool suppressed, CancellationToken ct)
    {
        if (!_settings.LongRunningJobEnabled)                                       /* :558 */
        {
            return;
        }

        try
        {
            var anomalousJobs = await _readAdapter.GetAnomalousJobsAsync(key, _settings.LongRunningJobMultiplier, ct); /* :562 */

            /* :564-573 — the per-run cooldown dict grows without bound; drop entries aged past
               the cooldown each pass (scans ALL servers' entries, exactly like Lite). */
            foreach (var staleJobKey in _lastLongRunningJobAlert
                         .Where(kv => now - kv.Value >= alertCooldown)
                         .Select(kv => kv.Key)
                         .ToList())
            {
                _lastLongRunningJobAlert.TryRemove(staleJobKey, out _);
            }

            if (anomalousJobs.Count > 0)
            {
                _activeLongRunningJobAlert[key] = true;                             /* :577 */
                var worst = anomalousJobs[0];                                       /* :578 */
                var jobKey = $"{key}:{worst.JobId}:{worst.StartTime:O}";            /* :579 */

                if (!suppressed && (!_lastLongRunningJobAlert.TryGetValue(jobKey, out var lastJob) || now - lastJob >= alertCooldown)) /* :581 */
                {
                    var currentMinutes = worst.CurrentDurationSeconds / 60;         /* :583 — feeds ShortMessage (the toast body) */
                    var muteCtx = new AlertMuteContext { ServerName = serverName, MetricName = "Long-Running Job", JobName = worst.JobName }; /* :585 */
                    bool isMuted = _isAlertMuted(muteCtx);                          /* :586 */
                    _lastLongRunningJobAlert[jobKey] = now;                         /* :587 */

                    var jobContext = AlertContextBuilders.BuildAnomalousJobContext(serverName, anomalousJobs); /* :600 */
                    var detailText = AlertContextBuilders.ContextToDetailText(jobContext);                     /* :601 */

                    /* :603-613. ShortMessage = the toast body of :595. */
                    await FireAsync(new AlertOutcome(
                        key, serverName, "Long-Running Job",
                        $"{anomalousJobs.Count} job(s) exceeding {_settings.LongRunningJobMultiplier}x average",
                        $"{_settings.LongRunningJobMultiplier}x historical avg",
                        jobContext, detailText,
                        NumericCurrentValue: (double)(worst.PercentOfAverage ?? 0),
                        NumericThresholdValue: _settings.LongRunningJobMultiplier * 100,
                        Muted: isMuted, Severity: jobContext?.SeverityOverride,
                        ShortMessage: $"{worst.JobName} at {worst.PercentOfAverage:F0}% of avg ({currentMinutes}m)"), ct);
                }
            }
            else if (_activeLongRunningJobAlert.TryGetValue(key, out var wasJob) && wasJob) /* :616 */
            {
                _activeLongRunningJobAlert[key] = false;                            /* :618 */
                if (!suppressed)                                                    /* :619 */
                {
                    await NotifyResolutionAsync(new AlertResolution(
                        key, serverName, "Long-Running Job",
                        "Long-Running Jobs Cleared",                                /* :622 */
                        $"{serverName}: No jobs exceeding threshold"), ct);         /* :623 */
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("Failed to check anomalous jobs for {Server}: {Message}", serverName, ex.Message); /* :630 */
        }
    }

    /* ---------------- failed Agent jobs (Lite AlertEngine.cs:634-717) ---------------- */

    /// <returns>
    /// True when the fetcher returned at least one failure in the lookback window — the standing
    /// condition Lite's #749 tab badge derives from (:663 <c>curBadgeFailedJob</c>), computed
    /// BEFORE the watermark/cooldown/suppression gates. False when the check is disabled, the
    /// server is offline/Azure SQL DB, or the fetch failed.
    /// </returns>
    private async Task<bool> CheckFailedJobsAsync(
        AlertServerSnapshot snapshot, string key, string serverName,
        DateTime now, TimeSpan alertCooldown, bool suppressed, CancellationToken ct)
    {
        if (!_settings.FailedJobEnabled || _failedJobsFetcher is null)              /* :639 */
        {
            return false;
        }

        bool conditionPresent = false;
        try
        {
            /* :649-653 — Lite gates on online + non-Azure-SQL-DB + HasMsdbAccess. The engine
               gates on the snapshot's online + IsAzureSqlDb flags; the msdb-access probe is
               deliberately NOT part of the seam (Phase-5 review F11) — hosts degrade a denied
               msdb read to an empty list inside the fetcher instead. Failures are point-in-time
               events: no "cleared" notification, watermark-dedup only (:634-638). */
            if (!snapshot.IsOnline || snapshot.IsAzureSqlDb)
            {
                return false;
            }

            var failedJobs = await _failedJobsFetcher(key, _settings.FailedJobLookbackMinutes, ct); /* :657 */
            conditionPresent = failedJobs.Count > 0;                                /* :663 — feeds the sweep result */

            if (failedJobs.Count > 0)
            {
                var newestFailure = failedJobs.Max(j => j.RunDateTime);             /* :665 */
                bool hasWatermark = _lastAlertedFailedJobTime.TryGetValue(key, out var lastFailure); /* :666 */
                bool hasNewFailure = !hasWatermark || newestFailure > lastFailure;  /* :667 */

                if (hasNewFailure && !suppressed &&
                    CooldownElapsed(_lastFailedJobAlert, key, now, alertCooldown))  /* :669-670 */
                {
                    var mostRecent = failedJobs[0]; /* ORDER BY run_datetime DESC — :672 */
                    var jobNames = string.Join(", ", failedJobs.Select(j => j.JobName).Distinct().Take(3)); /* :673 */

                    var muteCtx = new AlertMuteContext { ServerName = serverName, MetricName = "Failed Agent Job", JobName = mostRecent.JobName }; /* :675 */
                    bool isMuted = _isAlertMuted(muteCtx);                          /* :676 */
                    _lastFailedJobAlert[key] = now;                                 /* :677 */
                    _lastAlertedFailedJobTime[key] = newestFailure;                 /* :678 */
                    /* :679-682 — persist the SERVER-LOCAL watermark on-change only (#1145 parity). */
                    await _stateStore.SaveFailedJobWatermarkAsync(key, newestFailure);

                    var failedJobContext = AlertContextBuilders.BuildFailedJobContext(serverName, failedJobs); /* :695 */
                    var detailText = AlertContextBuilders.ContextToDetailText(failedJobContext);               /* :696 */

                    /* :698-708. ShortMessage = the toast body of :690. */
                    await FireAsync(new AlertOutcome(
                        key, serverName, "Failed Agent Job",
                        $"{failedJobs.Count} job failure(s) in last {_settings.FailedJobLookbackMinutes}m — {jobNames}",
                        $"last {_settings.FailedJobLookbackMinutes}m",
                        failedJobContext, detailText,
                        NumericCurrentValue: failedJobs.Count,
                        NumericThresholdValue: 0,
                        Muted: isMuted, Severity: failedJobContext?.SeverityOverride,
                        ShortMessage: $"{failedJobs.Count} job failure(s) — {jobNames}"), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("Failed to check failed jobs for {Server}: {Message}", serverName, ex.Message); /* :715 */
        }

        return conditionPresent;
    }

    /* ---------------- helpers ---------------- */

    /// <summary>Lite's per-check cooldown test: no prior fire, or the cooldown has elapsed.</summary>
    private static bool CooldownElapsed(
        ConcurrentDictionary<string, DateTime> lastFired, string key, DateTime now, TimeSpan cooldown) =>
        !lastFired.TryGetValue(key, out var last) || now - last >= cooldown;

    /// <summary>
    /// Delivers one fired alert AND logs it (#1681). Every family routes through here rather than calling
    /// the deliverer directly, so a tenth family cannot be added that silently skips the log — which is
    /// exactly how the nine below ended up firing silently while their RESOLUTIONS were logged, leaving an
    /// operator's log showing "… Cleared" with nothing before it.
    ///
    /// <para>Logged at Warning: a fired alert is by definition something wrong on a monitored server, and it
    /// has to stand out from the Information-level resolution it will eventually pair with. The wording comes
    /// from the shared <see cref="AlertFiringLog"/> so the engine, Darling's self-alerts and Lite's direct
    /// senders all read identically.</para>
    ///
    /// <para>The log happens BEFORE delivery on purpose. Delivery does I/O (SMTP, webhooks, a history-row
    /// write) and swallows its own failures, so logging afterwards would lose the record of an alert whose
    /// delivery hung or failed — and that alert is precisely the one an operator later goes looking for.</para>
    /// </summary>
    private async Task FireAsync(AlertOutcome outcome, CancellationToken ct)
    {
        _logger?.LogWarning(
            "{Line}",
            AlertFiringLog.Fired(
                outcome.ServerName,
                outcome.MetricName,
                outcome.Severity?.ToString() ?? "Warning",
                outcome.ShortMessage,
                outcome.Muted));

        await _deliverer.DeliverAsync(outcome, ct);
    }

    /// <summary>
    /// Reports a condition-recovered transition to the optional host callback. Callback failures
    /// are logged and swallowed — a broken toast/log hook must not abort the sweep.
    /// </summary>
    private async Task NotifyResolutionAsync(AlertResolution resolution, CancellationToken ct)
    {
        if (_resolutionCallback is null)
        {
            return;
        }

        try
        {
            await _resolutionCallback(resolution, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("Alert resolution callback failed for {Server} / {Metric}: {Message}",
                resolution.ServerName, resolution.MetricName, ex.Message);
        }
    }
}
