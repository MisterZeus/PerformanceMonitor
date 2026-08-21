/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

/*
 * The server page's TAB REGISTRY — the web port of the desktop viewer's per-server TabControl
 * (Darling/PerformanceMonitor.Darling.Viewer/ViewerServerTab.xaml, ~65 TabItems across 38 partials).
 *
 * Every entry is `{ id, label, note?, build(server, ctx) }` and `build` returns an array of nodes, almost all of
 * them PANEL DESCRIPTORS run through the unmodified renderPanel (the #1563 seam): a `read` naming an MCP tool
 * served at GET /api/read/{read}, `params`, and a `viz` from the four-kind registry in panels.js. No fifth viz
 * kind was added — see the PR for why the property-grid shape that tempted one is served honestly by `stat`
 * (the reads that return a flat object) and `table` (the reads that already return rows).
 *
 * `ctx.hours` is the page's time range, the web twin of ViewerServerTab.TimeRange.cs's preset picker. Panels
 * whose read takes no time window ignore it and say so in their subtitle, because a panel labelled "last 6
 * hours" that is really a latest-snapshot read is a lie the reader cannot see.
 *
 * TWO HONESTY RULES run through this file:
 *   1. Every panel carries an `emptyText` saying WHY it could be empty in the reader's own terms. A read that
 *      returns the {status,message} envelope supplies its own (better) sentence and renderPanel shows that
 *      instead; emptyText only covers the data-arrived-but-zero-rows case, which would otherwise read as a
 *      blank rectangle.
 *   2. A tab whose desktop twin does something the browser genuinely cannot do carries a `note` naming it and
 *      pointing at the desktop viewer. A reader told "open the desktop viewer for plan analysis" is better
 *      served than one given a web page that looks like plan analysis and is not.
 *
 * R4 (XSS): every value reaches the DOM through renderPanel/util.el's text path. The two custom cell renderers
 * here (query text, XML) build a <pre> through el() and never touch innerHTML.
 */

import { el, readTool, mount, loadingStrip, errorStrip, emptyStrip, disclosure, noticeStrip } from "../util.js";
import { renderPanel, VIZ } from "../panels.js";
import { renderLineChart, SERIES_COLORS } from "../charts.js";

/* ─────────────────────────── shared cell renderers ─────────────────────────── */

/** Query-text cell: a truncated one-liner that expands to the full statement (mono) — the B2 disclosure. */
function codeDisclosure(text) {
  if (text == null || text === "") return document.createTextNode("—");
  return disclosure(text, el("pre", { class: "code" }, [text]), { max: 100 });
}

/** XML cell: the same disclosure over a captured payload (blocked-process report, deadlock graph). The desktop
 *  viewer renders these as a graph; the browser has no graph viewer, so it hands over the payload verbatim
 *  rather than pretending. The tab's note says where the graph lives. */
function xmlDisclosure(text) {
  if (text == null || text === "") return document.createTextNode("—");
  return disclosure("XML capture (" + String(text).length.toLocaleString() + " chars)", el("pre", { class: "code" }, [text]), {
    max: 60,
  });
}

/* ─────────────────────────── composites ─────────────────────────── */

/* Two panels chain reads or reshape rows, so they are built by hand rather than declared. Both were already on
   the page before the tabs existed; they keep their behaviour and move into the tab that owns them. */

function panelShell(title, subtitle) {
  const body = el("div", { class: "panel-body" }, [loadingStrip()]);
  const panel = el("div", { class: "panel card span-2" }, [
    el("h3", {}, [title, subtitle ? el("span", { class: "panel-sub", text: " " + subtitle }) : null]),
    body,
  ]);
  return { panel, body };
}

/**
 * Wait Stats table + a trend for ONE wait type, chosen from a picker seeded with the heaviest.
 *
 * The desktop viewer's Wait Stats tab is a checkbox list of wait types over a multi-series chart. This is the
 * single-select version of the same idea: the picker's options are the rows of the table directly above it,
 * heaviest first, so the reader is choosing from what they can already see rather than from a second list that
 * may disagree with it. That is also why get_wait_types is NOT read here — it returns the full distinct set,
 * which would offer wait types absent from the table and make the two disagree.
 */
export function waitsPanel(server, ctx) {
  const { panel, body } = panelShell("Wait Stats", ctx.label + ", with a trend for the wait you pick");
  (async () => {
    const res = await readTool("get_wait_stats", { server, hours: ctx.hours, limit: 20 });
    if (res.kind === "error") return mount(body, errorStrip(res.message));
    if (res.kind === "empty") return mount(body, emptyStrip(res.message));

    const waits = res.data.waits || [];
    const parts = [VIZ.table(res.data, { rowsKey: "waits", columns: WAIT_COLUMNS })];

    if (waits.length) {
      const chartSlot = el("div", {}, [loadingStrip()]);
      const picker = pickerControl(
        "Trend",
        waits.map((w) => w.wait_type),
        (waitType) => drawWaitTrend(chartSlot, server, ctx, waitType)
      );
      parts.push(el("div", { class: "picker-row" }, [picker]), chartSlot);
      drawWaitTrend(chartSlot, server, ctx, waits[0].wait_type);
    }
    mount(body, parts);
  })();
  return panel;
}

async function drawWaitTrend(slot, server, ctx, waitType) {
  mount(slot, loadingStrip());
  const trend = await readTool("get_wait_trend", { server, wait_type: waitType, hours: ctx.hours });
  if (trend.kind !== "data") {
    mount(slot, trend.kind === "empty" ? emptyStrip(trend.message) : errorStrip(trend.message));
    return;
  }
  mount(
    slot,
    renderLineChart({
      points: trend.data.trend || [],
      xKey: "time",
      series: [
        { key: "wait_time_ms_per_second", label: "Wait ms/s", color: SERIES_COLORS[0] },
        { key: "signal_wait_time_ms_per_second", label: "Signal ms/s", color: SERIES_COLORS[1] },
      ],
      formatValue: (v) => Math.round(v).toLocaleString(),
      unit: "ms/s",
    })
  );
}

/**
 * Perfmon counters: a picker over the counters this server actually collects, charting the chosen one.
 *
 * The desktop viewer's Perfmon tab is a searchable counter list over a multi-series chart. get_perfmon_trend
 * REQUIRES a counter_name, so without a picker the read is unreachable from the browser — which is why the
 * options come from get_perfmon_stats (the latest snapshot's counter list) rather than being hardcoded: a
 * hardcoded name is exactly how you end up charting a counter this server does not collect.
 *
 * When the trend read still comes back empty it can carry hints.collected_counters. Its message tells the
 * reader to "see hints.collected_counters", which is a JSON path no browser reader can open, so the hint list
 * is rendered here instead of being dropped.
 */
export function perfmonPanel(server, ctx) {
  const { panel, body } = panelShell("Perfmon Counters", ctx.label);
  (async () => {
    const res = await readTool("get_perfmon_stats", { server });
    if (res.kind === "error") return mount(body, errorStrip(res.message));
    if (res.kind === "empty") return mount(body, emptyStrip(res.message));

    const names = [...new Set((res.data.counters || []).map((c) => c.counter_name).filter(Boolean))].sort();
    if (!names.length) return mount(body, emptyStrip("The latest snapshot holds no perfmon counters."));

    const chartSlot = el("div", {}, [loadingStrip()]);
    const picker = pickerControl("Counter", names, (name) => drawPerfmonTrend(chartSlot, server, ctx, name));
    mount(body, [el("div", { class: "picker-row" }, [picker]), chartSlot]);
    drawPerfmonTrend(chartSlot, server, ctx, names[0]);
  })();
  return panel;
}

async function drawPerfmonTrend(slot, server, ctx, counterName) {
  mount(slot, loadingStrip());
  const trend = await readTool("get_perfmon_trend", { server, counter_name: counterName, hours: ctx.hours });
  if (trend.kind === "error") return mount(slot, errorStrip(trend.message));
  if (trend.kind === "empty") {
    const hinted = trend.hints && Array.isArray(trend.hints.collected_counters) ? trend.hints.collected_counters : null;
    mount(slot, [
      emptyStrip(trend.message),
      hinted && hinted.length
        ? el("div", { class: "muted", style: "margin-top:0.4rem", text: "Collected here: " + hinted.join(", ") })
        : null,
    ]);
    return;
  }
  mount(
    slot,
    renderLineChart({
      points: trend.data.trend || [],
      xKey: "time",
      series: [
        { key: "value", label: "Value", color: SERIES_COLORS[0] },
        { key: "delta_value", label: "Delta", color: SERIES_COLORS[1] },
      ],
      formatValue: (v) => Number(v).toLocaleString(undefined, { maximumFractionDigits: 2 }),
    })
  );
}

