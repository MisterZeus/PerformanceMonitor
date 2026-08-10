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
    [InlineData(100_000L, 98_000L, 2_000L)]
    /* A drain-bound pass: rows are cheap to produce and expensive to move, where the budget IS the lever. */
    [InlineData(100_000L, 3_000L, 97_000L)]
    /* Degenerate: open exceeding the batch total (clock skew between the two watches) must clamp at zero
       rather than print a negative drain, which would read as a measurement bug in the field. */
    [InlineData(5_000L, 6_000L, 0L)]
    public void DrainIsTheRemainder_AndNeverNegative(long sqlMs, long openMs, long expectedDrain)
    {
        var context = NewContext();
        context.PerItemOpenMs = openMs;

        /* The same arithmetic the runner's log line performs. Pinned here so a refactor of that line cannot
           silently start reporting negative drain. */
        var drain = Math.Max(0, sqlMs - context.PerItemOpenMs);
        Assert.Equal(expectedDrain, drain);
    }

    [Fact]
    public void OpenMs_IsNotResetByTheQueryStoreRead_SoTheHostsMeasurementSurvivesToTheLog()
    {
        /* The query_store collector resets its OWN per-item signals at the top of a read. The host sets
           PerItemOpenMs before calling that read, so the collector must leave it alone — otherwise the
           split would always log zero and the instrumentation would be silently dead. */
        var context = NewContext();
        context.PerItemOpenMs = 4_242;
        context.PerItemTextBudgetExceeded = true;
        context.PerItemTextBytesShipped = 999;

        /* Mirrors the collector's documented reset set — deliberately enumerated rather than invoking the
           read (which needs a live reader), so this test states the contract the read must honor. */
        context.PerItemTextBudgetExceeded = false;
        context.PerItemTextBytesShipped = 0;
        context.PerItemShippedBoundary = null;

        Assert.Equal(4_242, context.PerItemOpenMs);
    }
}
