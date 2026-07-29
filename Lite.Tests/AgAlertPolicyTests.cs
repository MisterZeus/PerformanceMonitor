/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using PerformanceMonitor.Common;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// The shared Availability Group decision rules (#991/#1696). This file is DUPLICATED VERBATIM in
/// Darling.Tests — the <c>ConnectionAlertPolicyTests</c> arrangement — so both apps pin the same matrix from
/// their own suite and neither can quietly diverge from the policy the other enforces.
///
/// <para>Three properties carry most of the weight, and each one is a bug that shipped or nearly shipped:
/// a first sighting is a silent baseline; NULL is never a transition; and a SUSPENDED row may RAISE an
/// alarm but may NEVER CLEAR one.</para>
/// </summary>
public class AgAlertPolicyTests
{
    private static AgDatabaseReading Db(
        long? lagSeconds = 0, long? redoKb = 0, bool? suspended = false, string? suspendReason = null) =>
        new("AG1", "Sales", "NODE2", lagSeconds, redoKb, suspended, suspendReason);

    private static AgSyncJudgement Judge(AgDatabaseReading r, int lagSeconds, long redoKb) =>
        AgAlertPolicy.JudgeSync(r, lagSeconds, redoKb, out _);

    /* ---------------- failover ---------------- */

    [Theory]
    [InlineData("SECONDARY", "PRIMARY", true)]      // the classic promotion
    [InlineData("PRIMARY", "SECONDARY", true)]      // the reverse edge is just as much a failover
    [InlineData("SECONDARY", "RESOLVING", true)]    // a move into RESOLVING is a failover in progress
    [InlineData("PRIMARY", "PRIMARY", false)]       // steady state
    [InlineData(null, "PRIMARY", false)]            // first sighting: silent baseline
    [InlineData("PRIMARY", null, false)]            // quorum-loss NULL is not a transition
    [InlineData(null, null, false)]
    public void IsFailover_FiresOnlyOnARealChangeBetweenTwoKnownRoles(string? previous, string? current, bool expected) =>
        Assert.Equal(expected, AgAlertPolicy.IsFailover(previous, current));

    [Fact]
    public void IsFailover_RemembersAcrossANullReading_SoRecoveringToTheSameRoleIsNotAFailover()
    {
        /* The caller keeps the last non-null role; a NULL in between must not manufacture an edge when the
           role comes back unchanged. This is the fleet-wide-spray case under WSFC quorum loss. */
        Assert.False(AgAlertPolicy.IsFailover("PRIMARY", null));
        Assert.False(AgAlertPolicy.IsFailover("PRIMARY", "PRIMARY"));
    }

    /* ---------------- connected / disconnected ---------------- */

    [Theory]
    [InlineData("CONNECTED", "DISCONNECTED", AgConnectionDecision.Disconnected)]
    [InlineData("DISCONNECTED", "CONNECTED", AgConnectionDecision.Reconnected)]
    [InlineData("DISCONNECTED", "DISCONNECTED", AgConnectionDecision.None)]   // edge-triggered: no re-fire
    [InlineData("CONNECTED", "CONNECTED", AgConnectionDecision.None)]
    [InlineData(null, "DISCONNECTED", AgConnectionDecision.None)]             // already down at first sight
    [InlineData("CONNECTED", null, AgConnectionDecision.None)]                // no reading is no signal
    public void DecideConnection_IsAnEdgeBetweenKnownStates(string? previous, string? current, AgConnectionDecision expected) =>
        Assert.Equal(expected, AgAlertPolicy.DecideConnection(previous, current));

    [Fact]
    public void DecideConnection_UnrecognizedValue_ReadsAsConnected_NotAsAPage()
    {
        /* Only an exact DISCONNECTED pages. A value the product never learned to interpret must not wake
           somebody at 3am, and must not be mistaken for a recovery either. */
        Assert.Equal(AgConnectionDecision.None, AgAlertPolicy.DecideConnection("CONNECTED", "SOMETHING_NEW"));
        Assert.Equal(AgConnectionDecision.Reconnected, AgAlertPolicy.DecideConnection("DISCONNECTED", "SOMETHING_NEW"));
        Assert.False(AgAlertPolicy.IsDisconnected("SOMETHING_NEW"));
        Assert.False(AgAlertPolicy.IsDisconnected(null));
    }

    [Fact]
    public void DecideConnection_AlreadyDisconnectedAtFirstSighting_StaysSilent_ButItsRecoveryStillReports()
    {
        Assert.Equal(AgConnectionDecision.None, AgAlertPolicy.DecideConnection(null, "DISCONNECTED"));
        Assert.Equal(AgConnectionDecision.Reconnected, AgAlertPolicy.DecideConnection("DISCONNECTED", "CONNECTED"));
    }

    /* ---------------- suspension ---------------- */

    [Theory]
    [InlineData(false, true, AgSuspensionDecision.Suspended)]
    [InlineData(true, false, AgSuspensionDecision.Resumed)]
    [InlineData(true, true, AgSuspensionDecision.None)]
    [InlineData(false, false, AgSuspensionDecision.None)]
    [InlineData(null, true, AgSuspensionDecision.None)]   // already suspended at first sight
    [InlineData(true, null, AgSuspensionDecision.None)]   // no reading is no signal
    public void DecideSuspension_IsAnEdgeBetweenKnownStates(bool? previous, bool? current, AgSuspensionDecision expected) =>
        Assert.Equal(expected, AgAlertPolicy.DecideSuspension(previous, current));

