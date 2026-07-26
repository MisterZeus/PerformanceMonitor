/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// The default per-collector cadence and retention — the shared source both SKUs schedule by,
/// so portable Lite and the Darling service collect on identical rhythms out of the box. Lite's
/// ScheduleManager carries the same table (user-editable per install) and an identity-pin test
/// asserts the two cannot drift; Darling consumes this directly (no schedule knobs until someone
/// needs them — defaults over speculative config). FrequencyMinutes 0 = collect once on server
/// load only (config snapshots).
/// </summary>
public static class CollectorScheduleDefaults
{
    /// <summary>
    /// Per-collector cadence + retention, plus the collector's default enabled state. Nearly every
    /// collector ships ENABLED (<see cref="DefaultEnabled"/> = true); a collector that must ship OFF
    /// and be opted into (long_query_completions — a completion trace is not free on a busy server,
    /// #1496) sets it false. Both SKUs consult this: Lite's ScheduleManager seeds its per-install
    /// enabled flag from it, and Darling's <c>StoreConfigProvider.ResolveSchedule</c> falls back to it
    /// when no <c>config_collector_schedules</c> override row exists — so "reset to defaults" (which
    /// deletes override rows) returns a default-off collector to OFF, not ON.
    /// </summary>
    public sealed record Entry(int FrequencyMinutes, int RetentionDays, bool DefaultEnabled = true);

    public static IReadOnlyDictionary<string, Entry> All { get; } = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
    {
        ["wait_stats"] = new(1, 30),
        ["latch_stats"] = new(1, 30),
        ["spinlock_stats"] = new(1, 30),
        ["cpu_scheduler_stats"] = new(1, 30),
        ["plan_cache_stats"] = new(5, 30),
        ["query_stats"] = new(1, 30),
        ["procedure_stats"] = new(1, 30),
        ["query_store"] = new(5, 30),
        ["query_snapshots"] = new(1, 7),
        ["cpu_utilization"] = new(1, 30),
        ["file_io_stats"] = new(1, 30),
        ["memory_stats"] = new(1, 30),
        ["memory_clerks"] = new(5, 30),
        ["memory_pressure_events"] = new(5, 30),
        ["tempdb_stats"] = new(1, 30),
        ["perfmon_stats"] = new(1, 30),
        ["deadlocks"] = new(1, 30),
        ["server_config"] = new(0, 30),
        ["database_config"] = new(0, 30),
        ["memory_grant_stats"] = new(1, 30),
        ["waiting_tasks"] = new(1, 7),
        ["dmv_blocking_snapshot"] = new(1, 30),
        ["blocked_process_report"] = new(1, 30),
        /* #1496 long-running query completion trace: seeded DISABLED (DefaultEnabled: false). A
           completion trace (rpc_completed/sql_batch_completed) fires per statement/batch even though
           the duration predicate discards most of them, so it is opt-in per fleet; enabling it creates
           the XE session on the monitored servers and disabling it DROPS the session there. Cadence +
           retention mirror the sibling XE collectors. */
        ["long_query_completions"] = new(1, 30, DefaultEnabled: false),
        ["database_scoped_config"] = new(0, 30),
        ["trace_flags"] = new(0, 30),
        ["running_jobs"] = new(5, 7),
        ["database_size_stats"] = new(60, 90),
        ["index_object_stats"] = new(1440, 90),
        ["server_properties"] = new(0, 365),
        ["session_stats"] = new(5, 30),
        ["session_summary_stats"] = new(5, 30),
        ["system_health_events"] = new(5, 30),
        ["default_trace_events"] = new(5, 30),
        ["job_history"] = new(5, 365),
        ["agent_status"] = new(5, 7),
        /* #991 Availability Group health. Per-minute like the other health time series (wait_stats,
           cpu_utilization): send/redo queue depth and secondary lag are exactly the signals whose
           spikes are lost at a coarser grain, and both queries are a handful of rows against
           in-memory AG metadata. On an AG-less server every cycle is a zero-row read. */
        ["ag_replica_states"] = new(1, 30),
        ["ag_database_replica_states"] = new(1, 30),
    };
}
