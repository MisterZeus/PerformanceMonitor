/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Linq;
using PerformanceMonitor.Common;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Darling's half of the #1506 pin.
///
/// Darling inherited this bug by copy: it carried its own duplicate of the master-access error list,
/// commented "mirroring Lite's #857 behavior", including the same two reachability errors (40615
/// firewall, 40613 temporarily-unavailable) that must never be read as a permanent statement about a
/// login's right to read master.
///
/// Both SKUs now classify through the shared <see cref="SqlErrorClassification"/>, so the list cannot
/// be edited in one and not the other. These tests exist so the invariant is enforced from Darling's
/// suite too — Lite's copy of it protects Lite, and protecting only Lite is how this happened.
///
/// Also #1631, the over-correction: #1506 removed 40615 from the rights list (right) AND from the
/// single-database fallback path (wrong). Azure evaluates DATABASE-level firewall rules first, so a
/// user database can be fully reachable while master is firewalled off — the #857 case. "Not a rights
/// denial" and "no fallback" are separate questions, pinned independently below.
/// </summary>
public class AzureMasterFallbackTests
{
    /// <summary>
    /// The #1506 half. 40615 is a firewall rejection at the logical server and 40613 is "retry later" —
    /// neither says anything about whether this login may read master.
    /// </summary>
    [Theory]
    [InlineData(40615)]
    [InlineData(40613)]
    public void Reachability_Errors_Are_Not_Master_Access_Denied(int errorNumber)
    {
        Assert.False(SqlErrorClassification.IsMasterAccessDenied(errorNumber));
    }

    /// <summary>
    /// #857 still holds: a contained user that exists only in a user database really does get these
    /// when it opens master, and single-database fallback is the right answer.
    /// </summary>
    [Theory]
    [InlineData(229)]
    [InlineData(230)]
    [InlineData(916)]
    [InlineData(4060)]
    [InlineData(18456)]
    public void Permission_Errors_Are_Still_Master_Access_Denied(int errorNumber)
    {
        Assert.True(SqlErrorClassification.IsMasterAccessDenied(errorNumber));
    }

    /// <summary>
    /// #1631. A 40615 at the logical server is compatible with a database-level firewall rule that
    /// leaves the user database reachable, so the single-database fallback must still fire — even
    /// though 40615 is (correctly) not a rights denial.
    /// </summary>
    [Fact]
    public void Firewall_Rejection_At_The_Server_Still_Falls_Back_To_The_Database()
    {
        Assert.True(DarlingCollectorRunner.ShouldFallBackToSingleDatabaseError(40615));
    }

    /// <summary>
    /// Every rights denial still implies the fallback — the fallback set is a superset, never a
    /// replacement, of the #857 list. 40613 stays out: transient, so retry rather than degrade.
    /// </summary>
    [Theory]
    [InlineData(229, true)]
    [InlineData(230, true)]
    [InlineData(916, true)]
    [InlineData(4060, true)]
    [InlineData(18456, true)]
    [InlineData(40613, false)]
    public void Fallback_Set_Is_A_Superset_Of_The_Rights_Set(int errorNumber, bool expected)
    {
        Assert.Equal(expected, DarlingCollectorRunner.ShouldFallBackToSingleDatabaseError(errorNumber));
    }

    /// <summary>
    /// The structural invariant behind the two lists, asserted on the SETS rather than on examples: a
    /// rights denial must always imply the fallback, so the rights set can only ever be a subset.
    ///
    /// This also guards a real footgun — the fallback set is constructed FROM the rights set, so it
    /// depends on static-initializer order. Moving either field above the other would leave the fallback
    /// set holding only 40615 and silently disable the #857 fallback for every permission error.
    /// </summary>
    [Fact]
    public void Every_Rights_Denial_Is_Also_A_Fallback_Trigger()
    {
        var missing = SqlErrorClassification.MasterAccessDeniedErrorNumbers
            .Where(e => !SqlErrorClassification.ShouldFallBackToSingleDatabase(e))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Error(s) {string.Join(", ", missing)} deny access to master but do not trigger the " +
            "single-database fallback — #857 collection would fail outright for them.");
    }

    /// <summary>
    /// No error may mean both "retry, this will pass" and "give up, degrade to single-database". 40613
    /// meant both, which is the whole #1506 bug. Asserted against the FALLBACK set — the broader of the
    /// two give-up sets, so this covers the rights set implicitly.
    /// </summary>
    [Fact]
    public void No_Error_Is_Both_Transient_And_Permanently_Fatal()
    {
        var contradictory = SqlErrorClassification.TransientErrorNumbers
            .Where(SqlErrorClassification.ShouldFallBackToSingleDatabase)
            .ToList();

        Assert.True(
            contradictory.Count == 0,
            $"Error(s) {string.Join(", ", contradictory)} are treated as retryable-transient AND as a " +
            "reason to degrade to single-database collection. Pick one — see #1506.");
    }

    /// <summary>
    /// Lite and Darling must reach the same verdict for every error either one can see. They used to
    /// hold separate lists; this asserts the shared one is genuinely the only one in play.
    /// </summary>
    [Fact]
    public void Darling_Classifies_Identically_To_The_Shared_Source_Of_Truth()
    {
        int[] everyErrorWeReasonAbout =
        [
            229, 230, 297, 300, 916, 4060, 18456, 40613, 40615,
            -2, -1, 2, 53, 233, 10053, 10054, 10060, 10061,
            40143, 40197, 40501, 49918, 49919, 49920
        ];

        foreach (var errorNumber in everyErrorWeReasonAbout)
        {
            Assert.Equal(
                SqlErrorClassification.ShouldFallBackToSingleDatabase(errorNumber),
                DarlingCollectorRunner.ShouldFallBackToSingleDatabaseError(errorNumber));
        }
    }
}
