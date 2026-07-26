/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Alerting;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Notifications;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins Stage 4 of the Darling control plane — the SERVICE's self-alerts
/// (<see cref="DarlingSelfAlertEvaluator"/>). The pure detection (<c>IsCollectionStopped</c>) and the
/// EDGE behavior (fire once on the transition, not every sweep; write the resolution history row on
/// recovery) are tested ungated against a recording deliverer + fake history store + a controllable
/// clock; the two collection_log reads run against a real Postgres gated on <c>DARLING_TEST_PG</c>
/// (skipped otherwise), mirroring <see cref="DarlingAlertingTests"/>.
/// </summary>
[Collection("live-postgres")]
public sealed class DarlingSelfAlertTests
{
    private const int ServerId = 424242;
    private const string Key = "424242";
    private const string Name = "SELF-ALERT-SRV";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /* ---------------- fakes ---------------- */

    private sealed class FakeSettings : IAlertEngineSettings
    {
        public bool AlertsEnabled { get; set; } = true;
        public bool CpuEnabled { get; set; }
        public bool BlockingEnabled { get; set; } = true;
        public bool DeadlockEnabled { get; set; } = true;
        public bool PoisonWaitEnabled { get; set; }
        public bool LongRunningQueryEnabled { get; set; }
        public bool TempDbSpaceEnabled { get; set; }
        public bool LowDiskEnabled { get; set; }
        public bool LongRunningJobEnabled { get; set; }
        public bool FailedJobEnabled { get; set; }
        public int CpuThresholdPercent { get; set; } = 80;
        public int BlockingCountThreshold { get; set; } = 1;
        public int DeadlockCountThreshold { get; set; } = 1;
        public int PoisonWaitThresholdMs { get; set; } = 500;
        public int LongRunningQueryThresholdMinutes { get; set; } = 30;
        public int LongRunningQueryMaxResults { get; set; } = 5;
        public bool LongRunningQueryExcludeSpServerDiagnostics { get; set; } = true;
        public bool LongRunningQueryExcludeWaitFor { get; set; } = true;
        public bool LongRunningQueryExcludeBackups { get; set; } = true;
        public bool LongRunningQueryExcludeMiscWaits { get; set; } = true;
        public bool LongRunningQueryExcludeCdc { get; set; } = true;
        public int TempDbSpaceThresholdPercent { get; set; } = 80;
        public int LowDiskThresholdPercent { get; set; } = 10;
        public int LowDiskThresholdGb { get; set; } = 5;
        public int LongRunningJobMultiplier { get; set; } = 3;
        public int FailedJobLookbackMinutes { get; set; } = 60;
        public int CooldownMinutes { get; set; } = 5;
        public List<string> ExcludedDatabasesList { get; } = new();
        public IReadOnlyList<string> ExcludedDatabases => ExcludedDatabasesList;
        public CpuAlertMode CpuAlertMode { get; set; } = CpuAlertMode.TotalServer;
    }

    private sealed class RecordingDeliverer : IAlertDeliverer
    {
        public List<AlertOutcome> Outcomes { get; } = new();

