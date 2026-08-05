/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PerformanceMonitor.Darling.Storage;

/// <summary>
/// Darling's versioned schema migrations — plain SQL scripts the service applies on startup
/// (headless plan: no migration framework). Each script runs once, inside its own transaction,
/// tracked in darling_schema_version. V1 is generated from the collector definitions
/// (<see cref="PgSchemaGenerator.GenerateFullSchema"/>); later versions are appended, never
/// edited. Migrations stay engine-plain on purpose: TimescaleDB hypertable conversion is
/// RUNTIME setup (<see cref="TimescaleSupport"/>), applied by the service only when the
/// extension is detected — the same store must work on plain PostgreSQL.
/// </summary>
public static class PgMigrations
{
    public sealed class Migration
    {
        public Migration(int version, string name, string sql)
        {
            Version = version;
            Name = name;
            Sql = sql;
        }

        public int Version { get; }

        public string Name { get; }

        public string Sql { get; }
    }

    public static IReadOnlyList<Migration> Scripts { get; } = new[]
    {
        new Migration(1, "collector-tables", PgSchemaGenerator.GenerateFullSchema()),
        new Migration(2, "server-registry-and-collection-log", V2Sql),
        new Migration(3, "alerting-stores", V3Sql),
        new Migration(4, "analysis-tables", V4Sql),
        new Migration(5, "viewer-passthrough-views", V5Sql),
        new Migration(6, "memory-tab-passthrough-views", V6Sql),
        new Migration(7, "viewer-plan-capture-columns", V7Sql),
        new Migration(8, "schema-split-collect-config", PgSchemaGenerator.GenerateV8Move()),
        new Migration(9, "server-inventory-cost-fields", V9Sql),
        new Migration(10, "latch-spinlock-collectors", PgSchemaGenerator.GenerateV10AddLatchSpinlock()),
        new Migration(11, "cpu-scheduler-plan-cache-collectors", PgSchemaGenerator.GenerateV11AddCpuSchedulerPlanCache()),
        new Migration(12, "session-summary-collector", PgSchemaGenerator.GenerateV12AddSessionSummary()),
        new Migration(13, "system-health-events-collector", PgSchemaGenerator.GenerateV13AddSystemHealthEvents()),
        new Migration(14, "refresh-passthrough-views", PgSchemaGenerator.GenerateV14RefreshViews()),
        new Migration(15, "index-metadata-columns", V15Sql),
        new Migration(16, "server-utc-offset", V16Sql),
        new Migration(17, "config-control-plane", V17Sql),
        new Migration(18, "alert-delivery-mode", V18Sql),
        new Migration(19, "analysis-state-marker", V19Sql),
        new Migration(20, "alert-tuning-knobs", V20Sql),
        new Migration(21, "default-trace-events-collector", V21Sql),
        new Migration(22, "index-object-stats-latest-index", V22Sql),
        new Migration(23, "collection-log-hypertable", V23Sql),
        new Migration(24, "job-history-collector", V24Sql),
        new Migration(25, "agent-status-collector", V25Sql),
        new Migration(26, "generic-webhook-channel", V26Sql),
        new Migration(27, "deadlocks-database-name", V27Sql),
        new Migration(28, "query-store-replica-role", V28Sql),
        new Migration(29, "long-query-completions-collector", V29Sql),
        new Migration(30, "web-dashboard-config", V30Sql),
        new Migration(31, "custom-views-table", V31Sql),
        new Migration(32, "server-tags", V32Sql),
        new Migration(33, "connection-alert-refire", V33Sql),
        new Migration(34, "availability-group-collectors", V34Sql),
        new Migration(35, "availability-group-alerts", V35Sql),
        new Migration(36, "ag-latency-columns", V36Sql),
        new Migration(37, "ag-local-replica-and-disconnect-refire", V37Sql),
        new Migration(38, "query-payload-dimensions", PgSchemaGenerator.GenerateV38PayloadDimensions()),
        new Migration(39, "dim-feeding-fact-floor-indexes", V39Sql),
        new Migration(40, "blocking-wait-threshold", V40Sql),
        new Migration(41, "query-store-interval-identity", V41Sql),
        new Migration(42, "pagerduty-webhook", V42Sql),
        new Migration(43, "pagerduty-proxy", V43Sql),
        new Migration(44, "collector-state", V44Sql),
        /* 45 is permanently absent. It was reserved for the #1951 lane while that lane was still
           unmerged, but #1952 landed first and took 46, and the runner applies only versions ABOVE
           the store's MAX(version) — so a store already stamped 46 would have skipped a late-arriving
           45 forever, leaving upgraded stores without a table fresh stores get from V1. #1951 was
           renumbered to 47 rather than shipped into that hole. Version numbers only have to be unique
           and ascending: a gap costs nothing and a collision would cost a store. */
        new Migration(46, "plan-correction-collector", V46Sql),
        new Migration(47, "pvs-stats", V47Sql),
        new Migration(48, "pvs-pressure-alert", V48Sql),
        new Migration(49, "database-state-alert", V49Sql),
        new Migration(50, "server-tag-colour", V50Sql),
        new Migration(51, "query-stats-host-object", V51Sql + "\n" + PgSchemaGenerator.GenerateQueryStatsResolvingView()),
        new Migration(52, "finding-drilldown-json", V52Sql),
    };

    /// <summary>
    /// V2 — the service's observability store: the servers registry (upserted on every
    /// successful connect) and the per-run collection_log. Column names deliberately mirror
    /// Lite's DuckDB schema so viewer/analysis SQL can twin across stores — including
    /// duckdb_duration_ms, which in Darling records the Postgres storage phase. Lite's servers
    /// table also carries auth columns (use_windows_auth/username); Darling deliberately omits
    /// them because auth lives in darling.json. servers keeps its PRIMARY KEY (a registry, not
    /// a hypertable candidate); collection_log has none, same reasoning as the collector tables.
    /// </summary>
    private const string V2Sql = @"
CREATE TABLE IF NOT EXISTS servers (
    server_id integer NOT NULL PRIMARY KEY,
    server_name text NOT NULL,
    display_name text,
    is_enabled boolean NOT NULL DEFAULT TRUE,
    sql_engine_edition integer,
    sql_major_version integer,
    created_date timestamp,
    modified_date timestamp
);

CREATE TABLE IF NOT EXISTS collection_log (
    log_id bigint NOT NULL,
    server_id integer NOT NULL,
    server_name text,
    collector_name text NOT NULL,
    collection_time timestamp NOT NULL,
    duration_ms integer,
    status text NOT NULL,
    error_message text,
    rows_collected integer,
    sql_duration_ms integer,
    duckdb_duration_ms integer
);

CREATE INDEX IF NOT EXISTS idx_collection_log_time ON collection_log(server_id, collection_time);";

    /// <summary>
    /// V3 — the alerting stores behind the Phase-5 shared alert engine (slice D), each mirroring
    /// its Lite DuckDB twin column-for-column so viewer/analysis SQL can twin across stores:
    /// <c>config_alert_log</c> (one combined history row per fired alert — Lite's Schema.cs
    /// CreateAlertLogTable), <c>config_edge_trigger_watermarks</c> (the #1091 rolling-count
    /// watermarks + the time-based failed-job watermark, #1145 restart survival — Lite's
    /// CreateEdgeTriggerWatermarksTable, including its (server_id, metric_name) primary key the
    /// upserts conflict on), and <c>config_mute_rules</c> (Lite's CreateMuteRulesTable). The
    /// alert-log index serves the per-(server, metric) MAX(alert_time) cooldown seeds.
    /// </summary>
    private const string V3Sql = @"
CREATE TABLE IF NOT EXISTS config_alert_log (
    alert_time timestamp NOT NULL,
    server_id integer NOT NULL,
    server_name text NOT NULL,
    metric_name text NOT NULL,
    current_value double precision NOT NULL,
    threshold_value double precision NOT NULL,
    alert_sent boolean NOT NULL DEFAULT FALSE,
    notification_type text NOT NULL DEFAULT 'tray',
    send_error text,
    dismissed boolean NOT NULL DEFAULT FALSE,
    muted boolean NOT NULL DEFAULT FALSE,
    detail_text text,
    context_json text
);

CREATE INDEX IF NOT EXISTS idx_config_alert_log_time ON config_alert_log(server_id, metric_name, alert_time);

CREATE TABLE IF NOT EXISTS config_edge_trigger_watermarks (
    server_id integer NOT NULL,
    metric_name text NOT NULL,
    watermark integer NOT NULL,
    watermark_time timestamp,
    updated_at timestamp NOT NULL,
    PRIMARY KEY (server_id, metric_name)
);

CREATE TABLE IF NOT EXISTS config_mute_rules (
    id text NOT NULL PRIMARY KEY,
    enabled boolean NOT NULL DEFAULT TRUE,
    created_at_utc timestamp NOT NULL,
    expires_at_utc timestamp,
    reason text,
    server_name text,
    metric_name text,
    database_pattern text,
    query_text_pattern text,
    wait_type_pattern text,
    job_name_pattern text
);";

    /// <summary>
    /// V4 — the analysis-engine stores (Phase-5 analysis slice AN1) plus the passthrough views
    /// the ported analysis SQL reads. <c>analysis_findings</c> is Lite's AnalysisSchema v3 shape
    /// column-for-column PLUS the Dashboard's <c>remediation_action_json</c> (recommendations
    /// rebuild D2: the BUILT RemediationAction persisted on the row, serialized by the shared
    /// AlertContextSerializer) — no primary key, same hypertable/COPY reasoning as the collector
    /// tables (TimescaleDB hypertables require the partition column in any unique constraint, and
    /// bulk ingest doesn't want one); finding_id stays a NOT NULL bigint, and the two Lite index
    /// column sets carry over. <c>analysis_muted</c> is a small mute registry and KEEPS its
    /// primary key (like V2's servers); Lite's DuckDB <c>DEFAULT CURRENT_TIMESTAMP</c> on
    /// muted_date is deliberately NOT carried — in Postgres that default would stamp the PG
    /// server's LOCAL clock, and the store always supplies naive-UTC explicitly.
    ///
    /// The <c>v_&lt;table&gt;</c> views exist so analysis SQL ports VERBATIM (AN2/AN3): in Lite
    /// they union the hot DuckDB table with the parquet archive; Darling has no parquet tier, so
    /// they are plain passthroughs. Seventeen views — the fourteen the fact collectors read
    /// (wait/query/query-store/cpu/memory-grant/memory/perfmon/session/file-io stats,
    /// blocked_process_reports, deadlocks, dmv_blocking_snapshots, index_object_stats,
    /// database_size_stats) plus the three Lite's drill-down/storage collectors also read
    /// (v_tempdb_stats in DuckDbFactCollector.Storage + DrillDownCollector.Storage,
    /// v_query_snapshots in DrillDownCollector.Queries, v_database_config in
    /// DrillDownCollector.Config). Every view targets a V1 collector table, pinned by test.
    /// </summary>
    private const string V4Sql = @"
CREATE TABLE IF NOT EXISTS analysis_findings (
    finding_id bigint NOT NULL,
    analysis_time timestamp NOT NULL,
    server_id integer NOT NULL,
    server_name text NOT NULL,
    database_name text,
    time_range_start timestamp,
    time_range_end timestamp,
    severity double precision NOT NULL,
    confidence double precision NOT NULL,
    category text NOT NULL,
    story_path text NOT NULL,
    story_path_hash text NOT NULL,
    story_text text NOT NULL,
    root_fact_key text NOT NULL,
    root_fact_value double precision,
    leaf_fact_key text,
    leaf_fact_value double precision,
    fact_count integer NOT NULL,
    incident_id text,
    remediation_action_json text
);

CREATE INDEX IF NOT EXISTS idx_analysis_findings_time ON analysis_findings(server_id, analysis_time);
CREATE INDEX IF NOT EXISTS idx_analysis_findings_hash ON analysis_findings(story_path_hash);

CREATE TABLE IF NOT EXISTS analysis_muted (
    mute_id bigint NOT NULL PRIMARY KEY,
    server_id integer,
    database_name text,
    story_path_hash text NOT NULL,
    story_path text NOT NULL,
    muted_date timestamp NOT NULL,
    reason text
);

CREATE INDEX IF NOT EXISTS idx_analysis_muted_hash ON analysis_muted(story_path_hash);

CREATE OR REPLACE VIEW v_wait_stats AS SELECT * FROM wait_stats;
CREATE OR REPLACE VIEW v_query_stats AS SELECT * FROM query_stats;
CREATE OR REPLACE VIEW v_query_store_stats AS SELECT * FROM query_store_stats;
CREATE OR REPLACE VIEW v_cpu_utilization_stats AS SELECT * FROM cpu_utilization_stats;
CREATE OR REPLACE VIEW v_memory_grant_stats AS SELECT * FROM memory_grant_stats;
CREATE OR REPLACE VIEW v_memory_stats AS SELECT * FROM memory_stats;
CREATE OR REPLACE VIEW v_perfmon_stats AS SELECT * FROM perfmon_stats;
CREATE OR REPLACE VIEW v_session_stats AS SELECT * FROM session_stats;
CREATE OR REPLACE VIEW v_file_io_stats AS SELECT * FROM file_io_stats;
CREATE OR REPLACE VIEW v_blocked_process_reports AS SELECT * FROM blocked_process_reports;
CREATE OR REPLACE VIEW v_deadlocks AS SELECT * FROM deadlocks;
CREATE OR REPLACE VIEW v_dmv_blocking_snapshots AS SELECT * FROM dmv_blocking_snapshots;
CREATE OR REPLACE VIEW v_index_object_stats AS SELECT * FROM index_object_stats;
CREATE OR REPLACE VIEW v_database_size_stats AS SELECT * FROM database_size_stats;
CREATE OR REPLACE VIEW v_tempdb_stats AS SELECT * FROM tempdb_stats;
CREATE OR REPLACE VIEW v_query_snapshots AS SELECT * FROM query_snapshots;
CREATE OR REPLACE VIEW v_database_config AS SELECT * FROM database_config;";

