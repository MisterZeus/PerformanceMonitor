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

    /// <summary>
    /// #2210: the re-verify cursor paces itself off <c>RefreshAfter</c> and NEVER touches the watermark. The
    /// slice is a row count over an id range, which is the whole point — the old expiry walked BYTES and could
    /// not finish inside a day on the catalogs that mattered (15.9 to 107.5 hours measured), so those restarted
    /// forever. Redstone's 77k ids at a 5-minute cadence over a 1-day sweep is ~267 ids per pass.
    /// </summary>
    [Fact]
    public void CursorSlice_PacesASweepWithinTheRefreshPeriod_AndNeverReturnsZeroForALiveCatalog()
    {
        var day = TimeSpan.FromDays(1);
        var cadence = TimeSpan.FromMinutes(5);

        var redstone = QueryStorePlanMap.CursorSliceWidth(77_176, day, cadence);
        Assert.InRange(redstone, 200, 350);

        /* A sweep must actually cover the range within the period: slice * passes >= watermark. */
        var passes = day.Ticks / cadence.Ticks;
        Assert.True(redstone * passes >= 77_176, "the sweep must cover the id range inside one refresh period");

        /* Never zero for a live catalog, and never wider than the range itself. */
        Assert.True(QueryStorePlanMap.CursorSliceWidth(10, day, cadence) > 0);
        Assert.Equal(10, QueryStorePlanMap.CursorSliceWidth(10, day, cadence));

        /* A fresh database has no watermark to re-verify, so there is nothing to slice. */
        Assert.Equal(0, QueryStorePlanMap.CursorSliceWidth(0, day, cadence));
    }

    /// <summary>
    /// #2210: the DIMENSION must outlive the MAP, expressed the way it actually matters — as cutoff DATES from
    /// the two real code paths, not as the margin constants they happen to be derived from. An earlier cutoff
    /// deletes fewer rows, so the dim's cutoff has to be strictly earlier than the map's.
    ///
    /// <para>The asymmetry is the reason this is pinned. A pruned map row whose dim row survives renders a plan
    /// as "not collected" and leaves some bytes unreclaimed until the dim's own horizon passes — visible and
    /// self-correcting. A pruned DIM row whose map row survives is a reader resolving a live fact to absent
    /// content, silently, weeks after the cause. Only one of those is recoverable, and the margin ordering is
    /// what makes it the only reachable one.</para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(90)]
    public void DimensionOutlivesTheMap_AtEveryFactRetention(int factRetentionDays)
    {
        var dimCutoff = DarlingRetention.ComputeDimensionCutoff(Now, factRetentionDays, oldestSurvivingDigestFact: null);
        var mapCutoff = Now.AddDays(-(factRetentionDays + QueryStorePlanMap.PruneMarginDays));

        Assert.True(dimCutoff < mapCutoff,
            $"the dim GC would take content the map still points at: dim cutoff {dimCutoff:o} is not earlier " +
            $"than map cutoff {mapCutoff:o} at {factRetentionDays}d retention");
    }

    /// <summary>
    /// The invariant's own guard, and the direction it fails in. <see cref="TimescaleSupport.ChunkIntervalDays"/>
    /// is where the dim's margin comes from, so shrinking it is the realistic way somebody inverts this without
    /// touching either margin deliberately — at 0 the two margins meet and the ordering is gone.
    /// </summary>
    [Fact]
    public void MarginOrdering_HoldsAtTheLiveChunkInterval_AndFailsWhenTheMarginsMeet()
    {
        Assert.True(QueryStorePlanMap.MarginOrderingHolds(TimescaleSupport.ChunkIntervalDays));
        Assert.False(QueryStorePlanMap.MarginOrderingHolds(0));
    }

    /// <summary>
    /// The measured clamp cannot protect Query Store digests, which is why the batch-touch refresh is the whole
    /// protection (#2210). The clamp reads the oldest surviving DIGEST-CARRYING fact, and the digest-carrying
    /// fact tables are exactly the two in <see cref="PayloadDimensions.All"/> — Query Store is deliberately not
    /// among them, because its facts resolve through the map instead of carrying a digest column.
    ///
    /// <para>Pinned so that adding a query_store entry to <c>All</c> — the tempting way to "fix" the blindness —
    /// fails here and sends the reader to the comment explaining that the entry would describe a column that
    /// does not exist.</para>
    /// </summary>
    [Fact]
    public void TheMeasuredClamp_IsBlindToQueryStoreFacts_ByConstruction()
    {
        Assert.DoesNotContain("query_store_stats", PayloadDimensions.All.Select(d => d.TargetTable));
        Assert.DoesNotContain("query_store_stats", PayloadDimensions.DigestPredicateByTable.Keys);
    }
}
