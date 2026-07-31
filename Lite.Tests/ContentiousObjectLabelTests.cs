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
using PerformanceMonitor.Alerting;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Notifications;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// #1876, Lite half: the two blocking collectors wrote <c>contentious_object</c> in two formats, and
/// the alert engine merges them into ONE list whose labels it hashes into incident dedup keys.
///
/// <para>
/// <c>blocked_process_report</c> writes a plain <c>schema.object</c>, or an <c>Unresolved: …</c> sentinel.
/// <c>dmv_blocking_snapshots</c> writes a <c>QUOTENAME</c>'d <c>[schema].[object]</c> and, for the KEY,
/// PAGE and RID locks it does not resolve at all, the RAW wait resource. So the same contended table
/// alerted under two fingerprints depending on which collector saw it — and a raw KEY resource carries a
/// per-VALUE lock hash, so it alerted under a NEW fingerprint per row.
/// </para>
///
/// <para>Darling.Tests' <c>DarlingContentiousObjectLabelTests</c> pins the identical expectations, so editing one app's copy alone fails a build.</para>
/// </summary>
public sealed class ContentiousObjectLabelTests
{
    /* ── the headline: one object, one fingerprint, whichever collector saw it ── */

    [Fact]
    public void The_Same_Object_From_Both_Collectors_Now_Fingerprints_Once()
    {
        /* Red before #1876: "[dbo].[Users]" and "dbo.Users" differ by two characters the fingerprint's own
           normalization (whitespace + lower) does not touch, so the merged list produced two incidents for
           one table and the DMV fallback could never dedup against the reports it exists to stand in for.
           Distinct SPIDs and minutes so the merge's own dedup keeps both rows — this is about the LABEL. */
        var items = new List<BlockedProcessAlertRow> { Row("dbo.Users", 55, 66, minute: 0, BlockedProcessAlertRow.XeReportSource) };
        var dmv = new List<BlockedProcessAlertRow> { Row("[dbo].[Users]", 77, 88, minute: 5, BlockedProcessAlertRow.DmvSnapshotSource) };

        BlockedProcessReportMerge.AppendDmvFallbackRows(items, dmv);

        Assert.Equal(2, items.Count);
        var groups = Group(items);
        Assert.Single(groups);
        Assert.Equal(2, groups[0].OccurrenceCount);
    }

    [Fact]
    public void Two_Reports_And_A_Snapshot_Of_One_Table_Share_A_Dedup_Key()
    {
        /* The fingerprint itself, not just the grouping: the hash is over (server, type, normalized label),
           so an incident raised from a report cycle and one raised from a fallback cycle must carry the
           SAME dedup key or the cooldown and history rows never line up across the boundary. */
        var report = Group(new[] { Row("dbo.Users", 55, 66, 0, BlockedProcessAlertRow.XeReportSource) });
        var snapshot = Group(new[] { Row("[dbo].[Users]", 77, 88, 5, BlockedProcessAlertRow.DmvSnapshotSource) });

        Assert.Equal(report[0].Incident.DedupKey, snapshot[0].Incident.DedupKey);
    }

    [Fact]
    public void An_Unresolvable_Key_Lock_Stops_Fingerprinting_Once_Per_Row()
    {
        /* The worse half. A raw KEY wait resource embeds a per-VALUE lock hash, so the SAME recurring
           unresolvable lock produced a brand-new identity on every sample — one blocking problem rendered
           as an unbounded stream of one-occurrence incidents, each with its own cooldown. Collapsed to the
           sentinel, those rows group by (lock type, database) exactly like the report side's do. */
        var groups = Group(new[]
        {
            Row("KEY: 6:72057594041991168 (8194443284a0)", 55, 66, 0, BlockedProcessAlertRow.DmvSnapshotSource),
            Row("KEY: 6:72057594041991168 (99aa11bb22cc)", 57, 68, 5, BlockedProcessAlertRow.DmvSnapshotSource),
            Row("KEY: 6:72057594041991168 (0011deadbeef)", 59, 70, 9, BlockedProcessAlertRow.DmvSnapshotSource),
        });

        Assert.Single(groups);
        Assert.Equal(3, groups[0].OccurrenceCount);
        Assert.Equal("Unresolved: key lock, database: StackOverflow2013", groups[0].ContentiousObject);
    }

