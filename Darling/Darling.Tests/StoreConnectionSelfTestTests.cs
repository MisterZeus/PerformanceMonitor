/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using System.Security.Authentication;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace PerformanceMonitor.Darling.Tests;

/// <summary>
/// Pins the #1954 layered store-connection self-test. The pure halves are the ones that must not
/// drift: <see cref="StoreConnectionSelfTest.ClassifyOpenFailure"/> (the TLS-vs-auth attribution
/// table for a failed Open — one network exchange, attributed by exception shape),
/// <see cref="StoreConnectionSelfTest.EvaluateSchemaGate"/> (whose OLDER/NEWER wording tells the
/// operator WHICH half to upgrade), and <see cref="StoreConnectionSelfTest.FormatReport"/> (whose
/// verdict must name the FIRST failed layer and must not overclaim skipped ones). The gated live
/// half runs the real ladder against dev Postgres: the happy path, the by-elimination TCP arm, and
/// the unknown-database auth arm (chosen over a wrong password because CI's Postgres trusts local
/// connections — a wrong password would still authenticate there; the 28P01 shape is pinned purely
/// instead).
/// </summary>
public sealed class StoreConnectionSelfTestTests
{
    /* ---------------- ClassifyOpenFailure: the attribution table ---------------- */

    [Theory]
    [InlineData("28P01", StoreConnectionSelfTest.AuthLayer)] // invalid_password
    [InlineData("28000", StoreConnectionSelfTest.AuthLayer)] // invalid_authorization_specification
    [InlineData("3D000", StoreConnectionSelfTest.AuthLayer)] // invalid_catalog_name (post-auth: server said so)
    [InlineData("53300", StoreConnectionSelfTest.AuthLayer)] // any server-spoken state: session was rejected, not the handshake
    public void ClassifyOpenFailure_PostgresSqlStates_AttributeToAuth(string sqlState, string expectedLayer)
    {
        var (layer, detail) = StoreConnectionSelfTest.ClassifyOpenFailure(
            new PostgresException("server said no", "ERROR", "ERROR", sqlState));

        Assert.Equal(expectedLayer, layer);
        Assert.Contains(sqlState, detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyOpenFailure_NestedPostgresException_IsFoundThroughTheWrapper()
    {
        var wrapped = new NpgsqlException("outer wrapper",
            new PostgresException("password authentication failed for user \"darling\"", "FATAL", "FATAL", "28P01"));

        var (layer, detail) = StoreConnectionSelfTest.ClassifyOpenFailure(wrapped);

        Assert.Equal(StoreConnectionSelfTest.AuthLayer, layer);
        Assert.Contains("darling", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyOpenFailure_AuthenticationException_AttributesToTls()
    {
        var wrapped = new NpgsqlException("exception while performing SSL handshake",
            new AuthenticationException("The remote certificate is invalid according to the validation procedure."));

        var (layer, detail) = StoreConnectionSelfTest.ClassifyOpenFailure(wrapped);

        Assert.Equal(StoreConnectionSelfTest.TlsLayer, layer);
        Assert.Contains("certificate", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyOpenFailure_SslWordedMessage_AttributesToTls()
    {
        var (layer, _) = StoreConnectionSelfTest.ClassifyOpenFailure(
            new NpgsqlException("SSL connection is required by the server"));

        Assert.Equal(StoreConnectionSelfTest.TlsLayer, layer);
    }

    [Fact]
    public void ClassifyOpenFailure_Unrecognized_DefaultsToTlsByElimination_AndSaysSo()
    {
        var (layer, detail) = StoreConnectionSelfTest.ClassifyOpenFailure(
            new InvalidOperationException("something novel"));

        Assert.Equal(StoreConnectionSelfTest.TlsLayer, layer);
        Assert.Contains("unclassified", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("something novel", detail, StringComparison.Ordinal);
    }

    /* ---------------- EvaluateSchemaGate: which half upgrades ---------------- */

    [Fact]
    public void SchemaGate_EqualVersions_Pass()
    {
        var (outcome, detail) = StoreConnectionSelfTest.EvaluateSchemaGate(
            StorageVersion.SchemaVersion, StorageVersion.SchemaVersion);

        Assert.Equal(StoreConnectionSelfTest.LayerOutcome.Passed, outcome);
        Assert.Contains($"v{StorageVersion.SchemaVersion}", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaGate_OlderStore_TellsTheOperatorTheServiceMigrates()
    {
        var (outcome, detail) = StoreConnectionSelfTest.EvaluateSchemaGate(10, 48);

        Assert.Equal(StoreConnectionSelfTest.LayerOutcome.Failed, outcome);
        Assert.Contains("OLDER", detail, StringComparison.Ordinal);
        Assert.Contains("service", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SchemaGate_NewerStore_TellsTheOperatorToUpgradeTheViewer()
    {
        var (outcome, detail) = StoreConnectionSelfTest.EvaluateSchemaGate(99, 48);

        Assert.Equal(StoreConnectionSelfTest.LayerOutcome.Failed, outcome);
        Assert.Contains("NEWER", detail, StringComparison.Ordinal);
        Assert.Contains("viewer", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SchemaGate_NeverStamped_FailsWithStartTheService()
    {
        var (outcome, detail) = StoreConnectionSelfTest.EvaluateSchemaGate(0, 48);

        Assert.Equal(StoreConnectionSelfTest.LayerOutcome.Failed, outcome);
        Assert.Contains("never migrated", detail, StringComparison.Ordinal);
    }

    /* ---------------- FormatReport: verdict honesty ---------------- */

    [Fact]
    public void FormatReport_NamesTheFirstFailedLayer()
    {
        var report = StoreConnectionSelfTest.FormatReport(new[]
        {
            new StoreConnectionSelfTest.LayerResult(StoreConnectionSelfTest.ConfigLayer, StoreConnectionSelfTest.LayerOutcome.Passed, "ok", TimeSpan.Zero),
            new StoreConnectionSelfTest.LayerResult(StoreConnectionSelfTest.DnsLayer, StoreConnectionSelfTest.LayerOutcome.Failed, "no", TimeSpan.Zero),
            new StoreConnectionSelfTest.LayerResult(StoreConnectionSelfTest.TcpLayer, StoreConnectionSelfTest.LayerOutcome.NotReached, "an earlier layer failed", TimeSpan.Zero)
        });

        Assert.StartsWith(StoreConnectionSelfTest.ReportHeader, report, StringComparison.Ordinal);
        Assert.Contains("Verdict: failed at the dns layer.", report, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatReport_AllPassed_SaysSo_ButSkippedLayersAreNotOverclaimed()
    {
        var allPassed = StoreConnectionSelfTest.FormatReport(new[]
        {
            new StoreConnectionSelfTest.LayerResult(StoreConnectionSelfTest.ConfigLayer, StoreConnectionSelfTest.LayerOutcome.Passed, "ok", TimeSpan.Zero)
        });
        Assert.Contains("all layers passed", allPassed, StringComparison.Ordinal);

        var withSkips = StoreConnectionSelfTest.FormatReport(new[]
        {
            new StoreConnectionSelfTest.LayerResult(StoreConnectionSelfTest.ConfigLayer, StoreConnectionSelfTest.LayerOutcome.Passed, "ok", TimeSpan.Zero),
            new StoreConnectionSelfTest.LayerResult(StoreConnectionSelfTest.TlsLayer, StoreConnectionSelfTest.LayerOutcome.Skipped, "unproven under Prefer", TimeSpan.Zero)
        });
        Assert.DoesNotContain("all layers passed", withSkips, StringComparison.Ordinal);
        Assert.Contains("no layer failed", withSkips, StringComparison.Ordinal);
        Assert.Contains("tls", withSkips, StringComparison.Ordinal);
    }

    /* ---------------- RunAsync: the no-network shapes ---------------- */

    [Fact]
    public async Task RunAsync_GarbageConnectionString_FailsConfig_EverythingElseNotReached()
    {
        var results = await StoreConnectionSelfTest.RunAsync("this is not a connection string");

        Assert.Equal(6, results.Count);
        Assert.Equal(StoreConnectionSelfTest.LayerOutcome.Failed, results[0].Outcome);
        Assert.Equal(StoreConnectionSelfTest.ConfigLayer, results[0].Layer);
        Assert.All(results.Skip(1), r => Assert.Equal(StoreConnectionSelfTest.LayerOutcome.NotReached, r.Outcome));
    }

    [Fact]
    public async Task RunAsync_ParsesButNoHost_FailsConfig()
    {
        var results = await StoreConnectionSelfTest.RunAsync("Username=darling;Database=darling");

        Assert.Equal(StoreConnectionSelfTest.LayerOutcome.Failed, results[0].Outcome);
        Assert.Contains("no Host", results[0].Detail, StringComparison.Ordinal);
    }
}

/// <summary>
/// The gated live ladder against dev Postgres: the happy path proves every layer end-to-end
/// against a really-migrated store (schema verdict included); the wrong-port arm proves the
/// by-elimination firewall signature (DNS passed or skipped, TCP failed, nothing later reached);
/// the unknown-database arm proves the server-spoken auth attribution with a real 3D000.
/// </summary>
[Collection("live-postgres")]
public sealed class StoreConnectionSelfTestLiveTests
{
    [Fact]
    public async Task LiveLadder_HappyPath_WrongPort_UnknownDatabase_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live self-test ladder.");

        var ct = TestContext.Current.CancellationToken;

        /* Migrate first so the schema layer has a stamped store to agree with. */
        using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(ct);
            await PgMigrations.MigrateAsync(connection, ct);
        }

        /* ---- happy path: nothing fails; schema PASSES against the just-migrated store
                (the owner connection can read the version table directly). */
        var happy = await StoreConnectionSelfTest.RunAsync(connectionString!, ct);
        Assert.DoesNotContain(happy, r => r.Outcome == StoreConnectionSelfTest.LayerOutcome.Failed);
        var schema = happy.Single(r => r.Layer == StoreConnectionSelfTest.SchemaLayer);
        Assert.Equal(StoreConnectionSelfTest.LayerOutcome.Passed, schema.Outcome);
        Assert.Contains($"v{StorageVersion.SchemaVersion}", schema.Detail, StringComparison.Ordinal);

        /* ---- wrong port: DNS fine (or skipped for an IP literal), TCP fails, everything
                after NotReached — the by-elimination firewall signature. */
        var wrongPort = new NpgsqlConnectionStringBuilder(connectionString) { Port = 1, Timeout = 4 };
        var tcpArm = await StoreConnectionSelfTest.RunAsync(wrongPort.ConnectionString, ct);
        Assert.Equal(StoreConnectionSelfTest.LayerOutcome.Failed,
            tcpArm.Single(r => r.Layer == StoreConnectionSelfTest.TcpLayer).Outcome);
        Assert.Equal(StoreConnectionSelfTest.LayerOutcome.NotReached,
            tcpArm.Single(r => r.Layer == StoreConnectionSelfTest.AuthLayer).Outcome);

        /* ---- unknown database: TCP and TLS fine, the SERVER rejects with 3D000 — attributed to
                the auth layer with the server's own words. (A wrong password cannot be the live
                arm here: CI's Postgres trusts local connections, so it would still authenticate;
                the 28P01 shape is pinned in the pure classification tests instead.) */
        var wrongDatabase = new NpgsqlConnectionStringBuilder(connectionString) { Database = "darling_selftest_no_such_db" };
        var authArm = await StoreConnectionSelfTest.RunAsync(wrongDatabase.ConnectionString, ct);
        var auth = authArm.Single(r => r.Layer == StoreConnectionSelfTest.AuthLayer);
        Assert.Equal(StoreConnectionSelfTest.LayerOutcome.Failed, auth.Outcome);
        Assert.Contains("3D000", auth.Detail, StringComparison.Ordinal);
        Assert.Equal(StoreConnectionSelfTest.LayerOutcome.NotReached,
            authArm.Single(r => r.Layer == StoreConnectionSelfTest.SchemaLayer).Outcome);
    }
}