/** A labelled <select> over a list of strings, calling back with the chosen value. Options are set through
 *  el()'s text path, so a counter or wait-type name from a monitored server can never become markup (R4). */
function pickerControl(label, options, onPick) {
  const sel = el(
    "select",
    { class: "range-select-inline", "aria-label": label },
    options.map((o) => el("option", { value: o, text: o }))
  );
  sel.value = options[0];
  sel.addEventListener("change", () => onPick(sel.value));
  return el("label", { class: "range-control" }, [el("span", { text: label }), sel]);
}

/** File I/O latency: pivot the flat per-(time, database) trend into one read-latency series per database. */
export function fileIoPanel(server, ctx) {
  const { panel, body } = panelShell("File I/O Latency", "avg read latency per database, " + ctx.label);
  (async () => {
    const res = await readTool("get_file_io_trend", { server, hours: ctx.hours });
    if (res.kind === "error") return mount(body, errorStrip(res.message));
    if (res.kind === "empty") return mount(body, emptyStrip(res.message));

    const { points, series } = pivot(res.data.trend || [], {
      xKey: "time",
      seriesKey: "database_name",
      valueKey: "avg_read_latency_ms",
    });
    if (!series.length) return mount(body, emptyStrip("No file I/O samples in this window."));
    mount(body, renderLineChart({ points, xKey: "time", series, formatValue: (v) => Math.round(v) + " ms" }));
  })();
  return panel;
}

/** Reshape flat rows into per-series points, keeping the top `maxSeries` series by peak value. */
function pivot(rows, { xKey, seriesKey, valueKey }, maxSeries = 8) {
  const byTime = new Map();
  const peak = new Map();
  for (const r of rows) {
    const t = r[xKey];
    const name = r[seriesKey];
    const v = r[valueKey];
    if (t == null || name == null) continue;
    if (!byTime.has(t)) byTime.set(t, { [xKey]: t });
    byTime.get(t)[name] = v;
    peak.set(name, Math.max(peak.get(name) ?? -Infinity, v ?? -Infinity));
  }
  const names = [...peak.keys()].sort((a, b) => peak.get(b) - peak.get(a)).slice(0, maxSeries);
  const points = [...byTime.values()].sort((a, b) => String(a[xKey]).localeCompare(String(b[xKey])));
  const series = names.map((n, i) => ({ key: n, label: n, color: SERIES_COLORS[i % SERIES_COLORS.length] }));
  return { points, series };
}

/* ─────────────────────────── descriptor helpers ─────────────────────────── */

/**
 * A table panel over a read, spanning both grid columns (the default for anything wider than ~5 columns).
 *
 * `emptyText` is REQUIRED, and the throw is the reason it is a parameter rather than an option. renderPanel
 * already shows a read's own {status,message} envelope when the read has nothing, and that sentence is better
 * than anything a descriptor could carry. What it does not cover is the read returning data whose row array is
 * empty — where vizTable falls back to a generic "No rows in this window", which on a collector that is off,
 * opt-in, or daily reads as a fault. Every tab is built during the DOM-shim run, so a missing sentence fails
 * there rather than shipping as a blank rectangle nobody notices.
 */
function table(title, read, params, rowsKey, columns, subtitle, emptyText, span = 2) {
  if (!emptyText) throw new Error("table(" + title + "): a table panel must explain its own empty state.");
  return renderPanel({ title, subtitle, read, params, viz: "table", rowsKey, columns, emptyText, span });
}

/** A stat-tile panel over a read's top-level object (dotted keys reach into a nested summary). */
function stat(title, read, params, stats, subtitle, span = 1) {
  return renderPanel({ title, subtitle, read, params, viz: "stat", stats, span });
}

/** A line panel over a read's row array. */
function line(title, read, params, rowsKey, xKey, series, opts = {}) {
  return renderPanel({
    title,
    subtitle: opts.subtitle,
    read,
    params,
    viz: "line",
    rowsKey,
    xKey,
    series,
    format: opts.format,
    unit: opts.unit,
    span: opts.span ?? 1,
  });
}

/* The subtitle every latest-snapshot panel carries. These reads take no time window at all — they return the
   most recent collected snapshot — so letting the page's range label sit above them would claim a window the
   data does not have. */
const SNAPSHOT = "latest snapshot";

/* ─────────────────────────── the tabs ─────────────────────────── */

/**
 * The tab registry, in the order the desktop viewer presents them. `build(server, ctx)` returns the tab's nodes;
 * `ctx` is `{ hours, label }` — the page's time range and its human label.
 */
