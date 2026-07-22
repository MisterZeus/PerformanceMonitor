/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using PerformanceMonitorDashboard.Services;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// Verifies the "collection stopped" decision logic (<see cref="DatabaseService.DecideCollectionStopped"/>):
/// disabled collector jobs are the immediate, specific signal; a collection-freshness gap is the catch-all
/// for the Agent service being stopped or collectors silently erroring. This is the one health signal the
/// app must compute itself, since the collector that fills every other table is exactly what may be off.
/// </summary>
public class CollectionStoppedDetectionTests
{
    private const int Threshold = 30;

    [Fact]
    public void AllJobsDisabled_IsStopped_WithAllReason()
    {
        var r = DatabaseService.DecideCollectionStopped(disabledJobs: 6, totalJobs: 6, minutesSince: 2, thresholdMinutes: Threshold);

        Assert.True(r.Stopped);
        Assert.Contains("All 6", r.Reason);
        Assert.Equal(6, r.DisabledJobs);
    }

    [Fact]
    public void SomeJobsDisabled_IsStopped_WithPartialReason()
    {
        var r = DatabaseService.DecideCollectionStopped(disabledJobs: 2, totalJobs: 6, minutesSince: 1, thresholdMinutes: Threshold);

        Assert.True(r.Stopped);
        Assert.Contains("2 of 6", r.Reason);
    }

    [Fact]
    public void DisabledJobs_WinOverFreshness_EvenWhenDataIsFresh()
    {
        // Jobs just disabled — collection_log is still fresh, but we must flag it immediately, not wait 30m.
        var r = DatabaseService.DecideCollectionStopped(disabledJobs: 1, totalJobs: 6, minutesSince: 0, thresholdMinutes: Threshold);

        Assert.True(r.Stopped);
        Assert.Contains("disabled", r.Reason);
    }

    [Fact]
    public void NoDisabledJobs_FreshData_IsNotStopped()
    {
        var r = DatabaseService.DecideCollectionStopped(disabledJobs: 0, totalJobs: 6, minutesSince: 3, thresholdMinutes: Threshold);

        Assert.False(r.Stopped);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void NoDisabledJobs_StaleData_IsStopped_WithFreshnessReason()
    {
        // Jobs enabled but nothing has run for > threshold — Agent stopped or collectors erroring.
        var r = DatabaseService.DecideCollectionStopped(disabledJobs: 0, totalJobs: 6, minutesSince: 45, thresholdMinutes: Threshold);

        Assert.True(r.Stopped);
        Assert.Contains("45 minutes", r.Reason);
    }

    [Fact]
    public void FreshnessAtThreshold_IsStopped()
    {
        var r = DatabaseService.DecideCollectionStopped(disabledJobs: 0, totalJobs: 6, minutesSince: Threshold, thresholdMinutes: Threshold);

        Assert.True(r.Stopped);
    }

    [Fact]
    public void JobStateUnreadable_FreshData_IsNotStopped()
    {
        // msdb unreadable (Azure / RDS / no SQLAgentReaderRole): totalJobs = 0 must NOT read as "all disabled".
        var r = DatabaseService.DecideCollectionStopped(disabledJobs: 0, totalJobs: 0, minutesSince: 4, thresholdMinutes: Threshold);

        Assert.False(r.Stopped);
    }

    [Fact]
    public void JobStateUnreadable_StaleData_StillCaughtByFreshness()
    {
        var r = DatabaseService.DecideCollectionStopped(disabledJobs: 0, totalJobs: 0, minutesSince: 60, thresholdMinutes: Threshold);

        Assert.True(r.Stopped);
        Assert.Contains("60 minutes", r.Reason);
    }

    [Fact]
    public void NeverCollected_NoDisabledJobs_IsNotStopped()
    {
        // minutesSince null = collection_log empty (fresh install / never ran), not "stopped".
        var r = DatabaseService.DecideCollectionStopped(disabledJobs: 0, totalJobs: 6, minutesSince: null, thresholdMinutes: Threshold);

        Assert.False(r.Stopped);
    }
}
