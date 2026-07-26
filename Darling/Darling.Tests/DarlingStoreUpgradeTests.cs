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
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The service-orchestrated store runtime upgrade (#1706). The ungated tests pin the pure decisions the
/// orchestration is built on — version parsing, the transfer-mode arithmetic, and the initdb/pg_upgrade
/// argument shapes, which is where a wrong default silently produces an incompatible cluster.
///
/// <para>The gated test is the one that matters: it builds a REAL store on the OLD runtime (initdb,
/// TimescaleDB, a hypertable with TOAST-sized plan XML, a continuous aggregate, compression), then runs
/// the real <see cref="DarlingManagedPostgres.EnsureRunningAsync"/> against a deployment whose shipped zip
/// is the NEW runtime, and proves the upgraded store still holds the same rows and still answers through
/// its continuous aggregate. That is the UPGRADED-IN-PLACE fixture #1705 proved CI could not see: a store
/// that has been through a version change behaves differently from the fresh ones darling-pg creates.</para>
/// </summary>
public sealed class DarlingStoreUpgradeTests
{
    [Theory]
    [InlineData("pg_ctl (PostgreSQL) 18.4", 18)]
    [InlineData("pg_ctl (PostgreSQL) 17.10", 17)]
    [InlineData("initdb (PostgreSQL) 16.2", 16)]
    [InlineData("postgres (PostgreSQL) 19beta1", 19)]
    [InlineData("17", 17)]
    [InlineData("", null)]
    [InlineData("no version here", null)]
    public void ParsePostgresMajor_ReadsTheMajorOrRefuses(string input, int? expected)
        => Assert.Equal(expected, DarlingStoreUpgrade.ParsePostgresMajor(input));

    [Fact]
    public void ParseTimescaleDefaultVersion_ReadsTheControlFile()
    {
        const string control = """
            # timescaledb extension
            comment = 'Enables scalable inserts and complex queries for time-series data'
            default_version = '2.28.1'
            module_pathname = '$libdir/timescaledb'
            """;

        Assert.Equal("2.28.1", DarlingStoreUpgrade.ParseTimescaleDefaultVersion(control));
        Assert.Null(DarlingStoreUpgrade.ParseTimescaleDefaultVersion("comment = 'no default here'"));
        Assert.Null(DarlingStoreUpgrade.ParseTimescaleDefaultVersion(null));
    }

    [Fact]
    public void DecideTransferMode_CopyWhenTheVolumeHasRoomForTwoCopies()
    {
        const long tenGb = 10L * 1024 * 1024 * 1024;
        var decision = DarlingStoreUpgrade.DecideTransferMode(tenGb, 40L * 1024 * 1024 * 1024, hardLinksSupported: true);

        Assert.Equal(DarlingStoreUpgrade.FileTransferMode.Copy, decision.Mode);
    }

    [Fact]
    public void DecideTransferMode_LinkOnlyWhenCopyCannotFitAndLinksWork()
    {
        const long tenGb = 10L * 1024 * 1024 * 1024;

        /* 12 GB free cannot hold a second 10 GB copy plus slack, but easily covers link mode. */
        var link = DarlingStoreUpgrade.DecideTransferMode(tenGb, 12L * 1024 * 1024 * 1024, hardLinksSupported: true);
        Assert.Equal(DarlingStoreUpgrade.FileTransferMode.Link, link.Mode);

        /* Same space, but the volume cannot make hard links: there is no safe mode left, so do not upgrade.
           An abort keeps the store running on its existing major, which beats a half-finished upgrade. */
        var abort = DarlingStoreUpgrade.DecideTransferMode(tenGb, 12L * 1024 * 1024 * 1024, hardLinksSupported: false);
        Assert.Equal(DarlingStoreUpgrade.FileTransferMode.Abort, abort.Mode);
    }

    [Fact]
    public void DecideTransferMode_AbortWhenEvenLinkModeCannotFit()
    {
        const long tenGb = 10L * 1024 * 1024 * 1024;
        var decision = DarlingStoreUpgrade.DecideTransferMode(tenGb, 200L * 1024 * 1024, hardLinksSupported: true);

        Assert.Equal(DarlingStoreUpgrade.FileTransferMode.Abort, decision.Mode);
    }

    [Fact]
    public void BuildInitDbArguments_ReproducesTheOldClusterRatherThanTheNewMajorsDefaults()
    {
        /* The managed store's own identity: UTF8 + C locale via libc + checksums ON. */
        var identity = new DarlingStoreUpgrade.ClusterIdentity("UTF8", "C", "C", "c", null, DataChecksums: true);
        var arguments = DarlingStoreUpgrade.BuildInitDbArguments(@"C:\pg\new", "darling", @"C:\pg\pw.tmp", identity, 18);

        Assert.Contains("-U darling", arguments, StringComparison.Ordinal);
        Assert.Contains("-A scram-sha-256", arguments, StringComparison.Ordinal);
        Assert.Contains("-E UTF8", arguments, StringComparison.Ordinal);
        Assert.Contains("--locale-provider=libc", arguments, StringComparison.Ordinal);
        Assert.Contains("--lc-collate=C", arguments, StringComparison.Ordinal);
        Assert.Contains("--lc-ctype=C", arguments, StringComparison.Ordinal);
        Assert.Contains("--data-checksums", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-data-checksums", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInitDbArguments_DisablesChecksumsExplicitlyOn18BecauseItsDefaultFlipped()
    {
        /* PostgreSQL 18 changed initdb to enable data checksums by DEFAULT, and pg_upgrade hard-refuses a
           checksum mismatch. A checksum-less 17 store therefore needs an explicit --no-data-checksums, a
           flag that does not exist before 18 — which is exactly why this is derived, never assumed. */
        var identity = new DarlingStoreUpgrade.ClusterIdentity("UTF8", "C", "C", "c", null, DataChecksums: false);

        var on18 = DarlingStoreUpgrade.BuildInitDbArguments(@"C:\pg\new", "darling", @"C:\pg\pw.tmp", identity, 18);
        Assert.Contains("--no-data-checksums", on18, StringComparison.Ordinal);

        var on17 = DarlingStoreUpgrade.BuildInitDbArguments(@"C:\pg\new", "darling", @"C:\pg\pw.tmp", identity, 17);
        Assert.DoesNotContain("--no-data-checksums", on17, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInitDbArguments_CarriesTheIcuAndBuiltinProvidersThrough()
    {
        var icu = new DarlingStoreUpgrade.ClusterIdentity("UTF8", "en_US.UTF-8", "en_US.UTF-8", "i", "en-US", true);
        var icuArguments = DarlingStoreUpgrade.BuildInitDbArguments(@"C:\pg\new", "darling", @"C:\pg\pw.tmp", icu, 18);
        Assert.Contains("--locale-provider=icu", icuArguments, StringComparison.Ordinal);
        Assert.Contains("--icu-locale=en-US", icuArguments, StringComparison.Ordinal);

        var builtin = new DarlingStoreUpgrade.ClusterIdentity("UTF8", "C", "C", "b", "C.UTF-8", true);
        var builtinArguments = DarlingStoreUpgrade.BuildInitDbArguments(@"C:\pg\new", "darling", @"C:\pg\pw.tmp", builtin, 18);
        Assert.Contains("--locale-provider=builtin", builtinArguments, StringComparison.Ordinal);
        Assert.Contains("--builtin-locale=C.UTF-8", builtinArguments, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPgUpgradeArguments_CheckModeIsADryRunAndLinkModeIsOptIn()
    {
        var check = DarlingStoreUpgrade.BuildPgUpgradeArguments(
            @"C:\pg\old\bin", @"C:\pg\new\bin", @"C:\data\pg", @"C:\data\pg-upgrade-18", "darling",
            DarlingStoreUpgrade.FileTransferMode.Copy, checkOnly: true, jobs: 4);

        Assert.Contains("--check", check, StringComparison.Ordinal);
        Assert.Contains(@"--old-bindir ""C:\pg\old\bin""", check, StringComparison.Ordinal);
        Assert.Contains(@"--new-datadir ""C:\data\pg-upgrade-18""", check, StringComparison.Ordinal);
        Assert.Contains("--username darling", check, StringComparison.Ordinal);
        Assert.DoesNotContain("--link", check, StringComparison.Ordinal);
        /* --check does no file work, so jobs would only be noise. */
        Assert.DoesNotContain("--jobs", check, StringComparison.Ordinal);

        var real = DarlingStoreUpgrade.BuildPgUpgradeArguments(
            @"C:\pg\old\bin", @"C:\pg\new\bin", @"C:\data\pg", @"C:\data\pg-upgrade-18", "darling",
            DarlingStoreUpgrade.FileTransferMode.Link, checkOnly: false, jobs: 4);

        Assert.Contains("--link", real, StringComparison.Ordinal);
        Assert.Contains("--jobs 4", real, StringComparison.Ordinal);
        Assert.DoesNotContain("--check", real, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPgUpgradeArguments_QuiescesTimescaleOnBothClusters()
    {
        /* -o reaches the OLD cluster, -O the NEW one, and BOTH need it: pg_upgrade starts each in turn and
           connects database by database, which is exactly the workload TimescaleDB's scheduler background
           worker deadlocks against (timescale/timescaledb#1593). One side quiesced is not enough. */
        var arguments = DarlingStoreUpgrade.BuildPgUpgradeArguments(
            @"C:\pg\old\bin", @"C:\pg\new\bin", @"C:\data\pg", @"C:\data\pg-upgrade-18", "darling",
            DarlingStoreUpgrade.FileTransferMode.Copy, checkOnly: false, jobs: 1,
            DarlingStoreUpgrade.QuiesceTimescaleServerOptions);

        Assert.Contains(@"-o ""-c timescaledb.max_background_workers=0""", arguments, StringComparison.Ordinal);
        Assert.Contains(@"-O ""-c timescaledb.max_background_workers=0""", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void RetainedDataDirectory_IsNamedForTheMajorItCameFrom()
        => Assert.Equal(
            @"C:\ProgramData\PerformanceMonitorDarling\pg-old-17",
            DarlingStoreUpgrade.RetainedDataDirectoryFor(@"C:\ProgramData\PerformanceMonitorDarling\pg", 17));

    [Fact]
    public void PreviousRuntimeRoot_SitsBesideTheRuntimeItRescued()
        => Assert.Equal(
            @"C:\Program Files\Darling\pg-runtime-prev",
            DarlingStoreUpgrade.PreviousRuntimeRootFor(@"C:\Program Files\Darling\pg-runtime"));

    /* ==================================================================================
       The gated upgraded-in-place fixture.
       ================================================================================== */

    /// <summary>
    /// Builds a REAL store on the old runtime, ships the new runtime as the package's zip, and runs the
    /// production bootstrap over it — then proves the upgraded store kept its data and its TimescaleDB
    /// objects. Everything is measured before and after: this test fails if the upgrade "succeeds" while
    /// losing rows, breaking the continuous aggregate, or leaving the extension behind its binaries.
    /// </summary>
    [Fact]
    public async Task UpgradeInPlace_OldMajorStoreWithRealData_UpgradesAndKeepsEverything_Gated()
    {
        var oldRuntime = Environment.GetEnvironmentVariable("DARLING_TEST_PGRUNTIME_OLD");
        var newZip = Environment.GetEnvironmentVariable("DARLING_TEST_PGRUNTIME_NEWZIP");

        Assert.SkipWhen(string.IsNullOrWhiteSpace(oldRuntime) || string.IsNullOrWhiteSpace(newZip),
            "Set DARLING_TEST_PGRUNTIME_OLD to an assembled pg-runtime directory built from the PREVIOUS " +
            "PostgreSQL major (the folder containing pgsql\\bin\\pg_ctl.exe) and DARLING_TEST_PGRUNTIME_NEWZIP " +
            "to a pg-runtime.zip built from the CURRENT one. Darling\\tools\\new-upgraded-store-fixture.ps1 " +
            "produces both.");
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The bundled runtime is Windows-only.");
        Assert.SkipUnless(File.Exists(Path.Combine(oldRuntime!, "pgsql", "bin", "pg_ctl.exe")),
            $"DARLING_TEST_PGRUNTIME_OLD={oldRuntime} does not contain pgsql\\bin\\pg_ctl.exe.");
        Assert.SkipUnless(File.Exists(newZip!), $"DARLING_TEST_PGRUNTIME_NEWZIP={newZip} does not exist.");

        var root = Directory.CreateTempSubdirectory("darling-pgupgrade-");
        try
        {
            /* The deployment starts as the OLD install: pg-runtime\pgsql only. The NEW package's zip is
               dropped in later, because that IS the event under test — the store has to be created by the
               old runtime, undisturbed, before the upgrade can be a real upgrade. */
            var deployment = Path.Combine(root.FullName, "deploy");
            var runtimeRoot = Path.Combine(deployment, "pg-runtime");
            Directory.CreateDirectory(deployment);
            CopyDirectory(Path.Combine(oldRuntime!, "pgsql"), Path.Combine(runtimeRoot, "pgsql"));

            var dataDirectory = Path.Combine(root.FullName, "store", "pg");
            var config = new PostgresConfig
            {
                Managed = true,
                Port = FindFreeTcpPort(),
                DataDirectory = dataDirectory,
            };

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));

            /* ---- 1. Create the store on the OLD runtime, exactly as a field install did. The stamp is
                    written for the OLD runtime so the bootstrap sees a legitimately-installed one. ---- */
            var oldMajor = await BuildOldStoreAsync(runtimeRoot, dataDirectory, config.Port, timeout.Token);

            /* Stamp the installed runtime with the identity of the zip it came from — what production does
               at extraction time. Stored uncompressed: this is a hash source, not an artifact. */
            var stampSource = Path.Combine(root.FullName, "old-runtime-stamp-source.zip");
            ZipFile.CreateFromDirectory(
                Path.Combine(runtimeRoot, "pgsql"), stampSource, CompressionLevel.NoCompression, includeBaseDirectory: true);
            File.WriteAllText(
                Path.Combine(runtimeRoot, DarlingStoreUpgrade.RuntimeStampFileName),
                DarlingStoreUpgrade.ComputeFileHash(stampSource));

            /* NOW the new package arrives beside the old runtime — the deploy that must trigger the upgrade. */
            File.Copy(newZip!, Path.Combine(deployment, "pg-runtime.zip"));

            var password = DarlingSecrets.Unprotect(
                File.ReadAllText(DarlingManagedPostgres.CredentialPathFor(dataDirectory)).Trim());
            var oldConnection = DarlingManagedPostgres.BuildConnectionString(config.Port, password);

            /* ---- 2. Measure the store BEFORE, through the old binaries. ---- */
            await StartWithRuntimeAsync(runtimeRoot, dataDirectory, config.Port, timeout.Token);
            var before = await MeasureStoreAsync(oldConnection, timeout.Token);
            await StopWithRuntimeAsync(runtimeRoot, dataDirectory, timeout.Token);

            Assert.True(before.LogRows > 0, "the fixture must contain rows for the comparison to mean anything");
            Assert.True(before.CaggRows > 0, "the fixture must contain a materialized continuous aggregate");

            /* ---- 3. Run the REAL bootstrap. This is the whole feature: detect, rescue the old runtime,
                    extract the new one, bridge TimescaleDB, initdb, pg_upgrade, swap, start, verify. ---- */
            var managed = new DarlingManagedPostgres(config, NullLogger.Instance, runtimeRoot);
            var connectionString = await managed.EnsureRunningAsync(timeout.Token);

            try
            {
                /* ---- 4. The store is on the NEW major and its data survived intact. ---- */
                var after = await MeasureStoreAsync(connectionString, timeout.Token);

                Assert.True(after.ServerMajor > oldMajor,
                    $"expected an upgrade past PostgreSQL {oldMajor}, but the server reports {after.ServerMajor}");
                Assert.Equal(before.LogRows, after.LogRows);
                Assert.Equal(before.PlanRows, after.PlanRows);
                Assert.Equal(before.PlanXmlLength, after.PlanXmlLength);
                Assert.Equal(before.LogChecksum, after.LogChecksum);

                /* The continuous aggregate is not merely present — it still ANSWERS, which means the
                   TimescaleDB catalog, the materialization hypertable and its chunks all came across. */
                Assert.Equal(before.CaggRows, after.CaggRows);

                /* The extension matches the binaries it now runs on. A store whose extension lagged its
                   runtime is the #1705 drift this whole issue exists to end. */
                Assert.Equal(BundledTimescaleVersion(runtimeRoot), after.TimescaleVersion);

                /* Compressed chunks survived as compressed chunks. */
                Assert.Equal(before.CompressedChunks, after.CompressedChunks);

                /* The rollback copy is on disk, named for the major it came from. */
                Assert.True(
                    Directory.Exists(DarlingStoreUpgrade.RetainedDataDirectoryFor(dataDirectory, oldMajor)),
                    "the pre-upgrade data directory should be retained for rollback after a copy-mode upgrade");

                /* The rescued old runtime is still there — the only thing that could ever upgrade this
                   store again from the old major, and pg_upgrade's --old-bindir. */
                Assert.True(Directory.Exists(
                    Path.Combine(DarlingStoreUpgrade.PreviousRuntimeRootFor(runtimeRoot), "pgsql")));

                /* The managed conf blocks are on the NEW data directory: the upgrade wrote them before
                   pg_upgrade (shared_preload_libraries has to be live for the extension to restore) and
                   the normal heal path did not duplicate them. */
                var conf = await File.ReadAllTextAsync(Path.Combine(dataDirectory, "postgresql.conf"), timeout.Token);
                Assert.Contains("shared_preload_libraries = 'timescaledb'", conf, StringComparison.Ordinal);
                Assert.Equal(1, CountOccurrences(conf, DarlingManagedPostgres.ConfMarker));
                Assert.Equal(1, CountOccurrences(conf, DarlingManagedPostgres.ConfMarkerV6));
            }
            finally
            {
                await managed.StopIfStartedByThisProcessAsync();
            }
        }
        finally
        {
            TryDeleteTree(root.FullName);
        }
    }

    /* ---------------- fixture construction ---------------- */

    /// <summary>
    /// Creates the pre-upgrade store with the OLD runtime: a real initdb + managed conf, TimescaleDB, a
    /// hypertable of collector rows, a second hypertable carrying TOAST-sized plan XML (the blob shape the
    /// PostgreSQL 18 move is FOR), a continuous aggregate, and a compressed chunk. Returns its major.
    /// </summary>
    private static async Task<int> BuildOldStoreAsync(
        string runtimeRoot, string dataDirectory, int port, CancellationToken cancellationToken)
    {
        var config = new PostgresConfig { Managed = true, Port = port, DataDirectory = dataDirectory };

        /* The product's own first-run path builds the cluster, so the fixture is a store this service
           really would have created — not a hand-rolled approximation of one. */
        var bootstrap = new DarlingManagedPostgres(config, NullLogger.Instance, runtimeRoot);
        var connectionString = await bootstrap.EnsureRunningAsync(cancellationToken);

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            var major = await ReadServerMajorAsync(connection, cancellationToken);

            await ExecuteAsync(connection, "CREATE EXTENSION IF NOT EXISTS timescaledb", cancellationToken);
            await ExecuteAsync(connection, "CREATE SCHEMA IF NOT EXISTS collect", cancellationToken);

            await ExecuteAsync(
                connection,
                """
                CREATE TABLE collect.collection_log (
                    collection_time timestamptz NOT NULL,
                    server_id       integer      NOT NULL,
                    collector_name  text         NOT NULL,
                    status          text         NOT NULL,
                    rows_collected  bigint       NOT NULL
                )
                """,
                cancellationToken);
            await ExecuteAsync(
                connection,
                "SELECT create_hypertable('collect.collection_log', by_range('collection_time'))",
                cancellationToken);

            await ExecuteAsync(
                connection,
                """
                CREATE TABLE collect.query_plans (
                    collection_time timestamptz NOT NULL,
                    server_id       integer      NOT NULL,
                    query_hash      text         NOT NULL,
                    plan_xml        text         NOT NULL
                )
                """,
                cancellationToken);
            await ExecuteAsync(
                connection,
                "SELECT create_hypertable('collect.query_plans', by_range('collection_time'))",
                cancellationToken);

            /* Collector rows across several days so the hypertable really has multiple chunks. */
            await ExecuteAsync(
                connection,
                """
                INSERT INTO collect.collection_log (collection_time, server_id, collector_name, status, rows_collected)
                SELECT now() - (n || ' minutes')::interval,
                       1 + (n % 4),
                       'collector_' || (n % 7),
                       CASE WHEN n % 23 = 0 THEN 'ERROR' ELSE 'SUCCESS' END,
                       n * 3
                FROM generate_series(1, 20000) AS g(n)
                """,
                cancellationToken);

            /* TOAST-sized plan XML: the large-blob shape whose compression is the reason for the move. */
            await ExecuteAsync(
                connection,
                """
                INSERT INTO collect.query_plans (collection_time, server_id, query_hash, plan_xml)
                SELECT now() - (n || ' hours')::interval,
                       1 + (n % 4),
                       md5(n::text),
                       '<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">'
                       || repeat('<RelOp NodeId="' || n || '" PhysicalOp="Clustered Index Scan" LogicalOp="Index Scan" EstimateRows="1234.5"><OutputList><ColumnReference Database="[X]" Schema="[dbo]" Table="[T]" Column="C' || n || '" /></OutputList></RelOp>', 400)
                       || '</ShowPlanXML>'
                FROM generate_series(1, 500) AS g(n)
                """,
                cancellationToken);

            /* A continuous aggregate: its own materialization hypertable, catalog entries and refresh
               policy all have to survive pg_upgrade for the store to still work. */
            await ExecuteAsync(
                connection,
                """
                CREATE MATERIALIZED VIEW collect.collection_hourly
                WITH (timescaledb.continuous) AS
                SELECT time_bucket('1 hour', collection_time) AS bucket,
                       server_id,
                       count(*)             AS runs,
                       sum(rows_collected)  AS rows_collected
                FROM collect.collection_log
                GROUP BY 1, 2
                """,
                cancellationToken);

            /* Compression on the blob hypertable, then actually compress its chunks — a compressed chunk
               is a different on-disk shape, and "the upgrade kept the rows" has to hold for those too. */
            await ExecuteAsync(
                connection,
                "ALTER TABLE collect.query_plans SET (timescaledb.compress, timescaledb.compress_segmentby = 'server_id')",
                cancellationToken);
            await ExecuteAsync(
                connection,
                "SELECT compress_chunk(c) FROM show_chunks('collect.query_plans') AS c",
                cancellationToken);

            return major;
        }
        finally
        {
            await bootstrap.StopIfStartedByThisProcessAsync();
        }
    }

    /* ---------------- measurement ---------------- */

    private sealed record StoreSnapshot(
        int ServerMajor,
        string? TimescaleVersion,
        long LogRows,
        long PlanRows,
        long PlanXmlLength,
        string LogChecksum,
        long CaggRows,
        long CompressedChunks);

    /// <summary>
    /// The store's observable content, read identically before and after the upgrade. The checksum is an
    /// aggregate over every row's values, so a silently truncated or reordered restore fails the comparison
    /// even when the row COUNT happens to match.
    /// </summary>
    private static async Task<StoreSnapshot> MeasureStoreAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var major = await ReadServerMajorAsync(connection, cancellationToken);
        var timescale = await ScalarAsync<string>(
            connection, "SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'", cancellationToken);
        var logRows = await ScalarLongAsync(connection, "SELECT count(*) FROM collect.collection_log", cancellationToken);
        var planRows = await ScalarLongAsync(connection, "SELECT count(*) FROM collect.query_plans", cancellationToken);
        var planLength = await ScalarLongAsync(
            connection, "SELECT COALESCE(sum(length(plan_xml)), 0) FROM collect.query_plans", cancellationToken);
        var checksum = await ScalarAsync<string>(
            connection,
            """
            SELECT md5(string_agg(
                       collection_time::text || '|' || server_id || '|' || collector_name || '|' || status || '|' || rows_collected,
                       ',' ORDER BY collection_time, server_id, collector_name))
            FROM collect.collection_log
            """,
            cancellationToken);
        var caggRows = await ScalarLongAsync(connection, "SELECT count(*) FROM collect.collection_hourly", cancellationToken);
        var compressed = await ScalarLongAsync(
            connection,
            "SELECT count(*) FROM timescaledb_information.chunks WHERE is_compressed",
            cancellationToken);

        return new StoreSnapshot(major, timescale, logRows, planRows, planLength, checksum ?? string.Empty, caggRows, compressed);
    }

    private static async Task<int> ReadServerMajorAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var raw = await ScalarAsync<string>(connection, "SHOW server_version_num", cancellationToken);
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var num) ? num / 10000 : 0;
    }

    private static string? BundledTimescaleVersion(string runtimeRoot)
    {
        var control = Path.Combine(runtimeRoot, "pgsql", "share", "extension", "timescaledb.control");
        return File.Exists(control)
            ? DarlingStoreUpgrade.ParseTimescaleDefaultVersion(File.ReadAllText(control))
            : null;
    }

    /* ---------------- plumbing ---------------- */

    private static async Task StartWithRuntimeAsync(
        string runtimeRoot, string dataDirectory, int port, CancellationToken cancellationToken)
    {
        var exitCode = await DarlingManagedPostgres.RunDetachingToolAsync(
            Path.Combine(runtimeRoot, "pgsql", "bin", "pg_ctl.exe"),
            $"-D \"{dataDirectory}\" -o \"-p {port} -c listen_addresses=127.0.0.1\" -w -t 120 start",
            TimeSpan.FromMinutes(3),
            cancellationToken);
        Assert.Equal(0, exitCode);
    }

    private static async Task StopWithRuntimeAsync(string runtimeRoot, string dataDirectory, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await DarlingManagedPostgres.RunToolAsync(
            Path.Combine(runtimeRoot, "pgsql", "bin", "pg_ctl.exe"),
            $"stop -D \"{dataDirectory}\" -m fast -w -t 120",
            TimeSpan.FromMinutes(3),
            cancellationToken);
        Assert.True(exitCode == 0, $"pg_ctl stop failed: {output}");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
        where T : class
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
        return await command.ExecuteScalarAsync(cancellationToken) as T;
    }

    private static async Task<long> ScalarLongAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L, CultureInfo.InvariantCulture);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.Ordinal));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, destination, StringComparison.Ordinal), overwrite: true);
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            /* A leftover temp tree is not a test failure. */
        }
    }

    private static int FindFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