export const SERVER_TABS = [
  {
    id: "overview",
    label: "Overview",
    build: (server, ctx) => [
      stat("Overview", "get_server_summary", { server }, OVERVIEW_STATS, SNAPSHOT, 2),
      stat("Server Properties", "get_server_properties", { server }, PROPERTY_STATS, SNAPSHOT, 2),
      line("CPU Utilization", "get_cpu_utilization", { server, hours: ctx.hours }, "samples", "sample_time", CPU_SERIES, {
        subtitle: ctx.label,
        format: "pct",
        unit: "%",
      }),
      line("Memory", "get_memory_trend", { server, hours: ctx.hours }, "trend", "time", MEMORY_SERIES, {
        subtitle: ctx.label,
        format: "mb",
      }),
      line("Blocking Events", "get_blocking_trend", { server, hours: ctx.hours }, "trend", "time", COUNT_SERIES, {
        subtitle: ctx.label,
      }),
      line("Deadlocks", "get_deadlock_trend", { server, hours: ctx.hours }, "trend", "time", COUNT_SERIES, {
        subtitle: ctx.label,
      }),
      fileIoPanel(server, ctx),
      table(
        "Analysis Findings",
        "get_analysis_findings",
        { server, hours: ctx.hours },
        "findings",
        FINDING_COLUMNS,
        ctx.label,
        "No findings in this window. Findings are written by the analysis pass, which needs at least 24 hours of collected history."
      ),
      stat("Daily Summary", "get_daily_summary", { server }, DAILY_STATS, "today (UTC)", 2),
    ],
  },

  {
    id: "waits",
    label: "Wait Stats",
    build: (server, ctx) => [
      waitsPanel(server, ctx),
      table(
        "Waiting Tasks",
        "get_waiting_tasks",
        { server, hours: ctx.hours, limit: 30 },
        "tasks",
        WAITING_TASK_COLUMNS,
        ctx.label,
        "No waiting tasks were captured in this window."
      ),
      table(
        "Latch Stats",
        "get_latch_stats",
        { server, hours: ctx.hours, top: 10 },
        "latches",
        LATCH_COLUMNS,
        ctx.label,
        "No latch classes accumulated waits in this window."
      ),
      table(
        "Spinlock Stats",
        "get_spinlock_stats",
        { server, hours: ctx.hours, top: 10 },
        "spinlocks",
        SPINLOCK_COLUMNS,
        ctx.label,
        "No spinlocks recorded collisions in this window."
      ),
    ],
  },

  {
    id: "cpu",
    label: "CPU",
    build: (server, ctx) => [
      line("CPU Utilization", "get_cpu_utilization", { server, hours: ctx.hours }, "samples", "sample_time", CPU_SERIES, {
        subtitle: ctx.label,
        format: "pct",
        unit: "%",
        span: 2,
      }),
      stat("Scheduler Pressure", "get_cpu_scheduler_pressure", { server }, SCHEDULER_STATS, SNAPSHOT, 2),
      table(
        "Top Queries by CPU",
        "get_top_queries_by_cpu",
        { server, hours: ctx.hours, top: 20 },
        "queries",
        TOP_QUERY_COLUMNS,
        ctx.label,
        "No query stats in this window. Delta-based collection needs at least two cycles (~30 minutes) before it reports non-zero values."
      ),
      table(
        "Top Procedures by CPU",
        "get_top_procedures_by_cpu",
        { server, hours: ctx.hours, top: 20 },
        "procedures",
        TOP_PROC_COLUMNS,
        ctx.label,
        "No procedure stats in this window. Delta-based collection needs at least two cycles (~30 minutes)."
      ),
    ],
  },

  {
    id: "memory",
    label: "Memory",
    build: (server, ctx) => [
      stat("Memory", "get_memory_stats", { server }, MEMORY_STATS, SNAPSHOT, 2),
      line("Memory Trend", "get_memory_trend", { server, hours: ctx.hours }, "trend", "time", MEMORY_SERIES, {
        subtitle: ctx.label,
        format: "mb",
        span: 2,
      }),
      table(
        "Memory Clerks",
        "get_memory_clerks",
        { server },
        "clerks",
        CLERK_COLUMNS,
        SNAPSHOT,
        "No memory clerks in the latest snapshot — the clerk collector may not have run yet.",
        1
      ),
      line(
        "Memory Grants",
        "get_memory_grants",
        { server, hours: ctx.hours },
        "grants",
        "collection_time",
        GRANT_SERIES,
        { subtitle: ctx.label, format: "mb" }
      ),
      table(
        "Resource Semaphore",
        "get_resource_semaphore",
        { server, hours: ctx.hours },
        "grants",
        SEMAPHORE_COLUMNS,
        ctx.label,
        "No resource-semaphore samples in this window."
      ),
      table(
        "Memory Pressure Events",
        "get_memory_pressure_events",
        { server, hours: ctx.hours },
        "events",
        PRESSURE_COLUMNS,
        ctx.label,
        "No memory pressure events in this window — the healthy state for this read.",
        1
      ),
      stat("Plan Cache", "get_plan_cache_bloat", { server, hours: ctx.hours }, PLAN_CACHE_STATS, ctx.label, 2),
      table(
        "Plan Cache by Type",
        "get_plan_cache_bloat",
        { server, hours: ctx.hours },
        "cache_types",
        CACHE_TYPE_COLUMNS,
        ctx.label,
        "No plan-cache breakdown in this window."
      ),
    ],
  },

  {
    id: "blocking",
    label: "Blocking",
    note:
      "Blocked-process reports and deadlock graphs are shown here as their captured XML. The block-chain view " +
      "and the interactive deadlock graph are desktop-viewer features.",
    build: (server, ctx) => [
      line("Blocking Events", "get_blocking_trend", { server, hours: ctx.hours }, "trend", "time", COUNT_SERIES, {
        subtitle: ctx.label,
      }),
      line("Deadlocks", "get_deadlock_trend", { server, hours: ctx.hours }, "trend", "time", COUNT_SERIES, {
        subtitle: ctx.label,
      }),
      table(
        "Blocking",
        "get_blocking",
        { server, hours: ctx.hours, limit: 30 },
        "events",
        BLOCKING_COLUMNS,
        ctx.label,
        "No blocking events in this window."
      ),
      table(
        "Deadlocks",
        "get_deadlocks",
        { server, hours: ctx.hours, limit: 20 },
        "deadlocks",
        DEADLOCK_COLUMNS,
        ctx.label,
        "No deadlocks in this window."
      ),
      table(
        "Deadlock Graphs",
        "get_deadlock_detail",
        { server, hours: ctx.hours, limit: 5 },
        "deadlocks",
        DEADLOCK_XML_COLUMNS,
        ctx.label,
        "No deadlock graph XML captured in this window."
      ),
      table(
        "Blocked Process Reports",
        "get_blocked_process_xml",
        { server, hours: ctx.hours, limit: 5 },
        "reports",
        BPR_COLUMNS,
        ctx.label,
        "No blocked-process report XML in this window. The report is only written when the blocked process threshold is configured on the target."
      ),
      table(
        "Object Contention",
        "get_object_locking",
        { server },
        "objects",
        OBJECT_LOCK_COLUMNS,
        "daily collection",
        "No lock-wait rows recorded. Index and object stats are collected daily."
      ),
    ],
  },

  {
    id: "io",
    label: "File I/O",
    build: (server, ctx) => [
      fileIoPanel(server, ctx),
      table(
        "File I/O Stats",
        "get_file_io_stats",
        { server },
        "files",
        FILE_IO_COLUMNS,
        SNAPSHOT,
        "No file I/O rows in the latest snapshot."
      ),
      line("tempdb", "get_tempdb_trend", { server, hours: ctx.hours }, "trend", "time", TEMPDB_SERIES, {
        subtitle: ctx.label,
        format: "mb",
        span: 2,
      }),
      table(
        "Database Sizes",
        "get_database_sizes",
        { server },
        "databases",
        DB_SIZE_COLUMNS,
        SNAPSHOT,
        "No database sizes in the latest snapshot.",
        1
      ),
      table(
        "Table & Index Sizes",
        "get_table_index_sizes",
        { server },
        "tables",
        TABLE_SIZE_COLUMNS,
        "daily collection",
        "No object size rows recorded. Index and object stats are collected daily."
      ),
      table(
        "Persistent Version Store",
        "get_pvs_stats",
        { server },
        "databases",
        PVS_COLUMNS,
        SNAPSHOT,
        "No PVS rows. The collector reads a SQL Server 2019+ DMV, and a server with Accelerated Database Recovery off has nothing to report."
      ),
    ],
  },

  {
    id: "queries",
    label: "Queries",
    note:
      "Execution-plan analysis, the query heatmap, cached-plan retrieval and actual-plan re-execution are " +
      "desktop-viewer features — they need a plan renderer and a command back to the monitored server, neither " +
      "of which this read-only web seat has.",
    build: (server, ctx) => [
      table(
        "Active Queries",
        "get_active_queries",
        { server, hours: ctx.hours, limit: 50 },
        "queries",
        ACTIVE_COLUMNS,
        ctx.label,
        "No active-query snapshots in this window."
      ),
      line(
        "Query Duration Trend",
        "get_query_duration_trend",
        { server, hours: ctx.hours },
        "trend",
        "time",
        DURATION_SERIES,
        { subtitle: ctx.label, format: "ms", span: 2 }
      ),
      table(
        "Top Queries by CPU",
        "get_top_queries_by_cpu",
        { server, hours: ctx.hours, top: 20 },
        "queries",
        TOP_QUERY_COLUMNS,
        ctx.label,
        "No query stats in this window. Delta-based collection needs at least two cycles (~30 minutes)."
      ),
      table(
        "Top Procedures by CPU",
        "get_top_procedures_by_cpu",
        { server, hours: ctx.hours, top: 20 },
        "procedures",
        TOP_PROC_COLUMNS,
        ctx.label,
        "No procedure stats in this window. Delta-based collection needs at least two cycles (~30 minutes)."
      ),
      table(
        "Query Store",
        "get_query_store_top",
        { server, hours: ctx.hours, top: 20 },
        "queries",
        QUERY_STORE_COLUMNS,
        ctx.label,
        "No Query Store rows in this window."
      ),
      table(
        "Long Query Completions",
        "get_long_query_completions",
        { server, hours: ctx.hours, limit: 30 },
        "completions",
        LONG_QUERY_COLUMNS,
        ctx.label,
        "No long-running completions in this window. This collector is opt-in and off by default."
      ),
      table(
        "Plan Corrections",
        "get_plan_corrections",
        { server, hours: ctx.hours, limit: 50 },
        "recommendations",
        PLAN_CORRECTION_COLUMNS,
        ctx.label,
        "No tuning recommendations in this window."
      ),
      table(
        "Automatic Tuning",
        "get_plan_corrections",
        { server, hours: ctx.hours, limit: 50 },
        "automatic_tuning",
        AUTO_TUNING_COLUMNS,
        SNAPSHOT,
        "No per-database FORCE_LAST_GOOD_PLAN state recorded.",
        1
      ),
    ],
  },

  {
    id: "config",
    label: "Configuration",
    build: (server) => [
      stat("Server Properties", "get_server_properties", { server }, PROPERTY_STATS, SNAPSHOT, 2),
      table(
        "Configuration Audit",
        "audit_config",
        { server },
        "recommendations",
        AUDIT_COLUMNS,
        SNAPSHOT,
        "The audit found nothing to flag."
      ),
      table(
        "Server Configuration",
        "get_server_config",
        { server },
        "settings",
        SERVER_CONFIG_COLUMNS,
        SNAPSHOT,
        "No sp_configure snapshot yet."
      ),
      table(
        "Database Configuration",
        "get_database_config",
        { server },
        "databases",
        DB_CONFIG_COLUMNS,
        SNAPSHOT,
        "No database configuration snapshot yet."
      ),
      table(
        "Query Store Health",
        "get_query_store_health",
        { server },
        "databases",
        QS_HEALTH_COLUMNS,
        "hourly collection",
        "No Query Store health rows yet."
      ),
      table(
        "Trace Flags",
        "get_trace_flags",
        { server },
        "trace_flags",
        TRACE_FLAG_COLUMNS,
        SNAPSHOT,
        "No trace flags are enabled on this server.",
        1
      ),
    ],
  },

  {
    id: "changes",
    label: "Config Changes",
    build: (server, ctx) => [
      table(
        "Server Configuration Changes",
        "get_server_config_changes",
        { server, hours: ctx.hours },
        "changes",
        SERVER_CHANGE_COLUMNS,
        ctx.label,
        "No server configuration changed in this window."
      ),
      table(
        "Database Configuration Changes",
        "get_database_config_changes",
        { server, hours: ctx.hours },
        "changes",
        DB_CHANGE_COLUMNS,
        ctx.label,
        "No database configuration changed in this window."
      ),
      table(
        "Trace Flag Changes",
        "get_trace_flag_changes",
        { server, hours: ctx.hours },
        "changes",
        TRACE_FLAG_CHANGE_COLUMNS,
        ctx.label,
        "No trace flags changed in this window."
      ),
    ],
  },

  {
    id: "activity",
    label: "Activity",
    build: (server, ctx) => [
      perfmonPanel(server, ctx),
      stat("Sessions", "get_session_stats", { server }, SESSION_STATS, SNAPSHOT, 2),
      table(
        "Sessions by Application",
        "get_session_stats",
        { server },
        "applications",
        APPLICATION_COLUMNS,
        SNAPSHOT,
        "No application rows in the latest session snapshot."
      ),
      table(
        "Running Jobs",
        "get_running_jobs",
        { server },
        "jobs",
        JOB_COLUMNS,
        SNAPSHOT,
        "No SQL Agent jobs were running at the last collection — the normal state for most servers."
      ),
      table(
        "Perfmon Snapshot",
        "get_perfmon_stats",
        { server },
        "counters",
        PERFMON_COLUMNS,
        SNAPSHOT,
        "No perfmon counters in the latest snapshot."
      ),
      table(
        "Index Usage",
        "get_index_usage",
        { server },
        "indexes",
        INDEX_COLUMNS,
        "daily collection",
        "No index usage rows recorded. Index and object stats are collected daily."
      ),
    ],
  },

  {
    id: "events",
    label: "System Events",
    note:
      "These are the system_health session and default trace, parsed on read. The desktop viewer additionally " +
      "charts the corruption and contention counters hour-by-hour; here they are the raw parsed rows.",
    build: (server, ctx) => [
      line(
        "system_health CPU",
        "get_health_parser_system_health",
        { server, hours: ctx.hours, limit: 50 },
        "entries",
        "event_time",
        HEALTH_CPU_SERIES,
        { subtitle: ctx.label, format: "pct", unit: "%", span: 2 }
      ),
      table(
        "system_health Entries",
        "get_health_parser_system_health",
        { server, hours: ctx.hours, limit: 50 },
        "entries",
        HEALTH_ENTRY_COLUMNS,
        ctx.label,
        "No system_health entries in this window."
      ),
      table(
        "Severe Errors",
        "get_health_parser_severe_errors",
        { server, hours: ctx.hours, limit: 50 },
        "errors",
        SEVERE_ERROR_COLUMNS,
        ctx.label,
        "No severe errors in this window — the healthy state for this read."
      ),
      table(
        "Scheduler Issues",
        "get_health_parser_scheduler_issues",
        { server, hours: ctx.hours, limit: 50 },
        "issues",
        SCHEDULER_ISSUE_COLUMNS,
        ctx.label,
        "No scheduler issues in this window — the healthy state for this read."
      ),
      table(
        "I/O Issues",
        "get_health_parser_io_issues",
        { server, hours: ctx.hours, limit: 50 },
        "issues",
        IO_ISSUE_COLUMNS,
        ctx.label,
        "No I/O issues in this window — the healthy state for this read."
      ),
      table(
        "CPU Tasks",
        "get_health_parser_cpu_tasks",
        { server, hours: ctx.hours, limit: 50 },
        "events",
        CPU_TASK_COLUMNS,
        ctx.label,
        "No CPU task events in this window."
      ),
      table(
        "Memory Conditions",
        "get_health_parser_memory_conditions",
        { server, hours: ctx.hours, limit: 50 },
        "events",
        MEMORY_CONDITION_COLUMNS,
        ctx.label,
        "No memory condition events in this window."
      ),
      table(
        "Memory Broker",
        "get_health_parser_memory_broker",
        { server, hours: ctx.hours, limit: 50 },
        "events",
        MEMORY_BROKER_COLUMNS,
        ctx.label,
        "No memory broker events in this window."
      ),
      table(
        "Memory Node OOM",
        "get_health_parser_memory_node_oom",
        { server, hours: ctx.hours, limit: 50 },
        "events",
        MEMORY_OOM_COLUMNS,
        ctx.label,
        "No memory node OOM events in this window — the healthy state for this read."
      ),
      table(
        "Default Trace",
        "get_default_trace_events",
        { server, hours: ctx.hours, limit: 100 },
        "events",
        DEFAULT_TRACE_COLUMNS,
        ctx.label,
        "No significant default trace events in this window."
      ),
    ],
  },

  {
    id: "health",
    label: "Collection Health",
    build: (server) => [
      stat("Sweep Pressure", "get_collection_health", { server }, SWEEP_STATS, "trailing 7 days", 2),
      table(
        "Collectors",
        "get_collection_health",
        { server },
        "collectors",
        COLLECTOR_COLUMNS,
        "trailing 7 days",
        "No collection log rows for this server yet."
      ),
      table(
        "Heaviest Collectors",
        "get_collection_health",
        { server },
        "sweep_pressure.heaviest_collectors",
        HEAVIEST_COLUMNS,
        "trailing 7 days",
        "No per-collector timings recorded yet."
      ),
    ],
  },
];

