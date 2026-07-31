/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The seam tests for <see cref="LiveCleanupBatch"/> (#1873) — the helper the live suite's teardowns now go
/// through instead of a swallow.
///
/// <para><b>Why these run against a real store.</b> The behaviour under test is entirely about what the
/// PostgreSQL catalog says after a statement ran, and every one of the bugs in the shape it replaces was
/// invisible to anything that did not ask a real database: a probe that never matches always answers "gone",
/// and a swallow always answers "fine". A fake connection would reproduce both faults perfectly and pass.</para>
///
/// <para>The negative cases construct the batch with <c>publishResidue: false</c>. They fail a removal on
/// purpose, and a deliberate failure filed to the run-wide ledger would fail every run at collection
/// teardown — the coverage for the alarm would trip the alarm.</para>
/// </summary>
[Collection("live-postgres")]
public sealed class LiveCleanupBatchTests
{
    private const string SkipReason =
        "Set DARLING_TEST_PG to a Postgres connection string to run the live cleanup-batch seam tests.";

    private const string Probe = "cleanup_batch_1873_probe";

    /// <summary>
    /// The ordinary path: the object is there, the statement removes it, the probe agrees, nothing is
    /// recorded. Establishes that a clean removal does NOT manufacture residue — without this, a helper that
    /// reported residue unconditionally would pass every negative test below.
    /// </summary>
    [Fact]
    public async Task RemovingSomethingThatGoes_LeavesNoResidue_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), SkipReason);

        var ct = TestContext.Current.CancellationToken;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        using (var create = new NpgsqlCommand(
            $"CREATE TABLE IF NOT EXISTS collect.{Probe} (id integer)", connection))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        var batch = new LiveCleanupBatch(connection);
        await batch.DropTableAsync(Probe, ct);

        Assert.Empty(batch.Residue);
        Assert.False(await ExistsAsync(connection, Probe, ct),
            $"collect.{Probe} should have been dropped by the batch.");
    }

    /// <summary>
    /// The whole point: a removal that does not remove is REPORTED, rather than reported as success.
    ///
    /// <para>The statement here succeeds and changes nothing, which is the precise shape of the bug — a
    /// <c>DROP</c> that lost its race raises an error, but a helper that only watched for errors would also
    /// have to be right about which errors count. This one watches the object.</para>
    /// </summary>
    [Fact]
    public async Task AStatementThatDoesNotRemoveTheObject_IsRecordedAsResidue_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), SkipReason);

        var ct = TestContext.Current.CancellationToken;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        using (var create = new NpgsqlCommand(
            $"CREATE TABLE IF NOT EXISTS collect.{Probe} (id integer)", connection))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var batch = new LiveCleanupBatch(connection, publishResidue: false, maxAttempts: 2);

            await batch.RemoveAsync(
                $"table collect.{Probe}",
                "SELECT 1" /* succeeds, removes nothing */,
                "SELECT EXISTS (SELECT 1 FROM pg_class AS c JOIN pg_namespace AS n ON n.oid = c.relnamespace "
                + $"WHERE n.nspname = 'collect' AND c.relname = '{Probe}')",
                ct);

            var entry = Assert.Single(batch.Residue);
            Assert.Contains($"table collect.{Probe}", entry, StringComparison.Ordinal);
            Assert.Contains("the removal statement reported success but the object is still there", entry,
                StringComparison.Ordinal);

            /* The attribution half — the catalog knows WHAT survived, and only this knows WHOSE cleanup
               could not remove it. */
            Assert.Contains(nameof(AStatementThatDoesNotRemoveTheObject_IsRecordedAsResidue_AgainstDevPostgres),
                entry, StringComparison.Ordinal);
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connectionString);
            await cleanup.OpenAsync(CancellationToken.None);
            await new LiveCleanupBatch(cleanup).DropTableAsync(Probe, CancellationToken.None);
        }
    }

    /// <summary>
    /// A removal that THROWS but leaves the object gone is a success, not a fault.
    ///
    /// <para>This is not a corner case invented for the test — it is the pre-test call in
    /// <c>DarlingSecuritySplitLiveTests</c>, where <c>DROP OWNED BY</c> (which has no <c>IF EXISTS</c> form)
    /// raises <c>42704</c> on every run because the roles have not been created yet. Judging the postcondition
    /// rather than the exception is what lets that stay quiet while a role that genuinely survives does not.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AThrowingRemovalWhoseObjectIsAlreadyGone_IsNotResidue_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), SkipReason);

        var ct = TestContext.Current.CancellationToken;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var batch = new LiveCleanupBatch(connection, publishResidue: false, maxAttempts: 2);

        /* 42P01 every time: the relation does not exist, which is exactly why nothing needs removing. */
        await batch.RemoveAsync(
            $"table collect.{Probe}",
            $"DROP TABLE collect.{Probe}_never_created",
            "SELECT EXISTS (SELECT 1 FROM pg_class AS c JOIN pg_namespace AS n ON n.oid = c.relnamespace "
            + $"WHERE n.nspname = 'collect' AND c.relname = '{Probe}_never_created')",
            ct);

        Assert.Empty(batch.Residue);
    }

    /// <summary>
    /// A failing statement does not poison the statements after it.
    ///
    /// <para>The swallow this replaces existed partly for this: one broken statement must not cascade into
    /// every cleanup statement behind it, leaving renames and snapshots stranded (#1794's shape). The batch
    /// reopens the pooled session, so a session-breaking failure costs one object's removal, not the rest.</para>
    /// </summary>
    [Fact]
    public async Task AFailedRemoval_DoesNotStrandTheRemovalsAfterIt_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), SkipReason);

        var ct = TestContext.Current.CancellationToken;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        using (var create = new NpgsqlCommand(
            $"CREATE TABLE IF NOT EXISTS collect.{Probe} (id integer)", connection))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        var batch = new LiveCleanupBatch(connection, publishResidue: false, maxAttempts: 2);

        await batch.RemoveAsync(
            "a deliberately unremovable thing",
            "SELECT 1",
            "SELECT true",
            ct);

        /* The real removal, queued behind the failure. */
        await batch.DropTableAsync(Probe, ct);

        Assert.Single(batch.Residue);
        Assert.False(await ExistsAsync(connection, Probe, ct),
            $"collect.{Probe} should still have been dropped after an earlier removal failed.");
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, string table, CancellationToken ct)
    {
        using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_class AS c JOIN pg_namespace AS n ON n.oid = c.relnamespace "
            + $"WHERE n.nspname = 'collect' AND c.relname = '{table}')", connection);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }
}