        public Task DeliverAsync(AlertOutcome outcome, CancellationToken cancellationToken = default)
        {
            Outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHistoryStore : IAlertHistoryStore
    {
        public List<AlertHistoryRecord> Records { get; } = new();

        public Task RecordAlertAsync(AlertHistoryRecord record)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<DateTime?> GetLastEmailSentUtcAsync(string serverId, string metricName, string? dedupKey = null) =>
            Task.FromResult<DateTime?>(null);

        public Task<DateTime?> GetLastWebhookSentUtcAsync(string serverId, string metricName, string? dedupKey = null) =>
            Task.FromResult<DateTime?>(null);

        public Task<DateTime?> GetLastAlertTimeAsync(string serverId, string metricName) =>
            Task.FromResult<DateTime?>(null);
    }

    /// <summary>
    /// Minimal ILogger that records level + formatted message. #1681 pins that a self-alert FIRING reaches the
    /// service log, which for a long time only recoveries did.
    /// </summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>One evaluator + fakes + a controllable clock per test.</summary>
    private sealed class Harness
    {
        public FakeSettings Settings { get; } = new();
        public RecordingDeliverer Deliverer { get; } = new();
        public FakeHistoryStore History { get; } = new();
        public bool Muted { get; set; }

        /// <summary>When set, the mute check throws — simulates a broken mute rule's Matches() to prove the
        /// evaluation isolates it (a throw here must never propagate out and stop collection).</summary>
        public bool MuteThrows { get; set; }

        /// <summary>The V20 connection-change notify gate, read live by the evaluator's connect edge (default on).</summary>
        public bool NotifyConnectionChanges { get; set; } = true;

        /// <summary>#1659 opt-ins (V33), read live like the V20 gate. Defaults off = classic edge-only.</summary>
        public bool NotifyConnectionDownAtStartup { get; set; }
        public int ConnectionRefireMinutes { get; set; }

        public DateTime Now { get; set; } = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>#1681: captures what the evaluator writes to the service log, so the firing/recovery pair
        /// can be asserted rather than assumed.</summary>
        public CapturingLogger Log { get; } = new();

        public DarlingSelfAlertEvaluator Build() => new(
            Settings, Deliverer, History,
            _ => MuteThrows ? throw new InvalidOperationException("mute check boom") : Muted,
            logger: Log, utcNow: () => Now,
            notifyConnectionChanges: () => NotifyConnectionChanges,
            notifyConnectionDownAtStartup: () => NotifyConnectionDownAtStartup,
            connectionRefireMinutes: () => ConnectionRefireMinutes);
    }

    /* ---------------- collection-stopped detection (pure) ---------------- */

    [Fact]
    public void IsCollectionStopped_FreshSuccess_NotStopped()
    {
        var now = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        /* Last success two minutes ago, recent runs all succeeded — healthy. */
        Assert.False(DarlingSelfAlertEvaluator.IsCollectionStopped(now.AddMinutes(-2), 10, 10, now, out var reason));
        Assert.Equal("", reason);
    }

    [Fact]
    public void IsCollectionStopped_NoSuccessWithinStaleWindow_Stopped()
    {
        var now = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        /* Last success 45 minutes ago (>= the 30-minute window), recent runs mixed — stale => stopped. */
        Assert.True(DarlingSelfAlertEvaluator.IsCollectionStopped(now.AddMinutes(-45), 5, 3, now, out var reason));
        Assert.Contains("45 minutes", reason);
    }

    [Fact]
    public void IsCollectionStopped_ExactlyAtStaleWindow_Stopped()
    {
        var now = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        /* Boundary: elapsed == StaleWindow counts as stale (>=). */
        Assert.True(DarlingSelfAlertEvaluator.IsCollectionStopped(
            now - DarlingSelfAlertEvaluator.StaleWindow, 5, 4, now, out _));
    }

    [Fact]
    public void IsCollectionStopped_NeverRan_NotStopped()
    {
        var now = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        /* A freshly-added / never-connected server (no success ever, no runs) must NOT read as stopped —
           the connection-lost alert covers that, and a warming-up server is not "broken". */
        Assert.False(DarlingSelfAlertEvaluator.IsCollectionStopped(null, 0, 0, now, out _));
    }

    [Fact]
    public void IsCollectionStopped_ConsecutiveFailuresNoSuccessEver_Stopped()
    {
        var now = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        /* Connected but every one of the last N runs failed (never a success) => stopped fast, before the
           staleness backstop would trip. */
        Assert.True(DarlingSelfAlertEvaluator.IsCollectionStopped(
            null, DarlingSelfAlertEvaluator.ConsecutiveFailureThreshold, 0, now, out var reason));
        Assert.Contains("failed", reason);
    }

    [Fact]
    public void IsCollectionStopped_ConsecutiveFailuresButSomeSuccessInWindow_NotStopped()
    {
        var now = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        /* The last N runs include at least one success and the last success is recent — not stopped
           (a single failing collector among healthy ones must not trip the server-level alert). */
        Assert.False(DarlingSelfAlertEvaluator.IsCollectionStopped(
            now.AddMinutes(-1), DarlingSelfAlertEvaluator.ConsecutiveFailureThreshold, 2, now, out _));
    }

    [Fact]
    public void IsCollectionStopped_FewFailuresRecentSuccess_NotStopped()
    {
        var now = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        /* Fewer than the consecutive threshold and a recent success — a brief hiccup, not stopped. */
        Assert.False(DarlingSelfAlertEvaluator.IsCollectionStopped(now.AddMinutes(-1), 3, 0, now, out _));
    }

    /* ---------------- collection-stopped edge ---------------- */

    [Fact]
    public async Task CollectionStopped_FiresOnce_ThenCooldownSuppresses_ThenReFires()
    {
        var h = new Harness();
        var e = h.Build();

        await e.ApplyCollectionStoppedAsync(ServerId, Name, stopped: true, "no recent collection", Ct);
        var fired = Assert.Single(h.Deliverer.Outcomes);
        Assert.Equal("Collection Stopped", fired.MetricName);
        Assert.Equal("collecting", fired.ThresholdValue);
        Assert.Equal(AlertSeverityLevel.Critical, fired.Severity);
        Assert.Equal(Key, fired.ServerKey);

        /* Still stopped one minute later — inside the 5-minute cooldown, no re-fire (the EDGE: once,
           not every sweep). */
        h.Now = h.Now.AddMinutes(1);
        await e.ApplyCollectionStoppedAsync(ServerId, Name, stopped: true, "no recent collection", Ct);
        Assert.Single(h.Deliverer.Outcomes);

        /* After the cooldown the standing condition re-fires. */
        h.Now = h.Now.AddMinutes(5);
        await e.ApplyCollectionStoppedAsync(ServerId, Name, stopped: true, "no recent collection", Ct);
        Assert.Equal(2, h.Deliverer.Outcomes.Count);
    }

    [Fact]
    public async Task CollectionStopped_Recovery_WritesOneResumedHistoryRow()
    {
        var h = new Harness();
        var e = h.Build();

        await e.ApplyCollectionStoppedAsync(ServerId, Name, stopped: true, "no recent collection", Ct);
        Assert.Empty(h.History.Records);

        /* Recovery: exactly one "Collection Resumed" audit row, no email/webhook (it went to the history
           store, not the deliverer). */
        await e.ApplyCollectionStoppedAsync(ServerId, Name, stopped: false, "", Ct);
        var resumed = Assert.Single(h.History.Records);
        Assert.Equal("Collection Resumed", resumed.MetricName);
        Assert.True(resumed.AlertSent);
        Assert.Equal("tray", resumed.NotificationType);
        Assert.Single(h.Deliverer.Outcomes); /* only the original fire went to the deliverer */

        /* Still healthy on the next sweep — no duplicate resumed row (resolution is edge-triggered too). */
        await e.ApplyCollectionStoppedAsync(ServerId, Name, stopped: false, "", Ct);
        Assert.Single(h.History.Records);
    }

    [Fact]
    public async Task CollectionStopped_Muted_RecordedMutedNotSuppressed()
    {
        var h = new Harness { Muted = true };
        var e = h.Build();

        await e.ApplyCollectionStoppedAsync(ServerId, Name, stopped: true, "no recent collection", Ct);
        var fired = Assert.Single(h.Deliverer.Outcomes);
        Assert.True(fired.Muted); /* the deliverer skips channels but still records — same as the engine */
    }

    /* ---------------- capture-down edge ---------------- */

    [Fact]
    public async Task CaptureDown_FiresOnce_ThenRestoredHistoryRow()
    {
        var h = new Harness();
        var e = h.Build();

        await e.ApplyCaptureDownAsync(ServerId, Name, new[] { "Blocking", "Deadlock" }, Ct);
        var fired = Assert.Single(h.Deliverer.Outcomes);
        Assert.Equal("Capture Down", fired.MetricName);
        Assert.Equal("Blocking and Deadlock", fired.CurrentValue);
        Assert.Equal(AlertSeverityLevel.Critical, fired.Severity);

        /* Still down inside the cooldown — no re-fire. */
        h.Now = h.Now.AddMinutes(1);
        await e.ApplyCaptureDownAsync(ServerId, Name, new[] { "Blocking", "Deadlock" }, Ct);
        Assert.Single(h.Deliverer.Outcomes);

        /* Sessions back — one "Capture Restored" audit row. */
        await e.ApplyCaptureDownAsync(ServerId, Name, Array.Empty<string>(), Ct);
        var restored = Assert.Single(h.History.Records);
        Assert.Equal("Capture Restored", restored.MetricName);
    }

    [Fact]
    public async Task CaptureDown_BlockingAndDeadlockDisabled_DoesNotFire()
    {
        var h = new Harness();
        h.Settings.BlockingEnabled = false;
        h.Settings.DeadlockEnabled = false;
        var e = h.Build();

        await e.ApplyCaptureDownAsync(ServerId, Name, new[] { "Blocking" }, Ct);
        Assert.Empty(h.Deliverer.Outcomes);
    }

    /* ---------------- agent-not-running edge (#1433 Phase 2) ---------------- */

    [Fact]
    public async Task AgentNotRunning_FiresOnce_ThenCooldownSuppresses_ThenReFires()
    {
        var h = new Harness();
        var e = h.Build();

        await e.ApplyAgentNotRunningAsync(ServerId, Name, agentRunningFresh: false, Ct);
        var fired = Assert.Single(h.Deliverer.Outcomes);
        Assert.Equal("Agent Not Running", fired.MetricName);
        Assert.Equal("Stopped", fired.CurrentValue);
        Assert.Equal("Running", fired.ThresholdValue);
        Assert.Equal(AlertSeverityLevel.Critical, fired.Severity);
        Assert.Equal(Key, fired.ServerKey);

        /* Still stopped inside the cooldown — the EDGE: no re-fire. */
        h.Now = h.Now.AddMinutes(1);
        await e.ApplyAgentNotRunningAsync(ServerId, Name, agentRunningFresh: false, Ct);
        Assert.Single(h.Deliverer.Outcomes);

        /* After the cooldown the standing condition re-fires. */
        h.Now = h.Now.AddMinutes(5);
        await e.ApplyAgentNotRunningAsync(ServerId, Name, agentRunningFresh: false, Ct);
        Assert.Equal(2, h.Deliverer.Outcomes.Count);
    }

    [Fact]
    public async Task AgentNotRunning_Recovery_WritesOneRestartedHistoryRow()
    {
        var h = new Harness();
        var e = h.Build();

        await e.ApplyAgentNotRunningAsync(ServerId, Name, agentRunningFresh: false, Ct);
        Assert.Empty(h.History.Records);

        /* Agent back up: exactly one "Agent Restarted" audit row, no email/webhook. */
        await e.ApplyAgentNotRunningAsync(ServerId, Name, agentRunningFresh: true, Ct);
        var restarted = Assert.Single(h.History.Records);
        Assert.Equal("Agent Restarted", restarted.MetricName);
        Assert.True(restarted.AlertSent);
        Assert.Equal("tray", restarted.NotificationType);
        Assert.Single(h.Deliverer.Outcomes);

        /* Still running on the next sweep — no duplicate resolution (edge-triggered). */
        await e.ApplyAgentNotRunningAsync(ServerId, Name, agentRunningFresh: true, Ct);
        Assert.Single(h.History.Records);
    }

    [Fact]
    public async Task AgentNotRunning_NoFreshReading_DoesNotFireOrClearStandingAlert()
    {
        var h = new Harness();
        var e = h.Build();

        /* Fire on a fresh stopped reading. */
        await e.ApplyAgentNotRunningAsync(ServerId, Name, agentRunningFresh: false, Ct);
        Assert.Single(h.Deliverer.Outcomes);

        /* No fresh reading (stale snapshot / never collected) — must NEITHER clear the standing alert (no
           resolution row) NOR fire. The collection-stopped alert owns staleness. */
        h.Now = h.Now.AddMinutes(10);
        await e.ApplyAgentNotRunningAsync(ServerId, Name, agentRunningFresh: null, Ct);
        Assert.Empty(h.History.Records);

        /* The active flag persisted through the null gap: a fresh stopped reading after the cooldown re-fires
           (it would have been cleared to a first-fire if null had wrongly reset the state). */
        await e.ApplyAgentNotRunningAsync(ServerId, Name, agentRunningFresh: false, Ct);
        Assert.Equal(2, h.Deliverer.Outcomes.Count);
    }

    [Fact]
    public async Task AgentNotRunning_AlertsDisabled_DoesNotFire()
    {
        var h = new Harness();
        h.Settings.AlertsEnabled = false;
        var e = h.Build();

        await e.ApplyAgentNotRunningAsync(ServerId, Name, agentRunningFresh: false, Ct);
        Assert.Empty(h.Deliverer.Outcomes);
    }

    /* ---------------- connection lost / restored edge ---------------- */

    /* ---------------- #1659: already-down-at-first-sight + standing-outage re-fire ---------------- */

    [Fact]
    public async Task Connection_AlreadyDownAtFirstSight_OptIn_FiresOnTheFirstOutcome()
    {
        var h = new Harness { NotifyConnectionDownAtStartup = true };
        var e = h.Build();

        /* Unknown -> Offline with the opt-in: the outage is announced at first sight instead of being a
           silent baseline — the case where the service starts mid-outage and would otherwise never alert. */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "no route", Ct);

        var fired = Assert.Single(h.Deliverer.Outcomes);
        Assert.Equal("Server Unreachable", fired.MetricName);
        Assert.Contains("Already unreachable when monitoring started", fired.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connection_Refire_FiresAfterTheInterval_NotBefore_AndUnderTheSameMetricName()
    {
        var h = new Harness { ConnectionRefireMinutes = 10 };
        var e = h.Build();

        /* Establish online, then lose it: the classic edge fires once. */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "no route", Ct);
        Assert.Single(h.Deliverer.Outcomes);

        /* Inside the window: offline->offline stays quiet. */
        h.Now = h.Now.AddMinutes(5);
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "no route", Ct);
        Assert.Single(h.Deliverer.Outcomes);

        /* Past the window: the standing outage re-announces — SAME metric name, so webhook automation
           keyed on "Server Unreachable" re-triggers; the detail says it is a re-fire. */
        h.Now = h.Now.AddMinutes(6);
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "no route", Ct);
        Assert.Equal(2, h.Deliverer.Outcomes.Count);
        Assert.Equal("Server Unreachable", h.Deliverer.Outcomes[1].MetricName);
        Assert.Contains("Still unreachable", h.Deliverer.Outcomes[1].DetailText, StringComparison.Ordinal);

