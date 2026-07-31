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
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Establishes the shared <c>DARLING_TEST_PG</c> store ONCE, before the first class in the
/// <c>live-postgres</c> collection runs (#1862). Wired by <see cref="LivePostgresCollection"/>.
///
/// <para><b>The bug this closes.</b> Until now "the store is established" was an EMERGENT property of test
/// order: every live class did its own <see cref="PgMigrations.MigrateAsync"/>, and a class needing
/// TimescaleDB did its own <see cref="TimescaleSupport.TryEnableAsync"/> — so whether the extension existed
/// when a given test ran depended entirely on which class xUnit happened to schedule first. Both are
/// PERSISTENT, database-level changes, so the FIRST class to make them silently established the store for
/// every class after it. <c>PayloadDimensionLiveTests.DimensionGc_DefersWhenAFactFloorIsUnmeasurable_…</c>
/// reads <c>timescaledb_information.continuous_aggregates</c> without enabling the extension itself; run
/// first against a fresh database it died 3-5ms in with <c>42P01</c>, and run after any of its three
/// siblings (which DO enable) it passed. Same test, same code, opposite outcome.</para>
///
/// <para><b>Why that shape is expensive out of proportion to the bug.</b> The failure MOVES. It lands on
/// whichever class drew the short straw this run, so it reads as "the change under test broke something it
/// never touched" and gets re-run rather than fixed — the same cost <see cref="ViewerTimeStaticsCollection"/>
/// and <see cref="LivePostgresCollectionHygieneTests"/> were written to stop. And CI is no help: it builds a
/// throwaway cluster per run, so a green <c>darling-pg</c> job means the scheduling lottery came up good, not
/// that the suite is order-independent.</para>
///
/// <para><b>Why a collection fixture rather than fixing the one test.</b> Adding the missing
/// <c>TryEnableAsync</c> call to that one method would turn this run green and leave the defect in place: the
/// next live class to read a Timescale catalog, or to assume a migrated store, re-opens it, and the next
/// person pays the same diagnosis. xUnit constructs a collection fixture and awaits its
/// <see cref="IAsyncLifetime.InitializeAsync"/> before ANY class in the collection runs, which is exactly the
/// ordering guarantee that was missing. Classes do not need to inject it — the sixty-odd existing
/// <c>[Collection("live-postgres")]</c> classes are unchanged and simply find an established store.</para>
///
/// <para><b>Migrate BEFORE enabling the extension, and that order is load-bearing.</b> It mirrors what the
/// service does on every start (<c>DarlingWorker</c>: migrate, then <c>TryEnableAsync</c>), and the V23
/// migration branches on whether the extension exists — <c>IF EXISTS (SELECT 1 FROM pg_extension WHERE
/// extname = 'timescaledb')</c>. Enabling first would make V23 convert <c>collection_log</c> to a hypertable
/// during migration on a fresh store, which is the UPGRADE path, not the fresh-store path this fixture is
/// standing in for. <c>TimescaleSupportTests</c> reads the fresh-store premise directly ("on a store whose
/// migrations ran BEFORE CREATE EXTENSION (this shared test database, and any fresh managed store) V23's
/// guard skips"), so a reordering here would quietly retire the coverage of the authoritative runtime
/// conversion path.</para>
///
/// <para><b>What it deliberately does NOT do:</b> hypertable conversion, continuous aggregates, retention
/// policies. Those are what the live classes are TESTING — several create and snapshot-restore aggregates,
/// and <c>TimescaleSupportTests</c> asserts on the un-converted starting state. Establishing them here would
/// trade an ordering bug for a fixture that silently answers the questions the tests are asking. Migrate plus
/// <c>CREATE EXTENSION</c> is the whole of what every live class may ASSUME; everything past it stays the
/// test's own business.</para>
/// </summary>
public sealed class LivePostgresStoreFixture : IAsyncLifetime
{
    /// <summary>
    /// True once the store has been migrated and the extension attempted — i.e. this fixture actually ran
    /// against a real store. False when <c>DARLING_TEST_PG</c> is unset, which is the normal ungated run.
    /// </summary>
    public bool Established { get; private set; }

    /// <summary>
    /// Whether <c>CREATE EXTENSION timescaledb</c> succeeded. False on a plain-PostgreSQL rig, which is a
    /// supported configuration — the live classes that require TimescaleDB assert on it themselves.
    /// </summary>
    public bool TimescaleAvailable { get; private set; }

    /// <summary>The store this fixture established, or null when the suite is running ungated.</summary>
    public string? ConnectionString { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        if (string.IsNullOrEmpty(connectionString))
        {
            /* Ungated run: every live test skips itself, so there is nothing to establish. Deliberately
               silent rather than throwing — the gate is the env var, and it lives on the tests. */
            return;
        }

        /* No cancellation token: a collection fixture initializes outside any test, so there is no
           TestContext.Current.CancellationToken to thread. A migration that hangs is a broken rig, and the
           runner's own timeout is the backstop. */
        var cancellationToken = CancellationToken.None;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        /* Both calls are idempotent, so this stays a no-op on a store an earlier run already established —
           and the per-class MigrateAsync calls the live classes still make stay correct and cost nothing.
           A THROW here is the right outcome, not a swallow: it fails every test in the collection with the
           real reason, which is strictly better than the moving 42P01 this replaces. */
        await PgMigrations.MigrateAsync(connection, cancellationToken);
        TimescaleAvailable = await TimescaleSupport.TryEnableAsync(connection, null, cancellationToken);

        ConnectionString = connectionString;
        Established = true;
    }

    public ValueTask DisposeAsync()
    {
        /* Nothing to tear down: the store outlives the run by design (it is the operator's database, or CI's
           throwaway cluster), and the connection above is disposed at the end of InitializeAsync. */
        return ValueTask.CompletedTask;
    }
}
