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
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #2188: retiring the per-database <c>collector_state</c> rows query_store leaves behind for databases the
/// server no longer has. The #2164 plan-XML watermark writes one <c>planwm:</c> row per database and the
/// #2022 backfill worker writes <c>done:</c> and <c>hole:</c> rows the same way, and until this nothing
/// deleted any of them for a dropped or renamed database.
///
/// <para><b>What actually needs pinning is the input, not the delete.</b> A delete keyed on the wrong list is
/// the failure mode: query_store's own enumeration is filtered by ONLINE state, AG primary-ness, the
/// excluded-database list, a vendor-name screen, <c>HAS_DBACCESS</c>, and a per-database probe that can fail,
/// so a database absent from one cycle's items is far more often offline or unprobeable than dropped. Pruning
/// on that absence would delete LIVE watermarks on exactly the servers that have such databases, and because
/// the consequence is a silent refetch rather than an error, nothing downstream would ever report it. So the
/// live tests below seed a snapshot containing a database no enumeration would return and assert its state
/// survives — that assertion, not the delete, is what this change is.</para>
///
/// <para>Darling only, by construction: the write-back is gated on <c>CollectorContext.CapturePlanXml</c>,
/// Lite never sets it (pinned by <c>QueryStorePlanWatermarkTests.WriteBack_PlanCaptureOff_WritesNothing</c>),
/// so a Lite store has no such rows to prune. <see cref="LiteWritesNoPerDatabaseQueryStoreState"/> holds that
/// premise at source so the parity claim cannot rot silently into an un-pruned orphan class.</para>
/// </summary>
[Collection("live-postgres")]
public sealed class QueryStoreStatePruneTests
{
    /// <summary>Distinctive fake ids — a real server_id is a storage-name hash, never these.</summary>
    private const int LiveServerId = -218800;
    private const int NeighborServerId = -218801;
    private const string ServerName = "PLANWM-PRUNE-SRV";

    private static string Planwm(string database) => QueryStorePlanXmlState.WatermarkKeyPrefix + database;

    /* ---------------- the design's premise, pinned without a store ---------------- */