/** The tab for an id, falling back to the first (Overview) — an unknown/absent id is a deep link, not an error. */
export function findServerTab(id) {
  return SERVER_TABS.find((t) => t.id === id) || SERVER_TABS[0];
}

/** The tab's note as a rendered strip, or null. Kept here so the shell has no opinion about its wording. */
export function tabNote(tab) {
  return tab.note ? noticeStrip(tab.note) : null;
}

/* ─────────────────────────── stat descriptors ─────────────────────────── */

const OVERVIEW_STATS = [
  { key: "cpu_percent", label: "CPU", format: "pct" },
  { key: "memory_mb", label: "Memory", format: "mb" },
  { key: "blocking_count", label: "Blocking (recent)", format: "int" },
  { key: "deadlock_count", label: "Deadlocks (recent)", format: "int" },
  { key: "last_collection", label: "Last collection", format: "reltime", small: true },
];

const PROPERTY_STATS = [
  { key: "product_version", label: "Version", format: "text", small: true },
  { key: "edition", label: "Edition", format: "text", small: true },
  { key: "product_level", label: "Level", format: "text", small: true },
  { key: "cpu_count", label: "Logical CPUs", format: "int" },
  { key: "socket_count", label: "Sockets", format: "int" },
  { key: "cores_per_socket", label: "Cores/socket", format: "int" },
  { key: "hyperthread_ratio", label: "HT ratio", format: "int" },
  { key: "physical_memory_mb", label: "Physical memory", format: "mb" },
  { key: "is_clustered", label: "Clustered", format: "bool" },
  { key: "is_hadr_enabled", label: "Always On", format: "bool" },
  { key: "service_objective", label: "Service objective", format: "text", small: true },
];

