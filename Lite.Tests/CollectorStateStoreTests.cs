/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lite.Tests.Helpers;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Services;
using PerformanceMonitorLite.Tests;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Real-DuckDB pins for the per-server collector state store (#1962) — the seam between the runner and
/// Lite's store, which neither the collector-definition tests (they stop at the generated SQL and the
/// context) nor the schema tests (they stop at the DDL) reach.
///
/// <para>What makes this worth a real database: the store is what turns default_trace_events' rollover
/// comparison from a guess into a fact ACROSS RESTARTS, and the failure modes all live in SQL, not in
/// C# — an upsert that throws on the second cycle instead of replacing (the path changes on every
/// rollover, so the very first rollover would start failing), state that leaks between servers (one
/// server's rollover would silence another's), and a read that cannot see what the write stored. Every
/// one of those is invisible to a mocked store, and each would degrade silently: the collector keeps
/// working, it just quietly re-reads the whole rollover set forever, or quietly skips a file's events.</para>
/// </summary>
public sealed class CollectorStateStoreTests : IClassFixture<SharedDuckDbFixture>
{
    private const string Collector = "default_trace_events";
    private const string Key = DefaultTraceEventsCollector.LastTraceFilePathStateKey;

    private readonly StateStore _store;

    public CollectorStateStoreTests(SharedDuckDbFixture fixture)
    {
        fixture.ResetData();
        _store = new StateStore(fixture.DuckDb);
    }

    /// <summary>Exposes the runner's protected state accessors; only <c>_duckDb</c> is exercised.</summary>
    private sealed class StateStore(DuckDbInitializer duckDb)
        : RemoteCollectorService(duckDb, serverManager: null!, scheduleManager: null!)
    {
        public Task<Dictionary<string, string>> LoadAsync(int serverId, string collector) =>
            GetCollectorStateAsync(serverId, collector, CancellationToken.None);

        public Task SaveAsync(int serverId, string collector, IReadOnlyDictionary<string, string> state) =>
            SaveCollectorStateAsync(serverId, collector, state, CancellationToken.None);
    }

    private static Dictionary<string, string> Path(string value) =>
        new() { [Key] = value };

    [Fact]
    public async Task NoStoredState_ReadsEmpty_WhichIsTheFallbackTrigger()
    {
        /* A first run, a host restarted before it could store a path, a store upgraded onto this build.
           The definition binds NULL for @last_trace_path from this, and the query reads the whole
           rollover set — a collector that cannot know what it missed must not assume it missed nothing. */
        Assert.Empty(await _store.LoadAsync(serverId: 1, Collector));
    }

    [Fact]
    public async Task SavedPath_ReadsBackVerbatim()
    {
        await _store.SaveAsync(1, Collector, Path(@"C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\Log\log_766.trc"));

        var state = await _store.LoadAsync(1, Collector);

        Assert.Equal(
            @"C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\Log\log_766.trc",
            state[Key]);
    }

    [Fact]
    public async Task SavingAgain_ReplacesTheValue_BecauseEveryRolloverRewritesIt()
    {
        /* The path changes on EVERY rollover, so an insert that collided instead of replacing would start
           failing at the first rollover — the exact moment the state has to be right. */
        await _store.SaveAsync(1, Collector, Path(@"S:\Log\log_766.trc"));
        await _store.SaveAsync(1, Collector, Path(@"S:\Log\log_767.trc"));

        var state = await _store.LoadAsync(1, Collector);

        Assert.Equal(@"S:\Log\log_767.trc", Assert.Single(state).Value);
    }

    [Fact]
    public async Task StateIsScopedPerServerAndPerCollector()
    {
        /* Servers roll their traces independently; a shared row would let one server's rollover put
           another server onto the wrong arm. */
        await _store.SaveAsync(1, Collector, Path(@"S:\Log\log_766.trc"));
        await _store.SaveAsync(2, Collector, Path(@"T:\Log\log_12.trc"));
        await _store.SaveAsync(1, "some_other_collector", Path(@"S:\Log\other.trc"));

        Assert.Equal(@"S:\Log\log_766.trc", (await _store.LoadAsync(1, Collector))[Key]);
        Assert.Equal(@"T:\Log\log_12.trc", (await _store.LoadAsync(2, Collector))[Key]);
        Assert.Equal(@"S:\Log\other.trc", (await _store.LoadAsync(1, "some_other_collector"))[Key]);
        Assert.Empty(await _store.LoadAsync(3, Collector));
    }

    [Fact]
    public async Task EmptyPendingState_WritesNothing()
    {
        /* Every collector but default_trace_events reaches the save with nothing pending. */
        await _store.SaveAsync(1, "wait_stats", new Dictionary<string, string>());

        Assert.Empty(await _store.LoadAsync(1, "wait_stats"));
    }

    [Fact]
    public async Task RoundTripsTheFullCycleContract_ObservedPathBecomesNextCyclesComparison()
    {
        /* The whole loop, end to end: what ReadAsync records into PendingState is what BuildQuery binds
           as @last_trace_path on the next cycle. */
        var readContext = new CollectorContext
        {
            ServerId = 1,
            ServerName = "test-server",
            CollectionTime = new System.DateTime(2026, 7, 31, 12, 0, 0, System.DateTimeKind.Utc),
            Deltas = new RecordingCollectorDeltaCalculator(),
        };
        readContext.PendingState[Key] = @"S:\Log\log_766.trc";

        await _store.SaveAsync(1, Collector, readContext.PendingState);

        var nextCycle = new CollectorContext
        {
            ServerId = 1,
            ServerName = "test-server",
            CollectionTime = new System.DateTime(2026, 7, 31, 12, 6, 0, System.DateTimeKind.Utc),
            Deltas = new RecordingCollectorDeltaCalculator(),
            State = await _store.LoadAsync(1, Collector),
        };

        var bound = DefaultTraceEventsCollector.Instance.BuildQuery(nextCycle)
            .Parameters
            .Single(p => p.Name == "@last_trace_path");

        Assert.Equal(@"S:\Log\log_766.trc", bound.Value);
    }
}
