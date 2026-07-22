/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

/*
 * Custom Views (#1563): the list page and the renderer. A view is stored JSON {panels:[<renderPanel descriptor>]}
 * authored in the composer (editor.js); this page RENDERS it by mapping each stored panel through the UNMODIFIED
 * renderPanel into a `.panel-grid` — so a stored dashboard uses the exact same viz registry the built-in pages do
 * (per-panel isolation is free: renderPanel error-strips its own fetch/throw/unknown-viz). Before calling
 * renderPanel, each panel's read is checked against the cached catalog and its viz against the VIZ registry, so a
 * stale/unknown read renders a clean "unknown read" strip instead of an opaque 404 inside the panel.
 *
 * Edit affordances (New / Edit / Delete / Import) are shown when the session reports can_edit (the server is the
 * authority for every write); RENDER and EXPORT are available to every seat. All user text (names,
 * descriptions) reaches the DOM through el()/textContent (R4 — never innerHTML).
 */

import { el, mount, apiGet, loadingStrip, errorStrip, emptyStrip, relTime } from "../util.js";
import { renderPanel, VIZ } from "../panels.js";
import { renderComposedPanelCard } from "../compose.js";
import { renderMarkdown } from "../markdown.js";
import { NOTEBOOK_TEMPLATES, isNotebookDefinition } from "../notebook.js";
import * as api from "../views-api.js";

/** The time-range choices the rendered view's chrome offers (mirrors the composer's RANGE_OPTIONS). */
const VIEW_RANGE_OPTIONS = [
  { hours: 1, label: "Last hour" },
  { hours: 6, label: "Last 6 hours" },
  { hours: 24, label: "Last 24 hours" },
  { hours: 24 * 7, label: "Last 7 days" },
  { hours: 24 * 30, label: "Last 30 days" },
  { hours: 24 * 90, label: "Last 90 days" },
];

/* ─────────────────────────── list page ─────────────────────────── */

export async function renderViewList(main) {
  mount(main, [listHead(false), loadingStrip("Loading views…")]);

  const [session, res] = await Promise.all([api.getSession(), api.listViews()]);
  const canEdit = !!session.can_edit;

  if (res.kind === "error") {
    mount(main, [listHead(canEdit), errorStrip(res.message)]);
    return;
  }

  const views = Array.isArray(res.data) ? res.data : [];
  const nodes = [listHead(canEdit)];

  /* Empty list -> a real first-run hero that sells the feature (M4), not a bare strip. The hero carries its own
     New view + Import affordances (can_edit only), so the standalone import panel is only shown once views exist. */
  if (!views.length) {
    nodes.push(firstRunHero(canEdit));
    mount(main, nodes);
    return;
  }

  if (canEdit) {
    nodes.push(importPanel());
  }

  nodes.push(el("div", { class: "view-cards" }, views.map((v) => viewCard(v))));
  mount(main, nodes);
}

/* First-run hero (M4): a centered pitch + a prominent New view + a few starting-point ideas + the (secondary)
   import. A read-only network seat can't compose, so it gets an explanatory variant (no New view / import). */
function firstRunHero(canEdit) {
  if (!canEdit) {
    return el("div", { class: "views-hero" }, [
      el("div", { class: "hero-title", text: "No custom views yet" }),
      el("div", {
        class: "hero-pitch",
        text:
          "Your session couldn't be confirmed, so the composer is read-only right now — reload the page to create a " +
          "view. Once created, custom views appear here for every seat to open and export.",
      }),
    ]);
  }
  const suggestions = ["Top waits by server", "CPU trend over time", "Slowest procedures by database"];
  return el("div", { class: "views-hero" }, [
    el("div", { class: "hero-title", text: "Compose your own dashboards & notebooks" }),
    el("div", {
      class: "hero-pitch",
      text:
        "Build a DASHBOARD — pick a metric, aggregate it, group and filter it, and chart it as a line, bar, pie, " +
        "stacked area, or stat — or a NOTEBOOK that mixes markdown notes with those same charts into a guided " +
        "investigation. Both save for every seat and re-scope live by server and time range. No SQL to write.",
    }),
    el("div", { class: "hero-ctas" }, [
      el("a", { class: "btn primary hero-cta", href: "#/view/new", text: "New view" }),
      el("a", { class: "btn hero-cta", href: "#/notebook/new", text: "New notebook" }),
      templateMenu(),
    ]),
    el("div", { class: "hero-suggest" }, [
      el("span", { class: "hero-suggest-label", text: "Popular starting points" }),
      el("div", { class: "hero-suggest-chips" }, suggestions.map((s) => el("span", { class: "hero-chip", text: s }))),
    ]),
    importPanel(),
  ]);
}

