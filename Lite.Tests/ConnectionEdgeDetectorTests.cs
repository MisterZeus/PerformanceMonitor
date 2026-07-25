using System.Collections.Generic;
using System.Linq;
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// #1506: Lite's connection-lost / connection-restored alerts are EDGE-triggered. The property that matters
/// is that a transition alerts exactly once — a server that is unreachable for hours is polled hundreds of
/// times and must produce ONE "Server Unreachable", not one per poll. These pin that, plus the first-sighting
/// baseline (an app that starts with a server already down must not alert about a change it never observed —
/// the Dashboard's skip-first-check and Darling's Unknown-state rule).
/// </summary>
public class ConnectionEdgeDetectorTests
{
    [Fact]
    public void FirstObservation_IsASilentBaseline()
    {
        Assert.Equal(ConnectionAlertEdge.None, ConnectionEdgeDetector.Detect(previous: null, current: false));
        Assert.Equal(ConnectionAlertEdge.None, ConnectionEdgeDetector.Detect(previous: null, current: true));
    }

    [Fact]
    public void OnlineToOffline_IsLost()
        => Assert.Equal(ConnectionAlertEdge.Lost, ConnectionEdgeDetector.Detect(previous: true, current: false));

    [Fact]
    public void OfflineToOnline_IsRestored()
        => Assert.Equal(ConnectionAlertEdge.Restored, ConnectionEdgeDetector.Detect(previous: false, current: true));

    [Fact]
    public void SteadyStates_DoNotReFire()
    {
        Assert.Equal(ConnectionAlertEdge.None, ConnectionEdgeDetector.Detect(previous: false, current: false));
        Assert.Equal(ConnectionAlertEdge.None, ConnectionEdgeDetector.Detect(previous: true, current: true));
    }

    /// <summary>
    /// The anti-nag guarantee, stated the way the bug would be reported: a server offline across an 8-hour
    /// outage polled every minute must yield exactly ONE "Server Unreachable" — not 480.
    /// </summary>
    [Fact]
    public void EightHourOutage_PolledEveryMinute_FiresExactlyOnce()
    {
        var observations = new List<bool> { true };                       // online at first sighting
        observations.AddRange(Enumerable.Repeat(false, 8 * 60));          // then offline for 480 polls

        var edges = ReplayPolls(observations);

        Assert.Equal(1, edges.Count(e => e == ConnectionAlertEdge.Lost));
        Assert.DoesNotContain(ConnectionAlertEdge.Restored, edges);
    }

    /// <summary>An outage that ends: exactly one Lost and exactly one Restored, in that order.</summary>
    [Fact]
    public void OutageThenRecovery_FiresOneLostAndOneRestored()
    {
        var observations = new List<bool> { true, true };
        observations.AddRange(Enumerable.Repeat(false, 100));             // the outage
        observations.AddRange(Enumerable.Repeat(true, 100));              // recovered, stays up

        var edges = ReplayPolls(observations).Where(e => e != ConnectionAlertEdge.None).ToList();

        Assert.Equal(new[] { ConnectionAlertEdge.Lost, ConnectionAlertEdge.Restored }, edges);
    }

    /// <summary>
    /// The reporter's actual pattern (#1506): an Azure SQL firewall rule that stops matching when their public
    /// IP rotates, breaking the connection 1-2x/day and recovering when re-authorized. Each break/recover cycle
    /// is one alert and one restore — the webhook that re-runs their GitHub Action fires once per genuine break.
    /// </summary>
    [Fact]
    public void RepeatedDailyBreaks_EachCycleAlertsOnce()
    {
        var observations = new List<bool> { true };

        for (int day = 0; day < 5; day++)
        {
            observations.AddRange(Enumerable.Repeat(false, 30));          // connection broken, polled 30x
            observations.AddRange(Enumerable.Repeat(true, 30));           // access restored
        }

        var edges = ReplayPolls(observations);

        Assert.Equal(5, edges.Count(e => e == ConnectionAlertEdge.Lost));
        Assert.Equal(5, edges.Count(e => e == ConnectionAlertEdge.Restored));
    }

    /// <summary>Drives the detector exactly as MainWindow.CheckConnectionsAndNotify does: carry the previous
    /// state forward across polls, starting from "never observed".</summary>
    private static List<ConnectionAlertEdge> ReplayPolls(IEnumerable<bool> observations)
    {
        var edges = new List<ConnectionAlertEdge>();
        bool? previous = null;

        foreach (var isOnline in observations)
        {
            edges.Add(ConnectionEdgeDetector.Detect(previous, isOnline));
            previous = isOnline;
        }

        return edges;
    }
}
