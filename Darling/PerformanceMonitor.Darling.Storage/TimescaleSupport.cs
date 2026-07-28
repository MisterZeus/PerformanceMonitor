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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using PerformanceMonitor.Collectors;

namespace PerformanceMonitor.Darling.Storage;

/// <summary>
/// Optional TimescaleDB adoption — RUNTIME setup, deliberately NOT a versioned migration. The
/// store must work with or without the extension (plain PostgreSQL remains fully supported), so
/// the versioned <see cref="PgMigrations"/> scripts stay engine-plain and every Timescale feature
/// here is gated on extension presence, detected at runtime, never assumed. The service calls
/// <see cref="TryEnableAsync"/> once at startup right after migration; when the extension is
/// present it converts the collector tables to hypertables and applies compression policies —
/// all idempotent (<c>if_not_exists</c> everywhere), so every restart re-converges, and a store
/// that grew new collector tables since the last start picks them up on the next.
///
/// Scope: the COLLECTOR tables only (<see cref="HypertableTables"/> = the shared catalog). The
/// registry/config tables (servers, config_alert_log, config_edge_trigger_watermarks,
/// config_mute_rules, analysis_muted, darling_schema_version) are deliberately excluded —
/// registries keep their PRIMARY KEYs, which TimescaleDB would reject or force onto the partition
/// column, and none of them is time-series-shaped growth. analysis_findings COULD be a hypertable
/// later (it was designed keyless for exactly this, see the V4 remarks) — deliberately not
/// converted yet; revisit when finding volume warrants it.
///
/// <para><c>collection_log</c> IS a hypertable (the per-run observability log — the store's
/// highest-volume plain table), but it is converted + compressed DIRECTLY by the V23 migration
/// (<see cref="PgMigrations"/>), NOT here, because it lives OUTSIDE the collector catalog (it has no
/// <c>ICollectorSchemaInfo</c>), so the catalog-driven loops below never reach it. Its retention is
/// likewise handled directly by DarlingRetention (<c>drop_chunks</c>). It is counted in
/// <see cref="HypertableCount"/> so worker sizing reflects its compression policy.</para>
///
/// The collector tables were designed for this conversion: no PRIMARY KEY (see the
/// <see cref="PgSchemaGenerator"/> remarks) and a NOT NULL prefix time column per table
/// (<see cref="ICollectorSchemaInfo.PrefixTimeColumnName"/> — "collection_time" almost
/// everywhere, the config snapshots' "capture_time", memory_pressure_events included: its
/// prefix column is still collection_time; payload sample_time is not the partition column).
/// The partition columns are naive-UTC <c>timestamp</c> by the product-wide cross-store
/// contract, so create_hypertable emits an advisory use-TIMESTAMPTZ WARNING — expected and
/// accepted (validated live on TimescaleDB 2.28.1).
/// </summary>
public static class TimescaleSupport
{
    /// <summary>
    /// Compress chunks older than this many days — hardcoded (defaults over speculative config).
    /// Compressed chunks remain fully queryable, just columnar and ~10-20x smaller: this IS
    /// Darling's archival tier, the centralized-store answer to Lite's parquet archive, keeping the
    /// full retention horizon cheap instead of splitting hot/cold stores. Kept short (1 day) to
    /// match <see cref="ChunkIntervalDays"/>: at the collectors' 1-minute cadence a longer lag left
    /// the whole store uncompressed (a chunk cannot compress until it closes AND then ages past
    /// this), so even a near-idle fleet grew ~1 GB in a couple of days of hot data. Collectors only
    /// ever append current-time rows, so a day-old chunk never takes another write — safe to
    /// compress. Measured on this data: perfmon ~16.7x, plan-XML-heavy query_stats ~6.4x.
    /// </summary>
    public const int CompressAfterDays = 1;

    /// <summary>
    /// Hypertable chunk width in days. TimescaleDB's 7-day default is far too coarse for
    /// 1-minute-cadence monitoring data: a chunk stays open (and uncompressible) for its whole
    /// span, so 7-day chunks meant nothing compressed for ~2 weeks. 1-day chunks close daily and
    /// become compressible within <see cref="CompressAfterDays"/>, keeping the store compact.
    /// Applies at hypertable creation (fresh stores); existing chunks keep their original width.
    /// </summary>
    public const int ChunkIntervalDays = 1;

    /* The first conversion of a long-collected plain-PG store rewrites every row into chunks
       (migrate_data); Npgsql's default 30-second command timeout would abandon it halfway.
       Same budget reasoning as DarlingRetention's first-purge DELETE. */
    private const int SetupTimeoutSeconds = 300;

    /// <summary>
    /// Timeout for a one-time bulk aggregate materialization, as opposed to the setup statements
    /// <see cref="SetupTimeoutSeconds"/> covers. NOT a timeout bump papering over a slow query: this is a
    /// deliberate bulk backfill whose duration scales with how much history the store already had, and the
    /// 5-minute setup budget would abort it mid-way on any store large enough to need it. Bounded rather than
    /// infinite so a wedged connection still fails eventually, and safe to hit: TimescaleDB commits the
    /// refresh in per-batch transactions, so an abort keeps the progress made and the coverage gate resumes
    /// from there on the next start.
    /// </summary>
    private const int BackfillTimeoutSeconds = 6 * 60 * 60;

    /// <summary>
    /// The tables converted to hypertables — exactly the shared collector catalog, pinned by
    /// test so scope can never silently widen to the registry/config/analysis tables (see the
    /// class remarks for why those stay plain).
    /// </summary>
    public static IReadOnlyList<ICollectorSchemaInfo> HypertableTables => CollectorCatalog.All;

    /// <summary>
    /// The TRUE number of TimescaleDB hypertables in the store: the collector catalog
    /// (<see cref="HypertableTables"/>) PLUS <c>collection_log</c>, which is a hypertable (converted by the
    /// V23 migration) but lives OUTSIDE the catalog. Worker sizing derives from THIS so it is not under-sized
    /// by one background-worker slot for collection_log's compression policy. The <c>+ 1</c> must move if
    /// another non-catalog table is ever converted (pinned by test).
    /// </summary>
    public static int HypertableCount => HypertableTables.Count + 1;

    /// <summary>
    /// Is the timescaledb extension installed AND created in this database (extensions are
    /// per-database, so pg_extension is the authoritative check)? Callers cache the answer per
    /// data source — the worker detects once at startup and passes the flag around.
    /// </summary>
    public static async Task<bool> DetectAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb')", connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Attempts <c>CREATE EXTENSION IF NOT EXISTS timescaledb</c> and reports whether the
    /// extension is usable. IF NOT EXISTS short-circuits before any privilege check, so a store
    /// whose administrator pre-created the extension works for a service account that could
    /// never create it; a server without the loadable library (or without the privilege to
    /// create it) throws, which degrades gracefully to "not available" — logged once at
    /// Information (plain-PostgreSQL mode is a fully supported configuration, not a problem).
    /// </summary>
    public static async Task<bool> TryEnableAsync(NpgsqlConnection connection, ILogger? logger, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        try
        {
            using var create = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS timescaledb", connection);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogInformation("TimescaleDB not available — running in plain-PostgreSQL mode ({Message})", ex.Message);
            return false;
        }

        /* Belt-and-suspenders: CREATE EXTENSION IF NOT EXISTS succeeding means present, but
           pg_extension stays the single source of truth for "installed AND created". */
        var present = await DetectAsync(connection, cancellationToken);
        if (present)
        {
            logger?.LogInformation("TimescaleDB detected — hypertables, chunk-based retention, and compression enabled");
        }
        else
        {
            logger?.LogInformation("TimescaleDB not available — running in plain-PostgreSQL mode");
        }

        return present;
    }

    /// <summary>
    /// One collector table's hypertable conversion, partitioned on the definition's own prefix
    /// time column. The generalized <c>by_range</c> dimension form, validated live on
    /// TimescaleDB 2.28.1: <c>if_not_exists</c> makes an already-converted table a no-op NOTICE
    /// and <c>migrate_data</c> moves any rows a plain-PG store collected before the extension
    /// arrived. Table and column names come from the shared catalog constants, never from user
    /// input, so interpolation is safe here — the same reasoning as
    /// DarlingRetention.DeleteSqlFor.
    /// </summary>
    public static string CreateHypertableSql(ICollectorSchemaInfo schema)
    {
        if (schema is null)
        {
            throw new ArgumentNullException(nameof(schema));
        }

        return CreateHypertableSql(schema.TargetTable, schema.PrefixTimeColumnName);
    }

    /// <summary>
    /// The raw-name hypertable-conversion overload — the collection_log path (a hypertable since V23 but
    /// outside the collector catalog, so it has no <see cref="ICollectorSchemaInfo"/>). Identical shape to the
    /// schema overload; table/column come from compile-time constants, never user input, so interpolation is
    /// safe (the same reasoning as DarlingRetention.DeleteSqlFor).
    /// </summary>
    public static string CreateHypertableSql(string table, string timeColumn)
        => $"SELECT create_hypertable('{table}', by_range('{timeColumn}', INTERVAL '{ChunkIntervalDays} days'), if_not_exists => true, migrate_data => true)";

    /// <summary>
    /// One collector table's compression enablement, segmented by server_id so each server's
    /// rows compress together (every query filters server_id first — the retrieval indexes lead
    /// with it). The order-by defaults to the partition time column descending, which is exactly
    /// the read order. NOTE for the live validator: this is the long-stable pre-2.18 compression
    /// vocabulary (<c>timescaledb.compress</c> / <c>compress_segmentby</c>); TimescaleDB 2.18+
    /// rebranded it "columnstore" (<c>timescaledb.enable_columnstore</c> / <c>segmentby</c>) but
    /// keeps these as supported aliases — preferred here for compatibility across 2.x.
    /// </summary>
    public static string EnableCompressionSql(ICollectorSchemaInfo schema)
    {
        if (schema is null)
        {
            throw new ArgumentNullException(nameof(schema));
        }

        return EnableCompressionSql(schema.TargetTable);
    }

    /// <summary>The raw-name compression-enable overload — the collection_log path (see
    /// <see cref="CreateHypertableSql(string, string)"/>).</summary>
    public static string EnableCompressionSql(string table)
        => $"ALTER TABLE {table} SET (timescaledb.compress, timescaledb.compress_segmentby = 'server_id')";

    /// <summary>
    /// One collector table's background compression policy — chunks older than
    /// <see cref="CompressAfterDays"/> compress automatically; <c>if_not_exists</c> makes the
    /// re-apply on every service start a no-op. Same 2.18+ naming note as
    /// <see cref="EnableCompressionSql"/> (<c>add_compression_policy</c> is the long-stable
    /// alias of the newer <c>add_columnstore_policy</c>).
    /// </summary>
    public static string AddCompressionPolicySql(ICollectorSchemaInfo schema)
    {
        if (schema is null)
        {
            throw new ArgumentNullException(nameof(schema));
        }

        return AddCompressionPolicySql(schema.TargetTable);
    }

    /// <summary>The raw-name compression-policy overload — the collection_log path (see
    /// <see cref="CreateHypertableSql(string, string)"/>).</summary>
    public static string AddCompressionPolicySql(string table)
        => $"SELECT add_compression_policy('{table}', compress_after => INTERVAL '{CompressAfterDays} days', if_not_exists => true)";

    /* ─────────────────────────── continuous aggregates (query acceleration) ─────────────────────────── */

    /// <summary>The hourly continuous-aggregate view names — query-acceleration rollups for the two tables that
    /// dominate the store (query_stats ~145 GB, procedure_stats ~49 GB, ~90% together). Every Custom Views
    /// composer panel over these tables does date_trunc('hour', collection_time) + SUM(delta_*) GROUP BY a
    /// dimension; these pre-materialize exactly that shape so anything older than the ~2-day hot window reads the
    /// rollup instead of scanning raw per-sweep rows. NOT retention (raw still exists for the hot window; dropping
    /// old raw chunks is a separate, unmade decision).</summary>
    /* -- Baseline tier (#1757) ---------------------------------------------------------------------
       The anomaly baseline asks for BaselineWindowDays (30) of history bucketed by hour-of-day x
       day-of-week; tiered retention shrank raw to 4 days. That is a CORRECTNESS regression, not a speed
       one: seven day-of-week buckets cannot be filled from four days, so on every tiered store the
       thresholds still compute, just on a fraction of the intended history and with no error at all.

       These aggregates are the baseline's own supply. THE HOURLY BUCKET IS PURELY A PARTITIONING AND
       RETENTION KEY; collection_time CARRIES THE GRAIN -- which is why every one of them groups by
       time_bucket AND collection_time. Do not "simplify" the double GROUP BY away: collapsing to the
       hourly bucket changes the unit of observation from one collection snapshot to one hour, which is a
       different statistic at a different scale, and STDDEV_SAMP cannot be reconstructed from hourly sums
       at all (the hourly tier stores no sum-of-squares). Preserving collection_time is exactly what makes
       the provider's AVG / STDDEV_SAMP / restart-exclusion LAG numerically identical to the raw path.

       Each aggregate materializes its families' per-collection collapse with their row-level filters
       INSIDE. Baking is safe because every baseline filter is a literal constant or an immutable sanity
       bound -- the provider takes no settings dependency at all, so nothing here can freeze a configurable
       behavior. The restart-exclusion prior_* predicates deliberately stay in the provider: they apply
       AFTER the collapse, over the collapsed series.

       file_io is the one family whose unit is NOT the collection: IoLatency averages a per-FILE ratio
       across file rows, so a per-collection total would be a different statistic. It stores that ratio's
       SUFFICIENT STATISTICS instead (sum, sum of squares, count), from which AVG and STDDEV_SAMP
       reconstruct exactly. */

