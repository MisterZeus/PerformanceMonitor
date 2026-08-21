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

/* #2437 (the web twin of #2424): the needs-attention filter over the card grid — the destination the
   "+N more need attention" line finally has. Deliberately NOT persisted the way the sort and the grouped
   view are: a sort is a preference, this is a triage action tied to a moment, and a fleet page that opens
   with 52 of 57 servers already hidden is a support ticket even with the toggle in plain sight.
   attentionToggle is the header checkbox, held so the "+N more" line can turn it ON rather than filtering
   behind its back — a grid that silently shrank is the worse version of this defect. */
let attentionOnly = false;
let attentionToggle = null;

/* Grouped (tree) view — the web twin of the desktop FleetView: opt-in, and both the toggle and the collapsed
   groups persist client-side (localStorage, guarded so a locked-down browser still renders flat). lastTags is
   the full tag forest /api/fleet returns, so an organisational parent tag with no directly-tagged servers still
   nests its children correctly, exactly as FleetView does. */
let fleetGrouped = readStored("darling.fleet.grouped") === "1";
let lastTags = [];
const collapsedGroups = new Set(readStoredJson("darling.fleet.collapsed", []));
const GROUP_INDENT = 16; // px per tree depth

function readStored(key) { try { return localStorage.getItem(key); } catch { return null; } }
function readStoredJson(key, dflt) { try { return JSON.parse(localStorage.getItem(key) || "null") ?? dflt; } catch { return dflt; } }
function writeStored(key, value) { try { localStorage.setItem(key, value); } catch { /* private mode / disabled — view stays session-only */ } }

/* The web twin of FleetView's projection: the tag forest depth-first (child tags before a tag's own servers),
   then an Untagged group last. Each entry is one group header with its DIRECTLY-assigned server cards; a server
   carrying multiple tags appears under each, and an untagged server appears only under Untagged. Cycle- and
   dangling-parent-safe (an orphaned tag surfaces as a root rather than vanishing). No Favorites group — the web
   fleet has no per-user favourites. */
function buildTagGroups(forest, cards, sortFn) {
  const known = new Set(forest.map((t) => t.id));
  const byParent = new Map();
  for (const t of forest) {
    const p = t.parent_id != null && known.has(t.parent_id) ? t.parent_id : 0; // dangling parent -> root
    if (!byParent.has(p)) byParent.set(p, []);
    byParent.get(p).push(t);
  }
  for (const list of byParent.values()) list.sort((a, b) => a.sort_order - b.sort_order || a.name.localeCompare(b.name));

  const serversByTag = new Map();
  for (const c of cards) {
    for (const t of c.tags || []) {
      if (!serversByTag.has(t.id)) serversByTag.set(t.id, []);
      serversByTag.get(t.id).push(c);
    }
  }

  const groups = [];
  const visited = new Set();
  function emit(tag, depth) {
    if (visited.has(tag.id)) return;
    visited.add(tag.id);
    const servers = (serversByTag.get(tag.id) || []).slice().sort(sortFn);
    const kids = byParent.get(tag.id) || [];
    groups.push({ key: "tag:" + tag.id, name: tag.name, depth, cards: servers, hasChildren: kids.length > 0 || servers.length > 0 });
    for (const kid of kids) emit(kid, depth + 1);
  }
  for (const root of byParent.get(0) || []) emit(root, 0);
  for (const t of forest) if (!visited.has(t.id)) emit(t, 0); // cycle / disconnected -> surface as a root

  const untagged = cards.filter((c) => !(c.tags || []).length).slice().sort(sortFn);
  if (untagged.length) groups.push({ key: "untagged", name: "Untagged", depth: 0, cards: untagged, hasChildren: true });

  return groups;
}

/* Name/tag filter, matching the desktop apps' ServerOverviewFilter rule: an empty term matches everything,
   otherwise a case-insensitive substring of the display name, the instance name, or any of the server's tag
   names (#2020) — so `prod` finds both sql-prod-01 and everything tagged Production, as on the desktop. */
function cardMatches(c, q) {
  const needle = (q || "").trim().toLowerCase();
  if (!needle) return true;
  return (
    (c.display_name || "").toLowerCase().includes(needle) ||
    (c.server_name || "").toLowerCase().includes(needle) ||
    (c.tags || []).some((t) => (t.name || "").toLowerCase().includes(needle))
  );
}

/* The needs-attention predicate (#2437). It reads the card's PRE-BANDED `band` — the same field
   BuildRollup counted `additional_problem_count` from (`cards.Where(c => c.Band != FleetHealthBand.Healthy)`)
   and the same one the Warning / Critical / Offline roll-up tiles are summed from. That is the whole point: a
   client-side approximation of "needs attention" would eventually disagree with the number that sent the
   reader here, which is this defect wearing a new hat. R1 still holds — no threshold is re-derived here, a
   server-computed band is read. */
function cardNeedsAttention(c) {
  return c.band !== "Healthy";
}