const DAILY_STATS = [
  { key: "summary_date", label: "Date", format: "text", small: true },
  { key: "health_band", label: "Band", format: "text", small: true },
  { key: "overall_health", label: "Health", format: "num1" },
  { key: "top_wait_type", label: "Top wait", format: "text", small: true },
  { key: "total_wait_time_sec", label: "Total wait", format: "int" },
  { key: "unique_queries", label: "Unique queries", format: "int" },
  { key: "blocking_events", label: "Blocking", format: "int" },
  { key: "deadlock_count", label: "Deadlocks", format: "int" },
  { key: "alert_count", label: "Alerts", format: "int" },
  { key: "collection_errors", label: "Collection errors", format: "int" },
];

const SCHEDULER_STATS = [
  { key: "pressure_level", label: "Pressure", format: "text", small: true },
  { key: "schedulers", label: "Schedulers", format: "int" },
  { key: "runnable_tasks", label: "Runnable tasks", format: "int" },
  { key: "avg_runnable_per_scheduler", label: "Runnable/sched", format: "num2" },
  { key: "runnable_percent", label: "Runnable %", format: "num1" },
  { key: "workers", label: "Workers", format: "int" },
  { key: "max_workers", label: "Max workers", format: "int" },
  { key: "worker_utilization_percent", label: "Worker use %", format: "num1" },
  { key: "active_requests", label: "Active requests", format: "int" },
  { key: "queued_requests", label: "Queued requests", format: "int" },
  { key: "recommendation", label: "Recommendation", format: "text", small: true },
];

const MEMORY_STATS = [
  { key: "total_physical_memory_mb", label: "Physical", format: "mb" },
  { key: "available_physical_memory_mb", label: "Available", format: "mb" },
  { key: "memory_utilization_pct", label: "Utilization", format: "pct" },
  { key: "total_server_memory_mb", label: "Total server", format: "mb" },
  { key: "target_server_memory_mb", label: "Target server", format: "mb" },
  { key: "buffer_pool_mb", label: "Buffer pool", format: "mb" },
  { key: "plan_cache_mb", label: "Plan cache", format: "mb" },
  { key: "system_memory_state", label: "System state", format: "text", small: true },
  { key: "sql_memory_model", label: "Memory model", format: "text", small: true },
];

const PLAN_CACHE_STATS = [
  { key: "summary.bloat_level", label: "Bloat", format: "text", small: true },
  { key: "summary.total_plans", label: "Plans", format: "int" },
  { key: "summary.single_use_plans", label: "Single-use", format: "int" },
  { key: "summary.single_use_percent", label: "Single-use %", format: "num1" },
  { key: "summary.total_size_mb", label: "Cache size", format: "mb" },
  { key: "summary.single_use_size_mb", label: "Single-use size", format: "mb" },
  { key: "summary.wasted_percent", label: "Wasted %", format: "num1" },
  { key: "summary.bloat_recommendation", label: "Recommendation", format: "text", small: true },
];

const SESSION_STATS = [
  { key: "summary.total_connections", label: "Connections", format: "int" },
  { key: "summary.total_running", label: "Running", format: "int" },
  { key: "summary.total_sleeping", label: "Sleeping", format: "int" },
  { key: "summary.total_dormant", label: "Dormant", format: "int" },
  { key: "summary.distinct_applications", label: "Applications", format: "int" },
  { key: "collection_time", label: "Collected", format: "reltime", small: true },
];

const SWEEP_STATS = [
  { key: "sweep_pressure.verdict", label: "Verdict", format: "text", small: true },
  { key: "sweep_pressure.busy_percent", label: "Sweep busy %", format: "num1" },
  { key: "sweep_pressure.busy_ms_per_minute", label: "Busy ms/min", format: "int" },
  { key: "sweep_pressure.peak_cycle_ms", label: "Peak cycle", format: "ms" },
  { key: "sweep_pressure.peak_cycle_percent", label: "Peak cycle %", format: "num1" },
  { key: "sweep_pressure.peak_cycle_risk", label: "Peak risk", format: "text", small: true },
];

/* ─────────────────────────── line series ─────────────────────────── */

/* Neutral series colors assigned by the chart's ramp (B1) — no severity colors on chart lines. idle_cpu is
   dropped (B3): it would force a 0-100 domain and crush the real SQL/other/total series. */
const CPU_SERIES = [
  { key: "sql_server_cpu", label: "SQL CPU %" },
  { key: "other_process_cpu", label: "Other %" },
  { key: "total_cpu", label: "Total %" },
];

const MEMORY_SERIES = [
  { key: "total_server_memory_mb", label: "Total Server" },
  { key: "target_server_memory_mb", label: "Target" },
  { key: "buffer_pool_mb", label: "Buffer Pool" },
  { key: "plan_cache_mb", label: "Plan Cache" },
];

/* The two trend reads that return {time, count}. */
const COUNT_SERIES = [{ key: "count", label: "Events" }];

/* get_query_duration_trend returns {time, value, execution_count}. Only `value` (milliseconds) is charted —
   an execution count on the same axis would be a second unit sharing one y-domain, which is the mistake the
   CPU chart's dropped idle_cpu series exists to avoid. The count is in the Query Store / top-query tables. */
const DURATION_SERIES = [{ key: "value", label: "Avg duration" }];

const GRANT_SERIES = [
  { key: "granted_memory_mb", label: "Granted" },
  { key: "used_memory_mb", label: "Used" },
  { key: "available_memory_mb", label: "Available" },
];

const TEMPDB_SERIES = [
  { key: "total_reserved_mb", label: "Reserved" },
  { key: "user_objects_mb", label: "User objects" },
  { key: "internal_objects_mb", label: "Internal objects" },
  { key: "version_store_mb", label: "Version store" },
];

const HEALTH_CPU_SERIES = [
  { key: "sql_cpu_utilization", label: "SQL CPU %" },
  { key: "system_cpu_utilization", label: "System CPU %" },
];

/* ─────────────────────────── table columns ─────────────────────────── */

const WAIT_COLUMNS = [
  { key: "wait_type", label: "Wait Type" },
  { key: "total_wait_time_ms", label: "Total Wait", format: "ms" },
  { key: "resource_wait_ms", label: "Resource", format: "ms" },
  { key: "total_signal_wait_ms", label: "Signal", format: "ms" },
  { key: "waiting_tasks", label: "Tasks", format: "int" },
  { key: "signal_wait_pct", label: "Signal %", format: "num1" },
];

const WAITING_TASK_COLUMNS = [
  { key: "collection_time", label: "Time", format: "time" },
  { key: "session_id", label: "SPID", format: "int" },
  { key: "wait_type", label: "Wait" },
  { key: "wait_duration_ms", label: "Duration", format: "ms" },
  { key: "blocking_session_id", label: "Blocked by", format: "int" },
  { key: "database_name", label: "Database" },
];

const LATCH_COLUMNS = [
  { key: "latch_class", label: "Latch Class" },
  { key: "severity", label: "Severity", statusSev: true },
  { key: "total_delta_wait_time_ms", label: "Wait", format: "ms" },
  { key: "total_delta_waiting_requests", label: "Requests", format: "int" },
  { key: "avg_wait_ms_per_request", label: "Avg/req", format: "num2" },
  { key: "wait_ms_per_second", label: "ms/s", format: "num2" },
  { key: "description", label: "What it means", wrap: true },
];

const SPINLOCK_COLUMNS = [
  { key: "spinlock_name", label: "Spinlock" },
  { key: "total_delta_collisions", label: "Collisions", format: "int" },
  { key: "total_delta_spins", label: "Spins", format: "int" },
  { key: "total_delta_backoffs", label: "Backoffs", format: "int" },
  { key: "spins_per_collision", label: "Spins/coll", format: "num1" },
  { key: "collisions_per_second", label: "Coll/s", format: "num2" },
  { key: "description", label: "What it means", wrap: true },
];

/* #1949 ordering, which every query grid in both apps follows: the time/identity anchor, then the QUERY TEXT,
   then the metrics. Text pushed behind the metrics is text nobody scrolls to. */
