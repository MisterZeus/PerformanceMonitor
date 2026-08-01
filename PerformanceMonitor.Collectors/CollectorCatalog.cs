/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// Every collector definition in the library, as the engine-neutral schema surface. This is the
/// single enumeration storage hosts build from — Darling generates its full Postgres schema by
/// walking this list. A new definition MUST be added here (the catalog test pins the count).
/// </summary>
public static class CollectorCatalog
{
    public static IReadOnlyList<ICollectorSchemaInfo> All { get; } = new ICollectorSchemaInfo[]
    {
        WaitStatsCollector.Instance,
        LatchStatsCollector.Instance,
        SpinlockStatsCollector.Instance,
        CpuSchedulerStatsCollector.Instance,
        PlanCacheStatsCollector.Instance,
        TempDbStatsCollector.Instance,
        MemoryGrantsCollector.Instance,
        CpuUtilizationCollector.Instance,
        MemoryStatsCollector.Instance,
        MemoryClerksCollector.Instance,
        MemoryPressureEventsCollector.Instance,
        FileIoStatsCollector.Instance,
        ServerPropertiesCollector.Instance,
        ServerConfigCollector.Instance,
        DatabaseConfigCollector.Instance,
        TraceFlagsCollector.Instance,
        DatabaseScopedConfigCollector.Instance,
        SessionStatsCollector.Instance,
        SessionSummaryStatsCollector.Instance,
        WaitingTasksCollector.Instance,
        ProcedureStatsCollector.Instance,
        RunningJobsCollector.Instance,
        PerfmonStatsCollector.Instance,
        DmvBlockingSnapshotCollector.Instance,
        DatabaseSizeStatsCollector.Instance,
        IndexObjectStatsCollector.Instance,
        QueryStatsCollector.Instance,
        QuerySnapshotsCollector.Instance,
        QueryStoreCollector.Instance,
        DeadlocksCollector.Instance,
        BlockedProcessReportCollector.Instance,
        LongQueryCompletionsCollector.Instance,
        SystemHealthEventsCollector.Instance,
        DefaultTraceEventsCollector.Instance,
        JobHistoryCollector.Instance,
        AgentStatusCollector.Instance,
        AgReplicaStatesCollector.Instance,
        AgDatabaseReplicaStatesCollector.Instance,
        PlanCorrectionCollector.Instance,
        PvsStatsCollector.Instance,
    };

    /// <summary>Name → definition, for the by-name target-gate lookup. Built once from <see cref="All"/>.</summary>
    private static readonly Dictionary<string, ICollectorSchemaInfo> s_byName =
        All.ToDictionary(c => c.Name, StringComparer.Ordinal);

    /// <summary>
    /// The single authoritative target-gate check both SKUs share: whether <paramref name="collectorName"/>
    /// applies to <paramref name="target"/>. Delegates to the definition's
    /// <see cref="ICollectorSchemaInfo.AppliesTo"/> override — the gate CONDITION lives there and nowhere
    /// else. Darling's collector runner calls <c>definition.AppliesTo(target)</c> directly; Lite consults
    /// this by name for its pre-dispatch SKIPPED log (a genuine skip with no collection_log row, vs. the
    /// SUCCESS/0-rows a gated collector would otherwise record). An unknown name returns <c>true</c> (not
    /// gated) so a typo surfaces as the dispatch switch's "Unknown collector" rather than a silent skip.
    /// </summary>
    public static bool AppliesTo(string collectorName, CollectorTargetInfo target) =>
        s_byName.TryGetValue(collectorName, out var definition) ? definition.AppliesTo(target) : true;

    /// <summary>
    /// True when <paramref name="collectorName"/>'s query carries the deliberate short
    /// <c>SET LOCK_TIMEOUT</c> guard, so the runners' catch sites can classify SQL error 1222 as a
    /// <c>YIELDED</c> row by name (#1805) — the flag CONDITION lives on the definition
    /// (<see cref="ICollectorSchemaInfo.YieldsOnLockTimeout"/>) and nowhere else. An unknown name
    /// returns <c>false</c> — the opposite default from <see cref="AppliesTo"/>, deliberately: a
    /// 1222 from a collector this catalog does not know is unexpected and must stay an ERROR.
    /// </summary>
    public static bool YieldsOnLockTimeout(string collectorName) =>
        s_byName.TryGetValue(collectorName, out var definition) && definition.YieldsOnLockTimeout;
}
