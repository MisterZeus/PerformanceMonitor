/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Linq;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the V44 <c>collector_state</c> table (#1962) — the per-server state a collector needs that is NOT
/// derivable from its rows, so it cannot be a MAX() over the target table the way the <c>event_time</c> /
/// <c>instance_id</c> watermarks are. default_trace_events stores the trace FILE it read and compares it
/// next cycle to decide whether it can read only the current rollover file (the measured 5.0x saving) or
/// must re-read the whole set because the trace rolled.
///
/// <para>The cross-store pin is the point of this file. Lite and Darling run the SAME collector definition
/// against two different stores, so the definition's state contract only holds if BOTH stores keep the same
/// key AND both runners actually load and persist it. A drift on either side would not fail a build or a
/// query — the affected SKU would simply read nothing back, bind NULL forever, and pay the fallback on
/// every cycle on every server, which looks exactly like "the fix did not help" rather than like a bug.
/// Lite's DDL and both runners' wiring live in projects this one cannot all reference, so they are pinned
/// at source, the idiom this suite already uses for cross-artifact contracts.</para>
/// </summary>
public sealed class CollectorStateContractTests
{
    private const string RepoRootNotFound = "repo root not found -- the source pin cannot run";

    [Fact]
    public void V44_CreatesCollectorState_InCollect_KeyedPerServerCollectorAndKey()
    {
        var v44 = PgMigrations.Scripts.Single(m => m.Version == 44);

        Assert.Equal("collector-state", v44.Name);

        /* Schema-qualified collect.*: service-written state the operator never mutates, so it belongs with
           analysis_state (V19) and not in the config control plane. The migrate session's search_path would
           resolve a bare name to collect anyway — qualifying makes the intent explicit, per V17/V18/V19. */
        Assert.Contains("CREATE TABLE IF NOT EXISTS collect.collector_state (", v44.Sql, StringComparison.Ordinal);

        /* The key is what scopes state per server AND per collector: servers roll their traces
           independently, so a coarser key would let one server's rollover put another on the wrong arm. */
        Assert.Contains("PRIMARY KEY (server_id, collector_name, state_key)", v44.Sql, StringComparison.Ordinal);

        Assert.Contains("server_id integer NOT NULL", v44.Sql, StringComparison.Ordinal);
        Assert.Contains("collector_name text NOT NULL", v44.Sql, StringComparison.Ordinal);
        Assert.Contains("state_key text NOT NULL", v44.Sql, StringComparison.Ordinal);
        Assert.Contains("state_value text NOT NULL", v44.Sql, StringComparison.Ordinal);
        Assert.Contains("updated_at timestamp NOT NULL", v44.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectorState_IsNotAHypertable_AndCarriesNoPassthroughView()
    {
        /* A keyed registry, not time-series growth: TimescaleDB would reject the PRIMARY KEY or force it
           onto the partition column. The hypertable set is catalog-driven and this table is not a
           collector, so exclusion is structural — this pins that it stays that way. */
        Assert.DoesNotContain("collector_state", string.Join(",", TimescaleSupport.HypertableTables), StringComparison.Ordinal);

        /* Nothing outside the collector runner reads it, so it gets no v_* passthrough. */
        Assert.DoesNotContain("v_collector_state", string.Join(",", PgSchemaGenerator.AllPassthroughViews), StringComparison.Ordinal);
    }

    [Fact]
    public void BothStoresDeclareTheSameStateContract()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null, RepoRootNotFound);

        var liteSchema = File.ReadAllText(Path.Combine(root!, "Lite", "Database", "Schema.cs"));

        Assert.Contains("CREATE TABLE IF NOT EXISTS collector_state (", liteSchema, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (server_id, collector_name, state_key)", liteSchema, StringComparison.Ordinal);

        /* Same five columns in both stores, so the shared definition's key means the same thing on either.
           Types differ by store dialect (VARCHAR/TIMESTAMP vs text/timestamp) and are pinned per store
           above; what must not drift is the column set and the key. */
        foreach (var column in new[] { "server_id", "collector_name", "state_key", "state_value", "updated_at" })
        {
            Assert.Contains(column, liteSchema, StringComparison.Ordinal);
        }

        /* Lite creates it unconditionally on every startup (GetAllTableStatements is CREATE IF NOT EXISTS),
           which is what makes an upgraded Lite store get the table with no migration; Darling needs the
           versioned migration because its store is created once. Both paths must exist or one SKU silently
           has no state. */
        Assert.Contains("yield return CreateCollectorStateTable;", liteSchema, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOnlyCollectorDeclaringStateIsDefaultTraceEvents()
    {
        /* Both hosts load and persist state ONLY for collectors that declare keys, so a second declaring
           collector is a two-host change, not a definition-local one. Pinned on the catalog surface both
           hosts iterate. */
        Assert.Equal(
            new[] { "default_trace_events" },
            CollectorCatalog.All.Where(c => c.StateKeys.Count > 0).Select(c => c.Name).ToArray());
    }

    [Fact]
    public void BothRunnersLoadAndPersistTheState()
    {
        /* This wiring is INVISIBLE to every behavioural test in this repo. Drop the save call and every
           definition test, every schema test and every round-trip test still passes — the collector keeps
           collecting, it just never records a path, so it binds NULL forever and re-reads the whole
           rollover set on every cycle on every server. That is the exact cost #1962 exists to remove, and
           it would come back silently. The runners need a live SQL Server and a live store to exercise, so
           the wiring is pinned at source in BOTH hosts, together, because a fix applied to one host and not
           the other is the drift this product keeps paying for. */
        var root = FindRepoRoot();
        Assert.True(root is not null, RepoRootNotFound);

        var hosts = new[]
        {
            Path.Combine(root!, "Lite", "Services", "RemoteCollectorService.DefinitionRunner.cs"),
            Path.Combine(root!, "Darling", "PerformanceMonitor.Darling.Service", "DarlingCollectorRunner.cs"),
        };

        foreach (var host in hosts)
        {
            var source = File.ReadAllText(host);
            var name = Path.GetFileName(host);

            /* Loaded only for the collectors that declare keys — the other 37 must not pay a query. */
            Assert.True(
                source.Contains("definition.StateKeys.Count == 0", StringComparison.Ordinal),
                $"{name} must gate the state read on the definition's declared keys");
            Assert.True(
                source.Contains("GetCollectorStateAsync(", StringComparison.Ordinal),
                $"{name} must load collector state before building the query");

            /* Handed to the definition, with the shared empty for the no-keys case. */
            Assert.True(
                source.Contains("State = collectorState ?? CollectorContext.NoState", StringComparison.Ordinal),
                $"{name} must pass the loaded state to the definition");

            /* Persisted after the cycle, from what the definition observed. */
            Assert.True(
                source.Contains("context.PendingState.Count > 0", StringComparison.Ordinal),
                $"{name} must persist only when the definition recorded something");
            Assert.True(
                source.Contains("SaveCollectorStateAsync(", StringComparison.Ordinal),
                $"{name} must persist the observed state after the cycle");
        }
    }

    /// <summary>
    /// Walks up from the test output directory to the repo root — the directory holding
    /// <c>PerformanceMonitor.sln</c>. Same walk-up idiom as <c>DocCommentHygieneTests.FindRepoRoot</c>.
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
