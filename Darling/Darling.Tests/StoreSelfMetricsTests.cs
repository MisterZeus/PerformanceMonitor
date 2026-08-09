/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the V53 <c>collect.store_metrics</c> store surface (#2068): the migration's identity, the viewer
/// schema gate a StorageVersion bump obligates, the sweep SQL's dialect and shape, and the invariant the
/// whole design leans on — the table that MEASURES the compression/retention machinery is not in the
/// collector catalog, so that machinery can never recurse onto it (no hypertable conversion, no
/// compression policy, no catalog-driven purge; its retention is the sweep's own bounded DELETE).
///
/// <para><b>#1776 own-store</b> — the one live test here mints its own scratch database via
/// ScratchPostgres (it applies compression policies, which the shared fixture must never inherit), so
/// it cannot race the shared store and serializing it would be pure slowdown.</para>
/// </summary>
public sealed class StoreSelfMetricsTests
{
    [Fact]
    public void V53_MigrationIdentity_AndStorageVersionTracksTheNewestRung()
    {
        var v53 = PgMigrations.Scripts.Single(m => m.Version == 53);

        Assert.Equal("store-self-metrics", v53.Name);
        Assert.Equal(56, PgMigrations.Scripts[^1].Version);
        Assert.Equal(56, StorageVersion.SchemaVersion);

        /* collect.-qualified like V44/V47/V49, and idempotent so a re-run is a no-op. */
        Assert.Contains("CREATE TABLE IF NOT EXISTS collect.store_metrics (", v53.Sql, StringComparison.Ordinal);
        Assert.Contains(
            "CREATE INDEX IF NOT EXISTS idx_store_metrics_time ON collect.store_metrics(metric_time);",
            v53.Sql,
            StringComparison.Ordinal);

        /* A PLAIN table by design — the compression/retention machinery this table measures must never
           recurse onto it, and a tiny hourly series needs neither chunks nor compression. */
        Assert.DoesNotContain("create_hypertable", v53.Sql, StringComparison.OrdinalIgnoreCase);

        /* Every column the sweep writes must exist in the migration, or the first hourly run after an
           upgrade fails on a column fresh code writes and the upgraded store lacks. */
        foreach (var column in new[]
        {
            "metric_time", "object_name", "object_kind", "total_bytes", "compressed_before_bytes",
            "compressed_after_bytes", "chunk_count", "row_count", "enabled_server_count",
        })
        {
            Assert.Contains($"    {column} ", v53.Sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void V56_JobTelemetryColumns_MigrationAndSweepAgree_AndTheProbeKnowsTheRung()
    {
        /* #2136: every column the background-job sweep arm writes must exist in the V56 migration, or the
           first hourly run after an upgrade fails on a column fresh code writes and the store lacks —
           the exact failure class the V53 column pin above guards. */
        var v56 = PgMigrations.Scripts.Single(m => m.Version == 56);
        Assert.Equal("store-metrics-background-jobs", v56.Name);
        foreach (var column in new[]
        {
            "last_run_duration_ms", "schedule_interval_ms", "total_runs", "total_failures",
        })
        {
            Assert.Contains($"ADD COLUMN IF NOT EXISTS {column} bigint", v56.Sql, StringComparison.Ordinal);
            Assert.Contains(column, StoreSelfMetrics.BackgroundJobInsertSql, StringComparison.Ordinal);
        }

        /* The insert reads only TimescaleDB catalog surfaces, which is why the sweep gates it with the
           hypertable arm — a plain-PG store skips it silently. schedule_interval rides along so
           "duration vs cadence" — the tripwire that matters — is one division over the stored series. */
        Assert.Contains("FROM timescaledb_information.job_stats", StoreSelfMetrics.BackgroundJobInsertSql, StringComparison.Ordinal);
        Assert.Contains("JOIN timescaledb_information.jobs", StoreSelfMetrics.BackgroundJobInsertSql, StringComparison.Ordinal);
        Assert.Contains("'background_job'", StoreSelfMetrics.BackgroundJobInsertSql, StringComparison.Ordinal);

        /* The probe sentinel + arm: a fully-migrated V56 store maps to exactly the required version
           (the connect-time-gate trap), and a V55 store without the columns caps at 55. */
        Assert.Contains("column_name = 'last_run_duration_ms'", ViewerDataService.StoreSchemaProbeSql, StringComparison.Ordinal);
        Assert.Equal(56, ViewerDataService.MapProbedSchemaVersion(
            true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true,
            true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true,
            true, true, true, true, true, true, hasJobMetricsColumns: true));
        Assert.Equal(55, ViewerDataService.MapProbedSchemaVersion(
            true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true,
            true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true,
            true, true, true, true, true, true, hasJobMetricsColumns: false));
    }

    [Fact]
    public void StoreMetrics_IsNotACollectorTable_SoTheMachineryItMeasuresCannotReachIt()
    {
        /* TimescaleSupport's hypertable conversion + compression policies and DarlingRetention's purge both
           enumerate the collector catalog. store_metrics must stay OUT of it: a catalog entry would convert
           the self-metrics table into a hypertable (recursing the machinery onto its own measurement) and
           hand its retention to the policy path instead of the sweep's own 400-day DELETE. */
        Assert.DoesNotContain(TimescaleSupport.HypertableTables, schema => schema.TargetTable == "store_metrics");
    }

    [Fact]
    public void ViewerSchemaGate_KnowsV53_SoAFullyMigratedStoreIsNotRefused()
    {
        /* The trap a StorageVersion bump sets: a probe that cannot SEE the newest migration maps every
           healthy store below RequiredStoreSchemaVersion and the connect-time gate refuses it permanently. */
        Assert.Equal(56, ViewerDataService.RequiredStoreSchemaVersion);
        Assert.Contains("table_name = 'store_metrics'", ViewerDataService.StoreSchemaProbeSql, StringComparison.Ordinal);

        /* The V53 arm: store_metrics present (and everything below it, but NOT V54's gz column —
           hasPlanDimGzip defaults false) maps to exactly 53, the mid-ladder rung this feature added. */
        Assert.Equal(53, ViewerDataService.MapProbedSchemaVersion(
            hasConfigControlPlane: true, hasAlertDeliveryOverride: true, hasAnalysisState: true,
            hasAlertTuningKnobs: true, hasDefaultTraceEvents: true, hasIndexObjectStatsLatestIndex: true,
            hasCollectionLogHypertableOrPlainPg: true, hasJobHistory: true, hasAgentStatus: true,
            hasGenericWebhook: true, hasDeadlocksDatabaseName: true, hasQueryStoreReplicaRole: true,
            hasLongQueryCompletions: true, hasWebDashboardConfig: true, hasCustomViews: true,
            hasServerTags: true, hasConnectionRefireKnobs: true, hasAgCollectors: true,
            hasAgAlertKnobs: true, hasAgLatencyColumns: true, hasAgDisconnectRefire: true,
            hasPayloadDimensions: true, hasDimFloorIndexes: true, hasBlockingWaitThreshold: true,
            hasQueryStoreIntervalIdentity: true, hasPagerDutyWebhook: true, hasPagerDutyProxy: true,
            hasCollectorState: true, hasPlanCorrection: true, hasPvsStats: true,
            hasPvsPressureKnobs: true, hasDatabaseStateAlert: true, hasServerTagColour: true,
            hasQueryStatsHostObject: true, hasFindingDrillDown: true, hasStoreMetrics: true));
    }

    [Fact]
    public void HypertableInsertSql_ReadsTheThreeTimescaleCatalogSurfaces_TimescaleOnlyByConstruction()
    {
        var sql = StoreSelfMetrics.HypertableInsertSql;

        /* The three reads the issue names — the enumeration, the size, and the compression stats. All
           TimescaleDB-only objects, which is why the sweep gates this statement on the worker's cached
           TimescaleSupport detection and a plain-PG store skips it silently. */
        Assert.Contains("FROM timescaledb_information.hypertables", sql, StringComparison.Ordinal);
        Assert.Contains("hypertable_detailed_size", sql, StringComparison.Ordinal);
        Assert.Contains("chunk_compression_stats", sql, StringComparison.Ordinal);

        Assert.Contains("INSERT INTO collect.store_metrics", sql, StringComparison.Ordinal);
        Assert.Contains("'hypertable'", sql, StringComparison.Ordinal);
        /* The regclass is built from the catalog view's own rows via format('%I.%I', ...) — never input. */
        Assert.Contains("format('%I.%I', h.hypertable_schema, h.hypertable_name)::regclass", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DimensionInsertSql_CoversBothPayloadDims_WithTotalRelationSizeAndExactRowCount()
    {
        var sql = StoreSelfMetrics.DimensionInsertSql;

        /* The dims are the store's dominant payloads (measured: query_plan_dim alone was 101 GB of a
           147 GB store) and invisible to every hypertable surface — this is the row that makes the single
           biggest forecasting term a stored series. pg_total_relation_size because the plan XML lives in
           TOAST, which per-table heap sizes do not count. */
        Assert.Contains(PayloadDimensions.QueryTextDimTable, sql, StringComparison.Ordinal);
        Assert.Contains(PayloadDimensions.QueryPlanDimTable, sql, StringComparison.Ordinal);
        Assert.Contains("pg_total_relation_size", sql, StringComparison.Ordinal);
        Assert.Contains("'dimension'", sql, StringComparison.Ordinal);
        Assert.Contains("count(*)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreInsertSql_WholeStoreSize_PlusTheEnabledServerDenominator()
    {
        var sql = StoreSelfMetrics.StoreInsertSql;

        /* pg_database_size is the same read the disk-pressure check and the Viewer status bar use, and
           is_enabled is the fleet reader's own registry predicate — so the per-server rate divides by
           exactly the servers the fleet surfaces count. */
        Assert.Contains("pg_database_size(current_database())", sql, StringComparison.Ordinal);
        Assert.Contains("'store'", sql, StringComparison.Ordinal);
        Assert.Contains("FROM collect.servers WHERE is_enabled", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Retention_IsTheSweepsOwnBoundedDelete_At400Days()
    {
        /* One DELETE inside the sweep — deliberately no policy machinery on a plain ~30-rows/hour table. */
        Assert.Equal(400, StoreSelfMetrics.RetentionDays);
        Assert.Contains("DELETE FROM collect.store_metrics", StoreSelfMetrics.RetentionDeleteSql, StringComparison.Ordinal);
        Assert.Contains("WHERE metric_time < $1", StoreSelfMetrics.RetentionDeleteSql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(StoreSelfMetrics.HypertableInsertSql))]
    [InlineData(nameof(StoreSelfMetrics.DimensionInsertSql))]
    [InlineData(nameof(StoreSelfMetrics.StoreInsertSql))]
    [InlineData(nameof(StoreSelfMetrics.RetentionDeleteSql))]
    public void SweepSql_IsPostgresDialect_PositionalParams_NoBareNow(string sqlName)
    {
        var sql = (string)typeof(StoreSelfMetrics)
            .GetField(sqlName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

        Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("N'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("getdate", sql.ToLowerInvariant());
        Assert.DoesNotContain("[", sql, StringComparison.Ordinal);
        /* No bare now(): every statement stamps the ONE caller-supplied $1 metric_time, which is what
           makes a run's rows join and keeps the timestamps naive UTC by the cross-store contract. */
        Assert.DoesNotContain("now()", sql, StringComparison.Ordinal);
        Assert.Contains("$1", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sweep END TO END against a real TimescaleDB (#2136) — the drift-catcher the SQL pins alone
    /// cannot be: every INSERT the sweep runs must agree with the columns the migrations created, and the
    /// failure class this guards (sweep writes a column the upgraded store lacks) only surfaces when the
    /// statements actually execute. Mints its own scratch store (the #1776 own-store idiom), migrates it,
    /// converts + applies compression policies so real background jobs exist, then asserts one run writes
    /// hypertable, dimension, store, AND background_job rows — the job rows carrying a schedule interval,
    /// because "duration vs cadence" is the series' whole point.
    /// </summary>
    [Fact]
    public async Task Sweep_EndToEnd_WritesEveryObjectKind_IncludingBackgroundJobs_AgainstDevPostgres()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(baseConnectionString),
            "Set DARLING_TEST_PG to a Postgres connection string (with TimescaleDB installed) to run the live self-metrics sweep test (it mints its own scratch database).");

        var ct = TestContext.Current.CancellationToken;

        await using var scratch = await ScratchPostgres.CreateAsync(baseConnectionString!, ct);
        await using var connection = new NpgsqlConnection(scratch.ConnectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);

        Assert.True(await TimescaleSupport.TryEnableAsync(connection, null, ct),
            "the dev fixture is expected to have TimescaleDB installed");
        await TimescaleSupport.ConvertToHypertablesAsync(connection, null, ct);
        await TimescaleSupport.ApplyCompressionPolicyAsync(connection, null, ct);

        var written = await StoreSelfMetrics.SweepAsync(
            connection, timescaleAvailable: true, DateTime.UtcNow, null, ct);
        Assert.True(written > 0, "the sweep wrote nothing");

        await using var kinds = new NpgsqlCommand(@"
SELECT
    count(*) FILTER (WHERE object_kind = 'hypertable'),
    count(*) FILTER (WHERE object_kind = 'dimension'),
    count(*) FILTER (WHERE object_kind = 'store'),
    count(*) FILTER (WHERE object_kind = 'background_job'),
    count(*) FILTER (WHERE object_kind = 'background_job' AND schedule_interval_ms > 0)
FROM collect.store_metrics", connection);
        await using var reader = await kinds.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));

        Assert.True(reader.GetInt64(0) > 0, "no hypertable rows");
        Assert.True(reader.GetInt64(1) > 0, "no dimension rows");
        Assert.Equal(1, reader.GetInt64(2));
        Assert.True(reader.GetInt64(3) > 0, "no background_job rows — the compression policies just applied guarantee jobs exist");
        Assert.True(reader.GetInt64(4) > 0, "background_job rows carry no schedule interval — duration-vs-cadence needs it");
    }
}
