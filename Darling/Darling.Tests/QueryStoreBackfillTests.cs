/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace PerformanceMonitor.Darling.Tests;

/// <summary>
/// Pins the #2022 backfill worker's pure contracts: the hole codec (round-trip, malformed-input
/// conservatism, the merge-widens rule), the horizon DERIVATION (raw tier minus route margin —
/// the #1937 no-second-hand-maintained-number rule), and the state identity that keeps the
/// worker's collector_state rows off the query_store definition (whose "declares NO StateKeys"
/// contract is pinned by CollectorStateContractTests and must survive this feature).
/// </summary>
public sealed class QueryStoreBackfillTests
{
    [Fact]
    public void HoleCodec_RoundTrips_AndRejectsMalformedOrInverted()
    {
        var from = new DateTime(2026, 7, 1, 3, 15, 30, DateTimeKind.Utc).AddTicks(1234567);
        var to = new DateTime(2026, 7, 2, 3, 15, 30, DateTimeKind.Utc);

        var encoded = QueryStoreBackfill.EncodeHole(from, to);
        Assert.True(QueryStoreBackfill.TryDecodeHole(encoded, out var decodedFrom, out var decodedTo));
        Assert.Equal(from, decodedFrom);
        Assert.Equal(to, decodedTo);

        /* Malformed values decode false — the scan treats that as "no hole recorded", the
           conservative direction (the tail logic still runs; nothing throws mid-loop). */
        Assert.False(QueryStoreBackfill.TryDecodeHole("", out _, out _));
        Assert.False(QueryStoreBackfill.TryDecodeHole("not|dates", out _, out _));
        Assert.False(QueryStoreBackfill.TryDecodeHole(from.ToString("o"), out _, out _));
        /* An inverted or empty range is malformed too: from must be strictly before to. */
        Assert.False(QueryStoreBackfill.TryDecodeHole(QueryStoreBackfill.EncodeHole(to, from), out _, out _));
        Assert.False(QueryStoreBackfill.TryDecodeHole(QueryStoreBackfill.EncodeHole(from, from), out _, out _));
    }

    [Fact]
    public void MergeHole_WidensOverExisting_AndStartsFreshOverGarbage()
    {
        var from = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc);

        /* No existing record: the new clamp IS the hole. */
        Assert.Equal((from, to), QueryStoreBackfill.MergeHole(null, from, to));

        /* A repeat outage WIDENS the pending hole in both directions — overwriting would lose the
           unserviced earlier range, a silent hole in a design whose premise is that holes are
           recorded. */
        var earlierWider = QueryStoreBackfill.EncodeHole(from.AddDays(-1), to.AddHours(-12));
        Assert.Equal((from.AddDays(-1), to), QueryStoreBackfill.MergeHole(earlierWider, from, to));

        var laterWider = QueryStoreBackfill.EncodeHole(from.AddHours(12), to.AddDays(1));
        Assert.Equal((from, to.AddDays(1)), QueryStoreBackfill.MergeHole(laterWider, from, to));

        /* Garbage in the state row falls back to the fresh clamp, never a throw. */
        Assert.Equal((from, to), QueryStoreBackfill.MergeHole("garbage", from, to));
    }

    [Fact]
    public void Horizon_IsDerivedFromTheRawTier_NotHandMaintained()
    {
        /* The backfill refuses to dig below the raw tier's read horizon: inside it, every CAGG's
           3-day start_offset re-materializes backdated buckets and the 4-day raw retention cannot
           immediately drop them; below it, neither holds (the issue's own staging boundary).
           Derived so a retention change moves this automatically — the #1937 rule. */
        Assert.Equal(RetentionTierRouter.RawMaxAge, QueryStoreBackfill.Horizon);
        Assert.True(QueryStoreBackfill.Horizon < TimescaleSupport.RawRetentionSpan,
            "the backfill horizon must sit strictly inside raw retention, or a slice could land rows the next purge immediately drops");
    }

    [Fact]
    public void StateIdentity_IsTheWorkersOwn_NotTheDefinitions()
    {
        /* The worker owns its collector_state rows under its OWN name, so the query_store
           DEFINITION keeps declaring no StateKeys (pinned by CollectorStateContractTests'
           TheOnlyCollectorDeclaringStateIsDefaultTraceEvents — this is the seam that lets both
           stay true). The key prefixes are part of the stored contract: rows written today must
           decode after an upgrade. */
        Assert.Equal("query_store_backfill", QueryStoreBackfill.StateCollectorName);
        Assert.Equal("done:", QueryStoreBackfill.DoneKeyPrefix);
        Assert.Equal("hole:", QueryStoreBackfill.HoleKeyPrefix);
        Assert.Empty(PerformanceMonitor.Collectors.QueryStoreCollector.Instance.StateKeys);
    }
}
