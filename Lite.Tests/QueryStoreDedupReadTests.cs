/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Services;
using PerformanceMonitorLite.Tests;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Real-DuckDB round-trip pins for #1841: the Query Store aggregate reads must count a re-collected
/// runtime-stats interval ONCE, at its latest cumulative values.
///
/// <para>query_store_stats rows are CUMULATIVE per-interval snapshots. The collector is incremental on
/// last_execution_time, so the OPEN interval is re-fetched every cycle and stored again with a growing
/// execution_count. Live evidence on issue #1841: the same (server, database, query_id, plan_id) appeared
/// up to 496 times inside ONE hour bucket. Every read that SUMs raw rows counted that interval's work 496
/// times.</para>
///
/// <para>The seed reproduces both live shapes at once: interval A is collected four times with a
/// FLAT execution_count of 1 (the 496x shape — one interval, many collections), and interval B is
/// collected three times with a GROWING execution_count (10 -> 25 -> 40, the cumulative shape). The
/// assertions are written against the true totals, so they fail loudly against an un-deduped read.</para>
/// </summary>
public sealed class QueryStoreDedupReadTests : IClassFixture<SharedDuckDbFixture>, IDisposable
{
    private const int ServerId = 8841;
    private const string Db = "DedupDb";

    private readonly DuckDbInitializer _duckDb;
    private DuckDBConnection? _seedConn;
    private long _nextId = 1;

    public QueryStoreDedupReadTests(SharedDuckDbFixture fixture)
    {
        fixture.ResetData();
        _duckDb = fixture.DuckDb;
    }

    public void Dispose() => _seedConn?.Dispose();

    /* Every seeded collection lands inside ONE date_trunc('hour') bucket so the slicer assertion is about
       dedup and not about bucket boundaries, and the whole bucket sits comfortably inside hoursBack: 24.
       hoursBack (not fromDate/toDate) on purpose: GetTimeRange applies ServerTimeHelper.UtcOffsetMinutes
       to an explicit range, which would make these fixed timestamps depend on the machine's server-time
       offset. Floored to the hour, then to whole seconds by construction — DuckDB TIMESTAMP is
       microsecond-resolution, so raw DateTime ticks would not survive the round trip. */
    private static readonly DateTime BucketStart = HourFloor(DateTime.UtcNow.AddHours(-3));

    private static DateTime HourFloor(DateTime t) =>
        DateTime.SpecifyKind(new DateTime(t.Ticks - (t.Ticks % TimeSpan.TicksPerHour)), DateTimeKind.Unspecified);

    /* Interval identity: a runtime-stats interval has a stable first_execution_time. */
    private static readonly DateTime FirstExecA = BucketStart.AddMinutes(1);
    private static readonly DateTime FirstExecB = BucketStart.AddMinutes(2);

    private async Task<DuckDBConnection> SeedConnectionAsync()
    {
        if (_seedConn is null)
        {
            _seedConn = _duckDb.CreateConnection();
            await _seedConn.OpenAsync();
        }
        return _seedConn;
    }