/* What the filter did, shown beside the cards it left. Word-for-word the desktop viewer's
   FleetRollup.AttentionFilterCountText (#2424), because the point of doing all three surfaces at once is
   that a reader moving between them meets one vocabulary rather than three. The all-clear arms matter as
   much as the count: a filtered grid holding nothing is otherwise an empty page with no explanation, and an
   empty FLEET must not be told that zero of its servers are healthy. */
function attentionCountText(shown, total) {
  if (shown > 0) return "showing " + shown + " of " + total;
  if (total <= 0) return "no servers to filter";
  if (total === 1) return "the 1 server monitored is healthy";
  return "all " + total + " servers are healthy";
}

/* The server's tags as read-only coloured pills (#2020). Colour is the stored #RRGGBB or, when unset, a
   neutral pill (matching the desktop apps — no palette is resolved here). The hex is format-checked before it
   reaches the style attribute, so only a well-formed colour from the store is ever applied. Nothing renders
   for a server with no tags. */
function tagPills(c) {
  const tags = c.tags || [];
  if (!tags.length) return null;
  return el(
    "div",
    { class: "tag-pills" },
    tags.map((t) => {
      const safeColour = /^#[0-9a-fA-F]{6}$/.test(t.colour || "") ? t.colour : null;
      return el("span", {
        class: "tag-pill",
        text: t.name,
        title: t.name,
        style: safeColour ? "background:" + safeColour : null,
      });
    })
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
      /* #2437: the overflow line is the way TO the overflow, not a report that it exists. It was an inert
         muted div: on a 57-server fleet it read "+52 more need attention" and the reporter's question was
         literally "where do I find these warnings?". Activating it turns the needs-attention filter on, so
         the answer is the card grid below, with every metric chip those servers have. onActivate rather than
         onClick because a line that navigates has to be reachable from the keyboard too — it installs
         role=button, tabindex and Enter/Space alongside the click. */
      nodes.push(
        el("div", {
          class: "attention-link",
          text: "+ " + d.additional_problem_count + " more need attention",
          title: "Show only the servers that need attention",
          onActivate: () => setAttentionOnly(true),
        })
      );
    }
  }

  nodes.push(el("h3", { class: "section-title", style: "margin-top:1.25rem", text: "Servers" }));
  lastCards = d.cards || [];
  lastTags = d.tags || [];
  gridNode = el("div", { class: "fleet-cards" });
  redrawCards();
  nodes.push(gridNode);

  mount(main, nodes);
}

/** Filter the cached cards by the search term AND the needs-attention toggle, sort by the current choice, and
    (re)fill the grid — no refetch. */
function redrawCards() {
  if (!gridNode) return;
  const matched = lastCards
    .filter((c) => cardMatches(c, fleetFilter) && (!attentionOnly || cardNeedsAttention(c)))
    .sort(SORTS[fleetSort] || SORTS.severity);

  /* The active state rides with the CARDS, not only with the toggle that set it. The desktop viewer can put
     its count beside the toggle and stop there because its roll-up header is docked and never scrolls; this
     page head scrolls away, so a reader who has scrolled down to the grid would see a short fleet and nothing
     saying why. A filtered grid that looks unfiltered is a worse defect than the dead end it replaced, so the
     notice sits on the grid and carries its own way out. */
  const notice = attentionOnly ? attentionNotice(matched.length) : null;

  if (fleetGrouped && lastTags.length) {
    mount(gridNode, [notice, renderGrouped(matched)]);
    return;
  }

  mount(gridNode, [
    notice,
    matched.length
      ? el("div", { class: "grid" }, matched.map(serverCard))
      : el("div", { class: "muted", style: "padding:0.5rem", text: noMatchText() }),
  ]);
}

/** Why the grid is short, and how to make it whole again — rendered only while the filter is on.
    The colour follows the SENTENCE, not the filter: this line says either "N servers need attention" or an
    all-clear, and painting the all-clear amber would be a colour contradicting its own text — the same family
    of defect this whole change is about. (Raised in review on #2429 and ported.) */
function attentionNotice(shown) {
  return el("div", { class: "attention-note " + (shown > 0 ? "warn" : "ok") }, [
    el("span", { text: "Needs attention only — " + attentionCountText(shown, lastCards.length) + "." }),
    el("span", {
      class: "attention-link",
      text: "Show all servers",
      onActivate: () => setAttentionOnly(false),
    }),
  ]);
}

/** The empty-grid line has to name whichever filter emptied it. Reusing the search-term wording unguarded
    would tell a reader whose fleet is simply healthy that nothing matches a term they never typed. */
function noMatchText() {
  const term = fleetFilter.trim();
  if (term && attentionOnly) return "No servers needing attention match “" + term + "”.";
  if (term) return "No servers match “" + term + "”.";
  return "No servers need attention.";
}

/** Turns the needs-attention filter on or off from either end — the header toggle or the "+N more" line —
    keeping the checkbox and the grid in step. The checkbox is where the state lives: whichever affordance set
    it, the reader can see the filter is on and can turn it off in one place. */
