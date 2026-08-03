/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace PerformanceMonitor.Darling.Tests;

/// <summary>
/// Pins the #2007 retirement of the CPU/IO baseline aggregates. The invariants that must not
/// drift: the retired names stay OUT of <see cref="TimescaleSupport.BaselineAggregates"/> (a
/// re-add would resurrect an object the startup sweep deletes — a create/drop fight on every
/// restart); the retirement list itself names exactly the two orphans; the drop SQL discriminates
/// continuous-aggregate from plain-fallback-view (a CAGG is also a <c>relkind='v'</c> view, and
/// the two need different DROP verbs); and the worker runs the drop in the UNGATED fallback block
/// so both store shapes are cleaned. The live half exercises the sweep against dev Postgres in
/// all three states an upgraded store can present: the CAGG shape (with policies riding along
/// into the drop), the plain-view shape, and already-gone (idempotent no-op).
/// </summary>
public sealed class RetiredBaselineAggregateTests
{
    [Fact]
    public void RetiredList_NamesExactlyTheTwoOrphans()
    {
        Assert.Equal(
            new[] { "cpu_utilization_baseline", "file_io_baseline" },
            TimescaleSupport.RetiredBaselineRelations);
    }

    [Fact]
    public void BaselineAggregates_DoNotContainRetiredNames_SoTheSweepCannotRecreateWhatItDrops()
    {
        var living = TimescaleSupport.BaselineAggregates.Select(a => a.View).ToArray();

        Assert.Equal(7, living.Length);
        foreach (var retired in TimescaleSupport.RetiredBaselineRelations)
        {
            Assert.DoesNotContain(retired, living);
        }
    }

    [Fact]
    public void DropSql_DiscriminatesCaggFromPlainView_AndProbesWithoutPrivileges()
    {
        var sql = TimescaleSupport.DropRetiredBaselineRelationSql("cpu_utilization_baseline");

        /* Both verbs present, chosen by the continuous_aggregates membership check — and the
           timescaledb_information reference is itself to_regclass-guarded so the block runs on
           stores that never had the extension. */
        Assert.Contains("DROP MATERIALIZED VIEW IF EXISTS collect.cpu_utilization_baseline CASCADE", sql, StringComparison.Ordinal);
        Assert.Contains("DROP VIEW IF EXISTS collect.cpu_utilization_baseline CASCADE", sql, StringComparison.Ordinal);
        Assert.Contains("timescaledb_information.continuous_aggregates", sql, StringComparison.Ordinal);
        Assert.Contains("to_regclass('timescaledb_information.continuous_aggregates')", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_RunsTheRetirementDrop_InTheUngatedFallbackBlock()
    {
        var worker = ReadWorkerSource();

        var dropAt = worker.IndexOf("TimescaleSupport.DropRetiredBaselineAggregatesAsync(", StringComparison.Ordinal);
        var ensureAt = worker.IndexOf("TimescaleSupport.EnsureBaselineFallbackViewsAsync(", StringComparison.Ordinal);
        var plainModeAt = worker.IndexOf("continuing in plain-PostgreSQL mode", StringComparison.Ordinal);

        Assert.True(dropAt > 0, "the worker must run the retirement drop");
        /* Same reachability argument the fallback ensure carries (BaselineSupplyTests): after the
           TimescaleDB block's catch, so plain-PG stores' fallback VIEWS are cleaned too — the
           retired names exist in both implementations in the field. */
        Assert.True(plainModeAt > 0 && dropAt > plainModeAt,
            "the retirement drop must run after the TimescaleDB block, on every path — not inside it");
        Assert.True(ensureAt > dropAt,
            "drop retired names before the ensure sweep runs (order is hygiene, not correctness — the ensure list no longer contains them)");
    }

    private static string ReadWorkerSource([CallerFilePath] string thisFile = "")
    {
        var relative = Path.Combine("Darling", "PerformanceMonitor.Darling.Service", "DarlingWorker.cs");
        var dir = Path.GetDirectoryName(thisFile)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, relative)))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.False(dir is null, "could not locate the repo root from the test source path");
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}