    private async Task SeedAsync(
        DateTime collectionTime,
        long queryId,
        long planId,
        DateTime firstExecutionTime,
        long executionCount,
        long avgCpuUs,
        long avgDurationUs,
        long avgReads,
        string queryHash)
    {
        using var readLock = _duckDb.AcquireReadLock();
        var connection = await SeedConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO query_store_stats
    (collection_id, collection_time, server_id, server_name, database_name,
     query_id, plan_id, execution_type_desc, first_execution_time, last_execution_time,
     query_text, query_hash, execution_count, avg_cpu_time_us, avg_duration_us,
     avg_logical_io_reads, avg_logical_io_writes, avg_physical_io_reads,
     query_plan_hash, is_forced_plan, force_failure_count)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21)";
        cmd.Parameters.Add(new DuckDBParameter { Value = _nextId++ });
        cmd.Parameters.Add(new DuckDBParameter { Value = collectionTime });
        cmd.Parameters.Add(new DuckDBParameter { Value = ServerId });
        cmd.Parameters.Add(new DuckDBParameter { Value = "DedupSrv" });
        cmd.Parameters.Add(new DuckDBParameter { Value = Db });
        cmd.Parameters.Add(new DuckDBParameter { Value = queryId });
        cmd.Parameters.Add(new DuckDBParameter { Value = planId });
        cmd.Parameters.Add(new DuckDBParameter { Value = "Regular" });
        cmd.Parameters.Add(new DuckDBParameter { Value = firstExecutionTime });
        cmd.Parameters.Add(new DuckDBParameter { Value = collectionTime });
        cmd.Parameters.Add(new DuckDBParameter { Value = $"SELECT {queryId}" });
        cmd.Parameters.Add(new DuckDBParameter { Value = queryHash });
        cmd.Parameters.Add(new DuckDBParameter { Value = executionCount });
        cmd.Parameters.Add(new DuckDBParameter { Value = avgCpuUs });
        cmd.Parameters.Add(new DuckDBParameter { Value = avgDurationUs });
        cmd.Parameters.Add(new DuckDBParameter { Value = avgReads });
        cmd.Parameters.Add(new DuckDBParameter { Value = 0L });
        cmd.Parameters.Add(new DuckDBParameter { Value = 0L });
        cmd.Parameters.Add(new DuckDBParameter { Value = $"0xPLAN{planId}" });
        cmd.Parameters.Add(new DuckDBParameter { Value = false });
        cmd.Parameters.Add(new DuckDBParameter { Value = 0L });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Interval A — the 496x shape: ONE interval, execution_count never moves off 1, collected four times.
    /// True work: 1 execution, 1,000us CPU, 2,000us duration, 7 reads. An un-deduped SUM reports 4x that.
    /// Interval B — the cumulative shape: execution_count and the averages both grow as the interval
    /// accumulates. True work is the LAST snapshot only: 40 x 300us CPU, 40 x 7,000us duration, 40 x 5 reads.
    /// An un-deduped SUM reports 10x100 + 25x200 + 40x300 = 18,000us of CPU against a true 12,000us.
    /// </summary>
    private async Task SeedBothIntervalShapesAsync()
    {
        foreach (var minute in new[] { 5, 10, 15, 20 })
        {
            await SeedAsync(BucketStart.AddMinutes(minute), queryId: 1, planId: 11, FirstExecA,
                executionCount: 1, avgCpuUs: 1_000, avgDurationUs: 2_000, avgReads: 7, queryHash: "0xHASH_A");
        }

        var growth = new (int Minute, long Execs, long Cpu, long Dur)[]
        {
            (5, 10L, 100L, 5_000L),
            (10, 25L, 200L, 6_000L),
            (15, 40L, 300L, 7_000L),
        };
        foreach (var g in growth)
        {
            await SeedAsync(BucketStart.AddMinutes(g.Minute), queryId: 2, planId: 22, FirstExecB,
                executionCount: g.Execs, avgCpuUs: g.Cpu, avgDurationUs: g.Dur, avgReads: 5, queryHash: "0xHASH_B");
        }
    }

    /* True deduped totals for the single hour bucket, in the units the reads return. The un-deduped
       figures beside them are what the pre-#1841 queries returned for this same seed (4 collections of
       interval A, plus B's 10x, 25x and 40x snapshots all summed).
       CPU:      (1 x 1,000 + 40 x 300) / 1000                                   = 13 ms   (un-deduped: 22 ms)
       Duration: (1 x 2,000 + 40 x 7,000) / 1000                                 = 282 ms  (un-deduped: 488 ms)
       Reads:     1 x 7 + 40 x 5                                                 = 207     (un-deduped: 403) */
    private const double TrueBucketCpuMs = 13.0;
    private const double TrueBucketDurationMs = 282.0;
    private const double TrueBucketReads = 207.0;

    [Fact]
    public async Task SlicerBucket_CountsARecollectedIntervalOnce_AtItsLatestValues()
    {
        await SeedBothIntervalShapesAsync();

        var buckets = await new LocalDataService(_duckDb).GetQueryStoreSlicerDataAsync(ServerId, hoursBack: 24);

        var bucket = Assert.Single(buckets);
        Assert.Equal(2, bucket.SessionCount); /* COUNT(DISTINCT query_id) — unaffected, guards the seed */
        Assert.Equal(TrueBucketCpuMs, bucket.TotalCpu, precision: 6);
        Assert.Equal(TrueBucketDurationMs, bucket.TotalElapsed, precision: 6);
        Assert.Equal(TrueBucketReads, bucket.TotalReads, precision: 6);
    }

