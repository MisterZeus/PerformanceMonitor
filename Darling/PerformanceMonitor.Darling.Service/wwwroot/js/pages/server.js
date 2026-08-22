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
 * WHICH tab set is a question about the SERVER, not about the shell (#2530): a PostgreSQL target gets the six
 * PostgreSQL tabs and a SQL Server target the twelve SQL Server ones, chosen by serverTabsFor() from the fleet
 * card's server-derived `is_postgres`. That fact arrives with the card, so the bar and the grid wait for the
 * ONE /api/fleet read this page already made for the header's band and reason — not a second request, and not
 * a guess rendered first and corrected after. Rendering the SQL Server set optimistically would flash twelve
 * wrong tabs at every PostgreSQL target and start nine reads that cannot answer, which is the exact experience
 * #2530 was filed about; a loading strip for one fetch is the cheaper wrong answer. If the card never arrives
 * the page falls back to the SQL Server registry, which is what an unclaimed server has always rendered.
 *
 * The time range is the web twin of ViewerServerTab.TimeRange.cs's preset picker: one page-level window that
 * every time-windowed panel is given. It is module state (like the fleet page's sort), so it survives the
 * refresh — but it is NOT persisted to localStorage, because a page that reopens on a 30-day window is slow for
 * a reason the reader cannot see. Panels whose read takes no window at all say "latest snapshot" in their own
 * subtitle rather than inheriting a label that would misdescribe them.
 */

import { el, mount, apiGet, bandClass, loadingStrip } from "../util.js";
import { serverTabsFor, findServerTab, tabNote } from "./server-tabs.js";
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

/* The render generation, and why it appeared with the engine branch rather than with the page.
 *
 * `route()` re-renders this page on every hash change — every sub-tab click — and again on every 60s poll, so
 * two renderServer() calls are routinely in flight at once, and /api/fleet does not promise to answer them in
 * order. Before the tab set depended on the card, the only async work here touched header nodes captured in
 * its own closure, which a newer render had already detached from the document: a late response wrote to
 * garbage and nobody saw it. Now the callback writes MODULE state — `current`, and the grid redrawPanels()
 * reads — so the last response to land wins regardless of which render is on screen. Two servers in flight
 * paints one server's panels under the other's header and URL.
 *
 * A generation counter rather than an AbortController because the losing render must not cancel the shared
 * /api/fleet fetch out from under the winning one; the fetch is fine, it is only its RESULT that is stale. */
let renderGeneration = 0;

/** The {hours,label} context every tab build() is given. */
function rangeContext() {
  const opt = RANGE_OPTIONS.find((o) => o.hours === pageHours) || RANGE_OPTIONS[3];
  return { hours: opt.hours, label: opt.label };
}

export function renderServer(main, server, tabId) {
  const generation = ++renderGeneration;
  current = { server, tab: null };

  const dot = el("span", { class: "dot" });
  const badgeSlot = el("span", { class: "server-band" });
  const engineSlot = el("span", { class: "server-engine" });
  const whySlot = el("div", { class: "server-why" });
  const head = el("div", { class: "page-head" }, [
    el("a", { href: "#/fleet", text: "← Fleet" }),
    el("span", { class: "server-title" }, [dot, el("h2", { text: server })]),
    badgeSlot,
    engineSlot,
    el("div", { class: "spacer" }),
    rangeControl(),
  ]);

  /* The bar and the note share one slot because both are decided by the same card. */
  const tabsSlot = el("div", { class: "subtabs-slot" }, [loadingStrip()]);
  gridNode = el("div", { class: "panel-grid" });
  mount(main, [head, whySlot, tabsSlot, gridNode]);

  loadServerCard(server, (card, reason) => {
    /* A newer render has started since this fetch went out — everything below writes module state or mounts
       into nodes this render no longer owns, so the only correct thing to do with a stale answer is drop it. */
    if (generation !== renderGeneration) return;

    fillServerHead(dot, badgeSlot, engineSlot, whySlot, card, reason);

    const tabs = serverTabsFor(card);
    const tab = findServerTab(tabId, tabs);
    current = { server, tab };
    mount(tabsSlot, [subtabBar(server, tab, tabs), tabNote(tab)]);
    redrawPanels();
  });
}

/** (Re)fill the panel grid for the current server + tab at the current range. No refetch of anything else. */
function redrawPanels() {
  if (!gridNode || !current.tab || !current.server) return;
  mount(gridNode, current.tab.build(current.server, rangeContext()));
}

/* The sub-tab bar — the web port of ViewerServerTab.xaml's TabControl. Real <a href> links, not click handlers,
   so a tab can be middle-clicked, bookmarked and shared; the hash router does the rest. */
function subtabBar(server, active, tabs) {
  return el(
    "nav",
    { class: "subtabs", role: "tablist", "aria-label": "Server sections" },
    tabs.map((t) =>
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

/* This server's fleet card, plus the reason sentence the fleet's worst-first ranking computed for it. ONE
 * /api/fleet read serves both jobs the page has for it — the header's band and reason, and the engine the tab
 * set is chosen by — because two callers of the same endpoint on one page render is a second request nobody
 * asked for.
 *
 * `onCard` is ALWAYS called, card or not. A fleet read that failed, and a server name the fleet does not carry,
 * still have to get tabs; a null card is "no engine claim", which serverTabsFor answers with the SQL Server
 * registry, exactly as this page behaved before it could ask. */
function loadServerCard(server, onCard) {
  (async () => {
    const res = await apiGet("/api/fleet");
    if (res.kind !== "data") return onCard(null, null);

    const matches = (c) => c.server_name === server || c.display_name === server;
    const card = (res.data.cards || []).find(matches) || null;
    /* The reason belongs to the RANKING, not to the card, so it travels as its own value rather than being
       stapled onto the card object — and a server outside the ranking has none, which stays a null here
       rather than becoming a sentence invented in the browser. */
    const ranked = (res.data.worst_servers || []).find((w) => w.display_name === server);
    onCard(card, ranked && ranked.reason ? ranked.reason : null);
  })();
}

/* The server header's status dot, band badge, engine badge and the WHY beneath them, all from the card above.
 *
 * The band word alone is not an answer. `Warning` has three unrelated causes — a genuine metric breach, a server
 * awaiting its first collection, and a collector error — so a badge reading "Warning" with no way to ask why is
 * the #2422 report rebuilt on a new surface, which is exactly what #2429 fixed on the desktop by attaching the
 * card's own metric rows. Nothing shown here is derived in the browser (R1): the per-metric severity chips are
 * rendered by fleet.js's own metricBands so there is one implementation rather than two, the reason is the same
 * sentence the fleet page shows, and the engine is the token the store recorded. A server outside the ranking
 * gets the chips and no sentence, because inventing one here is the second derivation this comment refuses. */
function fillServerHead(dot, badgeSlot, engineSlot, whySlot, card, reason) {
  if (!card) return;

  dot.className = "dot " + bandClass(card.band);
  mount(
    badgeSlot,
    el("span", { class: "badge " + bandClass(card.band), text: card.status || card.band, title: reason || null })
  );

  /* The engine badge is the answer to "why does this server have six tabs and that one twelve". Only a card
     that made a claim gets one: an unstamped engine renders no badge rather than one reading "SQL Server",
     because the tabs it gets are a default, not a finding — which is why the SERVER sends null there rather
     than a description of the absence. The wording is MonitoredEngineKind's, not this file's (R1). */
  if (card.engine_description) {
    mount(engineSlot, [el("span", { class: "badge engine", text: card.engine_description })]);
  }

  mount(whySlot, [
    reason ? el("div", { class: "server-reason " + bandClass(card.band), text: reason }) : null,
    metricBands(card),
  ]);
}