    [Fact]
    public void ThePruneChecksSysDatabases_NotTheCollectorsFilteredEnumeration()
    {
        /* The whole correctness of this change is which relation answers "does this database still exist".
           database_states is an unfiltered SELECT ... FROM sys.databases; query_store's enumeration is not.
           Pinned on the statement text because the difference is invisible to any test that only seeds
           databases which are both present AND collectable — which is every naive fixture. */
        Assert.Contains("FROM database_states", DarlingCollectorRunner.PruneOrphanedDatabaseStateKeysSql, StringComparison.Ordinal);

        /* The snapshot guard. MAX() over zero rows yields one row holding NULL, so without this an anti-join
           against a server that has never collected database_states matches EVERY key and deletes the lot. */
        Assert.Contains("snapshot.newest IS NOT NULL", DarlingCollectorRunner.PruneOrphanedDatabaseStateKeysSql, StringComparison.Ordinal);

        /* Newest snapshot only: an older one names databases that have since been dropped, which would make
           the prune permanently unable to retire anything. */
        Assert.Contains("MAX(collection_time)", DarlingCollectorRunner.PruneOrphanedDatabaseStateKeysSql, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPerDatabaseKeyPrefixQueryStoreOwnsIsPruned()
    {
        /* The drift guard that matters more than the prune itself: a fourth per-database key prefix added to
           either state class is a new orphan class, and nothing about adding one would fail a test. Derived
           from the state classes' own consts rather than a hand-written list, so the two cannot disagree. */
        var declared = new[] { typeof(QueryStorePlanXmlState), typeof(QueryStoreBackfillState) }
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)
                && field.Name.EndsWith("KeyPrefix", StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(declared);

        var pruned = DarlingCollectorRunner.QueryStorePerDatabaseStateKeys.Select(pair => pair.Prefix).ToArray();

        foreach (var prefix in declared)
        {
            Assert.True(
                pruned.Contains(prefix, StringComparer.Ordinal),
                $"the key prefix '{prefix}' is written per DATABASE but is not in the #2188 prune set, so its "
                + "rows orphan forever when a database is dropped");
        }

        /* Owners paired correctly: a prefix pruned under the wrong collector_name silently deletes nothing,
           which looks exactly like "there was nothing to prune". */
        Assert.Contains(
            (QueryStorePlanXmlState.StateCollectorName, QueryStorePlanXmlState.WatermarkKeyPrefix),
            DarlingCollectorRunner.QueryStorePerDatabaseStateKeys);
    }

    [Fact]
    public void TheRunnerPrunesOnTheQueryStoreCycle()
    {
        /* Wiring invisible to everything else here: delete the call and every assertion in this file still
           passes, because they drive the prune directly. The rows would simply never be pruned in production.
           Source-pinned for the same reason CollectorStateContractTests pins the state save. */
        var root = FindRepoRoot();
        Assert.True(root is not null, "repo root not found -- the source pin cannot run");

        var source = File.ReadAllText(Path.Combine(
            root!, "Darling", "PerformanceMonitor.Darling.Service", "DarlingCollectorRunner.cs"));

        Assert.Contains("await PruneOrphanedQueryStoreDatabaseStateAsync(server.ServerId, cancellationToken);",
            source, StringComparison.Ordinal);
    }

    [Fact]
    public void LiteWritesNoPerDatabaseQueryStoreState()
    {
        /* The parity premise. Lite runs the SAME query_store definition, so if it ever set CapturePlanXml it
           would start writing planwm rows into its own collector_state with no prune on that side — and
           Darling's prune reads database_states in Postgres, which does not port. This pins the premise at
           its source rather than restating it in a comment: Lite's context construction must keep leaving
           the flag at its false default. */
        var root = FindRepoRoot();
        Assert.True(root is not null, "repo root not found -- the source pin cannot run");

        var liteRunner = File.ReadAllText(Path.Combine(
            root!, "Lite", "Services", "RemoteCollectorService.DefinitionRunner.cs"));

        Assert.False(
            liteRunner.Contains("CapturePlanXml", StringComparison.Ordinal),
            "Lite's definition runner now sets CapturePlanXml, so Lite has started writing per-database "
            + "query_store state (planwm:) into its own collector_state — and #2188's prune is Darling-only, "
            + "because it anti-joins against database_states in Postgres. Port the prune to DuckDB before "
            + "shipping plan capture in Lite, or those rows orphan forever with nothing to retire them.");
    }

    /// <summary>
    /// The recreate-with-the-same-name case, which is the only shape here that could cost data rather than a
    /// refetch: a dropped and recreated database restarts Query Store's plan_id numbering at 1, so every plan
    /// in the NEW database sorts below the OLD database's watermark and has its XML suppressed.
    ///
    /// <para><b>#2183 ships no reset detection</b> — it was written, found unsound, and removed, because the
    /// tempting test ("the highest plan_id seen this pass is below the standing watermark") is TRUE in any
    /// ordinary window where nothing new compiled. What actually bounds this is
    /// <see cref="QueryStorePlanXmlState.RefreshAfter"/>: the stamp dates the last FULL fetch, so a stale
    /// watermark stops applying within a day no matter what. This test states that mechanism explicitly, so
    /// the claim is a checked fact rather than a PR-description assertion.</para>
    ///
    /// <para>The prune strictly improves on that bound without replacing it — it removes the row outright
    /// when the drop is observed between cycles — but it cannot be the guarantee, because a drop and recreate
    /// entirely within one cycle is never observed as an absence at all.</para>
    /// </summary>
    [Fact]
    public void RecreatedDatabase_IsBoundedByTheRefreshHorizon_NotByResetDetection()
    {
        var now = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
        var state = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Planwm("Recreated")] = QueryStorePlanXmlState.Format(900_000, now - QueryStorePlanXmlState.RefreshAfter),
        };

        /* At the horizon the watermark stops applying, so the recreated database's plan_ids (which start at 1
           and would all fail a > 900000 predicate) are fetched again. */
        Assert.Equal(0, QueryStorePlanXmlState.Resolve(state, "Recreated", now));

        /* And one second inside it, the stale watermark DOES still apply — which is the exposure this bounds,
           and the reason the prune is worth having even though it is not the guarantee. */
        Assert.Equal(900_000, QueryStorePlanXmlState.Resolve(state, "Recreated", now - TimeSpan.FromSeconds(1)));

        /* A pruned row is simply absent, and absent is the conservative full-fetch path — so a recreate that
           happens after an observed drop inherits nothing at all. */
        Assert.Equal(0, QueryStorePlanXmlState.Resolve(new Dictionary<string, string>(StringComparer.Ordinal), "Recreated", now));
    }

