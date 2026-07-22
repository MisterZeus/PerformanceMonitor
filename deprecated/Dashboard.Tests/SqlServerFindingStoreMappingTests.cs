/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitor.Analysis;
using PerformanceMonitorDashboard.Analysis;
using Xunit;

namespace Dashboard.Tests;

/// <summary>
/// Pins the story→finding mapping the Dashboard finding store persists — extracted (and made
/// testable) after the Darling PgFindingStore port surfaced that this mapping dropped
/// <c>DatabaseName</c>, storing NULL database_name on every finding since the store shipped.
/// </summary>
public sealed class SqlServerFindingStoreMappingTests
{
    [Fact]
    public void MapStoryToFinding_PreservesDatabaseName_AndConvertsWindowToUtc()
    {
        var story = new AnalysisStory
        {
            DatabaseName = "StackOverflow2010",
            Severity = 70,
            Confidence = 0.9,
            Category = "Concurrency",
            StoryPath = "BLOCKING_STORM",
            StoryPathHash = "abc123",
            IncidentId = "inc-1",
            StoryText = "story",
            RootFactKey = "BLOCKING",
            RootFactValue = 42,
            FactCount = 3,
        };
        var context = new AnalysisContext
        {
            ServerId = 7,
            ServerName = "SQL2022",
            TimeRangeStart = new DateTime(2026, 7, 2, 10, 0, 0),
            TimeRangeEnd = new DateTime(2026, 7, 2, 14, 0, 0),
            ServerUtcOffset = TimeSpan.FromHours(-4),
        };
        var analysisTime = new DateTime(2026, 7, 2, 14, 5, 0, DateTimeKind.Utc);

        var finding = SqlServerFindingStore.MapStoryToFinding(story, context, analysisTime, 99);

        Assert.Equal("StackOverflow2010", finding.DatabaseName);   /* the fixed drop */
        Assert.Equal(99, finding.FindingId);
        Assert.Equal(7, finding.ServerId);
        Assert.Equal("SQL2022", finding.ServerName);
        /* Server-local window − offset = UTC (offset here is −4h, so UTC = local + 4h). */
        Assert.Equal(new DateTime(2026, 7, 2, 14, 0, 0), finding.TimeRangeStart);
        Assert.Equal(new DateTime(2026, 7, 2, 18, 0, 0), finding.TimeRangeEnd);
        Assert.Equal(70, finding.Severity);
        Assert.Equal("BLOCKING_STORM", finding.StoryPath);
        Assert.Equal("inc-1", finding.IncidentId);
        Assert.Equal(3, finding.FactCount);
    }

    [Fact]
    public void GetMutedHashesSql_HonorsGlobalNull_AndLegacyZeroServerId()
    {
        /* An all-servers mute persists server_id = NULL (the canonical global marker); legacy rows
           written by the pre-fix MCP "all servers" tool path used server_id = 0. The reader must honor
           both, plus the per-server id, so an all-servers mute actually mutes everywhere. */
        Assert.Contains("server_id = @serverId", SqlServerFindingStore.GetMutedHashesSql, StringComparison.Ordinal);
        Assert.Contains("server_id IS NULL", SqlServerFindingStore.GetMutedHashesSql, StringComparison.Ordinal);
        Assert.Contains("server_id = 0", SqlServerFindingStore.GetMutedHashesSql, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupOldFindingsSql_PurgesByAnalysisTime_FromFindingsTable()
    {
        /* Findings retention (scheduled once per 24h by AnalysisScheduler — the store declared this
           cleanup but nothing called it, so config.analysis_findings grew unbounded). Pin the purge
           target + predicate: it must DELETE from the findings table, keyed on analysis_time against
           the @cutoff parameter — never the muted registry. */
        Assert.Contains("DELETE FROM config.analysis_findings", SqlServerFindingStore.CleanupOldFindingsSql, StringComparison.Ordinal);
        Assert.Contains("analysis_time < @cutoff", SqlServerFindingStore.CleanupOldFindingsSql, StringComparison.Ordinal);
        Assert.DoesNotContain("analysis_muted", SqlServerFindingStore.CleanupOldFindingsSql, StringComparison.Ordinal);
    }
}