function listHead(canEdit) {
  return el("div", { class: "page-head" }, [
    el("h2", { text: "Custom Views" }),
    el("div", { class: "spacer" }),
    canEdit ? templateMenu() : null,
    canEdit ? el("a", { class: "btn", href: "#/notebook/new", text: "New notebook" }) : null,
    canEdit ? el("a", { class: "btn primary", href: "#/view/new", text: "New view" }) : null,
  ]);
}

/* The "New from template" menu (#1563 D7): a <details> dropdown of the seed notebooks, each linking to the
   notebook composer pre-filled from that template. Built with el()/textContent like everything else. */
function templateMenu() {
  const items = NOTEBOOK_TEMPLATES.map((t) =>
    el("a", { class: "template-item", href: "#/notebook/new/" + encodeURIComponent(t.key), title: t.description }, [
      el("span", { class: "template-label", text: t.label }),
      el("span", { class: "template-desc", text: t.description }),
    ])
  );
  return el("details", { class: "template-menu" }, [
    el("summary", { class: "btn template-summary", text: "New from template ▾" }),
    el("div", { class: "template-list" }, items),
  ]);
}

function viewCard(v) {
  const isNotebook = v.kind === "notebook";
  const meta =
    "v" + v.version + " · updated " + relTime(v.updated_at) + (v.updated_by ? " by " + v.updated_by : "");
  const chips = el("div", { class: "vc-chips" });
  /* The list summary carries a concrete `kind` (backend #56), so the link + "Notebook" badge are set SYNCHRONOUSLY
     here — no wait on the per-card definition fetch (which only fills in the count chips). */
  const href = (isNotebook ? "#/notebook/" : "#/view/") + encodeURIComponent(v.id);
  const card = el("a", { class: "view-card card", href }, [
    el("div", { class: "vc-name" }, [
      el("span", { class: "vc-name-text", text: v.name }),
      isNotebook ? el("span", { class: "notebook-badge", text: "Notebook" }) : null,
    ]),
    v.description ? el("div", { class: "vc-desc", text: v.description }) : null,
    chips,
    el("div", { class: "vc-meta", text: meta }),
  ]);
  enrichCard(v.id, chips, isNotebook);
  return card;
}

/* The list summary carries `kind` (link + badge already set in viewCard) but NOT the panel/cell counts, so those
   are fetched per card and filled in progressively — the card renders immediately and never blocks on this fetch,
   and a failed/empty fetch just leaves the (display:none-when-empty) chip row absent. `isNotebook` comes from the
   summary; the fetched definition is a robust fallback for the count shape. */
async function enrichCard(id, chips, isNotebook) {
  const res = await api.getView(id);
  if (res.kind !== "data" || !res.data) return;
  const def = res.data.definition || {};

  if (isNotebook || isNotebookDefinition(def)) {
    const cells = Array.isArray(def.cells) ? def.cells : [];
    if (!cells.length) return;
    const panelCount = cells.filter((c) => c && c.type === "panel").length;
    const nodes = [el("span", { class: "vc-chip count", text: cells.length + (cells.length === 1 ? " cell" : " cells") })];
    if (panelCount) nodes.push(el("span", { class: "vc-chip", text: panelCount + (panelCount === 1 ? " panel" : " panels") }));
    mount(chips, nodes);
    return;
  }

  const panels = Array.isArray(def.panels) ? def.panels : [];
  if (!panels.length) return;
  const vizTypes = [];
  for (const p of panels) {
    const viz = p && typeof p.viz === "string" ? p.viz : null;
    if (viz && !vizTypes.includes(viz)) vizTypes.push(viz);
  }
  const nodes = [
    el("span", { class: "vc-chip count", text: panels.length + (panels.length === 1 ? " panel" : " panels") }),
  ];
  for (const viz of vizTypes) nodes.push(el("span", { class: "vc-chip viz", text: viz }));
  mount(chips, nodes);
}

/* Paste-to-import (can_edit only): parse -> client-validate against the catalog -> POST (backend re-validates).
   Accepts an exported view/notebook {name, description?, definition} or a bare definition ({panels:[…]} dashboard
   or {kind:"notebook", cells:[…]} notebook) plus a name. */