        /* Restore clears the re-fire clock and fires the classic restore. */
        h.Now = h.Now.AddMinutes(1);
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);
        Assert.Equal(3, h.Deliverer.Outcomes.Count);
        Assert.Equal("Server Restored", h.Deliverer.Outcomes[2].MetricName);
    }

    [Fact]
    public async Task Connection_Refire_ClockStampsOnDeliveryOnly_SoASuppressedDecisionDoesNotConsumeTheWindow()
    {
        var h = new Harness { ConnectionRefireMinutes = 10, NotifyConnectionChanges = false };
        var e = h.Build();

        /* Down while the notify toggle is OFF: state advances, nothing delivers, nothing stamps. */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "no route", Ct);
        Assert.Empty(h.Deliverer.Outcomes);

        /* Toggle on mid-outage: the very next offline->offline poll is due immediately (no recorded down
           alert to measure the window from), so the outage is announced rather than silently aged. */
        h.NotifyConnectionChanges = true;
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "no route", Ct);
        var fired = Assert.Single(h.Deliverer.Outcomes);
        Assert.Equal("Server Unreachable", fired.MetricName);
    }

    [Fact]
    public async Task Connection_FirstConnect_IsSilentBaseline()
    {
        var h = new Harness();
        var e = h.Build();

        /* Unknown -> Online on the first-ever connect: no "restored" (there was no prior loss),
           mirroring the Dashboard's skip-first-check. */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);
        Assert.Empty(h.Deliverer.Outcomes);
    }

    [Fact]
    public async Task Connection_DownAtStartup_StaysDown_NeverFires()
    {
        var h = new Harness();
        var e = h.Build();

        /* Unknown -> Offline (baseline) then Offline -> Offline: a server simply down at startup never
           pages; only a transition FROM a known-online state does. */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "no route", Ct);
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "no route", Ct);
        Assert.Empty(h.Deliverer.Outcomes);
    }

    [Fact]
    public async Task Connection_Lost_FiresOnce_RepeatedFailedReconnectDoesNotReFire()
    {
        var h = new Harness();
        var e = h.Build();

        /* Establish an online baseline (silent). */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);
        Assert.Empty(h.Deliverer.Outcomes);

        /* Online -> Offline: fire "Server Unreachable" ONCE. */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "Login timeout expired", Ct);
        var lost = Assert.Single(h.Deliverer.Outcomes);
        Assert.Equal("Server Unreachable", lost.MetricName);
        Assert.Equal(AlertSeverityLevel.Critical, lost.Severity);
        Assert.Equal("Login timeout expired", lost.CurrentValue);

        /* Offline -> Offline (the 60s retry keeps failing): the EDGE — no re-fire. */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "Login timeout expired", Ct);
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "Login timeout expired", Ct);
        Assert.Single(h.Deliverer.Outcomes);
    }

    [Fact]
    public async Task Connection_Restored_FiresOnce_AfterALoss()
    {
        var h = new Harness();
        var e = h.Build();

        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);   /* baseline */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "boom", Ct); /* lost */
        Assert.Single(h.Deliverer.Outcomes);

        /* Offline -> Online: fire "Server Restored" once (Severity null so the shared map renders it green). */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);
        Assert.Equal(2, h.Deliverer.Outcomes.Count);
        var restored = h.Deliverer.Outcomes[1];
        Assert.Equal("Server Restored", restored.MetricName);
        Assert.Null(restored.Severity);

        /* Staying online does not re-fire. */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);
        Assert.Equal(2, h.Deliverer.Outcomes.Count);
    }

    [Fact]
    public async Task Connection_Lost_NotDelivered_WhenNotifyConnectionChangesOff()
    {
        /* V20: the connection-change notify toggle gates DELIVERY of the connect edge, independently of the
           per-alert enables. Off -> even a genuine online->offline transition delivers nothing. */
        var h = new Harness { NotifyConnectionChanges = false };
        var e = h.Build();

        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);   /* baseline */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "boom", Ct); /* lost — muted by the toggle */

        Assert.Empty(h.Deliverer.Outcomes);
    }

    [Fact]
    public async Task Connection_NotifyOff_StillTracksState_SoLaterEnabledRestoreFires()
    {
        /* The gate suppresses delivery only, NOT the state machine (mirrors the master-switch posture): a loss
           missed while the toggle was off still advances the state, so flipping it back on and reconnecting
           fires "Server Restored" from the correct baseline rather than replaying nothing. */
        var h = new Harness { NotifyConnectionChanges = false };
        var e = h.Build();

        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);   /* Online baseline */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "boom", Ct); /* Offline (state advanced, no delivery) */
        Assert.Empty(h.Deliverer.Outcomes);

        h.NotifyConnectionChanges = true; /* operator re-enables the toggle */

        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);   /* Offline -> Online: restore fires */
        var restored = Assert.Single(h.Deliverer.Outcomes);
        Assert.Equal("Server Restored", restored.MetricName);
    }

    [Fact]
    public async Task Connection_Forget_ResetsToBaseline()
    {
        var h = new Harness();
        var e = h.Build();

        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);  /* Online baseline */
        e.Forget(ServerId);

        /* After Forget the next offline is a fresh Unknown->Offline baseline again — no spurious "lost". */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "gone", Ct);
        Assert.Empty(h.Deliverer.Outcomes);
    }

    [Fact]
    public async Task Connection_ThrowingMuteCheck_IsIsolated_AndStateStillAdvances()
    {
        /* The connection edge fires straight from the un-guarded sweep loop (TryConnectAsync), whose OWN catch
           re-calls this with online:false — so a throwing mute-check here (a broken rule's Matches()) would
           propagate out and stop collection for the whole fleet. The delivery portion must isolate it, and
           because the state machine advances BEFORE the fire, the edge must still transition correctly. */
        var h = new Harness();
        var e = h.Build();

        /* Online baseline (Unknown->Online is silent; no mute check reached). */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);
        Assert.Empty(h.Deliverer.Outcomes);

        /* Now the mute check throws: Online->Offline WOULD fire "Server Unreachable" -> mute throws.
           Must NOT propagate (would kill the loop), and nothing is delivered (throw precedes delivery). */
        h.MuteThrows = true;
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "boom", Ct);
        Assert.Empty(h.Deliverer.Outcomes);

        /* The state STILL advanced to Offline despite the throwing fire: with the mute check healthy again,
           Offline->Online now fires "Server Restored" — it would NOT if the state were stuck at Online. */
        h.MuteThrows = false;
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);
        var restored = Assert.Single(h.Deliverer.Outcomes);
        Assert.Equal("Server Restored", restored.MetricName);
    }

    /* ---------------- store disk pressure (fleet-level, pure decision) ---------------- */

    private const long Gib = 1024L * 1024 * 1024;

    [Fact]
    public void IsDiskPressure_BelowThreshold_Pressure()
    {
        /* 5% free (< the 10% warn threshold) reads as pressure; the reason names the percentage. */
        Assert.True(DarlingSelfAlertEvaluator.IsDiskPressure(5 * Gib, 100 * Gib, out var reason));
        Assert.Contains("5", reason);
        Assert.Contains("%", reason);
    }

    [Fact]
    public void IsDiskPressure_ExactlyAtThreshold_NotPressure()
    {
        /* Boundary: exactly 10% free is NOT pressure (strictly-less-than the threshold). */
        Assert.False(DarlingSelfAlertEvaluator.IsDiskPressure(10 * Gib, 100 * Gib, out _));
    }

    [Fact]
    public void IsDiskPressure_JustBelowThreshold_Pressure()
    {
        /* 9.9% free trips it — the threshold is a real edge, not a wide band. */
        Assert.True(DarlingSelfAlertEvaluator.IsDiskPressure(99 * Gib, 1000 * Gib, out _));
    }

    [Fact]
    public void IsDiskPressure_PlentyFree_NotPressure()
    {
        Assert.False(DarlingSelfAlertEvaluator.IsDiskPressure(50 * Gib, 100 * Gib, out _));
    }

    [Fact]
    public void IsDiskPressure_NonPositiveTotal_NotPressure()
    {
        /* An undeterminable total ("can't tell") never reads as pressure. */
        Assert.False(DarlingSelfAlertEvaluator.IsDiskPressure(0, 0, out _));
    }

    /* ---------------- store disk pressure edge ---------------- */

    [Fact]
    public async Task DiskPressure_FiresOnce_ThenCooldownSuppresses_ThenReFires()
    {
        var h = new Harness();
        var e = h.Build();

        await e.ApplyDiskPressureAsync(5 * Gib, 100 * Gib, storeSizeBytes: 20 * Gib, Ct);
        var fired = Assert.Single(h.Deliverer.Outcomes);
        Assert.Equal("Store Disk Pressure", fired.MetricName);
        Assert.Equal(AlertSeverityLevel.Critical, fired.Severity);
        Assert.Equal("store", fired.ServerKey);   /* the fleet sentinel key, not a real server_id */

        /* Still low one minute later — inside the 5-minute cooldown, no re-fire (the EDGE: once, not every sweep). */
        h.Now = h.Now.AddMinutes(1);
        await e.ApplyDiskPressureAsync(5 * Gib, 100 * Gib, null, Ct);
        Assert.Single(h.Deliverer.Outcomes);

        /* After the cooldown the standing condition re-fires. */
        h.Now = h.Now.AddMinutes(5);
        await e.ApplyDiskPressureAsync(5 * Gib, 100 * Gib, null, Ct);
        Assert.Equal(2, h.Deliverer.Outcomes.Count);
    }

    [Fact]
    public async Task DiskPressure_Recovery_WritesOneResolvedHistoryRow()
    {
        var h = new Harness();
        var e = h.Build();

        await e.ApplyDiskPressureAsync(5 * Gib, 100 * Gib, null, Ct);   /* pressure */
        Assert.Empty(h.History.Records);

        /* Free space recovered: exactly one "Store Disk Pressure Resolved" audit row, no email/webhook. */
        await e.ApplyDiskPressureAsync(50 * Gib, 100 * Gib, null, Ct);
        var resolved = Assert.Single(h.History.Records);
        Assert.Equal("Store Disk Pressure Resolved", resolved.MetricName);
        Assert.True(resolved.AlertSent);
        Assert.Equal("tray", resolved.NotificationType);
        Assert.Single(h.Deliverer.Outcomes);   /* only the original fire went to the deliverer */

        /* Still healthy on the next sweep — no duplicate resolved row (resolution is edge-triggered too). */
        await e.ApplyDiskPressureAsync(50 * Gib, 100 * Gib, null, Ct);
        Assert.Single(h.History.Records);
    }

    [Fact]
    public async Task DiskPressure_UndeterminableFreeSpace_DoesNotFire()
    {
        /* A remote BYO store whose volume the service cannot see (null free/total) never alarms — even though
           a store size is known, it is context only and never the trigger. */
        var h = new Harness();
        var e = h.Build();

        await e.ApplyDiskPressureAsync(null, null, storeSizeBytes: 999 * Gib, Ct);
        Assert.Empty(h.Deliverer.Outcomes);
    }

    [Fact]
    public async Task DiskPressure_AlertsDisabled_DoesNotFire()
    {
        var h = new Harness();
        h.Settings.AlertsEnabled = false;
        var e = h.Build();

        await e.ApplyDiskPressureAsync(1 * Gib, 100 * Gib, null, Ct);
        Assert.Empty(h.Deliverer.Outcomes);
    }

    [Fact]
    public async Task DiskPressure_ThrowingMuteCheck_IsIsolated_DoesNotPropagate()
    {
        /* MAJOR: the disk-pressure sweep-loop body has no catch-all of its own, and the pre-deliver mute check
           (_isAlertMuted -> a mute rule's Matches()) is NOT internally isolated. EvaluateDiskPressureAsync must
           swallow a throw there — otherwise a single broken mute rule stops collection for the whole fleet. */
        var h = new Harness { MuteThrows = true };
        var e = h.Build();

        /* Must NOT throw (would propagate out of the un-guarded worker loop). */
        await e.EvaluateDiskPressureAsync(5 * Gib, 100 * Gib, storeSizeBytes: 20 * Gib, Ct);

        /* The throw happened in the mute check, before delivery — nothing was delivered, and we're still alive. */
        Assert.Empty(h.Deliverer.Outcomes);

        /* The isolation lives in the Evaluate wrapper (the worker's entry point), not the sibling-style
           un-isolated Apply: a fresh evaluator's Apply lets the same throw propagate. */
        var e2 = new Harness { MuteThrows = true }.Build();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => e2.ApplyDiskPressureAsync(5 * Gib, 100 * Gib, null, Ct));
    }

    /* ---------------- compression-job self-heal (#1581) ---------------- */

    /// <summary>Records the job_ids passed to the re-arm delegate and returns a configurable success.</summary>
    private sealed class RearmRecorder
    {
        public List<long> Calls { get; } = new();
        public bool Result { get; set; } = true;
        public Func<long, Task<bool>> Delegate => id =>
        {
            Calls.Add(id);
            return Task.FromResult(Result);
        };
    }

    private static IReadOnlyList<StuckCompressionJob> Stuck(params long[] jobIds)
    {
        var list = new List<StuckCompressionJob>();
        foreach (var id in jobIds)
        {
            list.Add(new StuckCompressionJob(id, "wait_stats", "next_start is -infinity — the scheduler will never run it again"));
        }

        return list;
    }

    [Fact]
    public async Task CompressionJobs_FirstDetection_RearmsOnce_AndFiresCritical()
    {
        var h = new Harness();
        var e = h.Build();
        var rearm = new RearmRecorder();

        await e.ApplyCompressionJobsStuckAsync(Stuck(1001), rearm.Delegate, Ct);

        /* Re-armed exactly once. */
        Assert.Equal(1001L, Assert.Single(rearm.Calls));

        var fired = Assert.Single(h.Deliverer.Outcomes);
        Assert.Equal("Compression Job Stuck", fired.MetricName);
        Assert.Equal(AlertSeverityLevel.Critical, fired.Severity);
        Assert.Equal("compressjob:1001", fired.ServerKey);  /* prefixed so it never parses as a server_id */
        Assert.Contains("auto-re-armed", fired.ShortMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompressionJobs_ReHangAfterSelfHeal_Escalates_AndStopsRearming()
    {
        var h = new Harness();
        var e = h.Build();
        var rearm = new RearmRecorder();

        /* Check 1: detect + re-arm + fire. */
        await e.ApplyCompressionJobsStuckAsync(Stuck(1001), rearm.Delegate, Ct);

        /* Check 2 (an hour later): STILL stuck = a re-hang. Escalate, and do NOT re-arm again. */
        h.Now = h.Now.AddHours(1);
        await e.ApplyCompressionJobsStuckAsync(Stuck(1001), rearm.Delegate, Ct);

        Assert.Single(rearm.Calls);  /* never re-armed a second time */
        Assert.Equal(2, h.Deliverer.Outcomes.Count);
        var escalated = h.Deliverer.Outcomes[1];
        Assert.Equal(AlertSeverityLevel.Critical, escalated.Severity);
        Assert.Contains("re-hung", escalated.ShortMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompressionJobs_AfterEscalation_NeverRearms_ReFiresOnlyOnCooldown()
    {
        var h = new Harness();
        var e = h.Build();
        var rearm = new RearmRecorder();

        await e.ApplyCompressionJobsStuckAsync(Stuck(1001), rearm.Delegate, Ct);  /* detect + re-arm */
        h.Now = h.Now.AddHours(1);
        await e.ApplyCompressionJobsStuckAsync(Stuck(1001), rearm.Delegate, Ct);  /* escalate (fire) */
        Assert.Equal(2, h.Deliverer.Outcomes.Count);

        /* Inside the 5-minute cooldown after the escalation: no re-fire, no re-arm. */
        h.Now = h.Now.AddMinutes(1);
        await e.ApplyCompressionJobsStuckAsync(Stuck(1001), rearm.Delegate, Ct);
        Assert.Single(rearm.Calls);
        Assert.Equal(2, h.Deliverer.Outcomes.Count);

        /* After the cooldown: re-fires (still no re-arm). */
        h.Now = h.Now.AddMinutes(5);
        await e.ApplyCompressionJobsStuckAsync(Stuck(1001), rearm.Delegate, Ct);
        Assert.Single(rearm.Calls);
        Assert.Equal(3, h.Deliverer.Outcomes.Count);
    }

    [Fact]
    public async Task CompressionJobs_RearmFailure_EscalatesImmediately_AndNeverRetriesRearm()
    {
        var h = new Harness();
        var e = h.Build();
        var rearm = new RearmRecorder { Result = false };  /* alter_job fails (e.g. permission) */

        await e.ApplyCompressionJobsStuckAsync(Stuck(1001), rearm.Delegate, Ct);

        /* Tried once, failed -> escalated with an "auto-re-arm FAILED" alert. */
        Assert.Single(rearm.Calls);
        var fired = Assert.Single(h.Deliverer.Outcomes);
        Assert.Equal(AlertSeverityLevel.Critical, fired.Severity);
        Assert.Contains("FAILED", fired.ShortMessage, StringComparison.Ordinal);

        /* Next check: never retries the re-arm (already escalated). */
        h.Now = h.Now.AddHours(1);
        await e.ApplyCompressionJobsStuckAsync(Stuck(1001), rearm.Delegate, Ct);
        Assert.Single(rearm.Calls);
    }

    [Fact]
    public async Task CompressionJobs_Recovery_WritesOneResolutionRow_AndClearsState()
    {
        var h = new Harness();
        var e = h.Build();
        var rearm = new RearmRecorder();

        await e.ApplyCompressionJobsStuckAsync(Stuck(1001), rearm.Delegate, Ct);  /* stuck */
        Assert.Empty(h.History.Records);

        /* No longer stuck: exactly one "Compression Job Recovered" audit row (BuildResolutionRecord maps the
           resolution Title onto the history MetricName, mirroring the disk-pressure recovery). */
        await e.ApplyCompressionJobsStuckAsync(Stuck(), rearm.Delegate, Ct);
        var resolved = Assert.Single(h.History.Records);
        Assert.Equal("Compression Job Recovered", resolved.MetricName);
        Assert.True(resolved.AlertSent);
        Assert.Contains("running on schedule again", resolved.DetailText, StringComparison.Ordinal);

        /* Still healthy next check — no duplicate resolution (edge-triggered), and a re-stuck job would be a
           fresh first-detection again (state was cleared) -> a new re-arm. */
        await e.ApplyCompressionJobsStuckAsync(Stuck(), rearm.Delegate, Ct);
        Assert.Single(h.History.Records);

        await e.ApplyCompressionJobsStuckAsync(Stuck(1001), rearm.Delegate, Ct);
        Assert.Equal(2, rearm.Calls.Count);  /* re-stuck after recovery -> re-armed fresh */
    }

    [Fact]
    public async Task CompressionJobs_AlertsDisabled_DoesNotFireOrRearm()
    {
        var h = new Harness();
        h.Settings.AlertsEnabled = false;
        var e = h.Build();
        var rearm = new RearmRecorder();

        await e.ApplyCompressionJobsStuckAsync(Stuck(1001), rearm.Delegate, Ct);

        Assert.Empty(rearm.Calls);
        Assert.Empty(h.Deliverer.Outcomes);
    }

    [Fact]
    public async Task CompressionJobs_MultipleStuckJobs_EachRearmedOncePerCheck()
    {
        var h = new Harness();
        var e = h.Build();
        var rearm = new RearmRecorder();

        await e.ApplyCompressionJobsStuckAsync(Stuck(1001, 1002, 1003), rearm.Delegate, Ct);

        Assert.Equal(new[] { 1001L, 1002L, 1003L }, rearm.Calls);
        Assert.Equal(3, h.Deliverer.Outcomes.Count);
    }

    [Fact]
    public async Task CompressionJobs_EvaluateWrapper_IsolatesAThrowingSeam_DoesNotPropagate()
    {
        /* The worker's compression sweep opens the connection OUTSIDE the evaluator; the Evaluate wrapper must
           still swallow a throw from the mute check OR the re-arm delegate so it can never propagate out of the
           un-guarded sweep loop and stop collection for the whole fleet. */
        var h = new Harness { MuteThrows = true };
        var e = h.Build();

        /* A throwing mute check (inside FireAsync, after a successful re-arm) is isolated. */
        await e.EvaluateCompressionJobsAsync(Stuck(1001), _ => Task.FromResult(true), Ct);
        Assert.Empty(h.Deliverer.Outcomes);

        /* A throwing re-arm delegate is isolated too. */
        var e2 = new Harness().Build();
        await e2.EvaluateCompressionJobsAsync(Stuck(1002), _ => throw new InvalidOperationException("boom"), Ct);
    }

    /* ---------------- master switch ---------------- */

    [Fact]
    public async Task AlertsDisabled_ConnectionEdge_TracksStateButDoesNotFire_AndReEnableResumes()
    {
        var h = new Harness();
        h.Settings.AlertsEnabled = false;
        var e = h.Build();

        /* Disabled: transitions are tracked (so re-enabling has a correct baseline) but nothing fires. */
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: false, error: "x", Ct);
        Assert.Empty(h.Deliverer.Outcomes);

        /* Re-enable, then recover: the tracked Offline baseline makes this a real Offline->Online, so
           "Server Restored" fires (state was not lost while disabled). */
        h.Settings.AlertsEnabled = true;
        await e.ApplyConnectionOutcomeAsync(ServerId, Name, online: true, error: null, Ct);
        var restored = Assert.Single(h.Deliverer.Outcomes);
        Assert.Equal("Server Restored", restored.MetricName);
    }

    [Fact]
    public async Task AlertsDisabled_EvaluateStoreAlerts_ShortCircuitsBeforeAnyStoreRead()
    {
        var h = new Harness();
        h.Settings.AlertsEnabled = false;
        var e = h.Build();

        /* The master gate returns before touching the store, so a null data source is never dereferenced;
           if the gate were ever moved after the read this NREs (i.e. still fails, flagging the regression). */
        await e.EvaluateStoreAlertsAsync(null!, ServerId, Name, connected: true, Ct);
        Assert.Empty(h.Deliverer.Outcomes);
        Assert.Empty(h.History.Records);
    }

    /* ---------------- resolution-record shape + engine wiring (finding #4) ---------------- */

    [Fact]
    public void BuildResolutionRecord_MirrorsDashboardClearedRowShape()
    {
        var record = DarlingSelfAlertEvaluator.BuildResolutionRecord(
            new AlertResolution(Key, Name, "High CPU", "CPU Resolved", $"{Name}: Total CPU back to 12%"));

        Assert.Equal(Key, record.ServerId);
        Assert.Equal(Name, record.ServerName);
        Assert.Equal("CPU Resolved", record.MetricName);           /* the "…Resolved/Cleared" title, Dashboard shape */
        Assert.Equal($"{Name}: Total CPU back to 12%", record.DetailText);
        Assert.True(record.AlertSent);
        Assert.Equal("tray", record.NotificationType);
        Assert.Null(record.SendError);
        Assert.False(record.Muted);
    }

    [Fact]
    public async Task EngineResolution_WritesResolvedHistoryRow_ThroughBuildResolutionRecord()
    {
        /* Replicates DarlingWorker.BuildAlertEngine's resolution wiring: the shared engine's resolution
           callback writes a resolved-flavored history row (finding #4 — previously it only logged). */
        var settings = new FakeSettings { CpuEnabled = true };
        var history = new FakeHistoryStore();
        var deliverer = new RecordingDeliverer();
        var now = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        var engine = new AlertEngine(
            settings, new StubReadAdapter(), new StubStateStore(), deliverer,
            isAlertMuted: _ => false,
            failedJobsFetcher: null,
            resolutionCallback: async (resolution, _) =>
                await history.RecordAlertAsync(DarlingSelfAlertEvaluator.BuildResolutionRecord(resolution)),
            logger: null,
            utcNow: () => now);

        /* Fire: total CPU 90 >= 80. */
        await engine.EvaluateServerAsync(new AlertServerSnapshot(Key, Name, IsOnline: true, 90, 90, false, false), Ct);
        Assert.Single(deliverer.Outcomes);
        Assert.Empty(history.Records); /* no resolution yet */

        /* Clear: CPU back below threshold => the engine emits a resolution => a history row is written. */
        now = now.AddMinutes(1);
        await engine.EvaluateServerAsync(new AlertServerSnapshot(Key, Name, IsOnline: true, 10, 10, false, false), Ct);
        var resolved = Assert.Single(history.Records);
        Assert.Equal("CPU Resolved", resolved.MetricName);
        Assert.Equal("tray", resolved.NotificationType);
    }

    private sealed class StubReadAdapter : IAlertReadAdapter
    {
        public Task<List<BlockedProcessAlertRow>> GetRecentBlockedProcessReportsAsync(string serverKey, int hoursBack, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<BlockedProcessAlertRow>());
        public Task<List<DeadlockAlertRow>> GetRecentDeadlocksAsync(string serverKey, int hoursBack, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<DeadlockAlertRow>());
        public Task<List<PoisonWaitDelta>> GetPoisonWaitDeltasAsync(string serverKey, double thresholdMs, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<PoisonWaitDelta>());
        public Task<List<LongRunningQueryInfo>> GetLongRunningQueriesAsync(
            string serverKey, int thresholdMinutes, int maxResults,
            bool excludeSpServerDiagnostics, bool excludeWaitFor, bool excludeBackups, bool excludeMiscWaits, bool excludeCdc,
            IReadOnlyList<string> excludedDatabases, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<LongRunningQueryInfo>());
        public Task<List<VolumeFreeSpaceInfo>> GetVolumeFreeSpaceAsync(string serverKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<VolumeFreeSpaceInfo>());
        public Task<TempDbSpaceInfo?> GetTempDbSpaceAsync(string serverKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<TempDbSpaceInfo?>(null);
        public Task<List<AnomalousJobInfo>> GetAnomalousJobsAsync(string serverKey, int multiplier, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<AnomalousJobInfo>());
    }

    private sealed class StubStateStore : IAlertStateStore
    {
        public Task<int?> LoadEdgeTriggerWatermarkAsync(string serverKey, string metricName) => Task.FromResult<int?>(null);
        public Task SaveEdgeTriggerWatermarkAsync(string serverKey, string metricName, int watermark) => Task.CompletedTask;
        public Task<DateTime?> LoadFailedJobWatermarkAsync(string serverKey) => Task.FromResult<DateTime?>(null);
        public Task SaveFailedJobWatermarkAsync(string serverKey, DateTime watermark) => Task.CompletedTask;
    }

    /* ---------------- live collection_log reads (gated on DARLING_TEST_PG) ---------------- */

    private const int LiveServerId = -770077;

    [Fact]
    public async Task LiveStoreReads_ComputeCollectionStoppedAndCaptureDown()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live self-alert store reads.");

        var ct = Ct;
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);
        await DeleteLiveRowsAsync(connection, ct);

        await using var postgres = NpgsqlDataSource.Create(connectionString!);
        try
        {
            var utcNow = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

            /* One old SUCCESS (45 min ago), then 12 recent ERRORs — the last 10 runs are all failures and
               the last success is well past the staleness window. */
            long logId = 9_000_000;
            await InsertLogAsync(connection, ct, logId++, "wait_stats", utcNow.AddMinutes(-45), "SUCCESS");
            for (int i = 0; i < 12; i++)
            {
                await InsertLogAsync(connection, ct, logId++, "wait_stats", utcNow.AddMinutes(-2), "ERROR");
            }

            var (lastSuccess, recentRuns, recentSuccess) = await DarlingSelfAlertEvaluator.ReadCollectionSignalsAsync(
                postgres, LiveServerId, DarlingSelfAlertEvaluator.ConsecutiveFailureThreshold, ct);

            Assert.NotNull(lastSuccess);
            Assert.Equal(DarlingSelfAlertEvaluator.ConsecutiveFailureThreshold, recentRuns);
            Assert.Equal(0, recentSuccess);
            Assert.True(DarlingSelfAlertEvaluator.IsCollectionStopped(
                lastSuccess, recentRuns, recentSuccess, DateTime.UtcNow, out _));

            /* Full path: EvaluateStoreAlertsAsync must NOT fire collection-stopped until the server has been
               online this run (the restart-staleness guard), then must fire once it has. Real-time clock so
               the 45-minute-old success reads as stale against the seeded rows. */
            var h = new Harness { Now = DateTime.UtcNow };
            var evaluator = h.Build();

            await evaluator.EvaluateStoreAlertsAsync(postgres, LiveServerId, Name, connected: true, ct);
            Assert.DoesNotContain(h.Deliverer.Outcomes, o => o.MetricName == "Collection Stopped");

            await evaluator.ApplyConnectionOutcomeAsync(LiveServerId, Name, online: true, error: null, ct); /* arm */
            await evaluator.EvaluateStoreAlertsAsync(postgres, LiveServerId, Name, connected: true, ct);
            Assert.Contains(h.Deliverer.Outcomes, o => o.MetricName == "Collection Stopped");

            /* Capture-down: latest deadlocks run is SESSION_MISSING, latest blocked_process_report is fine. */
            await InsertLogAsync(connection, ct, logId++, "blocked_process_report", utcNow.AddMinutes(-1), "SUCCESS");
            await InsertLogAsync(connection, ct, logId++, "deadlocks", utcNow.AddMinutes(-1), "SESSION_MISSING");

            var missing = await DarlingSelfAlertEvaluator.ReadMissingCaptureSessionsAsync(postgres, LiveServerId, ct);
            Assert.Equal(new[] { "Deadlock" }, missing);

            await evaluator.EvaluateStoreAlertsAsync(postgres, LiveServerId, Name, connected: true, ct);
            Assert.Contains(h.Deliverer.Outcomes, o => o.MetricName == "Capture Down");
        }
        finally
        {
            await DeleteLiveRowsAsync(connection, ct);
        }
    }

    private static async Task InsertLogAsync(
        NpgsqlConnection connection, CancellationToken ct, long logId, string collector, DateTime time, string status)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO collection_log (log_id, server_id, server_name, collector_name, collection_time, duration_ms, status, error_message, rows_collected, sql_duration_ms, duckdb_duration_ms)
VALUES ($1, $2, $3, $4, $5, 0, $6, NULL, 0, 0, 0)", connection);
        command.Parameters.AddWithValue(logId);
        command.Parameters.AddWithValue(LiveServerId);
        command.Parameters.AddWithValue(Name);
        command.Parameters.AddWithValue(collector);
        command.Parameters.AddWithValue(time);
        command.Parameters.AddWithValue(status);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteLiveRowsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        using var cleanup = new NpgsqlCommand(
            $"DELETE FROM collection_log WHERE server_id = {LiveServerId.ToString(CultureInfo.InvariantCulture)};", connection);
        await cleanup.ExecuteNonQueryAsync(ct);
    }

    /* ---------------- #1681: firings are logged, not just recoveries ---------------- */

    /// <summary>
    /// RecordResolutionAsync has always logged at Information, so the service log showed "... Recovered" with
    /// nothing preceding it - a spontaneous recovery from a condition that never appeared, which is worse than
    /// logging neither half. Every self-alert fired silently through the same path: compression stuck, disk
    /// pressure, capture-down, agent-not-running, collection-health.
    /// </summary>
    [Fact]
    public async Task CompressionStuck_Firing_IsLoggedAtWarning()
    {
        var h = new Harness();
        var e = h.Build();

        await e.ApplyCompressionJobsStuckAsync(Stuck(1001), new RearmRecorder().Delegate, Ct);

        var warnings = h.Log.Entries
            .Where(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .ToList();

        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, x => x.Message.Contains("Compression Job Stuck", StringComparison.Ordinal));
    }

    /// <summary>
    /// A muted alert still logs, flagged. Muting suppresses the notification CHANNELS, not the operator's ability
    /// to find the event afterwards - suppressing both would make a muted condition invisible everywhere at once.
    /// </summary>
    [Fact]
    public async Task CompressionStuck_Firing_IsLoggedEvenWhenMuted()
    {
        var h = new Harness { Muted = true };
        var e = h.Build();

        await e.ApplyCompressionJobsStuckAsync(Stuck(1001), new RearmRecorder().Delegate, Ct);

        var warnings = h.Log.Entries
            .Where(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .ToList();

        Assert.Contains(warnings, x => x.Message.Contains("[muted]", StringComparison.Ordinal));
    }
}
