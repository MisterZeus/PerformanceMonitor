/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The bundled-Postgres bootstrap (the shipped zero-admin default). Ungated: the derived
/// connection string, the postgresql.conf append pins (timescaledb preload, port, loopback
/// only), the generated password's shape, the DPAPI credential round-trip through the stored
/// file, and the data-directory/credential path conventions. Gated on DARLING_TEST_PGRUNTIME
/// (the path of an assembled pg-runtime directory — the folder containing
/// pgsql\bin\pg_ctl.exe; <c>fetch-pg-runtime.ps1 -KeepWork</c> leaves one at
/// artifacts\pg-runtime-work\assemble\pg-runtime): the full first-run story into a temp data
/// directory on a scratch port — initdb, start, create database, authenticate, then an
/// idempotent second EnsureRunning and an ownership-respecting stop — never touching a real
/// Postgres and never downloading anything.
/// </summary>
/* #1776 own-store: deliberately NOT [Collection("live-postgres")], and NOT for the reason a sweep might assume.
   This class never reads DARLING_TEST_PG at all — it reads DARLING_TEST_PGRUNTIME, which merely shares that
   prefix, and it stands up its OWN throwaway cluster from the bundled runtime. A substring search for
   "DARLING_TEST_PG" matches it anyway (that is how #1776's original sweep came to list it), so this note is here to
   stop the next one serializing a class that touches no shared store. */
public sealed class DarlingManagedPostgresTests
{
    [Fact]
    public void DerivedConnectionString_LocalhostPortDarlingDarling()
    {
        var parsed = new NpgsqlConnectionStringBuilder(DarlingManagedPostgres.BuildConnectionString(5641, "pw123"));

        /* Explicit IPv4 loopback (not the name "localhost"): listen_addresses binds 127.0.0.1 (plus the
           optional network IP when exposed), NOT ::1, so a host resolving "localhost" to IPv6 first could
           otherwise miss the listener (darling-network-endpoints). */
        Assert.Equal("127.0.0.1", parsed.Host);
        Assert.Equal(5641, parsed.Port);
        Assert.Equal("darling", parsed.Username);
        Assert.Equal("pw123", parsed.Password);
        Assert.Equal("darling", parsed.Database);

        /* V8 split: the owner connection string carries the collect/config search path so the
           service's bare-name COPY writes and reads resolve to the new schemas on every pooled
           connection, regardless of the database default. Same schemas, same order as the SQL-side
           PgSchemaGenerator.SearchPath. */
        Assert.Equal("collect,config,public", parsed.SearchPath);
        Assert.Equal(
            PerformanceMonitor.Darling.Storage.PgSchemaGenerator.SearchPath.Replace(" ", "", StringComparison.Ordinal),
            parsed.SearchPath);
    }