    /* ── and what normalizing must NOT cost ── */

    [Fact]
    public void The_Merge_Leaves_The_Stored_Label_Alone_So_The_Grid_Keeps_The_Raw_Resource()
    {
        /* Why this is normalized at the identity and not where the two collectors' rows are merged.
           dmv_blocking_snapshots stores NO wait_resource column, so for a lock it could not resolve,
           contentious_object IS the raw resource — hobt id and lock hash included — and that merge feeds
           the blocking GRIDS and both get_blocking MCP tools as well as the alert path. Rewriting rows
           there would fix the fingerprint by deleting the only copy of the evidence an operator needs to
           chase the lock. */
        var items = new List<BlockedProcessAlertRow>();
        BlockedProcessReportMerge.AppendDmvFallbackRows(items, new List<BlockedProcessAlertRow>
        {
            Row("KEY: 6:72057594041991168 (8194443284a0)", 55, 66, 0, BlockedProcessAlertRow.DmvSnapshotSource),
            Row("[dbo].[Users]", 57, 68, 5, BlockedProcessAlertRow.DmvSnapshotSource),
        });

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.ContentiousObject == "KEY: 6:72057594041991168 (8194443284a0)");
        Assert.Contains(items, i => i.ContentiousObject == "[dbo].[Users]");
    }

    [Fact]
    public void A_Producer_That_Never_Touches_The_Merge_Is_Normalized_Too()
    {
        /* Two independent producers group blocking rows — the live alert builders, which go through the
           merge, and the analysis drill-down, whose SQL UNIONs the two collectors before anything can tell
           them apart. Normalizing inside the grouper is the only placement neither can bypass, which is
           also what makes a future third producer safe by default. */
        var groups = Group(new[] { Row("PAGE: 6:1:2093167", 55, 66, 0, BlockedProcessAlertRow.DmvSnapshotSource) });

        Assert.Equal("Unresolved: page lock, database: StackOverflow2013", groups[0].ContentiousObject);
    }

    /* ── the transform ── */

    [Theory]
    [InlineData("[dbo].[Users]", "dbo.Users")]
    [InlineData("[My Schema].[Order Details]", "My Schema.Order Details")]
    [InlineData("[weird]]name].[t]", "weird]name.t")]
    public void A_Quotename_Path_Loses_Its_Brackets_And_Keeps_Its_Characters(string stored, string expected)
    {
        /* Unquoting rather than replacing '[' and ']' with nothing: QUOTENAME doubles an embedded ']', and
           a blind strip would turn "weird]name" into "weirdname" — a different object. */
        Assert.Equal(expected, ContentiousObjectLabel.Normalize(stored, "StackOverflow2013"));
    }

    [Theory]
    [InlineData("KEY: 6:72057594041991168 (8194443284a0)", "Unresolved: key lock, database: StackOverflow2013")]
    [InlineData("PAGE: 6:1:2093167", "Unresolved: page lock, database: StackOverflow2013")]
    [InlineData("RID: 6:1:2093167:0", "Unresolved: rid lock, database: StackOverflow2013")]
    [InlineData("OBJECT: 6:1170871288:0", "Unresolved: object lock, database: StackOverflow2013")]
    [InlineData("ALLOCATION_UNIT: 6:72057594041991168", "Unresolved: allocation_unit lock, database: StackOverflow2013")]
    public void A_Raw_Wait_Resource_Becomes_The_Report_Collectors_Sentinel(string stored, string expected)
    {
        /* Mirrors the collector's own classifier, including its fallback of taking the leading token for a
           resource type it has no special case for — verified live against the batch, which labels an
           unknown token exactly this way. */
        Assert.Equal(expected, ContentiousObjectLabel.Normalize(stored, "StackOverflow2013"));
    }

    [Fact]
    public void An_Unnamed_Database_Takes_The_Same_Placeholder_The_Collector_Writes()
    {
        Assert.Equal("Unresolved: key lock, database: unknown", ContentiousObjectLabel.Normalize("KEY: 6:123 (aa)", null));
        Assert.Equal("Unresolved: key lock, database: unknown", ContentiousObjectLabel.Normalize("KEY: 6:123 (aa)", "   "));
    }

    /* ── and what it must NOT touch ── */

    [Theory]
    [InlineData("dbo.Users")]
    [InlineData("Unresolved: key lock, database: StackOverflow2013")]
    [InlineData("Unresolved: page lock, database: StackOverflow2013 (page reallocated)")]
    [InlineData("Unresolved: database: unknown")]
    [InlineData("")]
    public void A_Label_Already_In_Report_Form_Is_Returned_Untouched(string stored)
    {
        /* This is what lets the transform be applied where the source is unknowable — the drill-down list
           UNIONs both collectors before anything can tell them apart. A plain name has no ": " to match the
           wait-resource shape, and "Unresolved: " fails it too because that pattern demands an ALL-CAPS
           leading token. Note the third case: #1865's reason suffix must survive, or the fix that put it
           there would be quietly undone here. */
        Assert.Equal(stored, ContentiousObjectLabel.Normalize(stored, "StackOverflow2013"));
    }

    [Fact]
    public void The_Transform_Is_Idempotent()
    {
        /* It runs at more than one surface and a value can pass through twice — the merge normalizes rows
           the drill-down may normalize again. A second pass must be a no-op or the surfaces would have to
           know about each other. */
        foreach (var stored in new[] { "[dbo].[Users]", "KEY: 6:1 (aa)", "dbo.Users", "Unresolved: key lock, database: X", "" })
        {
            var once = ContentiousObjectLabel.Normalize(stored, "X");
            Assert.Equal(once, ContentiousObjectLabel.Normalize(once, "X"));
        }
    }

    [Fact]
    public void Something_That_Is_Neither_Shape_Is_Left_Alone()
    {
        /* Fail soft: an unparseable bracketed value or an unexpected format is returned as stored rather
           than mangled into a sentinel that would assert something untrue about the row. */
        Assert.Equal("[unclosed", ContentiousObjectLabel.Normalize("[unclosed", "X"));
        Assert.Equal("[a].", ContentiousObjectLabel.Normalize("[a].", "X"));
        Assert.Equal("just some text", ContentiousObjectLabel.Normalize("just some text", "X"));
    }

    /* ── the label's own database (#1876's second half) ── */

    [Fact]
    public void The_Unresolved_Label_Names_The_Lock_Resources_Database_Not_The_Events()
    {
        /* For a cross-database lock these differ, and the event's database_id names where the blocked
           session was RUNNING — not where the object nobody could name lives. Verified live on SQL 2022
           with the same seeded row under both expressions: the old one said "database: master" for a KEY
           lock whose resource was in StackOverflow2013. COALESCE keeps the event's database as the
           fallback, so a resource shape carrying no database id reads exactly as it did before. */
        var text = BlockedProcessReportCollector.Instance.BuildQuery(Context()).Text;

        Assert.Contains(
            "N'database: ' + COALESCE(DB_NAME(b.resource_database_id), DB_NAME(b.database_id), N'unknown')",
            text, StringComparison.Ordinal);
        Assert.DoesNotContain("N'database: ' + ISNULL(DB_NAME(b.database_id)", text, StringComparison.Ordinal);
    }

    /* ── helpers ── */

    private static CollectorContext Context() => new()
    {
        ServerId = 42,
        ServerName = "test-server",
        CollectionTime = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc),
        Deltas = null!,
        Target = new CollectorTargetInfo(),
    };

    private static BlockedProcessAlertRow Row(string contentiousObject, int blocked, int blocking, int minute, string source) => new()
    {
        EventTime = new DateTime(2026, 7, 30, 12, minute, 0, DateTimeKind.Utc),
        DatabaseName = "StackOverflow2013",
        BlockedSpid = blocked,
        BlockingSpid = blocking,
        WaitTimeMs = 9000,
        LockMode = "X",
        BlockedSqlText = "UPDATE dbo.Users SET x = 1;",
        BlockingSqlText = "BEGIN TRAN;",
        ContentiousObject = contentiousObject,
        Source = source,
    };

    private static List<BlockingIncidentGrouper.BlockingGroup> Group(IEnumerable<BlockedProcessAlertRow> rows) =>
        BlockingIncidentGrouper.Group("test-server", rows.Select(r =>
            new BlockingIncidentGrouper.BlockedEvent(
                r.DatabaseName, r.ContentiousObject, r.BlockedSqlText, r.BlockingSqlText, r.WaitTimeMs, r.LockMode)));
}