    /* ---------------- sync judgement ---------------- */

    [Fact]
    public void JudgeSync_LagAtOrOverThreshold_IsBehind()
    {
        Assert.Equal(AgSyncJudgement.Behind, Judge(Db(lagSeconds: 300), 300, 0));
        Assert.Equal(AgSyncJudgement.Behind, Judge(Db(lagSeconds: 301), 300, 0));
        Assert.Equal(AgSyncJudgement.CaughtUp, Judge(Db(lagSeconds: 299), 300, 0));

        AgAlertPolicy.JudgeSync(Db(lagSeconds: 600), 300, 0, out var reason);
        Assert.Contains("600 seconds behind", reason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void JudgeSync_ZeroThreshold_DisablesThatTrigger()
    {
        /* Both off: a wildly lagging, hugely queued secondary is NOT MEASURABLE - not "caught up". Only a
           measured CaughtUp resolves a standing alert, so the distinction is load-bearing. */
        Assert.Equal(AgSyncJudgement.NotMeasurable, Judge(Db(lagSeconds: 99999, redoKb: 99999999), 0, 0));

        AgAlertPolicy.JudgeSync(Db(lagSeconds: 99999, redoKb: 5000), 0, 4096, out var reason);
        Assert.Contains("redo queue", reason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void JudgeSync_SuspendedRow_MayFire_ButMayNeverResolve()
    {
        /* Measured on a live AG: lag ACCRUES while suspended rather than reading 0 the way MS Learn
           documents, so a suspended secondary past the threshold MUST fire - an earlier rule that abstained
           on suspended rows silenced exactly that case. */
        Assert.Equal(AgSyncJudgement.Behind, Judge(Db(lagSeconds: 9999, suspended: true), 300, 0));

        /* The other half, and NOT hypothetical: on a quiet group lag was measured reading 0 at every sample
           across a whole suspension while already NOT SYNCHRONIZING. Calling that CaughtUp would clear a
           standing alarm on a replica receiving nothing. */
        Assert.Equal(AgSyncJudgement.NotMeasurable, Judge(Db(lagSeconds: 0, suspended: true), 300, 0));

        /* Redo fires while suspended (a frozen queue over the threshold is a real backlog) but cannot
           resolve (a frozen queue under it is stale data). */
        Assert.Equal(AgSyncJudgement.Behind, Judge(Db(lagSeconds: 0, redoKb: 8192, suspended: true), 300, 4096));
        Assert.Equal(AgSyncJudgement.NotMeasurable, Judge(Db(lagSeconds: 0, redoKb: 8, suspended: true), 300, 4096));

        /* Once movement resumes the same small readings are a real measurement and DO resolve. */
        Assert.Equal(AgSyncJudgement.CaughtUp, Judge(Db(lagSeconds: 0, redoKb: 8, suspended: false), 300, 4096));
    }

    [Fact]
    public void JudgeSync_NullReadings_AreNotMeasurable_NotCaughtUp()
    {
        /* The primary's own row, and every row under quorum loss. Reporting CaughtUp would resolve every
           standing lag alert on the fleet the moment the cluster lost quorum. */
        Assert.Equal(AgSyncJudgement.NotMeasurable, Judge(Db(lagSeconds: null, redoKb: null, suspended: null), 300, 4096));

        /* One usable arm is enough to judge, even when the other reads NULL. */
        Assert.Equal(AgSyncJudgement.CaughtUp, Judge(Db(lagSeconds: 5, redoKb: null), 300, 4096));
    }

    /* ---------------- metric names are webhook automation keys ---------------- */

    [Fact]
    public void MetricNames_AreStable_AndClassifyCorrectly()
    {
        /* Downstream automation matches on these strings; changing one silently breaks a customer's
           auto-heal loop, so they are pinned here rather than only being consts. */
        Assert.Equal("AG Failover", AgAlertPolicy.FailoverMetric);
        Assert.Equal("AG Replica Disconnected", AgAlertPolicy.ReplicaDisconnectedMetric);
        Assert.Equal("AG Replica Reconnected", AgAlertPolicy.ReplicaReconnectedMetric);
        Assert.Equal("AG Sync Fell Behind", AgAlertPolicy.SyncFellBehindMetric);
        Assert.Equal("AG Database Suspended", AgAlertPolicy.DatabaseSuspendedMetric);

        /* The reconnect is a resolution notice; the other four are actionable. */
        Assert.True(AlertMetricClassifier.IsResolution(AgAlertPolicy.ReplicaReconnectedMetric));
        Assert.False(AlertMetricClassifier.IsResolution(AgAlertPolicy.FailoverMetric));
        Assert.False(AlertMetricClassifier.IsResolution(AgAlertPolicy.ReplicaDisconnectedMetric));
        Assert.False(AlertMetricClassifier.IsResolution(AgAlertPolicy.SyncFellBehindMetric));
        Assert.False(AlertMetricClassifier.IsResolution(AgAlertPolicy.DatabaseSuspendedMetric));
    }
}
