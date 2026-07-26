/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitorLite.Services;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// The Job History tab's Agent indicator must not present absence of signal as a problem. Two states the old
/// Running/Stopped pair collapsed wrongly: a server where Agent has NEVER been observed running (a container
/// built without it, Express, a Linux-minimal image) is "not present", not "Stopped" — nothing stopped, and
/// Lite collects in-process so nothing depends on Agent anyway; and a STALE snapshot is "unknown", not
/// "Stopped" — a server nobody has collected from in days is no evidence Agent is down.
///
/// <para>This is the display-tier twin of the capability gate on Darling's "Agent Not Running" alert, and it
/// mirrors that path's refusal to judge a stale reading.</para>
/// </summary>
public sealed class AgentStatusHeaderHonestyTests
{
    private static AgentStatusRow Row(bool running, bool everSeenRunning, int minutesOld) => new()
    {
        ServerId = 1,
        ServerName = "SQL01",
        AgentRunning = running,
        EverSeenRunning = everSeenRunning,
        CollectionTime = DateTime.UtcNow.AddMinutes(-minutesOld),
    };

    [Fact]
    public void NeverSeenRunning_ReadsNotPresent_AndIsNotAProblem()
    {
        var row = Row(running: false, everSeenRunning: false, minutesOld: 1);

        Assert.Equal("not present", row.StatusDisplay);
        Assert.False(row.IsAgentProblem);
    }

    [Fact]
    public void StoppedOnAServerThatRunsAgent_ReadsStopped_AndIsAProblem()
    {
        var row = Row(running: false, everSeenRunning: true, minutesOld: 1);

        Assert.Equal("Stopped", row.StatusDisplay);
        Assert.True(row.IsAgentProblem);
    }

    [Fact]
    public void Running_ReadsRunning_AndIsNotAProblem()
    {
        var row = Row(running: true, everSeenRunning: true, minutesOld: 1);

        Assert.Equal("Running", row.StatusDisplay);
        Assert.False(row.IsAgentProblem);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StaleReading_ReadsUnknown_AndIsNeverAProblem_WhateverItLastSaid(bool everSeenRunning)
    {
        /* Older than the 30-minute window the headless service uses for the same refusal. */
        var row = Row(running: false, everSeenRunning: everSeenRunning, minutesOld: 45);

        Assert.True(row.IsStale);
        Assert.Equal("unknown (stale)", row.StatusDisplay);
        Assert.False(row.IsAgentProblem);
    }

    [Fact]
    public void MissingCollectionTime_CountsAsStale_RatherThanCurrent()
    {
        /* A row with no collection time cannot be vouched for; failing closed to "unknown" beats asserting a
           state we cannot date. */
        var row = new AgentStatusRow { AgentRunning = false, EverSeenRunning = true, CollectionTime = null };

        Assert.True(row.IsStale);
        Assert.Equal("unknown (stale)", row.StatusDisplay);
        Assert.False(row.IsAgentProblem);
    }

    [Fact]
    public void StaleWindow_MatchesTheHeadlessServicesRefusalWindow()
    {
        /* Both surfaces must refuse to judge on the same age of data; drifting them apart would let the header
           call a server down while the alert path stays silent about it (or the reverse). */
        Assert.Equal(TimeSpan.FromMinutes(30), AgentStatusRow.StaleWindow);
    }

    [Fact]
    public void RunningAlwaysWins_EvenIfEverSeenRunningSomehowLags()
    {
        /* A currently-running Agent is its own proof; the never-seen gate must not suppress the good news. */
        var row = Row(running: true, everSeenRunning: false, minutesOld: 1);

        Assert.Equal("Running", row.StatusDisplay);
        Assert.False(row.IsAgentProblem);
    }
}
