/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Common;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// BEHAVIOURAL coverage for the PostgreSQL engine gate on the <c>analyze_now</c> operator door (#2230).
///
/// <para><b>What was missing.</b> The three gates added by #2213's round-3 fix were covered by
/// source-scanning pins (<c>TheScheduledAnalysisPassIsGatedByEngine</c> greps the call site) and by a live
/// rig run, but nothing drove a PostgreSQL runtime through a gate and asserted the short-circuit. A source
/// scan cannot tell a gate that returns early from one that falls through and happens to write the same
/// text.</para>
///
/// <para><b>Two of the three doors.</b> The analyze_now gate's observable is a PRESENCE — a row in
/// <c>analysis_state</c> carrying the engine tombstone — so it is asserted directly. The reconcile gate
/// looked like it needed a counting seam to observe an absence, and that was wrong: the belt gate inside
/// <see cref="DarlingXeSessions.ReconcileLongQueryCompletionsAsync"/> is public and its own precondition,
/// so an ungated call THROWS and a gated one returns — a difference an assertion can see with no seam, no
/// live store, and no network. snapshot_now remains uncovered; see #2230.</para>
///
/// <para><b>The regression it guards is specific and was real.</b> Clicking "Generate now" against a
/// PostgreSQL target used to run the full SQL-Server-shaped pass, find nothing, and persist the GENERIC
/// <c>insufficient_data</c> message — OVERWRITING the honest engine tombstone the scheduled arm had already
/// written. The Recommendations tab regressed from "does not apply, use the PG reads" back to "still
/// collecting" the moment an operator pressed the button. So the assertion that matters is not just
/// "insufficient_data is true", it is that the MESSAGE is the engine one.</para>
///
/// <para>Live-store gated on <c>DARLING_TEST_PG</c>, which CI's "Darling PostgreSQL tests" job sets — the
/// gate's whole effect is a write through <c>_postgres</c>, so there is nothing to observe without one.</para>
/// </summary>
[Collection("live-postgres")]
public sealed class PostgresEngineGateBehaviorTests
{
    /// <summary>
    /// The HOST is what identity derives from, which is the trap this test tripped over first:
    /// <c>MonitoredServer.StorageName</c> is <c>BuildStorageName(Host, Database, ReadOnlyIntent)</c> — NOT
    /// <c>Name</c> — and <c>RunAnalyzeNowAsync</c> finds a server by hashing that. A server_id hashed from
    /// anything else simply is not found, and the gate then returns "server not monitored" rather than the
    /// arm under test. Unique hosts so neither case can collide with a real server's analysis_state row.
    /// </summary>
    private const string PgHost = "pg-engine-gate-behavior-2230.invalid";

    private const string SqlHost = "sql-engine-gate-behavior-2230.invalid";

    /// <summary>Derived through the SAME helper the worker uses, so the test cannot drift from the lookup.</summary>
    private static int ServerIdFor(string host) =>
        ServerIdHelper.GetDeterministicHashCode(ServerIdHelper.BuildStorageName(host, null, false));

    [Fact]
    public async Task AnalyzeNow_AgainstAPostgresTarget_WritesTheEngineTombstone_AndDoesNotRunThePass()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the analyze_now engine-gate test.");

        await using var postgres = NpgsqlDataSource.Create(connectionString!);
        var serverId = ServerIdFor(PgHost);