    /* V5 — the five passthrough views V4 left out, completing the v_* twin of Lite's DuckDB
       view layer so every ported viewer query stays byte-identical to Lite's (the copy-parity
       program's tail tabs read these: Running Jobs, Configuration ×3, Daily Summary /
       Collection Health). Tables all exist since V1/V2; views only. */
    private const string V5Sql = @"
CREATE OR REPLACE VIEW v_running_jobs AS SELECT * FROM running_jobs;
CREATE OR REPLACE VIEW v_server_config AS SELECT * FROM server_config;
CREATE OR REPLACE VIEW v_database_scoped_config AS SELECT * FROM database_scoped_config;
CREATE OR REPLACE VIEW v_trace_flags AS SELECT * FROM trace_flags;
CREATE OR REPLACE VIEW v_collection_log AS SELECT * FROM collection_log;";

    /* V6 — the two memory passthrough views V4/V5 left out, needed by the Memory tab port (W1j): the
       Memory Clerks + Memory Pressure Events sub-tabs read v_memory_clerks / v_memory_pressure_events,
       mirroring Lite, so their ported SQL stays byte-identical. Tables exist since V1; views only. */
    private const string V6Sql = @"
CREATE OR REPLACE VIEW v_memory_clerks AS SELECT * FROM memory_clerks;
CREATE OR REPLACE VIEW v_memory_pressure_events AS SELECT * FROM memory_pressure_events;";

    /* V7 — the deferred-plan-capture columns the viewer's blocked-process/deadlock/procedure "View
       Plan" surfaces read (PR #1262, extending the #1349 pattern to three more collectors). All are
       nullable text appended to existing collector tables, so a store already at V1's pre-plan shape
       comes up to shape with one ADD COLUMN each; a fresh install already has them (V1 is generated
       from the current collector definitions, which now include these columns), and ADD COLUMN IF NOT
       EXISTS makes that a harmless no-op. Darling captures the plans because DarlingConfig.CapturePlans
       defaults true (CollectorContext.CapturePlanXml) — no config change; Lite never sets the flag and
       always writes NULL here. Appended (not inserted) so a fresh V1 store and an upgraded V7 store keep
       an identical physical column order. */
    private const string V7Sql = @"
ALTER TABLE procedure_stats ADD COLUMN IF NOT EXISTS query_plan_xml text;
ALTER TABLE blocked_process_reports ADD COLUMN IF NOT EXISTS blocked_query_plan_xml text;
ALTER TABLE blocked_process_reports ADD COLUMN IF NOT EXISTS blocking_query_plan_xml text;
ALTER TABLE deadlocks ADD COLUMN IF NOT EXISTS victim_query_plan_xml text;";

    /* V27 — deadlocks.database_name: the victim process's currentdbname, keying the Azure SQL DB
       per-database watermark (#1535: capture is one database-scoped session per monitored database).
       Nullable text appended at the end (identical physical column order for the binary COPY whether
       fresh — V1 is generated from the current collector definition, which now includes it — or
       upgraded; ADD COLUMN IF NOT EXISTS no-ops on fresh). blocked_process_reports already had
       database_name. The trailing CREATE OR REPLACE VIEW re-expands v_deadlocks' pinned SELECT *
       (Postgres freezes it at CREATE; append-only ADDs keep the refresh legal), mirroring V15.
       Runs after V8, so the bare names resolve through search_path = collect, config, public. */
    private const string V27Sql = @"
ALTER TABLE deadlocks ADD COLUMN IF NOT EXISTS database_name text;
CREATE OR REPLACE VIEW v_deadlocks AS SELECT * FROM deadlocks;";

    /* V28 — query_store_stats.replica_role: the replica role SQL Server 2022+ attributed each
       runtime-stats row to (sys.query_store_replicas.replica_name, LEFT JOINed by replica_group_id).
       With "Query Store for secondary replicas" enabled an AG keeps ONE shared Query Store on the
       PRIMARY holding every replica's rows, so the primary's numbers silently blend in secondary
       workload unless split by this column. Nullable text appended at the end (identical physical
       column order for the binary COPY whether fresh — V1 is generated from the current collector
       definition, which now includes it — or upgraded; ADD COLUMN IF NOT EXISTS no-ops on fresh).
       The trailing CREATE OR REPLACE VIEW re-expands v_query_store_stats' pinned SELECT * (Postgres
       freezes it at CREATE; append-only ADDs keep the refresh legal), mirroring V15/V27. Runs after
       V8, so the bare names resolve through search_path = collect, config, public. */
    private const string V28Sql = @"
ALTER TABLE query_store_stats ADD COLUMN IF NOT EXISTS replica_role text;
CREATE OR REPLACE VIEW v_query_store_stats AS SELECT * FROM query_store_stats;";

    /// <summary>
    /// V46 — the <c>plan_correction</c> collector table (#1952): what the engine's own automatic plan
    /// correction is doing per database — the FORCE_LAST_GOOD_PLAN enablement state from
    /// <c>sys.database_automatic_tuning_options</c>, and the live recommendation set from
    /// <c>sys.dm_db_tuning_recommendations</c> with its details JSON shredded into typed columns and
    /// the regressed query's text resolved through Query Store at collection time. Added additively
    /// exactly like V29/V34 — a fresh store already has it (V1's
    /// <see cref="PgSchemaGenerator.GenerateFullSchema"/> walks the collector catalog), so
    /// <c>CREATE TABLE IF NOT EXISTS</c> is a no-op on fresh and the real create on upgrade. Column
    /// order/types are exactly <see cref="PgSchemaGenerator.CreateTable"/>'s output for the
    /// <see cref="PlanCorrectionCollector"/> catalog entry (prefix NOT NULL, payload nullable), which
    /// <c>DarlingPlanCorrectionMigrationTests</c> asserts rather than leaving to this comment — the one
    /// thing a hand-written literal can get wrong that a generated body cannot. No <c>v_*</c>
    /// passthrough view (a post-V14 collector); the viewer reads the base table directly. Hypertable /
    /// compression / 30-day retention flow from the catalog at runtime.
    /// </summary>
    private const string V46Sql = @"
CREATE TABLE IF NOT EXISTS collect.plan_correction (
    collection_id bigint NOT NULL,
    collection_time timestamp NOT NULL,
    server_id integer NOT NULL,
    server_name text NOT NULL,
    database_name text,
    force_last_good_plan_desired_state text,
    force_last_good_plan_actual_state text,
    force_last_good_plan_reason text,
    create_index_actual_state text,
    drop_index_actual_state text,
    recommendation_name text,
    recommendation_type text,
    recommendation_state text,
    recommendation_state_reason text,
    recommendation_reason text,
    valid_since timestamp,
    last_refresh timestamp,
    score integer,
    query_id bigint,
    query_text text,
    regressed_plan_id bigint,
    last_good_plan_id bigint,
    last_good_plan_forcing_type text,
    last_good_plan_is_forced boolean,
    last_good_plan_force_failure_reason text,
    regressed_plan_execution_count bigint,
    regressed_plan_cpu_time_average_ms double precision,
    regressed_plan_error_count bigint,
    last_good_plan_execution_count bigint,
    last_good_plan_cpu_time_average_ms double precision,
    last_good_plan_error_count bigint,
    estimated_gain_seconds double precision,
    is_executable_action boolean,
    is_revertable_action boolean,
    execute_action_initiated_by text,
    execute_action_initiated_time timestamp,
    execute_action_start_time timestamp,
    execute_action_duration_seconds double precision,
    revert_action_initiated_by text,
    revert_action_initiated_time timestamp,
    revert_action_start_time timestamp,
    revert_action_duration_seconds double precision,
    implementation_script text
);

CREATE INDEX IF NOT EXISTS idx_plan_correction_time ON collect.plan_correction(server_id, collection_time);";

    /// <summary>
    /// V29 — the <c>long_query_completions</c> collector table (#1496): long-running query completions
    /// (rpc_completed / sql_batch_completed >= duration threshold, plus attention/cancels) from a
    /// dedicated, OPT-IN XE ring-buffer session. Added additively exactly like V21/V24/V25 — a fresh
    /// store already has it (V1's <see cref="PgSchemaGenerator.GenerateFullSchema"/> walks the collector
    /// and V8 moved it to <c>collect</c>), so <c>CREATE TABLE IF NOT EXISTS</c> is a no-op on fresh and
    /// the real create on upgrade. Column order/types are exactly <see cref="PgSchemaGenerator.CreateTable"/>'s
    /// output for the <see cref="LongQueryCompletionsCollector"/> catalog entry (prefix NOT NULL, payload
    /// nullable). No <c>v_*</c> passthrough view (a post-V14 collector); the viewer reads the base table
    /// directly. Hypertable / compression / 30-day retention flow from the catalog at runtime.
    /// </summary>
    private const string V29Sql = @"
CREATE TABLE IF NOT EXISTS collect.long_query_completions (
    long_query_completion_id bigint NOT NULL,
    collection_time timestamp NOT NULL,
    server_id integer NOT NULL,
    server_name text NOT NULL,
    event_time timestamp,
    event_type text,
    database_id integer,
    database_name text,
    session_id integer,
    client_app_name text,
    client_pid integer,
    nt_username text,
    server_principal_name text,
    query_hash text,
    event_sequence bigint,
    duration_microseconds bigint,
    cpu_time_microseconds bigint,
    physical_reads bigint,
    logical_reads bigint,
    writes bigint,
    row_count bigint,
    result text,
    statement_text text,
    object_name text
);

CREATE INDEX IF NOT EXISTS idx_long_query_completions_time ON collect.long_query_completions(server_id, collection_time);";

    /// <summary>
    /// V30 — the web dashboard's live toggle (#1562): config_service gains web_enabled + web_port, the twins of
    /// mcp_enabled/mcp_port, so the viewer's Settings toggle drives the second Kestrel host through the same
    /// config_version reload beacon (the BEFORE-UPDATE self-bump trigger already covers the new columns). Additive
    /// ALTERs, schema-qualified config.* exactly like V18 (the migrate session's search_path resolves a bare name
    /// to collect — the wrong schema/ACL); IF NOT EXISTS matches the file's ALTER idiom so a re-run is a no-op. Both
    /// columns are NOT NULL with the shipped defaults (off / 5153) so the single seeded row comes up honoring them.
    /// config_service has no v_* passthrough view, so nothing to refresh. Role grants are table-wide SELECT for
    /// viewer/mcp and table-wide writes for admin (config_service is NOT column-carved in DarlingManagedRoles), so
    /// the new columns inherit the grants automatically — no grant change needed.
    /// </summary>
    private const string V30Sql = @"
ALTER TABLE config.config_service ADD COLUMN IF NOT EXISTS web_enabled boolean NOT NULL DEFAULT FALSE;
ALTER TABLE config.config_service ADD COLUMN IF NOT EXISTS web_port integer NOT NULL DEFAULT 5153;";

    /// <summary>
    /// V31 — the web dashboard's user-authored CUSTOM VIEWS store (#1563): the saved dashboard/notebook
    /// definitions the web renderer composes from the existing read allowlist. One row per named view; the
    /// <c>definition</c> is the view JSON (<c>{panels:[{read,params,viz,span,...}]}</c>) validated app-side
    /// before storage. A NEW config-plane table (the web renderer reads it; the Viewer's composer writes it),
    /// added additively exactly like V17's control-plane tables.
    ///
    /// <para><b>Schema — <c>config</c>, qualified.</b> V31 CREATEs directly in <c>config</c>, and the migrate
    /// session runs under <c>search_path = collect, config, public</c> (<see cref="PgSchemaGenerator.SearchPath"/>),
    /// so an UNQUALIFIED <c>CREATE TABLE custom_views</c> would resolve to <c>collect</c> (first in the path) —
    /// the wrong schema and the wrong ACL (config is the admin-writable surface). Qualifying <c>config.</c> pins
    /// it into the admin-writable schema, mirroring V17/V18/V30. <c>id</c> is <c>GENERATED ALWAYS AS IDENTITY</c>
    /// (not <c>serial</c>) so INSERTs need no sequence USAGE grant — the same reasoning as V17's
    /// <c>config_command</c>. <c>name</c> is UNIQUE (a duplicate insert/rename surfaces as a 409 to the caller);
    /// <c>definition</c> is <c>jsonb NOT NULL</c> (the engine rejects malformed JSON as a second line of defense
    /// behind the app-side validator). Timestamps store naive-UTC (<c>now() AT TIME ZONE 'UTC'</c>) to match the
    /// store-wide convention (Npgsql rejects Kind=Utc against a bare <c>timestamp</c>).
    ///
    /// <para><b>Versioned = an IN-PLACE <c>version</c> column + optimistic concurrency</b> (the store's UPDATE
    /// carries <c>WHERE id = $ AND version = $expected</c>; a 0-rowcount is a stale-write 409), NOT append-only
    /// history rows — a <c>custom_view_history</c> audit trail is a clean later add if it is ever wanted.</para>
    ///
    /// <para><b>NO <c>config_bump_version</c> trigger</b> (deliberately, unlike V17's four desired-state tables):
    /// custom_views feeds the WEB RENDERER, never the collector/service loop, so a write here has nothing for the
    /// service to reload — bumping the <c>config_version</c> reload beacon would only trigger a needless service
    /// reload on every view save. The write grant for the least-privilege viewer role (the web dashboard's
    /// identity) is an EXPLICIT single-table grant in <see cref="!:DarlingManagedRoles.BuildProvisioningSql"/>
    /// (and BYO <c>tools/provision-roles.sql</c>), NOT an <c>ALTER DEFAULT PRIVILEGES</c> widening — custom_views
    /// has no secret columns, so it needs no <c>ViewerRestrictedConfigTables</c> carve.</para>
    /// </summary>
    private const string V31Sql = @"
CREATE TABLE IF NOT EXISTS config.custom_views (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name text NOT NULL UNIQUE,
    definition jsonb NOT NULL,
    description text,
    version integer NOT NULL DEFAULT 1,
    created_at timestamp NOT NULL DEFAULT (now() AT TIME ZONE 'UTC'),
    updated_at timestamp NOT NULL DEFAULT (now() AT TIME ZONE 'UTC'),
    updated_by text
);";