const TOP_QUERY_COLUMNS = [
  { key: "database_name", label: "Database" },
  { key: "query_text", label: "Query", render: (r) => codeDisclosure(r.query_text) },
  { key: "host_object", label: "Host object" },
  { key: "execution_count", label: "Execs", format: "int" },
  { key: "total_cpu_ms", label: "Total CPU", format: "ms" },
  { key: "avg_cpu_ms", label: "Avg CPU", format: "ms" },
  { key: "total_elapsed_ms", label: "Total Elapsed", format: "ms" },
  { key: "avg_elapsed_ms", label: "Avg Elapsed", format: "ms" },
  { key: "max_cpu_ms", label: "Max CPU", format: "ms" },
  { key: "max_dop", label: "Max DOP", format: "int" },
  { key: "total_spills", label: "Spills", format: "int" },
  { key: "query_hash", label: "Query Hash", mono: true },
];

const TOP_PROC_COLUMNS = [
  { key: "full_name", label: "Procedure" },
  { key: "database_name", label: "Database" },
  { key: "object_type", label: "Type" },
  { key: "execution_count", label: "Execs", format: "int" },
  { key: "total_cpu_ms", label: "Total CPU", format: "ms" },
  { key: "avg_cpu_ms", label: "Avg CPU", format: "ms" },
  { key: "total_elapsed_ms", label: "Total Elapsed", format: "ms" },
  { key: "avg_elapsed_ms", label: "Avg Elapsed", format: "ms" },
  { key: "max_cpu_ms", label: "Max CPU", format: "ms" },
  { key: "total_spills", label: "Spills", format: "int" },
];

const QUERY_STORE_COLUMNS = [
  { key: "database_name", label: "Database" },
  { key: "query_text", label: "Query", render: (r) => codeDisclosure(r.query_text) },
  { key: "query_id", label: "Query ID", format: "int" },
  { key: "plan_id", label: "Plan ID", format: "int" },
  { key: "execution_count", label: "Execs", format: "int" },
  { key: "avg_duration_ms", label: "Avg Duration", format: "ms" },
  { key: "avg_cpu_ms", label: "Avg CPU", format: "ms" },
  { key: "avg_rowcount", label: "Avg Rows", format: "num1" },
  { key: "last_execution_time", label: "Last Exec", format: "time" },
  { key: "replica_role", label: "Replica" },
];

const LONG_QUERY_COLUMNS = [
  { key: "event_time", label: "Time", format: "time" },
  { key: "statement", label: "Statement", render: (r) => codeDisclosure(r.statement) },
  { key: "database_name", label: "Database" },
  { key: "object_name", label: "Object" },
  { key: "duration_ms", label: "Duration", format: "ms" },
  { key: "cpu_ms", label: "CPU", format: "ms" },
  { key: "row_count", label: "Rows", format: "int" },
  { key: "result", label: "Result" },
  { key: "client_app_name", label: "Application" },
  { key: "session_id", label: "SPID", format: "int" },
];

const PLAN_CORRECTION_COLUMNS = [
  { key: "collection_time", label: "Collected", format: "time" },
  { key: "query_text", label: "Query", render: (r) => codeDisclosure(r.query_text) },
  { key: "database_name", label: "Database" },
  { key: "query_id", label: "Query ID", format: "int" },
  { key: "recommendation_state", label: "State" },
  { key: "recommendation_reason", label: "Reason", wrap: true },
  { key: "score", label: "Score", format: "int" },
  { key: "estimated_gain_seconds", label: "Est. gain (s)", format: "num1" },
  { key: "last_good_plan_is_forced", label: "Forced", format: "bool" },
];

const AUTO_TUNING_COLUMNS = [
  { key: "database_name", label: "Database" },
  { key: "force_last_good_plan_desired_state", label: "Desired" },
  { key: "force_last_good_plan_actual_state", label: "Actual" },
  { key: "force_last_good_plan_reason", label: "Reason", wrap: true },
  { key: "as_of", label: "As of", format: "time" },
];

const ACTIVE_COLUMNS = [
  { key: "collection_time", label: "Time", format: "time" },
  { key: "query_text", label: "Query", render: (r) => codeDisclosure(r.query_text) },
  { key: "session_id", label: "SPID", format: "int" },
  { key: "database_name", label: "Database" },
  { key: "status", label: "Status" },
  { key: "cpu_time_ms", label: "CPU", format: "ms" },
  { key: "elapsed_time_formatted", label: "Elapsed" },
  { key: "wait_type", label: "Wait" },
  { key: "blocking_session_id", label: "Blocked by", format: "int" },
  { key: "dop", label: "DOP", format: "int" },
  { key: "program_name", label: "Application" },
  { key: "login_name", label: "Login" },
];

const BLOCKING_COLUMNS = [
  { key: "event_time", label: "Time", format: "time" },
  { key: "blocked_sql_text", label: "Blocked SQL", render: (r) => codeDisclosure(r.blocked_sql_text) },
  { key: "blocking_sql_text", label: "Blocking SQL", render: (r) => codeDisclosure(r.blocking_sql_text) },
  { key: "database_name", label: "Database" },
  { key: "blocked_spid", label: "Blocked", format: "int" },
  { key: "blocking_spid", label: "Blocker", format: "int" },
  { key: "wait_time_ms", label: "Wait", format: "ms" },
  { key: "lock_mode", label: "Mode" },
  { key: "contentious_object", label: "Object" },
  { key: "blocked_client_app", label: "Blocked App" },
  { key: "blocking_client_app", label: "Blocking App" },
];

const DEADLOCK_COLUMNS = [
  { key: "deadlock_time", label: "Deadlock Time", format: "time" },
  { key: "victim_sql_text", label: "Victim SQL", render: (r) => codeDisclosure(r.victim_sql_text) },
  { key: "victim_process_id", label: "Victim" },
  { key: "process_summary", label: "Processes", wrap: true },
  { key: "has_deadlock_xml", label: "Graph", format: "bool" },
];

const DEADLOCK_XML_COLUMNS = [
  { key: "deadlock_time", label: "Deadlock Time", format: "time" },
  { key: "victim_process_id", label: "Victim" },
  { key: "deadlock_graph_xml", label: "Deadlock graph", render: (r) => xmlDisclosure(r.deadlock_graph_xml) },
];

const BPR_COLUMNS = [
  { key: "event_time", label: "Time", format: "time" },
  { key: "database_name", label: "Database" },
  { key: "blocked_spid", label: "Blocked", format: "int" },
  { key: "blocking_spid", label: "Blocker", format: "int" },
  { key: "wait_time_ms", label: "Wait", format: "ms" },
  { key: "blocked_process_report_xml", label: "Report", render: (r) => xmlDisclosure(r.blocked_process_report_xml) },
];

const OBJECT_LOCK_COLUMNS = [
  { key: "database_name", label: "Database" },
  { key: "schema_name", label: "Schema" },
  { key: "table_name", label: "Table" },
  { key: "index_name", label: "Index" },
  { key: "row_lock_wait_ms", label: "Row lock wait", format: "ms" },
  { key: "page_lock_wait_ms", label: "Page lock wait", format: "ms" },
  { key: "lock_escalations", label: "Escalations", format: "int" },
  { key: "page_latch_wait_ms", label: "Page latch", format: "ms" },
  { key: "page_io_latch_wait_ms", label: "Page IO latch", format: "ms" },
  { key: "total_rows", label: "Rows", format: "int" },
];

const FILE_IO_COLUMNS = [
  { key: "database_name", label: "Database" },
  { key: "file_name", label: "File" },
  { key: "file_type", label: "Type" },
  { key: "size_mb", label: "Size", format: "mb" },
  { key: "avg_read_latency_ms", label: "Read latency", format: "num1" },
  { key: "avg_write_latency_ms", label: "Write latency", format: "num1" },
  { key: "delta_reads", label: "Reads", format: "int" },
  { key: "delta_writes", label: "Writes", format: "int" },
  { key: "physical_name", label: "Path", wrap: true },
];

const DB_SIZE_COLUMNS = [
  { key: "database_name", label: "Database" },
  { key: "total_size_mb", label: "Total", format: "mb" },
  { key: "used_size_mb", label: "Used", format: "mb" },
];

