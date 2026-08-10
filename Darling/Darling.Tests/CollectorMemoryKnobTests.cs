/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the V59 collector memory knobs (#2164 query_store text budget + #2170 fleet sweep width): the
/// migration's identity and behavior-preserving defaults, the read clamps, the probe/gate rung, the
/// context override the shared read loop honors, and the invariant that binds the two — peak transient
/// memory is their product, which is why they ship on one rung.
/// </summary>
public sealed class CollectorMemoryKnobTests
{
    [Fact]
    public void V59_MigrationIdentity_AndDefaultsReproduceTheOldConstants()
    {
        var v59 = PgMigrations.Scripts.Single(m => m.Version == 59);
        Assert.Equal("collector-memory-knobs", v59.Name);

        var sql = v59.Sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        /* Idempotent adds on config_service, and the defaults are the constants they replace — an upgraded
           store must behave identically until an operator turns a dial. */
        Assert.Contains("ADD COLUMN IF NOT EXISTS query_store_text_budget_mb integer NOT NULL DEFAULT 64", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN IF NOT EXISTS max_concurrent_sweeps integer NOT NULL DEFAULT 4", sql, StringComparison.Ordinal);

        /* The defaults must equal what the code did before the knobs existed, or an upgrade silently
           re-tunes every existing deployment. */
        Assert.Equal(64 * 1024 * 1024, QueryStoreCollector.MaxTextBytesPerDatabase);
        Assert.Equal(4, DarlingWorker.MaxConcurrentServerSweeps);
    }

    [Theory]
    [InlineData(0, 4)]          // corrupt/unset floors to the minimum, never to "ship nothing"
    [InlineData(-16, 4)]
    [InlineData(4, 4)]          // inclusive bounds
    [InlineData(8, 8)]
    [InlineData(64, 64)]
    [InlineData(256, 256)]
    [InlineData(4096, 256)]     // an over-generous value caps instead of reintroducing the #1556 balloon
    public void TextBudgetClamp_KeepsTheKnobInsideTheMemoryBound(int stored, int expected) =>
        Assert.Equal(expected, StoreConfigProvider.ClampTextBudgetMb(stored));

    [Theory]
    [InlineData(0, 1)]          // never zero — that would stop collection entirely
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(16, 16)]
    [InlineData(64, 16)]        // capped at the gate ceiling the semaphore is built with
    public void SweepClamp_StaysWithinTheGateCeiling(int stored, int expected)
    {
        Assert.Equal(expected, StoreConfigProvider.ClampConcurrentSweeps(stored));
        /* The clamp ceiling and the semaphore's construction ceiling are the SAME number by contract:
           the gate cannot grow past the permits it was built with, so a clamp above the ceiling would
           silently cap and the knob would lie about its effective value. */
        Assert.Equal(DarlingWorker.SweepGateCeiling, StoreConfigProvider.MaxConcurrentSweepsLimit);
    }

    [Fact]
    public void ApplyToConfig_CarriesBothKnobs_AndDefaultsMatchTheConstants()
    {
        var config = new DarlingConfig();
        Assert.Equal(64, config.QueryStoreTextBudgetMb);
        Assert.Equal(4, config.MaxConcurrentSweeps);

        StoreConfigProvider.ApplyToConfig(config, new StoreConfigView { QueryStoreTextBudgetMb = 8, MaxConcurrentSweeps = 12 });
        Assert.Equal(8, config.QueryStoreTextBudgetMb);
        Assert.Equal(12, config.MaxConcurrentSweeps);
    }

    [Fact]
    public void ContextOverride_WinsOverTheCollectorConstant_AndZeroMeansNoOverride()
    {
        /* The override is what the shared read loop consults (QueryStoreCollector's budget line), so this
           pins the precedence Lite depends on: Lite passes no override and must keep the constant. */
        var noOverride = new CollectorContext
        {
            ServerId = 1, ServerName = "s", CollectionTime = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc),
            Deltas = new CollectorDeltaCalculator(),
        };
        Assert.Null(noOverride.TextByteBudgetOverride);

        var overridden = new CollectorContext
        {
            ServerId = 1, ServerName = "s", CollectionTime = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc),
            Deltas = new CollectorDeltaCalculator(),
            TextByteBudgetOverride = 8 * 1024 * 1024,
        };
        Assert.Equal(8 * 1024 * 1024, overridden.TextByteBudgetOverride);
        Assert.True(overridden.TextByteBudgetOverride < QueryStoreCollector.MaxTextBytesPerDatabase,
            "the override must be able to LOWER the budget — that is the entire point of the knob");
    }

    [Fact]
    public void ProbeAndGate_KnowTheV59Rung()
    {
        Assert.Contains("column_name = 'query_store_text_budget_mb'", ViewerDataService.StoreSchemaProbeSql, StringComparison.Ordinal);
        Assert.Contains("query_store_text_budget_mb", ViewerDataService.ServiceConfigSelectSql, StringComparison.Ordinal);
        Assert.Contains("max_concurrent_sweeps", ViewerDataService.ServiceConfigSelectSql, StringComparison.Ordinal);
        Assert.Contains("query_store_text_budget_mb = $7", ViewerDataService.ServiceConfigUpdateFlagsSql, StringComparison.Ordinal);
        Assert.Contains("max_concurrent_sweeps = $8", ViewerDataService.ServiceConfigUpdateFlagsSql, StringComparison.Ordinal);

        Assert.Equal(59, ViewerDataService.MapProbedSchemaVersion(
            true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true,
            true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true,
            true, true, true, true, true, true, hasJobMetricsColumns: true, hasJobCadenceKnob: true,
            hasBackfillSwitch: true, hasCollectorMemoryKnobs: true));
        Assert.Equal(59, ViewerDataService.RequiredStoreSchemaVersion);
    }
}