function importPanel() {
  const textarea = el("textarea", {
    class: "import-box",
    rows: "4",
    placeholder: 'Paste exported view or notebook JSON — {"name": "...", "definition": { ... }}',
    "aria-label": "Paste exported view or notebook JSON",
  });
  const status = el("div", { class: "import-status" });
  const importBtn = el("button", { class: "btn", type: "button", text: "Import" });

  importBtn.addEventListener("click", async () => {
    mount(status, null);
    const raw = textarea.value.trim();
    if (!raw) {
      mount(status, errorStrip("Paste a view's JSON first."));
      return;
    }

    let parsed;
    try {
      parsed = JSON.parse(raw);
    } catch (e) {
      mount(status, errorStrip("That is not valid JSON: " + (e && e.message ? e.message : String(e))));
      return;
    }

    const def =
      parsed && parsed.definition ? parsed.definition : parsed && (parsed.panels || parsed.cells) ? parsed : null;
    if (!def) {
      mount(status, errorStrip("The JSON must contain a 'definition' (or be a definition with 'panels' or 'cells')."));
      return;
    }
    const name = (parsed.name || "").trim();
    if (!name) {
      mount(status, errorStrip('The imported JSON has no "name". Add a top-level "name" field and try again.'));
      return;
    }

    const catalog = await api.getCatalog();
    const invalid = api.validateDefinition(def, catalog);
    if (invalid) {
      mount(status, errorStrip(invalid));
      return;
    }

    importBtn.disabled = true;
    mount(status, el("div", { class: "strip loading", text: "Importing…" }));
    const result = await api.createView({ name, description: parsed.description || null, definition: def });
    importBtn.disabled = false;
    if (result.kind === "data" && result.data) {
      const route = isNotebookDefinition(def) ? "#/notebook/" : "#/view/";
      location.hash = route + encodeURIComponent(result.data.id);
      return;
    }
    mount(status, errorStrip(result.message || "Could not import."));
  });

  return el("details", { class: "import-panel" }, [
    el("summary", { text: "Import a view or notebook" }),
    el("div", { class: "import-body" }, [textarea, el("div", { class: "import-actions" }, [importBtn]), status]),
  ]);
}

/* ─────────────────────────── renderer ─────────────────────────── */

/* Per-view scope memory: the picked server / time range / variable values survive the app's 60s background
   re-render (app.js refresh() → route() rebuilds the whole view), which otherwise snapped the range back to the
   view's default every minute. Keyed by view id, session-lived — a full page reload clears it, restoring the
   view's declared default. */
const viewScopeMemory = new Map();

function seedState(id, defaultHours, variables) {
  const cached = viewScopeMemory.get(String(id));
  if (cached) {
    return { server: cached.server, hours: cached.hours, values: { ...cached.values } };
  }
  const state = { server: "All", hours: defaultHours, values: {} };
  for (const v of variables) {
    if (v.dimension !== "server" && v.default) state.values[v.name] = v.default;
  }
  return state;
}

function rememberScope(id, state) {
  viewScopeMemory.set(String(id), { server: state.server, hours: state.hours, values: { ...state.values } });
}