    /// <summary>Baseline-tier retention horizon. MUST stay at or above <c>BaselineMath.BaselineWindowDays</c>
    /// (30) or #1757 silently returns; Darling.Tests pins that relation, because Storage cannot reference
    /// Analysis. 35 days gives the window five days of headroom so a drop can never eat its edge.</summary>
    public const string BaselineRetentionInterval = "35 days";

    /// <summary><see cref="TimeSpan"/> twin of <see cref="BaselineRetentionInterval"/>, pinned equal by test.</summary>
    public static readonly TimeSpan BaselineRetentionSpan = TimeSpan.FromDays(35);

    public const string CpuBaselineView = "cpu_utilization_baseline";
    public const string PerfmonBaselineView = "perfmon_baseline";
    public const string WaitStatsBaselineView = "wait_stats_baseline";
    public const string SessionStatsBaselineView = "session_stats_baseline";
    public const string QueryStatsBaselineView = "query_stats_baseline";
    public const string FileIoBaselineView = "file_io_baseline";
    public const string BlockedProcessBaselineView = "blocked_process_baseline";
    public const string DeadlockBaselineView = "deadlock_baseline";
    public const string MemoryBaselineView = "memory_baseline";

    /// <summary>Cpu baseline supply. NOT one row per collection, despite how a point-in-time metric reads:
    /// CpuUtilizationCollector issues SELECT TOP (60) over RING_BUFFER_SCHEDULER_MONITOR and appends every
    /// row, so the first collection after a start or a gap writes up to 60 samples under a SINGLE
    /// collection_time. Averaging those to one value per collection would change the statistic (mean of
    /// per-collection means, not mean of samples) and silently redefine sample_count from samples to
    /// collections. Stores the sufficient statistics instead, exactly as file_io does.</summary>
    public const string CreateCpuBaselineSql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.cpu_utilization_baseline
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT
    server_id,
    time_bucket('1 hour', collection_time) AS bucket,
    collection_time,
    sum(sqlserver_cpu_utilization) AS cpu_sum,
    sum(power(sqlserver_cpu_utilization, 2)) AS cpu_sumsq,
    count(sqlserver_cpu_utilization) AS cpu_count
FROM collect.cpu_utilization_stats
GROUP BY server_id, bucket, collection_time
WITH NO DATA";

    /// <summary>BatchRequests baseline supply -- the counter_name and non-negative filters bake in. Unlike
    /// cpu, one row per collection here is a property of the DMV (Batch Requests/sec is a single instance)
    /// rather than something the collector guarantees -- it applies no object_name/instance_name predicate.
    /// sum() over a one-row group is that row, so this stays exact either way.</summary>
    public const string CreatePerfmonBaselineSql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.perfmon_baseline
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT
    server_id,
    time_bucket('1 hour', collection_time) AS bucket,
    collection_time,
    sum(delta_cntr_value) AS delta_cntr_value
FROM collect.perfmon_stats
WHERE counter_name = 'Batch Requests/sec'
AND   delta_cntr_value >= 0
GROUP BY server_id, bucket, collection_time
WITH NO DATA";

    /// <summary>WaitStats AND WaitMsPerSec baseline supply. Both families share this source and share the
    /// identical row-level filter (delta_wait_time_ms >= 0), which is what lets one aggregate serve both;
    /// Darling.Tests pins that sharing so a future family-specific filter cannot silently poison its
    /// sibling's supply. WaitMsPerSec's interval_sec comes from LAG(collection_time) over the COLLAPSED
    /// series, so the provider computes it and nothing extra is stored here.</summary>
    public const string CreateWaitStatsBaselineSql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.wait_stats_baseline
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT
    server_id,
    time_bucket('1 hour', collection_time) AS bucket,
    collection_time,
    sum(delta_wait_time_ms) AS total_wait_ms
FROM collect.wait_stats
WHERE delta_wait_time_ms >= 0
GROUP BY server_id, bucket, collection_time
WITH NO DATA";

    /// <summary>SessionCount baseline supply -- total connections per collection.</summary>
    public const string CreateSessionStatsBaselineSql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.session_stats_baseline
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT
    server_id,
    time_bucket('1 hour', collection_time) AS bucket,
    collection_time,
    sum(connection_count) AS total_connections
FROM collect.session_stats
GROUP BY server_id, bucket, collection_time
WITH NO DATA";

    /// <summary>QueryDuration baseline supply -- the family that reported #1757, and the expensive collapse:
    /// millions of per-query rows become one row per collection_time.</summary>
    public const string CreateQueryStatsBaselineSql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.query_stats_baseline
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT
    server_id,
    time_bucket('1 hour', collection_time) AS bucket,
    collection_time,
    sum(delta_elapsed_time) AS total_elapsed
FROM collect.query_stats
WHERE delta_execution_count > 0
AND   delta_elapsed_time >= 0
GROUP BY server_id, bucket, collection_time
WITH NO DATA";

    /// <summary>IoLatency baseline supply. THE ONE FAMILY WHOSE UNIT IS NOT THE COLLECTION: it averages a
    /// per-FILE stall/reads ratio across file rows, so a per-collection total would be a different statistic.
    /// Stores that ratio's sufficient statistics instead, from which the provider reconstructs AVG and
    /// STDDEV_SAMP exactly (var_samp = (sumsq - sum*sum/n) / (n-1)). row_count is kept SEPARATELY from
    /// ratio_count because the raw path's sample_count is COUNT(*), which counts rows whose ratio is NULL --
    /// a file with writes but no reads passes the filter and contributes to the count but not the average.</summary>
    public const string CreateFileIoBaselineSql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.file_io_baseline
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT
    server_id,
    time_bucket('1 hour', collection_time) AS bucket,
    collection_time,
    sum(delta_stall_read_ms::DOUBLE PRECISION / NULLIF(delta_reads, 0)) AS ratio_sum,
    sum(power(delta_stall_read_ms::DOUBLE PRECISION / NULLIF(delta_reads, 0), 2)) AS ratio_sumsq,
    count(delta_stall_read_ms::DOUBLE PRECISION / NULLIF(delta_reads, 0)) AS ratio_count,
    count(*) AS row_count
FROM collect.file_io_stats
WHERE delta_reads > 0 OR delta_writes > 0
GROUP BY server_id, bucket, collection_time
WITH NO DATA";

    /// <summary>Blocking AND BlockingPerMinute baseline supply -- both families share this source and both
    /// have NO row-level filter, so one aggregate serves both. Event counts per collection re-aggregate to
    /// either shape: Blocking sums them per hour/dow bucket, BlockingPerMinute re-buckets by minute.</summary>
    public const string CreateBlockedProcessBaselineSql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.blocked_process_baseline
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT
    server_id,
    time_bucket('1 hour', collection_time) AS bucket,
    collection_time,
    count(*) AS event_count
FROM collect.blocked_process_reports
GROUP BY server_id, bucket, collection_time
WITH NO DATA";

    /// <summary>Deadlock baseline supply -- event counts per collection, same shape as blocking.</summary>
    public const string CreateDeadlockBaselineSql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.deadlock_baseline
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT
    server_id,
    time_bucket('1 hour', collection_time) AS bucket,
    collection_time,
    count(*) AS event_count
FROM collect.deadlocks
GROUP BY server_id, bucket, collection_time
WITH NO DATA";

    /// <summary>Memory baseline supply -- the pressure ratio, server-level so one row per collection. The
    /// target > 0 filter bakes in, and the ratio is computed here so the provider averages the same numbers
    /// the raw path averaged.</summary>
    public const string CreateMemoryBaselineSql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.memory_baseline
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT
    server_id,
    time_bucket('1 hour', collection_time) AS bucket,
    collection_time,
    avg(total_server_memory_mb::DOUBLE PRECISION / NULLIF(target_server_memory_mb::DOUBLE PRECISION, 0) * 100) AS memory_pressure_pct
FROM collect.memory_stats
WHERE target_server_memory_mb > 0
GROUP BY server_id, bucket, collection_time
WITH NO DATA";

    /// <summary>
    /// One-time backfill of the baseline aggregates (#1757). WITHOUT THIS THE WHOLE CHANGE IS A REGRESSION:
    /// the aggregates are created WITH NO DATA and their refresh policy has a 3-day start_offset, so left
    /// alone they would hold roughly three days for a thirty-day question -- less than the four-day raw
    /// horizon the change exists to escape. The provider is repointed at them, so an un-backfilled deploy
    /// loses every baseline rather than improving it.
    ///
    /// <para>COVERAGE-GATED and self-healing, the shape the reshape sweep already uses: it compares the
    /// oldest bucket the aggregate holds against the oldest row raw still has and refreshes only the gap,
    /// so it converges to a no-op on every subsequent start rather than re-running a full refresh forever.
    /// A fresh store has nothing to backfill and skips immediately.</para>
    ///
    /// <para>Runs OUTSIDE a transaction on purpose -- refresh_continuous_aggregate cannot run inside one --
    /// and is failure-isolated per aggregate: a backfill that fails leaves that family reading a short
    /// window (logged loudly) rather than taking down startup. MUST run after
    /// <see cref="EnsureContinuousAggregatesAsync"/>.</para>
    /// </summary>
    public static async Task<int> BackfillBaselineAggregatesAsync(
        NpgsqlConnection connection, ILogger? logger, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        var backfilled = 0;

        foreach (var (_, view) in BaselineAggregates)
        {
            try
            {
                var source = SourceTableFor(view);

                var probeSql = BaselineBackfillProbeSql(view, source);

                DateTime? sourceOldest = null;
                DateTime? coverageOldest = null;
                DateTime? needFrom = null;
                using (var probe = new NpgsqlCommand(probeSql, connection) { CommandTimeout = SetupTimeoutSeconds })
                await using (var reader = await probe.ExecuteReaderAsync(cancellationToken))
                {
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        sourceOldest = reader.IsDBNull(0) ? null : reader.GetDateTime(0);
                        coverageOldest = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
                        needFrom = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
                    }
                }

                /* Nothing in the source: a fresh store has no history to materialize. */
                if (sourceOldest is null || needFrom is null)
                {
                    continue;
                }

                /* Already reaches at least as far back as we need. This is what makes the pass converge to a
                   no-op instead of re-refreshing on every start. */
                if (coverageOldest is not null && coverageOldest <= needFrom)
                {
                    continue;
                }

                var after = await RefreshFromAsync(connection, view, needFrom.Value, force: false, cancellationToken);

                /* VERIFY, DO NOT ASSUME -- the failure this guards is SILENT. On a CAGG that has just been
                   created the plain refresh is enough: creation writes an infinite [-infinity, +infinity]
                   invalidation ("initially, everything is invalid") and WITH NO DATA does not skip it, so the
                   whole pre-existing history is materialized on the first pass. What the plain refresh cannot
                   be trusted to repair is the SECOND pass: a refresh CONSUMES invalidations as it goes, so an
                   earlier backfill cut short by a shutdown can leave a region un-materialized whose
                   invalidation entries are already gone, and a later plain refresh then no-ops over the hole
                   and reports success. The forced form ignores the invalidation log and batches every bucket
                   in range, which is what actually repairs that. Escalate only on evidence: it is strictly
                   more work, and `force` only exists from TimescaleDB 2.18 (an older bring-your-own store
                   raises 42883 here, which the per-aggregate catch reports rather than crashing the pass). */
                if (after is null || after > needFrom)
                {
                    logger?.LogInformation(
                        "TimescaleDB: {View} still starts at {After} after a plain refresh from {NeedFrom}; escalating to a forced refresh.",
                        view, after, needFrom);
                    after = await RefreshFromAsync(connection, view, needFrom.Value, force: true, cancellationToken);
                }

                backfilled++;
                logger?.LogInformation(
                    "TimescaleDB: backfilled baseline aggregate {View} from {NeedFrom} (now starts at {After}) -- it covered from {CoverageOldest} but {Source} reaches back to {SourceOldest}.",
                    view, needFrom, after, coverageOldest, source, sourceOldest);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                /* Worse than it sounds, which is why it logs loudly: with real-time aggregation the watermark
                   is a HARD PARTITION (materialized WHERE time < watermark UNION ALL raw WHERE time >=
                   watermark), so an un-materialized region older than the watermark returns nothing from
                   EITHER branch -- raw is excluded by construction. A failed backfill is not "reads raw
                   instead", it is a hole. Still isolated per aggregate: one failure must not take down the
                   pass, and the coverage gate retries it on the next start. */
                logger?.LogWarning(
                    "Baseline aggregate {View} backfill FAILED -- its baselines are computed from a short window until this succeeds, and history older than its watermark reads as absent rather than falling back to raw: {Message}",
                    view, ex.Message);
            }
        }

