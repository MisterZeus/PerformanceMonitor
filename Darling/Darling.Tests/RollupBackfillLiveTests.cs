/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1759 end-to-end against a REAL TimescaleDB, on a store built into the exact broken shape the issue
/// describes: pre-existing raw history, a rollup created <c>WITH NO DATA</c> whose materialization starts
/// well after raw does.
///
/// <para>This is the leg that the unit tests cannot stand in for, because the defect lives in the ENGINE'S
/// semantics, not in ours. Both halves of the premise that produced #1759 read fine in C#: that real-time
/// aggregation is on by default (it is not, since 2.13), and that it would union in older raw anyway (it
/// cannot — the watermark is a hard partition). Only a live aggregate can show that a window below the
/// materialized floor really does come back EMPTY while raw holds the rows, and that the backfill really does
/// make it stop.</para>
///
/// <para>Mints its own scratch database: it creates continuous aggregates and retention policies, which the
/// shared live-fixture store must not inherit from a test.</para>
/// </summary>
public sealed class RollupBackfillLiveTests
{
    /// <summary>Distinctive fake id — a real server_id is a storage-name hash, never this.</summary>
    private const int TestServerId = -915915;

    /// <summary>How much pre-existing history to plant before the rollup is created. Comfortably past both the
    /// 3-day refresh-policy window and the 3-day raw route margin, so the routing assertions are not sitting on
    /// a boundary.</summary>
    private const int HistoryDays = 12;