    [Fact]
    public async Task TopQueries_ReportTheLatestCumulativeExecutionCount_NotTheSumOfSnapshots()
    {
        await SeedBothIntervalShapesAsync();

        var rows = await new LocalDataService(_duckDb).GetQueryStoreTopQueriesAsync(ServerId, hoursBack: 24);

        var a = Assert.Single(rows, r => r.QueryId == 1);
        var b = Assert.Single(rows, r => r.QueryId == 2);

        /* Four collections of one 1-execution interval is ONE execution, not four. */
        Assert.Equal(1L, a.TotalExecutions);
        /* 10 -> 25 -> 40 is one interval that reached 40, not 75 executions. */
        Assert.Equal(40L, b.TotalExecutions);

        /* The averages must come from the LATEST snapshot of the interval, not be an avg-of-avgs across
           re-collections (which would give B (5000+6000+7000)/3 = 6.0 ms). */
        Assert.Equal(2.0, a.AvgDurationMs, precision: 6);
        Assert.Equal(7.0, b.AvgDurationMs, precision: 6);
        Assert.Equal(0.3, b.AvgCpuTimeMs, precision: 6);
    }

    [Fact]
    public async Task Comparison_WeightsEachIntervalOnce_InBothWindows()
    {
        await SeedBothIntervalShapesAsync();

        /* The comparison takes explicit UTC ranges rather than hoursBack. Both windows cover the same
           seeded bucket, so a correct read reports identical current and baseline numbers — any dedup
           asymmetry between the two arms would show up as a spurious delta. */
        var start = BucketStart.AddMinutes(-1);
        var end = BucketStart.AddMinutes(59);

        var rows = await new LocalDataService(_duckDb)
            .GetQueryStoreComparisonAsync(ServerId, start, end, start, end);

        var b = Assert.Single(rows, r => r.QueryHash == "0xHASH_B");
        Assert.Equal(40L, b.ExecutionCount);
        Assert.Equal(40L, b.BaselineExecutionCount);
        Assert.Equal(7.0, b.AvgDurationMs, precision: 6);
        Assert.Equal(0.3, b.AvgCpuMs, precision: 6);

        var a = Assert.Single(rows, r => r.QueryHash == "0xHASH_A");
        Assert.Equal(1L, a.ExecutionCount);
        Assert.Equal(2.0, a.AvgDurationMs, precision: 6);
    }

    [Fact]
    public async Task DurationTrend_IsTheOneQueryStoreAggregateLeftUndeduped()
    {
        /* Pins the ONE deliberate exclusion (#1841 tier 2) so it stays a decision rather than looking
           like a read that was missed. Deduping this one is right for totals and wrong for the chart: it
           keeps a single row per interval, at the collection where the interval CLOSED, and Query Store's
           default 60-minute interval against a 5-minute cadence collapses every query's twelve snapshots
           onto one collection_time — a 1-hour window would render a SINGLE point, valued 0 because the
           LAG has no predecessor. Placing the work when it ran needs first_execution_time, which is the
           monitored server's LOCAL wall clock while this axis is UTC.

           So it still emits one point per COLLECTION (four here, one per cycle) and still overstates. */
        await SeedBothIntervalShapesAsync();

        var points = await new LocalDataService(_duckDb).GetQueryStoreDurationTrendAsync(ServerId, hoursBack: 24);

        Assert.Equal(4, points.Count);
        Assert.Equal(BucketStart.AddMinutes(5), points[0].CollectionTime);
        Assert.Equal(BucketStart.AddMinutes(20), points[3].CollectionTime);
    }

    [Fact]
    public async Task DedupIsScopedPerInterval_SoASecondIntervalOfTheSameQueryStillCounts()
    {
        /* The dedup key is (database, query_id, plan_id, first_execution_time). A NEW interval of the same
           query and plan is a distinct unit of work and must survive — a dedup keyed only on the query
           would silently drop it and under-count instead. */
        await SeedAsync(BucketStart.AddMinutes(5), queryId: 3, planId: 33, FirstExecA,
            executionCount: 4, avgCpuUs: 1_000, avgDurationUs: 1_000, avgReads: 0, queryHash: "0xHASH_C");
        await SeedAsync(BucketStart.AddMinutes(10), queryId: 3, planId: 33, FirstExecA,
            executionCount: 6, avgCpuUs: 1_000, avgDurationUs: 1_000, avgReads: 0, queryHash: "0xHASH_C");
        await SeedAsync(BucketStart.AddMinutes(15), queryId: 3, planId: 33, FirstExecB,
            executionCount: 5, avgCpuUs: 1_000, avgDurationUs: 1_000, avgReads: 0, queryHash: "0xHASH_C");

        var rows = await new LocalDataService(_duckDb).GetQueryStoreTopQueriesAsync(ServerId, hoursBack: 24);

        /* Interval one closed at 6, interval two at 5 — 11 executions, not 15 (un-deduped) and not 6
           (over-deduped to one row per query/plan). */
        var row = Assert.Single(rows);
        Assert.Equal(11L, row.TotalExecutions);
    }

