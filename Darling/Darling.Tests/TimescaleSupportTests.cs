/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the optional-TimescaleDB contract. Ungated: the hypertable scope is EXACTLY the shared
/// collector catalog (the registry/config/analysis tables can never sneak in), every
/// create_hypertable partitions by_range on the definition's own prefix time column
/// (collection_time almost everywhere; the config snapshots' capture_time) with if_not_exists +
/// migrate_data, compression segments by server_id, and the policy is the hardcoded 1-day
/// if_not_exists shape. Gated on DARLING_TEST_PG (the dev fixture has the extension): detect →
/// convert (idempotent) → a 40-day-old wait_stats row and a 70-day-old collection_log row are removed
/// by the drop_chunks-based purge (collection_log is a hypertable since V23) while a fresh row holds →
/// the compression policy applies idempotently and lands in timescaledb_information.jobs.
/// </summary>
/* Live-fixture tests share one Postgres store; the collection serializes them so
   cross-test row churn (inserts/purges/deletes/chunk drops) cannot race another class. */
[Collection("live-postgres")]
public sealed class TimescaleSupportTests
{
    /// <summary>Distinctive fake id — a real server_id is a storage-name hash, never this.</summary>
    private const int TestServerId = -717171;

    [Fact]
    public void HypertableScope_IsExactlyTheCollectorCatalog()
    {
        /* Scope = the catalog, table-for-table: 26 collector tables, nothing else. */
        Assert.Equal(
            CollectorCatalog.All.Select(s => s.TargetTable).ToArray(),
            TimescaleSupport.HypertableTables.Select(s => s.TargetTable).ToArray());

        /* The registry/config/analysis tables stay plain: registries keep their PRIMARY KEYs
           (which hypertables reject unless they include the partition column), and
           analysis_findings — designed keyless so it COULD convert later — is a deliberate
           not-yet. Widening the scope must consciously break this pin. */
        var hypertables = TimescaleSupport.HypertableTables.Select(s => s.TargetTable).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var excluded in new[]
        {
            "servers",
            "config_alert_log", "config_edge_trigger_watermarks", "config_mute_rules",
            "analysis_findings", "analysis_muted", "darling_schema_version",
        })
        {
            Assert.False(hypertables.Contains(excluded), $"'{excluded}' must never be converted to a hypertable");
        }