        /* Fabricated worker, the CollectorMemoryKnobTests.SweepGate idiom: the real ctor wants a host's worth
           of dependencies, and the gate under test reads exactly three fields. Reflection because pinning the
           BEHAVIOUR beats widening the surface just to observe it. */
        var worker = (DarlingWorker)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(DarlingWorker));
        SetField(worker, "_serversLock", new object());
        SetField(worker, "_logger", NullLogger<DarlingWorker>.Instance);
        SetField(worker, "_postgres", postgres);

        var server = PostgresLoopState(serverId);
        var servers = NewLoopStateList(server);

        var bodySucceeded = false;
        try
        {
            var outcome = await InvokeAnalyzeNowAsync(worker, servers, serverId);

            /* 1. The gate returned the success shape, not a failure and not the analysis result. */
            /* Assert the STATUS first: if the lookup missed, the status is "server not monitored" and says
               so, where a bare Assert.True on Success only reports Expected/Actual booleans. */
            Assert.Equal("analysis not applicable", GetOutcomeStatus(outcome));
            Assert.True(GetOutcomeSuccess(outcome));

            /* 2. The once-latch is set, so the scheduled tick will not re-write what this just wrote —
                  the two arms share the tombstone rather than racing to overwrite it. */
            Assert.True(AnalysisStateWritten(server));

            /* 3. THE REGRESSION GUARD: the persisted message is the ENGINE tombstone, not the generic
                  insufficient-data text the SQL-Server-shaped pass would have left. */
            var state = await ReadAnalysisStateAsync(postgres, serverId);
            var (found, insufficient, message) = (state.Found, state.Insufficient, state.Message);
            Assert.True(found, "the gate must PERSIST a row, or the Recommendations tab has nothing to show");
            Assert.True(insufficient);
            Assert.Equal(DarlingWorker.PostgresAnalysisNotApplicable, message);

            /* And the specific words that make it honest rather than merely non-empty. */
            Assert.Contains("does not apply to a PostgreSQL target", message, StringComparison.Ordinal);
            Assert.Contains("get_pg_blocking", message, StringComparison.Ordinal);
            /* And it DISCLAIMS the still-collecting reading rather than avoiding the words: the message
               quotes the phrase in order to contrast with it ("This is not \"still collecting\""), so a
               DoesNotContain on those words can never pass and asserting it was my error, not the
               product's. The property worth pinning is that the disclaimer is present. */
            Assert.Contains("This is not \"still collecting\"", message, StringComparison.Ordinal);

            bodySucceeded = true;
        }
        finally
        {
            await LiveStoreCleanup.RunAsync(connectionString!, bodySucceeded, (cleanup, cleanupCt) =>
                DeleteAnalysisStateAsync(cleanup, cleanupCt, serverId));
        }
    }

    /// <summary>
    /// The same door against a SQL Server target must NOT take the gate — otherwise the test above would
    /// pass on a gate that fires unconditionally, which is the failure mode a presence-assertion is blind to.
    /// <para>Asserted by the outcome status alone: a SQL Server target falls through to the real pass, which
    /// on a store with no data for this server_id reports insufficient data. Either way it is NOT
    /// "analysis not applicable", and that is the discriminator.</para>
    /// </summary>
    [Fact]
    public async Task AnalyzeNow_AgainstASqlServerTarget_DoesNotTakeTheEngineGate()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the analyze_now engine-gate test.");

        await using var postgres = NpgsqlDataSource.Create(connectionString!);
        var serverId = ServerIdFor(SqlHost);

        var worker = (DarlingWorker)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(DarlingWorker));
        SetField(worker, "_serversLock", new object());
        SetField(worker, "_logger", NullLogger<DarlingWorker>.Instance);
        SetField(worker, "_postgres", postgres);

        var server = SqlServerLoopState(serverId);
        var servers = NewLoopStateList(server);

        var bodySucceeded = false;
        try
        {
            /* The SQL Server path runs the real analysis pass, which needs collaborators the fabricated
               worker does not have — so the assertion is that it did NOT short-circuit as the PG arm, which
               is observable either as a different status or as a throw from the pass itself. Both prove the
               gate is engine-conditional; only "analysis not applicable" would disprove it. */
            string? status = null;
            try
            {
                status = GetOutcomeStatus(await InvokeAnalyzeNowAsync(worker, servers, serverId));
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                /* Fell through into the pass and hit a missing collaborator — which is itself the proof. */
                Assert.NotNull(ex);
            }

            Assert.NotEqual("analysis not applicable", status);
            Assert.False(AnalysisStateWritten(server),
                "the PostgreSQL once-latch must not be set for a SQL Server target");

            bodySucceeded = true;
        }
        finally
        {
            await LiveStoreCleanup.RunAsync(connectionString!, bodySucceeded, (cleanup, cleanupCt) =>
                DeleteAnalysisStateAsync(cleanup, cleanupCt, serverId));
        }
    }

    /// <summary>
    /// The reconcile door, gated (#2230). <see cref="DarlingXeSessions.ReconcileLongQueryCompletionsAsync"/>
    /// carries its own engine precondition — "belt to the worker's braces" — and it is the belt that
    /// actually stopped the field failure, so it is the one worth pinning.
    ///
    /// <para>The regression was measured, not theoretical: ungated, this method built a
    /// <c>SqlConnection</c> from a PostgreSQL connection string, the ctor threw
    /// <c>Keyword not supported: 'host'</c>, the caller's catch skipped the latch assignment, and because
    /// <c>LongQueryTraceApplied</c> resets to null on every connect it retried EVERY sweep forever —
    /// ~1,440 warnings/day/server (#2213 round 2).</para>
    ///
    /// <para>Both <c>enabled</c> values, because the gate sits ahead of that branch: ungated, the false arm
    /// would try to DROP a session over the same impossible connection.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconcileLongQueryCompletions_AgainstAPostgresTarget_ReturnsBeforeBuildingASqlConnection(bool enabled)
    {
        /* runner is null on purpose: reaching it would mean the gate did not fire. */
        await DarlingXeSessions.ReconcileLongQueryCompletionsAsync(
            PostgresRuntime(), runner: null!, enabled, NullLogger<DarlingWorker>.Instance, CancellationToken.None);
    }

    /// <summary>
    /// The proof the test above is not vacuous. Same connection string, ENGINE flipped to SQL Server: the
    /// gate no longer applies, the ctor rejects the string, and the exact field exception surfaces. Without
    /// this arm, the pin above would pass just as happily against a method that had stopped connecting for
    /// some unrelated reason.
    /// </summary>
    [Fact]
    public async Task ReconcileLongQueryCompletions_SameStringButSqlServerEngine_ThrowsTheFieldFailure()
    {
        /* The engine is the ONLY difference from the gated case — same host, same connection string. */
        var ungated = PostgresRuntime(CollectorTargetEngine.SqlServer);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            DarlingXeSessions.ReconcileLongQueryCompletionsAsync(
                ungated, runner: null!, enabled: true, NullLogger<DarlingWorker>.Instance, CancellationToken.None));

        /* The words from the sweep log, so a future reader can match this pin to that incident. */
        Assert.Contains("Keyword not supported", ex.Message, StringComparison.Ordinal);
        Assert.Contains("host", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A PostgreSQL runtime whose connection string is the PostgreSQL shape that <c>SqlConnection</c>
    /// cannot parse — the combination that produced the field failure.</summary>
    private static ServerRuntime PostgresRuntime(
        CollectorTargetEngine engine = CollectorTargetEngine.PostgreSql) => new()
    {
        Config = new MonitoredServer { Name = "pg-reconcile-gate", Host = PgHost, Engine = "postgres" },
        ConnectionString = $"Host={PgHost};Database=postgres;Username=monitor",
        Target = new CollectorTargetInfo { Engine = engine },
        StorageName = PgHost,
        ServerId = ServerIdFor(PgHost),
    };
    private static void SetField(DarlingWorker worker, string name, object value) =>
        typeof(DarlingWorker)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(worker, value);

    private static async Task<object> InvokeAnalyzeNowAsync(
        DarlingWorker worker, object servers, int serverId)
    {
        var method = typeof(DarlingWorker).GetMethod(
            "RunAnalyzeNowAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        /* planFetcher / notificationService / config are only touched on the SQL Server path, so the gate
           can be driven with nulls — which is itself part of what "short-circuits" means here. */
        var task = (Task)method.Invoke(worker, new object?[]
        {
            servers, null, null, null, serverId, CancellationToken.None,
        })!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    /* CommandOutcome is public (DarlingCommandExecutor), so no reflection is needed for the result —
       only for ServerLoopState, which is a private nested type. */
    private static bool GetOutcomeSuccess(object outcome) => ((CommandOutcome)outcome).Success;

    private static string? GetOutcomeStatus(object? outcome) => (outcome as CommandOutcome)?.ResultStatus;

    /// <summary>
    /// <c>DarlingWorker.ServerLoopState</c> is a PRIVATE nested class, so the test cannot name the type and
    /// builds it reflectively — the same trade <c>CollectorMemoryKnobTests</c> makes for private gate state.
    /// Widening it to <c>internal</c> purely for a test would be a production change to observe behaviour
    /// that reflection can already reach.
    /// </summary>
    private static readonly Type LoopStateType = typeof(DarlingWorker)
        .GetNestedType("ServerLoopState", BindingFlags.NonPublic)!;

    private static object NewLoopState(MonitoredServer config, ServerRuntime runtime)
    {
        var state = Activator.CreateInstance(LoopStateType)!;
        LoopStateType.GetProperty("Config")!.SetValue(state, config);
        LoopStateType.GetProperty("Runtime")!.SetValue(state, runtime);
        return state;
    }

    private static bool AnalysisStateWritten(object loopState) =>
        (bool)LoopStateType.GetProperty("PostgresAnalysisStateWritten")!.GetValue(loopState)!;

    /// <summary>The parameter is <c>List&lt;ServerLoopState&gt;</c>, so the list is reflective too.</summary>
    private static object NewLoopStateList(object single)
    {
        var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(LoopStateType))!;
        list.GetType().GetMethod("Add")!.Invoke(list, new[] { single });
        return list;
    }

    private static object PostgresLoopState(int serverId) => NewLoopState(
        new MonitoredServer { Name = "pg-gate", Host = PgHost, Engine = "postgres" },
        new ServerRuntime
        {
            Config = new MonitoredServer { Name = "pg-gate", Host = PgHost, Engine = "postgres" },
            ConnectionString = $"Host={PgHost};Database=postgres;Username=monitor",
            Target = new CollectorTargetInfo { Engine = CollectorTargetEngine.PostgreSql },
            StorageName = PgHost,
            ServerId = serverId,
        });

    private static object SqlServerLoopState(int serverId) => NewLoopState(
        new MonitoredServer { Name = "sql-gate", Host = SqlHost },
        new ServerRuntime
        {
            Config = new MonitoredServer { Name = "sql-gate", Host = SqlHost },
            ConnectionString = $"Server={SqlHost};Integrated Security=true",
            Target = new CollectorTargetInfo { Engine = CollectorTargetEngine.SqlServer },
            StorageName = SqlHost,
            ServerId = serverId,
        });

    private static async Task<(bool Found, bool Insufficient, string Message)> ReadAnalysisStateAsync(
        NpgsqlDataSource postgres, int serverId)
    {
        await using var command = postgres.CreateCommand(
            "SELECT insufficient_data, message FROM analysis_state WHERE server_id = $1 " +
            "ORDER BY analysis_time DESC LIMIT 1");
        command.Parameters.AddWithValue(serverId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return (false, false, string.Empty);
        }

        return (true,
            !reader.IsDBNull(0) && reader.GetBoolean(0),
            reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
    }

    /// <summary>
    /// Deletes only this test's own synthetic server_id, through <c>LiveStoreCleanup</c> so the teardown runs
    /// on its OWN connection rather than the body's (#1902). A finally that tears down on the body's
    /// connection throws out of the finally and REPLACES the body's exception with the teardown's — and it is
    /// the body's failure that closed the connection in the first place, so the teardown fails because of the
    /// thing it then hides. Opening a fresh connection by hand is explicitly not accepted either: it is half
    /// the fix and still throws from the finally.
    /// </summary>
    private static async Task DeleteAnalysisStateAsync(
        NpgsqlConnection cleanup, CancellationToken cleanupCt, int serverId)
    {
        await using var command = new NpgsqlCommand(
            "DELETE FROM analysis_state WHERE server_id = $1", cleanup);
        command.Parameters.AddWithValue(serverId);
        await command.ExecuteNonQueryAsync(cleanupCt);
    }
}