    [Fact]
    public async Task RollupCreatedOverExistingHistory_ReadsEmptyBelowItsFloor_UntilTheBackfillFixesIt()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(baseConnectionString),
            "Set DARLING_TEST_PG to a Postgres connection string (with TimescaleDB installed) to run the live #1759 backfill test (it mints its own scratch database).");

        var ct = TestContext.Current.CancellationToken;

        await using var scratch = await ScratchPostgres.CreateAsync(baseConnectionString!, ct);
        await using var connection = new NpgsqlConnection(scratch.ConnectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);

        Assert.True(await TimescaleSupport.TryEnableAsync(connection, null, ct),
            "the dev fixture is expected to have TimescaleDB installed");
        await TimescaleSupport.ConvertToHypertablesAsync(connection, null, ct);

        /* All timestamps Kind-Unspecified — naive-UTC storage, see PgCollectorRowWriter. */
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        var rawOldest = now.AddDays(-HistoryDays);

        /* ── 1. PRE-EXISTING HISTORY: raw rows going back well before any rollup exists. ── */
        await SeedHourlyQueryStatsAsync(connection, rawOldest, now, ct);

        /* ── 2. The rollup is created over that history, WITH NO DATA — the #1759 shape exactly. ── */
        await TimescaleSupport.EnsureContinuousAggregatesAsync(connection, null, ct);

        /* Materialize only the recent window, which is all the 3-day refresh policy would ever have done on a
           store that existed before its rollups. This is what makes the floor shallow. */
        await RollupBackfill.RunSliceAsync(
            connection, TimescaleSupport.QueryStatsHourlyView, now.Date.AddDays(-2), now.Date.AddDays(1), ct);

        await using var dataSource = NpgsqlDataSource.Create(scratch.ConnectionString);
        var rollups = await TimescaleSupport.DetectRollupsAsync(dataSource, ct);
        Assert.True(rollups.QueryGrainHourly, "the query_stats hourly rollup should exist after the ensure sweep");

        var before = await TimescaleSupport.DetectRollupCoverageAsync(dataSource, rollups, ct);
        var floorBefore = before.FloorOf(TimescaleSupport.QueryStatsHourlyView);
        var rawFloor = before.RawOldestOf("query_stats");

        Assert.NotNull(floorBefore);
        Assert.NotNull(rawFloor);
        Assert.True(floorBefore > rawFloor,
            $"the rollup should start AFTER raw does on this store shape (rollup {floorBefore:O}, raw {rawFloor:O})");

        /* ── 3. THE DEFECT, MEASURED. A window below the floor really is empty on the rollup while raw holds
               the rows — this is the hard partition, not a slow path. If this assertion ever stops holding,
               the premise behind all of #1759 has changed and the fix should be revisited, not patched. ── */
        var windowStart = now.AddDays(-8);
        var rollupRows = await CountAsync(connection, $"SELECT count(*) FROM collect.{TimescaleSupport.QueryStatsHourlyView} WHERE bucket >= $1 AND bucket < $2", windowStart, now.AddDays(-6), ct);
        var rawRows = await CountAsync(connection, "SELECT count(*) FROM collect.query_stats WHERE collection_time >= $1 AND collection_time < $2", windowStart, now.AddDays(-6), ct);

        Assert.True(rawRows > 0, "raw must hold rows in the pre-coverage window, or the test is not exercising #1759");
        Assert.Equal(0, rollupRows);

        /* ── 4. PHASE 1: the router must send that window to RAW, where the data actually is. Age alone puts an
               8-day window on the hourly rollup, which would answer with silence. ── */
        var coverageLadder = before.For(TimescaleSupport.QueryStatsHourlyView, TimescaleSupport.QueryStatsDailyView);

        Assert.Equal(
            RetentionTier.Hourly,
            RetentionTierRouter.Resolve(now, windowStart, rollups.QueryGrainHourly, rollups.QueryGrainDaily));

        Assert.Equal(
            RetentionTier.Raw,
            RetentionTierRouter.Resolve(now, windowStart, rollups.QueryGrainHourly, rollups.QueryGrainDaily, coverageLadder));

        /* ── 5. PHASE 2: back fill in slices, oldest first, exactly as the verb does. ── */
        var plan = RollupBackfill.Plan(
            TimescaleSupport.QueryStatsHourlyView, rawFloor, floorBefore,
            materializedBuckets: 48, materializedBytes: 48 * 1024,
            rawBytes: 0, bucketWidth: TimeSpan.FromHours(1));

        Assert.False(plan.IsComplete);
        Assert.True(plan.Slices > 1, "a 12-day history should plan more than one slice");

        var slicesRun = 0;
        var previousFloor = floorBefore;
        foreach (var (from, to) in RollupBackfill.Slices(plan.FromUtc, plan.ToUtc))
        {
            var floorDuring = await RollupBackfill.RunSliceAsync(connection, TimescaleSupport.QueryStatsHourlyView, from, to, ct);
            slicesRun++;

            /* Progress only ever goes BACKWARDS. Deliberately NOT "the floor reached this slice's start": a
               slice's range can hold no source rows at all — raw's oldest row lands partway into the first
               slice, and a collection gap does the same mid-run — so a healthy slice can leave the floor
               short of its own start. Asserting otherwise failed on the first slice of every run, which is
               how the product's per-slice check was found to be measuring the wrong thing. */
            Assert.NotNull(floorDuring);
            Assert.True(floorDuring <= previousFloor,
                $"slice {from:O} moved the coverage floor FORWARD ({previousFloor:O} -> {floorDuring:O}); the backfill must only ever extend coverage backwards");
            previousFloor = floorDuring;
        }

        Assert.Equal(plan.Slices, slicesRun);

        /* The very first slice must have moved the floor at all, or the run did nothing and every assertion
           after this would be vacuous. */
        Assert.True(previousFloor < floorBefore, "the backfill did not move the coverage floor at all");

        /* ── 6. CONVERGENCE, MEASURED FROM DATA — never from the fact that the calls returned. ── */
        var after = await TimescaleSupport.DetectRollupCoverageAsync(dataSource, rollups, ct);
        var floorAfter = after.FloorOf(TimescaleSupport.QueryStatsHourlyView);

        Assert.NotNull(floorAfter);
        Assert.True(floorAfter <= rawFloor,
            $"after the backfill the rollup must reach at or before raw's oldest row (rollup {floorAfter:O}, raw {rawFloor:O})");

        /* The window that was empty in step 3 now has rows. */
        var rollupRowsAfter = await CountAsync(connection, $"SELECT count(*) FROM collect.{TimescaleSupport.QueryStatsHourlyView} WHERE bucket >= $1 AND bucket < $2", windowStart, now.AddDays(-6), ct);
        Assert.True(rollupRowsAfter > 0, "the backfilled window must now be materialized");

        /* ── 7. The router goes BACK to the rollup — the backfill restored acceleration rather than stranding
               every old read on raw forever. ── */
        Assert.Equal(
            RetentionTier.Hourly,
            RetentionTierRouter.Resolve(
                now, windowStart, rollups.QueryGrainHourly, rollups.QueryGrainDaily,
                after.For(TimescaleSupport.QueryStatsHourlyView, TimescaleSupport.QueryStatsDailyView)));

        /* ── 8. And the ARMING GATE now reports safe, which is the whole point: the held raw purge releases
               itself on the next service start, with no manual step and nothing armed by the backfill. Run
               through EnsureRetentionPoliciesAsync — the real seam — rather than re-evaluating its predicate,
               because "the gate would say yes" and "the gate DID arm the policy" are different claims. ── */
        await TimescaleSupport.EnsureRetentionPoliciesAsync(connection, null, ct);

        var armed = await CountAsync(connection, @"
SELECT count(*)
FROM timescaledb_information.jobs AS j
WHERE j.proc_name = 'policy_retention'
AND   j.hypertable_schema = 'collect'
AND   j.hypertable_name = 'query_stats'
AND   j.scheduled", ct);

        Assert.True(armed == 1, "query_stats' retention policy should have armed itself once the rollup covered it");
    }

    /// <summary>
    /// The other half of the same store shape, and the one a naive fix breaks: a rollup whose floor is later
    /// than the window but where RAW IS SHALLOWER — the healthy, armed-purge store. Falling back to raw there
    /// would return LESS than the rollup does, so the router must stay put. Proven against a live aggregate so
    /// it is the engine's row counts making the case, not our arithmetic.
    /// </summary>
    [Fact]
    public async Task HealthyStore_WhereRawIsShallowerThanTheRollup_DoesNotFallBackToRaw()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(baseConnectionString),
            "Set DARLING_TEST_PG to a Postgres connection string (with TimescaleDB installed) to run the live #1759 no-regression test.");

        var ct = TestContext.Current.CancellationToken;

        await using var scratch = await ScratchPostgres.CreateAsync(baseConnectionString!, ct);
        await using var connection = new NpgsqlConnection(scratch.ConnectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);

        Assert.True(await TimescaleSupport.TryEnableAsync(connection, null, ct));
        await TimescaleSupport.ConvertToHypertablesAsync(connection, null, ct);

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        /* Plant history, materialize ALL of it, then purge raw back to a short horizon — the steady state a
           healthy store reaches once its purges are armed. */
        await SeedHourlyQueryStatsAsync(connection, now.AddDays(-HistoryDays), now, ct);
        await TimescaleSupport.EnsureContinuousAggregatesAsync(connection, null, ct);
        await RollupBackfill.RunSliceAsync(
            connection, TimescaleSupport.QueryStatsHourlyView, now.Date.AddDays(-HistoryDays - 1), now.Date.AddDays(1), ct);

        await using (var purge = new NpgsqlCommand("DELETE FROM collect.query_stats WHERE collection_time < $1", connection))
        {
            purge.Parameters.AddWithValue(now.AddDays(-4));
            await purge.ExecuteNonQueryAsync(ct);
        }

        await using var dataSource = NpgsqlDataSource.Create(scratch.ConnectionString);
        var rollups = await TimescaleSupport.DetectRollupsAsync(dataSource, ct);
        var coverage = await TimescaleSupport.DetectRollupCoverageAsync(dataSource, rollups, ct);

        var floor = coverage.FloorOf(TimescaleSupport.QueryStatsHourlyView);
        var rawFloor = coverage.RawOldestOf("query_stats");
        Assert.NotNull(floor);
        Assert.NotNull(rawFloor);
        Assert.True(rawFloor > floor, "raw must be SHALLOWER than the rollup here, or this is not the healthy shape");

        /* A window older than BOTH floors. A naive "window predates the rollup's floor -> use raw" rule sends
           this to a table holding 4 days when the rollup holds 12. */
        var windowStart = now.AddDays(-30);

        Assert.Equal(
            RetentionTier.Hourly,
            RetentionTierRouter.Resolve(
                now, windowStart, rollups.QueryGrainHourly, rollups.QueryGrainDaily,
                coverage.For(TimescaleSupport.QueryStatsHourlyView, TimescaleSupport.QueryStatsDailyView)));

        /* And the row counts say why: the rollup genuinely answers more of that window than raw does. */
        var rollupRows = await CountAsync(connection, $"SELECT count(*) FROM collect.{TimescaleSupport.QueryStatsHourlyView} WHERE bucket >= $1", windowStart, ct);
        var rawRows = await CountAsync(connection, "SELECT count(*) FROM collect.query_stats WHERE collection_time >= $1", windowStart, ct);
        Assert.True(rollupRows > 0);
        Assert.True(rawRows > 0);
    }

    /// <summary>
    /// The VERB itself, end to end, against a real store — not a re-implementation of its loop.
    ///
    /// <para>This is the seam the two tests above cannot reach. They exercise the plan, the slice runner and
    /// the router, all of which can be perfectly correct while the verb that wires them together reports
    /// success it did not earn: the convergence verdict, the preflight refusal and the dry-run stop all live in
    /// the verb body, and every one of them is a place where "returned 0" and "actually did the thing" can
    /// come apart. Driven through <c>BackfillRollupsAsync</c> with a bring-your-own darling.json pointed at the
    /// scratch store, so the exit codes and the operator output are the real ones.</para>
    /// </summary>
    [Fact]
    public async Task BackfillRollupsVerb_DryRunsThenBackfills_AndReportsCoverageItActuallyReached()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(baseConnectionString),
            "Set DARLING_TEST_PG to a Postgres connection string (with TimescaleDB installed) to run the live --backfill-rollups verb test.");
        Assert.SkipUnless(OperatingSystem.IsWindows(), "--backfill-rollups is Windows-only (DPAPI store credential in managed mode).");

        var ct = TestContext.Current.CancellationToken;

        await using var scratch = await ScratchPostgres.CreateAsync(baseConnectionString!, ct);
        await using (var setup = new NpgsqlConnection(scratch.ConnectionString))
        {
            await setup.OpenAsync(ct);
            await PgMigrations.MigrateAsync(setup, ct);
            Assert.True(await TimescaleSupport.TryEnableAsync(setup, null, ct));
            await TimescaleSupport.ConvertToHypertablesAsync(setup, null, ct);

            var seedNow = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
            await SeedHourlyQueryStatsAsync(setup, seedNow.AddDays(-HistoryDays), seedNow, ct);
            await TimescaleSupport.EnsureContinuousAggregatesAsync(setup, null, ct);
        }

        var configPath = Path.Combine(Path.GetTempPath(), $"darling-1759-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            configPath,
            $$"""{"postgres":{"managed":false,"connectionString":{{System.Text.Json.JsonSerializer.Serialize(scratch.ConnectionString)}}},"servers":[]}""",
            ct);

        try
        {
            /* ── DRY RUN: reports a real plan and changes NOTHING. A dry run that quietly materialized would be
                  the worst possible bug in a verb whose entire reason to exist is not writing until it is
                  safe to. ── */
            var dryOut = new StringWriter();
            var dryErr = new StringWriter();
            Assert.Equal(0, await DarlingCliCommands.BackfillRollupsAsync(configPath, dryRun: true, dryOut, dryErr, ct));

            var dryText = dryOut.ToString();
            Assert.Contains("--dry-run: nothing was materialized", dryText, StringComparison.Ordinal);
            Assert.Contains("Free space on the store volume", dryText, StringComparison.Ordinal);
            Assert.Contains(TimescaleSupport.QueryStatsHourlyView, dryText, StringComparison.Ordinal);

            await using (var check = new NpgsqlConnection(scratch.ConnectionString))
            {
                await check.OpenAsync(ct);
                var probe = await RollupBackfill.ProbeAsync(check, TimescaleSupport.QueryStatsHourlyView, "query_stats", ct);

                /* Deliberately NOT "the rollup is still empty": the aggregate's own refresh policy is attached
                   by the ensure sweep and fires immediately, so a trailing window IS materialized by the time a
                   dry run finishes — by the POLICY, not by this verb. The property that actually distinguishes
                   a dry run from a real one is that the pre-existing history is still uncovered. */
                Assert.True(probe.CoverageOldestUtc is null || probe.CoverageOldestUtc > probe.RawOldestUtc,
                    $"--dry-run materialized the backfill: the rollup already covers back to {probe.CoverageOldestUtc:O} against raw's {probe.RawOldestUtc:O}");
            }

            /* ── REAL RUN ── */
            var runOut = new StringWriter();
            var runErr = new StringWriter();
            var exit = await DarlingCliCommands.BackfillRollupsAsync(configPath, dryRun: false, runOut, runErr, ct);

            var runText = runOut.ToString();
            Assert.True(exit == 0, $"the verb reported failure.\nSTDOUT:\n{runText}\nSTDERR:\n{runErr}");
            Assert.Contains("DONE. Every rollup now covers its raw table.", runText, StringComparison.Ordinal);

            /* The restart instruction is the operator's ONLY next step, and the trim race is the one thing that
               can waste the run — both must be in the output, not just in a comment. */
            Assert.Contains("restart the PerformanceMonitor Darling service", runText, StringComparison.Ordinal);
            Assert.Contains("Do not delay the restart", runText, StringComparison.Ordinal);

            /* ── The verb's success claim is CHECKED against the store, not taken at its word. ── */
            await using (var verify = new NpgsqlConnection(scratch.ConnectionString))
            {
                await verify.OpenAsync(ct);
                foreach (var target in RollupBackfill.Targets)
                {
                    var probe = await RollupBackfill.ProbeAsync(verify, target.View, target.RawTable, ct);
                    if (probe.RawOldestUtc is null)
                    {
                        continue; /* that raw table was never seeded, so there was nothing to cover. */
                    }

                    Assert.True(probe.CoverageOldestUtc is not null && probe.CoverageOldestUtc <= probe.RawOldestUtc,
                        $"the verb reported DONE but {target.View} starts at {probe.CoverageOldestUtc:O}, after {target.RawTable}'s oldest row {probe.RawOldestUtc:O}");
                }
            }

            /* ── IDEMPOTENT: a second run finds nothing to do and says so, rather than re-materializing. ── */
            var againOut = new StringWriter();
            Assert.Equal(0, await DarlingCliCommands.BackfillRollupsAsync(configPath, dryRun: false, againOut, new StringWriter(), ct));
            Assert.Contains("Every rollup already covers its raw table", againOut.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    /// <summary>One query_stats row per hour across [from, to) — enough shape for the CAGG to have something to
    /// group, and cheap enough that a 12-day history is a few hundred rows.</summary>
    private static async Task SeedHourlyQueryStatsAsync(
        NpgsqlConnection connection, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(@"
INSERT INTO collect.query_stats
    (collection_id, collection_time, server_id, server_name, database_name, query_hash, sql_handle,
     delta_worker_time, delta_elapsed_time, delta_execution_count)
SELECT
    (extract(epoch FROM g)::bigint),
    g,
    $3,
    'backfill-e2e',
    'TestDb',
    decode(md5((extract(epoch FROM g)::bigint % 5)::text), 'hex'),
    decode(md5('handle'), 'hex'),
    1000,
    2000,
    10
FROM generate_series($1::timestamp, $2::timestamp, INTERVAL '1 hour') AS g", connection);

        insert.Parameters.AddWithValue(from);
        insert.Parameters.AddWithValue(to);
        insert.Parameters.AddWithValue(TestServerId);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection connection, string sql, DateTime p1, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(p1);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection connection, string sql, DateTime p1, DateTime p2, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(p1);
        command.Parameters.AddWithValue(p2);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