    /// <summary>
    /// V32 — the Viewer's fleet TAGS, the user-authored visual organization of a large server list
    /// (~100 servers at the motivating field site). Two NEW config-plane tables, added additively exactly
    /// like V31: <c>server_tags</c> is the tag tree, <c>server_tag_map</c> is the many-to-many assignment.
    ///
    /// <para><b>Tags, not exclusive groups.</b> A server may carry any number of tags, so the composite
    /// PK <c>(server_id, tag_id)</c> on the map is the whole membership rule — deliberately NOT a unique
    /// constraint on <c>server_id</c>, which is what an exclusive-group model would have needed.</para>
    ///
    /// <para><b>Membership lives in its own table, never as a column on
    /// <c>config_monitored_servers</c>.</b> A column there would have required all four of: an edit to the
    /// fail-closed <c>ViewerRestrictedConfigTables</c> column ACL (an unlisted column is invisible, which
    /// breaks the read-only viewer's ENTIRE sidebar read, not just the new field); a matching hand edit to
    /// BYO <c>tools/provision-roles.sql</c>; a silent widening of the network-reachable <c>mcp</c> role,
    /// whose INSERT/UPDATE/DELETE on that table is TABLE-level and so would cover a new column it cannot
    /// even SELECT (a blind write); and — because <c>trg_bump_monitored_servers</c> is a STATEMENT-level
    /// trigger — a full service <c>ReloadFromStoreAsync</c> per tag write, up to one per server on a bulk
    /// tag of 100. A side table avoids all four. It also survives the server-identity move
    /// (<c>server_id</c> is derived from host+database+read_only_intent, so editing any of those
    /// upserts-new-then-deletes-old), which a column on the server row would not.</para>
    ///
    /// <para><b>Nesting.</b> <c>parent_id</c> is a self-reference; NULL = a root tag. Depth is capped
    /// app-side at 4 levels — Postgres cannot express that without a trigger, and the tag table is tiny
    /// (dozens of rows), so the Viewer loads it whole, builds the tree in memory, and checks both the cap
    /// and cycle-freedom there. No recursive CTE, no ltree extension, no closure table.
    /// <c>ON DELETE CASCADE</c> on the self-reference means deleting a tag removes its whole subtree and
    /// (via the map's own cascade) those assignments — the folder mental model. It can never reach
    /// <c>config_monitored_servers</c>: there is no FK to it, so no server row, credential blob, or
    /// collected history is touched. The Viewer still warns with the descendant + assignment counts.</para>
    ///
    /// <para><b>Uniqueness is per-parent</b> so <c>prod/primaries</c> and <c>staging/primaries</c> coexist.
    /// It is a UNIQUE INDEX over <c>COALESCE(parent_id, 0)</c> rather than a plain <c>UNIQUE
    /// (parent_id, name)</c>, because Postgres treats NULLs as DISTINCT — two ROOT tags both named
    /// 'Prod' would otherwise both be accepted. Identity starts at 1, so 0 is never a real id.
    /// <c>lower(name)</c> makes it case-insensitive, matching how the rest of the product treats
    /// operator-entered names.</para>
    ///
    /// <para><b>NO <c>config_bump_version</c> trigger</b>, same reasoning as V31: tags feed the VIEWER's
    /// sidebar, never the collector/service loop, so there is nothing for the service to reload and a
    /// beacon bump would only cost a needless fleet reconcile. <c>id</c> is
    /// <c>GENERATED ALWAYS AS IDENTITY</c> so INSERTs need no sequence USAGE grant. No explicit grant is
    /// added: both tables are picked up by the blanket <c>GRANT ... ON ALL TABLES IN SCHEMA config</c>
    /// statements that provisioning re-runs on EVERY service start. Note it is those, not
    /// <c>ALTER DEFAULT PRIVILEGES</c>, that cover a table introduced by a migration — ADP only applies to
    /// objects created after it runs, and provisioning runs AFTER the migration pass. No
    /// <c>ViewerRestrictedConfigTables</c> carve is needed either, because neither table has a secret
    /// column — but note the corollary: a table in <c>config</c> is readable by the network-reachable
    /// <c>mcp</c> role by default, so if tag names may carry customer identifiers the REVOKE must be
    /// emitted inside provisioning AFTER that blanket grant (and mirrored into BYO
    /// <c>tools/provision-roles.sql</c>), or the next service start silently re-grants it.</para>
    /// </summary>
    private const string V32Sql = @"
CREATE TABLE IF NOT EXISTS config.server_tags (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name text NOT NULL,
    parent_id integer REFERENCES config.server_tags(id) ON DELETE CASCADE,
    sort_order integer NOT NULL DEFAULT 0,
    created_at timestamp NOT NULL DEFAULT (now() AT TIME ZONE 'UTC')
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_server_tags_parent_name
    ON config.server_tags (COALESCE(parent_id, 0), lower(name));

CREATE TABLE IF NOT EXISTS config.server_tag_map (
    server_id integer NOT NULL,
    tag_id integer NOT NULL REFERENCES config.server_tags(id) ON DELETE CASCADE,
    PRIMARY KEY (server_id, tag_id)
);

CREATE INDEX IF NOT EXISTS idx_server_tag_map_tag
    ON config.server_tag_map (tag_id);";

    /// <summary>
    /// V33 — the #1659 connection-alert opt-ins on the singleton config_alert_settings row: announce a
    /// server already down at first sight, and re-announce a standing outage every N minutes (0 = off).
    /// Both default OFF, preserving the classic edge-only behavior byte-for-byte. ADD COLUMN IF NOT EXISTS
    /// keeps the migration idempotent; NOT NULL DEFAULT means a pre-V33 row reads correctly with no backfill.
    /// No ACL/provisioning change: config_alert_settings carries table-level grants (no column carve).
    /// </summary>
    private const string V33Sql = @"
ALTER TABLE config.config_alert_settings
    ADD COLUMN IF NOT EXISTS notify_connection_down_at_startup boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS connection_refire_minutes integer NOT NULL DEFAULT 0;";

    /// <summary>
    /// V34 — the two Availability Group collector tables (#991): <c>ag_replica_states</c> (replica grain)
    /// and <c>ag_database_replica_states</c> (database grain, carrying the send/redo queues, rates and
    /// secondary lag). Added additively exactly like V29 — a fresh store already has both (V1's
    /// <see cref="PgSchemaGenerator.GenerateFullSchema"/> walks the collector catalog), so
    /// <c>CREATE TABLE IF NOT EXISTS</c> is a no-op on fresh and the real create on upgrade. Column
    /// order/types are exactly <see cref="PgSchemaGenerator.CreateTable"/>'s output for the
    /// <see cref="AgReplicaStatesCollector"/> / <see cref="AgDatabaseReplicaStatesCollector"/> catalog
    /// entries (prefix NOT NULL, payload nullable). No <c>v_*</c> passthrough views (post-V14 collectors);
    /// readers use the base tables. Hypertable / compression / retention flow from the catalog at runtime.
    /// </summary>
    private const string V34Sql = @"
CREATE TABLE IF NOT EXISTS collect.ag_replica_states (
    collection_id bigint NOT NULL,
    collection_time timestamp NOT NULL,
    server_id integer NOT NULL,
    server_name text NOT NULL,
    ag_name text,
    replica_server_name text,
    role_desc text,
    operational_state_desc text,
    connected_state_desc text,
    recovery_health_desc text,
    synchronization_health_desc text,
    availability_mode_desc text,
    failover_mode_desc text,
    endpoint_url text
);

CREATE INDEX IF NOT EXISTS idx_ag_replica_states_time ON collect.ag_replica_states(server_id, collection_time);

CREATE TABLE IF NOT EXISTS collect.ag_database_replica_states (
    collection_id bigint NOT NULL,
    collection_time timestamp NOT NULL,
    server_id integer NOT NULL,
    server_name text NOT NULL,
    ag_name text,
    database_name text,
    replica_server_name text,
    is_local boolean,
    synchronization_state_desc text,
    last_hardened_lsn text,
    last_commit_lsn text,
    log_send_queue_size bigint,
    redo_queue_size bigint,
    log_send_rate bigint,
    redo_rate bigint,
    is_suspended boolean,
    suspend_reason_desc text,
    availability_mode_desc text,
    secondary_lag_seconds bigint
);

CREATE INDEX IF NOT EXISTS idx_ag_database_replica_states_time ON collect.ag_database_replica_states(server_id, collection_time);";

    /// <summary>
    /// V35 — the #991 Availability Group alert knobs on the singleton config_alert_settings row: the master
    /// switch for the AG alert family plus the two "AG Sync Fell Behind" triggers. The lag trigger ships ON at
    /// 300 seconds (a secondary five minutes behind is worth knowing about on any AG); the redo-queue trigger
    /// ships OFF at 0, because a healthy queue size is workload-specific and a shipped guess would page half
    /// the fleet. Same shape as V33: ADD COLUMN IF NOT EXISTS keeps it idempotent, and NOT NULL DEFAULT means a
    /// pre-V35 row reads correctly with no backfill. No ACL/provisioning change — config_alert_settings carries
    /// table-level grants (no column carve).
    /// </summary>
    private const string V35Sql = @"
ALTER TABLE config.config_alert_settings
    ADD COLUMN IF NOT EXISTS notify_ag_health boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS ag_lag_alert_seconds integer NOT NULL DEFAULT 300,
    ADD COLUMN IF NOT EXISTS ag_redo_queue_alert_kb bigint NOT NULL DEFAULT 0;";

    /// <summary>
    /// V36 — the AG latency columns (#991 addendum): the four commit/hardened/redone/received timestamps
    /// the reference project's query skips, plus the two server-computed drain-time estimates
    /// (queue ÷ rate ÷ 60, guarded against BIGINT integer division and a zero rate).
    /// <para>APPENDED, never inserted, and deliberately a SEPARATE migration rather than an edit to V34:
    /// V34's <c>CREATE TABLE IF NOT EXISTS</c> is a no-op on a store that already ran it, so widening V34
    /// in place would silently leave every already-migrated store (the field box included) six columns
    /// short while fresh installs got them — the exact drift the fresh-vs-upgraded shape pin exists to
    /// catch. <c>ADD COLUMN IF NOT EXISTS</c> appends physically, matching the order
    /// <see cref="PgSchemaGenerator.CreateTable"/> emits for a fresh store, so both provenances end up
    /// column-for-column identical (pinned by PgSchemaGeneratorTests, which reconstructs the current
    /// shape from V34 + V36 and compares it to the generator).</para>
    /// <para>Version 36 because 35 was taken by the concurrent AG-alerts work; both are now on dev, so the
    /// ladder is dense again.</para>
    /// </summary>
    private const string V36Sql = @"
ALTER TABLE collect.ag_database_replica_states
    ADD COLUMN IF NOT EXISTS last_commit_time timestamp,
    ADD COLUMN IF NOT EXISTS last_hardened_time timestamp,
    ADD COLUMN IF NOT EXISTS last_redone_time timestamp,
    ADD COLUMN IF NOT EXISTS last_received_time timestamp,
    ADD COLUMN IF NOT EXISTS est_redo_completion_time_min double precision,
    ADD COLUMN IF NOT EXISTS est_send_drain_time_min double precision;";

    /// <summary>
    /// V37 — two additive columns for #1696, in ONE migration because they ship together.
    ///
    /// <para><c>ag_replica_states.is_local</c> marks the row describing the replica the collector was
    /// connected to. Every replica in an AG is visible from every node, so a fully-monitored 3-node AG
    /// collects the same replica's state three times and previously reported one failover three times. The
    /// alert path uses this to pick one node's view. NULLABLE, unlike the settings column below: rows
    /// collected before this migration genuinely do not know, and a NULL must read as "unknown" rather than
    /// as "not local" — de-duplicating on a false negative would drop a real alert. The de-dup treats a
    /// snapshot with no known-local row as un-de-duplicable and keeps every row, which is the safe direction.</para>
    ///
    /// <para><c>config_alert_settings.ag_disconnect_refire_minutes</c> is the #1659 re-fire treatment for
    /// "AG Replica Disconnected", which until now was a pure edge: a replica that stayed disconnected for a
    /// week announced it once. NOT NULL DEFAULT 0 = off, so the shipped behavior is byte-for-byte the old
    /// edge-only one and nothing starts re-alerting on upgrade.</para>
    ///
    /// ADD COLUMN IF NOT EXISTS throughout, per the file's additive idiom; config.-qualified because the
    /// migrate session runs under search_path = collect, config, public.
    /// </summary>
    private const string V37Sql = @"
ALTER TABLE collect.ag_replica_states
    ADD COLUMN IF NOT EXISTS is_local boolean;

ALTER TABLE config.config_alert_settings
    ADD COLUMN IF NOT EXISTS ag_disconnect_refire_minutes integer NOT NULL DEFAULT 0;";

    /// <summary>
    /// V39 — the partial indexes behind the dimension GC's MEASURED bound (#1795). The GC prunes
    /// dimension content against the oldest SURVIVING digest-carrying fact row rather than an assumed
    /// horizon, which requires a <c>min(collection_time)</c> probe per dim-feeding table every sweep.
    /// Unindexed, that is an ordered walk from each hypertable's oldest chunk filtering out NULL-digest
    /// rows (every pre-#1767 row); with a partial index whose predicate EXACTLY matches the probe's
    /// WHERE clause, it is an index-edge read. One index per dim-feeding fact table, predicate =
    /// "carries any digest" (the OR of that table's digest columns from <c>PayloadDimensions.All</c> —
    /// pinned against the map by test, since a new dimension column would silently fall out of both the
    /// index and the probe together or neither). TimescaleDB propagates the index to existing and
    /// future chunks; <c>IF NOT EXISTS</c> keeps the migration idempotent; plain-PostgreSQL stores take
    /// the same DDL unchanged. Bare names resolve through the migrate session's
    /// <c>search_path = collect, config, public</c>.
    /// </summary>
    private const string V39Sql = @"
CREATE INDEX IF NOT EXISTS ix_query_stats_digest_floor
    ON query_stats (collection_time)
    WHERE query_text_digest IS NOT NULL OR query_plan_digest IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_procedure_stats_digest_floor
    ON procedure_stats (collection_time)
    WHERE query_plan_digest IS NOT NULL;";

    /// <summary>
    /// V40 — <c>config_alert_settings.blocking_wait_seconds_threshold</c>, the #1839 total-blocked-wait
    /// gate. A second, independent blocking threshold beside the existing count one, because a count
    /// cannot distinguish one session blocked for an hour from one blocked for a second.
    /// <para>NOT NULL DEFAULT 0 = OFF, so an upgraded store alerts byte-for-byte as before and nobody
    /// starts getting a new alert because they took an update — the same shipped-behavior-preserving
    /// choice V37's re-fire column made. <c>ADD COLUMN IF NOT EXISTS</c> per the file's additive idiom;
    /// config.-qualified because the migrate session runs under
    /// <c>search_path = collect, config, public</c>.</para>
    /// </summary>
    private const string V40Sql = @"
ALTER TABLE config.config_alert_settings
    ADD COLUMN IF NOT EXISTS blocking_wait_seconds_threshold integer NOT NULL DEFAULT 0;";

    /// <summary>
    /// V41 — the REAL Query Store interval identity on <c>query_store_stats</c> (#1841 tier 2):
    /// <c>runtime_stats_interval_id</c> (<c>sys.query_store_runtime_stats</c>' own interval key) and
    /// <c>interval_start_time_utc</c> (<c>sys.query_store_runtime_stats_interval.start_time</c>, converted
    /// to UTC at collection).
    /// <para>Query Store rows are CUMULATIVE per-interval snapshots and the collector re-fetches the OPEN
    /// interval every cycle, so every aggregate read must collapse an interval to its latest snapshot
    /// before summing. Tier 1 (#1845) did that on the <c>first_execution_time</c> PROXY because the schema
    /// exposed no interval key; this is the real one, and it is also the only honest x-axis for "when the
    /// work ran" — <c>collection_time</c> dates an interval to the cycle that last FETCHED it, one bucket
    /// late on Query Store's default 60-minute interval.</para>
    /// <para>Both nullable and appended (identical physical column order for the binary COPY whether fresh —
    /// V1 is generated from the current collector definition, which now includes them — or upgraded;
    /// <c>ADD COLUMN IF NOT EXISTS</c> no-ops on fresh). Deliberately NOT backfilled: rows already stored
    /// were collected without the identity and nothing can reconstruct it, so readers key on the real id
    /// only when present and fall back to the tier-1 proxy otherwise. The trailing
    /// <c>CREATE OR REPLACE VIEW</c> re-expands <c>v_query_store_stats</c>' pinned <c>SELECT *</c>
    /// (Postgres freezes it at CREATE; append-only ADDs keep the refresh legal), mirroring V15/V27/V28.
    /// Runs after V8, so the bare names resolve through <c>search_path = collect, config, public</c>.</para>
    /// </summary>
    private const string V41Sql = @"
ALTER TABLE query_store_stats ADD COLUMN IF NOT EXISTS runtime_stats_interval_id bigint;
ALTER TABLE query_store_stats ADD COLUMN IF NOT EXISTS interval_start_time_utc timestamp;
CREATE OR REPLACE VIEW v_query_store_stats AS SELECT * FROM query_store_stats;";

    /// <summary>
    /// V42 — PagerDuty webhook channel (#1943). Adds two columns to <c>config.config_notification</c>:
    /// <c>pagerduty_routing_key</c> (the 32-character Events API v2 integration key, stored as plaintext
    /// but column-REVOKEd from the read-only viewer role — same secret tier as Teams/Slack/Generic webhook
    /// URLs) and <c>pagerduty_use_eu_region</c> (boolean flag for EU data center endpoint). Both are
    /// non-null with safe defaults (empty string / false) so existing rows get valid values without a data
    /// migration. The routing key is carved from the viewer role's SELECT grant in
    /// <c>DarlingManagedRoles.ViewerRestrictedConfigTables</c> and <c>Darling/tools/provision-roles.sql</c>;
    /// the EU flag is non-secret and stays in the viewer's grant.
    /// </summary>
    private const string V42Sql = @"
ALTER TABLE config.config_notification
    ADD COLUMN IF NOT EXISTS pagerduty_routing_key text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS pagerduty_use_eu_region boolean NOT NULL DEFAULT FALSE;";

    /// <summary>
    /// V43 - PagerDuty proxy (#1945). The channel was the only webhook that could not route through a
    /// proxy, and its endpoints are fixed public URLs, so a locked-down network that proxies webhook
    /// egress could not use it at all. Non-secret like the sibling proxy columns (teams_proxy et al.),
    /// so it stays in the viewer role's SELECT grant.
    /// </summary>
    private const string V43Sql = @"
ALTER TABLE config.config_notification
    ADD COLUMN IF NOT EXISTS pagerduty_proxy text NOT NULL DEFAULT '';";

    /// <summary>
    /// V44 — per-server collector state that is NOT derivable from the collected rows, so it cannot be a
    /// MAX() over the collector's own table the way the <c>event_time</c> / <c>instance_id</c> watermarks
    /// are (#1962). One collector declares state today: <c>default_trace_events</c> records the trace FILE
    /// it read, and compares it next cycle to decide whether it can read only the current rollover file
    /// (the measured 5.0x steady-state saving) or must re-read the whole set because the trace rolled.
    /// The state cannot ride on the payload precisely because the cycles that need it most collect zero
    /// rows — a server whose default trace churns through 20 MB files without producing any CURATED event
    /// would never record a rollover, and so would never leave the expensive fallback.
    ///
    /// <para><b>Schema — <c>collect</c>, qualified.</b> Service-written state the operator never mutates,
    /// so it belongs in <c>collect</c> beside <c>analysis_state</c> (V19) rather than the <c>config</c>
    /// control plane; qualifying is belt-and-suspenders over the migrate session's
    /// <c>search_path = collect, config, public</c>, exactly like V19. NOT a hypertable: it is a keyed
    /// registry with one short row per (server, collector, key), and
    /// <see cref="!:TimescaleSupport.HypertableTables"/> is catalog-driven, so a non-collector table is
    /// excluded automatically. No <c>v_*</c> passthrough view — nothing outside the collector runner reads
    /// it. <c>CREATE TABLE IF NOT EXISTS</c>, so a re-run is a no-op; a fresh store reaches it in
    /// migration order like every other post-V8 addition. Lite's twin is <c>Schema.CreateCollectorStateTable</c>
    /// — same columns, same key.</para>
    /// </summary>
    private const string V44Sql = @"
CREATE TABLE IF NOT EXISTS collect.collector_state (
    server_id integer NOT NULL,
    collector_name text NOT NULL,
    state_key text NOT NULL,
    state_value text NOT NULL,
    updated_at timestamp NOT NULL,
    PRIMARY KEY (server_id, collector_name, state_key)
);";

    /// <summary>
    /// V47 — the #1951 ADR persistent version store table for stores that already exist. A fresh store
    /// gets this table from V1's <see cref="PgSchemaGenerator.GenerateFullSchema"/> (catalog-driven, so
    /// registering <c>PvsStatsCollector</c> in <c>CollectorCatalog.All</c> is the whole fresh-install
    /// story); an UPGRADED store ran V1 before the collector existed and would otherwise never get the
    /// table, so it is spelled out here column-for-column in the generator's emission order. The
    /// fresh-vs-upgraded shape pin in PgSchemaGeneratorTests compares the two, which is what keeps this
    /// block honest if the payload ever changes.
    ///
    /// <para>Column types are the generator's mapping of the definition's 23 <c>PayloadColumns</c>: Varchar →
    /// <c>text</c>, Integer → <c>integer</c>, SmallInt → <c>smallint</c>, BigInt → <c>bigint</c>, Boolean →
    /// <c>boolean</c>, Timestamp → <c>timestamp</c>, Decimal(19,2) → <c>numeric(19,2)</c>, behind the four
    /// standard prefix columns. Bare names resolve through the migrate session's
    /// <c>search_path = collect, config, public</c> (V8); the table is <c>collect</c>-qualified anyway,
    /// matching V44 and V34.</para>
    ///
    /// <para>The view statement is deliberately UNQUALIFIED while the table is <c>collect.</c>-qualified,
    /// which looks inconsistent and is not: it resolves through the migrate session's
    /// <c>search_path = collect, config, public</c> exactly like V10-V13's view statements, and the
    /// drift guard in DarlingObservabilityTests scans every migration for the bare
    /// <c>CREATE OR REPLACE VIEW v_x AS SELECT * FROM x</c> form. A <c>collect.</c>-qualified view would
    /// be INVISIBLE to that guard — the guard that exists so a collector view can never be added without
    /// its V14 refresh — and would drop out of <c>PgSchemaGenerator.AllPassthroughViews</c>, which the MCP
    /// reader consults to answer "does this table have a v_* view".</para>
    ///
    /// <para>The <c>v_pvs_stats</c> passthrough view is what keeps the two viewers' SQL byte-identical.
    /// Lite's <c>v_*</c> views are load-bearing there — they UNION the hot DuckDB table with the parquet
    /// archive — and Darling's are the passthrough twin created for exactly that reason (V4/V5:
    /// "completing the v_* twin of Lite's DuckDB view layer so every ported viewer query stays
    /// byte-identical to Lite's"). Without it the Darling FinOps read would have to name the base table
    /// and the two front ends would diverge in their first line. The AG collectors read base tables
    /// directly and are the exception, not the pattern to copy for a grid twinned with Lite's.</para>
    ///
    /// <para>Everything else is automatic and deliberately NOT written here: the hypertable conversion,
    /// compression policy and retention come from <see cref="!:TimescaleSupport"/>, which enumerates
    /// <c>CollectorCatalog.All</c>, so a new collector table is converted on the next service start
    /// without a hand-written <c>create_hypertable</c> — the same path ag_replica_states took in V34.</para>
    /// </summary>
    private const string V47Sql = @"
CREATE TABLE IF NOT EXISTS collect.pvs_stats (
    collection_id bigint NOT NULL,
    collection_time timestamp NOT NULL,
    server_id integer NOT NULL,
    server_name text NOT NULL,
    database_name text,
    database_id integer,
    is_accelerated_database_recovery_on boolean,
    pvs_filegroup_id smallint,
    persistent_version_store_size_mb numeric(19,2),
    online_index_version_store_size_mb numeric(19,2),
    database_data_size_mb numeric(19,2),
    current_aborted_transaction_count bigint,
    oldest_active_transaction_id bigint,
    oldest_aborted_transaction_id bigint,
    min_transaction_timestamp bigint,
    online_index_min_transaction_timestamp bigint,
    secondary_low_water_mark bigint,
    offrow_version_cleaner_start_time timestamp,
    offrow_version_cleaner_end_time timestamp,
    aborted_version_cleaner_start_time timestamp,
    aborted_version_cleaner_end_time timestamp,
    pvs_off_row_page_skipped_low_water_mark bigint,
    pvs_off_row_page_skipped_transaction_not_cleaned bigint,
    pvs_off_row_page_skipped_oldest_active_xdesid bigint,
    pvs_off_row_page_skipped_min_useful_xts bigint,
    pvs_off_row_page_skipped_oldest_snapshot bigint,
    pvs_off_row_page_skipped_oldest_aborted_xdesid bigint
);

CREATE INDEX IF NOT EXISTS idx_pvs_stats_time ON collect.pvs_stats(server_id, collection_time);

CREATE OR REPLACE VIEW v_pvs_stats AS SELECT * FROM pvs_stats;";

    /// <summary>
    /// V48 — the #1984 PVS-pressure alert knobs on the singleton config_alert_settings row: an enable,
    /// the percent-of-database trigger, and the GB floor qualifier (AND semantics — see AlertsConfig).
    /// <para>Defaults ON at 40% / 1 GB, matching darling.json's <c>AlertsConfig</c> defaults — the
    /// #754 shipped-on precedent for a brand-new percent-based alert, NOT V40's default-off (that was a
    /// second gate on an EXISTING alert, where a non-zero default would have changed behavior someone had
    /// already tuned). 40 warns meaningfully before MS's "close to 50% of the database size" = large.
    /// <c>ADD COLUMN IF NOT EXISTS</c> per the file's additive idiom; config.-qualified because the
    /// migrate session runs under <c>search_path = collect, config, public</c>.</para>
    /// </summary>
    private const string V48Sql = @"
ALTER TABLE config.config_alert_settings
    ADD COLUMN IF NOT EXISTS pvs_enabled boolean NOT NULL DEFAULT TRUE;
ALTER TABLE config.config_alert_settings
    ADD COLUMN IF NOT EXISTS pvs_threshold_percent integer NOT NULL DEFAULT 40;
ALTER TABLE config.config_alert_settings
    ADD COLUMN IF NOT EXISTS pvs_floor_gb integer NOT NULL DEFAULT 1;";

    /// <summary>
    /// V49 — the database-state alert. Two tables plus a settings column:
    /// <para><c>collect.database_states</c> — the periodic per-database <c>state_desc</c> time series
    /// the alert compares against. Added additively exactly like V34/V44: a fresh store already has it
    /// (V1's <see cref="PgSchemaGenerator.GenerateFullSchema"/> walks the collector catalog, which now
    /// includes <see cref="PerformanceMonitor.Collectors.DatabaseStateCollector"/>), so
    /// <c>CREATE TABLE IF NOT EXISTS</c> is a no-op on fresh and the real create on upgrade. Column
    /// order/types are exactly <see cref="PgSchemaGenerator.CreateTable"/>'s output for that catalog
    /// entry (prefix NOT NULL, payload nullable) so the fresh-vs-upgraded shape pin holds. It gets the
    /// default retrieval index. No <c>v_*</c> passthrough view (post-V14 collectors read the base table).</para>
    /// <para><c>config.database_state_expected</c> — the per-(server, database) expected state the
    /// baseline-deviation alert compares the current state against: auto-seeded from first observation
    /// (non-critical states only — a critical first observation stays pending and alerts) and
    /// user-editable (the override), with the <c>(ignore)</c> sentinel opting a database out. Lives in the
    /// config control plane; the viewer role's SELECT grant is added in
    /// <c>Darling/tools/provision-roles.sql</c> / <c>DarlingManagedRoles</c>.</para>
    /// <para><c>config.config_alert_settings.database_state_enabled</c> — the master toggle, NOT NULL
    /// DEFAULT true so an upgraded store enables it without a data migration.</para>
    /// </summary>
    private const string V49Sql = @"
CREATE TABLE IF NOT EXISTS collect.database_states (
    collection_id bigint NOT NULL,
    collection_time timestamp NOT NULL,
    server_id integer NOT NULL,
    server_name text NOT NULL,
    database_name text,
    database_id integer,
    state_desc text,
    is_in_standby boolean
);

CREATE INDEX IF NOT EXISTS idx_database_states_time ON collect.database_states(server_id, collection_time);

CREATE TABLE IF NOT EXISTS config.database_state_expected (
    server_id integer NOT NULL,
    database_name text NOT NULL,
    expected_state text NOT NULL,
    is_user_override boolean NOT NULL DEFAULT false,
    updated_at timestamp NOT NULL DEFAULT (now() AT TIME ZONE 'UTC'),
    PRIMARY KEY (server_id, database_name)
);

ALTER TABLE config.config_alert_settings
    ADD COLUMN IF NOT EXISTS database_state_enabled boolean NOT NULL DEFAULT true;";

    /// <summary>
    /// V50 — a nullable colour for each server tag (#2008 stage 2a). Additive exactly like V33/V48: one
    /// <c>ADD COLUMN IF NOT EXISTS</c> on <c>config.server_tags</c> (created back in V32), so it is a no-op
    /// on a store already carrying it and the real add on an upgrade. There is no config-schema generator
    /// for server_tags — the table exists only because V32 created it — so a fresh store reaches this column
    /// by running V32 then V50 in order, the same path an upgraded store takes; nothing else needs editing.
    /// <para>Deliberately nullable with NO backfill: existing tags stay NULL and render as a neutral pill
    /// until a user picks a colour, while newly-created tags get a palette colour assigned at creation time
    /// (rotated by tag id, in the viewer). Stored as <c>#RRGGBB</c> text — the viewer's only concern, the
    /// service never reads server_tags — so no CHECK constraint is imposed here; the viewer writes only
    /// palette values or a user pick.</para>
    /// </summary>
    private const string V50Sql = @"
ALTER TABLE config.server_tags
    ADD COLUMN IF NOT EXISTS colour text;";

    /// <summary>
    /// V51 — the statement's HOST OBJECT on query_stats (#2012 stage 2): <c>sys.dm_exec_sql_text.objectid</c>
    /// resolved to schema.name at collection, NULL for ad-hoc/prepared text. This is what lets the
    /// hash-grouped readers split <c>INSERT...EXEC</c> callers that share a <c>query_hash</c> (the hash
    /// normalizes the callee away — reproduced and mis-attributed in live triage) while leaving the ad-hoc
    /// literal-collapse behavior untouched (NULLs group as one). Appended LAST to match the collector's
    /// append-only payload; nullable, no backfill — history rows stay NULL and read as "unknown host",
    /// aging out with raw retention. TimescaleDB accepts a nullable ADD COLUMN on a compressed hypertable.
    /// <para><c>v_query_stats</c> is NOT a passthrough on a V38+ store — it is the #1767 payload-RESOLVING
    /// view (<see cref="PgSchemaGenerator.GenerateQueryStatsResolvingView"/>), so it must be rebuilt from
    /// the generator, not replaced with <c>SELECT *</c> (which would silently return NULL query_text for
    /// every digest-era row). And because the generator emits payload columns BEFORE the trailing digest
    /// columns, the new column lands mid-list — an alteration <c>CREATE OR REPLACE VIEW</c> refuses
    /// (append-at-end only) — hence DROP + recreate. Plain DROP, no CASCADE: nothing persistent depends
    /// on the view; readers reference it per-query. The migration entry concatenates the regenerated view
    /// after this constant, the V38 idiom, so the definition can never go stale here.</para>
    /// </summary>
    private const string V51Sql = @"
ALTER TABLE query_stats
    ADD COLUMN IF NOT EXISTS host_object_name text;
DROP VIEW IF EXISTS v_query_stats;";

    /// <summary>
    /// V52 — the persisted finding drill-down (#2060): the evidence rows behind a finding
    /// (parameter-sensitive plans, top spill queries, blocking chains) previously existed only on
    /// the write path, so <c>get_analysis_findings</c> could say "13 plans showed the pattern"
    /// while no surface could enumerate them. Additive nullable text column carrying the CAPPED
    /// JSON (<c>DrillDownSerializer</c>: rows-per-section + total-size caps, explicit truncation
    /// note — never a silent cap), written beside <c>remediation_action_json</c> with the same D2
    /// rationale and read back into the finding. No backfill: pre-upgrade findings honestly return
    /// no drill-down and age out with finding retention. A fresh store's V4 creates the table
    /// without the column and this ALTER adds it later in the same ladder run — the V9 idiom.
    /// </summary>
    private const string V52Sql = @"
ALTER TABLE analysis_findings
    ADD COLUMN IF NOT EXISTS drill_down_json text;";

    /// <summary>
    /// V9 — the FinOps copy-parity fields that were user-input config or previously live-only:
    /// <c>server_properties</c> gains the three inventory columns the shared ServerPropertiesCollector now
    /// SELECTs (start time / host OS / AG replica role — Lite's FinOps Server Inventory previously read
    /// them from a LIVE query the headless viewer can't run), and <c>servers</c> gains
    /// <c>monthly_cost_usd</c> (the per-server FinOps budget — Lite's <c>ServerConnection.MonthlyCostUsd</c>,
    /// user config, upserted from darling.json). All are nullable appended columns: a fresh store's V1
    /// server_properties is generated from the current collector (which already includes the three), and
    /// V2's servers table gets the cost column here — so <c>ADD COLUMN IF NOT EXISTS</c> is a harmless
    /// no-op on the generated columns and the real add on an upgraded store. Appended (not inserted) so a
    /// fresh V1 store and an upgraded store keep an identical physical column order for the binary COPY.
    /// The bare names resolve through the migrate session's <c>search_path = collect, config, public</c>
    /// (V8) to <c>collect.server_properties</c> / <c>collect.servers</c>.
    /// </summary>
    private const string V9Sql = @"
ALTER TABLE server_properties ADD COLUMN IF NOT EXISTS sqlserver_start_time timestamp;
ALTER TABLE server_properties ADD COLUMN IF NOT EXISTS host_os_version text;
ALTER TABLE server_properties ADD COLUMN IF NOT EXISTS ag_replica_role text;
ALTER TABLE servers ADD COLUMN IF NOT EXISTS monthly_cost_usd numeric;";

    /// <summary>
    /// V15 — the per-index DEFINITION metadata monitor-side UNUSED/DUPLICATE index analysis needs
    /// (FinOps Index Analysis, Stage 1), added additively to <c>index_object_stats</c>: the ordered
    /// <c>key_columns</c> / <c>included_columns</c> lists (sp_IndexCleanup's delimited representation,
    /// so the Stage-2 analyzer's string-comparison dedupe ports cleanly), <c>filter_definition</c>,
    /// the uniqueness/constraint/FK discriminators (<c>is_unique_constraint</c>, <c>is_foreign_key</c>,
    /// <c>is_foreign_key_reference</c>) + <c>is_disabled</c>, and the reconstruct-a-CREATE options
    /// (<c>data_compression_desc</c>, <c>optimize_for_sequential_key</c>, <c>fill_factor</c>,
    /// <c>is_padded</c>, <c>allow_page_locks</c>, <c>allow_row_locks</c>). Every column is nullable and
    /// appended, so a fresh store's V1 <c>index_object_stats</c> (generated from the current collector
    /// definition, which now includes them) already has them and <c>ADD COLUMN IF NOT EXISTS</c>
    /// no-ops, while an upgraded store gets the real add — with an identical physical column order for
    /// the binary COPY either way. The trailing <c>CREATE OR REPLACE VIEW</c> re-expands
    /// <c>v_index_object_stats</c>' pinned <c>SELECT *</c> (Postgres freezes it at CREATE, so an
    /// upgraded store's view — last refreshed by V14 before these columns existed — would otherwise
    /// omit them; append-only ADDs keep the refresh legal). Runs after V8, so the bare names resolve
    /// through <c>search_path = collect, config, public</c>.
    /// </summary>
    private const string V15Sql = @"
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS key_columns text;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS included_columns text;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS filter_definition text;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_unique_constraint boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_foreign_key boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_foreign_key_reference boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_disabled boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS data_compression_desc text;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS optimize_for_sequential_key boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS fill_factor smallint;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_padded boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS allow_page_locks boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS allow_row_locks boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_indexed_view boolean;

CREATE OR REPLACE VIEW v_index_object_stats AS SELECT * FROM index_object_stats;";

    /// <summary>
    /// V16 — the monitored server's UTC offset, added additively to <c>server_properties</c> so the
    /// headless viewer can render timestamps in the server's own local time (the Server-time display
    /// mode ported from Lite). The store is naive-UTC; Server-time = UTC + this offset. It is nullable
    /// and appended, so a fresh store's V1 <c>server_properties</c> (generated from the current collector
    /// definition, which now includes it) already has it and <c>ADD COLUMN IF NOT EXISTS</c> no-ops,
    /// while an upgraded store gets the real add — with an identical physical column order for the binary
    /// COPY either way. <c>server_properties</c> has no <c>v_*</c> passthrough view, so nothing to refresh.
    /// Runs after V8, so the bare name resolves through <c>search_path = collect, config, public</c>.
    /// </summary>
    private const string V16Sql = @"
ALTER TABLE server_properties ADD COLUMN IF NOT EXISTS utc_offset_minutes integer;";

    /// <summary>
    /// V17 — the store&lt;-&gt;service control plane (Phase A, Stage 1): the six operator-writable
    /// <c>config.*</c> tables the Viewer will WRITE and the service READS + honors on the
    /// <c>config_version</c> reload beacon. Two directions over the V8 <c>config</c> schema (the
    /// admin-writable surface): the config plane (desired state — monitored servers, alert /
    /// analysis knobs, notification delivery, per-collector schedule overrides, service flags) and
    /// the command plane (<c>config_command</c>, the imperative queue Stage 2 executes).
    ///
    /// <para><b>CRITICAL — every object is schema-qualified <c>config.&lt;name&gt;</c>.</b> V17 is
    /// the first migration to CREATE directly in <c>config</c>; the migrate session runs under
    /// <c>search_path = collect, config, public</c> (<see cref="PgSchemaGenerator.SearchPath"/>), so
    /// an UNQUALIFIED <c>CREATE TABLE foo</c> would resolve to <c>collect</c> (first in the path) —
    /// wrong schema, wrong ACL (the V8 split grants config-writes to <c>admin</c> only). Qualifying
    /// every table/index/function/trigger with <c>config.</c> pins them into the admin-writable
    /// schema. The managed role provisioning (<c>DarlingManagedRoles</c>) and BYO
    /// <c>tools/provision-roles.sql</c> both <c>GRANT … ON ALL TABLES IN SCHEMA config</c> after
    /// migration + carry <c>ALTER DEFAULT PRIVILEGES</c>, so these new tables auto-inherit the
    /// admin/viewer grants with no per-table grant; <c>config_command</c> uses
    /// <c>GENERATED ALWAYS AS IDENTITY</c> (not <c>serial</c>) so INSERTs need no sequence USAGE.
    ///
    /// <para>The <c>config_version</c> reload beacon: statement-level bump triggers on the four
    /// desired-state tables increment <c>config_service.config_version</c> on any write (so the
    /// Viewer cannot forget to signal), and a BEFORE-UPDATE trigger on <c>config_service</c> itself
    /// self-increments the beacon on direct writes (pause/capture/mcp) without recursing — the
    /// service polls this one integer each sweep and reloads only when it changes. Single-row global
    /// tables are pinned to <c>id = 1</c>; <c>config_collector_schedules</c> is sparse (absent row /
    /// NULL column = the <c>CollectorScheduleDefaults</c> code default, <c>server_id</c> NULL =
    /// fleet-wide) with two partial-unique indexes because a PRIMARY KEY cannot span a nullable
    /// <c>server_id</c>. Timestamps store naive-UTC (<c>now() AT TIME ZONE 'UTC'</c>) to match the
    /// store-wide convention (Npgsql rejects Kind=Utc against <c>timestamp</c>). Server secrets are
    /// NEVER plaintext here — <c>encrypted_password</c>/<c>smtp_encrypted_password</c> are the
    /// DPAPI blobs (the <c>--encrypt-password</c> pattern); integrated auth needs none.</para>
    /// </summary>
    private const string V17Sql = @"
/* V17: store<->service control plane (Stage 1). EVERY object is schema-qualified config.* —
   the migrate session's search_path resolves bare names to collect (wrong schema/ACL). */

/* --- A. Config plane: the Viewer writes desired state, the service reads + honors it. --- */

/* 1. config_monitored_servers — the desired-state twin of the collect.servers observed registry.
      server_id = ServerIdHelper.GetDeterministicHashCode(BuildStorageName(host,database,ro)), the
      SAME identity the collectors stamp, so it JOINs collected data. is_enabled drives collection;
      the connection fields reconstruct a MonitoredServer for the service's connect path. */
CREATE TABLE IF NOT EXISTS config.config_monitored_servers (
    server_id integer NOT NULL PRIMARY KEY,
    name text NOT NULL,
    host text NOT NULL,
    database text,
    auth text NOT NULL DEFAULT 'integrated',
    username text,
    encrypted_password text,
    encrypt_mode text NOT NULL DEFAULT 'Mandatory',
    trust_server_certificate boolean NOT NULL DEFAULT FALSE,
    read_only_intent boolean NOT NULL DEFAULT FALSE,
    multi_subnet_failover boolean NOT NULL DEFAULT FALSE,
    excluded_databases text[] NOT NULL DEFAULT '{}'::text[],
    monthly_cost_usd numeric NOT NULL DEFAULT 0,
    capture_plans boolean,
    is_enabled boolean NOT NULL DEFAULT TRUE,
    created_at timestamp NOT NULL DEFAULT (now() AT TIME ZONE 'UTC'),
    modified_at timestamp NOT NULL DEFAULT (now() AT TIME ZONE 'UTC')
);

/* 2. config_alert_settings — single row (id=1), one column per AlertsConfig field + the analysis
      cadence knobs (analysis_enabled / interval / notifications_enabled / notify_severity). */
CREATE TABLE IF NOT EXISTS config.config_alert_settings (
    id smallint NOT NULL PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    enabled boolean NOT NULL DEFAULT TRUE,
    cpu_enabled boolean NOT NULL DEFAULT TRUE,
    cpu_threshold_percent integer NOT NULL DEFAULT 80,
    cpu_mode text NOT NULL DEFAULT 'total',
    blocking_enabled boolean NOT NULL DEFAULT TRUE,
    blocking_count_threshold integer NOT NULL DEFAULT 1,
    deadlock_enabled boolean NOT NULL DEFAULT TRUE,
    deadlock_count_threshold integer NOT NULL DEFAULT 1,
    poison_wait_enabled boolean NOT NULL DEFAULT TRUE,
    poison_wait_threshold_ms integer NOT NULL DEFAULT 500,
    long_running_query_enabled boolean NOT NULL DEFAULT TRUE,
    long_running_query_threshold_minutes integer NOT NULL DEFAULT 30,
    tempdb_space_enabled boolean NOT NULL DEFAULT TRUE,
    tempdb_space_threshold_percent integer NOT NULL DEFAULT 80,
    low_disk_enabled boolean NOT NULL DEFAULT TRUE,
    low_disk_threshold_percent integer NOT NULL DEFAULT 10,
    low_disk_threshold_gb integer NOT NULL DEFAULT 5,
    long_running_job_enabled boolean NOT NULL DEFAULT TRUE,
    long_running_job_multiplier integer NOT NULL DEFAULT 3,
    failed_job_enabled boolean NOT NULL DEFAULT TRUE,
    failed_job_lookback_minutes integer NOT NULL DEFAULT 60,
    cooldown_minutes integer NOT NULL DEFAULT 5,
    excluded_databases text[] NOT NULL DEFAULT '{}'::text[],
    analysis_enabled boolean NOT NULL DEFAULT TRUE,
    analysis_interval_minutes integer NOT NULL DEFAULT 30,
    analysis_notifications_enabled boolean NOT NULL DEFAULT TRUE,
    analysis_notify_severity double precision NOT NULL DEFAULT 1.5,
    modified_at timestamp NOT NULL DEFAULT (now() AT TIME ZONE 'UTC')
);

/* 3. config_notification — single row: SMTP + webhook delivery. Non-secret fields plus the SMTP
      DPAPI blob (smtp_encrypted_password); webhook URLs/proxies carry as darling.json holds them. */
CREATE TABLE IF NOT EXISTS config.config_notification (
    id smallint NOT NULL PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    smtp_host text NOT NULL DEFAULT '',
    smtp_port integer NOT NULL DEFAULT 587,
    smtp_use_ssl boolean NOT NULL DEFAULT TRUE,
    smtp_username text,
    smtp_encrypted_password text,
    smtp_from_address text NOT NULL DEFAULT '',
    smtp_recipients text NOT NULL DEFAULT '',
    email_cooldown_minutes integer NOT NULL DEFAULT 15,
    teams_url text NOT NULL DEFAULT '',
    teams_proxy text NOT NULL DEFAULT '',
    slack_url text NOT NULL DEFAULT '',
    slack_proxy text NOT NULL DEFAULT '',
    modified_at timestamp NOT NULL DEFAULT (now() AT TIME ZONE 'UTC')
);

/* 4. config_collector_schedules — SPARSE per-collector overrides layered on CollectorScheduleDefaults.
      Absent row / NULL column = code default; server_id NULL = fleet-wide. A PRIMARY KEY cannot span a
      nullable server_id, so two partial-unique indexes enforce one fleet-wide row + one per-server row
      per collector. */
CREATE TABLE IF NOT EXISTS config.config_collector_schedules (
    server_id integer,
    collector_name text NOT NULL,
    frequency_minutes integer CHECK (frequency_minutes >= 0),
    retention_days integer CHECK (retention_days >= 1),
    enabled boolean NOT NULL DEFAULT TRUE
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_config_collector_schedules_fleet
    ON config.config_collector_schedules (collector_name) WHERE server_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_config_collector_schedules_server
    ON config.config_collector_schedules (server_id, collector_name) WHERE server_id IS NOT NULL;

/* 5. config_service — single row: the service-wide flags + the config_version reload beacon. */
CREATE TABLE IF NOT EXISTS config.config_service (
    id smallint NOT NULL PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    paused boolean NOT NULL DEFAULT FALSE,
    capture_plans boolean NOT NULL DEFAULT TRUE,
    mcp_enabled boolean NOT NULL DEFAULT FALSE,
    mcp_port integer NOT NULL DEFAULT 5152,
    config_version bigint NOT NULL DEFAULT 0,
    updated_at timestamp NOT NULL DEFAULT (now() AT TIME ZONE 'UTC'),
    updated_by text
);

/* --- B. Command plane: the Viewer enqueues, the service (Stage 2) claims/executes/reports. --- */

/* 6. config_command — the imperative queue. GENERATED ALWAYS AS IDENTITY (a natural queue key that
      needs no sequence USAGE grant for admin INSERTs, unlike serial). */
CREATE TABLE IF NOT EXISTS config.config_command (
    command_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    created_at timestamp NOT NULL DEFAULT (now() AT TIME ZONE 'UTC'),
    requested_by text,
    command_type text NOT NULL,
    target_server_id integer,
    args_json jsonb,
    status text NOT NULL DEFAULT 'pending',
    claimed_at timestamp,
    completed_at timestamp,
    result_status text,
    result_json jsonb,
    service_instance text
);
CREATE INDEX IF NOT EXISTS idx_config_command_status ON config.config_command (status);

/* --- C. The config_version reload beacon (bump triggers). --- */

/* Statement-level bump on the four desired-state tables: any write increments the beacon so the
   Viewer can never forget to signal. Targets config_service by qualified name (SECURITY INVOKER —
   both writers, the owner during seed and admin via the Viewer, hold UPDATE on config_service). */
CREATE OR REPLACE FUNCTION config.config_bump_version() RETURNS trigger
LANGUAGE plpgsql AS $bump$
BEGIN
    UPDATE config.config_service
       SET config_version = config_version + 1,
           updated_at = (now() AT TIME ZONE 'UTC')
     WHERE id = 1;
    RETURN NULL;
END;
$bump$;

/* Direct writes to config_service (pause/capture/mcp) self-bump the beacon without recursion: the
   BEFORE-UPDATE trigger increments NEW.config_version only when the writer did not already change it
   (so the config_bump_version UPDATE above, which sets config_version explicitly, is not doubled). */
CREATE OR REPLACE FUNCTION config.config_service_bump() RETURNS trigger
LANGUAGE plpgsql AS $svc$
BEGIN
    IF NEW.config_version = OLD.config_version THEN
        NEW.config_version := OLD.config_version + 1;
    END IF;
    NEW.updated_at := (now() AT TIME ZONE 'UTC');
    RETURN NEW;
END;
$svc$;

DROP TRIGGER IF EXISTS trg_bump_monitored_servers ON config.config_monitored_servers;
CREATE TRIGGER trg_bump_monitored_servers
    AFTER INSERT OR UPDATE OR DELETE ON config.config_monitored_servers
    FOR EACH STATEMENT EXECUTE FUNCTION config.config_bump_version();

DROP TRIGGER IF EXISTS trg_bump_alert_settings ON config.config_alert_settings;
CREATE TRIGGER trg_bump_alert_settings
    AFTER INSERT OR UPDATE OR DELETE ON config.config_alert_settings
    FOR EACH STATEMENT EXECUTE FUNCTION config.config_bump_version();

DROP TRIGGER IF EXISTS trg_bump_notification ON config.config_notification;
CREATE TRIGGER trg_bump_notification
    AFTER INSERT OR UPDATE OR DELETE ON config.config_notification
    FOR EACH STATEMENT EXECUTE FUNCTION config.config_bump_version();

DROP TRIGGER IF EXISTS trg_bump_collector_schedules ON config.config_collector_schedules;
CREATE TRIGGER trg_bump_collector_schedules
    AFTER INSERT OR UPDATE OR DELETE ON config.config_collector_schedules
    FOR EACH STATEMENT EXECUTE FUNCTION config.config_bump_version();

DROP TRIGGER IF EXISTS trg_service_self_bump ON config.config_service;
CREATE TRIGGER trg_service_self_bump
    BEFORE UPDATE ON config.config_service
    FOR EACH ROW EXECUTE FUNCTION config.config_service_bump();";

    /// <summary>
    /// V18 — the per-server + per-event alert delivery mode (#1236 / #1141) the Viewer writes and the
    /// service honors at delivery time. The GLOBAL default lives on <c>config_alert_settings</c>
    /// (<c>delivery_mode</c> = Summary/PerEvent, <c>per_event_max</c> = the "+N more" cap the shared
    /// <c>PerEventNotification.Split</c> applies); a PER-SERVER override lives on
    /// <c>config_monitored_servers</c> (<c>alert_delivery_mode_override</c>, nullable = "use the global"),
    /// resolved through the shared <c>AlertDeliveryModeResolver</c>. Additive ALTERs, schema-qualified
    /// <c>config.*</c> exactly like V17 (the migrate session's <c>search_path = collect, config, public</c>
    /// resolves a bare name to <c>collect</c> — the wrong schema/ACL); <c>IF NOT EXISTS</c> matches the
    /// file's ALTER idiom (V7/V9/V15/V16) so a re-run is a harmless no-op. The two settings columns are
    /// NOT NULL with the shipped defaults (Summary / 5) so the single seeded row comes up honoring Summary;
    /// the per-server column is nullable so an un-overridden server inherits the global. Neither table has a
    /// <c>v_*</c> passthrough view, so nothing to refresh.
    /// </summary>
    private const string V18Sql = @"
ALTER TABLE config.config_alert_settings ADD COLUMN IF NOT EXISTS delivery_mode text NOT NULL DEFAULT 'Summary';
ALTER TABLE config.config_alert_settings ADD COLUMN IF NOT EXISTS per_event_max integer NOT NULL DEFAULT 5;
ALTER TABLE config.config_monitored_servers ADD COLUMN IF NOT EXISTS alert_delivery_mode_override text;";

    /// <summary>
    /// V19 — the per-server ANALYSIS-STATE marker: the analysis pass's insufficient-data determination,
    /// persisted so the Viewer's Recommendations tab can tell a young deployment ("still collecting —
    /// need ~24h of history") apart from a genuine all-clear. The engine ALREADY computes this
    /// (<c>DarlingAnalysisService.InsufficientDataMessage</c>, surfaced as the AN3 pass's
    /// <c>InsufficientData</c> status when total history is under the 24h data-span gate); the Viewer
    /// makes no engine call, so without a persisted marker a zero-finding read collapses to "All clear"
    /// even when the engine only skipped for want of data. One row per server (<c>server_id</c> PRIMARY
    /// KEY), upserted by the service after each completed pass and read by the Viewer.
    ///
    /// <para><b>Schema — <c>collect</c>, qualified.</b> This is service-produced OBSERVED analysis output
    /// the Viewer READS, so it belongs in <c>collect</c> beside <c>analysis_findings</c> /
    /// <c>collection_log</c> / <c>servers</c> — NOT the <c>config</c> control plane (which is the opposite
    /// direction: the Viewer writes DESIRED state the service reads). The service connects as the owner
    /// and writes it; the managed <c>admin</c>/<c>viewer</c> roles auto-inherit SELECT via
    /// <c>ALTER DEFAULT PRIVILEGES … IN SCHEMA collect</c> (<see cref="!:DarlingManagedRoles"/>), so no
    /// per-table grant is needed. Qualifying <c>collect.</c> is belt-and-suspenders — the migrate
    /// session's <c>search_path = collect, config, public</c> already resolves a bare name to
    /// <c>collect</c> — but it makes the intent explicit (the mirror of V17/V18's <c>config.</c>
    /// qualification). <c>message</c> is nullable (null when a real pass cleared the gate);
    /// <c>analysis_time</c> stores naive-UTC like the store-wide convention. No <c>v_*</c> passthrough
    /// view: no Lite SQL ports through this Darling-specific marker.
    /// </summary>
    private const string V19Sql = @"
CREATE TABLE IF NOT EXISTS collect.analysis_state (
    server_id integer NOT NULL PRIMARY KEY,
    insufficient_data boolean NOT NULL DEFAULT FALSE,
    message text,
    analysis_time timestamp NOT NULL
);";

    /// <summary>
    /// V20 — the alert-tuning knobs the Viewer writes and the service honors that had no store column before:
    /// the long-running-query read shape (<c>long_running_query_max_results</c> + the five noise-filter opt-outs
    /// the shared <c>AlertEngine</c> forwards to <c>GetLongRunningQueriesAsync</c>) and
    /// <c>notify_connection_changes</c> (the Server-Unreachable/Restored connect-edge gate in
    /// <see cref="!:DarlingSelfAlertEvaluator"/>). Before this the service HARDCODED all of them (Lite's App
    /// defaults: max 5, every filter on, connection-notify on), so suppression happened by default but the
    /// operator could not customize it — the Settings controls were inert. All columns are additive
    /// <c>ADD COLUMN IF NOT EXISTS</c> and NOT NULL with the shipped defaults, so a pre-V20 store's single
    /// seeded row comes up honoring exactly the old hardcoded behavior and a re-run is a harmless no-op.
    /// Schema-qualified <c>config.*</c> exactly like V17/V18 (the migrate session's
    /// <c>search_path = collect, config, public</c> resolves a bare name to <c>collect</c> — the wrong
    /// schema/ACL). <c>config_alert_settings</c> has no <c>v_*</c> passthrough view, so nothing to refresh.
    /// </summary>
    private const string V20Sql = @"
ALTER TABLE config.config_alert_settings ADD COLUMN IF NOT EXISTS long_running_query_max_results integer NOT NULL DEFAULT 5;
ALTER TABLE config.config_alert_settings ADD COLUMN IF NOT EXISTS long_running_query_exclude_sp_server_diagnostics boolean NOT NULL DEFAULT TRUE;
ALTER TABLE config.config_alert_settings ADD COLUMN IF NOT EXISTS long_running_query_exclude_wait_for boolean NOT NULL DEFAULT TRUE;
ALTER TABLE config.config_alert_settings ADD COLUMN IF NOT EXISTS long_running_query_exclude_backups boolean NOT NULL DEFAULT TRUE;
ALTER TABLE config.config_alert_settings ADD COLUMN IF NOT EXISTS long_running_query_exclude_misc_waits boolean NOT NULL DEFAULT TRUE;
ALTER TABLE config.config_alert_settings ADD COLUMN IF NOT EXISTS long_running_query_exclude_cdc boolean NOT NULL DEFAULT TRUE;
ALTER TABLE config.config_alert_settings ADD COLUMN IF NOT EXISTS notify_connection_changes boolean NOT NULL DEFAULT TRUE;";

    /// <summary>
    /// V21 — the <c>default_trace_events</c> collector table (built-in Default Trace read via
    /// <c>sys.fn_trace_gettable</c>: file auto-grow/shrink stalls, severe ErrorLog writes, schema DDL,
    /// security audits, Server Memory Change). A NEW collector table added additively for a store built
    /// before this collector existed; a FRESH store already has it (V1's
    /// <see cref="PgSchemaGenerator.GenerateFullSchema"/> now walks the collector — which includes
    /// default_trace_events — and V8 moved it to <c>collect</c>), so <c>CREATE TABLE IF NOT EXISTS</c> is a
    /// harmless no-op on fresh and the real create on upgrade, with an identical <c>collect.default_trace_events</c>
    /// shape either way. EXPLICITLY <c>collect.</c>-qualified (mirroring V19's <c>analysis_state</c>): this
    /// runs after V8, whose <c>search_path = collect, config, public</c> already resolves a bare name to
    /// <c>collect</c>, but the qualification makes the collect-schema intent explicit. Column order + types
    /// mirror the generated V1 shape exactly (the NOT NULL prefix, no PRIMARY KEY — the hypertable / binary
    /// COPY reasoning), so the positional COPY aligns on both paths.
    ///
    /// <para>It has NO <c>v_*</c> passthrough view (no Lite analysis SQL ports through it — the
    /// <c>get_default_trace_events</c> MCP tool reads the base table directly, exactly like
    /// <c>server_properties</c>), so the <see cref="PgSchemaGenerator.AllPassthroughViews"/> cross-check is
    /// unaffected. TimescaleDB hypertable conversion + compression + retention all flow from the catalog
    /// automatically (TimescaleSupport / DarlingRetention walk <see cref="CollectorCatalog.All"/>).</para>
    /// </summary>
    private const string V21Sql = @"
CREATE TABLE IF NOT EXISTS collect.default_trace_events (
    default_trace_event_id bigint NOT NULL,
    collection_time timestamp NOT NULL,
    server_id integer NOT NULL,
    server_name text NOT NULL,
    event_time timestamp,
    event_name text,
    event_class integer,
    spid integer,
    database_name text,
    database_id integer,
    login_name text,
    host_name text,
    application_name text,
    object_name text,
    filename text,
    integer_data bigint,
    integer_data_2 bigint,
    text_data text,
    session_login_name text,
    error_number integer,
    severity integer,
    state integer,
    event_sequence bigint,
    duration_us bigint,
    end_time timestamp
);

CREATE INDEX IF NOT EXISTS idx_default_trace_events_time ON collect.default_trace_events(server_id, collection_time);";

    /// <summary>
    /// V22 — the supporting index for the hot FinOps Index Analysis read. The viewer's
    /// <c>ViewerDataService.IndexObjectStatsLatestSql</c> picks the newest row per index identity with
    /// <c>SELECT DISTINCT ON (database_id, object_id, index_id) … WHERE server_id = $1
    /// ORDER BY database_id, object_id, index_id, collection_time DESC</c> — no time bound, so it walks the
    /// whole per-server history (daily cadence, 90-day retention) to find each index's latest snapshot. The V1
    /// index <c>idx_index_object_stats_object</c> leads <c>(server_id, database_name, …)</c> — a database_NAME
    /// vs the query's database_ID mismatch, so its ordering cannot satisfy the <c>DISTINCT ON (database_id, …)</c>
    /// and Postgres sorts the entire server row set. This index matches the read's exact
    /// <c>DISTINCT ON</c>/<c>ORDER BY</c> key (<c>server_id, database_id, object_id, index_id,
    /// collection_time DESC</c>), so the <c>DISTINCT ON</c> reads in index order with no sort. It is additive:
    /// it does NOT replace the V1 index (the anomaly-detector self-joins in <c>PgAnomalyDetector</c> key
    /// <c>database_name</c>, which that index still serves) — the two readers diverged on the database key, so
    /// each gets its own index.
    ///
    /// <para><c>index_object_stats</c> is a TimescaleDB hypertable (every collector table is —
    /// <c>TimescaleSupport.HypertableTables</c>), so a plain <c>CREATE INDEX</c> is applied across every chunk
    /// by Timescale (the standard post-hypertable index path, the same shape as the V1/V21 collector indexes);
    /// on a plain-PostgreSQL store it is an ordinary btree. It is a NON-unique index that includes the partition
    /// column (<c>collection_time</c>), so it is legal on the hypertable either way. <c>CREATE INDEX IF NOT
    /// EXISTS</c> makes it idempotent: an existing V21 store gets the real create, a fresh store (whose V1 built
    /// only the differently-keyed <c>idx_index_object_stats_object</c>) gets this second index when its
    /// migrations run through V22, and a re-run is a harmless no-op. EXPLICITLY <c>collect.</c>-qualified like
    /// V21 (the migrate session's <c>search_path = collect, config, public</c> already resolves the bare name
    /// to <c>collect</c>, but the qualification makes the collect-schema intent explicit). No table shape
    /// changes, so nothing to refresh for the binary COPY.</para>
    /// </summary>
    private const string V22Sql = @"
CREATE INDEX IF NOT EXISTS idx_index_object_stats_latest ON collect.index_object_stats (server_id, database_id, object_id, index_id, collection_time DESC);";

    /// <summary>
    /// V23 — converts <c>collect.collection_log</c> (the per-collector-run observability log, the store's
    /// highest-volume plain table) from a heap to a TimescaleDB hypertable, and applies the same compression
    /// policy the collector hypertables get. As a hypertable its daily retention becomes O(1)
    /// <c>drop_chunks</c> (no DELETE churn — see <see cref="!:DarlingRetention"/>), the Overview's per-server
    /// <c>MAX(collection_time)</c> freshness read touches only the newest chunk per server (chunk exclusion on
    /// the existing <c>idx_collection_log_time (server_id, collection_time)</c> index, kept), and old chunks
    /// compress — the scale-readiness pass for 500 servers.
    ///
    /// <para><b>Engine-plain, GUARDED, and NON-FATAL — a best-effort UPGRADE fast-path, NOT the authoritative
    /// conversion.</b> The versioned migrations must run on plain PostgreSQL too (the store works with or
    /// without TimescaleDB — see <see cref="TimescaleSupport"/>), where <c>create_hypertable</c> does not
    /// exist. So the body is a <c>DO</c> block gated on <c>pg_extension</c> (plain PostgreSQL skips it — plpgsql
    /// parses the guarded statements lazily, so the missing function is never parsed) AND wrapped in an
    /// <c>EXCEPTION WHEN OTHERS</c> handler so any failure only RAISEs a WARNING and the migration COMMITS
    /// regardless. That non-fatality is deliberate and load-bearing: <c>MigrateAsync</c> runs in the service's
    /// startup-CRITICAL path (a thrown migration aborts startup), and it runs BEFORE the runtime
    /// <c>CREATE EXTENSION</c> (<see cref="TimescaleSupport.TryEnableAsync"/>) — so on a FRESH managed store the
    /// guard here is false (the extension is not created yet) and this block no-ops. The AUTHORITATIVE,
    /// proven-live, self-healing conversion is <see cref="TimescaleSupport.EnsureCollectionLogHypertableAsync"/>,
    /// applied at runtime AFTER the extension exists (the same non-transactional path validated for every
    /// collector table). This migration only wins the case where an UPGRADE's store already had the extension
    /// created by a prior startup — then it converts collection_log's existing rows a step early — and quietly
    /// yields to the runtime path otherwise. <c>collection_log</c> lives OUTSIDE
    /// <see cref="CollectorCatalog.All"/> (it is a registry-side table with no <c>ICollectorSchemaInfo</c>), so
    /// the catalog-driven convert/compress loops never reach it — it must be handled directly, here and at
    /// runtime.</para>
    ///
    /// <para><b>create_hypertable.</b> <c>migrate_data =&gt; true</c> moves existing rows into chunks (the
    /// migrate connection uses a long command timeout — <see cref="MigrationCommandTimeoutSeconds"/> — so a
    /// large / many-server backlog is not abandoned at the 30s default); <c>if_not_exists =&gt; true</c> makes
    /// a re-run or an already-converted table a harmless no-op NOTICE (idempotent, so it composes with the
    /// runtime path). <c>collection_log</c> has NO PRIMARY KEY / UNIQUE constraint (see V2), so the hypertable
    /// rule that a unique key must include the partition column cannot conflict — the usual main risk is simply
    /// absent. <c>collection_time</c> stays a naive <c>timestamp</c> (the product-wide cross-store contract), so
    /// <c>create_hypertable</c> emits the advisory use-TIMESTAMPTZ WARNING — expected, exactly like the
    /// collector hypertables.</para>
    ///
    /// <para><b>Compression.</b> Enabled + policy added mirroring <see cref="TimescaleSupport"/> for this one
    /// table: segment by <c>server_id</c> (every read filters it first) and compress chunks older than the same
    /// <see cref="TimescaleSupport.CompressAfterDays"/>, with the same <see cref="TimescaleSupport.ChunkIntervalDays"/>
    /// chunk width — the constants are interpolated so the two never drift. <c>if_not_exists</c> on the policy
    /// keeps it idempotent. Explicitly <c>collect.</c>-qualified like V21/V22.</para>
    /// </summary>
    private static string V23Sql =>
        $@"
/* V23: best-effort UPGRADE fast-path that converts collect.collection_log to a TimescaleDB hypertable + compresses
   it, mirroring the collector hypertables. GUARDED on the extension (plain PostgreSQL skips it, keeping the table a
   heap with batched-DELETE retention) and wrapped in EXCEPTION WHEN OTHERS so it can NEVER abort the startup-critical
   migration. The AUTHORITATIVE conversion is TimescaleSupport.EnsureCollectionLogHypertableAsync at runtime (after
   CREATE EXTENSION), which is the proven-live path and self-heals a fresh store this guard skipped. collection_log is
   NOT in the collector catalog, so the runtime catalog loops never touch it. */
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb') THEN
        PERFORM create_hypertable('collect.collection_log', by_range('collection_time', INTERVAL '{TimescaleSupport.ChunkIntervalDays} days'), if_not_exists => true, migrate_data => true);
        ALTER TABLE collect.collection_log SET (timescaledb.compress, timescaledb.compress_segmentby = 'server_id');
        PERFORM add_compression_policy('collect.collection_log', compress_after => INTERVAL '{TimescaleSupport.CompressAfterDays} days', if_not_exists => true);
    END IF;
EXCEPTION WHEN OTHERS THEN
    RAISE WARNING 'V23: deferred collection_log hypertable conversion to the runtime path (%): %', SQLSTATE, SQLERRM;
END
$$;";

    /// <summary>
    /// V24 — the <c>job_history</c> collector table (retained SQL Agent job-run history from
    /// <c>msdb.dbo.sysjobhistory</c>: every step row + the job-outcome row, deduped on the monotonic
    /// instance_id high-water mark, 365-day retention) for the fleet-wide Job History tab (issue #1433). A
    /// NEW collector table added additively for a store built before this collector existed; a FRESH store
    /// already has it (V1's <see cref="PgSchemaGenerator.GenerateFullSchema"/> walks the collector — which
    /// now includes job_history — and V8 moved it to <c>collect</c>), so <c>CREATE TABLE IF NOT EXISTS</c>
    /// is a harmless no-op on fresh and the real create on upgrade, with an identical
    /// <c>collect.job_history</c> shape either way. EXPLICITLY <c>collect.</c>-qualified (mirroring
    /// V21's default_trace_events): this runs after V8, whose <c>search_path</c> resolves a bare name to
    /// <c>collect</c>, but the explicit schema is defensive.
    /// <para>Column order/types are exactly <see cref="PgSchemaGenerator.CreateTable"/>'s output for the
    /// <see cref="JobHistoryCollector"/> catalog entry (prefix columns NOT NULL, payload columns nullable —
    /// the generator's convention). Like default_trace_events it has NO <c>v_*</c> passthrough view (a
    /// collector added after V14 cannot join the V14 view refresh; the viewer reads the base table directly,
    /// exactly like server_properties), so the <see cref="PgSchemaGenerator.AllPassthroughViews"/> cross-check
    /// is unaffected. TimescaleDB hypertable conversion + compression + 365-day retention all flow from the
    /// catalog at runtime (DarlingRetention / TimescaleSupport iterate CollectorCatalog.All), so none is
    /// emitted here.</para>
    /// </summary>
    private const string V24Sql = @"
CREATE TABLE IF NOT EXISTS collect.job_history (
    job_history_id bigint NOT NULL,
    collection_time timestamp NOT NULL,
    server_id integer NOT NULL,
    server_name text NOT NULL,
    instance_id bigint,
    job_id text,
    job_name text,
    job_enabled boolean,
    category_name text,
    step_id integer,
    step_name text,
    run_status integer,
    run_status_desc text,
    run_datetime timestamp,
    run_duration_seconds bigint,
    retries_attempted integer,
    message text
);

CREATE INDEX IF NOT EXISTS idx_job_history_time ON collect.job_history(server_id, collection_time);";

    /// <summary>
    /// V25 — the <c>agent_status</c> collector table (SQL Agent service Running/Stopped from
    /// <c>sys.dm_server_services</c> + next scheduled run from <c>msdb.dbo.sysjobschedules</c>): the
    /// current-state snapshot behind the Job History tab header and the "Agent Not Running" self-alert
    /// (issue #1433 Phase 2). Added additively exactly like V21/V24 — a fresh store already has it (V1's
    /// <see cref="PgSchemaGenerator.GenerateFullSchema"/> walks the collector, V8 moved it to
    /// <c>collect</c>), so <c>CREATE TABLE IF NOT EXISTS</c> is a no-op on fresh and the real create on
    /// upgrade. Column order/types are exactly <see cref="PgSchemaGenerator.CreateTable"/>'s output for the
    /// <see cref="AgentStatusCollector"/> catalog entry (prefix NOT NULL, payload nullable). No <c>v_*</c>
    /// passthrough view (a post-V14 collector); the viewer reads the base table directly. Hypertable /
    /// compression / 7-day retention flow from the catalog at runtime.
    /// </summary>
    private const string V25Sql = @"
CREATE TABLE IF NOT EXISTS collect.agent_status (
    collection_id bigint NOT NULL,
    collection_time timestamp NOT NULL,
    server_id integer NOT NULL,
    server_name text NOT NULL,
    agent_running boolean,
    agent_status_desc text,
    agent_startup_desc text,
    next_scheduled_run timestamp
);

CREATE INDEX IF NOT EXISTS idx_agent_status_time ON collect.agent_status(server_id, collection_time);";

    /// <summary>
    /// V26 — the generic webhook channel (#1506): a third delivery channel beside Teams/Slack that POSTs an
    /// operator-authored JSON body to any endpoint, so an alert can drive automation we ship no adapter for
    /// (PagerDuty, Opsgenie, n8n, or a GitHub <c>repository_dispatch</c> that re-runs a workflow). It is the
    /// deliberate alternative to executing a script/exe on alert — same automation reach, no process-execution
    /// surface. Additive ALTERs on the V17 control-plane table, schema-qualified <c>config.*</c> and
    /// <c>IF NOT EXISTS</c> per the file's ALTER idiom (V7/V9/V15/V16/V18), so a re-run is a harmless no-op.
    /// The channel is enabled by a non-empty <c>generic_url</c> — the same derivation Teams/Slack use — so the
    /// empty-string defaults leave an upgraded store with the channel off.
    ///
    /// <para><b>Two of these columns are secrets.</b> <c>generic_url</c> is a bearer secret exactly like
    /// <c>teams_url</c>/<c>slack_url</c>, and <c>generic_headers</c> holds the <c>Authorization</c> token
    /// itself — both are carved out of the read-only <c>viewer</c> role's column grants in
    /// <c>DarlingManagedRoles.ViewerRestrictedConfigTables</c>. That list's union-equals-the-table invariant is
    /// gated live by the build, so adding these columns without carving them there FAILS the gate rather than
    /// silently exposing a token to a viewer seat.</para>
    /// </summary>
    private const string V26Sql = @"
ALTER TABLE config.config_notification ADD COLUMN IF NOT EXISTS generic_url text NOT NULL DEFAULT '';
ALTER TABLE config.config_notification ADD COLUMN IF NOT EXISTS generic_headers text NOT NULL DEFAULT '';
ALTER TABLE config.config_notification ADD COLUMN IF NOT EXISTS generic_body_template text NOT NULL DEFAULT '';
ALTER TABLE config.config_notification ADD COLUMN IF NOT EXISTS generic_proxy text NOT NULL DEFAULT '';";

    private const string VersionTableSql = @"
CREATE TABLE IF NOT EXISTS darling_schema_version (
    version integer NOT NULL PRIMARY KEY,
    name text NOT NULL,
    applied_at timestamp NOT NULL
);";

    /// <summary>
    /// Session-scoped advisory lock key serializing concurrent migrators — two connections
    /// racing MigrateAsync (a second service instance misconfigured onto the same store, or
    /// parallel test classes) would otherwise both read the same current version and collide on
    /// the darling_schema_version primary key. Released explicitly and on connection close.
    /// </summary>
    private const long MigrationLockKey = 0x4441524C_494E47; /* "DARLING" */

    /// <summary>
    /// Command timeout (seconds) for applying one migration — well above Npgsql's 30s default so a
    /// data-moving migration is never abandoned half-done. V23 converts collection_log to a hypertable with
    /// <c>migrate_data =&gt; true</c>, which rewrites every existing row into chunks; on a long-collected /
    /// many-server store that can exceed 30s. Mirrors <see cref="TimescaleSupport"/>'s runtime-conversion
    /// budget. Harmless for the DDL-only migrations — they finish in milliseconds regardless.
    /// </summary>
    private const int MigrationCommandTimeoutSeconds = 300;

    /// <summary>
    /// Applies every migration newer than the store's current version, each in its own
    /// transaction, stamping darling_schema_version as it goes. Idempotent — a fully migrated
    /// store is a no-op — and safe under concurrent callers (advisory-locked). The connection
    /// must be open.
    /// </summary>
    public static Task<int> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
        => MigrateAsync(connection, logger: null, cancellationToken);

    /// <summary>
    /// The logger-aware overload: after applying migrations it best-effort sets the database-default
    /// <c>search_path = collect, config, public</c> (V8 split) so EVERY future connection resolves the
    /// bare table names without a per-connection setting — the store-establishing side of migration.
    /// Best-effort because <c>ALTER DATABASE</c> needs the database-owner privilege a least-privilege
    /// BYO login may lack; a failure is warned (via <paramref name="logger"/>) but never fails the
    /// migration, since the moves already committed and the managed connection strings carry Search
    /// Path anyway (see <see cref="TrySetDatabaseSearchPathAsync"/>).
    /// </summary>
    public static async Task<int> MigrateAsync(
        NpgsqlConnection connection, ILogger? logger, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        using (var acquireLock = new NpgsqlCommand("SELECT pg_advisory_lock($1)", connection))
        {
            acquireLock.Parameters.AddWithValue(MigrationLockKey);
            await acquireLock.ExecuteNonQueryAsync(cancellationToken);
        }

        int applied;
        try
        {
            applied = await MigrateLockedAsync(connection, cancellationToken);
        }
        finally
        {
            try
            {
                using var releaseLock = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
                releaseLock.Parameters.AddWithValue(MigrationLockKey);
                await releaseLock.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch
            {
                /* Connection close releases session advisory locks anyway. */
            }
        }

        /* Outside the advisory lock and the per-migration transactions: establish the durable
           database-default search_path for all future connections. Idempotent, best-effort. */
        await TrySetDatabaseSearchPathAsync(connection, logger, cancellationToken);
        return applied;
    }

    private static async Task<int> MigrateLockedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        /* Resolve bare names through collect/config for this migrate session. Load-bearing from V8
           on: V8 moves darling_schema_version into collect, and the version stamp below writes it by
           its bare name — without this the post-move stamp would resolve against the default path
           ("$user", public) and fail. Setting a path whose schemas don't exist yet is legal (pre-V8
           they simply resolve to public, exactly as before), so this is safe on every store version
           and independent of any connection-string Search Path. Session-scoped (outside the
           per-migration transactions), so a migration rollback never unsets it. */
        using (var setPath = new NpgsqlCommand("SET search_path = " + PgSchemaGenerator.SearchPath, connection))
        {
            await setPath.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var createVersionTable = new NpgsqlCommand(VersionTableSql, connection))
        {
            await createVersionTable.ExecuteNonQueryAsync(cancellationToken);
        }

        int currentVersion;
        using (var readVersion = new NpgsqlCommand("SELECT COALESCE(MAX(version), 0) FROM darling_schema_version", connection))
        {
            currentVersion = Convert.ToInt32(await readVersion.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        var applied = 0;
        foreach (var migration in Scripts)
        {
            if (migration.Version <= currentVersion)
            {
                continue;
            }

            using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            using (var apply = new NpgsqlCommand(migration.Sql, connection, transaction) { CommandTimeout = MigrationCommandTimeoutSeconds })
            {
                await apply.ExecuteNonQueryAsync(cancellationToken);
            }

            using (var stamp = new NpgsqlCommand(
                "INSERT INTO darling_schema_version (version, name, applied_at) VALUES ($1, $2, $3)", connection, transaction))
            {
                stamp.Parameters.AddWithValue(migration.Version);
                stamp.Parameters.AddWithValue(migration.Name);
                /* Naive-UTC storage: Npgsql 6+ rejects Kind=Utc against `timestamp` — see PgCollectorRowWriter. */
                stamp.Parameters.AddWithValue(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified));
                await stamp.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            applied++;
        }

        return applied;
    }

    /// <summary>
    /// Best-effort: make <c>search_path = collect, config, public</c> the database default, so
    /// EVERY future connection (the service pool's collector writes, the MCP host, the Viewer,
    /// <c>psql</c>/<c>pg_dump</c>, BYO) resolves the bare table names to the V8 schemas without any
    /// per-connection setting. Complements — does not replace — the session <c>SET</c> in
    /// <see cref="MigrateAsync"/> (which covers the migrate connection itself) and the
    /// <c>Search Path</c> keyword the managed connection strings carry.
    ///
    /// <para>Deliberately OUTSIDE the V8 transaction and swallowing failure: <c>ALTER DATABASE</c>
    /// needs the database-owner privilege, which a least-privilege bring-your-own-Postgres login may
    /// lack. A failure here must NOT fail the migration (the moves already committed) — it logs a
    /// warning telling the operator to run the statement themselves as owner. Idempotent, so it
    /// re-asserts harmlessly on every start. Targets the connection's live database name (managed =
    /// <c>darling</c>; BYO = whatever the operator connected to), identifier-quoted.</para>
    /// </summary>
    public static async Task TrySetDatabaseSearchPathAsync(
        NpgsqlConnection connection, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        var databaseName = connection.Database;
        if (string.IsNullOrEmpty(databaseName))
        {
            return;
        }

        var quotedDatabase = "\"" + databaseName.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        try
        {
            using var command = new NpgsqlCommand(
                $"ALTER DATABASE {quotedDatabase} SET search_path = {PgSchemaGenerator.SearchPath}", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(
                "Could not set the database default search_path on {Database} ({Message}). The managed " +
                "connection strings still carry Search Path, but if you point your own tools at this store, " +
                "run this once as the database owner: ALTER DATABASE {Database} SET search_path = {SearchPath};",
                databaseName, ex.Message, databaseName, PgSchemaGenerator.SearchPath);
        }
    }
}
