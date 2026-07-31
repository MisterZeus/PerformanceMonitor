/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The live half of the #1888 worker-sizing guard: the cluster the gated-live suite is CONNECTED TO must
/// actually serve enough worker slots to launch TimescaleDB's background policy jobs.
///
/// <para><b>Why the workflow parse is not enough.</b> <see cref="CiClusterWorkerSizingTests"/> proves the two
/// numbers are written into <c>build.yml</c> and <c>nightly.yml</c>. It cannot prove they took EFFECT, and a
/// conf line that never took effect is indistinguishable from one that did by inspection — appended after the
/// server started, appended to the wrong data directory, overridden by a later assignment, or (the one that
/// actually bites) silently clamped by the postmaster at boot. <c>max_worker_processes</c> is restart-only, so
/// getting the ORDER wrong in a workflow step is a live possibility every time someone edits it, and the
/// result is a green suite that quietly went back to testing an 8-slot cluster. This asks the running server.</para>
///
/// <para><b>Also: this is what makes the workflow change self-verifying on its own pull request.</b> A
/// <c>pull_request</c> event runs the workflow file from the PR's branch, so the PR that raises the setting is
/// the run that must prove it — and it proves it here, in an assertion that fails the job, rather than in a log
/// line nobody reads.</para>
///
/// <para><b>Gated on DARLING_TEST_PGRUNTIME as well as DARLING_TEST_PG</b>, which is the pair CI's
/// <c>darling-pg</c> job and the nightly both set, and the same discriminator
/// <see cref="DarlingPgRuntimeVersionPinTests"/> uses. It is not incidental: the product writes worker sizing
/// in MANAGED mode only (<c>BuildConfAppend</c>'s remarks — a bring-your-own store keeps its operator's server
/// defaults), so demanding this of any store someone happens to point <c>DARLING_TEST_PG</c> at would be
/// asserting a guarantee the product does not make. The runtime variable is what says "this rig was stood up
/// from our bundled runtime and should be shaped like a managed store".</para>
///
/// <para>Asserted as a FLOOR, not equality: over-provisioning costs a few MB of idle worker slots and races
/// nothing, while under-provisioning is the defect. The exact-equality pin belongs on the workflow file, where
/// <see cref="CiClusterWorkerSizingTests"/> keeps it.</para>
/// </summary>
[Collection("live-postgres")]
public sealed class CiClusterWorkerSizingLiveTests
{
    [Fact]
    public async Task LiveCluster_ServesEnoughWorkerSlotsForTheBackgroundJobs_Gated()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to verify the live cluster's worker sizing.");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DARLING_TEST_PGRUNTIME")),
            "Set DARLING_TEST_PGRUNTIME as well — this asserts the MANAGED-mode worker sizing, which the "
            + "product writes only for stores it stands up itself, so it applies to a rig built from the "
            + "bundled runtime (as CI's darling-pg job and the nightly both are) and not to an arbitrary "
            + "bring-your-own store.");

        var ct = TestContext.Current.CancellationToken;

        var expectedBackgroundWorkers = TimescaleSupport.HypertableCount + 2;
        var expectedWorkerProcesses = 3 + expectedBackgroundWorkers + 8;

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var workerProcesses = await ReadIntSettingAsync(connection, "max_worker_processes", ct);
        var backgroundWorkers = await ReadIntSettingAsync(connection, "timescaledb.max_background_workers", ct);

        Assert.True(workerProcesses >= expectedWorkerProcesses,
            $"The live store serves max_worker_processes = {workerProcesses}, below the "
            + $"{expectedWorkerProcesses} the product would provision for {TimescaleSupport.HypertableCount} "
            + "hypertables (3 + (HypertableCount + 2) + 8, DarlingManagedPostgres.BuildWorkerSizingConfAppend). "
            + "At PostgreSQL's default of 8 the postmaster cannot launch the per-hypertable compression, "
            + "retention and continuous-aggregate policy jobs at all, so the suite tests a configuration no "
            + "customer runs and scheduler-racing tests pass or fail by luck of slot availability (#1888). Add "
            + $"'max_worker_processes = {expectedWorkerProcesses}' to this cluster's postgresql.conf and RESTART "
            + "it — the setting is restart-only, so a reload leaves the old value serving.");

        Assert.True(backgroundWorkers >= expectedBackgroundWorkers,
            $"The live store serves timescaledb.max_background_workers = {backgroundWorkers}, below the "
            + $"{expectedBackgroundWorkers} the product would provision (HypertableCount + 2). One background "
            + "worker per per-hypertable policy that can run concurrently, plus the scheduler and slack (#1888).");
    }

    /// <summary>
    /// Reads the setting the SERVER is running with. <c>current_setting</c>, not a scan of the conf file:
    /// the whole point is to catch a value that was written but never took effect.
    /// </summary>
    private static async Task<int> ReadIntSettingAsync(
        NpgsqlConnection connection,
        string settingName,
        System.Threading.CancellationToken cancellationToken)
    {
        /* The setting name cannot be a parameter in SHOW, and current_setting() takes it as a value — which is
           also why this is safe: the two names are compile-time constants at the only call sites. */
        await using var command = new NpgsqlCommand("SELECT current_setting(@setting)", connection);
        command.Parameters.AddWithValue("setting", settingName);

        var raw = (string?)await command.ExecuteScalarAsync(cancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(raw), $"current_setting('{settingName}') returned nothing.");

        return int.Parse(raw!, CultureInfo.InvariantCulture);
    }
}