const TABLE_SIZE_COLUMNS = [
  { key: "database_name", label: "Database" },
  { key: "schema_name", label: "Schema" },
  { key: "table_name", label: "Table" },
  { key: "reserved_mb", label: "Reserved", format: "mb" },
  { key: "used_mb", label: "Used", format: "mb" },
  { key: "total_rows", label: "Rows", format: "int" },
  { key: "index_count", label: "Indexes", format: "int" },
  { key: "growth_7d_mb", label: "7d growth", format: "mb" },
  { key: "growth_30d_mb", label: "30d growth", format: "mb" },
  { key: "growth_pct_30d", label: "30d %", format: "num1" },
];

const PVS_COLUMNS = [
  { key: "database_name", label: "Database" },
  { key: "is_adr_on", label: "ADR", format: "bool" },
  { key: "pvs_size_mb", label: "PVS size", format: "mb" },
  { key: "pct_of_database", label: "% of DB", format: "num1" },
  { key: "database_data_size_mb", label: "Data size", format: "mb" },
  { key: "aborted_transaction_count", label: "Aborted txns", format: "int" },
  { key: "oldest_active_transaction_id", label: "Oldest active txn" },
];

const CLERK_COLUMNS = [
  { key: "clerk_type", label: "Clerk" },
  { key: "memory_mb", label: "Memory", format: "mb" },
];

const SEMAPHORE_COLUMNS = [
  { key: "collection_time", label: "Time", format: "time" },
  { key: "pool_id", label: "Pool", format: "int" },
  { key: "target_memory_mb", label: "Target", format: "mb" },
  { key: "total_memory_mb", label: "Total", format: "mb" },
  { key: "granted_memory_mb", label: "Granted", format: "mb" },
  { key: "used_memory_mb", label: "Used", format: "mb" },
  { key: "available_memory_mb", label: "Available", format: "mb" },
  { key: "grantee_count", label: "Grantees", format: "int" },
  { key: "waiter_count", label: "Waiters", format: "int" },
  { key: "timeout_error_count_delta", label: "Timeouts", format: "int" },
  { key: "forced_grant_count_delta", label: "Forced grants", format: "int" },
];

const PRESSURE_COLUMNS = [
  { key: "sample_time", label: "Time", format: "time" },
  { key: "memory_notification", label: "Notification" },
  { key: "memory_indicators_process", label: "Process", format: "int" },
  { key: "memory_indicators_system", label: "System", format: "int" },
];

const CACHE_TYPE_COLUMNS = [
  { key: "cache_type", label: "Cache" },
  { key: "object_type", label: "Object type" },
  { key: "total_plans", label: "Plans", format: "int" },
  { key: "total_size_mb", label: "Size", format: "mb" },
  { key: "single_use_plans", label: "Single-use", format: "int" },
  { key: "single_use_size_mb", label: "Single-use size", format: "mb" },
  { key: "avg_use_count", label: "Avg uses", format: "num1" },
];

const SERVER_CONFIG_COLUMNS = [
  { key: "name", label: "Setting" },
  { key: "value_configured", label: "Configured" },
  { key: "value_in_use", label: "In use" },
  { key: "values_match", label: "Match", format: "bool" },
  { key: "is_dynamic", label: "Dynamic", format: "bool" },
  { key: "is_advanced", label: "Advanced", format: "bool" },
];

const DB_CONFIG_COLUMNS = [
  { key: "database_name", label: "Database" },
  { key: "state", label: "State" },
  { key: "compatibility_level", label: "Compat", format: "int" },
  { key: "recovery_model", label: "Recovery" },
  { key: "rcsi", label: "RCSI", format: "bool" },
  { key: "snapshot_isolation", label: "SI", format: "bool" },
  { key: "auto_close", label: "Auto close", format: "bool" },
  { key: "auto_shrink", label: "Auto shrink", format: "bool" },
  { key: "auto_create_stats", label: "Auto create stats", format: "bool" },
  { key: "auto_update_stats", label: "Auto update stats", format: "bool" },
  { key: "query_store", label: "Query Store" },
  { key: "page_verify", label: "Page verify" },
  { key: "accelerated_database_recovery", label: "ADR", format: "bool" },
  { key: "optimized_locking", label: "Optimized locking", format: "bool" },
  { key: "log_reuse_wait", label: "Log reuse wait" },
];

const QS_HEALTH_COLUMNS = [
  { key: "database_name", label: "Database" },
  { key: "actual_state", label: "Actual" },
  { key: "desired_state", label: "Desired" },
  { key: "state_matches_desired", label: "Match", format: "bool" },
  { key: "readonly_reason_decoded", label: "Read-only reason", wrap: true },
  { key: "current_storage_size_mb", label: "Used", format: "mb" },
  { key: "max_storage_size_mb", label: "Cap", format: "mb" },
  { key: "pct_of_cap", label: "% of cap", format: "num1" },
  { key: "size_based_cleanup_mode", label: "Cleanup" },
  { key: "stale_query_threshold_days", label: "Stale (days)", format: "int" },
];

const TRACE_FLAG_COLUMNS = [
  { key: "trace_flag", label: "Flag", format: "int" },
  { key: "enabled", label: "Enabled", format: "bool" },
  { key: "is_global", label: "Global", format: "bool" },
  { key: "is_session", label: "Session", format: "bool" },
];

const AUDIT_COLUMNS = [
  { key: "setting", label: "Setting" },
  { key: "status", label: "Status", statusSev: true },
  { key: "current_value", label: "Current" },
  { key: "suggested_value", label: "Suggested" },
  { key: "recommendation", label: "Why", wrap: true },
];

const SERVER_CHANGE_COLUMNS = [
  { key: "change_time", label: "Changed", format: "time" },
  { key: "configuration_name", label: "Setting" },
  { key: "old_value_configured", label: "Old (configured)" },
  { key: "new_value_configured", label: "New (configured)" },
  { key: "old_value_in_use", label: "Old (in use)" },
  { key: "new_value_in_use", label: "New (in use)" },
];

const DB_CHANGE_COLUMNS = [
  { key: "change_time", label: "Changed", format: "time" },
  { key: "database_name", label: "Database" },
  { key: "setting_name", label: "Setting" },
  { key: "old_value", label: "Old" },
  { key: "new_value", label: "New" },
];

const TRACE_FLAG_CHANGE_COLUMNS = [
  { key: "change_time", label: "Changed", format: "time" },
  { key: "trace_flag", label: "Flag", format: "int" },
  { key: "change_type", label: "Change" },
  { key: "previous_status", label: "Previous" },
  { key: "new_status", label: "New" },
  { key: "scope", label: "Scope" },
];

const APPLICATION_COLUMNS = [
  { key: "program_name", label: "Application" },
  { key: "connections", label: "Connections", format: "int" },
  { key: "running", label: "Running", format: "int" },
  { key: "sleeping", label: "Sleeping", format: "int" },
  { key: "dormant", label: "Dormant", format: "int" },
  { key: "total_cpu_time_ms", label: "CPU", format: "ms" },
];

const JOB_COLUMNS = [
  { key: "job_name", label: "Job" },
  { key: "job_enabled", label: "Enabled", format: "bool" },
  { key: "start_time", label: "Started", format: "time" },
  { key: "current_duration_formatted", label: "Running for" },
  { key: "avg_duration_formatted", label: "Average" },
  { key: "p95_duration_formatted", label: "p95" },
  { key: "percent_of_average", label: "% of avg", format: "num1" },
  { key: "is_running_long", label: "Long", format: "bool" },
  { key: "successful_run_count", label: "Successes", format: "int" },
];

const PERFMON_COLUMNS = [
  { key: "counter_name", label: "Counter" },
  { key: "instance_name", label: "Instance" },
  { key: "value", label: "Value", format: "num2" },
  { key: "delta_value", label: "Delta", format: "num2" },
];

const INDEX_COLUMNS = [
  { key: "database_name", label: "Database" },
  { key: "schema_name", label: "Schema" },
  { key: "table_name", label: "Table" },
  { key: "index_name", label: "Index" },
  { key: "index_type", label: "Type" },
  { key: "classification", label: "Classification" },
  { key: "reserved_mb", label: "Reserved", format: "mb" },
  { key: "total_rows", label: "Rows", format: "int" },
  { key: "total_reads", label: "Reads", format: "int" },
  { key: "user_updates", label: "Updates", format: "int" },
  { key: "last_user_access", label: "Last access", format: "time" },
];