        return backfilled;
    }

    /// <summary>
    /// The backfill's refresh statement. The bounds are BOUND parameters and explicitly cast, not
    /// interpolated: <c>window_start</c>/<c>window_end</c> are declared <c>"any"</c>, so an untyped literal
    /// leaves PostgreSQL without a type to resolve the polymorphic argument against.
    ///
    /// <para><paramref name="force"/> maps to the 4th argument of the 2.28.1 signature
    /// <c>refresh_continuous_aggregate(cagg REGCLASS, window_start "any", window_end "any", force BOOLEAN =
    /// FALSE, options JSONB = NULL)</c>. NOTE that the tunables the published API page documents as named
    /// arguments (<c>buckets_per_batch</c>, <c>refresh_newest_first</c>) are NOT parameters of this
    /// procedure — they live inside <c>options</c>, and only the POLICY takes them by name. The defaults are
    /// what we want anyway: a manual refresh already batches internally, so this needs no hand-rolled
    /// slicing.</para>
    /// </summary>
    public static string RefreshContinuousAggregateSql(string view, bool force = false)
        => force
            ? $"CALL refresh_continuous_aggregate('collect.{view}'::regclass, $1::timestamp, NULL::timestamp, true)"
            : $"CALL refresh_continuous_aggregate('collect.{view}'::regclass, $1::timestamp, NULL::timestamp)";

    /// <summary>
    /// Refreshes <paramref name="view"/> from <paramref name="from"/> forward and returns the aggregate's
    /// oldest bucket AFTERWARDS, so the caller can check the refresh actually materialized the range instead
    /// of trusting that a successful CALL means a filled aggregate.
    /// </summary>
    private static async Task<DateTime?> RefreshFromAsync(
        NpgsqlConnection connection, string view, DateTime from, bool force, CancellationToken cancellationToken)
    {
        using (var refresh = new NpgsqlCommand(RefreshContinuousAggregateSql(view, force), connection)
        {
            CommandTimeout = BackfillTimeoutSeconds,
        })
        {
            refresh.Parameters.AddWithValue(from);
            await refresh.ExecuteNonQueryAsync(cancellationToken);
        }

        using var probe = new NpgsqlCommand($"SELECT min(bucket) FROM collect.{view}", connection)
        {
            CommandTimeout = SetupTimeoutSeconds,
        };
        return await probe.ExecuteScalarAsync(cancellationToken) is DateTime oldest ? oldest : null;
    }

    /// <summary>
    /// The marker that separates a baseline aggregate's CREATE header from its SELECT body. Both the
    /// continuous aggregate and the plain-PostgreSQL fallback view are built from that ONE body, so the two
    /// cannot drift into computing different statistics — which is the whole reason the fallback is derived
    /// rather than written out a second time.
    /// </summary>
    private const string BaselineBodyMarker = "timescaledb.materialized_only = false) AS";

    /// <summary>
    /// The plain-PostgreSQL fallback for a baseline aggregate: the SAME select, as an ordinary view.
    ///
    /// <para>WITHOUT THIS, #1757's fix is a REGRESSION on any store without TimescaleDB. The provider reads
    /// the baseline relations by name; if they do not exist it throws, <c>ComputeBaselinesAsync</c> swallows
    /// it and logs "Failed to compute baselines for {metric}" — the exact line #1757 was reported on — and
    /// every family silently returns an empty baseline. Darling supports plain-PostgreSQL stores (the worker
    /// degrades to that mode whenever the TimescaleDB block fails), so this is a real deployment, not a
    /// theoretical one.</para>
    ///
    /// <para><c>time_bucket</c> is the one TimescaleDB-only construct in the body, and for a 1-hour bucket
    /// <c>date_trunc('hour', ...)</c> is the same value, so the view presents an identical column set. Nothing
    /// reads <c>bucket</c> off these relations outside the TimescaleDB-only backfill and retention paths, but
    /// it is kept so the two shapes stay column-for-column identical.</para>
    /// </summary>
    public static string CreateBaselineFallbackViewSql(string view, string createSql)
    {
        if (createSql is null)
        {
            throw new ArgumentNullException(nameof(createSql));
        }

        var bodyAt = createSql.IndexOf(BaselineBodyMarker, StringComparison.Ordinal);
        var endAt = createSql.LastIndexOf("WITH NO DATA", StringComparison.Ordinal);
        if (bodyAt < 0 || endAt < 0 || endAt <= bodyAt)
        {
            throw new ArgumentException(
                $"'{view}' does not have the expected baseline-aggregate shape, so its plain-PostgreSQL fallback cannot be derived",
                nameof(createSql));
        }

        var body = createSql[(bodyAt + BaselineBodyMarker.Length)..endAt]
            .Replace("time_bucket('1 hour', collection_time)", "date_trunc('hour', collection_time)", StringComparison.Ordinal)
            .Trim();

        return $"CREATE OR REPLACE VIEW collect.{view} AS{Environment.NewLine}{body}";
    }

    /// <summary>
    /// Guarantees every baseline relation EXISTS, filling any gap with an ordinary view over the same select.
    /// Returns how many gaps it filled.
    ///
    /// <para>PER VIEW AND DELIBERATELY UNGATED, because "no TimescaleDB" is not the only way a relation goes
    /// missing. <see cref="EnsureContinuousAggregatesAsync"/> is failure-isolated per aggregate, so a store
    /// with the extension can end up with eight aggregates and one gap — and the provider reads these
    /// relations by name, so that one gap is one family silently returning nothing. Gating this on
    /// "TimescaleDB unavailable" would cover the plain-PostgreSQL store and leave the partially-built one
    /// broken, which is the harder case to notice.</para>
    ///
    /// <para>The existence probe is what makes it safe to run everywhere: a continuous aggregate is itself a
    /// <c>relkind='v'</c> view, so an unconditional <c>CREATE OR REPLACE VIEW</c> by these names would destroy
    /// a materialization. Anything already present — aggregate or view — is left strictly alone. MUST run
    /// AFTER <see cref="EnsureContinuousAggregatesAsync"/> so a real aggregate always wins the name.</para>
    /// </summary>
    public static async Task<int> EnsureBaselineFallbackViewsAsync(
        NpgsqlConnection connection, ILogger? logger, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        var filled = 0;

        foreach (var (createSql, view) in BaselineAggregates)
        {
            try
            {
                using (var probe = new NpgsqlCommand(BaselineRelationExistsSql(view), connection) { CommandTimeout = SetupTimeoutSeconds })
                {
                    if (await probe.ExecuteScalarAsync(cancellationToken) is true)
                    {
                        continue;
                    }
                }

                using var create = new NpgsqlCommand(CreateBaselineFallbackViewSql(view, createSql), connection)
                {
                    CommandTimeout = SetupTimeoutSeconds,
                };
                await create.ExecuteNonQueryAsync(cancellationToken);
                filled++;

                logger?.LogInformation(
                    "Baseline relation {View} had no continuous aggregate — created it as a plain view so its anomaly baseline still computes (reading raw directly).",
                    view);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(
                    "Baseline relation {View} is MISSING and could not be backed by a plain view — that metric's anomaly baseline will silently return nothing: {Message}",
                    view, ex.Message);
            }
        }

        return filled;
    }

    /// <summary>Does this baseline relation exist under any implementation — continuous aggregate or plain view?</summary>
    public static string BaselineRelationExistsSql(string view)
        => $"SELECT to_regclass('collect.{view}') IS NOT NULL";

    /// <summary>
    /// The backfill's coverage gate: how far back the source reaches, how far back the aggregate covers, and
    /// how far back we NEED it to cover. The caller backfills when the source has rows and coverage is either
    /// empty or starts later than <c>need_from</c>.
    ///
    /// <para>THE CLAMP DIRECTION IS THE WHOLE THING, and it is easy to get backwards in a way that reads fine.
    /// <c>GREATEST</c> clamps the NEED — "go back as far as the source reaches, but no further than this
    /// tier's own retention" — because materializing past the tier's retention only hands its retention policy
    /// something to drop. Clamping the COVERAGE side instead (<c>LEAST</c> over the coverage and the window)
    /// inverts the gate: an empty aggregate collapses to <c>now - window</c>, the comparison becomes
    /// "now - window &lt;= source oldest", and on every store whose raw retention is SHORTER than the window
    /// it is unconditionally true — so the gate skips exactly the tiered stores it exists for, and fires only
    /// on stores that do not need it. BaselineSupplyTests pins this direction.</para>
    ///
    /// <para>This is the same predicate shape the retention arming uses (<c>IsSafeToArmRetentionAsync</c>) over
    /// the same pair of relations, deliberately: what we backfill and what unblocks arming cannot be allowed
    /// to drift apart.</para>
    /// </summary>
    public static string BaselineBackfillProbeSql(string view, string source)
        => $@"
SELECT
    (SELECT min(collection_time) FROM collect.{source}) AS source_oldest,
    (SELECT min(bucket) FROM collect.{view}) AS coverage_oldest,
    time_bucket('1 hour', GREATEST(
        (SELECT min(collection_time) FROM collect.{source}),
        now()::timestamp - INTERVAL '{BaselineRetentionInterval}')) AS need_from";

    /// <summary>
    /// Drops a baseline relation ONLY when it is a plain fallback view and NOT a continuous aggregate — the
    /// store gained TimescaleDB after running without it, and the fallback now stands in the way of the real
    /// aggregate. The `continuous_aggregates` half of the guard is what makes this safe: a CAGG is also a
    /// <c>relkind='v'</c> view, so an unguarded DROP VIEW here would silently destroy a materialized tier.
    /// </summary>
    public static string DropBaselineFallbackViewSql(string view)
        => $@"DO $do$
DECLARE
    is_continuous_aggregate boolean := false;
BEGIN
    IF NOT EXISTS (
            SELECT 1
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'collect' AND c.relname = '{view}' AND c.relkind = 'v')
    THEN
        RETURN;
    END IF;

    /* timescaledb_information only exists once the extension has been created, and this runs on stores where
       it never was -- reaching it unconditionally raises 42P01. Probed with to_regclass (NULL rather than an
       error when absent) and read through EXECUTE so the reference is parsed only when it resolves. No
       extension means nothing can be a continuous aggregate, so the guard is simply false. */
    IF to_regclass('timescaledb_information.continuous_aggregates') IS NOT NULL
    THEN
        EXECUTE 'SELECT EXISTS (SELECT 1 FROM timescaledb_information.continuous_aggregates WHERE view_schema = ''collect'' AND view_name = ''{view}'')'
        INTO is_continuous_aggregate;
    END IF;

    IF NOT is_continuous_aggregate
    THEN
        EXECUTE 'DROP VIEW collect.{view}';
    END IF;
END
$do$";

    /// <summary>The raw table each baseline aggregate is sourced from, for the backfill's coverage probe.</summary>
    public static string SourceTableFor(string view) => view switch
    {
        CpuBaselineView => "cpu_utilization_stats",
        PerfmonBaselineView => "perfmon_stats",
        WaitStatsBaselineView => "wait_stats",
        SessionStatsBaselineView => "session_stats",
        QueryStatsBaselineView => "query_stats",
        FileIoBaselineView => "file_io_stats",
        BlockedProcessBaselineView => "blocked_process_reports",
        DeadlockBaselineView => "deadlocks",
        MemoryBaselineView => "memory_stats",
        _ => throw new ArgumentOutOfRangeException(nameof(view), view, "not a baseline aggregate"),
    };

    /// <summary>The nine baseline-tier aggregates in creation order. Named ONCE so the ensure sweep, the
    /// retention list and the tests read one list rather than three hand-kept copies.</summary>
    public static readonly (string CreateSql, string View)[] BaselineAggregates =
    {
        (CreateCpuBaselineSql,            CpuBaselineView),
        (CreatePerfmonBaselineSql,        PerfmonBaselineView),
        (CreateWaitStatsBaselineSql,      WaitStatsBaselineView),
        (CreateSessionStatsBaselineSql,   SessionStatsBaselineView),
        (CreateQueryStatsBaselineSql,     QueryStatsBaselineView),
        (CreateFileIoBaselineSql,         FileIoBaselineView),
        (CreateBlockedProcessBaselineSql, BlockedProcessBaselineView),
        (CreateDeadlockBaselineSql,       DeadlockBaselineView),
        (CreateMemoryBaselineSql,         MemoryBaselineView),
    };

    public const string QueryStatsHourlyView = "query_stats_hourly";

    /// <summary><see cref="QueryStatsHourlyView"/>'s procedure_stats sibling.</summary>
    public const string ProcedureStatsHourlyView = "procedure_stats_hourly";

    /// <summary>The query_store_stats hourly continuous aggregate. Built now, ahead of any writable-Query-Store
    /// primary — on a read-only replica QS surfaces nothing new to harvest, so this sits empty until one is added,
    /// but the rollup path exists the moment data starts flowing. Weaker cardinality reduction than the delta
    /// tables (QS's own top-N sampling already surfaces a broad, shifting query/plan set), still worth having.</summary>
    public const string QueryStoreStatsHourlyView = "query_store_stats_hourly";

    /// <summary>The DAILY tier: hierarchical continuous aggregates sourced from the hourly CAGGs, NOT raw — 2.28.1
    /// supports a continuous aggregate built directly on another. Kept indefinitely (no retention policy) as the
    /// "coarsened but never fully lost" tier for anything past the hourly CAGG's own horizon.</summary>
    public const string QueryStatsDailyView = "query_stats_daily";

    /// <summary>The per-database query_stats rollup carrying the I/O sums FinOps needs (#1661).</summary>
    public const string QueryStatsDbHourlyView = "query_stats_db_hourly";

    /// <summary>The daily sibling of <see cref="QueryStatsDbHourlyView"/> — kept indefinitely.</summary>
    public const string QueryStatsDbDailyView = "query_stats_db_daily";

    /// <summary><see cref="QueryStatsDailyView"/>'s procedure_stats sibling (sourced from procedure_stats_hourly).</summary>
    public const string ProcedureStatsDailyView = "procedure_stats_daily";

    /// <summary>The Query Store DAILY continuous aggregate — hierarchical from <see cref="QueryStoreStatsHourlyView"/>,
    /// same composer dims (module_name / query_hash) + weighted sums. Kept indefinitely; a QS window past the
    /// hourly's 21d horizon routes here.</summary>
    public const string QueryStoreStatsDailyView = "query_store_stats_daily";

    /// <summary>
    /// The query_stats hourly continuous aggregate. 1-hour buckets grouped by the SAME dimensions the composer's
    /// <c>MeasureCatalog</c> uses for query_stats (server_id / server_name / database_name / query_hash), so a
    /// panel can point here with no dimension remapping. SUM/MIN/MAX on each per-interval DELTA column (NOT a
    /// pre-divided average — avg composes at query time as sum/execution_count_sum, which re-aggregates
    /// correctly; a materialized average would not) plus a <c>sample_count</c>. Summing the deltas is
    /// double-count-safe: they are Darling's own per-interval deltas, not raw cumulative DMV counters. Created
    /// WITH NO DATA — a full historical refresh over 145 GB is heavy I/O, a deliberate off-hours operator op
    /// (<c>--backfill-rollups</c>), NEVER startup work. IF NOT EXISTS so a restart re-converges. A SINGLE
    /// statement: a CAGG CREATE cannot run inside a transaction, so it must never be batched with the policy call.
    ///
    /// <para><b>MATERIALIZED-ONLY, and that is deliberate (#1759).</b> This used to claim the opposite — that
    /// real-time aggregation was opted into and the view was "correct to query for any window immediately, just
    /// un-accelerated". Both halves were false. TimescaleDB 2.13+ defaults <c>materialized_only</c> to TRUE, so
    /// naming no option means real-time aggregation is OFF; and even ON it would not help, because the watermark
    /// is a hard partition — <c>build_union_query</c> emits materialized-below <c>UNION ALL</c> raw-at-or-above,
    /// with no contiguity guarantee — so history below the watermark that was never materialized is served by
    /// NEITHER branch. That premise cost this product the #1759 defect: every window older than the rollup's
    /// materialized floor read EMPTY while raw still held the rows.</para>
    ///
    /// <para>Do NOT "fix" that by adding <c>materialized_only = false</c>. It cannot surface un-materialized
    /// history (see above), and it would break the two things that now depend on materialized-only semantics:
    /// <see cref="RollupCoverageProbeSql"/> and <see cref="RetentionArmSafetySql"/> both read
    /// <c>min(bucket)</c> to mean "the oldest bucket this rollup has MATERIALIZED". Union in the raw branch and
    /// an EMPTY materialization would report raw's own oldest row as the rollup's floor — coverage would look
    /// complete when it is not, and the arming gate would arm a purge over history nothing else holds.
    /// TimescaleContinuousAggregateTests pins the absence of the option for exactly this reason.</para>
    /// </summary>
    public const string CreateQueryStatsHourlySql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.query_stats_hourly
