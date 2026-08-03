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

namespace PerformanceMonitor.Analysis;

/// <summary>
/// Collapses a multi-run finding read into one entry per diagnostic chain for the MCP
/// get_analysis_findings twins (#2000). The engine re-persists the same stories every analysis
/// cycle, so a multi-hour read returns each chain dozens of times — measured on the 52-server
/// production fleet over 24h: 21,623 rows collapse to 774 (story, incident) groups, a 27.9x mean
/// duplication (39x on the worst server) — every occurrence re-carrying the full advice prose and
/// rendered remediation command. Grouping is by (story_path_hash, incident_id): the story hash is
/// the chain's identity (what muting keys on) and the incident id separates distinct episodes of
/// the same chain. Each group keeps its LATEST occurrence as the representative — value-bearing
/// advice is frozen into StoryText at analysis time, so latest = current truth — plus the
/// occurrence stats that replace the lost timeline, because severity genuinely moves within a
/// group (275 of the 774 measured groups spread more than 0.1). The store keeps every occurrence;
/// this shapes the READ only, and the viewer timelines are untouched.
/// </summary>
public static class FindingOccurrences
{
    /// <summary>
    /// The store-read row cap the MCP findings tools pass instead of the stores' default 100,
    /// which covered only the ~5 most recent engine runs of a 24h read and silently dropped the
    /// older ones — so occurrence stats computed under it would lie. Sized for the deepest legal
    /// read: 7 days (McpHelpers.MaxHoursBack) at the busiest measured cadence (~23 rows/hour on
    /// the production fleet) is ~3,900 rows; 10,000 leaves comfortable headroom. A cap is still a
    /// cap: a read that FILLS it has had its oldest rows dropped by the stores' newest-first
    /// LIMIT, so both tools disclose that in the response (truncation_note) rather than letting
    /// first_seen quietly under-report.
    /// </summary>
    public const int WindowCoveringLimit = 10_000;

    /// <summary>
    /// One group per (story_path_hash, incident_id), still-firing chains first (latest last-seen,
    /// then representative severity). The representative is the group's latest occurrence,
    /// tie-broken by severity then finding id so equal-time rows collapse deterministically.
    /// </summary>
    public static List<FindingOccurrenceGroup> Collapse(IReadOnlyList<AnalysisFinding> findings)
    {
        if (findings is null || findings.Count == 0)
            return new List<FindingOccurrenceGroup>();

        return findings
            .GroupBy(f => (f.StoryPathHash, f.IncidentId))
            .Select(g =>
            {
                var latest = g
                    .OrderByDescending(f => f.AnalysisTime)
                    .ThenByDescending(f => f.Severity)
                    .ThenByDescending(f => f.FindingId)
                    .First();

                /* Group time range spans every occurrence's window, skipping the null ends a
                   partially-populated row may carry (both fields are nullable on the model). */
                var starts = g.Where(f => f.TimeRangeStart.HasValue).Select(f => f.TimeRangeStart!.Value).ToList();
                var ends = g.Where(f => f.TimeRangeEnd.HasValue).Select(f => f.TimeRangeEnd!.Value).ToList();

                return new FindingOccurrenceGroup
                {
                    Latest = latest,
                    Occurrences = g.Count(),
                    FirstSeen = g.Min(f => f.AnalysisTime),
                    LastSeen = g.Max(f => f.AnalysisTime),
                    PeakSeverity = g.Max(f => f.Severity),
                    TimeRangeStart = starts.Count > 0 ? starts.Min() : null,
                    TimeRangeEnd = ends.Count > 0 ? ends.Max() : null
                };
            })
            .OrderByDescending(x => x.LastSeen)
            .ThenByDescending(x => x.Latest.Severity)
            .ToList();
    }
}

/// <summary>
/// One deduplicated diagnostic chain from <see cref="FindingOccurrences.Collapse"/>: the latest
/// occurrence as the representative, plus the stats that summarize the collapsed timeline.
/// </summary>
public sealed class FindingOccurrenceGroup
{
    /// <summary>The group's most recent occurrence — the representative the consumer renders.</summary>
    public required AnalysisFinding Latest { get; init; }

    /// <summary>How many persisted rows (engine cycles) this group collapsed.</summary>
    public required int Occurrences { get; init; }

    /// <summary>Earliest analysis_time in the group — when the chain first fired in the window.</summary>
    public required DateTime FirstSeen { get; init; }

    /// <summary>Latest analysis_time in the group — when the chain last fired.</summary>
    public required DateTime LastSeen { get; init; }

    /// <summary>The highest severity any occurrence reached; the latest severity rides on <see cref="Latest"/>.</summary>
    public required double PeakSeverity { get; init; }

    /// <summary>Earliest analyzed-window start across the group (null when no occurrence carried one).</summary>
    public DateTime? TimeRangeStart { get; init; }

    /// <summary>Latest analyzed-window end across the group (null when no occurrence carried one).</summary>
    public DateTime? TimeRangeEnd { get; init; }
}
