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
    /// WITH NO DATA — a full historical refresh over 145 GB is heavy I/O, a deliberate off-hours manual op, NEVER
    /// startup work; real-time aggregation stays ON (the default — no <c>materialized_only</c>), so the view is
    /// correct to query for any window immediately, just un-accelerated for old windows until the policy + a
    /// manual backfill materialize them. IF NOT EXISTS so a restart re-converges. A SINGLE statement: a CAGG
    /// CREATE cannot run inside a transaction, so it must never be batched with the policy call.
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
    /// The query_stats DAILY continuous aggregate — a HIERARCHICAL CAGG sourced from <see cref="QueryStatsHourlyView"/>
    /// (not raw). Re-aggregates the hourly rollup to 1-day buckets: SUM of the hourly sums, MIN of the hourly mins,
    /// MAX of the hourly maxes (each composes correctly across the coarser bucket), plus SUM of the hourly
    /// sample_counts. The GROUP BY uses the explicit <c>time_bucket('1 day', bucket)</c> expression, NOT the bare
    /// <c>bucket</c> alias: an unqualified <c>bucket</c> in GROUP BY binds to the SOURCE column (the hourly bucket)
    /// under Postgres's input-column-wins ambiguity rule, which would group by hour, not day. WITH NO DATA +
    /// IF NOT EXISTS; the hourly CAGG must already exist (it is created earlier in the same sweep).
    /// </summary>
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
    /// Idempotent (IF NOT EXISTS on both), so it re-converges every restart. Does NOT backfill history (WITH NO
    /// DATA + real-time aggregation keeps the view correct immediately; the heavy full refresh is a deliberate
    /// off-hours op). Returns the number ready.
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
        };

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

        logger?.LogInformation(
            "TimescaleDB: {Ready}/{Total} continuous aggregate(s) ready (3 hourly: query_stats, procedure_stats, query_store_stats; 3 daily: query_stats, procedure_stats, query_store_stats)",
            ready, aggregates.Length);
        return ready;
    }

    /// <summary>Raw-tier retention horizon: keep per-sweep raw ~4 days — one day past the hourly CAGG's own 3-day
    /// refresh window, so the raw drop never outruns the aggregate that preserves it.</summary>
    public const string RawRetentionInterval = "4 days";

    /// <summary>Hourly-CAGG-tier retention horizon: keep the hourly rollups 21 days — well past the daily CAGG's
    /// 3-day refresh window, so the hourly drop never outruns the daily aggregate. The daily CAGGs themselves get
    /// NO retention policy: they are the coarsened, kept-indefinitely tier.</summary>
    public const string HourlyRetentionInterval = "21 days";

    /// <summary>A TimescaleDB retention policy: schedule a background job that DROPs chunks older than
    /// <paramref name="dropAfter"/>. <c>if_not_exists</c> so a restart re-converges. The actual drop is a
    /// chunk-level DROP TABLE (cheap, no rewrite), so unlike the CAGG backfill it needs no off-hours window.</summary>
    public static string AddRetentionPolicySql(string relation, string dropAfter)
        => $"SELECT add_retention_policy('collect.{relation}', drop_after => INTERVAL '{dropAfter}', if_not_exists => true)";

    /// <summary>
    /// Attaches the tiered retention policies: the three raw tables drop at <see cref="RawRetentionInterval"/>, the
    /// three hourly CAGGs at <see cref="HourlyRetentionInterval"/>; the daily CAGGs are kept indefinitely (no
    /// policy). Ordering safety is by HORIZON, not run order — each tier's drop stays comfortably past the next
    /// tier's 3-day refresh start_offset (4d raw vs 3d hourly refresh; 21d hourly vs 3d daily refresh), so a drop
    /// never removes history the next tier has not yet materialized. Idempotent (<c>if_not_exists</c>) and
    /// failure-isolated per policy. MUST run AFTER <see cref="EnsureContinuousAggregatesAsync"/> so the hourly
    /// CAGGs the hourly policies target already exist. Returns the number of policies in place.
    ///
    /// COLD-START CAVEAT (existing stores only): on a store that already holds raw history OLDER than the hourly
    /// CAGG has materialized, the first raw drop can remove buckets the CAGG never captured. Fresh installs are
    /// safe automatically — nothing is older than 4 days until the CAGG has been materializing that long. For an
    /// EXISTING store, backfill the hourly CAGGs past the raw horizon BEFORE this policy's first run.
    /// </summary>
    public static async Task<int> EnsureRetentionPoliciesAsync(NpgsqlConnection connection, ILogger? logger, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        var policies = new[]
        {
            (Relation: "query_stats",             DropAfter: RawRetentionInterval),
            (Relation: "procedure_stats",         DropAfter: RawRetentionInterval),
            (Relation: "query_store_stats",       DropAfter: RawRetentionInterval),
            (Relation: QueryStatsHourlyView,      DropAfter: HourlyRetentionInterval),
            (Relation: ProcedureStatsHourlyView,  DropAfter: HourlyRetentionInterval),
            (Relation: QueryStoreStatsHourlyView, DropAfter: HourlyRetentionInterval),
            (Relation: QueryStatsDbHourlyView,    DropAfter: HourlyRetentionInterval),
        };

        var applied = 0;
        foreach (var (relation, dropAfter) in policies)
        {
            try
            {
                using var command = new NpgsqlCommand(AddRetentionPolicySql(relation, dropAfter), connection) { CommandTimeout = SetupTimeoutSeconds };
                await command.ExecuteNonQueryAsync(cancellationToken);
                applied++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(
                    "Retention policy for {Relation} ({DropAfter}) failed — that tier keeps growing until the next restart retries: {Message}",
                    relation, dropAfter, ex.Message);
            }
        }

        logger?.LogInformation(
            "TimescaleDB: {Applied}/{Total} retention policies in place (raw {Raw}, hourly CAGGs {Hourly}; daily CAGGs kept indefinitely)",
            applied, policies.Length, RawRetentionInterval, HourlyRetentionInterval);
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
        $"to_regclass('collect.{QueryStatsDbDailyView}') IS NOT NULL";

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
            reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3));
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
/// query-grain pair (query_stats_hourly / _daily — the Daily Summary and top-consumer readers) and the
/// database-grain pair (query_stats_db_hourly / _daily — the FinOps database-resource reader). All false on a
/// plain-PostgreSQL store, where raw is complete anyway; per-flag on a TimescaleDB store so a
/// failure-isolated partial build degrades one tier instead of erroring (#1664).
/// </summary>
public readonly record struct RollupAvailability(
    bool QueryGrainHourly, bool QueryGrainDaily, bool DbGrainHourly, bool DbGrainDaily)
{
    /// <summary>True when every rollup exists — the steady state on a TimescaleDB store, safe to cache
    /// permanently (a created continuous aggregate is never dropped outside the reshape sweep).</summary>
    public bool AllPresent => QueryGrainHourly && QueryGrainDaily && DbGrainHourly && DbGrainDaily;

    /// <summary>No rollups at all — the plain-PostgreSQL shape, and the safe fallback when a probe fails.</summary>
    public static RollupAvailability None => default;
}