export async function renderView(main, id) {
  mount(main, loadingStrip("Loading view…"));

  const [session, catalog, res, fleetRes] = await Promise.all([
    api.getSession(),
    api.getCatalog(),
    api.getView(id),
    apiGet("/api/fleet"),
  ]);
  if (res.kind === "error") {
    mount(main, [el("div", { class: "page-head" }, [backToViews(), el("h2", { text: "View" })]), errorStrip(res.message)]);
    return;
  }

  const view = res.data || {};
  const def = view.definition || {};

  /* A notebook is the same stored row with a kind:"notebook" definition — render it as a document, not a grid. */
  if (isNotebookDefinition(def)) {
    renderNotebookDoc(main, view, session, catalog, fleetRes);
    return;
  }

  const panels = Array.isArray(def.panels) ? def.panels : [];
  const canEdit = !!session.can_edit;
  const readSet = new Set((catalog.reads || []).map((r) => r.name));
  const sourceSet = new Set(((catalog.compose || {}).measures || []).map((m) => m.source));

  const status = el("div", { class: "view-status" });
  const head = el("div", { class: "page-head" }, [
    backToViews(),
    el("h2", { text: view.name || "View" }),
    view.description ? el("div", { class: "meta", text: view.description }) : null,
    el("div", { class: "spacer" }),
    exportButton(view),
    canEdit ? el("a", { class: "btn small", href: "#/view/" + encodeURIComponent(id) + "/edit", text: "Edit" }) : null,
    canEdit ? deleteButton(id, status) : null,
  ]);

  if (!panels.length) {
    mount(main, [head, status, emptyStrip("This view has no panels yet.")]);
    return;
  }

  /* View-level scope (Erik's Decision 1): server / time / template-variable controls at the top re-scope EVERY
     panel at once. The scope only affects composed panels (v1 read panels carry their own fixed params); flipping
     it re-runs the whole grid. `state` is the live control state; currentScope() snapshots it for a run. */
  const fleet = fleetOptions(fleetRes);
  const variables = Array.isArray(def.variables) ? def.variables.filter((v) => v && v.name) : [];
  const defaultHours = def.range && typeof def.range.hours === "number" ? def.range.hours : 24;
  const hasComposed = panels.some((p) => p && p.source != null);

  const state = seedState(id, defaultHours, variables);

  function currentScope() {
    return {
      server: state.server,
      hours: state.hours,
      variables: variables.map((v) => ({ name: v.name, dimension: v.dimension, default: v.default || undefined })),
      values: { ...state.values },
    };
  }

  const gridBox = el("div", { class: "panel-grid" });
  function renderGrid() {
    mount(gridBox, panels.map((p) => panelOrError(p, readSet, sourceSet, currentScope())));
  }

  /* The chrome is only meaningful when a composed panel can re-scope; a pure v1 read view skips it. */
  const onChange = () => { rememberScope(id, state); renderGrid(); };
  const controls = hasComposed ? buildViewControls(fleet, variables, state, defaultHours, onChange) : null;

  mount(main, [head, status, controls, gridBox]);
  renderGrid();
}

/* ─────────────────────────── notebook renderer (#1563 D7) ─────────────────────────── */

/*
 * Render a notebook definition as a vertical DOCUMENT: markdown cells through the air-gapped renderMarkdown, panel
 * cells through the SAME composed-panel card + validation the dashboard grid uses (panelOrError). It reuses this
 * page's server/time/variable scope bar (buildViewControls) exactly as a dashboard does, so flipping the scope
 * re-scopes every panel cell at once. Export/Delete/Edit are shared with the dashboard renderer; Edit points at the
 * notebook composer route.
 */
async function renderNotebookDoc(main, view, session, catalog, fleetRes) {
  const def = view.definition || {};
  const cells = Array.isArray(def.cells) ? def.cells : [];
  const canEdit = !!session.can_edit;
  const readSet = new Set((catalog.reads || []).map((r) => r.name));
  const sourceSet = new Set(((catalog.compose || {}).measures || []).map((m) => m.source));

  const status = el("div", { class: "view-status" });
  const head = el("div", { class: "page-head" }, [
    backToViews(),
    el("h2", { text: view.name || "Notebook" }),
    el("span", { class: "notebook-badge", text: "Notebook" }),
    view.description ? el("div", { class: "meta", text: view.description }) : null,
    el("div", { class: "spacer" }),
    exportButton(view),
    canEdit ? el("a", { class: "btn small", href: "#/notebook/" + encodeURIComponent(view.id) + "/edit", text: "Edit" }) : null,
    canEdit ? deleteButton(view.id, status) : null,
  ]);

  if (!cells.length) {
    mount(main, [head, status, emptyStrip("This notebook has no cells yet.")]);
    return;
  }

  /* Scope bar identical to a dashboard's: it only drives the panel cells (markdown is static). Skipped when a
     notebook is prose-only (no panel cells to re-scope). */
  const fleet = fleetOptions(fleetRes);
  const variables = Array.isArray(def.variables) ? def.variables.filter((v) => v && v.name) : [];
  const defaultHours = def.range && typeof def.range.hours === "number" ? def.range.hours : 24;
  const hasPanels = cells.some((c) => c && c.type === "panel" && c.source != null);

  const state = seedState(view.id, defaultHours, variables);

  function currentScope() {
    return {
      server: state.server,
      hours: state.hours,
      variables: variables.map((v) => ({ name: v.name, dimension: v.dimension, default: v.default || undefined })),
      values: { ...state.values },
    };
  }

  const docBox = el("div", { class: "notebook-doc" });
  function renderDoc() {
    mount(docBox, cells.map((cell) => renderCell(cell, readSet, sourceSet, currentScope())));
  }

  const onChange = () => { rememberScope(view.id, state); renderDoc(); };
  const controls = hasPanels ? buildViewControls(fleet, variables, state, defaultHours, onChange) : null;
  mount(main, [head, status, controls, docBox]);
  renderDoc();
}

