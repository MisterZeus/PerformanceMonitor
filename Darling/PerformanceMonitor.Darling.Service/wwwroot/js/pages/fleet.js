/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

/*
 * Fleet Overview page (#1562) — the NOC roll-up from GET /api/fleet. The API is PRE-BANDED: every band, status,
 * and the worst-first ranking are computed server-side by ServerHealthClassifier. This page ONLY renders them;
 * it never re-derives a threshold (R1). The amber "Awaiting first collection" status is rendered exactly as the
 * API reports it (band = Warning, status text verbatim) — never the red offline treatment.
 */

import { el, mount, apiGet, loadingStrip, errorStrip, emptyStrip, localTime, localClock, relTime, fmtInt, fmtPct, fmtMb, fmtMs, bandClass } from "../util.js";
import { VIZ, navigateServer } from "../panels.js";

const BAND_RANK = { Offline: 0, Critical: 1, Warning: 2, Healthy: 3 };

/** Client-side card orderings (M8). Severity is the default; CPU sorts busiest-first (grids default DESC). */
const SORTS = {
  severity: (a, b) => (BAND_RANK[a.band] ?? 9) - (BAND_RANK[b.band] ?? 9) || a.display_name.localeCompare(b.display_name),
  name: (a, b) => a.display_name.localeCompare(b.display_name),
  cpu: (a, b) =>
    (b.total_cpu_percent ?? b.cpu_percent ?? -1) - (a.total_cpu_percent ?? a.cpu_percent ?? -1) ||
    a.display_name.localeCompare(b.display_name),
};

/* The sort choice AND the search term persist across the 60s refresh (a full re-render): the header controls
   re-read them, and the grid re-filters + re-sorts in place without a refetch when either changes. */
let fleetSort = "severity";
let fleetFilter = "";
let lastCards = [];
let gridNode = null;

/* Name filter, matching the desktop apps' ServerOverviewFilter rule: an empty term matches everything,
   otherwise a case-insensitive substring of the display name or the instance name. */
function cardMatches(c, q) {
  const needle = (q || "").trim().toLowerCase();
  if (!needle) return true;
  return (
    (c.display_name || "").toLowerCase().includes(needle) ||
    (c.server_name || "").toLowerCase().includes(needle)
  );
}

export async function renderFleet(main) {
  mount(main, [pageHead(null), loadingStrip("Loading fleet…")]);

  const res = await apiGet("/api/fleet");
  if (res.kind === "error") {
    mount(main, [pageHead(null), errorStrip(res.message)]);
    return;
  }

  const d = res.data;
  const nodes = [pageHead(d), rollup(d)];

  if (!d.total_servers) {
    nodes.push(
      emptyStrip("No servers are enabled yet. Add servers to darling.json and cards appear here as collection begins.")
    );
    mount(main, nodes);
    return;
  }

  const problems = d.critical_count + d.warning_count + d.offline_count;
  if (problems === 0) {
    nodes.push(
      el("div", { class: "all-healthy" }, [
        el("span", { class: "dot band-Healthy" }),
        "All " + d.total_servers + " server" + (d.total_servers === 1 ? "" : "s") + " healthy.",
      ])
    );
  } else {
    nodes.push(el("h3", { class: "section-title", text: "Needs attention" }));
    nodes.push(
      VIZ.bandlist(d, {
        rowsKey: "worst_servers",
        primaryKey: "display_name",
        bandKey: "band",
        bandLabelKey: "band_label",
        reasonKey: "reason",
        navKey: "display_name",
      })
    );
    if (d.additional_problem_count > 0) {
      nodes.push(el("div", { class: "muted", style: "margin:0.4rem 0 0.2rem", text: "+ " + d.additional_problem_count + " more need attention" }));
    }
  }

  nodes.push(el("h3", { class: "section-title", style: "margin-top:1.25rem", text: "Servers" }));
  lastCards = d.cards || [];
  gridNode = el("div", { class: "grid" });
  redrawCards();
  nodes.push(gridNode);

  mount(main, nodes);
}

/** Filter the cached cards by the search term, sort by the current choice, and (re)fill the grid — no refetch. */
function redrawCards() {
  if (!gridNode) return;
  const matched = lastCards.filter((c) => cardMatches(c, fleetFilter)).sort(SORTS[fleetSort] || SORTS.severity);
  mount(
    gridNode,
    matched.length
      ? matched.map(serverCard)
      : [el("div", { class: "muted", style: "padding:0.5rem", text: "No servers match “" + fleetFilter.trim() + "”." })]
  );
}

function pageHead(d) {
  return el("div", { class: "page-head" }, [
    el("h2", { text: "Fleet Overview" }),
    el("div", { class: "spacer" }),
    d && d.total_servers ? searchControl() : null,
    d && d.total_servers ? sortControl() : null,
    d ? el("div", { class: "meta", text: "Updated " + localTime(d.generated_at) }) : null,
  ]);
}

/** Client-side name filter on the fleet header: narrows the cards live as you type. The term persists across
    the 60s re-render because the input re-reads the module-level fleetFilter, exactly like the sort control. */
function searchControl() {
  const input = el("input", {
    class: "search-input",
    type: "search",
    placeholder: "server name",
    "aria-label": "Filter servers by name",
  });
  input.value = fleetFilter;
  input.addEventListener("input", () => {
    fleetFilter = input.value;
    redrawCards();
  });
  return el("label", { class: "search-control" }, [el("span", { text: "Search" }), input]);
}

