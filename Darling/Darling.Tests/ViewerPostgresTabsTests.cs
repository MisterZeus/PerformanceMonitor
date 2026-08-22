/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The WPF viewer's PostgreSQL tab set (#2530) — the counterpart of <c>ServerPageTabsTests</c>, which
/// holds the web dashboard's two registries to the same standard.
///
/// <para><b>What these pins are for.</b> The nine PostgreSQL collectors spent three releases MCP-only, and
/// nothing went red about it: no check could see that the graphical surface had fallen behind the data.
/// So the placement question is asked in BOTH directions and DERIVED from
/// <see cref="CollectorCatalog"/> — a tenth PostgreSQL collector turns
/// <see cref="EveryPostgresCollector_IsShownOnExactlyOnePostgresTab"/> red naming itself, and a registry
/// entry for a collector that does not exist turns its twin red. An enumerated list of nine would have
/// passed through the whole failure it exists to prevent.</para>
/// </summary>
public sealed class ViewerPostgresTabsTests
{
    // ── Placement, derived from the catalog in both directions ───────────────────────────────────

    /// <summary>
    /// Every PostgreSQL collector the catalog ships reaches exactly one PostgreSQL tab. The forward
    /// direction: add a collector and forget the screen, and this names it.
    /// </summary>
    [Fact]
    public void EveryPostgresCollector_IsShownOnExactlyOnePostgresTab()
    {
        var placements = ViewerPostgresTabs.All
            .SelectMany(tab => tab.Collectors.Select(c => (Collector: c, tab.Id)))
            .ToList();

        var missing = new List<string>();
        var duplicated = new List<string>();

        foreach (var definition in ViewerPostgresTabs.PostgresCollectors())
        {
            var on = placements.Where(p => string.Equals(p.Collector, definition.Name, StringComparison.Ordinal)).ToList();
            if (on.Count == 0)
            {
                missing.Add($"{definition.Name} (stored in {definition.TargetTable})");
            }
            else if (on.Count > 1)
            {
                duplicated.Add($"{definition.Name} on {string.Join(", ", on.Select(p => p.Id))}");
            }
        }

        Assert.True(missing.Count == 0,
            "The service collects these PostgreSQL signals and no PostgreSQL viewer tab shows them. Add a " +
            "panel and list the collector on that tab in ViewerPostgresTabs.All — do NOT relax this pin, " +
            "which exists because eight of them shipped invisible for three releases:\n  " +
            string.Join("\n  ", missing));

        Assert.True(duplicated.Count == 0,
            "A collector is claimed by more than one PostgreSQL tab, so its panel has two homes and the " +
            "'which screen shows this' question has two answers:\n  " + string.Join("\n  ", duplicated));
    }

    /// <summary>
    /// The reverse: the registry may only name collectors that exist, are PostgreSQL, and whose store table
    /// resolves. A panel wired to a name the catalog does not know can never fill.
    /// </summary>
    [Fact]
    public void TheRegistry_NamesOnlyRealPostgresCollectors()
    {
        var bad = new List<string>();

        foreach (var tab in ViewerPostgresTabs.All)
        {
            foreach (var name in tab.Collectors)
            {
                var definition = CollectorCatalog.Find(name);
                if (definition is null)
                {
                    bad.Add($"{tab.Id}: '{name}' is not in CollectorCatalog");
                }
                else if (definition.TargetEngine != CollectorTargetEngine.PostgreSql)
                {
                    bad.Add($"{tab.Id}: '{name}' is a {definition.TargetEngine} collector");
                }
                else if (string.IsNullOrWhiteSpace(ViewerPostgresTabs.TableOf(name)))
                {
                    bad.Add($"{tab.Id}: '{name}' resolves to no store table");
                }
            }
        }

        Assert.True(bad.Count == 0, "PostgreSQL tab registry names collectors that cannot serve it:\n  " +
                                    string.Join("\n  ", bad));
    }