    /// <summary>
    /// Source-containment guard, the Lite counterpart of the Darling Viewer's SQL-constant theory. Lite
    /// builds its Query Store SQL inline (no exposed constants to pin), so a FIFTH aggregate read added to
    /// this file could ship un-deduped and silently reintroduce #1841. Every read of v_query_store_stats in
    /// LocalDataService.QueryStore.cs must therefore be accounted for: either it carries a dedup CTE, or it
    /// is one of the two reads deliberately left raw, each of which says so at the source.
    /// </summary>
    [Fact]
    public void EveryQueryStoreAggregateInTheFile_CarriesADedupCte()
    {
        var source = File.ReadAllText(SourcePath("Lite", "Services", "LocalDataService.QueryStore.cs"));

        /* The four aggregate reads: slicer, top queries, and the comparison's two windows. Counting
           occurrences rather than matching order keeps this from breaking on a harmless reshuffle. */
        var partitions = Regex.Matches(
            source,
            @"PARTITION BY database_name, query_id, plan_id, first_execution_time, execution_type_desc, replica_role").Count;
        var rankFilters = Regex.Matches(source, @"(?:WHERE|AND)\s+(?:qs\.)?rn = 1").Count;

        Assert.Equal(4, partitions);
        /* Six, not four: the comparison's two deduped CTEs are each consumed TWICE — once to pick the
           top 100 hashes and once by the value aggregate — and an rn filter missing from either consumer
           would let the un-deduped rows straight back into the numbers. */
        Assert.Equal(6, rankFilters);

        /* Every dedup orders by collection_time — "latest" is never decided by execution_count, which can
           sit still across a hundred re-collections of the same interval. */
        Assert.Equal(partitions, Regex.Matches(source, @"ORDER BY collection_time DESC\s*\n\s*\) AS rn").Count);

        /* The two deliberate exclusions must keep explaining themselves, so neither reads as an oversight. */
        Assert.Contains("Deliberately NOT deduped per interval (#1841)", source, StringComparison.Ordinal);
        Assert.Contains("KNOWN OVERSTATEMENT, deliberately still here (#1841 tier 2)", source, StringComparison.Ordinal);
    }

    /// <summary>Walks up from the test binary to the repo root so the pin works from any run directory.</summary>
    private static string SourcePath(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PerformanceMonitor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.True(dir is not null, "could not locate the repository root from " + AppContext.BaseDirectory);
        return Path.Combine([dir!, .. parts]);
    }

    [Fact]
    public async Task DedupKeepsTheLatestRow_EvenWhenTheCumulativeCountDoesNotGrow()
    {
        /* Guards the ORDER BY: "latest" must be decided by collection_time, not by execution_count.
           Interval A's execution_count never moves, so a MAX(execution_count) style dedup would be
           satisfied by the FIRST row and would silently keep the stalest averages. */
        await SeedAsync(BucketStart.AddMinutes(5), queryId: 4, planId: 44, FirstExecA,
            executionCount: 2, avgCpuUs: 1_000, avgDurationUs: 1_000, avgReads: 0, queryHash: "0xHASH_D");
        await SeedAsync(BucketStart.AddMinutes(10), queryId: 4, planId: 44, FirstExecA,
            executionCount: 2, avgCpuUs: 9_000, avgDurationUs: 9_000, avgReads: 0, queryHash: "0xHASH_D");

        var rows = await new LocalDataService(_duckDb).GetQueryStoreTopQueriesAsync(ServerId, hoursBack: 24);

        var row = Assert.Single(rows);
        Assert.Equal(2L, row.TotalExecutions);
        Assert.Equal(9.0, row.AvgDurationMs, precision: 6);
    }
}
