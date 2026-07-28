/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The live-store half of the runtime version pin (#1787, completing #1706/#1713's guard).
/// <see cref="DarlingPgRuntimeVersionPinTests"/> proves the ASSEMBLED bundle matches
/// <c>fetch-pg-runtime.ps1</c>'s pins — <c>pg_ctl.exe</c> answers for the binaries, the extension files
/// answer for the TimescaleDB payload. What it cannot prove is that the cluster the live suite actually
/// CONNECTED to is serving that runtime: <c>DARLING_TEST_PG</c> is just a connection string, and nothing
/// ties it to the extracted bundle. #1787's motivating incident was exactly that ambiguity — a fixture
/// DDL failure was attributed to an older CI extension version, and disproving the claim took a night of
/// derivation (it was a <c>search_path</c> effect) when one catalog row would have settled it instantly.
///
/// <para>Gated on BOTH variables: <c>DARLING_TEST_PG</c> says where the suite connects,
/// <c>DARLING_TEST_PGRUNTIME</c> says the cluster under test is supposed to BE the bundled runtime (CI's
/// darling-pg job and the nightly set both, pointed at the same throwaway cluster). A developer pointing
/// <c>DARLING_TEST_PG</c> at their own Postgres without the bundle variable makes no parity claim, and
/// this test stays out of the way rather than failing their legitimately different cluster.</para>
/// </summary>
[Collection("live-postgres")]
public sealed class DarlingLiveStoreExtensionParityTests
{
    [Fact]
    public async Task LiveStore_ServesThePinnedPostgresAndTimescaleVersions_Gated()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live extension-parity check.");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DARLING_TEST_PGRUNTIME")),
            "Set DARLING_TEST_PGRUNTIME too: the parity claim only binds when the live store is the bundled runtime.");

        var script = DarlingPgRuntimeVersionPinTests.FetchScriptText;
        var pinnedPgVersion = DarlingPgRuntimeVersionPinTests.PinnedValue(script, "pgVersion");
        var pinnedTsVersion = DarlingPgRuntimeVersionPinTests.PinnedValue(script, "tsVersion");

        var ct = TestContext.Current.CancellationToken;

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        /* SHOW server_version reports e.g. "18.4"; some builds append platform detail after the number,
           so this is a prefix match rather than equality. */
        using (var versionCmd = new NpgsqlCommand("SHOW server_version;", connection))
        {
            var serverVersion = (string?)await versionCmd.ExecuteScalarAsync(ct);
            Assert.NotNull(serverVersion);
            Assert.StartsWith(pinnedPgVersion, serverVersion, StringComparison.Ordinal);
        }

        /* default_version is the control file on the RUNNING cluster's disk — what CREATE EXTENSION
           would install — so it answers for the cluster even before any migration has run.
           installed_version is what THIS database actually loaded; it is null until the first migration
           creates the extension, so it is asserted only when present rather than ordering this test
           after a migration. */
        using var extensionCmd = new NpgsqlCommand(
            "SELECT default_version, installed_version FROM pg_available_extensions WHERE name = 'timescaledb';",
            connection);
        using var reader = await extensionCmd.ExecuteReaderAsync(ct);

        Assert.True(await reader.ReadAsync(ct),
            "The live store has no timescaledb extension available at all — this cluster cannot be the bundled runtime.");

        var defaultVersion = reader.GetString(0);
        Assert.Equal(pinnedTsVersion, defaultVersion);

        if (!await reader.IsDBNullAsync(1, ct))
        {
            Assert.Equal(pinnedTsVersion, reader.GetString(1));
        }
    }
}
