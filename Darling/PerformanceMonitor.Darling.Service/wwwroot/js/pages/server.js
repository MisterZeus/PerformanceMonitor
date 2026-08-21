/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

/*
 * Server detail page (#1562, deepened for the web/desktop parity pass) — the per-server drill-down reached by
 * clicking a fleet card. This module is the SHELL: the header, the sub-tab bar, the time-range control, and the
 * panel grid. Every panel lives in pages/server-tabs.js as a descriptor run through the unmodified renderPanel
 * (the #1563 seam), which is why adding a tab is data, not plumbing.
 *
 * The sub-tabs are the web port of the desktop viewer's per-server TabControl (ViewerServerTab.xaml). The tab id
 * rides in the hash — #/server/{name}/{tab} — so a tab is DEEP-LINKABLE and survives the 60s refresh, which
 * re-renders the whole route. An unknown or absent tab id resolves to Overview rather than erroring, so every
 * pre-existing #/server/{name} link keeps working unchanged.
 *
 * The time range is the web twin of ViewerServerTab.TimeRange.cs's preset picker: one page-level window that
 * every time-windowed panel is given. It is module state (like the fleet page's sort), so it survives the
 * refresh — but it is NOT persisted to localStorage, because a page that reopens on a 30-day window is slow for
 * a reason the reader cannot see. Panels whose read takes no window at all say "latest snapshot" in their own
 * subtitle rather than inheriting a label that would misdescribe them.
 */

import { el, mount, apiGet, bandClass } from "../util.js";
import { SERVER_TABS, findServerTab, tabNote } from "./server-tabs.js";
import { metricBands } from "./fleet.js";

/** The page time range. Mirrors the desktop viewer's presets, plus the two longer windows the view chrome uses. */
const RANGE_OPTIONS = [
  { hours: 1, label: "last hour" },
  { hours: 4, label: "last 4 hours" },
  { hours: 12, label: "last 12 hours" },
  { hours: 24, label: "last 24 hours" },
  { hours: 24 * 7, label: "last 7 days" },
  { hours: 24 * 30, label: "last 30 days" },
];

/* Module state, deliberately not persisted — see the header comment. `gridNode` + the current server/tab let the
   range control redraw only the panels, so changing the window does not flash the header or refetch /api/fleet. */
let pageHours = 24;
let gridNode = null;
let current = { server: null, tab: null };

/** The {hours,label} context every tab build() is given. */
function rangeContext() {
  const opt = RANGE_OPTIONS.find((o) => o.hours === pageHours) || RANGE_OPTIONS[3];
  return { hours: opt.hours, label: opt.label };
}

export function renderServer(main, server, tabId) {
  const tab = findServerTab(tabId);
  current = { server, tab };

  const dot = el("span", { class: "dot" });
  const badgeSlot = el("span", { class: "server-band" });
  const whySlot = el("div", { class: "server-why" });
  const head = el("div", { class: "page-head" }, [
    el("a", { href: "#/fleet", text: "← Fleet" }),
    el("span", { class: "server-title" }, [dot, el("h2", { text: server })]),
    badgeSlot,
    el("div", { class: "spacer" }),
    rangeControl(),
  ]);
  enrichServerHead(dot, badgeSlot, whySlot, server);

  gridNode = el("div", { class: "panel-grid" });
  redrawPanels();

  mount(main, [head, whySlot, subtabBar(server, tab), tabNote(tab), gridNode]);
}

/** (Re)fill the panel grid for the current server + tab at the current range. No refetch of anything else. */
function redrawPanels() {
  if (!gridNode || !current.tab || !current.server) return;
  mount(gridNode, current.tab.build(current.server, rangeContext()));
}

/* The sub-tab bar — the web port of ViewerServerTab.xaml's TabControl. Real <a href> links, not click handlers,
   so a tab can be middle-clicked, bookmarked and shared; the hash router does the rest. */
function subtabBar(server, active) {
  return el(
    "nav",
    { class: "subtabs", role: "tablist", "aria-label": "Server sections" },
    SERVER_TABS.map((t) =>
      el("a", {
        class: "subtab" + (t.id === active.id ? " active" : ""),
        href: "#/server/" + encodeURIComponent(server) + "/" + t.id,
        role: "tab",
        "aria-selected": t.id === active.id ? "true" : "false",
        text: t.label,
      })
    )
  );
}

/** The time-range preset picker. Changing it redraws the panels in place, exactly like the fleet page's sort. */
function rangeControl() {
  const sel = el(
    "select",
    { class: "range-select-inline", "aria-label": "Time range" },
    RANGE_OPTIONS.map((o) => el("option", { value: String(o.hours), text: o.label }))
  );
  sel.value = String(pageHours);
  sel.addEventListener("change", () => {
    pageHours = Number(sel.value) || 24;
    redrawPanels();
  });
  return el("label", { class: "range-control" }, [el("span", { text: "Range" }), sel]);
}

/* The server header's status dot, band badge and the WHY beneath them, all from this server's pre-banded fleet
 * card (one /api/fleet read). The header renders immediately and this fills them in when the card arrives.
 *
 * The band word alone is not an answer. `Warning` has three unrelated causes — a genuine metric breach, a server
 * awaiting its first collection, and a collector error — so a badge reading "Warning" with no way to ask why is
 * the #2422 report rebuilt on a new surface, which is exactly what #2429 fixed on the desktop by attaching the
 * card's own metric rows. Two things are shown here and NEITHER is derived in the browser (R1): the per-metric
 * severity chips, rendered by fleet.js's own metricBands so there is one implementation rather than two, and —
 * when this server is in the fleet's worst-first ranking — the reason string the server already computed for it,
 * the same sentence the fleet page shows. A server outside that ranking gets the chips and no sentence, because
 * inventing one here is the second derivation this comment exists to refuse. */
function enrichServerHead(dot, badgeSlot, whySlot, server) {
  (async () => {
    const res = await apiGet("/api/fleet");
    if (res.kind !== "data") return;
    const matches = (c) => c.server_name === server || c.display_name === server;
    const card = (res.data.cards || []).find(matches);
    if (!card) return;

    dot.className = "dot " + bandClass(card.band);
    const ranked = (res.data.worst_servers || []).find((w) => w.display_name === server);
    const reason = ranked && ranked.reason ? ranked.reason : null;
    mount(
      badgeSlot,
      el("span", { class: "badge " + bandClass(card.band), text: card.status || card.band, title: reason || null })
    );

    mount(whySlot, [
      reason ? el("div", { class: "server-reason " + bandClass(card.band), text: reason }) : null,
      metricBands(card),
    ]);
  })();
}