WITH (timescaledb.continuous) AS
SELECT
    server_id,
    server_name,
    database_name,
    query_hash,
    sql_handle,
    time_bucket('1 hour', collection_time) AS bucket,
    sum(delta_worker_time) AS worker_time_sum,
    min(delta_worker_time) AS worker_time_min,
    max(delta_worker_time) AS worker_time_max,
    sum(delta_elapsed_time) AS elapsed_time_sum,
    min(delta_elapsed_time) AS elapsed_time_min,
    max(delta_elapsed_time) AS elapsed_time_max,
    sum(delta_execution_count) AS execution_count_sum,
    min(delta_execution_count) AS execution_count_min,
    max(delta_execution_count) AS execution_count_max,
    count(*) AS sample_count
FROM collect.query_stats
GROUP BY server_id, server_name, database_name, query_hash, sql_handle, bucket
WITH NO DATA";

    /// <summary>The procedure_stats hourly continuous aggregate — <see cref="CreateQueryStatsHourlySql"/>'s
    /// sibling, grouped by <c>schema_name</c> + <c>object_name</c> (procedure_stats' composer dimensions; a panel
    /// grouping by schema_name alone re-aggregates over its objects). Same aggregation shape, same WITH NO DATA +
    /// IF NOT EXISTS discipline.</summary>
    public const string CreateProcedureStatsHourlySql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.procedure_stats_hourly
WITH (timescaledb.continuous) AS
SELECT
    server_id,
    server_name,
    database_name,
    schema_name,
    object_name,
    time_bucket('1 hour', collection_time) AS bucket,
    sum(delta_worker_time) AS worker_time_sum,
    min(delta_worker_time) AS worker_time_min,
    max(delta_worker_time) AS worker_time_max,
    sum(delta_elapsed_time) AS elapsed_time_sum,
    min(delta_elapsed_time) AS elapsed_time_min,
    max(delta_elapsed_time) AS elapsed_time_max,
    sum(delta_execution_count) AS execution_count_sum,
    min(delta_execution_count) AS execution_count_min,
    max(delta_execution_count) AS execution_count_max,
    count(*) AS sample_count
FROM collect.procedure_stats
GROUP BY server_id, server_name, database_name, schema_name, object_name, bucket
WITH NO DATA";

    /// <summary>
    /// The query_store_stats hourly continuous aggregate, grouped by the COMPOSER's Query Store dimensions
    /// (server / database_name / module_name / query_hash) so a composed QS panel can route here — NOT Query
    /// Store's own query_id/plan_id, which the composer never exposes. Carries the EXECUTION-WEIGHTED sums
    /// (<c>sum(avg_* * execution_count)</c>) so the composer's weighted mean composes EXACTLY as
    /// <c>duration_us_weighted_sum / execution_count_sum</c> across any window (avg*count = the interval's total,
    /// summed = the true total) — never an avg-of-avgs. This matters the moment a writable-Query-Store primary is
    /// added (the scenario this CAGG exists to be ready for); on the current read-only replica it is simply empty.
    /// WITH NO DATA + IF NOT EXISTS, one statement.
    /// </summary>
    public const string CreateQueryStoreStatsHourlySql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.query_store_stats_hourly
WITH (timescaledb.continuous) AS
SELECT
    server_id,
    server_name,
    database_name,
    module_name,
    query_hash,
    time_bucket('1 hour', collection_time) AS bucket,
    sum(execution_count) AS execution_count_sum,
    sum(avg_duration_us::double precision * execution_count) AS duration_us_weighted_sum,
    sum(avg_cpu_time_us::double precision * execution_count) AS cpu_us_weighted_sum,
    max(max_duration_us) AS max_duration_us_max,
    max(max_cpu_time_us) AS max_cpu_time_us_max,
    count(*) AS sample_count
FROM collect.query_store_stats
GROUP BY server_id, server_name, database_name, module_name, query_hash, bucket
WITH NO DATA";

    /// <summary>
    /// The per-DATABASE query_stats rollup (#1661). Added rather than folded into
    /// <see cref="CreateQueryStatsHourlySql"/> deliberately: TimescaleDB cannot ALTER columns into a continuous
    /// aggregate, so widening that one would mean DROP + recreate, and now that retention is active the rebuild
    /// would re-materialize from 4 days of raw and permanently destroy the 21-day hourly and indefinite daily
    /// history the tiers exist to preserve. A NEW aggregate costs nothing existing; its history simply starts
    /// accumulating from deploy.
    ///
    /// <para>Carries the I/O sums no other rollup has — FinOps' database-grain workload view sums
    /// <c>delta_logical_reads</c> / <c>delta_physical_reads</c> / <c>delta_logical_writes</c>, and the composer's
    /// measure set (which the other CAGGs were built to) never exposed I/O. Grouped by database_name only, NOT
    /// query_hash, so it is far smaller than the query-grain aggregate despite carrying more columns.</para>
    /// </summary>
    public const string CreateQueryStatsDbHourlySql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.query_stats_db_hourly
WITH (timescaledb.continuous) AS
SELECT
    server_id,
    server_name,
    database_name,
    time_bucket('1 hour', collection_time) AS bucket,
    sum(delta_worker_time) AS worker_time_sum,
    sum(delta_logical_reads) AS logical_reads_sum,
    sum(delta_physical_reads) AS physical_reads_sum,
    sum(delta_logical_writes) AS logical_writes_sum,
    sum(delta_execution_count) AS execution_count_sum,
    max(last_execution_time) AS last_execution_time_max,
    count(*) AS sample_count
FROM collect.query_stats
WHERE delta_worker_time IS NOT NULL
GROUP BY server_id, server_name, database_name, bucket
WITH NO DATA";

    /// <summary>The DAILY sibling of <see cref="CreateQueryStatsDbHourlySql"/> — hierarchical (sourced from the
    /// hourly one, not raw), kept indefinitely like the other daily rollups.</summary>
    public const string CreateQueryStatsDbDailySql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.query_stats_db_daily
WITH (timescaledb.continuous) AS
SELECT
    server_id,
    server_name,
    database_name,
    time_bucket('1 day', bucket) AS bucket,
    sum(worker_time_sum) AS worker_time_sum,
    sum(logical_reads_sum) AS logical_reads_sum,
    sum(physical_reads_sum) AS physical_reads_sum,
    sum(logical_writes_sum) AS logical_writes_sum,
    sum(execution_count_sum) AS execution_count_sum,
    max(last_execution_time_max) AS last_execution_time_max,
    sum(sample_count) AS sample_count
FROM collect.query_stats_db_hourly
GROUP BY server_id, server_name, database_name, time_bucket('1 day', bucket)
WITH NO DATA";

    /// <summary>
    /// The query_stats DAILY continuous aggregate — a HIERARCHICAL CAGG sourced from <see cref="QueryStatsHourlyView"/>
    /// (not raw). Re-aggregates the hourly rollup to 1-day buckets: SUM of the hourly sums, MIN of the hourly mins,
    /// MAX of the hourly maxes (each composes correctly across the coarser bucket), plus SUM of the hourly
    /// sample_counts. The GROUP BY uses the explicit <c>time_bucket('1 day', bucket)</c> expression, NOT the bare
    /// <c>bucket</c> alias: an unqualified <c>bucket</c> in GROUP BY binds to the SOURCE column (the hourly bucket)
    /// under Postgres's input-column-wins ambiguity rule, which would group by hour, not day. WITH NO DATA +
    /// IF NOT EXISTS; the hourly CAGG must already exist (it is created earlier in the same sweep).
    /// </summary>
    public const string CreateQueryStatsDailySql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.query_stats_daily
WITH (timescaledb.continuous) AS
SELECT
    server_id,
    server_name,
    database_name,
    query_hash,
    sql_handle,
    time_bucket('1 day', bucket) AS bucket,
    sum(worker_time_sum) AS worker_time_sum,
    min(worker_time_min) AS worker_time_min,
    max(worker_time_max) AS worker_time_max,
    sum(elapsed_time_sum) AS elapsed_time_sum,
    min(elapsed_time_min) AS elapsed_time_min,
    max(elapsed_time_max) AS elapsed_time_max,
    sum(execution_count_sum) AS execution_count_sum,
    min(execution_count_min) AS execution_count_min,
    max(execution_count_max) AS execution_count_max,
    sum(sample_count) AS sample_count
FROM collect.query_stats_hourly
GROUP BY server_id, server_name, database_name, query_hash, sql_handle, time_bucket('1 day', bucket)
WITH NO DATA";

    /// <summary>The procedure_stats DAILY continuous aggregate — <see cref="CreateQueryStatsDailySql"/>'s sibling,
    /// sourced from <see cref="ProcedureStatsHourlyView"/> and grouped by <c>schema_name</c> + <c>object_name</c>.
    /// Same hierarchical re-aggregation and same explicit-<c>time_bucket</c> GROUP BY discipline.</summary>
    public const string CreateProcedureStatsDailySql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.procedure_stats_daily
WITH (timescaledb.continuous) AS
SELECT
    server_id,
    server_name,
    database_name,
    schema_name,
    object_name,
    time_bucket('1 day', bucket) AS bucket,
    sum(worker_time_sum) AS worker_time_sum,
    min(worker_time_min) AS worker_time_min,
    max(worker_time_max) AS worker_time_max,
    sum(elapsed_time_sum) AS elapsed_time_sum,
    min(elapsed_time_min) AS elapsed_time_min,
    max(elapsed_time_max) AS elapsed_time_max,
    sum(execution_count_sum) AS execution_count_sum,
    min(execution_count_min) AS execution_count_min,
    max(execution_count_max) AS execution_count_max,
    sum(sample_count) AS sample_count
FROM collect.procedure_stats_hourly
GROUP BY server_id, server_name, database_name, schema_name, object_name, time_bucket('1 day', bucket)
WITH NO DATA";

    /// <summary>The Query Store DAILY continuous aggregate — <see cref="CreateQueryStatsDailySql"/>'s Query Store
    /// sibling, hierarchical from <see cref="QueryStoreStatsHourlyView"/> and grouped by the composer's QS dims
    /// (module_name / query_hash). SUM re-aggregates the hourly weighted sums (so the weighted mean composes as
    /// duration_us_weighted_sum / execution_count_sum across days) and MAX the peaks. Same column NAMES as the
    /// hourly, so <c>ComposeCaggValueMapper</c> reads both with no change. Explicit-<c>time_bucket</c> GROUP BY.</summary>
    public const string CreateQueryStoreStatsDailySql = @"CREATE MATERIALIZED VIEW IF NOT EXISTS collect.query_store_stats_daily
WITH (timescaledb.continuous) AS
SELECT
    server_id,
    server_name,
    database_name,
    module_name,
    query_hash,
    time_bucket('1 day', bucket) AS bucket,
    sum(execution_count_sum) AS execution_count_sum,
    sum(duration_us_weighted_sum) AS duration_us_weighted_sum,
    sum(cpu_us_weighted_sum) AS cpu_us_weighted_sum,
    max(max_duration_us_max) AS max_duration_us_max,
    max(max_cpu_time_us_max) AS max_cpu_time_us_max,
    sum(sample_count) AS sample_count
