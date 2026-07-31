/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1849 end-to-end against a REAL TimescaleDB: the corrected Query Store rollups actually remove the
/// cumulative-snapshot double-count, and the raw purge cannot outrun them.
///
/// <para>These cannot be unit tests, for the same reason #1759's could not: every load-bearing fact here is
/// the ENGINE's, not ours. That a continuous aggregate may not bucket on a non-dimension column, that
/// <c>last(x, collection_time)</c> is accepted where <c>row_number()</c> is not, and — the one that shaped
/// this design and is in no documentation — that a hierarchical CAGG whose bucket EQUALS its parent's is a
/// LEAF that nothing can be built on. A C# test can assert we emit a string; only a live store can say
/// whether TimescaleDB accepts it and what it then computes.</para>
///
/// <para><b>#1776 own-store</b> — mints its own scratch database rather than sharing the live fixture, so it
/// is deliberately NOT in the <c>live-postgres</c> collection. It creates continuous aggregates and retention
/// policies the shared fixture must never inherit.</para>
/// </summary>
public sealed class QueryStoreCorrectedRollupLiveTests
{
    /// <summary>Distinctive fake id — a real server_id is a storage-name hash, never this.</summary>
    private const int TestServerId = -918491;

    /// <summary>
    /// How many times one Query Store interval gets re-collected inside a single hour. This is the shape
    /// #1849 measured on a live store (up to 496x), reproduced exactly: <c>execution_count</c> is CUMULATIVE
    /// within an interval, so the honest answer for the interval is its LAST snapshot — this number — while
    /// the old rollup's <c>sum(execution_count)</c> adds up every snapshot it ever saw.
    /// </summary>
    private const int ReCollections = 496;

    /// <summary>The inflated total the OLD rollup produces for that one interval: 1+2+...+496. Written as the
    /// closed form so the expectation is derived, not a magic number transcribed from a test run.</summary>
    private const long InflatedSum = ReCollections * (ReCollections + 1L) / 2L;