    /// <summary>
    /// The Aurora-only collectors are on the tab set, not hidden from it — the decision #2547 made for the
    /// web, pinned here so the viewer cannot quietly diverge. <c>pg_wait_stats</c> and
    /// <c>pg_statement_stats</c> gate on <c>IsAurora</c>, so on stock PostgreSQL they can never have
    /// content; since #2532 they answer with a sentence naming the exact Aurora surface instead of a blank,
    /// so the defect this issue is about — UNEXPLAINED emptiness — is closed by showing them, and hiding
    /// them would make the tab strip a different shape on two PostgreSQL servers in one fleet.
    /// <para>Derived from the shipped gates rather than named: this reads which collectors are Aurora-only
    /// out of <see cref="CollectorEngineCapability"/> and requires each to be placed.</para>
    /// </summary>
    [Fact]
    public void TheAuroraOnlyCollectors_AreShown_NotHidden()
    {
        var auroraOnly = ViewerPostgresTabs.PostgresCollectors()
            .Where(d => !CollectorEngineCapability.IsCollectedOnEngineKind(d, MonitoredEngineKind.Postgres))
            .Where(d => CollectorEngineCapability.IsCollectedOnEngineKind(d, MonitoredEngineKind.AuroraPostgres))
            .Select(d => d.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(auroraOnly.Count >= 2,
            "Expected at least two Aurora-only PostgreSQL collectors (pg_wait_stats and pg_statement_stats " +
            "both read an aurora_stat_* surface). Found: " + string.Join(", ", auroraOnly));

        var placed = ViewerPostgresTabs.All.SelectMany(t => t.Collectors).ToHashSet(StringComparer.Ordinal);
        var hidden = auroraOnly.Where(n => !placed.Contains(n)).ToList();

        Assert.True(hidden.Count == 0,
            "Aurora-only collector(s) were dropped from the PostgreSQL tab set: " + string.Join(", ", hidden) +
            ". They are SHOWN deliberately — the panel prints CollectorEngineCapability's sentence naming " +
            "the Aurora surface, which is the opposite of the unexplained blank this issue is about.");
    }

    // ── The registry, the XAML and the dispatch agree ────────────────────────────────────────────

    /// <summary>
    /// The registry's indices, headers and order are what the XAML actually declares. Three separate things
    /// could drift — an index constant, a header string, the TabItem order — and each one silently sends a
    /// tab click to the wrong loader, which is not a failure any of the other pins here can see.
    /// </summary>
    [Fact]
    public void TheRegistry_MatchesTheTabItemsDeclaredInTheXaml()
    {
        var xaml = ViewerFile("ViewerServerTab.xaml");

        /* Top-level TabItems only: the nested sub-tab groups (Queries, Memory, Activity...) are indented
           further, and the inner TabControl's own items are not inner-tab indices. */
        var headers = Regex
            .Matches(xaml, "^        <TabItem[^>]*?Header=\"([^\"]+)\"", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.True(headers.Count > ViewerPostgresTabs.All.Count,
            $"Only {headers.Count} top-level TabItems found in ViewerServerTab.xaml — the scan is not " +
            "matching the file's shape any more.");

        foreach (var tab in ViewerPostgresTabs.All)
        {
            Assert.True(tab.InnerTabIndex < headers.Count,
                $"Registry tab '{tab.Id}' claims inner-tab index {tab.InnerTabIndex}, but ViewerServerTab.xaml " +
                $"declares only {headers.Count} top-level TabItems.");

            Assert.Equal(tab.Header, Unescape(headers[tab.InnerTabIndex]));
        }

        /* The PostgreSQL run is a CONTIGUOUS block at the END. That is what lets both engines keep fixed
           indices: every SQL Server tab is collapsed for a PostgreSQL server and vice versa, and no
           existing index constant moves. A tab inserted between them would renumber the SQL Server run and
           break every drill-down that navigates by one of those constants. */
        var indices = ViewerPostgresTabs.All.Select(t => t.InnerTabIndex).ToList();
        Assert.Equal(indices.OrderBy(i => i).ToList(), indices);
        Assert.Equal(headers.Count - ViewerPostgresTabs.All.Count, indices[0]);
        Assert.Equal(headers.Count - 1, indices[^1]);
        Assert.Equal(indices.Count, indices.Distinct().Count());
    }

    /// <summary>
    /// Every PostgreSQL tab has its OWN arm in <c>LoadInnerTabAsync</c>. Without one it would fall through
    /// to <c>default:</c> and run the SQL Server correlated-lane reads — four collectors that cannot run on
    /// this engine — against a PostgreSQL server, painting the exact blank panels this issue is about.
    /// </summary>
    [Fact]
    public void EveryPostgresTab_HasItsOwnArmInTheDispatch()
    {
        var code = ViewerFile("ViewerServerTab.xaml.cs");

        var constants = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["overview"] = "PgOverviewInnerTabIndex",
            ["activity"] = "PgActivityInnerTabIndex",
            ["vacuum"] = "PgVacuumInnerTabIndex",
            ["waits"] = "PgWaitsInnerTabIndex",
            ["io"] = "PgIoInnerTabIndex",
            ["replication"] = "PgReplicationInnerTabIndex",
        };

        var missing = new List<string>();
        foreach (var tab in ViewerPostgresTabs.All)
        {
            Assert.True(constants.ContainsKey(tab.Id), $"No index constant is mapped for tab id '{tab.Id}'.");

            if (!code.Contains($"case {constants[tab.Id]}:", StringComparison.Ordinal))
            {
                missing.Add($"{tab.Id} (case {constants[tab.Id]}:)");
            }
        }

        Assert.True(missing.Count == 0,
            "PostgreSQL tab(s) have no arm in LoadInnerTabAsync and would fall through to the SQL Server " +
            "Overview lanes: " + string.Join(", ", missing));
    }

    /// <summary>
    /// No SQL Server tab loader reaches a PostgreSQL read. The "and from no other tab" half of the web's
    /// pin, in the shape the viewer has: the PostgreSQL reads live behind <c>GetPg*</c> on the data service,
    /// and only the PostgreSQL loader file may call them.
    /// </summary>
    [Fact]
    public void NoSqlServerTabLoader_CallsAPostgresRead()
    {
        var offenders = new List<string>();

        foreach (var file in ViewerFiles("ViewerServerTab"))
        {
            if (Path.GetFileName(file).Equals("ViewerServerTab.Postgres.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"_dataService\.GetPg[A-Za-z]+Async"))
            {
                offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A SQL Server tab loader calls a PostgreSQL read. Those tabs are collapsed for a PostgreSQL " +
            "server and shown for a SQL Server one, so this can only ever run against the wrong engine:\n  " +
            string.Join("\n  ", offenders));
    }

    // ── The tab set switches only on a positive claim ────────────────────────────────────────────

    /// <summary>
    /// Only a POSITIVE PostgreSQL claim switches the tab set. Absence is not evidence for either engine,
    /// and the unclaimed population is every server that has not reconnected since the engine-kind rung
    /// landed — guessing PostgreSQL from a NULL would break every one of them.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("sqlserver", false)]
    [InlineData("mysql", false)]
    [InlineData("postgres", true)]
    [InlineData("POSTGRES", true)]
    [InlineData("aurora-postgres", true)]
    public void TheTabSet_SwitchesOnlyOnAPositivePostgresClaim(string? engineKind, bool expected)
    {
        Assert.Equal(expected, Server(engineKind).IsPostgres);
    }

    /// <summary>
    /// The engine badge has three answers, and the asymmetry is the point: NO badge for a server the store
    /// makes no claim about (the tabs it gets are a default, not a finding), the describer's words for a
    /// recognised token, and the RAW TOKEN for one this build does not recognise — because the describer's
    /// "an unrecognised engine" is worded to sit mid-sentence in the capability messages and reads wrong as
    /// a label, while the literal string is what an operator would search their store for.
    /// </summary>
    [Fact]
    public void TheEngineBadge_ShowsNothingRatherThanGuessing()
    {
        Assert.Null(Server(null).EngineDescription);
        Assert.Null(Server("").EngineDescription);
        Assert.Null(Server("   ").EngineDescription);

        Assert.Equal("SQL Server", Server("sqlserver").EngineDescription);
        Assert.Equal("PostgreSQL", Server("postgres").EngineDescription);
        Assert.Equal("Aurora PostgreSQL", Server("aurora-postgres").EngineDescription);

        Assert.Equal("cockroach", Server("cockroach").EngineDescription);
    }

    /// <summary>
    /// BOTH server reads carry the discriminator. The sidebar uses <c>ManagedServersSql</c> on any seeded
    /// store — i.e. every real deployment — so a column added to only <c>ServersSql</c> would have left
    /// every PostgreSQL target on the SQL Server tab set while a unit test over the other query passed.
    /// </summary>
    [Fact]
    public void BothServerReads_CarryTheEngineDiscriminator()
    {
        foreach (var sql in new[] { ViewerDataService.ServersSql, ViewerDataService.ManagedServersSql })
        {
            Assert.Contains("engine_kind", sql, StringComparison.Ordinal);
            Assert.Contains("sql_engine_edition", sql, StringComparison.Ordinal);
        }
    }

    // ── The Overview grid is built from the catalog, not from the log ────────────────────────────

    /// <summary>
    /// The Overview grid shows every PostgreSQL collector, including the ones that wrote no
    /// <c>collection_log</c> row because dispatch filtered them out — and it says why. A grid built from the
    /// log alone would drop precisely the collector an operator most needs explained, which is the same
    /// defect as a blank tab one layer down.
    /// </summary>
    [Fact]
    public void TheOverviewGrid_ShowsAGatedOffCollector_WithTheReason()
    {
        var collectors = ViewerPostgresTabs.PostgresCollectors();
        var stock = Server(MonitoredEngineKind.Postgres);

        /* No log rows at all: the harshest case, and the one a stock PostgreSQL target actually produces
           for the two Aurora-only collectors. */
        var rows = ViewerDataService.BuildPostgresCollectorHealth(
            stock, collectors, Array.Empty<ViewerDataService.PostgresCollectorLogFacts>());

        Assert.Equal(collectors.Count, rows.Count);

        var waits = rows.Single(r => r.Collector == "pg_wait_stats");
        Assert.True(waits.IsPermanentGap);
        Assert.False(waits.IsFault);
        Assert.Contains("aurora_stat_system_waits()", waits.Explanation, StringComparison.Ordinal);
        Assert.Contains("never will", waits.Explanation, StringComparison.Ordinal);
        Assert.Contains(stock.ServerName, waits.Explanation, StringComparison.Ordinal);

        /* A collector that CAN run here and has no rows is a fault to chase, not a fact to read. The two
           states are deliberately distinct because they need different responses. */
        var xmin = rows.Single(r => r.Collector == "pg_xmin_horizon");
        Assert.False(xmin.IsPermanentGap);
        Assert.True(xmin.IsFault);
    }

    /// <summary>On AURORA the same two collectors are not a gap at all — proving the row's state is derived
    /// from the engine rather than from the collector's name.</summary>
    [Fact]
    public void TheOverviewGrid_DoesNotCallAnAuroraCollectorAGap_OnAurora()
    {
        var collectors = ViewerPostgresTabs.PostgresCollectors();
        var aurora = Server(MonitoredEngineKind.AuroraPostgres);

        var facts = new[]
        {
            new ViewerDataService.PostgresCollectorLogFacts(
                "pg_wait_stats", new DateTime(2026, 8, 22, 12, 0, 0), new DateTime(2026, 8, 22, 12, 0, 0),
                Runs: 60, FailedRuns: 0, RowsCollected: 1200, LastStatus: "SUCCESS", LastMessage: null),
        };

        var rows = ViewerDataService.BuildPostgresCollectorHealth(aurora, collectors, facts);
        var waits = rows.Single(r => r.Collector == "pg_wait_stats");

        Assert.False(waits.IsPermanentGap);
        Assert.False(waits.IsFault);
        Assert.Equal("Collecting", waits.Status);
        Assert.Equal(60, waits.Runs);
        Assert.Equal(1200, waits.RowsCollected);
        Assert.Equal("", waits.Explanation);
    }

    // ── Empty-state prose ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every note element the PostgreSQL XAML declares is actually filled by the loaders, and every panel
    /// collector goes through <c>PanelNote</c> — the one place that asks
    /// <see cref="CollectorEngineCapability.NotCollectedMessage"/> first. A panel whose note is never
    /// assigned is a blank rectangle with no explanation, which is the defect, not a cosmetic gap.
    /// </summary>
    [Fact]
    public void EveryPostgresPanel_ExplainsItsOwnEmptyState()
    {
        var xaml = ViewerFile("ViewerServerTab.xaml");
        var loaders = ViewerFile("ViewerServerTab.Postgres.cs");

        var notes = Regex
            .Matches(xaml, "x:Name=\"(Pg[A-Za-z]*Note)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(notes.Count >= ViewerPostgresTabs.All.Count,
            $"Only {notes.Count} PostgreSQL note elements found in the XAML; expected at least one per tab.");

        var unfilled = notes.Where(n => !loaders.Contains($"{n}.Text", StringComparison.Ordinal)).ToList();
        Assert.True(unfilled.Count == 0,
            "PostgreSQL panel note(s) are declared in the XAML and never filled, so those panels can be " +
            "blank with no explanation: " + string.Join(", ", unfilled));

        var unasked = ViewerPostgresTabs.All
            .SelectMany(t => t.Collectors)
            .Distinct(StringComparer.Ordinal)
            .Where(c => !loaders.Contains($"\"{c}\"", StringComparison.Ordinal))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        Assert.True(unasked.Count == 0,
            "Panel collector(s) are never named in the loaders, so their panel never asks the capability " +
            "question and cannot print the engine sentence: " + string.Join(", ", unasked));
    }

    // ── Display projections: the sentinels never reach a cell ────────────────────────────────────

    /// <summary>
    /// The store's <c>-1</c> not-applicable sentinel never renders as a number. It is <c>-1</c> and not
    /// <c>0</c> precisely because <c>0</c> would read as a measurement — "started this instant", "retains
    /// nothing" — and printing <c>-1</c> in a grid makes exactly that mistake one step later.
    /// </summary>
    [Fact]
    public void TheNotApplicableSentinel_NeverRendersAsANumber()
    {
        Assert.Equal(PgDisplay.NotApplicableText, PgDisplay.Bytes(-1));
        Assert.Equal(PgDisplay.NotApplicableText, PgDisplay.Count(-1));
        Assert.Equal(PgDisplay.NotApplicableText, PgDisplay.Milliseconds(-1));
        Assert.Equal(PgDisplay.NotApplicableText, PgDisplay.ByteDelta(-1, 4096));
        Assert.Equal(PgDisplay.NotApplicableText, PgDisplay.ByteDelta(4096, -1));

        /* Zero is a real measurement and must NOT be swallowed by the same guard. */
        Assert.Equal("0 B", PgDisplay.Bytes(0));
        Assert.Equal("0", PgDisplay.Count(0));
        Assert.Equal("0 ms", PgDisplay.Milliseconds(0));
    }

    /// <summary>
    /// A size never renders expressed in its own next unit. The unit is chosen against the quotient and the
    /// quotient is then ROUNDED, so one byte short of a megabyte would otherwise print "1024.0 KB" — a
    /// number that reads as a typo rather than as a size, on every column built over bytes.
    /// </summary>
    [Fact]
    public void ASize_NeverRendersExpressedInItsOwnNextUnit()
    {
        var boundaries = new[]
        {
            1024L * 1024 - 1,                      // one byte short of 1 MB
            1024L * 1024 * 1024 - 1,               // one byte short of 1 GB
            1024L * 1024 * 1024 * 1024 - 1,        // one byte short of 1 TB
        };

        foreach (var value in boundaries)
        {
            Assert.DoesNotContain("1024", PgDisplay.Bytes(value), StringComparison.Ordinal);
        }

        /* The exact boundaries and the last value below them still read correctly — a guard that fixed the
           rounding by breaking the ordinary case would be worse than the defect. */
        Assert.Equal("1,023 B", PgDisplay.Bytes(1023));
        Assert.StartsWith("1.0 KB", PgDisplay.Bytes(1024), StringComparison.Ordinal);
        Assert.StartsWith("1.0 MB", PgDisplay.Bytes(1024 * 1024), StringComparison.Ordinal);
        Assert.StartsWith("1.0 MB", PgDisplay.Bytes(1024L * 1024 - 1), StringComparison.Ordinal);
    }

    /// <summary>
    /// In every PostgreSQL row style, the LEAST severe highlight is declared first.
    ///
    /// <para>WPF applies the LAST matching <c>DataTrigger</c> when several set the same property, and these
    /// flags are not exclusive — an invalidated replication slot is usually also inactive, because its
    /// subscriber has already gone. Declaring the informational amber last paints it over the red on exactly
    /// the rows that need the red, which understates severity to someone scanning row colours. Asserted as a
    /// rule over every row style on these tabs rather than fixed on the one grid where review found it.</para>
    /// </summary>
    [Fact]
    public void EveryPostgresRowStyle_DeclaresItsSeverestHighlightLast()
    {
        var xaml = ViewerFile("ViewerServerTab.xaml");
        var pgRegion = xaml[xaml.IndexOf("<TabItem x:Name=\"PgOverviewTab\"", StringComparison.Ordinal)..];

        /* 2 = act on this, 1 = read this. A flag with no entry fails the scan rather than being skipped:
           an unranked highlight is one nobody decided the severity of. */
        var severity = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["IsFault"] = 2,
            ["IsInvalidated"] = 2,
            ["AutovacuumDisabled"] = 2,
            ["IsPermanentGap"] = 1,
            ["IsInactive"] = 1,
        };

        var styles = Regex.Matches(pgRegion, "<Style.Triggers>(.*?)</Style.Triggers>", RegexOptions.Singleline);
        Assert.True(styles.Count >= 3,
            $"Only {styles.Count} PostgreSQL row styles found; the scan is not matching the XAML's shape.");

        var problems = new List<string>();
        foreach (Match style in styles)
        {
            var flags = Regex
                .Matches(style.Groups[1].Value, "DataTrigger Binding=\"\\{Binding (\\w+)\\}\"")
                .Select(m => m.Groups[1].Value)
                .ToList();

            foreach (var flag in flags.Where(f => !severity.ContainsKey(f)))
            {
                problems.Add($"'{flag}' has no severity ranking — add one rather than leaving it unordered");
            }

            for (var i = 1; i < flags.Count; i++)
            {
                if (severity.TryGetValue(flags[i - 1], out var previous) &&
                    severity.TryGetValue(flags[i], out var current) &&
                    current < previous)
                {
                    problems.Add($"'{flags[i]}' (severity {current}) is declared after '{flags[i - 1]}' " +
                                 $"(severity {previous}); WPF applies the LAST match, so the milder state wins");
                }
            }
        }

        Assert.True(problems.Count == 0,
            "PostgreSQL row highlight(s) declared in the wrong order:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>A percentage of nothing is not 0%. Dividing by a zero denominator has no answer, and "0%"
    /// is a claim about a ratio that was never computable.</summary>
    [Fact]
    public void APercentageOfNothing_IsNotZeroPercent()
    {
        Assert.Equal(PgDisplay.NotApplicableText, PgDisplay.Percent(0, 0));
        Assert.StartsWith("0", PgDisplay.Percent(0, 100), StringComparison.Ordinal);
    }

    /// <summary>
    /// Aurora reports NULL across the whole <c>pg_stat_io</c> write side, because backends there do not
    /// write data files. The read carries that as a flag so the cell can say "not tracked"; a 0 would claim
    /// a measurement nobody took.
    /// </summary>
    [Fact]
    public void AnUntrackedIoCounter_SaysSo_RatherThanReadingZero()
    {
        var aurora = PgDisplay.Io(new PerformanceMonitor.Darling.Storage.DarlingPgIoReader.PgIoRow(
            "client backend", "relation", "normal", Reads: 10, ReadTimeMs: 1.0, Hits: 5, Extends: 0,
            ExtendTimeMs: 0, Evictions: 0, Reuses: 0, Writes: 0, WriteTimeMs: 0, OpBytes: 8192,
            WriteCountersTracked: false, StatsReset: null), totalReadTimeMs: 1.0);

        Assert.Equal("not tracked", aurora.Writes);
        Assert.Equal("not tracked", aurora.WriteTimeMs);

        var selfManaged = PgDisplay.Io(new PerformanceMonitor.Darling.Storage.DarlingPgIoReader.PgIoRow(
            "client backend", "relation", "normal", Reads: 10, ReadTimeMs: 1.0, Hits: 5, Extends: 0,
            ExtendTimeMs: 0, Evictions: 0, Reuses: 0, Writes: 0, WriteTimeMs: 0, OpBytes: 8192,
            WriteCountersTracked: true, StatsReset: null), totalReadTimeMs: 1.0);

        Assert.Equal("0", selfManaged.Writes);
    }

    // ── Nothing the reader returns is dropped on the floor ───────────────────────────────────────

    /// <summary>
    /// Every field the shared PostgreSQL readers return reaches a viewer grid column, is FOLDED into one
    /// with a stated reason, or the pin fails naming it.
    ///
    /// <para><b>Why this exists as a category rather than as two fixes.</b> Review found two instances of
    /// the same defect in one PR — the replication grid silently dropped <c>Conflicting</c> (a broken
    /// logical slot, invisible on the desktop while the web showed it) and the I/O grid dropped
    /// <c>ExtendTimeMs</c> plus three derived columns. Two instances, one category: a stored field the
    /// reader goes to the trouble of returning and no screen shows. Fixing the instances would have left
    /// the third one to be found by someone else, so the invariant is asserted instead — a projection is
    /// only allowed to omit a field by SAYING SO here, and the saying-so has to name a real field.</para>
    ///
    /// <para>Derived from the shipped record types by reflection, both directions: a field added to a
    /// reader turns this red until a column or an exemption appears, and an exemption for a field that no
    /// longer exists is deleted the moment the reader stops returning it.</para>
    /// </summary>
    [Fact]
    public void EveryFieldTheSharedReadersReturn_ReachesAColumn_OrIsFoldedWithAReason()
    {
        var missing = new List<string>();

        foreach (var (readerRow, displayRow) in ProjectionPairs())
        {
            var shown = displayRow
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var field in readerRow.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var key = $"{readerRow.Name}.{field.Name}";
                if (shown.Contains(field.Name) || FoldedFields.ContainsKey(key))
                {
                    continue;
                }

                missing.Add(key);
            }
        }

        Assert.True(missing.Count == 0,
            "Stored PostgreSQL field(s) the shared reader returns reach no viewer column and are not " +
            "recorded as folded. Add a column, or add an entry to FoldedFields saying which column carries " +
            "the fact and why that is the better shape:\n  " +
            string.Join("\n  ", missing.OrderBy(m => m, StringComparer.Ordinal)));
    }

    /// <summary>The ratchet's other half: an exemption may only name a field that still exists, so a
    /// renamed or removed reader field cannot leave a stale excuse behind that quietly covers its
    /// replacement.</summary>
    [Fact]
    public void EveryFoldedField_StillExists_AndSaysWhy()
    {
        var real = ProjectionPairs()
            .SelectMany(pair => pair.Reader
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => $"{pair.Reader.Name}.{p.Name}"))
            .ToHashSet(StringComparer.Ordinal);

        var stale = FoldedFields.Keys.Where(k => !real.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "FoldedFields names field(s) no shared reader returns any more — delete them: " +
            string.Join(", ", stale));

        var unexplained = FoldedFields
            .Where(kv => string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => kv.Key)
            .ToList();
        Assert.True(unexplained.Count == 0,
            "FoldedFields entr(y/ies) with no reason: " + string.Join(", ", unexplained));
    }

    /// <summary>
    /// Reader row type -> the display class that renders it. Named explicitly rather than matched by
    /// convention, because a naming rule would silently pass a projection that was never wired up.
    /// </summary>
    private static IReadOnlyList<(Type Reader, Type Display)> ProjectionPairs() => new[]
    {
        (typeof(DarlingPgXminReader.PgXminRow), typeof(PgDisplay.XminRow)),
        (typeof(DarlingPgAutovacuumReader.PgAutovacuumRow), typeof(PgDisplay.AutovacuumRow)),
        (typeof(DarlingPgWraparoundReader.PgWraparoundRow), typeof(PgDisplay.WraparoundRow)),
        (typeof(DarlingPgWaitReader.PgWaitRow), typeof(PgDisplay.WaitRow)),
        (typeof(DarlingPgIoReader.PgIoRow), typeof(PgDisplay.IoRow)),
        (typeof(DarlingPgSlotReader.PgSlotRow), typeof(PgDisplay.SlotRow)),
        (typeof(DarlingPgBlockingReader.PgBlockingChainRow), typeof(PgDisplay.ChainRow)),
        (typeof(DarlingPgBlockingReader.PgBlockingCycleRow), typeof(PgDisplay.CycleRow)),
        (typeof(DarlingPgStatementReader.PgStatementRow), typeof(PgDisplay.StatementRow)),
        (typeof(DarlingPgDatabaseReader.PgDatabaseRow), typeof(PgDisplay.DatabaseRow)),
    };

    /// <summary>
    /// Fields a projection deliberately does not give a column of their own, and the column that carries
    /// the fact instead. Every entry is a fold, never a drop: the fact still reaches the operator, in a
    /// shape that reads better than the raw field would.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FoldedFields =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PgXminRow.IsWinner"] = "IsWinnerText — rendered as a word rather than True/False",
            ["PgXminRow.SamplesAsWinner"] = "WinnerShare — '58 of 60 samples' says more than either half alone",
            ["PgXminRow.Samples"] = "WinnerShare — it is that fraction's denominator",

            ["PgAutovacuumRow.FirstDeadTuples"] = "DeadTupleTrend — the window's start, as a signed delta",
            ["PgAutovacuumRow.FirstSeenAt"] = "DeadTupleTrend — the delta's other end; the window is the toolbar's",
            ["PgAutovacuumRow.TotalBytes"] = "TotalSize — the same number, formatted",

            ["PgWraparoundRow.AllowsConnections"] = "ConnectionsAllowed — as words, because NOT ALLOWED is the finding",
            ["PgWraparoundRow.AutovacuumFreezeMaxAge"] = "FreezeMaxAge — the same setting, without the column's prefix",
            ["PgWraparoundRow.AutovacuumMultixactFreezeMaxAge"] = "MultixactFreezeMaxAge — same",

            ["PgIoRow.WriteCountersTracked"] = "Writes / WriteTimeMs — they read 'not tracked' rather than 0",
            ["PgIoRow.OpBytes"] = "OpSize — the same number, formatted",

            ["PgSlotRow.FirstRetainedWalBytes"] = "RetainedWalTrend — the window's start, as a signed delta",
            ["PgSlotRow.FirstSeenAt"] = "RetainedWalTrend — the delta's other end; the window is the toolbar's",
            ["PgSlotRow.IsActive"] = "ActiveText, and the inactive row highlight",
            ["PgSlotRow.RetainedWalBytes"] = "RetainedWal — the same number, formatted",
            ["PgSlotRow.SafeWalSizeBytes"] = "SafeWalSize — the same number, formatted",

            ["PgBlockingChainRow.MaxDepth"] = "Depth — carries the '(capped)' marker with the number",
            ["PgBlockingChainRow.ChainMayBeTruncated"] = "Depth's '(capped)' marker, and Caveats",
            ["PgBlockingChainRow.QueryTextMayBeTruncated"] = "Caveats — one column for every qualifier on the row",
            ["PgBlockingChainRow.RootIsIdleInTransaction"] = "RootState — 'idle in transaction' replaces the raw state",
            ["PgBlockingChainRow.WorstVictimWaitMs"] = "WorstVictimWait — the -1 sentinel must not render as a number",
            ["PgBlockingChainRow.RootXactDurationMs"] = "RootXactDuration — same sentinel handling",
            ["PgBlockingChainRow.RootQueryDurationMs"] =
                "RootXactDuration — the TRANSACTION's age is what holds the lock; a root idle in transaction "
                + "has no query duration at all, so showing both invites reading the wrong one",

            ["PgStatementRow.SharedBlocksHit"] = "CacheHitPct — one ratio over both cache tiers and both miss sources",
            ["PgStatementRow.OrcacheBlocksHit"] = "CacheHitPct — Aurora's Optimized Read cache, same ratio",
            ["PgStatementRow.SharedBlocksRead"] = "CacheHitPct — the miss side of that ratio",
            ["PgStatementRow.StorageBlocksRead"] = "CacheHitPct — Aurora's storage-layer miss, same ratio",
            ["PgStatementRow.TempBlocksRead"] = "TempRead — labelled in blocks, not converted to bytes",
            ["PgStatementRow.TempBlocksWritten"] = "TempWritten — labelled in blocks, not converted to bytes",
            ["PgStatementRow.WalBytes"] = "WalWritten — the same number, formatted",
            ["PgStatementRow.MaxPeakMemBytes"] = "PeakMemory — the same number, formatted",
            ["PgStatementRow.TotalExecTimeMs"] = "TotalExecTimeMs is shown; AvgExecTimeMs derives from it and Calls",

            ["PgDatabaseRow.BlksHit"] = "CacheHitPct — the hit side of that ratio",
            ["PgDatabaseRow.BlksRead"] = "CacheHitPct — the miss side of that ratio",
            ["PgDatabaseRow.StatsResetCount"] = "Caveats — 'counters reset Nx in window', which is what it qualifies",
            ["PgDatabaseRow.CounterRewindCount"] = "Caveats — the implicit-reset half of the same qualifier",
            ["PgDatabaseRow.FirstSampleAt"] =
                "SampleCount — the window's bounds are the toolbar's own and are shown there, so repeating "
                + "them per row would be the same two timestamps on every line",
            ["PgDatabaseRow.LastSampleAt"] = "SampleCount — the other end of that same window",
        };

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static DarlingServer Server(string? engineKind) =>
        new(1, "alpha-pg-01", "alpha", isEnabled: true, sqlMajorVersion: null, monthlyCostUsd: 0m,
            engineKind: engineKind, engineEdition: CollectorEngineCapability.UnknownEngineEdition);

    private static string Unescape(string xmlAttributeValue) =>
        xmlAttributeValue.Replace("&amp;", "&", StringComparison.Ordinal);

    private static string ViewerFile(string fileName, [CallerFilePath] string thisFile = "")
    {
        var path = Path.Combine(ViewerDir(thisFile), fileName);
        Assert.True(File.Exists(path), $"Expected the viewer source {fileName} at {path}.");
        return File.ReadAllText(path);
    }

    private static IEnumerable<string> ViewerFiles(string filePrefix, [CallerFilePath] string thisFile = "") =>
        Directory
            .EnumerateFiles(ViewerDir(thisFile), "*.cs", SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetFileName(f).StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase));

    private static string ViewerDir(string thisFile) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "PerformanceMonitor.Darling.Viewer"));
}