const FINDING_COLUMNS = [
  { key: "last_seen", label: "Last seen", format: "time" },
  { key: "category", label: "Category" },
  { key: "story_path", label: "Story", wrap: true },
  { key: "severity", label: "Severity", format: "num2" },
  { key: "confidence", label: "Confidence", format: "num2" },
  { key: "occurrences", label: "Occurrences", format: "int" },
  { key: "first_seen", label: "First seen", format: "time" },
];

const HEALTH_ENTRY_COLUMNS = [
  { key: "event_time", label: "Time", format: "time" },
  { key: "sql_cpu_utilization", label: "SQL CPU %", format: "int" },
  { key: "system_cpu_utilization", label: "System CPU %", format: "int" },
  { key: "non_yielding_tasks_reported", label: "Non-yielding", format: "int" },
  { key: "latch_warnings", label: "Latch warnings", format: "int" },
  { key: "spinlock_backoffs", label: "Spinlock backoffs", format: "int" },
  { key: "sick_spinlock_type", label: "Sick spinlock" },
  { key: "bad_pages_detected", label: "Bad pages", format: "int" },
  { key: "bad_pages_fixed", label: "Bad pages fixed", format: "int" },
  { key: "is_access_violation_occurred", label: "AV", format: "int" },
  { key: "total_dump_requests", label: "Dumps", format: "int" },
  { key: "page_faults", label: "Page faults", format: "int" },
];

const SEVERE_ERROR_COLUMNS = [
  { key: "event_time", label: "Time", format: "time" },
  { key: "error_number", label: "Error", format: "int" },
  { key: "severity", label: "Severity", format: "int" },
  { key: "state", label: "State", format: "int" },
  { key: "database_name", label: "Database" },
  { key: "message", label: "Message", wrap: true },
];

const SCHEDULER_ISSUE_COLUMNS = [
  { key: "event_time", label: "Time", format: "time" },
  { key: "scheduler_id", label: "Scheduler", format: "int" },
  { key: "cpu_id", label: "CPU", format: "int" },
  { key: "status", label: "Status" },
  { key: "is_online", label: "Online", format: "bool" },
  { key: "is_runnable", label: "Runnable", format: "bool" },
  { key: "non_yielding_time_ms", label: "Non-yielding", format: "ms" },
  { key: "thread_quantum_ms", label: "Quantum", format: "ms" },
];

const IO_ISSUE_COLUMNS = [
  { key: "event_time", label: "Time", format: "time" },
  { key: "state", label: "State" },
  { key: "io_latch_timeouts", label: "Latch timeouts", format: "int" },
  { key: "interval_long_ios", label: "Long I/Os (interval)", format: "int" },
  { key: "total_long_ios", label: "Long I/Os (total)", format: "int" },
  { key: "longest_pending_requests_duration_ms", label: "Longest pending", format: "ms" },
  { key: "longest_pending_requests_file_path", label: "File", wrap: true },
];

const CPU_TASK_COLUMNS = [
  { key: "event_time", label: "Time", format: "time" },
  { key: "state", label: "State" },
  { key: "max_workers", label: "Max workers", format: "int" },
  { key: "workers_created", label: "Created", format: "int" },
  { key: "workers_idle", label: "Idle", format: "int" },
  { key: "pending_tasks", label: "Pending", format: "int" },
  { key: "oldest_pending_task_waiting_time", label: "Oldest pending", format: "int" },
  { key: "tasks_completed_within_interval", label: "Completed", format: "int" },
  { key: "has_deadlocked_schedulers_occurred", label: "Deadlocked scheds", format: "bool" },
  { key: "did_blocking_occur", label: "Blocking", format: "bool" },
];

const MEMORY_CONDITION_COLUMNS = [
  { key: "event_time", label: "Time", format: "time" },
  { key: "last_notification", label: "Notification" },
  { key: "out_of_memory_exceptions", label: "OOM exceptions", format: "int" },
  { key: "available_physical_memory_gb", label: "Available", format: "num1" },
  { key: "working_set_gb", label: "Working set", format: "num1" },
  { key: "vm_committed_gb", label: "VM committed", format: "num1" },
  { key: "target_committed_gb", label: "Target committed", format: "num1" },
  { key: "current_committed_gb", label: "Current committed", format: "num1" },
  { key: "system_physical_memory_low", label: "System low", format: "int" },
  { key: "process_physical_memory_low", label: "Process low", format: "int" },
  { key: "last_oom_factor", label: "Last OOM factor" },
];

const MEMORY_BROKER_COLUMNS = [
  { key: "event_time", label: "Time", format: "time" },
  { key: "broker", label: "Broker" },
  { key: "notification", label: "Notification" },
  { key: "memory_ratio", label: "Ratio", format: "num2" },
  { key: "new_target", label: "New target", format: "int" },
  { key: "currently_allocated", label: "Allocated", format: "int" },
  { key: "previously_allocated", label: "Previously", format: "int" },
  { key: "currently_predicated", label: "Predicated", format: "int" },
  { key: "rate", label: "Rate", format: "num2" },
];

const MEMORY_OOM_COLUMNS = [
  { key: "event_time", label: "Time", format: "time" },
  { key: "memory_node_id", label: "Node", format: "int" },
  { key: "memory_utilization_pct", label: "Utilization", format: "pct" },
  { key: "failure_type", label: "Failure" },
  { key: "failure_value", label: "Value", format: "int" },
  { key: "available_physical_memory_kb", label: "Available", format: "int" },
  { key: "committed_kb", label: "Committed", format: "int" },
  { key: "target_kb", label: "Target", format: "int" },
  { key: "resources", label: "Resources", wrap: true },
  { key: "last_error", label: "Last error" },
];

const DEFAULT_TRACE_COLUMNS = [
  { key: "event_time", label: "Time", format: "time" },
  { key: "category", label: "Category" },
  { key: "event_name", label: "Event" },
  { key: "database_name", label: "Database" },
  { key: "object_name", label: "Object" },
  { key: "login_name", label: "Login" },
  { key: "application_name", label: "Application" },
  { key: "duration_ms", label: "Duration", format: "ms" },
  { key: "growth_mb", label: "Growth", format: "mb" },
  { key: "error_number", label: "Error", format: "int" },
  { key: "text_data", label: "Detail", wrap: true },
];

const COLLECTOR_COLUMNS = [
  { key: "collector", label: "Collector" },
  { key: "status", label: "Status", statusSev: true },
  { key: "total_runs", label: "Runs", format: "int" },
  { key: "errors", label: "Errors", format: "int" },
  { key: "yields", label: "Yields", format: "int" },
  { key: "failure_rate_pct", label: "Failure %", format: "num1" },
  { key: "avg_duration_ms", label: "Avg Dur", format: "ms" },
  { key: "p95_duration_ms", label: "p95 Dur", format: "ms" },
  { key: "last_success", label: "Last Success", format: "time" },
  { key: "last_error", label: "Last Error", wrap: true },
  /* #1837: what a NON-failing run reported (an enumeration that came back with 0 items). Blank for a
     plainly healthy collector; the same column the two WPF grids carry, so the web view is not the one
     Collection Health surface that still hides it. note_summary, not the raw last_note: it carries the
     "(all N runs)" qualifier that separates a persistently empty collector from an occasionally quiet
     one, composed server-side from the shared formatter so this table cannot render it a third way. */
  { key: "note_summary", label: "Note", wrap: true },
];

const HEAVIEST_COLUMNS = [
  { key: "collector", label: "Collector" },
  { key: "avg_duration_ms", label: "Avg", format: "ms" },
  { key: "p95_duration_ms", label: "p95", format: "ms" },
  { key: "max_duration_ms", label: "Max", format: "ms" },
  { key: "frequency_minutes", label: "Every (min)", format: "num1" },
  { key: "amortized_ms_per_minute", label: "ms/min", format: "num1" },
  { key: "pct_of_sweep_budget_per_run", label: "% of sweep", format: "num1" },
];