FROM collect.query_store_stats_hourly
GROUP BY server_id, server_name, database_name, module_name, query_hash, time_bucket('1 day', bucket)
WITH NO DATA";

    /// <summary>
    /// The refresh policy for a continuous aggregate: materialize <c>[now - 3 days, now - endOffset]</c> every
    /// <c>scheduleInterval</c>. <c>start_offset 3 days</c> gives margin past the ~2-day compression/hot window
    /// (covers same-day-arriving corrections) and is the buffer the retention tiers lean on — a tier's drop must
    /// never outrun the next tier's 3-day refresh start. <c>endOffset</c> leaves the still-filling current bucket
    /// unmaterialized (no repeated rework); <c>scheduleInterval</c> matches the bucket. Defaults are the hourly
    /// shape; the daily CAGGs pass <c>"1 day"</c>/<c>"1 day"</c>. <c>if_not_exists</c> so a restart re-converges.
    /// </summary>
    public static string AddContinuousAggregatePolicySql(string view, string endOffset = "1 hour", string scheduleInterval = "1 hour")
        => $"SELECT add_continuous_aggregate_policy('collect.{view}', start_offset => INTERVAL '3 days', end_offset => INTERVAL '{endOffset}', schedule_interval => INTERVAL '{scheduleInterval}', if_not_exists => true)";

    /// <summary>
    /// The composer-dimension reshape: the QS hourly CAGG regrouped query_id/plan_id → module_name/query_hash
    /// (+ weighted sums), and the procedure_stats CAGGs gained schema_name. <c>CREATE ... IF NOT EXISTS</c> cannot
    /// ALTER an existing CAGG, so a store that already built the OLD shape must DROP it first;
    /// <see cref="EnsureContinuousAggregatesAsync"/> (run right after) recreates it in the new shape. Each affected
    /// CAGG is empty (QS on a read-only replica) or only a day or two old, so the drop loses little and the refresh
    /// backfills the recent window within the hour. Staleness is detected STRUCTURALLY — the OLD QS CAGG still has
    /// a <c>query_id</c> column; the OLD procedure_stats CAGG lacks <c>schema_name</c> — so this is a strict no-op
    /// once reshaped, and on a fresh store (no CAGG yet) nothing matches. Failure-isolated: a failed drop leaves the
    /// old shape in place (logged), never kills startup. query_stats CAGGs are unchanged and untouched. CASCADE
    /// drops the dependent daily CAGG, which the ensure sweep also recreates.
    /// </summary>
    public static async Task<int> DropStaleContinuousAggregatesAsync(NpgsqlConnection connection, ILogger? logger, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        var reshapes = new[]
        {
            /* OLD query_store_stats_hourly grouped by query_id/plan_id → stale iff it still has a query_id column. */
            (View: "query_store_stats_hourly",
             StaleCheck: "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'collect' AND table_name = 'query_store_stats_hourly' AND column_name = 'query_id')"),
            /* OLD procedure_stats_hourly lacked schema_name → stale iff the view EXISTS but has no schema_name
               column. CASCADE also drops procedure_stats_daily, which the ensure sweep recreates. */
            (View: "procedure_stats_hourly",
             StaleCheck: "SELECT (EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'collect' AND table_name = 'procedure_stats_hourly') AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'collect' AND table_name = 'procedure_stats_hourly' AND column_name = 'schema_name'))"),
            /* query_stats_hourly / _daily gained sql_handle (object_name routing) → stale iff the view EXISTS but
               has no sql_handle column. CASCADE drops query_stats_daily, which the ensure sweep recreates. */
            (View: "query_stats_hourly",
             StaleCheck: "SELECT (EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'collect' AND table_name = 'query_stats_hourly') AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'collect' AND table_name = 'query_stats_hourly' AND column_name = 'sql_handle'))"),
        };

        var dropped = 0;
        foreach (var (view, staleCheck) in reshapes)
        {
            try
            {
                bool stale;
                using (var check = new NpgsqlCommand(staleCheck, connection) { CommandTimeout = SetupTimeoutSeconds })
                {
                    stale = await check.ExecuteScalarAsync(cancellationToken) is true;
                }

                if (!stale)
                {
                    continue;
                }

                using (var drop = new NpgsqlCommand($"DROP MATERIALIZED VIEW IF EXISTS collect.{view} CASCADE", connection) { CommandTimeout = SetupTimeoutSeconds })
                {
                    await drop.ExecuteNonQueryAsync(cancellationToken);
                }

                dropped++;
                logger?.LogInformation(
                    "TimescaleDB: dropped stale continuous aggregate {View} (composer-dimension reshape) — recreated in the new shape this cycle.",
                    view);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(
                    "Reshape drop of {View} failed — it stays in the OLD shape until the next restart retries: {Message}",
                    view, ex.Message);
            }
        }

        return dropped;
    }

    /// <summary>
    /// Creates the continuous aggregates and attaches each one's refresh policy
    /// (<see cref="AddContinuousAggregatePolicySql"/>): three HOURLY (query_stats, procedure_stats,
    /// query_store_stats) then two DAILY (query_stats, procedure_stats). The daily tier is HIERARCHICAL — each
    /// daily CAGG is sourced from its hourly CAGG, so the ordered sweep creates the hourly ones first. Runs in the
    /// worker's TimescaleDB block (CAGGs need the extension), AFTER hypertables + compression are in place. The
    /// CREATE and the policy are SEPARATE commands
    /// per aggregate — a CAGG CREATE cannot run inside a transaction, so it is never batched with another
    /// statement. Failure-isolated per aggregate: one failure warns and the composer keeps querying raw.
    /// Idempotent (IF NOT EXISTS on both), so it re-converges every restart. Returns the number ready.
    ///
    /// <para>Does NOT backfill history, and on a store that already holds history that leaves a real gap rather
    /// than a merely un-accelerated one (#1759): the aggregates are born WITH NO DATA and each refresh policy
    /// starts 3 days back, so the materialized span begins at roughly creation-minus-3-days and never reaches
    /// further back on its own. Reads stay CORRECT because <see cref="RetentionTierRouter"/> routes windows
    /// below a rollup's measured floor to raw; the materialization itself is an operator op
    /// (<c>--backfill-rollups</c>), which is where the disk cost is preflighted rather than incurred at
    /// startup.</para>
    /// </summary>
    public static async Task<int> EnsureContinuousAggregatesAsync(NpgsqlConnection connection, ILogger? logger, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        // Hourly CAGGs FIRST (the two delta tables + query_store_stats), THEN the daily tier — the daily CAGGs are
        // hierarchical (sourced from the hourly CAGGs), so the hourly ones must be created earlier in this ordered
        // sweep. Daily policies use the 1-day end-offset/schedule; the hourly ones take the helper's defaults.
        var aggregates = new[]
        {
            (CreateSql: CreateQueryStatsHourlySql,      View: QueryStatsHourlyView,      PolicySql: AddContinuousAggregatePolicySql(QueryStatsHourlyView)),
            (CreateSql: CreateProcedureStatsHourlySql,  View: ProcedureStatsHourlyView,  PolicySql: AddContinuousAggregatePolicySql(ProcedureStatsHourlyView)),
            (CreateSql: CreateQueryStoreStatsHourlySql, View: QueryStoreStatsHourlyView, PolicySql: AddContinuousAggregatePolicySql(QueryStoreStatsHourlyView)),
            (CreateSql: CreateQueryStatsDbHourlySql,    View: QueryStatsDbHourlyView,    PolicySql: AddContinuousAggregatePolicySql(QueryStatsDbHourlyView)),
            (CreateSql: CreateQueryStatsDailySql,       View: QueryStatsDailyView,       PolicySql: AddContinuousAggregatePolicySql(QueryStatsDailyView, "1 day", "1 day")),
            (CreateSql: CreateProcedureStatsDailySql,   View: ProcedureStatsDailyView,   PolicySql: AddContinuousAggregatePolicySql(ProcedureStatsDailyView, "1 day", "1 day")),
            (CreateSql: CreateQueryStoreStatsDailySql,  View: QueryStoreStatsDailyView,  PolicySql: AddContinuousAggregatePolicySql(QueryStoreStatsDailyView, "1 day", "1 day")),
            (CreateSql: CreateQueryStatsDbDailySql,     View: QueryStatsDbDailyView,     PolicySql: AddContinuousAggregatePolicySql(QueryStatsDbDailyView, "1 day", "1 day")),
        }
        /* The nine baseline-tier aggregates (#1757) take the helper's hourly defaults: they are sourced from
           raw like the hourly tier, not hierarchically from another CAGG, so they carry no ordering
           requirement against the daily tier. Appended from the single BaselineAggregates list so this sweep
           and the retention list cannot drift apart. */
        .Concat(BaselineAggregates.Select(a => (CreateSql: a.CreateSql, View: a.View, PolicySql: AddContinuousAggregatePolicySql(a.View))))
        .ToArray();

        /* A store that ran WITHOUT TimescaleDB and has now gained it is carrying the plain fallback views
           under the exact names the baseline aggregates need (#1757). CREATE MATERIALIZED VIEW IF NOT EXISTS
           would quietly do nothing against them, leaving the store permanently on raw scans, so the stale
           fallback is dropped first. Guarded to never touch a real continuous aggregate, and isolated per
           view — this is a transition path, and failing it must not stop the sweep. */
        foreach (var (_, view) in BaselineAggregates)
        {
            try
            {
                using var drop = new NpgsqlCommand(DropBaselineFallbackViewSql(view), connection) { CommandTimeout = SetupTimeoutSeconds };
                await drop.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(
                    "Could not drop the plain-PostgreSQL fallback view {View} — its continuous aggregate cannot be created while it stands: {Message}",
                    view, ex.Message);
            }
        }

        var ready = 0;
        foreach (var (createSql, view, policySql) in aggregates)
        {
            try
            {
                using (var create = new NpgsqlCommand(createSql, connection) { CommandTimeout = SetupTimeoutSeconds })
                {
                    await create.ExecuteNonQueryAsync(cancellationToken);
                }

                using (var policy = new NpgsqlCommand(policySql, connection) { CommandTimeout = SetupTimeoutSeconds })
                {
                    await policy.ExecuteNonQueryAsync(cancellationToken);
                }

                ready++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(
                    "Continuous aggregate {View} setup failed — composer queries fall back to raw scans: {Message}",
                    view, ex.Message);
            }
        }

        /* The names are DERIVED from the array above, never restated (#1746). The previous line spelled out
           "3 hourly ... 3 daily" by hand, so when #1664 added the db-grain pair the counts said 8 and the
           text still said 6 — and an operator mid-upgrade spent real time reconciling the two before
           concluding it was not a bug. A summary that quotes its own source cannot drift from it. */
        logger?.LogInformation(
            "TimescaleDB: {Ready}/{Total} continuous aggregate(s) ready ({Views})",
            ready, aggregates.Length, string.Join(", ", aggregates.Select(a => a.View)));
        return ready;
    }

    /// <summary>Raw-tier retention horizon: keep per-sweep raw ~4 days — one day past the hourly CAGG's own 3-day
    /// refresh window, so the raw drop never outruns the aggregate that preserves it.</summary>
    public const string RawRetentionInterval = "4 days";

    /// <summary>Hourly-CAGG-tier retention horizon: keep the hourly rollups 21 days — well past the daily CAGG's
    /// 3-day refresh window, so the hourly drop never outruns the daily aggregate. The daily CAGGs themselves get
    /// NO retention policy: they are the coarsened, kept-indefinitely tier.</summary>
    public const string HourlyRetentionInterval = "21 days";

    /// <summary><see cref="TimeSpan"/> twin of <see cref="RawRetentionInterval"/> for callers doing arithmetic
    /// (the #1665 partial-window notice); RetentionTierRouterTests pins the two equal so they can't drift.</summary>
    public static readonly TimeSpan RawRetentionSpan = TimeSpan.FromDays(4);

    /// <summary><see cref="TimeSpan"/> twin of <see cref="HourlyRetentionInterval"/>; pinned equal by
    /// RetentionTierRouterTests.</summary>
    public static readonly TimeSpan HourlyRetentionSpan = TimeSpan.FromDays(21);

    /// <summary>
    /// A TimescaleDB retention policy: schedule a background job that DROPs chunks older than
    /// <paramref name="dropAfter"/>. <c>if_not_exists</c> so a restart re-converges. The actual drop is a
    /// chunk-level DROP TABLE (cheap, no rewrite), so unlike the CAGG backfill it needs no off-hours window.
    ///
    /// <para><b>There is deliberately no <c>scheduled</c> argument here (#1705).</b> <c>add_retention_policy</c>
    /// has NEVER accepted one on any TimescaleDB 2.x — the parameter exists only on <c>add_job</c> /
    /// <c>alter_job</c>. Passing it made this statement fail with <c>42883 function ... does not exist</c> on
    /// EVERY store, fresh or upgraded, and the per-policy catch in
    /// <see cref="EnsureRetentionPoliciesAsync"/> turned that into a warning — so retention silently stopped
    /// existing everywhere rather than only on old versions. The paused-at-creation guarantee #1680 needs is
    /// preserved by the CALLER instead: it creates and pauses inside ONE transaction (see
    /// <see cref="PauseJobSql"/>). Verified against 2.28.1: the accepted signature is
    /// <c>(regclass, "any", boolean, interval, timestamptz, text, interval)</c>.</para>
    ///
    /// <para>Returns the new policy's <c>job_id</c>, or <c>-1</c> when <c>if_not_exists</c> matched an existing
    /// policy and skipped — the caller MUST NOT feed that -1 to <c>alter_job</c>.</para>
    /// </summary>
    public static string AddRetentionPolicySql(string relation, string dropAfter)
        => $"SELECT add_retention_policy('collect.{relation}', drop_after => INTERVAL '{dropAfter}', if_not_exists => true)";

    /// <summary>
    /// Pauses a just-created job by id. Run in the SAME transaction as
    /// <see cref="AddRetentionPolicySql"/>: the TimescaleDB job scheduler is a separate backend, so it cannot see
    /// the <c>bgw_job</c> row until that transaction commits, and by then the row already reads
    /// <c>scheduled = false</c>. That closes the #1680 window without needing a parameter the API does not have.
    /// <c>job_id</c> is <c>integer</c>, not bigint (the #1586 cast trap). $1 the job id.
    /// </summary>
    public const string PauseJobSql = "SELECT alter_job($1::integer, scheduled => false)";

    /// <summary>
    /// Arms a retention policy that was created paused. Separated from creation because TimescaleDB runs a new
    /// policy's first check IMMEDIATELY at creation, not on its next interval (#1680).
    /// </summary>
    public static string ArmRetentionPolicySql(string relation)
        => $@"SELECT alter_job(j.job_id, scheduled => true)
