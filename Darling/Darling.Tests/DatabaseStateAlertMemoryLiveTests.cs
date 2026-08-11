/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #2166 gated-live contract for <see cref="DarlingAlertReadAdapter.ClearRecoveredDatabaseStateAlertsSql"/>,
/// the store-derived half of the database-state edge trigger.
///
/// <para>Why this has to be a LIVE test rather than a harness one: the bug it guards against was precisely
/// that the clear depended on the engine's in-memory active set, so a restart between an alert and the
/// recovery left <c>last_alerted_state</c> sticky and swallowed the next episode forever. Any test that
/// drives the engine can only prove the path a running process takes — the restart gap is invisible to it by
/// construction. This asks the store the same question the statement does, with nothing in memory at all.</para>
///
/// <para>Runs against a real Postgres gated on <c>DARLING_TEST_PG</c>, on the serialized "live-postgres"
/// collection, against a negative sentinel server_id, cleaning up in finally — the house pattern.</para>
/// </summary>
[Collection("live-postgres")]
public sealed class DatabaseStateAlertMemoryLiveTests
{
    private const int LiveServerId = -915758;
    private const string Name = "DBSTATE-MEMORY-SRV";

    [Fact]
    public async Task ClearRecovered_ForgetsOnlyDatabasesBackAtExpected_WithNothingHeldInMemory()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live database-state memory clear.");

        var ct = TestContext.Current.CancellationToken;
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);
        await DeleteLiveRowsAsync(connection, ct);

        var bodySucceeded = false;
        try
        {
            var newest = new DateTime(2026, 08, 11, 9, 0, 0, DateTimeKind.Unspecified);

            /* Recovered: alerted OFFLINE, now back ONLINE == expected. MUST be cleared — this is the
               restart-gap case, where no process ever witnessed the falling edge. */
            await StateAsync(connection, ct, "BackOnline", "ONLINE", standby: false, at: newest);
            await ExpectedAsync(connection, ct, "BackOnline", expected: "ONLINE", lastAlerted: "OFFLINE");

            /* Still deviating: alerted OFFLINE and still OFFLINE. MUST be kept, or the repetition this alert
               went quiet about starts over on the next cycle. */
            await StateAsync(connection, ct, "StillParked", "OFFLINE", standby: false, at: newest);
            await ExpectedAsync(connection, ct, "StillParked", expected: "ONLINE", lastAlerted: "OFFLINE");

            /* Deviating DIFFERENTLY: alerted OFFLINE, now SUSPECT. Not at expected, so the memory stays —
               the engine's own state comparison is what fires this one again, not a cleared memory. */
            await StateAsync(connection, ct, "TurnedSuspect", "SUSPECT", standby: false, at: newest);
            await ExpectedAsync(connection, ct, "TurnedSuspect", expected: "ONLINE", lastAlerted: "OFFLINE");

            /* Standby: expected STANDBY and currently in standby, which the effective-state CASE resolves to
               STANDBY rather than the raw state_desc. Pins that this statement reads the same effective
               state the deviation query does — comparing against state_desc would leave it uncleared. */
            await StateAsync(connection, ct, "LogShipped", "RESTORING", standby: true, at: newest);
            await ExpectedAsync(connection, ct, "LogShipped", expected: "STANDBY", lastAlerted: "RESTORING");

            /* The (ignore) sentinel: an operator silenced it, so a memory must not outlive the silence. */
            await StateAsync(connection, ct, "Silenced", "OFFLINE", standby: false, at: newest);
            await ExpectedAsync(connection, ct, "Silenced", expected: "(ignore)", lastAlerted: "OFFLINE");

            using (var clear = new NpgsqlCommand(DarlingAlertReadAdapter.ClearRecoveredDatabaseStateAlertsSql, connection))
            {
                clear.Parameters.AddWithValue(LiveServerId);
                await clear.ExecuteNonQueryAsync(ct);
            }

            Assert.Null(await MemoryAsync(connection, ct, "BackOnline"));
            Assert.Null(await MemoryAsync(connection, ct, "LogShipped"));
            Assert.Null(await MemoryAsync(connection, ct, "Silenced"));

            Assert.Equal("OFFLINE", await MemoryAsync(connection, ct, "StillParked"));
            Assert.Equal("OFFLINE", await MemoryAsync(connection, ct, "TurnedSuspect"));

            /* Idempotent: a second sweep over an already-clear store is a no-op, which matters because this
               runs on EVERY evaluation of every server. */
            using (var again = new NpgsqlCommand(DarlingAlertReadAdapter.ClearRecoveredDatabaseStateAlertsSql, connection))
            {
                again.Parameters.AddWithValue(LiveServerId);
                Assert.Equal(0, await again.ExecuteNonQueryAsync(ct));
            }

            bodySucceeded = true;
        }
        finally
        {
            await LiveStoreCleanup.RunAsync(connectionString!, bodySucceeded, async (cleanup, cleanupCt) =>
                await DeleteLiveRowsAsync(cleanup, cleanupCt));
        }
    }

    private static async Task StateAsync(
        NpgsqlConnection connection, CancellationToken ct, string database, string stateDesc, bool standby, DateTime at)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO collect.database_states (collection_id, collection_time, server_id, server_name, database_name, database_id, state_desc, is_in_standby)
VALUES (0, $1, $2, $3, $4, 5, $5, $6)", connection);
        command.Parameters.AddWithValue(at);
        command.Parameters.AddWithValue(LiveServerId);
        command.Parameters.AddWithValue(Name);
        command.Parameters.AddWithValue(database);
        command.Parameters.AddWithValue(stateDesc);
        command.Parameters.AddWithValue(standby);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExpectedAsync(
        NpgsqlConnection connection, CancellationToken ct, string database, string expected, string lastAlerted)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO config.database_state_expected (server_id, database_name, expected_state, is_user_override, updated_at, last_alerted_state, last_alerted_at)
VALUES ($1, $2, $3, false, (now() AT TIME ZONE 'UTC'), $4, (now() AT TIME ZONE 'UTC'))", connection);
        command.Parameters.AddWithValue(LiveServerId);
        command.Parameters.AddWithValue(database);
        command.Parameters.AddWithValue(expected);
        command.Parameters.AddWithValue(lastAlerted);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> MemoryAsync(NpgsqlConnection connection, CancellationToken ct, string database)
    {
        using var command = new NpgsqlCommand(
            "SELECT last_alerted_state FROM config.database_state_expected WHERE server_id = $1 AND database_name = $2",
            connection);
        command.Parameters.AddWithValue(LiveServerId);
        command.Parameters.AddWithValue(database);
        var value = await command.ExecuteScalarAsync(ct);
        return value is DBNull or null ? null : (string)value;
    }

    private static async Task DeleteLiveRowsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var id = LiveServerId.ToString(CultureInfo.InvariantCulture);
        using var cleanup = new NpgsqlCommand(
            $"DELETE FROM collect.database_states WHERE server_id = {id};" +
            $"DELETE FROM config.database_state_expected WHERE server_id = {id};", connection);
        await cleanup.ExecuteNonQueryAsync(ct);
    }
}