    [Fact]
    public void RoleCredentialPaths_BesideTheDataDirectory()
    {
        /* The admin/viewer role credentials live beside the data directory, same posture as the
           owner's pg-credential.dpapi (trailing separator tolerated). */
        Assert.Equal(@"D:\darling\pg-admin-credential.dpapi", DarlingManagedPostgres.AdminCredentialPathFor(@"D:\darling\pg"));
        Assert.Equal(@"D:\darling\pg-admin-credential.dpapi", DarlingManagedPostgres.AdminCredentialPathFor(@"D:\darling\pg\"));
        Assert.Equal(@"D:\darling\pg-viewer-credential.dpapi", DarlingManagedPostgres.ViewerCredentialPathFor(@"D:\darling\pg"));

        /* Three distinct files: owner, admin, viewer. */
        Assert.Equal("pg-credential.dpapi", DarlingManagedPostgres.CredentialFileName);
        Assert.Equal("pg-admin-credential.dpapi", DarlingManagedPostgres.AdminCredentialFileName);
        Assert.Equal("pg-viewer-credential.dpapi", DarlingManagedPostgres.ViewerCredentialFileName);
    }

    [Fact]
    public void ConfAppend_PinsPreloadPortAndLoopbackOnly()
    {
        var block = DarlingManagedPostgres.BuildConfAppend(5641);

        Assert.Contains(DarlingManagedPostgres.ConfMarker, block, StringComparison.Ordinal);
        Assert.Contains("shared_preload_libraries = 'timescaledb'", block, StringComparison.Ordinal);
        Assert.Contains("port = 5641", block, StringComparison.Ordinal);
        Assert.Contains("listen_addresses = '127.0.0.1'", block, StringComparison.Ordinal);

        /* Worker sizing lives in the v2 block and memory sizing in the v3 block, never in v1 — pre-v2/v3
           clusters heal by gaining the LATER blocks, so v1's content must stay stable. */
        Assert.DoesNotContain("max_worker_processes", block, StringComparison.Ordinal);
        Assert.DoesNotContain("shared_buffers", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// PostgreSQL's default max_worker_processes = 8 cannot launch the per-hypertable compression
    /// policy jobs (live smoke: "failed to start a background worker" storms). Pins the
    /// TimescaleDB-guidance sizing, DERIVED from the hypertable count so it never goes stale as
    /// collectors are added: background workers = hypertables + 2; max_worker_processes = 3 + that + 8.
    /// The count is <see cref="TimescaleSupport.HypertableCount"/> = the collector catalog PLUS collection_log
    /// (the V23 hypertable outside the catalog), so the collection_log compression policy is not under-provisioned.
    /// </summary>
    [Fact]
    public void WorkerSizingConfAppend_PinsV2MarkerAndSizing()
    {
        /* collection_log is a hypertable but lives OUTSIDE the collector catalog, so the true count is
           collectors + 1 — the worker sizing must derive from that, not HypertableTables.Count alone. */
        Assert.Equal(TimescaleSupport.HypertableTables.Count + 1, TimescaleSupport.HypertableCount);

        var block = DarlingManagedPostgres.BuildWorkerSizingConfAppend();
        var expectedBackgroundWorkers = TimescaleSupport.HypertableCount + 2;
        var expectedWorkerProcesses = 3 + expectedBackgroundWorkers + 8;

        Assert.Contains(DarlingManagedPostgres.ConfMarkerV2, block, StringComparison.Ordinal);
        Assert.Contains($"timescaledb.max_background_workers = {expectedBackgroundWorkers}", block, StringComparison.Ordinal);
        Assert.Contains($"max_worker_processes = {expectedWorkerProcesses}", block, StringComparison.Ordinal);

        /* v2 must not restate v1 settings — the blocks compose, they don't compete. */
        Assert.DoesNotContain("shared_preload_libraries", block, StringComparison.Ordinal);
        Assert.DoesNotContain("listen_addresses", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// SCALE-READINESS memory tuning (mirrors the worker sizing): the v3 block derives shared_buffers /
    /// effective_cache_size / maintenance_work_mem / work_mem from the host's physical RAM, injected here so
    /// the derivation is deterministic and unit-testable. 8 GB is the current DARLING01 box — none of the
    /// caps engage, so it exercises the raw percentages (and pins work_mem at its 16 MB floor).
    /// </summary>
    [Fact]
    public void MemorySizingConfAppend_PinsV3Marker_AndDerivesFrom8GbRam()
    {
        const long eightGb = 8L * 1024 * 1024 * 1024;
        var block = DarlingManagedPostgres.BuildMemorySizingConfAppend(eightGb);

        Assert.Contains(DarlingManagedPostgres.ConfMarkerV3, block, StringComparison.Ordinal);
        Assert.Contains("shared_buffers = 1024MB", block, StringComparison.Ordinal);        /* 25% of 8 GB, capped at the 1 GB co-located ceiling (#1559) */
        Assert.Contains("effective_cache_size = 6144MB", block, StringComparison.Ordinal);  /* 75% of 8 GB */
        Assert.Contains("maintenance_work_mem = 409MB", block, StringComparison.Ordinal);   /* 5% of 8 GB */
        Assert.Contains("work_mem = 16MB", block, StringComparison.Ordinal);                /* RAM/512, at the 16 MB floor */

        /* The blocks compose, they don't compete — v3 must not restate v1/v2 settings. */
        Assert.DoesNotContain("shared_preload_libraries", block, StringComparison.Ordinal);
        Assert.DoesNotContain("max_worker_processes", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// The v4 write-throughput block (24-server field incident): PG's default max_connections = 100
    /// and max_wal_size = 1GB are toy-sized — a fleet bootstrap's write burst forced back-to-back
    /// spread checkpoints while backend spawn churn surfaced as transient store write failures.
    /// Fixed values, deliberately not derived (WAL space is a ceiling, idle connections are cheap).
    /// </summary>
    [Fact]
    public void WriteThroughputConfAppend_PinsV4Marker_ConnectionsAndWalCeiling()
    {
        var block = DarlingManagedPostgres.BuildWriteThroughputConfAppend();

        Assert.Contains(DarlingManagedPostgres.ConfMarkerV4, block, StringComparison.Ordinal);
        Assert.Contains("max_connections = 200", block, StringComparison.Ordinal);
        Assert.Contains("max_wal_size = 4GB", block, StringComparison.Ordinal);

        /* The blocks compose, they don't compete — v4 must not restate v1/v2/v3 settings. */
        Assert.DoesNotContain("shared_preload_libraries", block, StringComparison.Ordinal);
        Assert.DoesNotContain("max_worker_processes", block, StringComparison.Ordinal);
        Assert.DoesNotContain("shared_buffers", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// The v5 co-located-sizing override (#1559): re-states shared_buffers at the CAPPED derivation so an
    /// existing cluster provisioned under the old min(25%, 8 GB) rule heals DOWN via conf
    /// last-occurrence-wins. Pins the marker, the capped value at two RAM tiers, and that the block
    /// restates NOTHING else (compose, don't compete).
    /// </summary>
    [Fact]
    public void ColocatedSizingConfAppend_PinsV5Marker_AndCappedSharedBuffers()
    {
        const long sixteenGb = 16L * 1024 * 1024 * 1024;
        var block = DarlingManagedPostgres.BuildColocatedSizingConfAppend(sixteenGb);
        Assert.Contains(DarlingManagedPostgres.ConfMarkerV5, block, StringComparison.Ordinal);
        Assert.Contains("shared_buffers = 1024MB", block, StringComparison.Ordinal); /* 25% of 16 GB, capped */

        const long twoGb = 2L * 1024 * 1024 * 1024;
        var small = DarlingManagedPostgres.BuildColocatedSizingConfAppend(twoGb);
        Assert.Contains("shared_buffers = 512MB", small, StringComparison.Ordinal);  /* under the cap: restated as-is */

        /* The blocks compose, they don't compete — v5 restates ONLY shared_buffers. */
        Assert.DoesNotContain("max_connections", block, StringComparison.Ordinal);
        Assert.DoesNotContain("work_mem", block, StringComparison.Ordinal);
        Assert.DoesNotContain("effective_cache_size", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// The v6 log-rotation block (#1652): the logging collector as a SELF-CAPPING weekday ring. The three
    /// settings that make the ring bounded are load-bearing together — %a weekday naming caps the set at
    /// seven files, truncate-on-rotation stops a weekday file from growing week over week, and
    /// rotation_size 0 keeps size rolls (which append rather than truncate) from defeating the ring.
    /// </summary>
    [Fact]
    public void LogRotationConfAppend_PinsV6Marker_AndTheSelfCappingWeekdayRing()
    {
        var block = DarlingManagedPostgres.BuildLogRotationConfAppend();

        Assert.Contains(DarlingManagedPostgres.ConfMarkerV6, block, StringComparison.Ordinal);
        Assert.Contains("logging_collector = on", block, StringComparison.Ordinal);
        Assert.Contains("log_directory = 'log'", block, StringComparison.Ordinal);
        Assert.Contains("log_filename = 'postgresql-%a.log'", block, StringComparison.Ordinal);
        Assert.Contains("log_rotation_age = 1d", block, StringComparison.Ordinal);
        Assert.Contains("log_rotation_size = 0", block, StringComparison.Ordinal);
        Assert.Contains("log_truncate_on_rotation = on", block, StringComparison.Ordinal);

        /* The blocks compose, they don't compete. */
        Assert.DoesNotContain("shared_buffers", block, StringComparison.Ordinal);
        Assert.DoesNotContain("max_connections", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1652: the diagnostics tail must follow the log wherever Postgres last wrote it — pg.log for
    /// pre-collector/startup failures, the v6 ring for a server that came up and then complained.
    /// </summary>
    [Fact]
    public void PickNewestServerLog_ChoosesTheNewestOfPgLogAndTheRing()
    {
        var root = Directory.CreateTempSubdirectory("darling-pglogpick-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            Directory.CreateDirectory(dataDirectory);
            var pgLog = Path.Combine(root.FullName, "pg.log");

            /* Nothing exists yet. */
            Assert.Null(DarlingManagedPostgres.PickNewestServerLog(pgLog, dataDirectory));

            /* Only pg.log — the pre-collector failure shape. */
            File.WriteAllText(pgLog, "FATAL: could not start");
            Assert.Equal(pgLog, DarlingManagedPostgres.PickNewestServerLog(pgLog, dataDirectory));

            /* The ring exists and is newer — the started-then-complained shape. */
            var ringDirectory = Path.Combine(dataDirectory, "log");
            Directory.CreateDirectory(ringDirectory);
            var ringFile = Path.Combine(ringDirectory, "postgresql-Mon.log");
            File.WriteAllText(ringFile, "ERROR: something after startup");
            File.SetLastWriteTimeUtc(pgLog, DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(ringFile, DateTime.UtcNow);
            Assert.Equal(ringFile, DarlingManagedPostgres.PickNewestServerLog(pgLog, dataDirectory));

            /* pg.log newer again (a fresh failed restart after the server had been up). */
            File.SetLastWriteTimeUtc(pgLog, DateTime.UtcNow.AddMinutes(5));
            Assert.Equal(pgLog, DarlingManagedPostgres.PickNewestServerLog(pgLog, dataDirectory));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// #1652: the one-time legacy cap — an oversized pre-rotation pg.log rolls to pg.log.old (replacing any
    /// prior roll, so the pair is bounded forever); a small file is left alone; a missing file is a no-op.
    /// </summary>
    [Fact]
    public void CapLegacyServerLog_RollsOnlyOversizedFiles()
    {
        var root = Directory.CreateTempSubdirectory("darling-pglogcap-");
        try
        {
            var pgLog = Path.Combine(root.FullName, "pg.log");
            var rolled = pgLog + ".old";

            /* Missing file: no-op, no throw. */
            DarlingManagedPostgres.CapLegacyServerLog(pgLog, capBytes: 10, logger: null);
            Assert.False(File.Exists(rolled));

            /* Under the cap: untouched. */
            File.WriteAllText(pgLog, "small");
            DarlingManagedPostgres.CapLegacyServerLog(pgLog, capBytes: 1024, logger: null);
            Assert.True(File.Exists(pgLog));
            Assert.False(File.Exists(rolled));

            /* Over the cap: rolled aside; a second oversized roll REPLACES the first (two files, ever). */
            File.WriteAllText(pgLog, new string('x', 2048));
            DarlingManagedPostgres.CapLegacyServerLog(pgLog, capBytes: 1024, logger: null);
            Assert.False(File.Exists(pgLog));
            Assert.True(File.Exists(rolled));

            File.WriteAllText(pgLog, new string('y', 4096));
            DarlingManagedPostgres.CapLegacyServerLog(pgLog, capBytes: 1024, logger: null);
            Assert.Equal(4096, new FileInfo(rolled).Length);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// On a big box every cap/ceiling engages: shared_buffers pins at 8 GB (not 25% = 16 GB),
    /// maintenance_work_mem at 1 GB (not 5% = 3.2 GB), work_mem at 64 MB (not RAM/512 = 128 MB);
    /// effective_cache_size stays the uncapped 75% planner hint.
    /// </summary>
    [Fact]
    public void MemorySizingConfAppend_EngagesCapsOnLargeRam()
    {
        const long sixtyFourGb = 64L * 1024 * 1024 * 1024;
        var block = DarlingManagedPostgres.BuildMemorySizingConfAppend(sixtyFourGb);

        Assert.Contains("shared_buffers = 1024MB", block, StringComparison.Ordinal);          /* capped at the 1 GB co-located ceiling (#1559) */
        Assert.Contains("effective_cache_size = 49152MB", block, StringComparison.Ordinal);   /* 75% of 64 GB, uncapped */
        Assert.Contains("maintenance_work_mem = 1024MB", block, StringComparison.Ordinal);    /* capped at 1 GB */
        Assert.Contains("work_mem = 64MB", block, StringComparison.Ordinal);                  /* capped at 64 MB */
    }

    /// <summary>
    /// The pure derivation across RAM tiers, pinning each formula and its cap/clamp. work_mem is the
    /// flagged per-connection setting: it scales RAM/512 and reaches the 64 MB ceiling at 32 GB, so the
    /// pathological max_connections × sorts × work_mem never grows past the ceiling on a bigger box.
    /// </summary>
    [Theory]
    [InlineData(2, 512, 1536, 102, 16)]      /* 2 GB: under every cap; work_mem at the 16 MB floor */
    [InlineData(8, 1024, 6144, 409, 16)]     /* 8 GB: shared_buffers hits the 1 GB co-located cap (#1559); work_mem at the floor */
    [InlineData(16, 1024, 12288, 819, 32)]   /* 16 GB: shared_buffers capped (the field box); work_mem RAM/512 = 32 MB */
    [InlineData(32, 1024, 24576, 1024, 64)]  /* 32 GB: shared_buffers at the 1 GB cap, maintenance hits 1 GB, work_mem hits the 64 MB ceiling */
    [InlineData(64, 1024, 49152, 1024, 64)]  /* 64 GB: everything but effective_cache_size capped; the planner hint keeps scaling */
    public void DeriveMemorySettings_PerTier(long ramGb, int sharedBuffersMb, int effectiveCacheMb, int maintenanceMb, int workMemMb)
    {
        var settings = DarlingManagedPostgres.DeriveMemorySettings(ramGb * 1024 * 1024 * 1024);

        Assert.Equal(sharedBuffersMb, settings.SharedBuffersMb);
        Assert.Equal(effectiveCacheMb, settings.EffectiveCacheSizeMb);
        Assert.Equal(maintenanceMb, settings.MaintenanceWorkMemMb);
        Assert.Equal(workMemMb, settings.WorkMemMb);
    }

    /// <summary>A zero/garbage RAM reading (the GlobalMemoryStatusEx failure path) falls back to a
    /// conservative 4 GB derivation rather than emitting a 0 MB / divide-by-nothing setting.</summary>
    [Fact]
    public void DeriveMemorySettings_NonPositiveRam_FallsBackToConservative4Gb()
    {
        var settings = DarlingManagedPostgres.DeriveMemorySettings(0);

        Assert.Equal(1024, settings.SharedBuffersMb);       /* 25% of the 4 GB fallback */
        Assert.Equal(3072, settings.EffectiveCacheSizeMb);  /* 75% of 4 GB */
        Assert.Equal(204, settings.MaintenanceWorkMemMb);   /* 5% of 4 GB */
        Assert.Equal(16, settings.WorkMemMb);               /* RAM/512 = 8 MB, lifted to the 16 MB floor */
    }

    [Fact]
    public void GeneratePassword_32AlphanumericCryptoRandom()
    {
        var first = DarlingManagedPostgres.GeneratePassword();
        var second = DarlingManagedPostgres.GeneratePassword();

        /* Alphanumeric-only by design (survives --pwfile and connection strings without
           escaping); the 32-char length carries the strength (~190 bits over a 62 charset). */
        Assert.Equal(32, first.Length);
        Assert.All(first, c => Assert.True(char.IsAsciiLetterOrDigit(c), $"unexpected password character '{c}'"));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void PathConventions_DefaultDataDirectory_AndCredentialBesideIt()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PerformanceMonitorDarling", "pg"),
            DarlingManagedPostgres.ResolveDataDirectory(new PostgresConfig()));

        /* The credential lives BESIDE the data directory (not inside it — initdb wants the
           directory empty), trailing separator tolerated. */
        Assert.Equal(@"D:\darling\pg-credential.dpapi", DarlingManagedPostgres.CredentialPathFor(@"D:\darling\pg"));
        Assert.Equal(@"D:\darling\pg-credential.dpapi", DarlingManagedPostgres.CredentialPathFor(@"D:\darling\pg\"));
    }

    [Fact]
    public void StoredCredential_DpapiRoundTrip_DerivesTheConnectionString()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-pgcred-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var config = new PostgresConfig { Managed = true, Port = 5991, DataDirectory = dataDirectory };

            /* No credential yet → null (the MCP host's first-boot wait relies on this). */
            Assert.Null(DarlingManagedPostgres.TryBuildConnectionStringFromStoredCredential(config));

            var password = DarlingManagedPostgres.GeneratePassword();
            var credentialPath = DarlingManagedPostgres.CredentialPathFor(dataDirectory);
            File.WriteAllText(credentialPath, DarlingSecrets.Protect(password));

            /* The blob on disk is never the plaintext. */
            Assert.DoesNotContain(password, File.ReadAllText(credentialPath), StringComparison.Ordinal);

            var derived = DarlingManagedPostgres.TryBuildConnectionStringFromStoredCredential(config);
            Assert.NotNull(derived);
            var parsed = new NpgsqlConnectionStringBuilder(derived);
            Assert.Equal(password, parsed.Password);
            Assert.Equal(5991, parsed.Port);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void McpCredentialPath_BesideTheDataDirectory_AndDistinctFile()
    {
        /* The mcp role credential lives beside the data directory, same posture as owner/admin/viewer
           (trailing separator tolerated) — a fourth distinct file (darling-network-endpoints, D3-role). */
        Assert.Equal(@"D:\darling\pg-mcp-credential.dpapi", DarlingManagedPostgres.McpCredentialPathFor(@"D:\darling\pg"));
        Assert.Equal(@"D:\darling\pg-mcp-credential.dpapi", DarlingManagedPostgres.McpCredentialPathFor(@"D:\darling\pg\"));
        Assert.Equal("pg-mcp-credential.dpapi", DarlingManagedPostgres.McpCredentialFileName);
        Assert.Equal("mcp", DarlingManagedPostgres.McpRoleName);
    }

    [Fact]
    public void McpStoredCredential_DpapiRoundTrip_DerivesMcpConnectionString()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI requires Windows.");

        var root = Directory.CreateTempSubdirectory("darling-mcpcred-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            var config = new PostgresConfig { Managed = true, Port = 5992, DataDirectory = dataDirectory };

            /* No mcp credential yet → null (the MCP host polls for it after the worker provisions it). */
            Assert.Null(DarlingManagedPostgres.TryBuildMcpConnectionStringFromStoredCredential(config));

            var password = DarlingManagedPostgres.GeneratePassword();
            File.WriteAllText(DarlingManagedPostgres.McpCredentialPathFor(dataDirectory), DarlingSecrets.Protect(password));

            var derived = DarlingManagedPostgres.TryBuildMcpConnectionStringFromStoredCredential(config);
            Assert.NotNull(derived);
            var parsed = new NpgsqlConnectionStringBuilder(derived);

            /* The mcp pool connects as the mcp role over the explicit IPv4 loopback, same search path. */
            Assert.Equal("127.0.0.1", parsed.Host);
            Assert.Equal(5992, parsed.Port);
            Assert.Equal("mcp", parsed.Username);
            Assert.Equal(password, parsed.Password);
            Assert.Equal("darling", parsed.Database);
            Assert.Equal("collect,config,public", parsed.SearchPath);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void CertificateSanCoversIp_TrueForItsIpSan_FalseForOthers()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Parse("192.168.1.205"));
        san.AddDnsName("test-host");
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        Assert.True(DarlingManagedPostgres.CertificateSanCoversIp(cert, IPAddress.Parse("192.168.1.205")));
        Assert.False(DarlingManagedPostgres.CertificateSanCoversIp(cert, IPAddress.Parse("192.168.1.206")));
        Assert.False(DarlingManagedPostgres.CertificateSanCoversIp(cert, IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void EnsureServerCertificate_ReusesWhenSanCoversIp_RegeneratesOnIpChange()
    {
        /* The key hardening (DarlingFileSecurity.HardenFile) is Windows-only. */
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Cert key hardening is Windows-only.");

        var root = Directory.CreateTempSubdirectory("darling-cert-");
        try
        {
            var dataDirectory = Path.Combine(root.FullName, "pg");
            Directory.CreateDirectory(dataDirectory);
            var config = new PostgresConfig { Managed = true, Port = 5993, DataDirectory = dataDirectory };
            var pg = new DarlingManagedPostgres(config, NullLogger.Instance);

            /* The cert/key live beside the data directory, same convention as the credential files. */
            var certPath = Path.Combine(root.FullName, DarlingManagedPostgres.ServerCertFileName);
            var keyPath = Path.Combine(root.FullName, DarlingManagedPostgres.ServerKeyFileName);

            /* First generation for IP A. */
            pg.EnsureServerCertificate(IPAddress.Parse("192.168.1.205"), certPath, keyPath);
            Assert.True(File.Exists(certPath) && File.Exists(keyPath));
            var certA = File.ReadAllBytes(certPath);

            /* Same IP -> reuse (SAN covers it), bytes unchanged. */
            pg.EnsureServerCertificate(IPAddress.Parse("192.168.1.205"), certPath, keyPath);
            Assert.Equal(certA, File.ReadAllBytes(certPath));

            /* Different IP -> regenerate (stale SAN would break verify-full); the new cert covers B. */
            pg.EnsureServerCertificate(IPAddress.Parse("192.168.1.206"), certPath, keyPath);
            var certB = File.ReadAllBytes(certPath);
            Assert.NotEqual(certA, certB);
            using var reloaded = X509Certificate2.CreateFromPem(File.ReadAllText(certPath));
            Assert.True(DarlingManagedPostgres.CertificateSanCoversIp(reloaded, IPAddress.Parse("192.168.1.206")));
            Assert.False(DarlingManagedPostgres.CertificateSanCoversIp(reloaded, IPAddress.Parse("192.168.1.205")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Bootstrap_EndToEnd_FirstRunThenIdempotentSecondRun_Gated()
    {
        var runtimeRoot = Environment.GetEnvironmentVariable("DARLING_TEST_PGRUNTIME");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(runtimeRoot),
            "Set DARLING_TEST_PGRUNTIME to an assembled pg-runtime directory (the folder containing pgsql\\bin\\pg_ctl.exe; " +
            "Darling\\tools\\fetch-pg-runtime.ps1 -KeepWork leaves one under artifacts\\pg-runtime-work\\assemble\\pg-runtime) " +
            "to run the managed-Postgres bootstrap E2E.");
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The bundled runtime is Windows-only.");
        Assert.SkipUnless(File.Exists(Path.Combine(runtimeRoot!, "pgsql", "bin", "pg_ctl.exe")),
            $"DARLING_TEST_PGRUNTIME={runtimeRoot} does not contain pgsql\\bin\\pg_ctl.exe.");

        var root = Directory.CreateTempSubdirectory("darling-pgboot-");
        var dataDirectory = Path.Combine(root.FullName, "pg");
        var config = new PostgresConfig
        {
            Managed = true,
            Port = FindFreeTcpPort(),
            DataDirectory = dataDirectory,
        };

        var owner = new DarlingManagedPostgres(config, NullLogger.Instance, runtimeRoot);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            /* First run: initdb (scram + generated credential) + conf append + start + create db. */
            var connectionString = await owner.EnsureRunningAsync(timeout.Token);
            Assert.True(owner.StartedByThisProcess);
            Assert.True(File.Exists(Path.Combine(dataDirectory, "PG_VERSION")));

            var credentialPath = DarlingManagedPostgres.CredentialPathFor(dataDirectory);
            Assert.True(File.Exists(credentialPath));
            var credentialBytes = File.ReadAllBytes(credentialPath);

            var conf = File.ReadAllText(Path.Combine(dataDirectory, "postgresql.conf"));
            Assert.Contains("shared_preload_libraries = 'timescaledb'", conf, StringComparison.Ordinal);
            Assert.Contains("listen_addresses = '127.0.0.1'", conf, StringComparison.Ordinal);
            /* HypertableCount, not HypertableTables.Count: the product sizes workers from the TRUE
               hypertable count (catalog + collection_log, the V23 non-catalog hypertable). */
            Assert.Contains($"max_worker_processes = {3 + (TimescaleSupport.HypertableCount + 2) + 8}", conf, StringComparison.Ordinal);

            /* v3 memory sizing rode the SAME append path on first run, derived from THIS host's physical RAM
               (the exact MB depend on the runner, so pin the marker + that the settings are present). */
            Assert.Contains(DarlingManagedPostgres.ConfMarkerV3, conf, StringComparison.Ordinal);
            Assert.Contains("shared_buffers = ", conf, StringComparison.Ordinal);
            Assert.Contains("work_mem = ", conf, StringComparison.Ordinal);

            /* v4 write throughput rode the same first-run append: connection headroom + WAL ceiling. */
            Assert.Contains(DarlingManagedPostgres.ConfMarkerV4, conf, StringComparison.Ordinal);

            /* v5 co-located sizing rode the same first-run append (its shared_buffers override equals the
               v3 value on a fresh cluster, since both now derive through the same 1 GB cap). */
            Assert.Contains(DarlingManagedPostgres.ConfMarkerV5, conf, StringComparison.Ordinal);
            Assert.Contains("max_connections = 200", conf, StringComparison.Ordinal);
            Assert.Contains("max_wal_size = 4GB", conf, StringComparison.Ordinal);

            /* v6 log rotation rode the same first-run append, and the server ACCEPTED it (a bad line here
               fails pg_ctl start outright) — the logging collector is live, proven by the weekday ring file
               it creates under <data>\log the moment it starts (#1652). */
            Assert.Contains(DarlingManagedPostgres.ConfMarkerV6, conf, StringComparison.Ordinal);
            var ringFiles = Directory.GetFiles(Path.Combine(dataDirectory, "log"), "postgresql-*.log");
            Assert.NotEmpty(ringFiles);

            /* The derived credential really authenticates (scram, not trust) into the darling
               database — and the server started with our appended conf, so the timescaledb
               preload line was accepted; the v2 worker sizing was accepted too (the setting is
               live, not just written). */
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(timeout.Token);
                using var current = new NpgsqlCommand(
                    "SELECT current_database(), current_user, current_setting('max_worker_processes'), current_setting('work_mem'), current_setting('shared_buffers')",
                    connection);
                using var reader = await current.ExecuteReaderAsync(timeout.Token);
                Assert.True(await reader.ReadAsync(timeout.Token));
                Assert.Equal("darling", reader.GetString(0));
                Assert.Equal("darling", reader.GetString(1));
                /* Derived, not hard-pinned (a "40" pin from the 27-hypertable era went stale when
                   collectors were added): the same HypertableCount formula BuildWorkerSizingConfAppend
                   writes into the conf, proven LIVE here. */
                Assert.Equal((3 + (TimescaleSupport.HypertableCount + 2) + 8).ToString(CultureInfo.InvariantCulture), reader.GetString(2));
                /* The v3 memory block is LIVE, not merely written: work_mem and shared_buffers hold our
                   derived values (>= the 16 MB work_mem floor / 25%-of-RAM shared_buffers on any real host),
                   never the stock 4 MB / 128 MB defaults. */
                Assert.NotEqual("4MB", reader.GetString(3));
                Assert.NotEqual("128MB", reader.GetString(4));
            }

            /* Second EnsureRunning against the live server: idempotent — no re-init (credential
               bytes untouched), no ownership grab (this instance did not start the server),
               the same derived connection string, and no duplicate conf blocks (one v1 marker,
               one v2 marker). */
            var second = new DarlingManagedPostgres(config, NullLogger.Instance, runtimeRoot);
            var secondConnectionString = await second.EnsureRunningAsync(timeout.Token);
            Assert.False(second.StartedByThisProcess);
            Assert.Equal(credentialBytes, File.ReadAllBytes(credentialPath));
            Assert.Equal(connectionString, secondConnectionString);

            var confAfterSecond = File.ReadAllText(Path.Combine(dataDirectory, "postgresql.conf"));
            Assert.Equal(1, CountOccurrences(confAfterSecond, DarlingManagedPostgres.ConfMarker));
            Assert.Equal(1, CountOccurrences(confAfterSecond, DarlingManagedPostgres.ConfMarkerV2));
            Assert.Equal(1, CountOccurrences(confAfterSecond, DarlingManagedPostgres.ConfMarkerV3));
            Assert.Equal(1, CountOccurrences(confAfterSecond, DarlingManagedPostgres.ConfMarkerV4));
            Assert.Equal(1, CountOccurrences(confAfterSecond, DarlingManagedPostgres.ConfMarkerV5));
            Assert.Equal(1, CountOccurrences(confAfterSecond, DarlingManagedPostgres.ConfMarkerV6));

            /* Both up/down probes below must bypass Npgsql's pool: OpenAsync on a pooled string
               can hand back an idle socket with no I/O at all, which "succeeds" against a stopped
               server — the refused-connection assert below failed exactly that way live. */
            var unpooled = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

            /* A non-owner's stop must be a no-op — the server keeps accepting connections. */
            await second.StopIfStartedByThisProcessAsync();
            await using (var stillUp = new NpgsqlConnection(unpooled))
            {
                await stillUp.OpenAsync(timeout.Token);
            }

            /* The owner's stop is real: fast shutdown, then connections are refused. */
            await owner.StopIfStartedByThisProcessAsync();
            Assert.False(owner.StartedByThisProcess);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await using var refused = new NpgsqlConnection(unpooled);
                await refused.OpenAsync(timeout.Token);
            });
        }
        finally
        {
            /* Idempotent when the happy path already stopped it; the safety net when an assert
               threw mid-flight. */
            await owner.StopIfStartedByThisProcessAsync();
            TryDeleteRecursive(root.FullName);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static int FindFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Postgres releases its files a beat after fast shutdown — retry the temp-dir delete.</summary>
    private static void TryDeleteRecursive(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }
}
