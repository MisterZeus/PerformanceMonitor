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
using PerformanceMonitor.Collectors;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// One PostgreSQL inner tab: which inner-tab index it occupies, what its header reads, and which
/// COLLECTORS serve it. Panels are declared by collector NAME rather than by store table, so the table can
/// only ever be what <see cref="ICollectorSchemaInfo.TargetTable"/> says it is — the name is also the key
/// <see cref="CollectorEngineCapability.NotCollectedMessage"/> takes, so a panel's data and a panel's
/// explanation for having none are keyed on the same string by construction.
/// </summary>
/// <param name="InnerTabIndex">Its position in <c>ViewerServerTab.xaml</c>'s <c>InnerTabs</c>.</param>
/// <param name="Id">A stable identifier for pins and for the SQL Server registry's deep links to collide
/// with deliberately (<c>overview</c> means the same place on either engine).</param>
/// <param name="Header">The tab strip label, which must match the XAML <c>&lt;TabItem Header=&gt;</c>.</param>
/// <param name="Collectors">Every collector whose stored rows this tab renders, in panel order.</param>
/// <param name="Note">Why these panels sit together, when that is not obvious from the header. Rendered
/// above the tab's panels. Null for a tab whose single panel needs no framing.</param>
internal sealed record ViewerPostgresTab(
    int InnerTabIndex,
    string Id,
    string Header,
    IReadOnlyList<string> Collectors,
    string? Note);

/// <summary>
/// The PostgreSQL inner-tab registry — the viewer's half of #2530, and the direct counterpart of the web
/// dashboard's <c>POSTGRES_TABS</c> in <c>server-tabs.js</c> (#2547). Same six tabs, same ids, same
/// grouping, so the two front ends do not teach an operator two different shapes for one engine.
///
/// <para><b>Why a registry rather than "read the XAML".</b> The SQL Server surface has nineteen inner tabs
/// declared in XAML and dispatched by integer index, and nothing there is machine-readable enough to ask
/// "does every PostgreSQL collector reach a screen?". That question is the whole point of this issue: the
/// nine PostgreSQL collectors spent three releases MCP-only precisely because no check could see that the
/// graphical surface had fallen behind the data. This list is what such a check reads, and
/// <c>ViewerPostgresTabsTests</c> derives the other side of it from <see cref="CollectorCatalog"/> — so a
/// TENTH PostgreSQL collector turns the pin red naming itself, rather than quietly shipping invisible.</para>
///
/// <para><b>Six tabs against SQL Server's nineteen is the design.</b> Parity was never the constraint;
/// the missing tabs (tempdb, Query Store, trace flags, plan cache, <c>system_health</c>, Always On) have no
/// PostgreSQL analogue to fill, and the signals that DO matter here — wraparound headroom, the xmin
/// horizon, vacuum backlog, WAL retention — have no SQL Server analogue either. A PostgreSQL surface built
/// by asking "what does the SQL Server viewer have" would have missed most of them.</para>
///
/// <para><b>The Aurora-only panels are SHOWN, not hidden.</b> <c>pg_wait_stats</c> and
/// <c>pg_statement_stats</c> both gate on <c>CollectorTargetInfo.IsAurora</c>, so on stock PostgreSQL they
/// can never have content — and since #2532 the reason is a sentence
/// (<see cref="CollectorEngineCapability.NotCollectedMessage"/>) naming the server, the engine, the
/// collector and the exact Aurora surface, ending "and never will". The defect #2530 is about is
/// UNEXPLAINED emptiness, not emptiness: a panel that says that is the opposite of the twelve blank SQL
/// Server tabs it replaces. Hiding them would also make the tab strip a different shape on two PostgreSQL
/// servers in one fleet, and would make the one Aurora-specific capability the product has invisible from
/// a stock instance — while re-deriving in the viewer a gate the collectors already decide.</para>
/// </summary>
internal static class ViewerPostgresTabs
{
    /// <summary>
    /// The six tabs, in strip order. Indices continue the SQL Server run rather than displacing it: for a
    /// PostgreSQL server every SQL Server <c>TabItem</c> is collapsed and these six are shown, so both sets
    /// keep their fixed indices and <c>ViewerServerTab</c>'s existing index constants — which drill-down
    /// navigation keys on — are untouched by this feature.
    /// </summary>
    public static readonly IReadOnlyList<ViewerPostgresTab> All = new[]
    {
        new ViewerPostgresTab(
            ViewerServerTab.PgOverviewInnerTabIndex,
            "overview",
            "Overview",
            Array.Empty<string>(),
            /* No collector of its own: it reports on all nine, from collection_log joined to the catalog.
               That join is the point — a collector gated off for this engine writes NO collection_log row
               at all (pre-dispatch filtering, deliberately, so a permanent gap does not manufacture ~2,880
               fake SUCCESS rows a day), so a grid built from the log alone would show a PostgreSQL
               operator eight rows and never mention the ninth. Derived from the catalog, the missing one
               appears with the reason it is missing. */
            "Every PostgreSQL collector for this server: when it last ran, what it returned, and — for a "
            + "collector this engine cannot run at all — why it never will."),

        new ViewerPostgresTab(
            ViewerServerTab.PgActivityInnerTabIndex,
            "activity",
            "Activity",
            new[] { "pg_blocking", "pg_statement_stats", "pg_database_stats" },
            /* pg_database_stats sits under the statement grid rather than on a tab of its own because it
               answers the question the statement grid raises and cannot answer: a statement whose time
               makes no sense from its row count usually spilled, and pg_stat_database's temp counters are
               the only evidence of that we collect. */
            "What ran, what waited on what, and what it cost. Blocking is a periodic SAMPLE rather than an "
            + "event log, so the capture counts above the grid are its denominator — three chains means "
            + "something different in 60 captures than in 4."),

        new ViewerPostgresTab(
            ViewerServerTab.PgVacuumInnerTabIndex,
            "vacuum",
            "Vacuum",
            new[] { "pg_xmin_horizon", "pg_autovacuum_stats", "pg_wraparound_stats" },
            /* One tab, three panels, in causal order. Read separately each of the three looks survivable. */
            "One story in three panels: something holds the xmin horizon, the horizon starves vacuum, and "
            + "vacuum falling behind ends in wraparound. Read on their own each of these looks survivable."),

        new ViewerPostgresTab(
            ViewerServerTab.PgWaitsInnerTabIndex,
            "waits",
            "Waits",
            new[] { "pg_wait_stats" },
            null),

        new ViewerPostgresTab(
            ViewerServerTab.PgIoInnerTabIndex,
            "io",
            "I/O",
            new[] { "pg_io_stats" },
            null),

        new ViewerPostgresTab(
            ViewerServerTab.PgReplicationInnerTabIndex,
            "replication",
            "Replication",
            new[] { "pg_replication_slots" },
            null),
    };

