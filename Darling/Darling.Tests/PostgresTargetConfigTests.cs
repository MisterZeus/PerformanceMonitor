/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using Npgsql;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the config-to-connection path for a PostgreSQL target: how the engine is declared, that
/// omitting it cannot change an existing server's behaviour, and the connection posture the builder
/// produces.
/// </summary>
public class PostgresTargetConfigTests
{
    private static MonitoredServer PgServer() => new()
    {
        Name = "aurora-writer",
        Engine = "postgres",
        Host = "segments-multi-1.cluster-x.us-east-1.rds.amazonaws.com",
        Auth = "sql",
        Username = "collector",
    };

    /// <summary>
    /// The default matters more than the parsing: every darling.json in the field omits "engine", and
    /// omitting it must keep those entries on the SQL Server path exactly as before.
    /// </summary>
    [Fact]
    public void DefaultsToSqlServerWhenEngineIsAbsent()
    {
        var server = new MonitoredServer { Host = "SQL2022" };

        Assert.Equal(CollectorTargetEngine.SqlServer, server.TargetEngine);
        Assert.False(server.IsPostgres);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("postgresql")]
    [InlineData("pg")]
    [InlineData("aurora-postgresql")]
    [InlineData("aurora")]
    [InlineData("  Postgres  ")]
    [InlineData("POSTGRESQL")]
    public void RecognizesThePostgresSpellings(string engine)
    {
        Assert.Equal(CollectorTargetEngine.PostgreSql, new MonitoredServer { Engine = engine }.TargetEngine);
    }

    /// <summary>
    /// A typo resolves to SQL Server rather than throwing: one bad server entry must not stop the
    /// service from starting and monitoring every other server. The mismatch is loud anyway — the SQL
    /// Server detection query fails immediately against a Postgres host.
    /// </summary>
    [Theory]
    [InlineData("postgrez")]
    [InlineData("mysql")]
    [InlineData("")]
    public void FallsBackToSqlServerOnAnUnrecognizedEngine(string engine)
    {
        Assert.Equal(CollectorTargetEngine.SqlServer, new MonitoredServer { Engine = engine }.TargetEngine);
    }

    [Fact]
    public void BuildsAPostgresConnectionStringWithTheIntendedPosture()
    {
        var raw = MonitoredServerConnection.BuildConnectionString(PgServer(), "secret");
        var built = new NpgsqlConnectionStringBuilder(raw);

        Assert.Equal("segments-multi-1.cluster-x.us-east-1.rds.amazonaws.com", built.Host);
        Assert.Equal("postgres", built.Database);          // maintenance DB when none is configured
        Assert.Equal("collector", built.Username);
        Assert.Equal("PerformanceMonitorDarling", built.ApplicationName);
        Assert.Equal(15, built.Timeout);                    // same connect budget as the SQL Server path
        Assert.Equal(60, built.CommandTimeout);
        Assert.Equal(SslMode.VerifyFull, built.SslMode);    // fail-closed by default
    }

    [Fact]
    public void UsesTheConfiguredDatabaseWhenOneIsGiven()
    {
        var server = PgServer();
        server.Database = "payment_processing";

        var built = new NpgsqlConnectionStringBuilder(MonitoredServerConnection.BuildConnectionString(server, "secret"));

        Assert.Equal("payment_processing", built.Database);
    }

    /// <summary>
    /// pg_stat_statements lives in the app database on some of our clusters rather than in postgres,
    /// so pointing an entry at a specific database is a supported configuration, not an edge case.
    /// </summary>
    [Fact]
    public void HonorsANonDefaultPort()
    {
        var server = PgServer();
        server.Port = 5433;

        Assert.Equal(5433, new NpgsqlConnectionStringBuilder(
            MonitoredServerConnection.BuildConnectionString(server, "secret")).Port);
    }

    [Fact]
    public void DefaultsToTheStandardPortWhenNoneIsConfigured()
    {
        Assert.Equal(5432, new NpgsqlConnectionStringBuilder(
            MonitoredServerConnection.BuildConnectionString(PgServer(), "secret")).Port);
    }

    /// <summary>
    /// TrustServerCertificate relaxes verification without abandoning TLS — Aurora presents an RDS CA
    /// a stock trust store does not know, which is exactly the case this covers.
    /// </summary>
    [Fact]
    public void TrustServerCertificateRelaxesVerificationButKeepsTls()
    {
        var server = PgServer();
        server.TrustServerCertificate = true;

        Assert.Equal(SslMode.Require, new NpgsqlConnectionStringBuilder(
            MonitoredServerConnection.BuildConnectionString(server, "secret")).SslMode);
    }

    [Fact]
    public void OptionalEncryptModeDowngradesToPrefer()
    {
        var server = PgServer();
        server.EncryptMode = "optional";

        Assert.Equal(SslMode.Prefer, new NpgsqlConnectionStringBuilder(
            MonitoredServerConnection.BuildConnectionString(server, "secret")).SslMode);
    }

