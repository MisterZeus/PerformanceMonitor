/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The verdict half of the #1873 residue check, pinned without a store.
///
/// <para><see cref="LivePostgresStoreFixture.DisposeAsync"/> can only be observed by running the whole live
/// collection and then reading how it failed, which makes it a poor place to pin WORDING and an impossible
/// place to pin the cases that must stay quiet. The decision — accuse, or say nothing — is separated out here
/// so all four combinations are covered on every run, gated or not.</para>
///
/// <para>The quiet case is the one worth having: a residue check that fired spuriously would be turned off
/// within a week, and then the defect it was written for comes back with the alarm already disabled.</para>
/// </summary>
public sealed class LiveStoreResidueReportTests
{
    [Fact]
    public void ACleanRun_IsNotAccused()
        => Assert.Null(LivePostgresStoreFixture.BuildResidueReport([], []));

    /// <summary>
    /// Leaked relations with an EMPTY ledger is its own diagnosis: nothing gave up, so nothing lost a race —
    /// something creates these and never attempts removal at all. Without the distinction the next reader
    /// goes looking through cleanup code for a bug that is not there.
    /// </summary>
    [Fact]
    public void LeakedRelationsWithNoLedger_NameThemAndSayNoCleanupEvenTried()
    {
        var report = LivePostgresStoreFixture.BuildResidueReport(["query_stats_hourly", "tick1778_fresh"], []);

        Assert.NotNull(report);
        Assert.Contains("collect.query_stats_hourly", report, StringComparison.Ordinal);
        Assert.Contains("collect.tick1778_fresh", report, StringComparison.Ordinal);
        Assert.Contains("never tries to remove them at all", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ledger entry with NOTHING leaked still fails. An armed retention policy that could not be removed
    /// leaves no relation behind and will drop another test's chunks on its own schedule — the residue with
    /// the worst blast radius is the one the catalog diff cannot see.
    /// </summary>
    [Fact]
    public void ALedgerEntryAlone_IsEnoughToAccuse()
    {
        var report = LivePostgresStoreFixture.BuildResidueReport(
            [], ["retention policy on collect.query_stats survived 7 removal attempts"]);

        Assert.NotNull(report);
        Assert.Contains("retention policy on collect.query_stats", report, StringComparison.Ordinal);

        /* ...and it must NOT claim relations leaked when none did. */
        Assert.DoesNotContain("did not remove (", report, StringComparison.Ordinal);
        Assert.DoesNotContain("never tries to remove them at all", report, StringComparison.Ordinal);
    }

    /// <summary>Both together: the catalog says WHAT, the ledger says WHOSE, and the report carries both.</summary>
    [Fact]
    public void RelationsAndLedgerTogether_AreBothReported()
    {
        var report = LivePostgresStoreFixture.BuildResidueReport(
            ["query_stats_db_hourly"],
            ["continuous aggregate collect.query_stats_db_hourly survived 7 removal attempts — "
             + "PostgresException (40P01): deadlock detected"]);

        Assert.NotNull(report);
        Assert.Contains("collect.query_stats_db_hourly", report, StringComparison.Ordinal);
        Assert.Contains("40P01", report, StringComparison.Ordinal);
        Assert.DoesNotContain("never tries to remove them at all", report, StringComparison.Ordinal);
    }
}
