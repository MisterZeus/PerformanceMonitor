/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1795: the dimension GC's cutoff math and the three-way alignment behind its floor probe. The probe's
/// speed contract is that its WHERE clause EXACTLY matches the V39 partial index predicate, and both are
/// derived from <see cref="PayloadDimensions.All"/> — these pins hold the derived strings AND the V39 DDL
/// to each other, so a new dimension column cannot land in the map without landing in the index, and the
/// index predicate cannot drift from the probe's.
/// </summary>
public sealed class DarlingDimensionGcBoundTests
{
    private static readonly DateTime Now = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Unspecified);

    /* widest = 30 → assumed cutoff = now - (30 + ChunkIntervalDays + 1). */
    private static DateTime Assumed => Now.AddDays(-(30 + TimescaleSupport.ChunkIntervalDays + 1));

    [Fact]
    public void HealthyFloor_LeavesTheAssumedHorizonAlone()
    {
        /* Floor well inside retention (facts purging normally): the measured bound (floor - 1d) sits
           NEWER than the assumed cutoff, and the cutoff must not move forward past the assumed horizon —
           the GC never gets MORE aggressive than today. */
        var cutoff = DarlingRetention.ComputeDimensionCutoff(Now, 30, Now.AddDays(-4));
        Assert.Equal(Assumed, cutoff);
    }

    [Fact]
    public void HeldFloor_ClampsTheCutoffToOneDayBeforeIt()
    {
        /* The #1795 field state: the clamp holds 45-day-old facts, older than the assumed horizon. The
           cutoff follows the MEASURED floor minus the one-day last_seen margin, so content those facts
           reference survives while anything older is reclaimed. */
        var floor = Now.AddDays(-45);
        var cutoff = DarlingRetention.ComputeDimensionCutoff(Now, 30, floor);
        Assert.Equal(floor.AddDays(-1), cutoff);
        Assert.True(cutoff < Assumed);
    }

    [Fact]
    public void NoDigestFacts_FallBackToTheAssumedHorizon()
    {
        /* A fresh (or fully-aged) store has no digest-carrying facts at all: nothing can dangle, and
           last_seen still bounds what is old enough to take. */
        var cutoff = DarlingRetention.ComputeDimensionCutoff(Now, 30, oldestSurvivingDigestFact: null);
        Assert.Equal(Assumed, cutoff);
    }

    [Fact]
    public void DigestPredicates_AreExactlyTheDeclaredColumns_InDeclarationOrder()
    {
        /* The strings themselves, pinned: the probe filters on these, the V39 index is declared with
           these, and both derive from PayloadDimensions.All. */
        Assert.Equal(
            "query_text_digest IS NOT NULL OR query_plan_digest IS NOT NULL",
            PayloadDimensions.DigestPredicateByTable["query_stats"]);
        Assert.Equal(
            "query_plan_digest IS NOT NULL",
            PayloadDimensions.DigestPredicateByTable["procedure_stats"]);
        Assert.Equal(2, PayloadDimensions.DigestPredicateByTable.Count);
    }

    [Fact]
    public void V39Indexes_UseExactlyTheProbePredicates()
    {
        var v39 = PgMigrations.Scripts.Single(m => m.Version == 39).Sql;

        foreach (var (factTable, predicate) in PayloadDimensions.DigestPredicateByTable)
        {
            Assert.Contains($"ON {factTable} (collection_time)", v39, StringComparison.Ordinal);
            Assert.Contains($"WHERE {predicate}", v39, StringComparison.Ordinal);
        }
    }
}