function setAttentionOnly(on) {
  attentionOnly = on;
  if (attentionToggle) attentionToggle.checked = on;
  redrawCards();
}

/* Renders the grouped (tree) view: DFS group headers each followed by a grid of their cards. Collapsing a
   header hides its cards AND every descendant group via the hideBelow depth-gate, mirroring FleetView's
   collapse-reveal (a collapsed tag hides its whole subtree). */
function renderGrouped(matched) {
  const groups = buildTagGroups(lastTags, matched, SORTS[fleetSort] || SORTS.severity);
  if (!groups.length) {
    return [el("div", { class: "muted", style: "padding:0.5rem", text: fleetFilter.trim() ? "No servers match “" + fleetFilter.trim() + "”." : "No tagged servers yet." })];
  }

  const nodes = [];
  let hideBelow = Infinity;
  for (const g of groups) {
    if (g.depth > hideBelow) continue; // inside a collapsed ancestor's subtree
    hideBelow = Infinity;
    const collapsed = collapsedGroups.has(g.key);
    nodes.push(groupHeader(g, collapsed));
    if (collapsed) {
      hideBelow = g.depth;
      continue;
    }
    if (g.cards.length) {
      nodes.push(el("div", { class: "grid tag-group-grid", style: "margin-left:" + (g.depth + 1) * GROUP_INDENT + "px" }, g.cards.map(serverCard)));
    }
  }
  return nodes;
}

/** One collapsible tag-group header: chevron + name + (n) count, indented by tree depth. Click or Enter/Space
    toggles it; the collapsed set persists client-side so it survives the 60s refresh and a reload. */
function groupHeader(g, collapsed) {
  const chevron = g.hasChildren ? (collapsed ? "▸" : "▾") : "";
  const header = el(
    "div",
    {
      class: "tag-group-header",
      style: "margin-left:" + g.depth * GROUP_INDENT + "px",
      role: "button",
      tabindex: "0",
      "aria-expanded": collapsed ? "false" : "true",
    },
    [
      el("span", { class: "tag-group-chevron", text: chevron }),
      el("span", { class: "tag-group-name", text: g.name }),
      el("span", { class: "tag-group-count", text: g.cards.length ? "(" + g.cards.length + ")" : "" }),
    ]
  );
  const toggle = () => {
    if (collapsedGroups.has(g.key)) collapsedGroups.delete(g.key);
    else collapsedGroups.add(g.key);
    writeStored("darling.fleet.collapsed", JSON.stringify([...collapsedGroups]));
    redrawCards();
  };
  header.addEventListener("click", toggle);
  header.addEventListener("keydown", (e) => {
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      toggle();
    }
  });
  return header;
}

function pageHead(d) {
  return el("div", { class: "page-head" }, [
    el("h2", { text: "Fleet Overview" }),
    el("div", { class: "spacer" }),
    d && d.total_servers ? searchControl() : null,
    d && d.total_servers ? attentionControl() : null,
    d && d.tags && d.tags.length ? groupControl() : null,
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
    placeholder: "server name / tag",
    "aria-label": "Filter servers by name or tag",
  });
  input.value = fleetFilter;
  input.addEventListener("input", () => {
    fleetFilter = input.value;
    redrawCards();
  });
  return el("label", { class: "search-control" }, [el("span", { text: "Search" }), input]);
}

/** The needs-attention toggle (#2437) — a view control over the same cards, so it sits beside Search and Sort
    rather than being a mode the "+N more" link switches on invisibly. It re-reads the module-level
    attentionOnly on every re-render, exactly like the sort and search controls, so the 60s refresh cannot
    silently drop an active filter. */
function attentionControl() {
  const cb = el("input", { type: "checkbox", class: "attention-toggle", "aria-label": "Show only servers that need attention" });
  cb.checked = attentionOnly;
  cb.addEventListener("change", () => setAttentionOnly(cb.checked));
  attentionToggle = cb;
  return el("label", {
    class: "attention-control",
    title: "Show only the cards that are not Healthy — Critical, Warning or Offline. Uncheck to show the whole fleet again.",
  }, [cb, el("span", { text: "Needs attention only" })]);
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

/** Toggles the grouped (tag-tree) view (#2020) — shown only when tags exist. Groups the fleet cards under a
    nested, collapsible tag tree (then Untagged), the read-only web twin of the desktop sidebar. Persists. */
function groupControl() {
  const cb = el("input", { type: "checkbox", class: "group-toggle", "aria-label": "Group servers by tag" });
  cb.checked = fleetGrouped;
  cb.addEventListener("change", () => {
    fleetGrouped = cb.checked;
    writeStored("darling.fleet.grouped", fleetGrouped ? "1" : "0");
    redrawCards();
  });
  return el("label", { class: "group-control" }, [cb, el("span", { text: "Group by tag" })]);
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
      tagPills(c),
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