    /* ---------------- gated: the real statement against a real store ---------------- */

    /// <summary>
    /// One live pass over every case that separates a correct prune from a destructive one. It drives
    /// <see cref="DarlingCollectorRunner.PruneOrphanedQueryStoreDatabaseStateAsync"/> — the production method,
    /// on a real store — because the statement's whole risk is in how PostgreSQL evaluates the anti-join and
    /// the guard together, which no source pin can speak to.
    /// </summary>
    [Fact]
    public async Task Prune_RetiresOnlyDroppedDatabases_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live query_store state prune test.");

        var ct = TestContext.Current.CancellationToken;
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);
        await DeleteLiveRowsAsync(connection, ct);

        await using var postgres = NpgsqlDataSource.Create(connectionString!);
        var runner = new DarlingCollectorRunner(postgres, new CollectorDeltaCalculator());

        var bodySucceeded = false;
        try
        {
            var newest = new DateTime(2026, 8, 11, 9, 0, 0, DateTimeKind.Unspecified);

            /* The snapshot: what sys.databases still holds. "Parked" is the case the whole design turns on —
               a database that EXISTS but that query_store's enumeration would never return (it screens
               state_desc = ONLINE), so a prune keyed on the enumeration deletes it and a prune keyed on
               sys.databases keeps it. */
            await SnapshotAsync(connection, ct, newest, "Live", "Parked");

            /* An OLDER snapshot still naming the dropped database. If the prune read any snapshot but the
               newest, "Dropped" would look present forever and nothing would ever be retired. */
            await SnapshotAsync(connection, ct, newest.AddMinutes(-15), "Live", "Parked", "Dropped");

            await StateAsync(connection, ct, LiveServerId, QueryStorePlanXmlState.StateCollectorName, Planwm("Live"), "900000:1786449600");
            await StateAsync(connection, ct, LiveServerId, QueryStorePlanXmlState.StateCollectorName, Planwm("Parked"), "800000:1786449600");
            await StateAsync(connection, ct, LiveServerId, QueryStorePlanXmlState.StateCollectorName, Planwm("Dropped"), "700000:1786449600");

            /* The backfill worker's per-database keys, which orphan identically. */
            await StateAsync(connection, ct, LiveServerId, QueryStoreBackfillState.StateCollectorName,
                QueryStoreBackfillState.DoneKeyPrefix + "Live", "2026-08-11T09:00:00.0000000Z");
            await StateAsync(connection, ct, LiveServerId, QueryStoreBackfillState.StateCollectorName,
                QueryStoreBackfillState.DoneKeyPrefix + "Dropped", "2026-08-11T09:00:00.0000000Z");
            await StateAsync(connection, ct, LiveServerId, QueryStoreBackfillState.StateCollectorName,
                QueryStoreBackfillState.HoleKeyPrefix + "Dropped", QueryStoreBackfillState.EncodeHole(
                    new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 10, 6, 0, 0, DateTimeKind.Utc)));

            /* A key under the SAME owner that is not database-keyed. The prefix filter is what protects it;
               a prune written as "every key of this collector" would take it. */
            await StateAsync(connection, ct, LiveServerId, QueryStoreBackfillState.StateCollectorName,
                "unrelated-bookkeeping", "keep me");

            /* Another collector's state entirely, and a NEIGHBOUR SERVER whose database really was dropped
               here — server scoping is the difference between pruning one server and pruning the fleet. */
            await StateAsync(connection, ct, LiveServerId, DefaultTraceEventsCollector.Instance.Name,
                DefaultTraceEventsCollector.LastTraceFilePathStateKey, @"S:\MSSQL\Log\log_766.trc");
            await StateAsync(connection, ct, NeighborServerId, QueryStorePlanXmlState.StateCollectorName,
                Planwm("Dropped"), "600000:1786449600");

            await runner.PruneOrphanedQueryStoreDatabaseStateAsync(LiveServerId, ct);

            /* Retired: the database is gone from the newest snapshot, on every prefix it could have left. */
            Assert.Null(await ValueAsync(connection, ct, LiveServerId, QueryStorePlanXmlState.StateCollectorName, Planwm("Dropped")));
            Assert.Null(await ValueAsync(connection, ct, LiveServerId, QueryStoreBackfillState.StateCollectorName,
                QueryStoreBackfillState.DoneKeyPrefix + "Dropped"));
            Assert.Null(await ValueAsync(connection, ct, LiveServerId, QueryStoreBackfillState.StateCollectorName,
                QueryStoreBackfillState.HoleKeyPrefix + "Dropped"));

            /* Kept: still collected. */
            Assert.Equal("900000:1786449600",
                await ValueAsync(connection, ct, LiveServerId, QueryStorePlanXmlState.StateCollectorName, Planwm("Live")));
            Assert.Equal("2026-08-11T09:00:00.0000000Z",
                await ValueAsync(connection, ct, LiveServerId, QueryStoreBackfillState.StateCollectorName,
                    QueryStoreBackfillState.DoneKeyPrefix + "Live"));

            /* Kept, and this is the assertion the change exists for: present in sys.databases, absent from
               every enumeration query_store runs. Pruning it costs a full plan-XML refetch of a database that
               never went anywhere, on precisely the servers that keep databases parked. */
            Assert.Equal("800000:1786449600",
                await ValueAsync(connection, ct, LiveServerId, QueryStorePlanXmlState.StateCollectorName, Planwm("Parked")));

            /* Kept: not database-keyed, not this collector, not this server. */
            Assert.Equal("keep me",
                await ValueAsync(connection, ct, LiveServerId, QueryStoreBackfillState.StateCollectorName, "unrelated-bookkeeping"));
            Assert.Equal(@"S:\MSSQL\Log\log_766.trc",
                await ValueAsync(connection, ct, LiveServerId, DefaultTraceEventsCollector.Instance.Name,
                    DefaultTraceEventsCollector.LastTraceFilePathStateKey));
            Assert.Equal("600000:1786449600",
                await ValueAsync(connection, ct, NeighborServerId, QueryStorePlanXmlState.StateCollectorName, Planwm("Dropped")));

            /* Idempotent — it runs on every query_store cycle of every server, so a second pass over a clean
               store must touch nothing. Five survivors: planwm for Live and Parked, done for Live, the
               non-database-keyed bookkeeping row, and the other collector's key. */
            await runner.PruneOrphanedQueryStoreDatabaseStateAsync(LiveServerId, ct);
            Assert.Equal(5, await CountAsync(connection, ct, LiveServerId));

            bodySucceeded = true;
        }
        finally
        {
            await LiveStoreCleanup.RunAsync(connectionString!, bodySucceeded, async (cleanup, cleanupCt) =>
                await DeleteLiveRowsAsync(cleanup, cleanupCt));
        }
    }

    /// <summary>
    /// The guard, isolated. A server with NO database_states snapshot must lose nothing — this is the case
    /// that turns a hygiene sweep into a fleet-wide data event, and it is reached by ordinary configurations:
    /// Azure SQL DB never collects database_states at all (<c>DatabaseStateCollector.AppliesTo</c>), and any
    /// server whose database_states rows have aged out of the raw retention tier looks identical.
    /// </summary>
    [Fact]
    public async Task Prune_WithNoDatabaseSnapshot_RetiresNothing_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live prune guard test.");

        var ct = TestContext.Current.CancellationToken;
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);
        await DeleteLiveRowsAsync(connection, ct);

        await using var postgres = NpgsqlDataSource.Create(connectionString!);
        var runner = new DarlingCollectorRunner(postgres, new CollectorDeltaCalculator());

        var bodySucceeded = false;
        try
        {
            /* No snapshot for THIS server. A neighbour's snapshot exists and names none of these databases,
               so a prune that forgot to scope the snapshot read by server would wipe every row here. */
            await SnapshotAsync(connection, ct, new DateTime(2026, 8, 11, 9, 0, 0, DateTimeKind.Unspecified),
                NeighborServerId, "SomeOtherServersDatabase");

            await StateAsync(connection, ct, LiveServerId, QueryStorePlanXmlState.StateCollectorName, Planwm("Alpha"), "1:1786449600");
            await StateAsync(connection, ct, LiveServerId, QueryStorePlanXmlState.StateCollectorName, Planwm("Beta"), "2:1786449600");

            await runner.PruneOrphanedQueryStoreDatabaseStateAsync(LiveServerId, ct);

            Assert.Equal("1:1786449600",
                await ValueAsync(connection, ct, LiveServerId, QueryStorePlanXmlState.StateCollectorName, Planwm("Alpha")));
            Assert.Equal("2:1786449600",
                await ValueAsync(connection, ct, LiveServerId, QueryStorePlanXmlState.StateCollectorName, Planwm("Beta")));

            bodySucceeded = true;
        }
        finally
        {
            await LiveStoreCleanup.RunAsync(connectionString!, bodySucceeded, async (cleanup, cleanupCt) =>
                await DeleteLiveRowsAsync(cleanup, cleanupCt));
        }
    }

    /// <summary>
    /// The race the issue asks about, driven in the order that would lose data if the write-back were not an
    /// upsert: a cycle loads state, the prune deletes that key underneath it, and the cycle then persists what
    /// it observed. The row must come back.
    ///
    /// <para>The prune cannot actually target a live database — its predicate is absence from the newest
    /// sys.databases snapshot — so this drives the adversarial case DELIBERATELY, by pruning while the name is
    /// missing from the snapshot. That is what makes the consequence a measured fact instead of an argument:
    /// even a prune that fires on a database it should not have costs one refetch and never a lost row.</para>
    /// </summary>
    [Fact]
    public async Task Prune_RacingAnInFlightCycle_CannotLoseTheWatermark_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live prune race test.");

        var ct = TestContext.Current.CancellationToken;
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);
        await DeleteLiveRowsAsync(connection, ct);

        await using var postgres = NpgsqlDataSource.Create(connectionString!);
        var runner = new DarlingCollectorRunner(postgres, new CollectorDeltaCalculator());

        var bodySucceeded = false;
        try
        {
            /* A snapshot that does NOT name Racer — the adversarial setup. */
            await SnapshotAsync(connection, ct, new DateTime(2026, 8, 11, 9, 0, 0, DateTimeKind.Unspecified), "Live");
            await StateAsync(connection, ct, LiveServerId, QueryStorePlanXmlState.StateCollectorName, Planwm("Racer"), "900000:1786449600");

            /* Cycle start: the collection pass reads its state. */
            var loaded = await runner.GetCollectorStateAsync(LiveServerId, QueryStorePlanXmlState.StateCollectorName, ct);
            Assert.Equal("900000:1786449600", Assert.Contains(Planwm("Racer"), loaded));

            /* Mid-flight: the prune fires and takes the row this cycle is still working from. */
            await runner.PruneOrphanedQueryStoreDatabaseStateAsync(LiveServerId, ct);
            Assert.Null(await ValueAsync(connection, ct, LiveServerId, QueryStorePlanXmlState.StateCollectorName, Planwm("Racer")));

            /* Cycle end: the write-back is an INSERT ... ON CONFLICT, so it restores rather than failing on a
               row that is no longer there. The database keeps collecting; the delete cost nothing. */
            await runner.SaveCollectorStateAsync(
                LiveServerId, QueryStorePlanXmlState.StateCollectorName,
                new Dictionary<string, string>(StringComparer.Ordinal) { [Planwm("Racer")] = "950000:1786449600" },
                ct);

            Assert.Equal("950000:1786449600",
                await ValueAsync(connection, ct, LiveServerId, QueryStorePlanXmlState.StateCollectorName, Planwm("Racer")));

            bodySucceeded = true;
        }
        finally
        {
            await LiveStoreCleanup.RunAsync(connectionString!, bodySucceeded, async (cleanup, cleanupCt) =>
                await DeleteLiveRowsAsync(cleanup, cleanupCt));
        }
    }

    /* ---------------- helpers ---------------- */

    private static Task SnapshotAsync(
        NpgsqlConnection connection, CancellationToken ct, DateTime at, params string[] databases)
        => SnapshotAsync(connection, ct, at, LiveServerId, databases);

    private static async Task SnapshotAsync(
        NpgsqlConnection connection, CancellationToken ct, DateTime at, int serverId, params string[] databases)
    {
        foreach (var database in databases)
        {
            /* state_desc is deliberately never read by the prune — existence is the only question — so these
               rows carry ONLINE uniformly except where a case needs otherwise. */
            using var command = new NpgsqlCommand(@"
INSERT INTO collect.database_states (collection_id, collection_time, server_id, server_name, database_name, database_id, state_desc, is_in_standby)
VALUES (0, $1, $2, $3, $4, 5, $5, false)", connection);
            command.Parameters.AddWithValue(at);
            command.Parameters.AddWithValue(serverId);
            command.Parameters.AddWithValue(ServerName);
            command.Parameters.AddWithValue(database);
            command.Parameters.AddWithValue(string.Equals(database, "Parked", StringComparison.Ordinal) ? "OFFLINE" : "ONLINE");
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task StateAsync(
        NpgsqlConnection connection, CancellationToken ct, int serverId, string owner, string key, string value)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO collect.collector_state (server_id, collector_name, state_key, state_value, updated_at)
VALUES ($1, $2, $3, $4, (now() AT TIME ZONE 'UTC'))", connection);
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(owner);
        command.Parameters.AddWithValue(key);
        command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> ValueAsync(
        NpgsqlConnection connection, CancellationToken ct, int serverId, string owner, string key)
    {
        using var command = new NpgsqlCommand(
            "SELECT state_value FROM collect.collector_state WHERE server_id = $1 AND collector_name = $2 AND state_key = $3",
            connection);
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(owner);
        command.Parameters.AddWithValue(key);
        var value = await command.ExecuteScalarAsync(ct);
        return value is DBNull or null ? null : (string)value;
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, CancellationToken ct, int serverId)
    {
        using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM collect.collector_state WHERE server_id = $1", connection);
        command.Parameters.AddWithValue(serverId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task DeleteLiveRowsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var live = LiveServerId.ToString(CultureInfo.InvariantCulture);
        var neighbor = NeighborServerId.ToString(CultureInfo.InvariantCulture);
        using var cleanup = new NpgsqlCommand(
            $"DELETE FROM collect.collector_state WHERE server_id IN ({live}, {neighbor});" +
            $"DELETE FROM collect.database_states WHERE server_id IN ({live}, {neighbor});", connection);
        await cleanup.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Walks up from the test output directory to the repo root — the same walk-up idiom
    /// <c>CollectorStateContractTests</c> uses.
    /// </summary>
    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PerformanceMonitor.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
