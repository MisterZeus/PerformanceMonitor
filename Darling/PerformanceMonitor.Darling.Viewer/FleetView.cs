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

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// Owns the fleet: the AUTHORITATIVE full server set (<see cref="All"/>) and the subset currently shown
/// in the sidebar (<see cref="Visible"/>).
///
/// <para><b>Why this exists.</b> Before this class, <c>ServerList.ItemsSource</c> WAS the fleet: ten call
/// sites read it back with <c>as IEnumerable&lt;DarlingServer&gt;</c> to answer "what servers exist?" —
/// the alert-badge painter, the Overview builder, the deep-link resolver, the card click handlers, the
/// schedule editor. That is fine only while the bound list is the complete list. The moment the sidebar
/// shows a SUBSET (a tag filter, a search) or a MIXED row type (tag headers above servers), every one of
/// those consumers silently answers the wrong question — badges stop painting for filtered-out servers,
/// the schedule editor receives a truncated fleet, and a card click for a hidden server resolves to
/// nothing. None of that throws; it just quietly misbehaves.</para>
///
/// <para>So the two ideas are separated permanently: ask <see cref="All"/> "does this server exist / give
/// me the whole fleet", and bind <see cref="Visible"/> to the list. Free of any WPF dependency, so the
/// projection and lookup rules are unit-testable without a window.</para>
///
/// <para><b>Step 1 is deliberately behaviour-neutral:</b> <see cref="Visible"/> is every server, in the
/// order handed in. Filtering (tags, search, database) slots into <see cref="Rebuild"/> alone — the one
/// seam — so no consumer has to change again when it lands.</para>
/// </summary>
internal sealed class FleetView
{
    private List<DarlingServer> _all = new();
    private List<DarlingServer> _visible = new();

    /// <summary>Every known server, regardless of what the sidebar is currently showing. This is what a
    /// consumer asking "what is in the fleet?" must read — never the bound list.</summary>
    public IReadOnlyList<DarlingServer> All => _all;

    /// <summary>The servers the sidebar shows, after filtering. Bound to the list control.</summary>
    public IReadOnlyList<DarlingServer> Visible => _visible;

    /// <summary>Total fleet size — the M in "showing N of M".</summary>
    public int TotalCount => _all.Count;

    /// <summary>How many servers survive the current filter — the N in "showing N of M".</summary>
    public int VisibleCount => _visible.Count;

    /// <summary>True when a filter is hiding at least one server, so the UI must say so rather than
    /// letting a short list read as the whole fleet.</summary>
    public bool IsFiltered => _visible.Count != _all.Count;

    /// <summary>Raised after <see cref="Visible"/> is recomputed, so the window can rebind once.</summary>
    public event EventHandler? ProjectionChanged;

    /// <summary>Replaces the fleet (a load, or a refresh that changed membership) and reprojects.</summary>
    public void SetAll(IEnumerable<DarlingServer> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);

        _all = servers.ToList();
        Rebuild();
    }

    /// <summary>
    /// Recomputes <see cref="Visible"/> from <see cref="All"/>. The SINGLE place filtering will be
    /// applied, so adding tag/search/database filters never touches a consumer or a call site again.
    /// </summary>
    public void Rebuild()
    {
        /* Step 1: identity projection — no filters exist yet, so Visible is the whole fleet in the order
           it was handed in (already favourites-first sorted by the caller). */
        _visible = new List<DarlingServer>(_all);

        ProjectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The server with this id, or null. Resolves against <see cref="All"/> ON PURPOSE: a deep link, an
    /// Overview card click, or a selection restore must still find a server that the current filter
    /// happens to be hiding — otherwise the action silently does nothing, which is exactly the class of
    /// bug this type exists to prevent.
    /// </summary>
    public DarlingServer? Find(int serverId) => _all.Find(s => s.ServerId == serverId);

    /// <summary>
    /// The server to select after a rebuild: the previously-selected one if it still exists (by id, so a
    /// renamed or re-sorted server still resolves), else the first VISIBLE server, else null when the
    /// filter hides everything. Callers must not fall back to index 0 of the bound list — once tag rows
    /// share that list, row 0 is a header, not a server.
    /// </summary>
    public DarlingServer? ResolveSelection(int? previousServerId)
    {
        if (previousServerId is int previous)
        {
            var kept = _visible.Find(s => s.ServerId == previous);
            if (kept is not null)
            {
                return kept;
            }
        }

        return _visible.Count > 0 ? _visible[0] : null;
    }
}