    /// <summary>
    /// Integrated auth is rejected loudly rather than producing a connection string that cannot
    /// authenticate and failing later, further from the cause.
    /// </summary>
    [Fact]
    public void RejectsIntegratedAuthForPostgresTargets()
    {
        var server = PgServer();
        server.Auth = "integrated";

        var ex = Assert.Throws<InvalidOperationException>(
            () => MonitoredServerConnection.BuildConnectionString(server, null));
        Assert.Contains("PostgreSQL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsSqlAuthWithNoResolvedPassword()
    {
        Assert.Throws<InvalidOperationException>(
            () => MonitoredServerConnection.BuildConnectionString(PgServer(), null));
    }

    /// <summary>
    /// A Postgres entry still derives its identity through the shared rule, so it lands in the same
    /// server registry with a server_id computed the same way as every SQL Server entry.
    /// </summary>
    [Fact]
    public void DerivesStorageIdentityThroughTheSharedRule()
    {
        var server = PgServer();
        server.Database = "segments_horizon";
        server.ReadOnlyIntent = true;

        Assert.Equal(
            "segments-multi-1.cluster-x.us-east-1.rds.amazonaws.com:segments_horizon:RO",
            server.StorageName);
    }

    /// <summary>
    /// The detection query must only touch surfaces a pg_monitor-grade login can read, and must not
    /// depend on version() text formatting.
    /// </summary>
    [Fact]
    public void PostgresDetectionQueryUsesPortableSurfaces()
    {
        var sql = DarlingServerConnector.PostgresDetectionQueryText;

        Assert.Contains("server_version_num", sql, StringComparison.Ordinal);
        Assert.Contains("pg_is_in_recovery()", sql, StringComparison.Ordinal);
        Assert.Contains("aurora_version", sql, StringComparison.Ordinal);
        // Aurora detection must not hard-fail on stock PostgreSQL, so it is a pg_proc lookup.
        Assert.Contains("pg_proc", sql, StringComparison.Ordinal);
        // No T-SQL leaked into the Postgres path.
        Assert.DoesNotContain("SERVERPROPERTY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@@VERSION", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static DarlingConfig ConfigWith(MonitoredServer server)
    {
        var config = new DarlingConfig();
        config.Postgres.ConnectionString = "Host=localhost;Database=darling;Username=x;Password=y";
        config.Servers.Add(server);
        return config;
    }

    /// <summary>
    /// The pre-flight has to reject integrated auth on a PostgreSQL target. The connection builder
    /// throws on it too, but that fires at first connect — for a service, hours after deployment and
    /// only in a log, where --test-connection would have said so before install.
    /// </summary>
    [Fact]
    public void ValidationRejectsIntegratedAuthOnAPostgresTarget()
    {
        var problems = ConfigWith(new MonitoredServer
        {
            Name = "aurora-writer",
            Engine = "postgres",
            Host = "aurora.cluster-x.us-east-1.rds.amazonaws.com",
            Auth = "integrated",
        }).Validate();

        Assert.Contains(problems, p => p.Contains("PostgreSQL target requires auth 'sql'", StringComparison.Ordinal));
    }

    /// <summary>Integrated auth stays perfectly valid on a SQL Server entry — the new rule is engine-scoped.</summary>
    [Fact]
    public void ValidationStillAllowsIntegratedAuthOnASqlServerTarget()
    {
        var problems = ConfigWith(new MonitoredServer { Name = "SQL2022", Host = "SQL2022", Auth = "integrated" })
            .Validate();

        Assert.Empty(problems);
    }

    /// <summary>A fully specified Postgres entry passes, so the new rules cannot reject a good config.</summary>
    [Fact]
    public void ValidationAcceptsAWellFormedPostgresTarget()
    {
        var server = PgServer();
        server.Password = "env:PGPASSWORD";
        server.Port = 5432;

        Assert.Empty(ConfigWith(server).Validate());
    }

    /// <summary>
    /// 0 is the documented "use the driver's default" value and must not be flagged; a real out-of-range
    /// port must be. Left unvalidated, a typo'd port surfaces as a connect timeout, which reads like a
    /// firewall problem and gets escalated to the wrong team.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(5432, false)]
    [InlineData(65535, false)]
    [InlineData(-1, true)]
    [InlineData(65536, true)]
    public void ValidationRangeChecksTheOptionalPort(int port, bool expectProblem)
    {
        var server = PgServer();
        server.Password = "env:PGPASSWORD";
        server.Port = port;

        var problems = ConfigWith(server).Validate();

        Assert.Equal(expectProblem, problems.Any(p => p.Contains("port must be between", StringComparison.Ordinal)));
    }
}