/* One notebook cell -> a document block: a markdown cell renders through renderMarkdown (XSS-safe); a panel cell
   through the shared panelOrError (unknown source/viz -> a clean strip, exactly as in the dashboard grid). */
function renderCell(cell, readSet, sourceSet, scope) {
  if (!cell || typeof cell !== "object") return null;
  if (cell.type === "markdown") {
    return el("div", { class: "notebook-md markdown-body" }, [renderMarkdown(cell.text)]);
  }
  if (cell.type === "panel") {
    return el("div", { class: "notebook-panel" }, [panelOrError(cell, readSet, sourceSet, scope)]);
  }
  return null;
}

function backToViews() {
  return el("a", { href: "#/views", text: "← Views" });
}

/* Fleet server options for the scope picker (value = stored server_name, label = display name). */
function fleetOptions(fleetRes) {
  if (fleetRes.kind !== "data" || !fleetRes.data) return [];
  return [...(fleetRes.data.cards || [])]
    .map((c) => ({ value: c.server_name || c.display_name, label: c.display_name }))
    .filter((o) => o.value)
    .sort((a, b) => a.label.localeCompare(b.label));
}

/* The view-scope control bar: a server scope picker (single / multi / All), a time-range select, and a value input
   per declared template variable (a server-typed variable is covered by the scope picker, so it is not repeated). */
function buildViewControls(fleet, variables, state, defaultHours, onChange) {
  const fields = [
    el("div", { class: "vc-field" }, [el("span", { class: "vc-label", text: "Servers" }), buildServerScopePicker(fleet, state, onChange)]),
    el("div", { class: "vc-field" }, [el("span", { class: "vc-label", text: "Time range" }), buildRangeSelect(defaultHours, state, onChange)]),
  ];
  for (const v of variables) {
    if (v.dimension === "server") continue;
    fields.push(el("div", { class: "vc-field" }, [el("span", { class: "vc-label", text: "$" + v.name }), varControl(v, state, onChange)]));
  }
  return el("div", { class: "view-controls" }, fields);
}

/* A template-variable value input. Bound to state.values on `change` (not each keystroke, so the grid re-runs on
   commit, not per character). Empty clears the binding (the panels referencing it then return no rows — the honest
   "pick a value" state, since the compose contract has no wildcard for an unbound $var). */
function varControl(v, state, onChange) {
  const inp = el("input", { class: "filter-box", type: "text", placeholder: "(pick a value)", "aria-label": v.name });
  inp.value = state.values[v.name] || "";
  inp.addEventListener("change", () => {
    const val = inp.value.trim();
    if (val) state.values[v.name] = val;
    else delete state.values[v.name];
    onChange();
  });
  return inp;
}

/* A native <select> time-range picker bound to state.hours. A stored default outside the standard options is added
   so the view opens on exactly what it declared. */
function buildRangeSelect(defaultHours, state, onChange) {
  const sel = el("select", { class: "filter-box", "aria-label": "Time range" });
  for (const r of VIEW_RANGE_OPTIONS) sel.appendChild(el("option", { value: String(r.hours), text: r.label }));
  if (!VIEW_RANGE_OPTIONS.some((r) => r.hours === defaultHours)) {
    sel.appendChild(el("option", { value: String(defaultHours), text: "Default (" + defaultHours + "h)" }));
  }
  sel.value = String(state.hours);
  sel.addEventListener("change", () => {
    state.hours = parseInt(sel.value, 10) || defaultHours;
    onChange();
  });
  return sel;
}

/* The server scope picker: a <details> dropdown of checkboxes — "All servers (fleet)" plus each fleet server.
   state.server is "All" (or an empty selection ⇒ fleet) or an array of names; checking a server switches off All,
   and clearing every server falls back to All. <details> gives open/close + keyboard for free (no outside-click JS). */
