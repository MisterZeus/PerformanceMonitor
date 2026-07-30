/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.IO;
using System.Runtime.CompilerServices;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Common;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1837 (minimal core), Darling half: an enumerated collector whose enumeration query returns NO items
/// recorded a bare SUCCESS/0-rows row, indistinguishable from a healthy collector whose databases were
/// simply quiet. The status deliberately STAYS SUCCESS; the fix is a fixed, greppable message on the
/// collection_log row, carried out of the runner on <see cref="CollectorRunResult.Note"/> — the Darling
/// twin of Lite's per-run telemetry slot. Lite.Tests (<c>EmptyEnumerationNoteTests</c>) pins
/// the same contract on the same shared constant; parity is the point.
///
/// The zero-items branch needs a live SQL Server, so its wiring is pinned at source (the #1805
/// DarlingLockTimeoutYieldTests idiom). The record's carrying behavior is pinned for real.
/// </summary>
public sealed class DarlingEmptyEnumerationNoteTests
{
    [Fact]
    public void The_Message_Is_Fixed_And_Shared_With_Lite()
    {
        /* The identical literal Lite.Tests pins, asserted independently here: if either app's copy of
           this expectation is edited alone, one suite fails. The value lives once, on the shared
           EnumeratedCollectorDriver, so the runners cannot drift on what an operator greps for. */
        Assert.Equal(
            "enumeration yielded 0 items - nothing to collect this cycle",
            EnumeratedCollectorDriver.EmptyEnumerationMessage);
    }

    [Fact]
    public void An_Ordinary_Run_Result_Carries_No_Note()
    {
        /* The default keeps every other collector's row exactly as it was — message column null. */
        Assert.Null(new CollectorRunResult(12, 34, 56).Note);
    }

    [Fact]
    public void A_Run_Result_Round_Trips_The_Note()
    {
        var result = new CollectorRunResult(0, 5, 0, EnumeratedCollectorDriver.EmptyEnumerationMessage);

        Assert.Equal(EnumeratedCollectorDriver.EmptyEnumerationMessage, result.Note);
        Assert.Equal(0, result.Rows);
    }

    [Fact]
    public void Runner_Takes_Its_Note_From_The_Shared_Enumeration_Read()
    {
        var source = ReadRepoFile(Path.Combine("Darling", "PerformanceMonitor.Darling.Service", "DarlingCollectorRunner.cs"));

        /* Strengthened from the original "constructs the result with the shared constant" pin when
           #1837's probe-failure contract landed: the note can now be the empty-enumeration message, the
           probe-failure summary, or both, so pinning ONE of those literals would no longer prove the host
           cannot drift. Routing the whole enumeration read — items, probe failures, and the composed
           note — through the shared driver does, because there is then no host-side text at all. Lite's
           twin pin asserts the identical routing on its runner. */
        Assert.Contains("EnumeratedCollectorDriver.ReadEnumerationAsync(enumerationReader, cancellationToken)", source);
        Assert.Contains("new CollectorRunResult(0, sqlMs, 0, enumeration.Note)", source);

        /* Via the shared driver, never a copy of the text — a literal here is exactly the drift this
           fix exists to prevent. */
        Assert.DoesNotContain("\"enumeration yielded 0 items", source);
        Assert.DoesNotContain("failed their enumeration probe", source);
    }

    [Fact]
    public void Runner_Carries_The_Note_Onto_The_Success_Return_Too()
    {
        /* The partial case: items WERE enumerated but some of their probes failed, so the run collects
           normally and returns through the success path at the bottom of the method — which built a
           note-less result until #1837's contract. Without this the probe summary would reach the store
           only when the enumeration came back completely empty. */
        var source = ReadRepoFile(Path.Combine("Darling", "PerformanceMonitor.Darling.Service", "DarlingCollectorRunner.cs"));

        Assert.Contains("collectionNote = enumeration.Note;", source);
        Assert.Contains("return new CollectorRunResult(rowsWritten, sqlMs, storageMs, collectionNote);", source);
    }

