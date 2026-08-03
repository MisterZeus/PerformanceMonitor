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
using PerformanceMonitor.Analysis;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// Pins <see cref="FindingOccurrences.Collapse"/> — the #2000 dedup both MCP findings twins share.
/// The contract under test: one group per (story_path_hash, incident_id); the representative is the
/// LATEST occurrence (severity/finding-id tie-breaks so equal-time rows collapse deterministically);
/// occurrence stats summarize the collapsed timeline (count, first/last seen, peak severity,
/// spanning time range with null ends skipped); output orders still-firing chains first. These are
/// pure in-memory pins — the envelope carrying the groups is pinned by
/// <c>McpAnalysisFindingsCommandTests</c> (Lite) and the Darling gated e2e.
/// </summary>
public class FindingOccurrencesTests
{
    private static readonly DateTime T0 = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private static AnalysisFinding Make(
        string hash, string incident, DateTime analysisTime, double severity,
        long findingId = 0, DateTime? rangeStart = null, DateTime? rangeEnd = null) =>
        new()
        {
            FindingId = findingId,
            AnalysisTime = analysisTime,
            ServerId = 1,
            ServerName = "unit",
            Severity = severity,
            Confidence = 0.9,
            Category = "cpu",
            StoryPath = "ROOT → LEAF",
            StoryPathHash = hash,
            StoryText = "unit finding",
            RootFactKey = "ROOT",
            FactCount = 1,
            IncidentId = incident,
            TimeRangeStart = rangeStart,
            TimeRangeEnd = rangeEnd
        };

    [Fact]
    public void Collapse_GroupsByStoryHashAndIncident_SameHashDifferentIncidentStaysSeparate()
    {
        var groups = FindingOccurrences.Collapse(new[]
        {
            Make("hash-a", "incident-1", T0, 1.0),
            Make("hash-a", "incident-1", T0.AddMinutes(30), 1.5),
            Make("hash-a", "incident-2", T0.AddMinutes(60), 2.0),
            Make("hash-b", "incident-1", T0, 0.5)
        });

        Assert.Equal(3, groups.Count);

        var episode1 = groups.Single(g => g.Latest.StoryPathHash == "hash-a" && g.Latest.IncidentId == "incident-1");
        Assert.Equal(2, episode1.Occurrences);

        var episode2 = groups.Single(g => g.Latest.StoryPathHash == "hash-a" && g.Latest.IncidentId == "incident-2");
        Assert.Equal(1, episode2.Occurrences);
    }

    [Fact]
    public void Collapse_RepresentativeIsTheLatestOccurrence_StatsSummarizeTheTimeline()
    {
        var groups = FindingOccurrences.Collapse(new[]
        {
            Make("hash-a", "incident-1", T0, severity: 2.5, findingId: 1,
                rangeStart: T0.AddHours(-4), rangeEnd: T0),
            Make("hash-a", "incident-1", T0.AddMinutes(30), severity: 1.8, findingId: 2,
                rangeStart: T0.AddHours(-3.5), rangeEnd: T0.AddMinutes(30)),
            Make("hash-a", "incident-1", T0.AddMinutes(60), severity: 2.1, findingId: 3,
                rangeStart: T0.AddHours(-3), rangeEnd: T0.AddMinutes(60))
        });

        var g = Assert.Single(groups);
        Assert.Equal(3, g.Latest.FindingId);          // latest run wins, not highest severity
        Assert.Equal(2.1, g.Latest.Severity);
        Assert.Equal(3, g.Occurrences);
        Assert.Equal(T0, g.FirstSeen);
        Assert.Equal(T0.AddMinutes(60), g.LastSeen);
        Assert.Equal(2.5, g.PeakSeverity);            // the first occurrence's peak survives
        Assert.Equal(T0.AddHours(-4), g.TimeRangeStart);   // earliest window start
        Assert.Equal(T0.AddMinutes(60), g.TimeRangeEnd);   // latest window end
    }

    [Fact]
    public void Collapse_EqualAnalysisTimes_TieBreakBySeverityThenFindingId()
    {
        var bySeverity = Assert.Single(FindingOccurrences.Collapse(new[]
        {
            Make("hash-a", "incident-1", T0, severity: 1.0, findingId: 10),
            Make("hash-a", "incident-1", T0, severity: 3.0, findingId: 5)
        }));
        Assert.Equal(5, bySeverity.Latest.FindingId);

        var byId = Assert.Single(FindingOccurrences.Collapse(new[]
        {
            Make("hash-a", "incident-1", T0, severity: 2.0, findingId: 10),
            Make("hash-a", "incident-1", T0, severity: 2.0, findingId: 20)
        }));
        Assert.Equal(20, byId.Latest.FindingId);
    }

    [Fact]
    public void Collapse_NullTimeRangesAreSkipped_AllNullYieldsNull()
    {
        var mixed = Assert.Single(FindingOccurrences.Collapse(new[]
        {
            Make("hash-a", "incident-1", T0, 1.0, findingId: 1),
            Make("hash-a", "incident-1", T0.AddMinutes(30), 1.0, findingId: 2,
                rangeStart: T0.AddHours(-1), rangeEnd: T0.AddMinutes(30))
        }));
        Assert.Equal(T0.AddHours(-1), mixed.TimeRangeStart);
        Assert.Equal(T0.AddMinutes(30), mixed.TimeRangeEnd);

        var allNull = Assert.Single(FindingOccurrences.Collapse(new[]
        {
            Make("hash-a", "incident-1", T0, 1.0)
        }));
        Assert.Null(allNull.TimeRangeStart);
        Assert.Null(allNull.TimeRangeEnd);
    }

    [Fact]
    public void Collapse_OrdersStillFiringFirst_ThenBySeverity()
    {
        var groups = FindingOccurrences.Collapse(new[]
        {
            // Resolved earlier: last seen T0, even though its severity is the highest.
            Make("hash-old", "incident-1", T0, severity: 5.0),
            // Still firing: last seen T0+60.
            Make("hash-live-low", "incident-1", T0.AddMinutes(60), severity: 1.0),
            Make("hash-live-high", "incident-1", T0.AddMinutes(60), severity: 2.0)
        });

        Assert.Equal(
            new[] { "hash-live-high", "hash-live-low", "hash-old" },
            groups.Select(g => g.Latest.StoryPathHash).ToArray());
    }

    [Fact]
    public void Collapse_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(FindingOccurrences.Collapse(Array.Empty<AnalysisFinding>()));
        Assert.Empty(FindingOccurrences.Collapse(new List<AnalysisFinding>()));
    }
}