function buildServerScopePicker(fleet, state, onChange) {
  const summary = el("summary", { class: "scope-summary" });
  const list = el("div", { class: "scope-list" });

  const isAll = () => state.server === "All" || (Array.isArray(state.server) && state.server.length === 0);
  const names = () => (Array.isArray(state.server) ? state.server : []);
  const labelFor = (val) => {
    const o = fleet.find((x) => x.value === val);
    return o ? o.label : val;
  };

  function toggle(val, on) {
    let arr = isAll() ? [] : [...names()];
    if (on) {
      if (!arr.includes(val)) arr.push(val);
    } else {
      arr = arr.filter((x) => x !== val);
    }
    state.server = arr.length ? arr : "All";
  }

  function refresh() {
    const n = names();
    summary.textContent = isAll() ? "All servers (fleet)" : n.length === 1 ? labelFor(n[0]) : n.length + " servers";
  }

  function redraw() {
    const rows = [
      checkRow("All servers (fleet)", isAll(), () => {
        state.server = "All";
        redraw();
        onChange();
      }),
    ];
    for (const o of fleet) {
      const on = !isAll() && names().includes(o.value);
      rows.push(
        checkRow(o.label, on, () => {
          toggle(o.value, !on);
          redraw();
          onChange();
        })
      );
    }
    mount(list, rows);
    refresh();
  }

  redraw();
  return el("details", { class: "scope-picker" }, [summary, list]);
}

function checkRow(label, checked, onToggle) {
  const box = el("input", { type: "checkbox", class: "editor-check" });
  box.checked = checked;
  box.addEventListener("change", onToggle);
  return el("label", { class: "scope-item" }, [box, el("span", { text: label })]);
}

/* Pre-check a panel against the catalog so an unknown read/source/viz is a clean strip, not an opaque 404 / crash.
   A composed panel (names a `source`) runs through /api/compose/run with the view scope; a read panel through the
   v1 renderPanel (its own fixed params, scope-independent). */
function panelOrError(p, readSet, sourceSet, scope) {
  if (!p || typeof p !== "object") {
    return panelErrorCard("Invalid panel", "This panel is not an object.");
  }
  if (p.path != null) {
    return panelErrorCard(p.title, "This panel uses raw 'path' mode, which is not supported.");
  }
  if (p.source != null) {
    if (!sourceSet.has(p.source)) {
      return panelErrorCard(p.title, "Unknown source '" + p.source + "'. It may have been renamed or removed.");
    }
    if (!p.viz) {
      return panelErrorCard(p.title, "This composed panel has no chart type.");
    }
    return renderComposedPanelCard(p, scope);
  }
  if (!p.read || !readSet.has(p.read)) {
    return panelErrorCard(p.title, "Unknown read '" + (p.read || "") + "'. It may have been renamed or removed.");
  }
  if (!p.viz || !VIZ[p.viz]) {
    return panelErrorCard(p.title, "Unknown visualization '" + (p.viz || "") + "'.");
  }
  return renderPanel(p);
}

function panelErrorCard(title, message) {
  return el("div", { class: "panel card" }, [
    el("h3", {}, [title || "Panel"]),
    el("div", { class: "panel-body" }, [errorStrip(message)]),
  ]);
}

/* Export = a runtime Blob download of {name, description?, definition} (re-importable). Available to every seat;
   the Blob URL is created at runtime (never a source literal), so the air-gap scan stays green. */
function exportButton(view) {
  const btn = el("button", { class: "btn small", type: "button", text: "Export" });
  btn.addEventListener("click", () => {
    const payload = { name: view.name, description: view.description || null, definition: view.definition || {} };
    const blob = new Blob([JSON.stringify(payload, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = el("a", { href: url, download: safeFileName(view.name) + ".json" });
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
  });
  return btn;
}

function safeFileName(name) {
  const cleaned = String(name || "view").replace(/[^A-Za-z0-9._-]+/g, "_").replace(/^_+|_+$/g, "");
  return cleaned || "view";
}

function deleteButton(id, status) {
  const btn = el("button", { class: "btn small danger", type: "button", text: "Delete" });
  btn.addEventListener("click", async () => {
    if (!window.confirm("Delete this view? This cannot be undone.")) {
      return;
    }
    btn.disabled = true;
    mount(status, el("div", { class: "strip loading", text: "Deleting…" }));
    const res = await api.deleteView(id);
    if (res.kind === "data") {
      location.hash = "#/views";
      return;
    }
    btn.disabled = false;
    mount(status, errorStrip(res.message || "Could not delete the view."));
  });
  return btn;
}
