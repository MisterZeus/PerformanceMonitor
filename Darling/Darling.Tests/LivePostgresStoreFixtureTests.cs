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
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The seam test for <see cref="LivePostgresStoreFixture"/> (#1862): a member of the live collection that
/// asserts the store is already established WITHOUT establishing anything itself.
///
/// <para><b>Why this is not redundant with the sixty classes it sits beside.</b> Those classes establish what
/// they need on the way past — a <c>MigrateAsync</c> here, a <c>TryEnableAsync</c> there — so every one of
/// them would stay green if the fixture silently stopped running, and the ordering flake would come straight
/// back wearing someone else's name. This class establishes nothing, so it can only pass on a store the
/// fixture set up.</para>
///
/// <para><b>The constructor parameter is half the test.</b> Taking the fixture is what pins the WIRING: if
/// <see cref="LivePostgresCollection"/> is deleted, renamed, or loses its <c>ICollectionFixture&lt;&gt;</c>
/// declaration, xUnit cannot satisfy this constructor and the class fails to construct with "the following
/// constructor parameters did not have matching fixture data" — loudly, on every run including an ungated one
/// where the body below would have skipped. A behavioural check alone could not catch that, because with the
/// fixture gone the assertions would still pass whenever this class happened to run after a class that
/// migrates and enables, which is most of the time. That is the exact failure mode under repair, so the guard
/// against it has to be the structural half rather than the observable one.</para>
/// </summary>
[Collection("live-postgres")]
public sealed class LivePostgresStoreFixtureTests
{
    private readonly LivePostgresStoreFixture _fixture;

    public LivePostgresStoreFixtureTests(LivePostgresStoreFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Fixture_LeavesTheStoreMigratedAndTimescaleEnabled_BeforeAnyLiveClassRuns()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live-store fixture contract test.");

        var ct = TestContext.Current.CancellationToken;

        Assert.True(_fixture.Established,
            "The live-postgres collection fixture did not establish the store. DARLING_TEST_PG is set, so "
            + "LivePostgresStoreFixture.InitializeAsync should have migrated it and enabled TimescaleDB before "
            + "any class in the collection ran.");

        /* A FRESH connection that migrates nothing and enables nothing — the whole point. Whatever it finds is
           what an arbitrary live class finds when the runner schedules it first. */
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        using (var version = new NpgsqlCommand(
            "SELECT COALESCE(MAX(version), 0) FROM collect.darling_schema_version", connection))
        {
            Assert.Equal(StorageVersion.SchemaVersion, Convert.ToInt32(
                await version.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture));
        }

        /* The half that actually broke: timescaledb_information.* is a catalog the extension BRINGS, so a
           store that is migrated but extension-less answers 42P01 to any Timescale catalog read — which is
           how PayloadDimensionLiveTests died 3-5ms into a fresh-database run. Asserted through pg_extension
           rather than by reading a timescaledb_information view, so the failure names the missing extension
           instead of re-raising the same cryptic relation error the fix exists to eliminate. */
        using (var extension = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb')", connection))
        {
            var present = (bool)(await extension.ExecuteScalarAsync(ct))!;

            Assert.True(present == _fixture.TimescaleAvailable,
                $"The fixture reported TimescaleAvailable={_fixture.TimescaleAvailable} but pg_extension says "
                + $"present={present}. Those cannot disagree: one of them is lying about the store every live "
                + "class is about to run against.");

            Assert.True(present,
                "TimescaleDB is not enabled on DARLING_TEST_PG. The live rig is expected to have it (the "
                + "bundled pg-runtime and CI's throwaway cluster both preload it), and the live classes that "
                + "read timescaledb_information assume the fixture created it.");
        }
    }
}
