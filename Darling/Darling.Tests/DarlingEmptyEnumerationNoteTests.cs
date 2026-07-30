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
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1837 (minimal core), Darling half: an enumerated collector whose enumeration query returns NO items
/// recorded a bare SUCCESS/0-rows row, indistinguishable from a healthy collector whose databases were
/// simply quiet. The status deliberately STAYS SUCCESS; the fix is a fixed, greppable message on the
/// collection_log row, carried out of the runner on <see cref="CollectorRunResult.Note"/> — the Darling
/// twin of Lite's <c>_lastCollectionNote</c> field. Lite.Tests (<c>EmptyEnumerationNoteTests</c>) pins
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
    public void Runner_Annotates_The_Zero_Items_Branch_With_The_Shared_Message()
    {
        var source = ReadRepoFile(Path.Combine("Darling", "PerformanceMonitor.Darling.Service", "DarlingCollectorRunner.cs"));

        Assert.Contains("new CollectorRunResult(0, sqlMs, 0, EnumeratedCollectorDriver.EmptyEnumerationMessage)", source);

        /* Via the shared constant, never a copy of the text — a literal here is exactly the drift this
           fix exists to prevent. */
        Assert.DoesNotContain("\"enumeration yielded 0 items", source);
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