        /* collection_log IS a hypertable (since V23) but is deliberately NOT in the catalog: it is converted +
           compressed DIRECTLY — authoritatively by EnsureCollectionLogHypertableAsync at runtime, plus a
           best-effort V23-migration fast-path — and purged directly by DarlingRetention, so the catalog-driven
           runtime loops (ConvertToHypertables / ApplyCompressionPolicy) must never touch it. Its +1 IS reflected
           in the worker-sizing count, though (HypertableCount). */
        Assert.False(hypertables.Contains("collection_log"),
            "collection_log must stay OUT of the collector catalog — it is converted directly, not via the catalog loop");
        Assert.Equal(TimescaleSupport.HypertableTables.Count + 1, TimescaleSupport.HypertableCount);
    }

    [Fact]
    public void CreateHypertableSql_PartitionsByEachDefinitionsOwnTimeColumn()
    {
        var byName = CollectorCatalog.All.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            "SELECT create_hypertable('wait_stats', by_range('collection_time', INTERVAL '1 days'), if_not_exists => true, migrate_data => true)",
            TimescaleSupport.CreateHypertableSql(byName["wait_stats"]));

        /* The config snapshots partition on their capture_time, not collection_time. */
        Assert.Equal(
            "SELECT create_hypertable('server_config', by_range('capture_time', INTERVAL '1 days'), if_not_exists => true, migrate_data => true)",
            TimescaleSupport.CreateHypertableSql(byName["server_config"]));
        Assert.Equal(
            "SELECT create_hypertable('trace_flags', by_range('capture_time', INTERVAL '1 days'), if_not_exists => true, migrate_data => true)",
            TimescaleSupport.CreateHypertableSql(byName["trace_flags"]));

        /* Every table: its own prefix time column, 1-day chunk interval, idempotent, and existing
           plain-PG data migrates into chunks. */
        foreach (var schema in CollectorCatalog.All)
        {
            var sql = TimescaleSupport.CreateHypertableSql(schema);
            Assert.Contains($"create_hypertable('{schema.TargetTable}', by_range('{schema.PrefixTimeColumnName}', INTERVAL '1 days')", sql, StringComparison.Ordinal);
            Assert.Contains("if_not_exists => true", sql, StringComparison.Ordinal);
            Assert.Contains("migrate_data => true", sql, StringComparison.Ordinal);
        }

        /* collection_log's runtime conversion (the raw-name overload, since it has no ICollectorSchemaInfo) —
           the AUTHORITATIVE path EnsureCollectionLogHypertableAsync runs, identical shape to the collectors. */
        Assert.Equal(
            "SELECT create_hypertable('collection_log', by_range('collection_time', INTERVAL '1 days'), if_not_exists => true, migrate_data => true)",
            TimescaleSupport.CreateHypertableSql(TimescaleSupport.CollectionLogTable, TimescaleSupport.CollectionLogTimeColumn));
    }

    [Fact]
    public void CompressionSql_SegmentsByServerId_OneDayPolicy_IfNotExists()
    {
        var byName = CollectorCatalog.All.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            "ALTER TABLE wait_stats SET (timescaledb.compress, timescaledb.compress_segmentby = 'server_id')",
            TimescaleSupport.EnableCompressionSql(byName["wait_stats"]));
        Assert.Equal(
            "SELECT add_compression_policy('wait_stats', compress_after => INTERVAL '1 days', if_not_exists => true)",
            TimescaleSupport.AddCompressionPolicySql(byName["wait_stats"]));

        /* 1 day matches the 1-day chunk interval so chunks become compressible quickly, keeping the
           managed store compact (#1458). */
        Assert.Equal(1, TimescaleSupport.CompressAfterDays);

        foreach (var schema in CollectorCatalog.All)
        {
            Assert.Contains("timescaledb.compress_segmentby = 'server_id'",
                TimescaleSupport.EnableCompressionSql(schema), StringComparison.Ordinal);
            Assert.Contains("if_not_exists => true",
                TimescaleSupport.AddCompressionPolicySql(schema), StringComparison.Ordinal);
        }

        /* collection_log gets the identical compression via the raw-name overloads (the runtime path). */
        Assert.Equal(
            "ALTER TABLE collection_log SET (timescaledb.compress, timescaledb.compress_segmentby = 'server_id')",
            TimescaleSupport.EnableCompressionSql(TimescaleSupport.CollectionLogTable));
        Assert.Equal(
            "SELECT add_compression_policy('collection_log', compress_after => INTERVAL '1 days', if_not_exists => true)",
            TimescaleSupport.AddCompressionPolicySql(TimescaleSupport.CollectionLogTable));
    }

    /* ---------------- compression-job self-heal (#1581) — pure predicate ---------------- */

    private static readonly DateTime s_now = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsCompressionJobStuck_NextStartNegativeInfinity_IsStuck()
    {
        /* The dominant failure mode: next_start = -infinity, so the scheduler never re-fires it. Stuck
           regardless of status/last-run — it will never run again. */
        Assert.True(TimescaleSupport.IsCompressionJobStuck(
            nextStartIsNegativeInfinity: true, jobStatus: "Scheduled", lastRunStartedAtUtc: null,
            scheduleInterval: TimeSpan.FromHours(12), nowUtc: s_now, out var reason));
        Assert.Contains("-infinity", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void IsCompressionJobStuck_HealthyScheduled_IsNotStuck()
    {
        /* A normally scheduled job (finite next_start, not running) is healthy. */
        Assert.False(TimescaleSupport.IsCompressionJobStuck(
            nextStartIsNegativeInfinity: false, jobStatus: "Scheduled", lastRunStartedAtUtc: s_now.AddMinutes(-5),
            scheduleInterval: TimeSpan.FromHours(12), nowUtc: s_now, out var reason));
        Assert.Equal("", reason);
    }

    [Fact]
    public void IsCompressionJobStuck_RunningPastBound_IsStuck()
    {
        /* Running since well past max(2x interval, floor): 2x 1h = 2h == floor, and 5h elapsed > 2h -> hung. */
        Assert.True(TimescaleSupport.IsCompressionJobStuck(
            nextStartIsNegativeInfinity: false, jobStatus: "Running", lastRunStartedAtUtc: s_now.AddHours(-5),
            scheduleInterval: TimeSpan.FromHours(1), nowUtc: s_now, out var reason));
        Assert.Contains("Running", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void IsCompressionJobStuck_RunningWithinBound_IsNotStuck()
    {
        /* Running for 10 minutes with a 12h interval (bound = 24h) — legitimately in progress, not stuck. */
        Assert.False(TimescaleSupport.IsCompressionJobStuck(
            nextStartIsNegativeInfinity: false, jobStatus: "Running", lastRunStartedAtUtc: s_now.AddMinutes(-10),
            scheduleInterval: TimeSpan.FromHours(12), nowUtc: s_now, out _));
    }

    [Fact]
    public void IsCompressionJobStuck_RunningButNoStartTime_IsNotStuck()
    {
        /* Running with an unknown last_run_started_at cannot be judged as hung — do not false-flag. */
        Assert.False(TimescaleSupport.IsCompressionJobStuck(
            nextStartIsNegativeInfinity: false, jobStatus: "Running", lastRunStartedAtUtc: null,
            scheduleInterval: TimeSpan.FromHours(1), nowUtc: s_now, out _));
    }

    [Fact]
    public void IsCompressionJobStuck_RunningWithNeverRanSentinel_IsNotStuck()
    {
        /* #1760: TimescaleDB's never-ran sentinel is -infinity, which Npgsql maps to DateTime.MinValue. Read
           literally that is a run "started" in year 1 — an elapsed of ~739,000 days that clears every bound —
           so a healthy job got flagged as stuck for the whole of its FIRST run. StuckCompressionJobsSql NULLIFs
           the sentinel; this is the second line of defence, so a future caller reading the column un-guarded
           cannot resurrect the false positive. */
        Assert.False(TimescaleSupport.IsCompressionJobStuck(
            nextStartIsNegativeInfinity: false, jobStatus: "Running", lastRunStartedAtUtc: DateTime.MinValue,
            scheduleInterval: TimeSpan.FromHours(12), nowUtc: s_now, out _));
    }

    [Fact]
    public void StuckCompressionJobsSql_GuardsTheNeverRanSentinel()
    {
        /* The guard lives in the ONE query the detector and the live test both run. Containment, not shape:
           the point is that last_run_started_at is never read raw. */
        Assert.Contains("NULLIF(js.last_run_started_at, '-infinity'::timestamptz)",
            TimescaleSupport.StuckCompressionJobsSql, StringComparison.Ordinal);
    }

    [Fact]
    public void StuckRunningBound_UsesMaxOfTwiceIntervalAndFloor()
    {
        /* 2x a large interval wins over the floor. */
        Assert.Equal(TimeSpan.FromHours(24), TimescaleSupport.StuckRunningBound(TimeSpan.FromHours(12)));
        /* The 2-hour floor wins over 2x a tiny interval. */
        Assert.Equal(TimeSpan.FromHours(2), TimescaleSupport.StuckRunningBound(TimeSpan.FromMinutes(1)));
        /* A missing/zero interval falls back to the floor. */
        Assert.Equal(TimeSpan.FromHours(2), TimescaleSupport.StuckRunningBound(null));
        Assert.Equal(TimeSpan.FromHours(2), TimescaleSupport.StuckRunningBound(TimeSpan.Zero));
    }

    [Fact]
    public void RearmJobSql_IsParameterized_NotInterpolated()
    {
        /* The job_id is ALWAYS bound as $1, never interpolated; next_start is SQL now(), not a value. */
        Assert.Equal("SELECT alter_job($1::integer, next_start => now())", TimescaleSupport.RearmJobSql);
        Assert.Contains("$1", TimescaleSupport.RearmJobSql, StringComparison.Ordinal);
        Assert.Contains("next_start => now()", TimescaleSupport.RearmJobSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadStuckCompressionJobs_StoreUnavailable_ReturnsEmpty_DoesNotThrow()
    {
        /* Failure-isolated: when the timescaledb_information views are absent (a plain-PostgreSQL store — the
           belt-and-suspenders behind the worker's _timescaleAvailable gate) or the store is unreachable, the
           read returns an empty list and logs at Debug, NEVER throwing into the sweep loop. An unopened
           connection exercises that catch deterministically without a live store. */
        using var connection = new NpgsqlConnection("Host=localhost;Port=1;Database=darling-does-not-exist");
        var result = await TimescaleSupport.ReadStuckCompressionJobsAsync(
            connection, DateTime.UtcNow, logger: null, TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task TryRearmJob_StoreUnavailable_ReturnsFalse_DoesNotThrow()
    {
        /* An alter_job failure (permission denied on a least-privilege BYO store, or a store hiccup) degrades to
           false + a single log line — never a crash. An unopened connection stands in for any such failure. */
        using var connection = new NpgsqlConnection("Host=localhost;Port=1;Database=darling-does-not-exist");
        Assert.False(await TimescaleSupport.TryRearmJobAsync(
            connection, jobId: 1234, logger: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EndToEnd_DetectConvertAndDropChunksPurge_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string (with TimescaleDB installed) to run the live Timescale test.");

        var ct = TestContext.Current.CancellationToken;

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);

        /* The dev fixture has the extension (validated live on 2.28.1): enable must succeed and
           detection must agree. */
        Assert.True(await TimescaleSupport.TryEnableAsync(connection, null, ct),
            "the dev fixture is expected to have TimescaleDB installed");
        Assert.True(await TimescaleSupport.DetectAsync(connection, ct));

        /* Conversion covers every collector table and is idempotent (if_not_exists no-ops). */
        Assert.Equal(CollectorCatalog.All.Count, await TimescaleSupport.ConvertToHypertablesAsync(connection, null, ct));
        Assert.Equal(CollectorCatalog.All.Count, await TimescaleSupport.ConvertToHypertablesAsync(connection, null, ct));

        /* wait_stats really is a hypertable now — so the purge below genuinely exercises
           drop_chunks, not the per-table DELETE fallback. */
        using (var isHypertable = new NpgsqlCommand(
            "SELECT COUNT(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'wait_stats'", connection))
        {
            Assert.Equal(1L, await isHypertable.ExecuteScalarAsync(ct));
        }

        /* collection_log is ALSO a hypertable now — but NOT via ConvertToHypertablesAsync (it is outside the
           collector catalog). The V23 migration converts it only on an upgrade where the extension already
           exists; on a store whose migrations ran BEFORE CREATE EXTENSION (this shared test database, and any
           fresh managed store) V23's guard skips and the AUTHORITATIVE runtime path is
           EnsureCollectionLogHypertableAsync — the same call the service makes right after TryEnableAsync on
           every start. Exercise it exactly like the service does, then the purge below genuinely hits
           drop_chunks, not the DELETE fallback. */
        Assert.True(await TimescaleSupport.EnsureCollectionLogHypertableAsync(connection, null, ct),
            "EnsureCollectionLogHypertableAsync is expected to convert (or no-op on) collection_log once the extension is enabled");

        using (var logIsHypertable = new NpgsqlCommand(
            "SELECT COUNT(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'collection_log'", connection))
        {
            Assert.Equal(1L, await logIsHypertable.ExecuteScalarAsync(ct));
        }

        /* Clear leftovers from an earlier aborted run so the assertions below are deterministic. */
        await DeleteTestRowsAsync(connection);

        await using var postgres = NpgsqlDataSource.Create(connectionString!);

        try
        {
            /* All timestamps Kind-Unspecified — naive-UTC storage, see PgCollectorRowWriter. */
            var utcNow = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

            /* wait_stats retention is 30 days. The old row is 40 days back so its WHOLE chunk
               (7-day default width → spanning at most now-43d..now-36d) is past the horizon —
               drop_chunks only drops fully-expired chunks. The fresh row lives in the current
               chunk, which can never be fully expired. */
            using (var insert = new NpgsqlCommand(
                "INSERT INTO wait_stats (collection_id, collection_time, server_id, server_name) VALUES ($1, $2, $3, $4)", connection))
            {
                insert.Parameters.AddWithValue(1L);
                insert.Parameters.AddWithValue(utcNow.AddDays(-40));
                insert.Parameters.AddWithValue(TestServerId);
                insert.Parameters.AddWithValue("timescale-e2e");
                await insert.ExecuteNonQueryAsync(ct);
            }

            using (var insert = new NpgsqlCommand(
                "INSERT INTO wait_stats (collection_id, collection_time, server_id, server_name) VALUES ($1, $2, $3, $4)", connection))
            {
                insert.Parameters.AddWithValue(2L);
                insert.Parameters.AddWithValue(utcNow.AddHours(-1));
                insert.Parameters.AddWithValue(TestServerId);
                insert.Parameters.AddWithValue("timescale-e2e");
                await insert.ExecuteNonQueryAsync(ct);
            }

            /* collection_log is a hypertable since V23, so in Timescale mode it purges via drop_chunks too.
               drop_chunks only drops WHOLE expired chunks, so this row must be past collection_log's own 2x
               horizon (60 days) for its 1-day chunk to be fully expired: 70 days back. (A row inside the 60-day
               window would survive — exercised on the plain-PG DELETE path in DarlingRetentionTests.) */
            using (var insert = new NpgsqlCommand(
                "INSERT INTO collection_log (log_id, server_id, server_name, collector_name, collection_time, status) VALUES ($1, $2, $3, $4, $5, $6)", connection))
            {
                insert.Parameters.AddWithValue(1L);
                insert.Parameters.AddWithValue(TestServerId);
                insert.Parameters.AddWithValue("timescale-e2e");
                insert.Parameters.AddWithValue("wait_stats");
                insert.Parameters.AddWithValue(utcNow.AddDays(-70));
                insert.Parameters.AddWithValue("SUCCESS");
                await insert.ExecuteNonQueryAsync(ct);
            }

            /* The Timescale purge. Deliberately NO assertion on the returned global activity count
               (#1564): chunk drops are per-table + time-window across the WHOLE shared store, so sibling
               collection classes' rows make the global number order-dependent. The contract is the
               OWN-SCOPED evidence below: this server's fresh row survives, its old rows are gone — plus
               the is-hypertable assertions above proving the drop_chunks branch was in play. If
               drop_chunks transiently fails (e.g. a lock clash with the shared fixture's compression
               policy jobs, which run mid-suite), the time-sliced DELETE fallback now clears the rows even
               inside a compressed chunk — the capturing logger surfaces any such fallback in the failure
               text instead of silencing it (a silent skip was #1564's whole failure mode). */
            var purgeLog = new CapturingTestLogger();
            await DarlingRetention.PurgeAsync(postgres, timescaleAvailable: true, purgeLog, ct);

            using (var read = new NpgsqlCommand(
                "SELECT collection_time FROM wait_stats WHERE server_id = $1", connection))
            {
                read.Parameters.AddWithValue(TestServerId);
                using var reader = await read.ExecuteReaderAsync(ct);
                Assert.True(await reader.ReadAsync(ct), $"the fresh wait_stats row did not survive the drop_chunks purge; {purgeLog.Joined}");
                var survivor = reader.GetDateTime(0);
                Assert.True(survivor > utcNow.AddDays(-1), $"the surviving row should be the 1-hour one, got {survivor:O}; {purgeLog.Joined}");
                Assert.False(await reader.ReadAsync(ct), $"the 40-day wait_stats row survived the drop_chunks purge; {purgeLog.Joined}");
            }

            /* The 70-day collection_log row went — via drop_chunks (past the 60-day horizon), or via the
               DELETE fallback if drop_chunks transiently failed. */
            using (var read = new NpgsqlCommand(
                "SELECT COUNT(*) FROM collection_log WHERE server_id = $1", connection))
            {
                read.Parameters.AddWithValue(TestServerId);
                var remaining = (long)(await read.ExecuteScalarAsync(ct))!;
                Assert.True(remaining == 0L, $"the 70-day collection_log row survived the purge ({remaining} row(s)); {purgeLog.Joined}");
            }
        }
        finally
        {
            await DeleteTestRowsAsync(connection);
        }
    }

    [Fact]
    public async Task EndToEnd_CompressionPolicyApplies_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string (with TimescaleDB installed) to run the live compression test.");

        var ct = TestContext.Current.CancellationToken;

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);

        Assert.True(await TimescaleSupport.TryEnableAsync(connection, null, ct),
            "the dev fixture is expected to have TimescaleDB installed");

        /* Compression needs hypertables first — idempotent, so safe regardless of test order. */
        Assert.Equal(CollectorCatalog.All.Count, await TimescaleSupport.ConvertToHypertablesAsync(connection, null, ct));

        /* Applies cleanly and idempotently (the second pass re-runs ALTER SET and the policy
           no-ops on if_not_exists). */
        Assert.Equal(CollectorCatalog.All.Count, await TimescaleSupport.ApplyCompressionPolicyAsync(connection, null, ct));
        Assert.Equal(CollectorCatalog.All.Count, await TimescaleSupport.ApplyCompressionPolicyAsync(connection, null, ct));

        /* The background job really exists. proc_name is 'policy_compression' on the long-stable
           API; the LIKE also tolerates the 2.18+ columnstore rebrand's naming. */
        using (var job = new NpgsqlCommand(@"
SELECT COUNT(*)
FROM timescaledb_information.jobs
WHERE hypertable_name = 'wait_stats'
  AND (proc_name LIKE '%compression%' OR proc_name LIKE '%columnstore%')", connection))
        {
            var jobs = (long)(await job.ExecuteScalarAsync(ct))!;
            Assert.True(jobs >= 1, "expected a compression policy job on wait_stats in timescaledb_information.jobs");
        }

        /* Deliberately NO policy removal on cleanup: the applied policies are the service's
           real end state on this fixture, and if_not_exists keeps every rerun a no-op. */
    }

    /// <summary>
    /// #1705: EXECUTES the retention policies instead of string-matching the SQL. The bug this replaces shipped
    /// precisely because the only pin asserted the generated string contained <c>scheduled =&gt; false</c> — an
    /// argument <c>add_retention_policy</c> has never had — so the pin passed while the statement failed 42883 on
    /// every store and the per-policy catch downgraded it to a warning. Nothing that reads a string can catch
    /// that; only running it can. Asserts every policy is created (not swallowed), lands in
    /// <c>timescaledb_information.jobs</c>, and is created PAUSED so #1680's guarantee still holds.
    /// </summary>
    [Fact]
    public async Task EndToEnd_RetentionPoliciesActuallyApply_AndAreCreatedPaused_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string (with TimescaleDB installed) to run the live retention test.");

        var ct = TestContext.Current.CancellationToken;

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);

        Assert.True(await TimescaleSupport.TryEnableAsync(connection, null, ct),
            "the dev fixture is expected to have TimescaleDB installed");
        Assert.Equal(CollectorCatalog.All.Count, await TimescaleSupport.ConvertToHypertablesAsync(connection, null, ct));

        /* This test MUTATES the shared fixture's shape, so it restores it. Creating the hourly CAGGs changes
           compose's tier routing (RunComposedPanel_OldWindow_AgainstPlainPostgres_RunsCleanOnRaw asserts a
           10-day window lands on RAW, which only holds while no rollup exists), and leaving an ARMED raw
           retention policy behind could drop chunks another live test planted. Snapshot what already exists,
           and drop only what this test creates. */
        var preexistingCaggs = await ExistingCaggsAsync(connection, ct);

        try
        {
            /* Retention targets the hourly CAGGs as well as the raw tables, so the aggregates must exist first —
               the same ordering EnsureRetentionPoliciesAsync documents. */
            await TimescaleSupport.EnsureContinuousAggregatesAsync(connection, null, ct);

            /* THE assertion: every policy applied. A 42883 would be caught per-policy and counted as 0. */
            var applied = await TimescaleSupport.EnsureRetentionPoliciesAsync(connection, null, ct);
            Assert.True(applied == RetentionPolicyCount,
                $"expected all {RetentionPolicyCount} retention policies to apply, got {applied} — a swallowed error means the policy SQL is invalid on this TimescaleDB");

            /* Idempotent: the second pass hits if_not_exists (job_id -1) and must not throw on alter_job(-1). */
            Assert.Equal(RetentionPolicyCount, await TimescaleSupport.EnsureRetentionPoliciesAsync(connection, null, ct));

            using var job = new NpgsqlCommand(@"
SELECT COUNT(*)
FROM timescaledb_information.jobs
WHERE proc_name = 'policy_retention'
AND   hypertable_schema = 'collect'", connection);
            var jobs = (long)(await job.ExecuteScalarAsync(ct))!;
            Assert.True(jobs >= RetentionPolicyCount,
                $"expected at least {RetentionPolicyCount} policy_retention jobs on collect.*, found {jobs}");

            /* Created PAUSED (#1680). The invariant that holds whether or not the safety check armed a policy:
               none may be ARMED while its source still holds rows its coverage tier does not cover. An
               un-paused creation is exactly what would violate it. */
            using var unsafeArmed = new NpgsqlCommand(@"
SELECT COUNT(*)
FROM timescaledb_information.jobs AS j
WHERE j.proc_name = 'policy_retention'
AND   j.hypertable_schema = 'collect'
AND   j.scheduled
AND   j.hypertable_name = 'query_stats'
AND   (SELECT min(collection_time) FROM collect.query_stats) IS NOT NULL
AND   ((SELECT min(bucket) FROM collect.query_stats_hourly) IS NULL
       OR (SELECT min(bucket) FROM collect.query_stats_hourly) > (SELECT min(collection_time) FROM collect.query_stats))", connection);
            var bad = (long)(await unsafeArmed.ExecuteScalarAsync(ct))!;
            Assert.True(bad == 0, "a retention policy is ARMED while its coverage tier does not cover everything the source holds — creation was not paused");
        }
        finally
        {
            /* Retention policies first (they reference the relations), then only the CAGGs this test created —
               DROP ... CASCADE takes each aggregate's own policy with it. */
            foreach (var relation in RetentionRelations)
            {
                await TryExecAsync(connection, $"SELECT remove_retention_policy('collect.{relation}', if_exists => true)", ct);
            }

            foreach (var cagg in (await ExistingCaggsAsync(connection, ct)).Except(preexistingCaggs, StringComparer.Ordinal))
            {
                await TryExecAsync(connection, $"DROP MATERIALIZED VIEW IF EXISTS collect.{cagg} CASCADE", ct);
            }
        }
    }

    /// <summary>The continuous aggregates present in <c>collect</c> right now, so the retention test can drop
    /// exactly the ones it created and leave a pre-existing store's shape alone.</summary>
    private static async Task<string[]> ExistingCaggsAsync(NpgsqlConnection connection, System.Threading.CancellationToken ct)
    {
        using var command = new NpgsqlCommand(
            "SELECT view_name FROM timescaledb_information.continuous_aggregates WHERE view_schema = 'collect'", connection);
        using var reader = await command.ExecuteReaderAsync(ct);
        var names = new System.Collections.Generic.List<string>();
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    /// <summary>Best-effort cleanup statement — a teardown failure must not mask the assertion that already ran.</summary>
    private static async Task TryExecAsync(NpgsqlConnection connection, string sql, System.Threading.CancellationToken ct)
    {
        try
        {
            using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException)
        {
        }
    }

    /// <summary>
    /// The relations EnsureRetentionPoliciesAsync attaches policies to, for teardown: the three raw tables,
    /// the four hourly CAGGs, and the nine baseline aggregates (#1757). The last group is DERIVED from the
    /// product's own list rather than restated, so adding a baseline aggregate cannot leave an armed retention
    /// policy behind on this shared fixture.
    /// </summary>
    private static readonly string[] RetentionRelations = new[]
    {
        "query_stats", "procedure_stats", "query_store_stats",
        "query_stats_hourly", "procedure_stats_hourly", "query_store_stats_hourly", "query_stats_db_hourly",
    }
    .Concat(TimescaleSupport.BaselineAggregates.Select(a => a.View))
    .ToArray();

    /// <summary>The policy set EnsureRetentionPoliciesAsync attaches, derived so the two cannot drift.</summary>
    private static readonly int RetentionPolicyCount = RetentionRelations.Length;

    [Fact]
    public async Task CompressionJobSelfHeal_DetectionQueryValid_AndRearmSucceeds_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string (with TimescaleDB installed) to run the live compression self-heal test.");

        var ct = TestContext.Current.CancellationToken;

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);

        Assert.True(await TimescaleSupport.TryEnableAsync(connection, null, ct),
            "the dev fixture is expected to have TimescaleDB installed");
        Assert.Equal(CollectorCatalog.All.Count, await TimescaleSupport.ConvertToHypertablesAsync(connection, null, ct));
        Assert.Equal(CollectorCatalog.All.Count, await TimescaleSupport.ApplyCompressionPolicyAsync(connection, null, ct));

        /* Pick one real compression policy job (on wait_stats). */
        long jobId;
        using (var find = new NpgsqlCommand(@"
SELECT j.job_id
FROM timescaledb_information.jobs AS j
WHERE j.hypertable_name = 'wait_stats'
  AND (j.proc_name LIKE '%compression%' OR j.proc_name LIKE '%columnstore%')
ORDER BY j.job_id
LIMIT 1", connection))
        {
            var result = await find.ExecuteScalarAsync(ct);
            Assert.NotNull(result);
            jobId = Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }

        /* (1) The detection query is valid SQL against the REAL timescaledb_information job_stats/jobs views
           (including the `next_start = '-infinity'::timestamptz` comparison), and a healthy compression job is
           NOT flagged — no false alarm. This is the full ReadStuckCompressionJobsAsync path against the live
           schema.

           A just-added compression policy job can momentarily read next_start = '-infinity' in job_stats
           BEFORE TimescaleDB's background scheduler assigns its first real next run — and the detector is
           CORRECT to flag -infinity — so asserting immediately after ApplyCompressionPolicy raced that window
           and intermittently false-failed on a slow CI runner. Deterministically settle the job into the
           healthy state the assertion is actually about: give it a real FUTURE next_start (via the same
           alter_job the self-heal uses), then wait for the catalog to reflect a non-(-infinity) next_start. */
        using (var arm = new NpgsqlCommand("SELECT alter_job($1::integer, next_start => now() + interval '1 hour')", connection))
        {
            arm.Parameters.Add(new NpgsqlParameter { Value = jobId });
            await arm.ExecuteNonQueryAsync(ct);
        }
        await WaitUntilDetectorReportsHealthyAsync(connection, jobId, ct);

        /* The SQL really is valid against the live catalog, and this job really is in its result set.
           ReadStuckCompressionJobsAsync is failure-isolated (a broken query is swallowed and returns an EMPTY
           list), so DoesNotContain ALONE would pass just as happily against SQL that never compiled — the one
           thing this leg claims to prove. Run the production const directly, where a syntax or column error
           throws, and require the job to be present: only then does "not flagged" mean the detector looked at
           this job and judged it healthy. */
        var observed = await ReadObservedJobIdsAsync(connection, ct);
        Assert.Contains(jobId, observed);

        var healthy = await TimescaleSupport.ReadStuckCompressionJobsAsync(connection, DateTime.UtcNow, null, ct);
        Assert.DoesNotContain(healthy, s => s.JobId == jobId);

        /* (2) The #1586 REGRESSION GUARD: the production re-arm runs the real alter_job against TimescaleDB and
           SUCCEEDS. The job_id MUST be sent as `integer`, not `bigint` — an un-cast bound long fails with
           `42883: function alter_job(bigint, ...) does not exist`, which shipped in #1585 and made every
           self-heal re-arm silently throw. This drives the exact production path (TryRearmJobAsync ->
           RearmJobSql `alter_job($1::integer, next_start => now())`), which the unit tests — using a fake re-arm
           delegate — cannot reach.

           We deliberately do NOT simulate the stuck state by forcing next_start to -infinity: TimescaleDB
           REJECTS `alter_job(..., next_start => '-infinity')` with `22023: cannot set next start to -infinity`
           (the dead-scheduler -infinity arises from TimescaleDB's own background scheduler on a failed run, not
           from a user call, so it cannot be injected through the public API). The -infinity / Running-past-bound
           DETECTION logic is covered by the pure IsCompressionJobStuck unit tests. */
        Assert.True(await TimescaleSupport.TryRearmJobAsync(connection, jobId, null, ct));

        /* (3) After a real re-arm (next_start => now()) the job is scheduled/running within bound, not stuck. */
        var afterRearm = await TimescaleSupport.ReadStuckCompressionJobsAsync(connection, DateTime.UtcNow, null, ct);
        Assert.DoesNotContain(afterRearm, s => s.JobId == jobId);
    }

    /// <summary>
    /// Wait until <see cref="TimescaleSupport.ReadStuckCompressionJobsAsync"/> — the DETECTION QUERY ITSELF,
    /// the thing under test — stops flagging this job. #1760: the predecessor polled
    /// <c>next_start &lt;&gt; '-infinity'</c> directly, which is only ONE of the two arms the detector
    /// evaluates, so "settled" and "the assertion will pass" were different statements and the gap between
    /// them was the flake. Worse, the value it polled was the one the caller's own
    /// <c>alter_job(next_start =&gt; …)</c> had just written, so it was satisfied on its first poll and waited
    /// for nothing at all — no timeout increase could ever have helped.
    ///
    /// <para>Polling the detector closes that by construction: there is one predicate, not two copies that
    /// drift, so settled-according-to-the-wait IS settled-according-to-the-assertion. Bounded, and it fails
    /// loudly carrying the detector's OWN reason string — a job that never settles is genuinely stuck and must
    /// not silently pass, and the reason names which arm held it rather than assuming <c>next_start</c>.</para>
    /// </summary>
    private static async Task WaitUntilDetectorReportsHealthyAsync(
        NpgsqlConnection connection, long jobId, System.Threading.CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var flagged = await TimescaleSupport.ReadStuckCompressionJobsAsync(connection, DateTime.UtcNow, null, ct);
            var mine = flagged.FirstOrDefault(s => s.JobId == jobId);
            if (mine is null)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline,
                $"compression job {jobId} was still flagged as stuck after 30s: {mine.Reason}");
            await Task.Delay(250, ct);
        }
    }

    /// <summary>
    /// Every job_id the production detection query OBSERVES — not just the ones it flags — by running
    /// <see cref="TimescaleSupport.StuckCompressionJobsSql"/> itself. Sharing the const is the whole point: a
    /// paraphrase here could compile happily while the real query did not.
    /// </summary>
    private static async Task<List<long>> ReadObservedJobIdsAsync(
        NpgsqlConnection connection, System.Threading.CancellationToken ct)
    {
        var ids = new List<long>();
        using var command = new NpgsqlCommand(TimescaleSupport.StuckCompressionJobsSql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
        }

        return ids;
    }

    /// <summary>
    /// #1760 root cause, pinned deterministically: TimescaleDB's never-ran sentinel in
    /// <c>last_run_started_at</c> is <b>-infinity</b>, not NULL, and Npgsql maps that to
    /// <see cref="DateTime.MinValue"/>. Un-guarded, that turned "never ran" into "started in year 1" — an
    /// elapsed of ~739,000 days that clears every <see cref="TimescaleSupport.StuckRunningBound"/> — so a
    /// healthy job was flagged stuck for the whole of its FIRST run, whenever the detector's read landed while
    /// <c>job_status</c> already said <c>Running</c>. Those two fields come from independent sources in
    /// TimescaleDB's own view (<c>pg_stat_activity.state</c> vs <c>bgw_job_stat.last_start</c>), so that window
    /// is structural, not hypothetical.
    ///
    /// <para>Deterministic, not timing-dependent: a freshly added policy has no <c>bgw_job_stat</c> row at all
    /// (every column NULL), and it is <c>alter_job</c> that materialises the row carrying the -infinity
    /// sentinel — so arming it an hour out both creates the row and guarantees the scheduler cannot run the job
    /// out from under the assertion. Asserts the raw column really does carry the sentinel (otherwise the
    /// NULLIF guard would be dead code that passes for the wrong reason) AND that the production query hands
    /// back NULL for it.</para>
    /// </summary>
    [Fact]
    public async Task StuckCompressionJobsSql_NeverRunJob_ReadsNullLastRunStartedAt_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string (with TimescaleDB installed) to run the never-ran sentinel pin.");

        var ct = TestContext.Current.CancellationToken;

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        /* Migrate first, as every other gated test in this class does. It is not the schema this test wants —
           it is the search_path: the migration sets the database to collect, config, public, and WITHOUT public
           on the path TimescaleDB's own by_range is unresolvable, so create_hypertable fails to resolve its
           argument and the whole statement dies as 42883 by_range(unknown, interval) does not exist. Skipping
           this line is what made the test pass on a rig whose search_path a previous run had already set, and
           fail on CI's throwaway cluster. */
        /* Migrate first, as every other gated test in this class does. It is not the schema this test wants -
           it is the search_path. TimescaleDB'''s by_range lives in PUBLIC, so a session whose search_path omits
           public cannot resolve it, create_hypertable never resolves its argument, and the statement dies as
           42883 by_range(unknown, interval) does not exist. Skipping this line passed on a rig whose connection
           carried the default "$user", public and failed on CI, whose connection pins collect,config. */
        await PgMigrations.MigrateAsync(connection, ct);

        Assert.True(await TimescaleSupport.TryEnableAsync(connection, null, ct),
            "the dev fixture is expected to have TimescaleDB installed");

        const string table = "stuck_sentinel_probe_1760";
        try
        {
            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {table} CASCADE", ct);
            await ExecuteAsync(connection,
                $"CREATE TABLE {table} (collection_time timestamptz NOT NULL, server_id integer NOT NULL)", ct);
            /* The product's own SQL builders, not hand-rolled equivalents: a one-argument by_range('col') is
               accepted by TimescaleDB 2.28 but not by the older version CI's fixture carries, and the point of
               this test is the catalog's behaviour rather than a second dialect of the same DDL. */
            await ExecuteAsync(connection, TimescaleSupport.CreateHypertableSql(table, "collection_time"), ct);
            await ExecuteAsync(connection, TimescaleSupport.EnableCompressionSql(table), ct);
            await ExecuteAsync(connection, TimescaleSupport.AddCompressionPolicySql(table), ct);

            long jobId;
            using (var find = new NpgsqlCommand(
                $"SELECT job_id FROM timescaledb_information.jobs WHERE hypertable_name = '{table}' "
                + "AND (proc_name LIKE '%compression%' OR proc_name LIKE '%columnstore%') ORDER BY job_id LIMIT 1",
                connection))
            {
                var found = await find.ExecuteScalarAsync(ct);
                Assert.NotNull(found);
                jobId = Convert.ToInt64(found, System.Globalization.CultureInfo.InvariantCulture);
            }

            /* Materialise the stat row and park the job an hour out so it cannot run mid-assertion. */
            using (var arm = new NpgsqlCommand(
                "SELECT alter_job($1::integer, next_start => now() + interval '1 hour')", connection))
            {
                arm.Parameters.Add(new NpgsqlParameter { Value = jobId });
                await arm.ExecuteNonQueryAsync(ct);
            }

            /* The sentinel is really there — the guard is not dead code. */
            using (var raw = new NpgsqlCommand(
                "SELECT last_run_started_at = '-infinity'::timestamptz FROM timescaledb_information.job_stats WHERE job_id = $1::integer",
                connection))
            {
                raw.Parameters.Add(new NpgsqlParameter { Value = jobId });
                Assert.Equal(true, await raw.ExecuteScalarAsync(ct));
            }

            /* ...and the production query neutralises it, so the stuck-Running arm cannot fire on a job that
               has never run. Without the NULLIF this reads DateTime.MinValue. */
            using (var guarded = new NpgsqlCommand(TimescaleSupport.StuckCompressionJobsSql, connection))
            {
                await using var reader = await guarded.ExecuteReaderAsync(ct);
                var sawJob = false;
                while (await reader.ReadAsync(ct))
                {
                    if (Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) != jobId)
                    {
                        continue;
                    }

                    sawJob = true;
                    Assert.True(reader.IsDBNull(3),
                        "a never-run compression job must read a NULL last_run_started_at through the production "
                        + "query; a non-null value here is the year-1 elapsed that flagged healthy jobs as stuck.");
                }

                Assert.True(sawJob, $"the production query did not observe compression job {jobId}.");
            }

            /* The pure predicate agrees end to end: never-ran + Running is NOT stuck. */
            Assert.False(
                TimescaleSupport.IsCompressionJobStuck(false, "Running", null, TimeSpan.FromHours(12), DateTime.UtcNow, out _));
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {table} CASCADE", ct);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, System.Threading.CancellationToken ct)
    {
        using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteTestRowsAsync(NpgsqlConnection connection)
    {
        using var cleanup = new NpgsqlCommand(
            $"DELETE FROM wait_stats WHERE server_id = {TestServerId}; DELETE FROM collection_log WHERE server_id = {TestServerId};", connection);
        await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