FROM timescaledb_information.jobs AS j
WHERE j.proc_name = 'policy_retention'
AND   j.hypertable_schema = 'collect'
AND   j.hypertable_name = '{relation}'";

    /// <summary>
    /// Is it safe to arm <paramref name="relation"/>'s retention policy — i.e. does the tier below it already
    /// cover everything this relation holds? Returns true when the source is empty (nothing to lose), or when the
    /// coverage relation's oldest bucket is at or before the source's oldest row.
    ///
    /// <para>This is the check that makes arming provably non-destructive rather than a race the operator has to
    /// win. It also self-heals: a store that is not yet covered stays paused and arms on the first start AFTER a
    /// backfill, with no manual step.</para>
    /// </summary>
    public static string RetentionArmSafetySql(string relation, string sourceTimeColumn, string coverageRelation)
        => $@"SELECT
    (SELECT min({sourceTimeColumn}) FROM collect.{relation}) AS source_oldest,
    (SELECT min(bucket) FROM collect.{coverageRelation}) AS coverage_oldest";

    /// <summary>
    /// True when arming <paramref name="relation"/>'s retention policy cannot drop anything the tier below has
    /// not already captured (#1680): the source is empty, or <paramref name="coverageRelation"/> reaches at least
    /// as far back as the source does. Any failure to determine this returns FALSE - an unknown coverage state
    /// must leave the policy paused, never armed on an assumption.
    /// </summary>
    private static async Task<bool> IsSafeToArmRetentionAsync(
        NpgsqlConnection connection, string relation, string sourceTimeColumn, string coverageRelation, CancellationToken cancellationToken)
    {
        try
        {
            using var command = new NpgsqlCommand(RetentionArmSafetySql(relation, sourceTimeColumn, coverageRelation), connection) { CommandTimeout = SetupTimeoutSeconds };
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return false;
            }

            /* Nothing in the source - a fresh store. No history to lose, so arm. */
            if (await reader.IsDBNullAsync(0, cancellationToken))
            {
                return true;
            }

            /* Source has data but the coverage tier is empty - arming would drop history nothing else holds. */
            if (await reader.IsDBNullAsync(1, cancellationToken))
            {
                return false;
            }

            return reader.GetDateTime(1) <= reader.GetDateTime(0);
        }
        catch (Exception)
        {
            /* Fail closed: if coverage cannot be established, the policy stays paused. */
            return false;
        }
    }

    /// <summary>
    /// Attaches the tiered retention policies: the three raw tables drop at <see cref="RawRetentionInterval"/>, the
    /// three hourly CAGGs at <see cref="HourlyRetentionInterval"/>; the daily CAGGs are kept indefinitely (no
    /// policy). Ordering safety is by HORIZON, not run order — each tier's drop stays comfortably past the next
    /// tier's 3-day refresh start_offset (4d raw vs 3d hourly refresh; 21d hourly vs 3d daily refresh), so a drop
    /// never removes history the next tier has not yet materialized. Idempotent (<c>if_not_exists</c>) and
    /// failure-isolated per policy. MUST run AFTER <see cref="EnsureContinuousAggregatesAsync"/> so the hourly
    /// CAGGs the hourly policies target already exist. Returns the number of policies in place.
    ///
    /// COLD START ON AN EXISTING STORE (#1759): a store that already holds raw history older than its hourly
    /// CAGG has materialized does NOT lose it. <see cref="IsSafeToArmRetentionAsync"/> is fail-closed, so that
    /// store's raw policies are created and left PAUSED, and the per-policy WARN says which rollup is short and
    /// by how much. This used to be documented as a caveat prescribing a manual backfill "BEFORE this policy's
    /// first run" — a step no store ever received, and a defect rather than a caveat. The backfill is now a real
    /// operator verb (<c>--backfill-rollups</c>) with a disk preflight, and once it carries a rollup past the raw
    /// horizon this gate arms the held policy by itself on the next start, with no manual step.
    /// </summary>
    public static async Task<int> EnsureRetentionPoliciesAsync(NpgsqlConnection connection, ILogger? logger, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        /* Each policy names the tier that must already cover it before arming is safe (#1680). The rule this
           list enforces is: NEVER DROP WHAT YOUR CONSUMER HAS NOT CAPTURED YET.

           For the raw and hourly tiers the consumer is the next aggregate down the ladder -- raw tables are
           covered by their hourly CAGG, hourly CAGGs by their daily one -- so "coverage" names that tier.

           THE LEAF RULE (#1757): a tier with nothing below it is not exempt, it just has a different consumer.
           The baseline aggregates are leaves; their consumer is the baseline COMPUTATION, whose capture
           requirement is BaselineMath.BaselineWindowDays (30). Their arming condition is therefore "the tier
           holds at least the baseline window of buckets" -- the same rule with the consumer named honestly,
           still runtime-evaluable like the other seven rather than a degenerate always-open gate. It is
           belt-and-braces by construction: BaselineRetentionSpan (35d) already exceeds the window, so even an
           immediately-armed policy could not eat it, and Darling.Tests pins that static relation. A policy
           with no identifiable consumer at all still does not belong in this list. */
        var policies = new[]
        {
            (Relation: "query_stats",             DropAfter: RawRetentionInterval,    TimeColumn: "collection_time", Coverage: QueryStatsHourlyView),
            (Relation: "procedure_stats",         DropAfter: RawRetentionInterval,    TimeColumn: "collection_time", Coverage: ProcedureStatsHourlyView),
            (Relation: "query_store_stats",       DropAfter: RawRetentionInterval,    TimeColumn: "collection_time", Coverage: QueryStoreStatsHourlyView),
            (Relation: QueryStatsHourlyView,      DropAfter: HourlyRetentionInterval, TimeColumn: "bucket",          Coverage: QueryStatsDailyView),
            (Relation: ProcedureStatsHourlyView,  DropAfter: HourlyRetentionInterval, TimeColumn: "bucket",          Coverage: ProcedureStatsDailyView),
            (Relation: QueryStoreStatsHourlyView, DropAfter: HourlyRetentionInterval, TimeColumn: "bucket",          Coverage: QueryStoreStatsDailyView),
            (Relation: QueryStatsDbHourlyView,    DropAfter: HourlyRetentionInterval, TimeColumn: "bucket",          Coverage: QueryStatsDbDailyView),
        }
        /* The nine baseline-tier policies (#1757). Coverage is the tier ITSELF: see the leaf rule in the
           comment above -- their consumer is the baseline computation, whose capture requirement is the
           30-day window, and BaselineRetentionSpan (35d) exceeds it by construction. */
        .Concat(BaselineAggregates.Select(a =>
            (Relation: a.View, DropAfter: BaselineRetentionInterval, TimeColumn: "bucket", Coverage: a.View)))
        .ToArray();

        var applied = 0;
        var armed = 0;
        var held = 0;

        foreach (var (relation, dropAfter, timeColumn, coverage) in policies)
        {
            try
            {
                /* Created PAUSED, always. TimescaleDB runs a new policy's first check immediately at creation
                   rather than on its next interval, so a policy created live drops before any external session
                   can pause it - there is no window to win. That cost a field store two days of history.

                   The pause happens in the SAME transaction as the create (#1705). add_retention_policy has no
                   scheduled argument on any 2.x, so the only way to never expose an armed job is to keep the
                   bgw_job row invisible until it already reads scheduled = false: the scheduler is a separate
                   backend and cannot see an uncommitted row. Verified on 2.28.1 against a hypertable holding
                   30-day-old rows under a 4-day policy - the rows survived, so no immediate drop occurred. */
                await using (var tx = await connection.BeginTransactionAsync(cancellationToken))
                {
                    int jobId;
                    using (var create = new NpgsqlCommand(AddRetentionPolicySql(relation, dropAfter), connection, tx) { CommandTimeout = SetupTimeoutSeconds })
                    {
                        jobId = Convert.ToInt32(await create.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                    }

                    /* -1 means if_not_exists matched an existing policy and skipped. There is no new job to
                       pause, and the existing one keeps whatever armed/paused state it already had - which is
                       what makes a restart converge instead of re-pausing a policy this store already armed. */
                    if (jobId > 0)
                    {
                        using var pause = new NpgsqlCommand(PauseJobSql, connection, tx) { CommandTimeout = SetupTimeoutSeconds };
                        pause.Parameters.AddWithValue(jobId);
                        await pause.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await tx.CommitAsync(cancellationToken);
                }

                applied++;

                if (await IsSafeToArmRetentionAsync(connection, relation, timeColumn, coverage, cancellationToken))
                {
                    using var arm = new NpgsqlCommand(ArmRetentionPolicySql(relation), connection) { CommandTimeout = SetupTimeoutSeconds };
                    await arm.ExecuteNonQueryAsync(cancellationToken);
                    armed++;
                }
                else
                {
                    held++;
                    logger?.LogWarning(
                        "Retention policy for {Relation} created but HELD PAUSED - {Coverage} does not yet cover everything it holds, so arming could drop history no rollup has. Backfill that rollup past the {DropAfter} horizon and the policy arms itself on the next start.",
                        relation, coverage, dropAfter);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(
                    "Retention policy for {Relation} ({DropAfter}) failed - that tier keeps growing until the next restart retries: {Message}",
                    relation, dropAfter, ex.Message);
            }
        }

        logger?.LogInformation(
            "TimescaleDB: {Applied}/{Total} retention policies in place, {Armed} armed, {Held} held paused pending backfill (raw {Raw}, hourly CAGGs {Hourly}; daily CAGGs kept indefinitely)",
            applied, policies.Length, armed, held, RawRetentionInterval, HourlyRetentionInterval);
        return applied;
    }

    /* ─────────────── rollup availability (the plain-PostgreSQL guard, #1664) ─────────────── */

    /// <summary>
    /// One catalog round trip answering "which retention rollups exist in THIS store?" — the availability
    /// input to <see cref="RetentionTierRouter.Resolve(DateTime, DateTime, bool, bool)"/>. <c>to_regclass</c>
    /// needs no table privilege and returns NULL for a missing relation, so this is safe under the viewer's
    /// least-privilege role and on any store shape. Column order matches
    /// <see cref="RollupAvailability"/>'s constructor.
    /// </summary>
    public static readonly string RollupProbeSql =
        "SELECT " +
        $"to_regclass('collect.{QueryStatsHourlyView}') IS NOT NULL, " +
        $"to_regclass('collect.{QueryStatsDailyView}') IS NOT NULL, " +
        $"to_regclass('collect.{QueryStatsDbHourlyView}') IS NOT NULL, " +
        $"to_regclass('collect.{QueryStatsDbDailyView}') IS NOT NULL, " +
        $"to_regclass('collect.{ProcedureStatsHourlyView}') IS NOT NULL, " +
        $"to_regclass('collect.{ProcedureStatsDailyView}') IS NOT NULL, " +
        $"to_regclass('collect.{QueryStoreStatsHourlyView}') IS NOT NULL, " +
        $"to_regclass('collect.{QueryStoreStatsDailyView}') IS NOT NULL";

    /// <summary>
    /// Detects which continuous-aggregate rollups exist in the store (<see cref="RollupProbeSql"/>). On a
    /// plain-PostgreSQL store every flag is false — and that is a COMPLETE configuration, not a degraded one:
    /// without the extension no retention policy ever drops raw, so the raw tables hold full history and
    /// routing everything to raw loses nothing. On a TimescaleDB store the worker's ensure sweep creates the
    /// views before any reader can need them; a partially-built store (one aggregate's failure-isolated
    /// setup failed) reports exactly what exists, so the router degrades per tier instead of a reader
    /// throwing 42P01 at a user (#1664, the gated-live catch on #1661's first cut).
    /// </summary>
    public static async Task<RollupAvailability> DetectRollupsAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        if (dataSource is null)
        {
            throw new ArgumentNullException(nameof(dataSource));
        }

        await using var command = dataSource.CreateCommand(RollupProbeSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new RollupAvailability(
            reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3),
            reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7));
    }

    /* ─────────────── rollup COVERAGE (the un-materialized-history guard, #1759) ─────────────── */

    /// <summary>Every rollup view in probe order, paired with the RAW table its tier ladder falls back to.
    /// One list so the coverage probe's column order, <see cref="RollupCoverage"/>'s constructor and
    /// <see cref="RollupCoverage.RawTableFor"/> cannot drift into disagreeing.</summary>
    public static readonly (string View, string RawTable)[] RollupViews =
    {
        (QueryStatsHourlyView, "query_stats"),
        (QueryStatsDailyView, "query_stats"),
        (QueryStatsDbHourlyView, "query_stats"),
        (QueryStatsDbDailyView, "query_stats"),
        (ProcedureStatsHourlyView, "procedure_stats"),
        (ProcedureStatsDailyView, "procedure_stats"),
        (QueryStoreStatsHourlyView, "query_store_stats"),
        (QueryStoreStatsDailyView, "query_store_stats"),
    };

    /// <summary>The three raw tables the rollups roll up, in coverage-probe order (deduplicated
    /// <see cref="RollupViews"/>).</summary>
    public static readonly string[] RolledRawTables = { "query_stats", "procedure_stats", "query_store_stats" };

    /// <summary>
    /// How far back each rollup has actually MATERIALIZED, and how far back each raw table still reaches —
    /// the input <see cref="RetentionTierRouter"/> needs to stop routing a window at a rollup that cannot
    /// answer it (#1759).
    ///
    /// <para>The mechanism this exists for: a continuous aggregate created <c>WITH NO DATA</c> over
    /// pre-existing history serves ONLY what was materialized. Real-time aggregation cannot rescue it —
    /// the watermark is a hard partition (materialized below <c>UNION ALL</c> raw at-or-above), so raw
    /// older than the watermark is excluded by construction, not merely un-accelerated. Every rollup's
    /// refresh policy starts 3 days back, so on a store that existed before its rollups the materialized
    /// span begins at roughly creation-minus-3-days and NEVER reaches further back on its own.</para>
    ///
    /// <para><b><c>to_regclass</c>-safe by construction, not by guard.</b> A relation named in a statement
    /// is resolved at PARSE time, so no in-statement <c>to_regclass</c> test can keep <c>min(bucket)</c>
    /// off a view that does not exist. Instead the SQL is BUILT from
    /// <paramref name="availability"/> — a view the <see cref="RollupProbeSql"/> round trip just proved
    /// absent contributes a literal <c>NULL</c> and is never named. Column count is fixed either way, so
    /// the reader's indexing does not depend on the store's shape.</para>
    ///
    /// <para><c>min(bucket)</c> is deliberately the SAME expression <see cref="RetentionArmSafetySql"/>
    /// gates arming on. Routing and arming must agree about what a rollup covers, or the router would
    /// serve a window the arming gate considers uncovered (or worse, the reverse).</para>
    /// </summary>
    public static string RollupCoverageProbeSql(RollupAvailability availability)
    {
        var columns = RollupViews
            .Select(r => availability.Has(r.View)
                ? $"(SELECT min(bucket) FROM collect.{r.View})"
                : "NULL::timestamp")
            /* The raw tables are migration-created and always exist, so they need no availability gate. */
            .Concat(RolledRawTables.Select(t => $"(SELECT min(collection_time) FROM collect.{t})"));

        return "SELECT " + string.Join(", ", columns);
    }

    /// <summary>
    /// Reads every rollup's materialized floor and every rolled raw table's oldest row
    /// (<see cref="RollupCoverageProbeSql"/>). <paramref name="availability"/> comes from
    /// <see cref="DetectRollupsAsync"/> in the same probe cycle and decides which relations are named at
    /// all.
    ///
    /// <para>NOTE that a DAILY rollup's floor is the day-FLOOR of its oldest hourly bucket, so it can read
    /// up to a day earlier than the hourly it is sourced from. That over-claims coverage by at most one
    /// bucket, and it is exactly the semantics the arming gate already runs on — matching it is the point.</para>
    /// </summary>
    public static async Task<RollupCoverage> DetectRollupCoverageAsync(
        NpgsqlDataSource dataSource, RollupAvailability availability, CancellationToken cancellationToken = default)
    {
        if (dataSource is null)
        {
            throw new ArgumentNullException(nameof(dataSource));
        }

        await using var command = dataSource.CreateCommand(RollupCoverageProbeSql(availability));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return RollupCoverage.Unknown;
        }

        var floors = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        for (var i = 0; i < RollupViews.Length; i++)
        {
            if (!await reader.IsDBNullAsync(i, cancellationToken))
            {
                floors[RollupViews[i].View] = reader.GetDateTime(i);
            }
        }

        var rawOldest = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        for (var i = 0; i < RolledRawTables.Length; i++)
        {
            var ordinal = RollupViews.Length + i;
            if (!await reader.IsDBNullAsync(ordinal, cancellationToken))
            {
                rawOldest[RolledRawTables[i]] = reader.GetDateTime(ordinal);
            }
        }

        return new RollupCoverage(floors, rawOldest);
    }

    /// <summary>
    /// Converts every collector table to a hypertable (<see cref="HypertableTables"/> scope;
    /// <see cref="CreateHypertableSql"/> per table). Failure-isolated per table: one failed
    /// conversion warns and the sweep continues — that table stays a plain PG table, keeps
    /// working (COPY and DELETE-based retention are hypertable-agnostic), and is retried on the
    /// next service start. Returns the number of tables that converted (or no-op'd) cleanly.
    /// </summary>
    public static async Task<int> ConvertToHypertablesAsync(NpgsqlConnection connection, ILogger? logger, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        var converted = 0;
        foreach (var schema in HypertableTables)
        {
            try
            {
                using var command = new NpgsqlCommand(CreateHypertableSql(schema), connection) { CommandTimeout = SetupTimeoutSeconds };
                await command.ExecuteNonQueryAsync(cancellationToken);
                converted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning("Hypertable conversion failed for {Table} — it stays a plain table: {Message}",
                    schema.TargetTable, ex.Message);
            }
        }

        logger?.LogInformation("TimescaleDB: {Converted}/{Total} collector table(s) are hypertables",
            converted, HypertableTables.Count);
        return converted;
    }

    /// <summary>
    /// Enables compression and adds the <see cref="CompressAfterDays"/>-day background policy on
    /// every collector table (both statements per table, failure-isolated per table — a table
    /// that failed hypertable conversion warns here too and stays uncompressed). Compressed
    /// chunks remain fully queryable: this is Darling's archival tier (see
    /// <see cref="CompressAfterDays"/>). Returns the number of tables with a policy in place.
    /// </summary>
    public static async Task<int> ApplyCompressionPolicyAsync(NpgsqlConnection connection, ILogger? logger, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        var applied = 0;
        foreach (var schema in HypertableTables)
        {
            try
            {
                using (var enable = new NpgsqlCommand(EnableCompressionSql(schema), connection) { CommandTimeout = SetupTimeoutSeconds })
                {
                    await enable.ExecuteNonQueryAsync(cancellationToken);
                }

                using (var policy = new NpgsqlCommand(AddCompressionPolicySql(schema), connection) { CommandTimeout = SetupTimeoutSeconds })
                {
                    await policy.ExecuteNonQueryAsync(cancellationToken);
                }

                applied++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning("Compression policy failed for {Table} — it stays uncompressed: {Message}",
                    schema.TargetTable, ex.Message);
            }
        }

        logger?.LogInformation("TimescaleDB: compression policy ({Days}d) in place on {Applied}/{Total} collector table(s)",
            CompressAfterDays, applied, HypertableTables.Count);
        return applied;
    }

    /// <summary>The V23 non-catalog hypertable: the per-run observability log. Bare name — the connection's
    /// <c>collect,config,public</c> search path resolves it to <c>collect.collection_log</c>, exactly like the
    /// collector tables' bare TargetTable names.</summary>
    public const string CollectionLogTable = "collection_log";

    /// <summary>collection_log's partition (prefix time) column.</summary>
    public const string CollectionLogTimeColumn = "collection_time";

    /// <summary>
    /// The AUTHORITATIVE conversion + compression of <c>collection_log</c> — a hypertable since V23, but OUTSIDE
    /// the collector catalog, so <see cref="ConvertToHypertablesAsync"/>/<see cref="ApplyCompressionPolicyAsync"/>
    /// (which iterate the catalog) never reach it. Called by the worker in the runtime TimescaleDB block, AFTER
    /// <see cref="TryEnableAsync"/> has created the extension — which is exactly why this, not the V23 migration,
    /// is authoritative: migrations run BEFORE <c>CREATE EXTENSION</c>, so a fresh store's V23 guard skips the
    /// conversion, and this heals it. Same three statements the collector tables get, via the raw-name overloads
    /// (<see cref="CreateHypertableSql(string, string)"/>: <c>migrate_data</c> moves any existing rows into
    /// chunks — the proven non-transactional path, so no migration-transaction risk; compression segments by
    /// <c>server_id</c> at <see cref="CompressAfterDays"/>). Idempotent (<c>if_not_exists</c>), so it re-converges
    /// every restart and no-ops a store the V23 migration already converted. Failure-isolated: a failure warns and
    /// collection_log stays a plain table — its DELETE-based retention (DarlingRetention) still honors the horizon.
    /// The long <see cref="SetupTimeoutSeconds"/> command timeout covers a large first <c>migrate_data</c>.
    /// </summary>
    public static async Task<bool> EnsureCollectionLogHypertableAsync(NpgsqlConnection connection, ILogger? logger, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        try
        {
            using (var convert = new NpgsqlCommand(CreateHypertableSql(CollectionLogTable, CollectionLogTimeColumn), connection) { CommandTimeout = SetupTimeoutSeconds })
            {
                await convert.ExecuteNonQueryAsync(cancellationToken);
            }

            using (var enable = new NpgsqlCommand(EnableCompressionSql(CollectionLogTable), connection) { CommandTimeout = SetupTimeoutSeconds })
            {
                await enable.ExecuteNonQueryAsync(cancellationToken);
            }

            using (var policy = new NpgsqlCommand(AddCompressionPolicySql(CollectionLogTable), connection) { CommandTimeout = SetupTimeoutSeconds })
            {
                await policy.ExecuteNonQueryAsync(cancellationToken);
            }

            logger?.LogInformation("TimescaleDB: collection_log is a hypertable with a {Days}d compression policy", CompressAfterDays);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(
                "collection_log hypertable setup failed — it stays a plain table (DELETE-based retention still honors its horizon): {Message}",
                ex.Message);
            return false;
        }
    }

    /* ---------------- compression-job self-heal (#1581) ---------------- */

    /// <summary>
    /// The parameterized re-arm statement (#1581): reschedule a background job to run immediately, which
    /// un-sticks a job whose <c>next_start</c> has become <c>-infinity</c> (the scheduler will never re-fire
    /// it otherwise — the field-incident root cause). The job_id is ALWAYS bound as <c>$1</c>, never
    /// interpolated (the discipline is uniform with DarlingRetention's parameterized paths); <c>now()</c> is
    /// SQL, not a value. It is cast <c>$1::integer</c> because TimescaleDB's <c>alter_job</c> takes
    /// <c>job_id integer</c>, but <see cref="StuckCompressionJob.JobId"/> is a <c>long</c> that Npgsql sends as
    /// <c>bigint</c>; Postgres does NOT down-cast bigint→integer during function resolution, so an un-cast bind
    /// fails with <c>42883: function alter_job(bigint, ...) does not exist</c> (a real defect the gated-live
    /// test caught — a TimescaleDB job_id never exceeds int4, so the cast is always safe).
    /// </summary>
    public const string RearmJobSql = "SELECT alter_job($1::integer, next_start => now())";

    /* The stuck-Running bound floor: a compression run on a single day-chunk of 1-minute-cadence data
       finishes in seconds-to-minutes, so a run still 'Running' past this floor (when it dominates
       2x the schedule interval) has hung. Kept generous so a genuinely long first-compression of a
       large adopted store is not false-flagged; next_start = -infinity (the dominant failure mode) is
       caught immediately regardless of this. */
    private static readonly TimeSpan s_stuckRunningFloor = TimeSpan.FromHours(2);

    /// <summary>
    /// The stuck-<c>Running</c> bound: <c>max(2x the schedule interval, a floor)</c>. A run legitimately in
    /// progress finishes well within twice its own cadence; crossing this bound means it hung. A missing or
    /// non-positive schedule interval falls back to the floor. Pure so the predicate pins directly.
    /// </summary>
    public static TimeSpan StuckRunningBound(TimeSpan? scheduleInterval)
    {
        if (scheduleInterval is TimeSpan interval && interval > TimeSpan.Zero)
        {
            var twice = interval + interval;
            return twice > s_stuckRunningFloor ? twice : s_stuckRunningFloor;
        }

        return s_stuckRunningFloor;
    }

    /// <summary>
    /// The pure stuck-compression-job decision (#1581). A compression policy job is STUCK when either:
    /// <list type="bullet">
    /// <item>its <c>next_start</c> is <c>-infinity</c> — the scheduler will NEVER re-fire it (the dead-job
    /// bug that let uncompressed data grow without bound until the disk filled), or</item>
    /// <item>it has been in the <c>Running</c> state since a <c>last_run_started_at</c> older than
    /// <see cref="StuckRunningBound"/> — a run that began long ago and never finished (a hung run).</item>
    /// </list>
    /// A job with neither condition is healthy and is NOT flagged. No I/O, so it pins directly with a
    /// controllable clock. Scoping to compression jobs happens in the query — this decides only "stuck".
    /// </summary>
    public static bool IsCompressionJobStuck(
        bool nextStartIsNegativeInfinity,
        string? jobStatus,
        DateTime? lastRunStartedAtUtc,
        TimeSpan? scheduleInterval,
        DateTime nowUtc,
        out string reason)
    {
        if (nextStartIsNegativeInfinity)
        {
            reason = "next_start is -infinity — the scheduler will never run it again";
            return true;
        }

        if (string.Equals(jobStatus, "Running", StringComparison.OrdinalIgnoreCase)
            && lastRunStartedAtUtc is DateTime startedUtc)
        {
            var bound = StuckRunningBound(scheduleInterval);
            var elapsed = nowUtc - startedUtc;
            if (elapsed > bound)
            {
                reason = string.Format(
                    CultureInfo.InvariantCulture,
                    "stuck in the Running state for {0:F0} minutes (over the {1:F0}-minute bound) — the run hung and never finished",
                    elapsed.TotalMinutes, bound.TotalMinutes);
                return true;
            }
        }

        reason = "";
        return false;
    }

    /// <summary>
    /// Reads every COMPRESSION-policy background job (<c>proc_name</c> is <c>policy_compression</c>, or the
    /// 2.18+ columnstore rebrand's name — the same tolerant LIKE the compression test uses) and returns the
    /// ones the pure <see cref="IsCompressionJobStuck"/> predicate flags as stuck. The <c>-infinity</c> test
    /// runs IN SQL (so it never depends on Npgsql's infinity-to-DateTime conversion setting); the
    /// stuck-Running bound is computed in C# from the raw fields. Scoped to compression jobs ONLY — retention,
    /// continuous-aggregate refresh, reorder, and every other job type are untouched. Failure-isolated: a
    /// store hiccup, or the views being absent (a plain-PostgreSQL store — the caller also gates on the
    /// extension), yields an empty list and a Debug line, never a throw.
    /// </summary>
    public static async Task<IReadOnlyList<StuckCompressionJob>> ReadStuckCompressionJobsAsync(
        NpgsqlConnection connection, DateTime nowUtc, ILogger? logger, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        var stuck = new List<StuckCompressionJob>();
        try
        {
            using var command = new NpgsqlCommand(@"
SELECT
    js.job_id,
    (js.next_start = '-infinity'::timestamptz)  AS next_start_neg_infinity,
    js.job_status,
    js.last_run_started_at,
    EXTRACT(EPOCH FROM j.schedule_interval)     AS schedule_interval_seconds,
    j.hypertable_name
FROM timescaledb_information.job_stats AS js
JOIN timescaledb_information.jobs      AS j USING (job_id)
WHERE j.proc_name LIKE '%compression%'
   OR j.proc_name LIKE '%columnstore%'", connection);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                long jobId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                bool negInfinity = !reader.IsDBNull(1) && reader.GetBoolean(1);
                string? jobStatus = reader.IsDBNull(2) ? null : reader.GetString(2);
                DateTime? lastRunStartedAt = reader.IsDBNull(3)
                    ? null
                    : DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc);
                TimeSpan? scheduleInterval = reader.IsDBNull(4)
                    ? null
                    : TimeSpan.FromSeconds(Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture));
                string? hypertable = reader.IsDBNull(5) ? null : reader.GetString(5);

                if (IsCompressionJobStuck(negInfinity, jobStatus, lastRunStartedAt, scheduleInterval, nowUtc, out var reason))
                {
                    stuck.Add(new StuckCompressionJob(jobId, hypertable, reason));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* The views are absent (a plain-PG store or the extension was removed) or the store hiccuped —
               no signal this check. The caller already gates on the extension; this is belt-and-suspenders. */
            logger?.LogDebug("Compression-job health check: could not read job stats: {Message}", ex.Message);
        }

        return stuck;
    }

    /// <summary>
    /// Re-arms one stuck background job via the parameterized <see cref="RearmJobSql"/> (job_id BOUND). Returns
    /// true when <c>alter_job</c> succeeds; false (logged once, no throw) when it fails — most often because the
    /// store login does not OWN the job (a least-privilege bring-your-own store), which the service cannot fix
    /// itself. Cancellation propagates; every other failure degrades so a single un-re-armable job can never
    /// crash the health check or the sweep.
    /// </summary>
    public static async Task<bool> TryRearmJobAsync(
        NpgsqlConnection connection, long jobId, ILogger? logger, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        try
        {
            using var command = new NpgsqlCommand(RearmJobSql, connection);
            command.Parameters.AddWithValue(jobId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(
                "Could not re-arm compression job {JobId} via alter_job (often a permission issue — the store login must own the job): {Message}",
                jobId, ex.Message);
            return false;
        }
    }
}

