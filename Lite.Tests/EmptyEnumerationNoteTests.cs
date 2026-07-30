/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// #1837 (minimal core), Lite half: an enumerated collector whose enumeration query returns NO items
/// used to record a bare SUCCESS/0-rows row — byte-identical to a healthy collector whose databases
/// were simply quiet, and to query_store finding no Query-Store-enabled database at all. The status
/// deliberately STAYS SUCCESS (nothing failed; the health-banding design is the rest of #1837); the fix
/// is a fixed, greppable message on the collection_log row that normally leaves that column null.
///
/// The zero-items branch itself needs a live SQL Server (it is the tail of a real enumeration read), so
/// the wiring is pinned at source where it lives — the #1805 LockTimeoutYieldTests idiom. What IS
/// reachable, and is the actual regression risk this fix introduces, is pinned for real: a non-null
/// message on a SUCCESS row must stay inert everywhere health is computed.
/// </summary>
public class EmptyEnumerationNoteTests
{
    private const int ServerId = 4242;

    /* ── the shared message (the cross-app contract) ── */

    [Fact]
    public void The_Message_Is_Fixed_And_Shared_By_Both_Runners()
    {
        /* Fixed text, because its whole job is to be greppable in a support log. It lives on the shared
           EnumeratedCollectorDriver — the one owner of the enumerated path — so Lite and Darling cannot
           drift on the wording an operator searches for. Darling.Tests pins the identical literal. */
        Assert.Equal(
            "enumeration yielded 0 items - nothing to collect this cycle",
            EnumeratedCollectorDriver.EmptyEnumerationMessage);
    }

    /* ── the runner wiring (source pins — the branch is the tail of a live enumeration read) ── */

    [Fact]
    public void Runner_Annotates_The_Zero_Items_Branch_With_The_Shared_Message()
    {
        var source = File.ReadAllText(FindRepoFile(
            Path.Combine("Lite", "Services", "RemoteCollectorService.DefinitionRunner.cs")));

        Assert.Contains("_lastCollectionNote = EnumeratedCollectorDriver.EmptyEnumerationMessage;", source);

        /* Via the shared constant, never a copy of the text — a literal here is exactly the drift this
           fix exists to prevent. */
        Assert.DoesNotContain("\"enumeration yielded 0 items", source);
    }

    [Fact]
    public void Runner_Resets_The_Note_With_The_Timing_Fields_Every_Run()
    {
        /* The note rides the same per-call field convention as _lastSqlMs/_lastDuckDbMs, so it must be
           cleared at the top of every definition run — otherwise one empty enumeration would annotate
           the NEXT collector's row too. */
        var source = File.ReadAllText(FindRepoFile(
            Path.Combine("Lite", "Services", "RemoteCollectorService.DefinitionRunner.cs")));

        Assert.Contains("_lastCollectionNote = null;", source);
    }

    [Fact]
    public void RunCollectorAsync_Carries_The_Note_Onto_The_Collection_Log_Row()
    {
        /* The note reaches the row through errorMessage — the parameter LogCollectionAsync writes to the
           error_message column — assigned on the success path only, where errorMessage is provably null
           (only the catch blocks assign it). */
        var source = File.ReadAllText(FindRepoFile(
            Path.Combine("Lite", "Services", "RemoteCollectorService.cs")));

        Assert.Contains("errorMessage = _lastCollectionNote;", source);
    }

    /* ── the neutrality this fix depends on (real assertions) ── */

    [Fact]
    public void A_Message_On_A_SUCCESS_Row_Is_Not_An_Erroring_Collector()
    {
        /* The regression this fix could have caused: SUCCESS rows never carried a message before, so
           anything that treated "has a message" as "failed" would now mark every quiet enumerated
           collector unhealthy. Health tracking keys on STATUS — a SUCCESS resets the streak and ignores
           the message entirely — and that must stay true. */
        var service = CreateService();

        service.RecordCollectorResult(ServerId, "query_store", "SUCCESS",
            EnumeratedCollectorDriver.EmptyEnumerationMessage);

        var summary = service.GetHealthSummary(ServerId);
        Assert.Equal(0, summary.ErroringCollectors);
        Assert.Empty(summary.Errors);
    }

    [Fact]
    public void A_Message_On_A_SUCCESS_Row_Still_Clears_A_Real_Error_Streak()
    {
        /* An annotated success is still a success: the collector demonstrably ran, so it must clear a
           prior FAILING streak exactly as an unannotated one does. */
        var service = CreateService();

        service.RecordCollectorResult(ServerId, "query_store", "ERROR", "genuine failure");
        Assert.Equal(1, service.GetHealthSummary(ServerId).ErroringCollectors);

        service.RecordCollectorResult(ServerId, "query_store", "SUCCESS",
            EnumeratedCollectorDriver.EmptyEnumerationMessage);
        Assert.Equal(0, service.GetHealthSummary(ServerId).ErroringCollectors);
    }

    [Fact]
    public void Last_Error_Stays_Gated_On_Failure_Statuses_Not_On_Message_Presence()
    {
        /* The read-side guard that keeps this note out of the Collection Health "last error" surface. A
           broadening to error_message IS NOT NULL would turn every quiet enumeration cycle into a fake
           last-error — the note is deliberately visible ONLY in the raw collection-log detail grid. */
        var source = File.ReadAllText(FindRepoFile(
            Path.Combine("Lite", "Services", "LocalDataService.CollectionHealth.cs")));

        Assert.Contains("MAX(CASE WHEN status IN ('ERROR', 'PERMISSIONS') THEN error_message END) AS last_error", source);
        Assert.DoesNotContain("error_message IS NOT NULL", source);
    }

    /* ── helpers ── */

    private static RemoteCollectorService CreateService() =>
        new(duckDb: null!, serverManager: null!, scheduleManager: null!);

    private static string FindRepoFile(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"Could not locate {relativePath} walking up from {AppContext.BaseDirectory}");
    }
}