    [Fact]
    public void Worker_Passes_The_Note_To_The_Collection_Log_Write()
    {
        /* The note reaches error_message through LogCollectionAsync's message parameter, on the SUCCESS
           write only — the status argument on that same call must stay "SUCCESS". */
        var source = ReadRepoFile(Path.Combine("Darling", "PerformanceMonitor.Darling.Service", "DarlingWorker.cs"));

        Assert.Contains("\"SUCCESS\", result.Rows, result.SqlMs, result.StorageMs, result.Note", source);
    }

    [Fact]
    public void Last_Error_Stays_Gated_On_Failure_Statuses_Not_On_Message_Presence()
    {
        /* The read-side guard that keeps this note out of the Collection Health "last error" surface, in
           both readers. A broadening to error_message IS NOT NULL would turn every quiet enumeration
           cycle into a fake last-error. */
        foreach (var relative in new[]
        {
            Path.Combine("Darling", "PerformanceMonitor.Darling.Viewer", "ViewerDataService.CollectionHealth.cs"),
            Path.Combine("Darling", "PerformanceMonitor.Darling.Service", "Mcp", "DarlingDataReader.cs"),
        })
        {
            var source = ReadRepoFile(relative);
            Assert.Contains("MAX(CASE WHEN status IN ('ERROR', 'PERMISSIONS') THEN error_message END) AS last_error", source);
            Assert.DoesNotContain("error_message IS NOT NULL", source);
        }
    }

    /* ── #1837 health visibility: the note gets its own column, and it is NOT an error ── */

    [Fact]
    public void Health_Reads_Surface_The_Note_Gated_On_SUCCESS()
    {
        /* Both Darling readers, matching Lite. Gated on SUCCESS, not on "not a failure status": the
           runners attach a note only to the SUCCESS write, and the looser complement of last_error would
           drag Darling's SESSION_MISSING rows — a real capture fault with its own self-alert — into a
           column whose tooltip promises it is NOT an error. Written as MAX/COUNT over a CASE rather than
           a NULL test on purpose: no read on this surface may key on message PRESENCE (the pin above). */
        foreach (var relative in new[]
        {
            Path.Combine("Darling", "PerformanceMonitor.Darling.Viewer", "ViewerDataService.CollectionHealth.cs"),
            Path.Combine("Darling", "PerformanceMonitor.Darling.Service", "Mcp", "DarlingDataReader.cs"),
        })
        {
            var source = ReadRepoFile(relative);
            Assert.Contains("MAX(CASE WHEN status = 'SUCCESS' THEN error_message END) AS last_note", source);
            Assert.Contains("COUNT(CASE WHEN status = 'SUCCESS' THEN error_message END) AS note_count", source);
        }
    }

    [Fact]
    public void The_Viewers_Fleet_Read_Projects_The_Same_Columns_As_The_Per_Server_One()
    {
        /* Both viewer health reads feed ONE mapper (MapHealthRow), so a column added to the per-server
           projection alone would make the fleet read throw on the new ordinal at runtime — a defect no
           per-SQL test would catch, because each SQL string is individually valid. */
        var source = ReadRepoFile(Path.Combine("Darling", "PerformanceMonitor.Darling.Viewer", "ViewerDataService.CollectionHealth.cs"));

        var perServer = ViewerDataService.CollectionHealthSql;
        var fleet = ViewerDataService.FleetCollectionHealthSql;
        foreach (var column in new[] { "AS last_note", "AS note_count" })
        {
            Assert.Contains(column, perServer);
            Assert.Contains(column, fleet);
        }

        Assert.Contains("LastNote = reader.IsDBNull(11)", source);
        Assert.Contains("NoteCount = reader.IsDBNull(12)", source);
    }

