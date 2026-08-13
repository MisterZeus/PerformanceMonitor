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
/// <para><b>Why this gate and not the other two.</b> Its observable is a PRESENCE — a row in
/// <c>analysis_state</c> carrying the engine tombstone — so it can be asserted directly. The reconcile and
/// snapshot_now gates observe an ABSENCE (no connection attempted), which needs a counting seam that does
/// not exist yet; #2230 says as much, and this is the tractable half.</para>
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
    /// A server_id far outside the fleet's range, derived the same way the worker derives it, so this test
    /// cannot collide with a real server's analysis_state row.
    /// </summary>
    private const string StorageName = "pg-engine-gate-behavior-test-2230";

    [Fact]
    public async Task AnalyzeNow_AgainstAPostgresTarget_WritesTheEngineTombstone_AndDoesNotRunThePass()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the analyze_now engine-gate test.");

        await using var postgres = NpgsqlDataSource.Create(connectionString!);
        var serverId = ServerIdHelper.GetDeterministicHashCode(StorageName);

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

        await CleanupAsync(postgres, serverId);
        try
        {
            var outcome = await InvokeAnalyzeNowAsync(worker, servers, serverId);

            /* 1. The gate returned the success shape, not a failure and not the analysis result. */
            Assert.True(GetOutcomeSuccess(outcome));
            Assert.Equal("analysis not applicable", GetOutcomeStatus(outcome));

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
            Assert.DoesNotContain("still collecting\"", message, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(postgres, serverId);
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
        var serverId = ServerIdHelper.GetDeterministicHashCode(StorageName + "-sqlserver");

        var worker = (DarlingWorker)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(DarlingWorker));
        SetField(worker, "_serversLock", new object());
        SetField(worker, "_logger", NullLogger<DarlingWorker>.Instance);
        SetField(worker, "_postgres", postgres);

        var server = SqlServerLoopState(serverId);
        var servers = NewLoopStateList(server);

        await CleanupAsync(postgres, serverId);
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
        }
        finally
        {
            await CleanupAsync(postgres, serverId);
        }
    }

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
        new MonitoredServer { Name = StorageName, Host = "pg-gate-test.invalid", Engine = "postgres" },
        new ServerRuntime
        {
            Config = new MonitoredServer { Name = StorageName, Host = "pg-gate-test.invalid" },
            ConnectionString = "Host=pg-gate-test.invalid;Database=postgres;Username=monitor",
            Target = new CollectorTargetInfo { Engine = CollectorTargetEngine.PostgreSql },
            StorageName = StorageName,
            ServerId = serverId,
        });

    private static object SqlServerLoopState(int serverId) => NewLoopState(
        new MonitoredServer { Name = StorageName + "-sqlserver", Host = "sql-gate-test.invalid" },
        new ServerRuntime
        {
            Config = new MonitoredServer { Name = StorageName + "-sqlserver", Host = "sql-gate-test.invalid" },
            ConnectionString = "Server=sql-gate-test.invalid;Integrated Security=true",
            Target = new CollectorTargetInfo { Engine = CollectorTargetEngine.SqlServer },
            StorageName = StorageName + "-sqlserver",
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
    /// Deletes only this test's own synthetic server_id. Runs on CancellationToken.None deliberately, so a
    /// cancelled run still leaves the shared live store clean — the LiveStoreCleanup convention.
    /// </summary>
    private static async Task CleanupAsync(NpgsqlDataSource postgres, int serverId)
    {
        await using var command = postgres.CreateCommand("DELETE FROM analysis_state WHERE server_id = $1");
        command.Parameters.AddWithValue(serverId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
