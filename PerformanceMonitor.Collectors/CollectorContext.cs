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
/// Host-supplied context a collector definition runs under: the server identity and collection
/// timestamp the host stamps on every row, the delta calculator, and host-configured filters.
/// </summary>
public sealed class CollectorContext
{
    private static readonly IReadOnlySet<string> s_emptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public required int ServerId { get; init; }

    public required string ServerName { get; init; }

    /// <summary>UTC timestamp the host stamps as collection_time; definitions pass it to delta calls.</summary>
    public required DateTime CollectionTime { get; init; }

    public required ICollectorDeltaCalculator Deltas { get; init; }

    /// <summary>Target-server facts the definition may branch on (engine edition, etc.).</summary>
    public CollectorTargetInfo Target { get; init; } = new();

    /// <summary>
    /// The most recent already-collected value of the definition's <c>WatermarkColumn</c>, fetched
    /// by the host from ITS store (Lite: DuckDB; Darling: Postgres) before the query is built.
    /// Null when the definition declares no watermark or nothing was collected yet.
    /// Settable (not init-only) for one host mutation: definitions with a
    /// <c>PerDatabaseWatermarkColumn</c> get this re-fetched per database inside the Azure
    /// per-database loop, right before each per-database <c>BuildQuery</c> (see
    /// <see cref="CurrentDatabaseName"/> and <see cref="EnumerationProbeResult"/> for the other
    /// mid-cycle mutations).
    /// </summary>
    public DateTime? Watermark { get; set; }

    /// <summary>
    /// The database the host's Azure per-database loop is currently reading, set per iteration
    /// (null outside that loop — single-connection runs and non-Azure targets). The XE collectors
    /// use it as the authoritative <c>database_name</c> for rows read on this path: a
    /// database-scoped session only ever captures its own database, so the connection's database IS
    /// the ring buffer's database — while the value parsed from the event XML can be missing
    /// (older graph shapes), and a row with a null database_name could never advance its
    /// database's watermark, re-inserting every cycle until it aged out of the ring buffer.
    /// </summary>
    public string? CurrentDatabaseName { get; set; }

    /// <summary>
    /// The most recent already-collected value of the definition's <c>NumericWatermarkColumn</c>,
    /// fetched by the host from ITS store (Lite: DuckDB; Darling: Postgres) before the query is
    /// built — the numeric (bigint) twin of <see cref="Watermark"/>, for collectors that dedup on a
    /// monotonic identity/sequence column rather than a timestamp (job_history's
    /// <c>sysjobhistory.instance_id</c>, a unique monotonic IDENTITY bigint that survives server-side
    /// purges). Null when the definition declares no numeric watermark or nothing was collected yet.
    /// Purely additive: the timestamp <see cref="Watermark"/> path is unchanged and every existing
    /// collector leaves this null.
    /// </summary>
    public long? NumericWatermark { get; init; }

    /// <summary>
    /// Set by the host ONLY when the definition's <c>WatermarkColumn</c> came back null (the hot store is
    /// empty): true when a prior SUCCESS row for this collector+server exists in the host's collection_log.
    /// Lets a definition whose all-history first-run fallback would re-scan source data already aged out of
    /// the hot store (default_trace_events re-reading .trc files whose events are in Lite's parquet archive,
    /// which <c>v_default_trace_events</c> UNIONs without dedup) tell a TRUE first run (no prior success →
    /// collect all history) from a hot store merely emptied by retention/archival (prior success → a
    /// BOUNDED recent window, never all-history). False in the common case — the host computes it only when
    /// the watermark is null, and only default_trace_events and job_history consult it.
    /// </summary>
    public bool HasCollectedBefore { get; init; }

    /// <summary>Wait types excluded from collection (Lite: ignored_wait_types.json — #1240).</summary>
    public IReadOnlySet<string> IgnoredWaitTypes { get; init; } = s_emptySet;