    /// <summary>The first PostgreSQL tab's index — what a PostgreSQL server's tab strip selects, since
    /// every index below it belongs to a collapsed SQL Server tab.</summary>
    public static int FirstInnerTabIndex => All[0].InnerTabIndex;

    /// <summary>True when <paramref name="innerTabIndex"/> belongs to this registry.</summary>
    public static bool Owns(int innerTabIndex) => All.Any(t => t.InnerTabIndex == innerTabIndex);

    /// <summary>
    /// A tab's framing note by ID — empty for a tab that has none, and empty rather than throwing for an id
    /// this registry does not carry, because a missing note must never take a tab down with it. Looked up by
    /// id rather than by position so the strip order stays free to change.
    /// </summary>
    public static string NoteFor(string tabId) =>
        All.FirstOrDefault(t => string.Equals(t.Id, tabId, StringComparison.Ordinal))?.Note ?? string.Empty;

    /// <summary>
    /// The store table a declared collector writes, from the collector's own definition. Null for a name
    /// the catalog does not know, which <c>ViewerPostgresTabsTests</c> refuses — the registry may not name
    /// a collector that does not exist.
    /// </summary>
    public static string? TableOf(string collectorName) =>
        CollectorCatalog.Find(collectorName)?.TargetTable;

    /// <summary>
    /// Every PostgreSQL collector the catalog ships, derived rather than listed. The pins compare this
    /// against <see cref="All"/> in BOTH directions: a collector missing from the registry is a screen that
    /// was never built, and a registry entry that is not a PostgreSQL collector is a panel wired to
    /// something that will never fill it.
    /// </summary>
    public static IReadOnlyList<ICollectorSchemaInfo> PostgresCollectors() =>
        CollectorCatalog.All
            .Where(d => d.TargetEngine == CollectorTargetEngine.PostgreSql)
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
}