    [Fact]
    public async Task CorrectedRollups_DedupTheReCollectedInterval_WhileTheOldPairKeepsInflating()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(baseConnectionString),
            "Set DARLING_TEST_PG to a Postgres connection string (with TimescaleDB installed) to run the live #1849 corrected-rollup test (it mints its own scratch database).");

        var ct = TestContext.Current.CancellationToken;

        await using var scratch = await ScratchPostgres.CreateAsync(baseConnectionString!, ct);
        await using var connection = new NpgsqlConnection(scratch.ConnectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);

        Assert.True(await TimescaleSupport.TryEnableAsync(connection, null, ct),
            "the dev fixture is expected to have TimescaleDB installed");
        await TimescaleSupport.ConvertToHypertablesAsync(connection, null, ct);

        /* Fixed instants, not now-relative: every assertion below is an exact arithmetic identity, and a
           window that drifted across an hour boundary between seeding and asserting would turn a real
           regression into a flake. Kind-Unspecified — naive-UTC storage, see PgCollectorRowWriter. */
        var hour = new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Unspecified);

        /* ── SEED 1: ONE interval, re-collected 496 times inside the 10:00 hour. Honest answer: 496. ── */
        await SeedIntervalAsync(connection, hour, intervalId: 1001, queryId: 42, planId: 77,
            firstExecution: hour.AddSeconds(5), snapshots: ReCollections, secondsApart: 7,
            avgDurationUs: 100, avgCpuUs: 50, ct: ct);

        /* ── SEED 2: a SECOND interval in the SAME hour, 10 snapshots. Honest hour total: 496 + 10 = 506. ── */
        await SeedIntervalAsync(connection, hour.AddMinutes(30), intervalId: 1002, queryId: 43, planId: 78,
            firstExecution: hour.AddMinutes(30), snapshots: 10, secondsApart: 10,
            avgDurationUs: 200, avgCpuUs: 80, ct: ct);

        /* ── SEED 3: LEGACY rows — collected before V41, so runtime_stats_interval_id is NULL and the tier-1
               PROXY (first_execution_time) is the only identity there has ever been for them. Five snapshots
               of one interval in the 12:00 hour; honest answer 5. This is the case the L1 GROUP BY includes
               ON PURPOSE rather than excluding: excluding them is the easier claim to make true and would
               silently drop every pre-upgrade hour out of the corrected rollup. ── */
        await SeedIntervalAsync(connection, hour.AddHours(2), intervalId: null, queryId: 44, planId: 79,
            firstExecution: hour.AddHours(2).AddSeconds(3), snapshots: 5, secondsApart: 60,
            avgDurationUs: 300, avgCpuUs: 90, ct: ct);

        await EnsureAggregatesWithoutRefreshPoliciesAsync(connection, ct);
        await RefreshAllAsync(connection, ct);

        /* ── 1. THE DEDUP ITSELF. L1 collapses 496 raw rows to ONE, holding the interval's LAST snapshot.
               Asserted as a row COUNT and a VALUE together: a count alone passes if the aggregate picked the
               first snapshot or the minimum, and a value alone passes if it happened to land on one row. ── */
        var l1 = await ReadIntervalRowsAsync(connection, TimescaleSupport.QueryStoreStatsIntervalHourlyView, ct);

        var reCollected = Assert.Single(l1, r => r.QueryId == 42);
        Assert.Equal(ReCollections, reCollected.ExecutionCount);
        Assert.Equal(ReCollections, reCollected.SampleCount);
        Assert.Equal(1001L, reCollected.IntervalId);

        /* The legacy row deduped too, keyed on the proxy alone — tier-1 fidelity, which is exactly what
           #1853 says a legacy-only window degrades to. */
        var legacy = Assert.Single(l1, r => r.QueryId == 44);
        Assert.Null(legacy.IntervalId);
        Assert.Equal(5, legacy.ExecutionCount);

        /* ── 2. THE SIDE-BY-SIDE. The corrected hourly reports the honest 506 for the 10:00 hour while the
               ORIGINAL hourly, still standing and still fed from the same raw rows, reports the inflated sum.
               Asserting BOTH in one test is the point: a corrected-only assertion would still pass if the old
               view had been quietly reshaped, which is the one thing #1759/#1793 forbid. ── */
        var correctedHour = await ReadCompositeAsync(connection, TimescaleSupport.QueryStoreStatsCorrectedHourlyView, hour, ct);
        var legacyHour = await ReadCompositeAsync(connection, TimescaleSupport.QueryStoreStatsHourlyView, hour, ct);

        Assert.Equal(506, correctedHour.ExecutionCountSum);
        Assert.Equal(InflatedSum + 55, legacyHour.ExecutionCountSum);

        /* The correction is worth stating as a ratio, because the ratio is the product claim. */
        Assert.True(legacyHour.ExecutionCountSum / (double)correctedHour.ExecutionCountSum > 200,
            $"expected the old rollup to be at least 200x inflated against the corrected one for this shape, " +
            $"got {legacyHour.ExecutionCountSum} vs {correctedHour.ExecutionCountSum}");

        /* ── 3. THE WEIGHTED MEAN STILL COMPOSES. The whole reason the corrected view carries weighted SUMS
               rather than a materialized average: duration_us_weighted_sum / execution_count_sum must be the
               true execution-weighted mean, never an avg-of-avgs. Hand-computed:
               (496 x 100 + 10 x 200) / 506. ── */
        var expectedMean = ((496d * 100d) + (10d * 200d)) / 506d;
        Assert.Equal(expectedMean, correctedHour.DurationWeightedSum / correctedHour.ExecutionCountSum, 6);

        /* ── 4. THE HAND-COMPUTED DAY. The corrected daily is sourced from L1 DIRECTLY (a sibling of the
               corrected hourly, not its child — an identity-width hierarchical CAGG is a leaf), so it must
               agree with summing the corrected hours: 506 from the 10:00 hour + 5 legacy from 12:00. ── */
        var correctedDay = await ReadCompositeAsync(connection, TimescaleSupport.QueryStoreStatsCorrectedDailyView, hour.Date, ct);
        Assert.Equal(511, correctedDay.ExecutionCountSum);

        var correctedLegacyHour = await ReadCompositeAsync(connection, TimescaleSupport.QueryStoreStatsCorrectedHourlyView, hour.AddHours(2), ct);
        Assert.Equal(5, correctedLegacyHour.ExecutionCountSum);
        Assert.Equal(correctedHour.ExecutionCountSum + correctedLegacyHour.ExecutionCountSum, correctedDay.ExecutionCountSum);
    }

    /// <summary>
    /// WATCHED (mutation): drop <c>query_store_stats_interval_hourly</c> from
    /// <see cref="TimescaleSupport.RawTierCoverage"/>'s coverage list and this goes red on the FIRST assertion
    /// — the raw purge arms while the corrected rollups hold nothing, which is the #1790-class race in one
    /// step.
    ///
    /// <para>query_store_stats is the only raw table with two rollup families reading it, and the gate is an
    /// AND over both. The original hourly can be fully caught up while the corrected one is empty (exactly the
    /// state every existing store is in the moment it takes this build), so a gate that checked either one
    /// alone would let raw drop the only copy of history the corrected rollups have never seen.</para>
    /// </summary>
    [Fact]
    public async Task RawPurgeArming_HeldWhileTheCorrectedIntervalLayerIsShort_ThenReleasesWhenItCovers()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(baseConnectionString),
            "Set DARLING_TEST_PG to a Postgres connection string (with TimescaleDB installed) to run the live #1849 arming-gate test (it mints its own scratch database).");

        var ct = TestContext.Current.CancellationToken;

        await using var scratch = await ScratchPostgres.CreateAsync(baseConnectionString!, ct);
        await using var connection = new NpgsqlConnection(scratch.ConnectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);

        Assert.True(await TimescaleSupport.TryEnableAsync(connection, null, ct));
        await TimescaleSupport.ConvertToHypertablesAsync(connection, null, ct);

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        var oldest = now.Date.AddDays(-9);

        /* Pre-existing raw history, one interval per hour, going back well past any refresh policy's 3-day
           window — the shape every real store upgrading into this build is in. */
        for (var offset = 0; oldest.AddHours(offset) < now; offset += 3)
        {
            await SeedIntervalAsync(connection, oldest.AddHours(offset), intervalId: 2000 + offset, queryId: 7, planId: 9,
                firstExecution: oldest.AddHours(offset), snapshots: 3, secondsApart: 120,
                avgDurationUs: 100, avgCpuUs: 40, ct: ct);
        }

        await EnsureAggregatesWithoutRefreshPoliciesAsync(connection, ct);

        /* Bring the ORIGINAL pair fully up to date, and leave the corrected one SHORT. This is the precise
           state that makes a single-consumer gate wrong: by the old rule raw is "covered". */
        await RefreshRangeAsync(connection, TimescaleSupport.QueryStoreStatsHourlyView, oldest.AddDays(-1), now.AddDays(1), ct);

        var legacyFloor = await FloorAsync(connection, TimescaleSupport.QueryStoreStatsHourlyView, ct);
        var rawOldestBefore = await RawOldestAsync(connection, ct);
        Assert.NotNull(rawOldestBefore);
        Assert.True(legacyFloor <= rawOldestBefore,
            "the ORIGINAL rollup must fully cover raw here, or the test is not exercising the case where a " +
            "single-consumer gate would wrongly arm.");

        /* L1 must NOT cover raw. Asserted as "does not reach raw's oldest row" rather than "is empty",
           because empty is the wrong invariant to pin: this is a live store whose refresh policies were just
           removed, and TimescaleDB runs a new policy's first check IMMEDIATELY (#1564) — so under suite load
           a job could materialize the recent window before the removal lands, and the test would fail on a
           technicality while the property it exists to prove still held. Shallow-or-empty is the real
           precondition, and it is what the gate itself reads. */
        var l1FloorBefore = await FloorAsync(connection, TimescaleSupport.QueryStoreStatsIntervalHourlyView, ct);
        Assert.True(l1FloorBefore is null || l1FloorBefore > rawOldestBefore,
            $"the corrected interval layer should not yet reach raw's oldest row (L1 {l1FloorBefore:O}, raw {rawOldestBefore:O}).");

        /* ── 1. HELD. The policy is created and left PAUSED, because L1 covers nothing. ── */
        await TimescaleSupport.EnsureRetentionPoliciesAsync(connection, null, ct);
        Assert.False(await IsArmedAsync(connection, "query_store_stats", ct),
            "raw's purge armed while the corrected interval layer had materialized NOTHING — the gate is " +
            "reading only one of query_store_stats' two rollup families.");

        /* ── 2. Coverage arrives. Materialize L1 back over everything raw holds, exactly as the backfill
               verb's slices do. ── */
        await RefreshRangeAsync(connection, TimescaleSupport.QueryStoreStatsIntervalHourlyView, oldest.AddDays(-1), now.AddDays(1), ct);

        var l1Floor = await FloorAsync(connection, TimescaleSupport.QueryStoreStatsIntervalHourlyView, ct);
        var rawOldest = await RawOldestAsync(connection, ct);
        Assert.NotNull(l1Floor);
        Assert.NotNull(rawOldest);
        Assert.True(l1Floor <= rawOldest,
            $"the interval layer must reach at least as far back as raw before the gate can release (L1 {l1Floor:O}, raw {rawOldest:O})");

        /* ── 3. RELEASED, by itself, on the next sweep — no manual step. That self-healing property is what
               #1759 replaced a caveat with, and adding a second consumer must not break it. ── */
        await TimescaleSupport.EnsureRetentionPoliciesAsync(connection, null, ct);
        Assert.True(await IsArmedAsync(connection, "query_store_stats", ct),
            "both rollup families now cover raw, so the held purge should have armed itself on this sweep.");
    }

    /* ─────────────────────────── seeding + reading helpers ─────────────────────────── */

    /// <summary>
    /// Plants one Query Store interval as <paramref name="snapshots"/> CUMULATIVE re-collections of the same
    /// interval — <c>execution_count</c> running 1..n, which is what the collector actually stores when it
    /// re-fetches an open interval. A null <paramref name="intervalId"/> plants the PRE-V41 shape, where the
    /// proxy is the only identity.
    /// </summary>
    private static async Task SeedIntervalAsync(
        NpgsqlConnection connection, DateTime firstCollection, long? intervalId, long queryId, long planId,
        DateTime firstExecution, int snapshots, int secondsApart, long avgDurationUs, long avgCpuUs,
        CancellationToken ct)
    {
        /* collection_id is the NOT NULL prefix id every collector table carries; one per snapshot, derived
           from the row's position so it is unique without needing a sequence. */
        const string sql = @"
INSERT INTO collect.query_store_stats
    (collection_id, collection_time, server_id, server_name, database_name, module_name, query_hash,
     query_id, plan_id, execution_type_desc, replica_role,
     runtime_stats_interval_id, interval_start_time_utc, first_execution_time,
     execution_count, avg_duration_us, avg_cpu_time_us, max_duration_us, max_cpu_time_us)
SELECT
    (extract(epoch FROM $1)::bigint * 100000) + ($4 * 1000) + n,
    $1 + (n * ($2 || ' seconds')::interval),
    $3, 'SQL01', 'AdventureWorks', 'dbo.GetOrders', '0xABCD',
    $4, $5, 'Regular', 'PRIMARY',
    $6, $7, $8,
    n, $9, $10, 900, 400
FROM generate_series(1, $11) AS n";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(firstCollection);
        command.Parameters.AddWithValue(secondsApart.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(TestServerId);
        command.Parameters.AddWithValue(queryId);
        command.Parameters.AddWithValue(planId);
        command.Parameters.AddWithValue(intervalId.HasValue ? intervalId.Value : DBNull.Value);
        command.Parameters.AddWithValue(intervalId.HasValue ? firstCollection : (object)DBNull.Value);
        command.Parameters.AddWithValue(firstExecution);
        command.Parameters.AddWithValue(avgDurationUs);
        command.Parameters.AddWithValue(avgCpuUs);
        command.Parameters.AddWithValue(snapshots);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Builds the aggregates, then strips every REFRESH policy the sweep attached.
    ///
    /// <para>TimescaleDB runs a new policy's first check IMMEDIATELY rather than on its next interval
    /// (#1564/#1567), so the background scheduler starts materializing within seconds of the ensure sweep.
    /// These tests refresh manually and deterministically and assert on exact floors and exact sums, so a
    /// concurrent background refresh is pure interference: it moves a floor mid-assertion and it collides
    /// with a manual refresh as 55P03. Same reason PayloadDimensionLiveTests strips them.</para>
    /// </summary>
    private static async Task EnsureAggregatesWithoutRefreshPoliciesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await TimescaleSupport.EnsureContinuousAggregatesAsync(connection, null, ct);

        foreach (var (view, _, _, _, _) in TimescaleSupport.RollupViews)
        {
            await using var remove = new NpgsqlCommand(
                $"SELECT remove_continuous_aggregate_policy('collect.{view}', if_exists => true)", connection);
            await remove.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task RefreshAllAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        /* Dependency order: L1 before the two corrected views that read it. The original pair is refreshed
           too, because half of what this test proves is what the OLD view still reports. */
        foreach (var view in new[]
        {
            TimescaleSupport.QueryStoreStatsIntervalHourlyView,
            TimescaleSupport.QueryStoreStatsHourlyView,
            TimescaleSupport.QueryStoreStatsCorrectedHourlyView,
            TimescaleSupport.QueryStoreStatsCorrectedDailyView,
            TimescaleSupport.QueryStoreStatsDailyView,
        })
        {
            await using var refresh = new NpgsqlCommand($"CALL refresh_continuous_aggregate('collect.{view}', NULL, NULL)", connection);
            await refresh.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task RefreshRangeAsync(NpgsqlConnection connection, string view, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var refresh = new NpgsqlCommand($"CALL refresh_continuous_aggregate('collect.{view}', $1::timestamp, $2::timestamp)", connection);
        refresh.Parameters.AddWithValue(from);
        refresh.Parameters.AddWithValue(to);
        await refresh.ExecuteNonQueryAsync(ct);
    }

    private sealed record IntervalRow(long QueryId, long? IntervalId, long ExecutionCount, long SampleCount);

    private static async Task<List<IntervalRow>> ReadIntervalRowsAsync(NpgsqlConnection connection, string view, CancellationToken ct)
    {
        var rows = new List<IntervalRow>();
        await using var command = new NpgsqlCommand(
            $"SELECT query_id, runtime_stats_interval_id, execution_count, sample_count FROM collect.{view} ORDER BY bucket, query_id", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new IntervalRow(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return rows;
    }

    private sealed record CompositeRow(long ExecutionCountSum, double DurationWeightedSum);

    private static async Task<CompositeRow> ReadCompositeAsync(NpgsqlConnection connection, string view, DateTime bucket, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT sum(execution_count_sum)::bigint, sum(duration_us_weighted_sum)::double precision FROM collect.{view} WHERE bucket = $1", connection);
        command.Parameters.AddWithValue(bucket);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct), $"no row in {view} for bucket {bucket:O}");
        Assert.False(reader.IsDBNull(0), $"{view} materialized nothing for bucket {bucket:O}");
        return new CompositeRow(reader.GetInt64(0), reader.GetDouble(1));
    }

    private static async Task<DateTime?> FloorAsync(NpgsqlConnection connection, string view, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT min(bucket) FROM collect.{view}", connection);
        var value = await command.ExecuteScalarAsync(ct);
        return value is DBNull or null ? null : (DateTime)value;
    }

    private static async Task<DateTime?> RawOldestAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT min(collection_time) FROM collect.query_store_stats", connection);
        var value = await command.ExecuteScalarAsync(ct);
        return value is DBNull or null ? null : (DateTime)value;
    }

    private static async Task<bool> IsArmedAsync(NpgsqlConnection connection, string relation, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(@"
SELECT COALESCE(bool_or(j.scheduled), false)
FROM timescaledb_information.jobs AS j
WHERE j.proc_name = 'policy_retention'
AND   j.hypertable_schema = 'collect'
AND   j.hypertable_name = $1", connection);
        command.Parameters.AddWithValue(relation);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }
}