    /// <summary>Per-server excluded database names (spliced via <see cref="DatabaseExclusionFilter"/>).</summary>
    public IReadOnlyList<string> ExcludedDatabases { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// When true, the query_stats and query_store collectors capture the execution plan text into
    /// their plan column (query_stats.query_plan_xml / query_store_stats.query_plan_text); when
    /// false they leave it NULL and the generated SQL is byte-identical to the no-plan form.
    /// Default false: Lite deliberately never captures plans (they blew out DuckDB/parquet) and
    /// never sets this flag. Darling sets it true — PostgreSQL TOAST compresses the plan text
    /// transparently. This is the shared-collector equivalent of the full Dashboard's per-collector
    /// <c>config.collection_schedule.collect_plan</c> flag (install/08_collect_query_stats.sql,
    /// install/09_collect_query_store.sql).
    /// </summary>
    public bool CapturePlanXml { get; init; }

    /// <summary>
    /// When true (the default — today's behavior), the default_trace_events collector records
    /// Object:Created/Altered/Deleted schema-change (DDL) events; when false its
    /// <c>@include_object_ddl</c> gate drops that entire slice while leaving every other curated
    /// category (file auto-grow/shrink stalls, ErrorLog, security audits, Server Memory Change)
    /// untouched. The shared-collector equivalent of the full Dashboard proc's
    /// <c>@include_object_events</c> (install/29_collect_default_trace.sql). Default true so Lite is
    /// unchanged — it never sets this flag; Darling sets it from darling.json's
    /// <c>collectSchemaChangeEvents</c> to silence the flood a create/drop-happy workload produces
    /// (e.g. HammerDB's TPC-H Query 15 creates and drops a <c>revenue</c> view thousands of times,
    /// and the collector faithfully records every Object:Created/Object:Deleted).
    /// </summary>
    public bool CollectSchemaChangeEvents { get; init; } = true;

    /// <summary>
    /// Host-configured perfmon counter override (Lite: perfmon_counters.json). Null means the
    /// definition's curated default list applies.
    /// </summary>
    public IReadOnlyList<string>? PerfmonCounterOverride { get; init; }

    /// <summary>
    /// Result of the definition's enumeration probe (see
    /// <c>ICollectorDefinition.BuildEnumerationProbe</c>), set by the host between enumeration
    /// and the per-item loop. Null when the definition declares no probe, the probe failed, or
    /// it returned SQL NULL — the definition falls back to its documented default (query_store:
    /// PRODUCTVERSION 13). One of the context members the host mutates mid-cycle (the others are
    /// <see cref="Watermark"/> and <see cref="CurrentDatabaseName"/>, set per database inside the
    /// Azure per-database loop).
    /// </summary>
    public object? EnumerationProbeResult { get; set; }

    /// <summary>
    /// Truncation signal for the per-item text-byte budget (#1556), set by a definition's read method
    /// when it stops reading a database's rows because its
    /// <see cref="ICollectorDefinition{TRow}.PerItemTextByteBudget"/> was reached, and read back by the
    /// host to surface the collection WARNING. Written per database (each definition that enforces a
    /// budget resets it at the top of its read), so the host reads it immediately after the read
    /// returns — on the enumerated path from <c>ReadItemAsync</c>, and on the Azure per-database path
    /// from <c>ReadAsync</c> (#1836). False in the common case — only budgeted collectors touch it.
    /// </summary>
    public bool PerItemTextBudgetExceeded { get; set; }

    /// <summary>
    /// Catch-up signal (#1836): set by a definition whose cutoff computation floored a stale watermark
    /// to the <see cref="WatermarkPolicy"/> horizon, so the host can log the bounded history hole the
    /// clamp deliberately creates. Only query_store clamps, and only the Azure per-database branch
    /// needs this: on the enumeration path the HOST clamps (and logs) before the definition ever sees
    /// the watermark, so the definition's own clamp is a no-op there and this stays false — the same
    /// signal cannot produce a duplicate warning. Written per database, at query build time, so the
    /// host reads it right after building that database's query.
    /// </summary>
    public bool CatchupClampApplied { get; set; }
}