/// <summary>
/// The live retirement sweep against dev Postgres, in the three states an upgraded store presents.
/// The fixture recreates the RETIRED objects from their pre-#2007 definitions inline (the
/// constants are gone from the codebase — that is the point), sourced on a hypertable the same
/// idempotent way the worker converts them.
/// </summary>
[Collection("live-postgres")]
public sealed class RetiredBaselineAggregateLiveTests
{
    [Fact]
    public async Task Sweep_DropsCaggWithPolicies_ThenPlainView_ThenNoOps_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live retirement sweep.");

        var ct = TestContext.Current.CancellationToken;

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);

        /* ---- fixture: the pre-#2007 CAGG shape for cpu (policies riding along), the plain
                fallback-view shape for file_io — one sweep must clean BOTH implementations.
                create_hypertable is the worker's own idempotent conversion; CAGG CREATE cannot
                run inside a transaction, so every statement executes on its own. */
        await ExecuteAsync(connection,
            "SELECT create_hypertable('collect.cpu_utilization_stats', by_range('collection_time', INTERVAL '1 day'), if_not_exists => true, migrate_data => true)", ct);
        await ExecuteAsync(connection, @"
CREATE MATERIALIZED VIEW IF NOT EXISTS collect.cpu_utilization_baseline
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT server_id, time_bucket('1 hour', collection_time) AS bucket, collection_time,
       sum(sqlserver_cpu_utilization) AS cpu_sum,
       sum(power(sqlserver_cpu_utilization, 2)) AS cpu_sumsq,
       count(sqlserver_cpu_utilization) AS cpu_count
FROM collect.cpu_utilization_stats
GROUP BY server_id, bucket, collection_time
WITH NO DATA", ct);
        await ExecuteAsync(connection,
            "SELECT add_continuous_aggregate_policy('collect.cpu_utilization_baseline', start_offset => INTERVAL '3 days', end_offset => INTERVAL '1 hour', schedule_interval => INTERVAL '1 hour', if_not_exists => true)", ct);
        await ExecuteAsync(connection,
            "SELECT add_retention_policy('collect.cpu_utilization_baseline', drop_after => INTERVAL '35 days', if_not_exists => true)", ct);
        await ExecuteAsync(connection,
            "CREATE OR REPLACE VIEW collect.file_io_baseline AS SELECT server_id, date_trunc('hour', collection_time) AS bucket, collection_time, count(*) AS row_count FROM collect.file_io_stats GROUP BY 1, 2, 3", ct);

        /* ---- the sweep: both shapes drop in one pass; policies go with the CAGG. */
        var dropped = await TimescaleSupport.DropRetiredBaselineAggregatesAsync(connection, null, ct);
        Assert.Equal(2, dropped);

        using (var check = new NpgsqlCommand(
            "SELECT to_regclass('collect.cpu_utilization_baseline') IS NULL AND to_regclass('collect.file_io_baseline') IS NULL", connection))
        {
            Assert.True(await check.ExecuteScalarAsync(ct) is true, "both retired relations must be gone");
        }

        using (var policyCheck = new NpgsqlCommand(
            "SELECT COUNT(*) FROM timescaledb_information.continuous_aggregates WHERE view_name = ANY($1)", connection))
        {
            policyCheck.Parameters.AddWithValue(TimescaleSupport.RetiredBaselineRelations);
            Assert.Equal(0L, await policyCheck.ExecuteScalarAsync(ct));
        }

        /* ---- idempotent: a second pass finds nothing. */
        Assert.Equal(0, await TimescaleSupport.DropRetiredBaselineAggregatesAsync(connection, null, ct));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, System.Threading.CancellationToken ct)
    {
        using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(ct);
    }
}
