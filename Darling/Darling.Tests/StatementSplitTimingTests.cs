/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the open-vs-drain timing split (#2164). It exists because a single blended <c>sql:</c> number could
/// not answer the question a 5x payload cut raised on production: the byte budget moved bytes 5x and the
/// batch clock ~0%, so the cost is upstream of shipping — but WHICH statement was unprovable from the log,
/// and the next fix would have been a guess. Open time (everything before the first rowset) and drain time
/// (row streaming) have different fixes, so they must be separately visible.
/// </summary>
public sealed class StatementSplitTimingTests
{
    private static CollectorContext NewContext() => new()
    {
        ServerId = 1,
        ServerName = "s",
        CollectionTime = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc),
        Deltas = new CollectorDeltaCalculator(),
    };

    [Fact]
    public void OpenMs_DefaultsToZero_SoAnUnmeasuredHostIsNotReadAsInstant()
    {
        /* Lite does not measure this today. Zero must mean "not measured", which is why the log only emits
           the split when the value is positive rather than printing "open:0ms" and inviting the reader to
           conclude the aggregate was free. */
        Assert.Equal(0, NewContext().PerItemOpenMs);
    }

    [Theory]
    /* An aggregate-bound pass: nearly all the batch is spent before the first row arrives, so no client
       byte budget can shorten it — the query_store shape measured on the field server. */
    [InlineData(100_000L, 0L, 98_000L, 2_000L)]
    /* A drain-bound pass: rows are cheap to produce and expensive to move, where the budget IS the lever. */
    [InlineData(100_000L, 0L, 3_000L, 97_000L)]
    /* The watermark phase is a STORE round trip the driver's stopwatch already started before. It must come
       out of drain, not inflate it — the review catch this arithmetic exists to prevent. */
    [InlineData(100_000L, 40_000L, 55_000L, 5_000L)]
    /* Degenerate: phases exceeding the batch total (skew across separate stopwatches) must clamp at zero
       rather than print a negative drain, which would read as a measurement bug in the field. */
    [InlineData(5_000L, 3_000L, 6_000L, 0L)]
    public void DrainExcludesWatermarkAndOpen_AndNeverGoesNegative(long sqlMs, long watermarkMs, long openMs, long expectedDrain)
    {
        var context = NewContext();
        context.PerItemWatermarkMs = watermarkMs;
        context.PerItemOpenMs = openMs;

        /* Calls the SHIPPED arithmetic (CollectorContext.DrainMsFrom) — the log line calls the same method,
           so this cannot drift into pinning a copy of the formula the way the first cut did. */
        Assert.Equal(expectedDrain, context.DrainMsFrom(sqlMs));
    }

    [Fact]
    public void EveryPhaseAccountedFor_ThePartsNeverExceedTheWhole()
    {
        /* The split's contract as a reader sees it: wm + open + drain == the sql: total, so nothing is
           silently unattributed. Holds for any measurement where the phases fit inside the total. */
        var context = NewContext();
        context.PerItemWatermarkMs = 1_200;
        context.PerItemOpenMs = 300_000;
        const long sqlMs = 350_000;

        Assert.Equal(sqlMs, context.PerItemWatermarkMs + context.PerItemOpenMs + context.DrainMsFrom(sqlMs));
    }
}