/// <summary>
/// A COMPRESSION-policy background job that <see cref="TimescaleSupport.ReadStuckCompressionJobsAsync"/> flagged
/// as stuck (#1581): its immutable <c>job_id</c>, the hypertable it compresses (for a friendlier alert label —
/// may be null on an odd catalog), and the human-readable reason the pure predicate produced.
/// </summary>
public sealed record StuckCompressionJob(long JobId, string? HypertableName, string Reason);

/// <summary>
/// Which retention rollups exist in a store (<see cref="TimescaleSupport.DetectRollupsAsync"/>): the
/// query-grain pair (query_stats_hourly / _daily — the Daily Summary and top-consumer readers), the
/// database-grain pair (query_stats_db_hourly / _daily — the FinOps database-resource reader), and the
/// composer's remaining catalog pairs (procedure_stats and query_store_stats, #1665 — the built-in tabs never
/// read those rollups, but <c>ComposeSourceRouter</c> routes onto all three tables' pairs). All false on a
/// plain-PostgreSQL store, where raw is complete anyway; per-flag on a TimescaleDB store so a
/// failure-isolated partial build degrades one tier instead of erroring (#1664).
/// </summary>
public readonly record struct RollupAvailability(
    bool QueryGrainHourly, bool QueryGrainDaily, bool DbGrainHourly, bool DbGrainDaily,
    bool ProcedureGrainHourly, bool ProcedureGrainDaily, bool QueryStoreGrainHourly, bool QueryStoreGrainDaily)
{
    /// <summary>True when every rollup exists — the steady state on a TimescaleDB store, safe to cache
    /// permanently (a created continuous aggregate is never dropped outside the reshape sweep).</summary>
    public bool AllPresent =>
        QueryGrainHourly && QueryGrainDaily && DbGrainHourly && DbGrainDaily
        && ProcedureGrainHourly && ProcedureGrainDaily && QueryStoreGrainHourly && QueryStoreGrainDaily;

    /// <summary>No rollups at all — the plain-PostgreSQL shape, and the safe fallback when a probe fails.</summary>
    public static RollupAvailability None => default;

    /// <summary>Every flag true — the fully-built TimescaleDB shape (and the test shorthand for it).</summary>
    public static RollupAvailability All => new(true, true, true, true, true, true, true, true);

    /// <summary>
    /// Whether <paramref name="caggView"/> (an unqualified <c>collect.*</c> rollup view name — the strings the
    /// compose catalog carries, which are the <see cref="TimescaleSupport"/> view constants) exists in this
    /// store. Unknown names answer false: a view this probe never checked must be treated as absent, so a
    /// catalog entry added without extending the probe degrades to raw instead of routing blind (#1665).
    /// </summary>
    public bool Has(string caggView) => caggView switch
    {
        TimescaleSupport.QueryStatsHourlyView => QueryGrainHourly,
        TimescaleSupport.QueryStatsDailyView => QueryGrainDaily,
        TimescaleSupport.QueryStatsDbHourlyView => DbGrainHourly,
        TimescaleSupport.QueryStatsDbDailyView => DbGrainDaily,
        TimescaleSupport.ProcedureStatsHourlyView => ProcedureGrainHourly,
        TimescaleSupport.ProcedureStatsDailyView => ProcedureGrainDaily,
        TimescaleSupport.QueryStoreStatsHourlyView => QueryStoreGrainHourly,
        TimescaleSupport.QueryStoreStatsDailyView => QueryStoreGrainDaily,
        _ => false,
    };
}

