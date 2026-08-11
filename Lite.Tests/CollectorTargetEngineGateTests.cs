/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the target-engine half of the dispatch gate: a definition written in one engine's dialect
/// must never be dispatched at the other engine, and adding that dimension must not have moved a
/// single existing dispatch decision.
/// </summary>
public class CollectorTargetEngineGateTests
{
    /// <summary>
    /// Every shipped definition is T-SQL today. This is the drift guard: if a Postgres definition is
    /// added without deriving from <see cref="PostgresCollectorDefinitionBase{TRow}"/>, it would
    /// silently advertise itself as SQL Server and be dispatched at SQL Server targets, where every
    /// cycle would fail. The failure message names the offender.
    /// </summary>
    [Fact]
    public void EveryCatalogDefinitionDeclaresAnEngine()
    {
        var wrong = CollectorCatalog.All
            .Where(d => d.TargetEngine != CollectorTargetEngine.SqlServer)
            .Select(d => $"{d.Name} => {d.TargetEngine}")
            .ToList();

        Assert.True(
            wrong.Count == 0,
            "These catalog definitions are not SQL Server. If that is intentional, update this test "
            + "and confirm they derive from PostgresCollectorDefinitionBase: " + string.Join(", ", wrong));
    }

    /// <summary>A target with no engine specified is SQL Server, so nothing existing changes.</summary>
    [Fact]
    public void TargetDefaultsToSqlServer()
    {
        Assert.Equal(CollectorTargetEngine.SqlServer, new CollectorTargetInfo().Engine);
    }

    /// <summary>
    /// The whole point: a T-SQL definition is gated off a PostgreSQL target even though its own
    /// <c>AppliesTo</c> would say yes.
    /// </summary>
    [Fact]
    public void SqlServerDefinitionDoesNotApplyToPostgresTarget()
    {
        var pgTarget = new CollectorTargetInfo { Engine = CollectorTargetEngine.PostgreSql };

        Assert.True(WaitStatsCollector.Instance.AppliesTo(pgTarget));
        Assert.False(CollectorCatalog.AppliesTo(WaitStatsCollector.Instance, pgTarget));
        Assert.False(CollectorCatalog.AppliesTo(WaitStatsCollector.Instance.Name, pgTarget));
        Assert.False(CollectorCatalog.EngineMatches(WaitStatsCollector.Instance.Name, pgTarget));
    }

    /// <summary>And the composed gate is a no-op for a SQL Server target — the pre-existing behaviour.</summary>
    [Fact]
    public void SqlServerDefinitionStillAppliesToSqlServerTarget()
    {
        var sqlTarget = new CollectorTargetInfo();

        Assert.True(CollectorCatalog.AppliesTo(WaitStatsCollector.Instance, sqlTarget));
        Assert.True(CollectorCatalog.AppliesTo(WaitStatsCollector.Instance.Name, sqlTarget));
        Assert.True(CollectorCatalog.EngineMatches(WaitStatsCollector.Instance.Name, sqlTarget));
    }

    /// <summary>
    /// The composed gate must still honour a definition's own within-engine gate, not just the
    /// engine: agent_status is off on RDS regardless of dialect.
    /// </summary>
    [Fact]
    public void ComposedGateStillHonoursWithinEngineGating()
    {
        var rds = new CollectorTargetInfo { IsAwsRds = true };

        Assert.False(CollectorCatalog.AppliesTo(AgentStatusCollector.Instance, rds));
        Assert.True(CollectorCatalog.EngineMatches(AgentStatusCollector.Instance.Name, rds));
    }

    /// <summary>A Postgres definition is dispatched at Postgres targets and nowhere else.</summary>
    [Fact]
    public void PostgresDefinitionAppliesOnlyToPostgresTarget()
    {
        var definition = new FakePostgresCollector();

        Assert.Equal(CollectorTargetEngine.PostgreSql, definition.TargetEngine);
        Assert.True(CollectorCatalog.AppliesTo(definition, new CollectorTargetInfo { Engine = CollectorTargetEngine.PostgreSql }));
        Assert.False(CollectorCatalog.AppliesTo(definition, new CollectorTargetInfo()));
    }

    /// <summary>
    /// An unknown name is not filtered, so a typo surfaces as the dispatch switch's unknown-collector
    /// path rather than a collector that silently never runs.
    /// </summary>
    [Fact]
    public void UnknownCollectorNameIsNotEngineFiltered()
    {
        Assert.True(CollectorCatalog.EngineMatches("no_such_collector", new CollectorTargetInfo { Engine = CollectorTargetEngine.PostgreSql }));
        Assert.True(CollectorCatalog.AppliesTo("no_such_collector", new CollectorTargetInfo()));
    }

    private sealed class FakePostgresCollector : PostgresCollectorDefinitionBase<object>
    {
        public override string Name => "pg_fake";

        public override string TargetTable => "pg_fake";

        public override IReadOnlyList<CollectorColumn> PayloadColumns => [];

        public override CollectorQuery BuildQuery(CollectorContext context) => new("SELECT 1");

        public override ValueTask<List<object>> ReadAsync(DbDataReader reader, CollectorContext context, CancellationToken cancellationToken)
            => new(new List<object>());

        public override void WritePayload(object row, ICollectorRowWriter writer, CollectorContext context)
        {
        }
    }
}