    [Fact]
    public void The_Web_Dashboards_Collection_Health_Table_Shows_The_Note_Too()
    {
        /* Darling has THREE Collection Health surfaces, not two: the WPF Viewer grid, the MCP tool, and
           the web dashboard's table, which renders whatever COLLECTOR_COLUMNS lists from that same tool's
           payload. A field added to the tool but not to that list is silently dropped, leaving the
           browser as the one surface still hiding what #1837 exists to show. */
        var source = ReadRepoFile(Path.Combine(
            "Darling", "PerformanceMonitor.Darling.Service", "wwwroot", "js", "pages", "server.js"));

        /* The DEFINITION, not the earlier `columns: COLLECTOR_COLUMNS` use site. */
        var start = source.IndexOf("const COLLECTOR_COLUMNS", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "server.js must still define COLLECTOR_COLUMNS");
        var columns = source[start..];
        columns = columns[..columns.IndexOf("];", System.StringComparison.Ordinal)];

        Assert.Contains("last_error", columns);
        Assert.Contains("last_note", columns);
    }

    [Fact]
    public void The_Note_Never_Reaches_The_Banding()
    {
        /* Constraint (a)/(b) of #1837's design: the band order and its inputs are untouched, so a target
           that is legitimately empty — no user databases, no AGs, nothing matching a filter — keeps
           reading HEALTHY. Two collectors identical except for the note must band identically. */
        var quiet = new CollectorHealthRow
        {
            CollectorName = "query_store",
            TotalRuns = 96,
            SuccessCount = 96,
            LastSuccessTime = System.DateTime.UtcNow.AddMinutes(-5),
            LastRunTime = System.DateTime.UtcNow.AddMinutes(-5),
        };
        var annotated = new CollectorHealthRow
        {
            CollectorName = "query_store",
            TotalRuns = 96,
            SuccessCount = 96,
            LastSuccessTime = quiet.LastSuccessTime,
            LastRunTime = quiet.LastRunTime,
            LastNote = EnumeratedCollectorDriver.EmptyEnumerationMessage,
            NoteCount = 96,
        };

        Assert.Equal(CollectorHealthClassifier.Healthy, quiet.HealthStatus);
        Assert.Equal(quiet.HealthStatus, annotated.HealthStatus);
    }

    [Theory]
    /* Nothing to say — the overwhelmingly common row — stays blank rather than shouting "OK". */
    [InlineData(null, 0L, 96L, "")]
    [InlineData("", 0L, 96L, "")]
    /* A note counted zero times is incoherent input; blank beats a "(0 of N)" that reads like a defect. */
    [InlineData("note", 0L, 96L, "")]
    /* The distinction the issue asks for: sometimes-empty is normal, always-empty is the signal. */
    [InlineData("note", 3L, 96L, "note (3 of 96 runs)")]
    [InlineData("note", 96L, 96L, "note (all 96 runs)")]
    public void Note_Qualifier_Says_How_Much_Of_The_Window_Was_Empty(string? note, long noteCount, long totalRuns, string expected)
    {
        /* The identical expectations Lite.Tests pins, asserted independently here against the identical
           shared helper — Erik's parity rule in test form. */
        Assert.Equal(expected, CollectorHealthClassifier.FormatCollectionNote(note, noteCount, totalRuns));
    }

    [Fact]
    public void Note_Qualifier_Is_The_Shared_One_Both_Apps_Render()
    {
        var row = new CollectorHealthRow
        {
            CollectorName = "query_store",
            TotalRuns = 96,
            LastNote = EnumeratedCollectorDriver.EmptyEnumerationMessage,
            NoteCount = 96,
        };

        Assert.Equal(
            CollectorHealthClassifier.FormatCollectionNote(row.LastNote, row.NoteCount, row.TotalRuns),
            row.NoteFormatted);
        Assert.Contains("(all 96 runs)", row.NoteFormatted);
    }

    /* Locate the repo from this file — the DarlingLockTimeoutYieldTests idiom; no build-output copying. */
    private static string ReadRepoFile(string relative, [CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, relative)))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
