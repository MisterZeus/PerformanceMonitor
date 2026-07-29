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
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins Darling's COPY plumbing: the binary-COPY type map, the per-collector COPY command
/// (prefix columns first, id omitted for running_jobs), and the migration script list. The
/// end-to-end migrate + COPY + read-back test runs only when DARLING_TEST_PG points at a dev
/// Postgres (connection string), so CI stays green without one and the test lights up the moment
/// a live instance exists.
/// </summary>
/* Live-fixture tests share one Postgres store; the collection serializes them so
   cross-test row churn (inserts/purges/deletes) cannot race another class's assertions. */
[Collection("live-postgres")]
public sealed class PgCopyWriterTests
{
    [Fact]
    public void DbTypeFor_MapsEveryColumnType()
    {
        var mapped = Enum.GetValues<CollectorColumnType>()
            .Select(PgCollectorRowWriter.DbTypeFor)
            .ToArray();

        Assert.Equal(Enum.GetValues<CollectorColumnType>().Length, mapped.Length);
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Bigint, PgCollectorRowWriter.DbTypeFor(CollectorColumnType.BigInt));
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Text, PgCollectorRowWriter.DbTypeFor(CollectorColumnType.Varchar));
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Timestamp, PgCollectorRowWriter.DbTypeFor(CollectorColumnType.Timestamp));
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Numeric, PgCollectorRowWriter.DbTypeFor(CollectorColumnType.Decimal));
    }

    [Fact]
    public void CopyCommandFor_PrefixColumnsFirst_MirroringLiteNames()
    {
        Assert.StartsWith(
            "COPY wait_stats (collection_id, collection_time, server_id, server_name, wait_type,",
            PgCollectorRowWriter.CopyCommandFor(WaitStatsCollector.Instance),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "COPY deadlocks (deadlock_id, collection_time, server_id, server_name, deadlock_time,",
            PgCollectorRowWriter.CopyCommandFor(DeadlocksCollector.Instance),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "COPY running_jobs (collection_time, server_id, server_name, job_name,",
            PgCollectorRowWriter.CopyCommandFor(RunningJobsCollector.Instance),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "COPY server_config (config_id, capture_time, server_id, server_name,",
            PgCollectorRowWriter.CopyCommandFor(ServerConfigCollector.Instance),
            StringComparison.Ordinal);
        Assert.EndsWith(") FROM STDIN (FORMAT BINARY)",
            PgCollectorRowWriter.CopyCommandFor(WaitStatsCollector.Instance),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationScripts_StartAtV1_StrictlyIncreasing_V1CreatesEveryTable()
    {
        Assert.Equal(1, PgMigrations.Scripts[0].Version);
        for (int i = 1; i < PgMigrations.Scripts.Count; i++)
        {
            Assert.True(PgMigrations.Scripts[i].Version > PgMigrations.Scripts[i - 1].Version);
        }

        var v1 = PgMigrations.Scripts[0].Sql;
        foreach (var schema in CollectorCatalog.All)
        {
            Assert.Contains($"CREATE TABLE IF NOT EXISTS {schema.TargetTable} (", v1, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StripEmbeddedNuls_RemovesNulsPostgresRejects_LeavesCleanTextAlone()
    {
        /* SQL Server NVARCHAR allows embedded NUL; Postgres text rejects it with 22021 (#1614). */
        Assert.Equal("SELECT 1;", PgCollectorRowWriter.StripEmbeddedNuls("SELECT 1;"));
        Assert.Equal("SELECT 1;", PgCollectorRowWriter.StripEmbeddedNuls("SELECT\0 1;\0"));
        Assert.Equal(string.Empty, PgCollectorRowWriter.StripEmbeddedNuls("\0\0"));
        Assert.Equal(string.Empty, PgCollectorRowWriter.StripEmbeddedNuls(string.Empty));
    }

    [Fact]
    public async Task EndToEnd_MigrateCopyReadBack_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live COPY test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        /* Migrations are idempotent — a second run applies nothing. */
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        Assert.Equal(0, await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken));

        /* COPY one deadlocks row through the definition's own WritePayload. The victim text
           carries an embedded NUL — the #1614 repro: SQL Server NVARCHAR allows it, Postgres
           rejects the raw byte with 22021, and the writer must strip it or this COPY fails. */
        var collectionTime = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        var row = new DeadlocksCollector.Row
        {
            DeadlockTime = collectionTime,
            VictimProcessId = "process-e2e",
            VictimSqlText = "UPDATE t SET x = 1;\0",
            GraphXml = "<deadlock/>",
        };

        var writer = new PgCollectorRowWriter();
        using (var importer = await connection.BeginBinaryImportAsync(
            PgCollectorRowWriter.CopyCommandFor(DeadlocksCollector.Instance), TestContext.Current.CancellationToken))
        {
            writer.Importer = importer;
            await importer.StartRowAsync(TestContext.Current.CancellationToken);
            writer.Value(1L)                     /* deadlock_id */
                  .Value(collectionTime)         /* collection_time */
                  .Value(42)                     /* server_id */
                  .Value("e2e-server");          /* server_name */
            DeadlocksCollector.Instance.WritePayload(row, writer, null!);
            await importer.CompleteAsync(TestContext.Current.CancellationToken);
        }

        using (var read = new NpgsqlCommand(
            "SELECT victim_sql_text FROM deadlocks WHERE server_name = 'e2e-server' ORDER BY collection_time DESC LIMIT 1", connection))
        {
            Assert.Equal("UPDATE t SET x = 1;", await read.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        using (var cleanup = new NpgsqlCommand("DELETE FROM deadlocks WHERE server_name = 'e2e-server'", connection))
        {
            await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }
}