/// <summary>
/// One tier ladder's measured history: how far back the hourly rollup and the daily rollup have actually
/// MATERIALIZED, and how far back the RAW table underneath them still holds rows (#1759). The unit
/// <see cref="RetentionTierRouter.Resolve(DateTime, DateTime, bool, bool, TierCoverage)"/> routes on.
///
/// <para>Every field is nullable and null means EXACTLY ONE thing to the router: "no positive evidence".
/// A floor is null when the view is empty, when it does not exist, or when the probe failed — and all
/// three must behave identically, because the router only ever moves a window DOWN a tier on a positive
/// measurement that the lower tier reaches further back. That makes an unknown coverage state inert: the
/// age + availability ladder decides, exactly as it did before this existed.</para>
/// </summary>
public readonly record struct TierCoverage(DateTime? HourlyFloorUtc, DateTime? DailyFloorUtc, DateTime? RawOldestUtc)
{
    /// <summary>Nothing measured — the router falls back to the pure age + availability decision.</summary>
    public static TierCoverage Unknown => default;

    /// <summary>
    /// Does a tier whose materialized floor is <paramref name="floorUtc"/> hold the oldest point of a window
    /// starting at <paramref name="windowStartUtc"/>? A null floor covers NOTHING (see the type remarks) —
    /// which is the whole #1759 defect in one line: a rollup created <c>WITH NO DATA</c> answers only what it
    /// materialized, so a window below its floor comes back empty rather than falling through to raw.
    /// </summary>
    public static bool Covers(DateTime? floorUtc, DateTime windowStartUtc) =>
        floorUtc is DateTime floor && floor <= windowStartUtc;

    /// <summary>
    /// Does <paramref name="candidateFloorUtc"/> reach STRICTLY further back than <paramref name="floorUtc"/> —
    /// i.e. would routing there cover more of the window? A null candidate never wins (no evidence), and a null
    /// incumbent floor is beaten by any real measurement (a tier holding nothing loses to a tier holding
    /// something).
    ///
    /// <para>This asymmetry is the guard that keeps the #1759 fix from becoming its own regression. "Window
    /// starts before the rollup's floor" ALONE is not a reason to drop to raw: on a healthy store whose purges
    /// are armed, raw keeps ~4 days while the rollup keeps weeks, so a 30-day window on a 10-day-old store
    /// predates every floor and dropping to raw would return LESS. The fallback fires only where the lower tier
    /// is measurably deeper, which is precisely the held-purge shape #1759 describes.</para>
    /// </summary>
    public static bool ReachesFurtherBack(DateTime? candidateFloorUtc, DateTime? floorUtc) =>
        candidateFloorUtc is DateTime candidate && candidate < (floorUtc ?? DateTime.MaxValue);
}

/// <summary>
/// How far back every rollup in a store has actually materialized, and how far back each rolled RAW table
/// still reaches (<see cref="TimescaleSupport.DetectRollupCoverageAsync"/>) — the #1759 companion to
/// <see cref="RollupAvailability"/>'s "does it exist at all".
///
/// <para>Kept as a separate type rather than more fields on <see cref="RollupAvailability"/> for one
/// concrete reason: availability is a value that callers compare (<c>rollups == RollupAvailability.None</c>)
/// and cache PERMANENTLY once complete, because a created aggregate is never dropped. Coverage does the
/// opposite — it MOVES, backwards on a backfill and forwards on a retention drop — so it must be re-probed
/// on a cadence, and folding a mutable dictionary into a record struct would break the equality the
/// existing caches rely on.</para>
/// </summary>
public sealed class RollupCoverage
{
    private readonly IReadOnlyDictionary<string, DateTime> _floorsByView;
    private readonly IReadOnlyDictionary<string, DateTime> _oldestByRawTable;

    public RollupCoverage(
        IReadOnlyDictionary<string, DateTime> floorsByView,
        IReadOnlyDictionary<string, DateTime> oldestByRawTable)
    {
        _floorsByView = floorsByView ?? throw new ArgumentNullException(nameof(floorsByView));
        _oldestByRawTable = oldestByRawTable ?? throw new ArgumentNullException(nameof(oldestByRawTable));
    }

    /// <summary>Nothing measured — every lookup answers null, so the router keeps its pre-#1759 behaviour.
    /// The safe answer for a store with no rollups AND for a probe that failed.</summary>
    public static RollupCoverage Unknown { get; } = new(
        new Dictionary<string, DateTime>(StringComparer.Ordinal),
        new Dictionary<string, DateTime>(StringComparer.Ordinal));

    /// <summary>The oldest bucket <paramref name="caggView"/> has materialized, or null when it holds nothing
    /// (or was never probed). Mirrors <see cref="RollupAvailability.Has"/>: an unknown name answers null.</summary>
    public DateTime? FloorOf(string caggView) =>
        _floorsByView.TryGetValue(caggView, out var floor) ? floor : null;

    /// <summary>The oldest row <paramref name="rawTable"/> still holds, or null when it is empty (or was never
    /// probed).</summary>
    public DateTime? RawOldestOf(string rawTable) =>
        _oldestByRawTable.TryGetValue(rawTable, out var oldest) ? oldest : null;

    /// <summary>
    /// The tier ladder for one rollup pair: both floors plus the raw table underneath them, ready for
    /// <see cref="RetentionTierRouter.Resolve(DateTime, DateTime, bool, bool, TierCoverage)"/>. A pair with no
    /// daily view (or an unrecognized hourly view) still answers — with nulls, which the router treats as
    /// "no evidence".
    /// </summary>
    public TierCoverage For(string hourlyView, string? dailyView)
    {
        var rawTable = RawTableFor(hourlyView);
        return new TierCoverage(
            FloorOf(hourlyView),
            dailyView is null ? null : FloorOf(dailyView),
            rawTable is null ? null : RawOldestOf(rawTable));
    }

    /// <summary>The raw table a rollup view's tier ladder falls back TO, or null for a name outside
    /// <see cref="TimescaleSupport.RollupViews"/> (which answers "no evidence" rather than guessing).</summary>
    public static string? RawTableFor(string caggView)
    {
        foreach (var (view, rawTable) in TimescaleSupport.RollupViews)
        {
            if (string.Equals(view, caggView, StringComparison.Ordinal))
            {
                return rawTable;
            }
        }

        return null;
    }
}