/** Client-side card-sort control on the fleet header (M8): severity (default) / name / CPU. */
function sortControl() {
  const sel = el("select", { class: "sort-select", "aria-label": "Sort servers" }, [
    el("option", { value: "severity", text: "Severity" }),
    el("option", { value: "name", text: "Name" }),
    el("option", { value: "cpu", text: "CPU" }),
  ]);
  sel.value = fleetSort;
  sel.addEventListener("change", () => {
    fleetSort = sel.value;
    redrawCards();
  });
  return el("label", { class: "sort-control" }, [el("span", { text: "Sort" }), sel]);
}

function rollup(d) {
  const tile = (num, lbl, cls) =>
    el("div", { class: "tile " + (cls || "") }, [
      el("div", { class: "num", text: fmtInt(num) }),
      el("div", { class: "lbl", text: lbl }),
    ]);
  /* Two fixed groups (server-band counts | event counts) split by a divider; a non-zero blocking / deadlock
     total takes a severity color. */
  return el("div", { class: "rollup" }, [
    el("div", { class: "rollup-group" }, [
      tile(d.total_servers, "Servers"),
      tile(d.healthy_count, "Healthy", "healthy"),
      tile(d.warning_count, "Warning", "warning"),
      tile(d.critical_count, "Critical", "critical"),
      tile(d.offline_count, "Offline", "offline"),
    ]),
    el("div", { class: "rollup-divider" }),
    el("div", { class: "rollup-group" }, [
      tile(d.total_blocking_events, "Blocking (recent)", d.total_blocking_events > 0 ? "warning" : ""),
      tile(d.total_deadlocks, "Deadlocks (recent)", d.total_deadlocks > 0 ? "critical" : ""),
    ]),
  ]);
}

function serverCard(c) {
  const cls = bandClass(c.band);
  const statusLine = c.awaiting_first_collection
    ? el("div", { class: "status-line awaiting", text: c.status })
    : el("div", { class: "status-line", text: c.status + " · last collect " + localClock(c.last_collection) });

  return el(
    "div",
    { class: "server-card " + cls, onActivate: () => navigateServer(c.server_name || c.display_name) },
    [
      el("div", { class: "head" }, [
        el("span", { class: "dot " + cls }),
        /* #2031: a muted-bell right of the dot when a whole-server alert silence is active — display-only
           (the web seat has no silence action), so a silenced server stops looking healthy-quiet. */
        c.is_silenced ? el("span", { class: "silenced-bell", title: "Alerts silenced for this server", role: "img", "aria-label": "Alerts silenced" }) : null,
        el("span", { class: "title", text: c.display_name }),
      ]),
      statusLine,
      metricBands(c),
    ]
  );
}

/* Enriched metric chips (M1): each carries a secondary detail line from fields /api/fleet already returns —
   the SQL-vs-total CPU split, threads available/max, memory + buffer-pool GB, blocking max wait, deadlocks
   last-seen, and collectors healthy/failing. */
function metricBands(c) {
  const threadsValue =
    c.threads_severity === "Unknown"
      ? "n/a"
      : c.requests_waiting_for_threads > 0
      ? fmtInt(c.requests_waiting_for_threads) + " starved"
      : c.available_threads != null
      ? fmtInt(c.available_threads) + " free"
      : "ok";
  const threadsDetail =
    c.total_threads != null
      ? fmtInt(c.available_threads ?? c.total_threads - (c.current_workers ?? 0)) + " / " + fmtInt(c.total_threads) + " threads"
      : null;

  const cpuValue =
    c.total_cpu_percent != null || c.cpu_percent != null ? fmtPct(c.total_cpu_percent ?? c.cpu_percent) : "n/a";
  const cpuDetail =
    c.cpu_percent != null
      ? "SQL " + fmtPct(c.cpu_percent) + (c.other_process_cpu_percent != null ? " · other " + fmtPct(c.other_process_cpu_percent) : "")
      : null;

  const memValue = c.has_memory_pressure ? fmtInt(c.memory_waiter_count) + " waiters" : "ok";
  const memDetail =
    c.memory_mb != null
      ? fmtMb(c.memory_mb) + (c.buffer_pool_mb != null ? " · BP " + fmtMb(c.buffer_pool_mb) : "")
      : null;

  const blockingDetail = c.blocking_count > 0 && c.max_blocking_wait_ms > 0 ? "max wait " + fmtMs(c.max_blocking_wait_ms) : null;
  const deadlockDetail = c.deadlock_count > 0 && c.deadlock_last_seen ? "last " + relTime(c.deadlock_last_seen) : null;

  const collectorsValue = c.failed_collector_count > 0 ? fmtInt(c.failed_collector_count) + " failing" : "OK";
  const collectorsDetail = fmtInt(c.healthy_collector_count) + " healthy · " + fmtInt(c.failed_collector_count) + " failing";

  return el("div", { class: "metric-bands" }, [
    chip("CPU", cpuValue, c.cpu_severity, cpuDetail),
    chip("Threads", threadsValue, c.threads_severity, threadsDetail),
    chip("Memory", memValue, c.memory_severity, memDetail),
    chip("Blocking", fmtInt(c.blocking_count), c.blocking_severity, blockingDetail),
    chip("Deadlocks", fmtInt(c.deadlock_count), c.deadlock_severity, deadlockDetail),
    chip("Collectors", collectorsValue, c.collector_severity, collectorsDetail),
  ]);
}

/* A short non-color severity cue so severity isn't conveyed by border color alone (M2). */
const SEV_BADGE = { Critical: "CRIT", Warning: "WARN" };

function chip(label, value, sev, detail) {
  const badge = SEV_BADGE[sev];
  return el("div", { class: "metric-chip sev-" + (sev || "Unknown") }, [
    el("div", { class: "label" }, [
      el("span", { text: label }),
      badge ? el("span", { class: "sev-badge", text: badge }) : null,
    ]),
    el("div", { class: "value", text: value }),
    detail ? el("div", { class: "detail", text: detail }) : null,
  ]);
}
