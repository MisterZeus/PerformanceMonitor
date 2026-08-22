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
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// The web server page's sub-tabs — the port of the desktop viewer's per-server TabControl.
///
/// <para><b>The invariant these pins exist for.</b> A panel descriptor names its data source as a STRING
/// (<c>read: "get_wait_stats"</c>). A typo, or a read renamed on the C# side, produces a 400 inside one panel at
/// runtime and is completely invisible to inspection — the JS still parses, the page still renders, one panel
/// just says "unknown". With ~60 reads named across twelve tabs that is not a defect you find by reading. So
/// rather than pinning individual panels, this asserts the CATEGORY: every read name the shipped module mentions
/// exists in the shipped dispatch table, and every viz it names exists in the shipped viz vocabulary. Both sides
/// come from the artifacts themselves, never a transcribed copy, so the check cannot drift into agreeing with a
/// stale list of its own.</para>
///
/// <para>This repository carries no JavaScript test runner, so the scan is a text scan over the shipped module
/// (the <see cref="FleetPageAttentionFilterTests"/> / <see cref="ViewerGridPayloadColumnOrderPinTests"/>
/// pattern). Behaviour was verified separately by running the shipped modules under a minimal DOM shim with a
/// stubbed fetch across four response shapes (empty envelope, error, data-with-no-rows, data-with-rows): every
/// tab built without throwing at three time ranges, and every request the page actually issued was checked
/// against <c>CatalogDescriptors</c> for parameter-key validity. See the PR.</para>
/// </summary>
public sealed class ServerPageTabsTests
{
    private static string ServerTabsJs => ReadRepoFile(Path.Combine(
        "Darling", "PerformanceMonitor.Darling.Service", "wwwroot", "js", "pages", "server-tabs.js"));

    private static string ServerJs => ReadRepoFile(Path.Combine(
        "Darling", "PerformanceMonitor.Darling.Service", "wwwroot", "js", "pages", "server.js"));

    private static string AppJs => ReadRepoFile(Path.Combine(
        "Darling", "PerformanceMonitor.Darling.Service", "wwwroot", "js", "app.js"));

    /// <summary>
    /// Every read name the tab module mentions is a read the service actually serves.
    ///
    /// <para>The scan is deliberately over the whole file rather than over parsed descriptors: read names live in
    /// the <c>get_*</c> namespace and nothing else in this module does, so a literal in a comment is caught too —
    /// which is the point. A comment that names a read the dispatch no longer has is stale documentation about
    /// the one thing here that is impossible to verify by eye.</para>
    /// </summary>
    [Fact]
    public void EveryReadTheServerPageNames_ExistsInTheDispatch()
    {
        var dispatch = DarlingWebEndpoints.BuildReadDispatch().Keys.ToHashSet(StringComparer.Ordinal);
        var named = ReadNamesIn(ServerTabsJs);

        Assert.NotEmpty(named);

        var unknown = named.Where(n => !dispatch.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.True(
            unknown.Length == 0,
            "server-tabs.js names reads that GET /api/read/{name} does not serve — each renders as a broken " +
            "panel at runtime and looks fine on inspection: " + string.Join(", ", unknown));
    }

    /// <summary>
    /// Every parameter key the page sends is one its read actually binds.
    ///
    /// <para>This is the half that fails SILENTLY rather than loudly: an unknown query key is ignored, so a panel
    /// asking <c>limit=10</c> of a read that binds <c>top</c> quietly returns the read's default 20 rows and
    /// nothing anywhere says so. <c>CatalogDescriptors</c> is the authority for a read's real wire keys (the
    /// dispatch lambdas bind string literals imperatively, so the C# parameter names are not those keys), and the
    /// two documented aliases the dispatch also accepts are allowed explicitly rather than by accident.</para>
    /// </summary>
    [Fact]
    public void EveryParameterKeyTheServerPageSends_IsOneItsReadBinds()
    {
        var js = ServerTabsJs;
        var problems = new List<string>();

        foreach (var (read, keys) in ParamsSentIn(js))
        {
            if (!DarlingWebEndpoints.CatalogDescriptors.TryGetValue(read, out var descriptor))
            {
                continue; // the read-existence pin above owns this failure and names it better.
            }

            var allowed = descriptor.Params.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            /* The dispatch's two documented aliases: Hours() reads ?hours= then ?hours_back=, and Server() reads
               ?server= then ?server_name=. Named here rather than assumed, so removing one breaks this test. */
            if (allowed.Contains("hours")) allowed.Add("hours_back");
            if (allowed.Contains("server")) allowed.Add("server_name");

            foreach (var key in keys.Where(k => !allowed.Contains(k)))
            {
                problems.Add($"{read} is sent '{key}' but binds only [{string.Join(", ", allowed.OrderBy(a => a, StringComparer.Ordinal))}]");
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// Every REQUIRED parameter of a read the page fetches is one the page actually sends.
    ///
    /// <para>The pin above is the other half of this, and on its own it cannot see the failure that matters here.
    /// It asks whether every key sent is bound; a read fetched with NO key at all sends nothing wrong and passes
    /// it vacuously. Four reads in the catalog carry required params — <c>get_wait_trend</c>,
    /// <c>get_perfmon_trend</c>, <c>get_plan_xml</c> and <c>get_query_trend</c> — and every one of them answers a
    /// request that omits its key with a 400 inside one panel, which is exactly the shape of failure this class
    /// exists to catch by inspection rather than at runtime.</para>
    ///
    /// <para>Written when #2520 put the first two-required-key read on the page: <c>get_query_trend</c> needs a
    /// <c>query_hash</c> AND a <c>database_name</c>, so a drill-down that wired up one of them would look
    /// finished, parse, render its picker, and return a 400 on every selection. Both sides are derived —
    /// <c>CatalogDescriptors</c> for what is required, the shipped module for what is sent — so neither can
    /// drift into agreeing with a stale copy of the other.</para>
    /// </summary>
    [Fact]
    public void EveryRequiredParameter_OfAReadThePageFetches_IsSent()
    {
        var problems = new List<string>();

        foreach (var (read, keys) in ParamsSentIn(ServerTabsJs))
        {
            if (!DarlingWebEndpoints.CatalogDescriptors.TryGetValue(read, out var descriptor))
            {
                continue; // the read-existence pin owns this failure and names it better.
            }

            var sent = keys.ToHashSet(StringComparer.Ordinal);
            foreach (var missing in descriptor.Params.Where(p => p.Required && !sent.Contains(p.Name)))
            {
                problems.Add($"{read} is fetched without its required '{missing.Name}' — that panel 400s at runtime");
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));

        /* And the guard is only worth having if some read on the page actually has a required param — otherwise
           it passes for the wrong reason and would keep passing after the drill-down was deleted. */
        var required = ParamsSentIn(ServerTabsJs)
            .Where(p => DarlingWebEndpoints.CatalogDescriptors.ContainsKey(p.Read))
            .SelectMany(p => DarlingWebEndpoints.CatalogDescriptors[p.Read].Params.Where(x => x.Required))
            .ToArray();
        Assert.NotEmpty(required);
    }

    /// <summary>
    /// The per-query drill-down offers exactly the queries the table above it shows (#2520).
    ///
    /// <para><c>get_query_trend</c> was the one read in the catalog whose absence from the web was missing UI
    /// rather than a stated boundary: it keys on a required <c>query_hash</c> plus a required
    /// <c>database_name</c>, every other panel on this page fetches with nothing but a server and a window, so
    /// there was no query_hash anywhere on the surface to send. The Queries tab's Top Queries table now carries
    /// a picker, in the shape the Wait Stats tab already established.</para>
    ///
    /// <para><b>The rule this pins is the one waitsPanel wrote down.</b> <c>get_wait_types</c> is deliberately
    /// not read there because it returns the full distinct set and would offer wait types absent from the
    /// table, making the two disagree. The same rule binds here, and it is enforced by construction rather than
    /// by care: the picker's option VALUE is the row's index into the very array the table rendered, so the
    /// query trended is the same array element the reader is looking at — not a matching name that a second,
    /// broader read happened to supply. So the composite is asserted to fetch exactly two reads: the table's,
    /// and the trend. A third would be the "list every query" read this design exists to refuse.</para>
    /// </summary>
    [Fact]
    public void TheQueryDrillDown_OffersOnlyTheQueriesTheTableAboveItShows()
    {
        var js = ServerTabsJs;

        /* The composite spans from its own definition to pickerControl's doc comment, which follows it. */
        var at = js.IndexOf("export function topQueriesPanel", StringComparison.Ordinal);
        Assert.True(at > 0, "topQueriesPanel is gone — remap this test before editing it");
        var end = js.IndexOf("/**\n * A labelled <select>", at, StringComparison.Ordinal);
        Assert.True(end > at, "pickerControl no longer follows the composite — remap this test before editing it");
        var region = js[at..end];

        Assert.Equal(
            new[] { "get_query_trend", "get_top_queries_by_cpu" },
            ReadNamesIn(region).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        /* The picker is seeded from THAT payload's rows, and an option's value indexes into them. This is the
           mechanism, not a paraphrase of it: replace either line with a second read and the assert above goes
           red, replace the index with a name lookup and these do. */
        Assert.Contains("const queries = res.data.queries || [];", region, StringComparison.Ordinal);
        Assert.Contains("if (q.query_hash && q.database_name) trendable.push({ rank: i + 1, query: q });", region, StringComparison.Ordinal);
        Assert.Contains("value: String(i),", region, StringComparison.Ordinal);
        Assert.Contains("trendable[Number(i)].query", region, StringComparison.Ordinal);

        /* And both required keys come off the selected row rather than from anywhere else. */
        Assert.Contains("query_hash: query.query_hash,", region, StringComparison.Ordinal);
        Assert.Contains("database_name: query.database_name,", region, StringComparison.Ordinal);

        /* The Queries tab reaches it, and get_wait_types stays unread for the reason waitsPanel gives. */
        Assert.Contains("topQueriesPanel(server, ctx),", js, StringComparison.Ordinal);
        Assert.DoesNotContain("\"get_wait_types\"", js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page speaks the catalog's canonical time key. Both <c>hours</c> and <c>hours_back</c> reach the same
    /// binding, so this is a consistency rule rather than a correctness one — but it is the rule that keeps the
    /// pin above meaningful: with two spellings in play, a reviewer cannot tell a deliberate alias from a typo
    /// that happened to land on the alias.
    /// </summary>
    [Fact]
    public void TheServerPage_UsesTheCatalogsCanonicalTimeKey()
    {
        Assert.DoesNotContain("hours_back:", ServerTabsJs, StringComparison.Ordinal);
        Assert.Contains("hours: ctx.hours", ServerTabsJs, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every viz a descriptor names is in the shipped vocabulary. The four kinds are the whole registry; a fifth
    /// would have to be added to panels.js's VIZ, to <c>KnownVizList</c> (or the composer could not offer it), to
    /// derive.js's <c>deriveVizConfig</c> and to the editor's config arms — so a page quietly introducing one
    /// would be a page-only special case, which is exactly what the seam exists to prevent.
    /// </summary>
    [Fact]
    public void EveryVizTheServerPageNames_IsInTheShippedVocabulary()
    {
        var vocabulary = DarlingWebEndpoints.KnownVizList.ToHashSet(StringComparer.Ordinal);
        var named = Regex.Matches(ServerTabsJs, "viz:\\s*\"([a-z]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(named);
        Assert.All(named, v => Assert.Contains(v, vocabulary));

        /* And the registry in panels.js is that same vocabulary — the C# validator and the browser renderer
           agreeing is what lets a stored view and a built-in page share one seam. */
        var panels = ReadRepoFile(Path.Combine(
            "Darling", "PerformanceMonitor.Darling.Service", "wwwroot", "js", "panels.js"));
        foreach (var v in vocabulary)
        {
            Assert.Contains("  " + v + ": viz", panels, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// No PostgreSQL read appears on the server page, and this is a guard rather than an oversight.
    ///
    /// <para>The web dashboard's per-server surface is fed by <c>/api/fleet</c>, whose card
    /// (<c>DarlingFleetReader.FleetServerCard</c>) carries <c>engine_edition</c> — the SQL Server
    /// SERVERPROPERTY value — and NO <c>CollectorTargetEngine</c> discriminator. So the browser cannot tell a
    /// PostgreSQL target from a SQL Server one, and a <c>get_pg_*</c> panel added to these tabs would render for
    /// every server in the fleet, permanently empty on the ~all of them that are SQL Server. Adding the
    /// discriminator to the fleet payload is the prerequisite; this fails until then rather than letting the
    /// panel ship first.</para>
    /// </summary>
    [Fact]
    public void NoPostgresRead_IsOnTheServerPage_UntilTheFleetPayloadCanTellTheEngines()
    {
        var pg = ReadNamesIn(ServerTabsJs).Where(n => n.StartsWith("get_pg_", StringComparison.Ordinal)).ToArray();
        Assert.True(
            pg.Length == 0,
            "the fleet payload carries no target-engine discriminator, so these would render on every SQL Server " +
            "too: " + string.Join(", ", pg));
    }

    /// <summary>
    /// The shell links only to tabs that exist, and every tab is reachable. A sub-tab link is a real href, so a
    /// stale id is a page that renders Overview while claiming to be something else — the fallback that makes old
    /// <c>#/server/{name}</c> links keep working is the same fallback that would hide this.
    /// </summary>
    [Fact]
    public void TheSubTabBar_IsBuiltFromTheRegistry_AndTheRouterCarriesTheTab()
    {
        /* One source of truth for the bar: it maps the registry rather than listing ids. */
        Assert.Contains("SERVER_TABS.map((t) =>", ServerJs, StringComparison.Ordinal);
        Assert.Contains("\"#/server/\" + encodeURIComponent(server) + \"/\" + t.id", ServerJs, StringComparison.Ordinal);

        /* And the router parses that second segment back out and hands it to the page. */
        Assert.Contains("function serverRoute(rest)", AppJs, StringComparison.Ordinal);
        Assert.Contains("renderServer(main, r.param, r.tab)", AppJs, StringComparison.Ordinal);

        /* The name is decoded AFTER the split, so an encoded '/' inside a server name survives the tab segment
           being introduced — the one way this change could have broken existing links. */
        Assert.Contains("decodeURIComponent(rest.slice(0, slash))", AppJs, StringComparison.Ordinal);
        Assert.Contains("if (slash < 0) return { name: \"server\", param: decodeURIComponent(rest) };", AppJs, StringComparison.Ordinal);

        /* An unknown id resolves to a tab rather than throwing, which is what keeps a stale bookmark working. */
        Assert.Contains("SERVER_TABS.find((t) => t.id === id) || SERVER_TABS[0]", ServerTabsJs, StringComparison.Ordinal);

        /* Ids are unique — two tabs sharing one id makes the second unreachable and the bar's active state lie. */
        var ids = Regex.Matches(ServerTabsJs, "^\\s{4}id: \"([a-z-]+)\",$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToArray();
        /* An exact count, not a floor. A floor would have let the prose in the CHANGELOG, the commit and this
           file drift from the registry — which it did, at "eleven" against twelve, before this pin existed. */
        Assert.Equal(12, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("overview", ids[0]); // the fallback tab must be the first one
    }

    /// <summary>
    /// The tabs whose desktop twin does something the browser cannot say so, in the tab itself.
    ///
    /// <para>Plan analysis, the query heatmap, cached-plan retrieval and actual-plan re-execution all need either
    /// a plan renderer or a command back to the monitored server, and this read-only seat has neither. The same
    /// goes for the block-chain view and the interactive deadlock graph. A reader told to open the desktop viewer
    /// is better served than one given a web page that looks like plan analysis and is not — so the absence is
    /// stated where they go looking for it, not left as a page that simply lacks the feature.</para>
    /// </summary>
    [Fact]
    public void TheTabsThatCannotDoWhatTheDesktopDoes_SaySo()
    {
        var js = ServerTabsJs;

        Assert.Contains("desktop-viewer features", js, StringComparison.Ordinal);
        Assert.Contains("Execution-plan analysis, the query heatmap", js, StringComparison.Ordinal);
        Assert.Contains("block-chain view", js, StringComparison.Ordinal);

        /* The note renders — a `note` field with no renderer is the same silence in a different place. */
        Assert.Contains("return tab.note ? noticeStrip(tab.note) : null;", js, StringComparison.Ordinal);
        Assert.Contains("tabNote(tab)", ServerJs, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every data panel says why it could be empty — charts as well as tables.
    ///
    /// <para>renderPanel already shows a read's own <c>{status,message}</c> envelope when the read has nothing,
    /// and that sentence is better than anything a descriptor could carry. What it does NOT cover is the read
    /// returning DATA whose row array is empty, and both renderers had a wrong generic for that case. vizTable
    /// falls back to "No rows in this window", which on a collector that is off, opt-in, or daily reads as a
    /// fault. vizLine fell through to the chart's "Not enough data points to chart yet", which is right while
    /// collection is warming up and wrong for a read whose empty array means the thing did not happen:
    /// <c>get_blocking_trend</c> and <c>get_deadlock_trend</c> used to answer an idle server with <c>trend: []</c>
    /// and no envelope at all, so a healthy server was told its blocking chart was still warming up. Those two
    /// now carry an envelope of their own (#2485) and are handled a layer above this guard; the guard still
    /// stands, because every other line read on these tabs has no envelope and lands here at zero rows.</para>
    ///
    /// <para>Both helpers THROW without a sentence, and every tab is built during the DOM-shim run, so a panel
    /// that forgot one cannot reach a browser. The zero-versus-one distinction was verified against the shipped
    /// vizLine: zero points with an emptyText renders the descriptor's sentence, one point still renders the
    /// chart's own (which is the true statement there), and zero points WITHOUT one still falls through — so a
    /// stored view authored before this existed is unchanged.</para>
    /// </summary>
    [Fact]
    public void EveryDataPanel_ExplainsItsOwnEmptyState()
    {
        var js = ServerTabsJs;

        /* Not a count of sentences — a structural guard. Counting "No ..." literals would pass vacuously the
           moment a comment happened to contain one, which is the shape of check that converts an open question
           into false confidence. The helper THROWS without an emptyText, and every tab is built during the
           DOM-shim run, so a table panel that forgot one cannot reach a browser. */
        Assert.Contains("function table(title, read, params, rowsKey, columns, subtitle, emptyText, span = 2)", js, StringComparison.Ordinal);
        Assert.Contains(
            "if (!emptyText) throw new Error(\"table(\" + title + \"): a table panel must explain its own empty state.\");",
            js,
            StringComparison.Ordinal);

        Assert.Contains("function line(title, read, params, rowsKey, xKey, series, opts = {})", js, StringComparison.Ordinal);
        Assert.Contains(
            "if (!opts.emptyText) throw new Error(\"line(\" + title + \"): a chart panel must explain its own empty state.\");",
            js,
            StringComparison.Ordinal);

        /* And renderPanel is what renders both, from the descriptor field the helpers set. The line guard fires
           at EXACTLY zero rows: at one row the chart's own sentence is the true one, and a descriptor that never
           had an emptyText (every stored view authored before this) still falls through unchanged. */
        var panels = ReadRepoFile(Path.Combine(
            "Darling", "PerformanceMonitor.Darling.Service", "wwwroot", "js", "panels.js"));
        Assert.Contains("desc.emptyText || \"No rows in this window.\"", panels, StringComparison.Ordinal);
        Assert.Contains("if (!points.length && desc.emptyText) return emptyStrip(desc.emptyText);", panels, StringComparison.Ordinal);
    }

    /// <summary>
    /// No tab fetches the same read twice.
    ///
    /// <para>A descriptor owning its own fetch is the right default and is what makes the seam composable — but
    /// <c>readTool</c>/<c>apiGet</c> have no cache, so a read feeding two or three panels on ONE tab ran two or
    /// three times. Review caught two; there were six, and the worst was <c>get_collection_health</c>, which
    /// rolls up seven days of collector logs and computes sweep pressure, rendered as three slices of one
    /// payload — so opening that tab ran the page's heaviest query three times. <c>fanout()</c> is the fix, and
    /// this is the guard, because "fix the two review named" is how the other four ship.</para>
    ///
    /// <para>Composites name their reads inside their own function bodies rather than in a tab, so their reads
    /// are mapped here explicitly — and the map is asserted against the functions, so it cannot quietly go
    /// stale and start passing a tab it no longer describes.</para>
    /// </summary>
    [Fact]
    public void NoTab_FetchesTheSameReadTwice()
    {
        var js = ServerTabsJs;

        /* The composite -> reads map, verified against the composites themselves before it is trusted. */
        var composites = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["waitsPanel("] = new[] { "get_wait_stats", "get_wait_trend" },
            ["fileIoPanel("] = new[] { "get_file_io_trend" },
            ["perfmonPanel("] = new[] { "get_perfmon_stats", "get_perfmon_trend" },
            ["topQueriesPanel("] = new[] { "get_top_queries_by_cpu", "get_query_trend" },
        };
        foreach (var (call, reads) in composites)
        {
            var fn = "function " + call.TrimEnd('(');
            var at = js.IndexOf(fn, StringComparison.Ordinal);
            Assert.True(at > 0, "composite " + call + " is gone — remap it before editing this test");
            /* Its reads and its helpers' reads: take everything from the definition to the descriptor section. */
            var region = js[at..js.IndexOf("/* ─────────────────────────── descriptor helpers", StringComparison.Ordinal)];
            foreach (var read in reads) Assert.Contains("\"" + read + "\"", region, StringComparison.Ordinal);
        }

        var problems = new List<string>();
        var ids = Regex.Matches(js, "^    id: \"([a-z-]+)\",$", RegexOptions.Multiline).ToArray();

        for (var i = 0; i < ids.Length; i++)
        {
            var start = ids[i].Index;
            var end = i + 1 < ids.Length
                ? ids[i + 1].Index
                : js.IndexOf("/** The tab for an id", StringComparison.Ordinal);
            var block = js[start..end];

            var reads = Regex.Matches(block, "\"(get_[a-z0-9_]+|audit_config)\"").Select(m => m.Groups[1].Value).ToList();
            foreach (var (call, composed) in composites)
            {
                if (block.Contains(call, StringComparison.Ordinal)) reads.AddRange(composed);
            }

            foreach (var dupe in reads.GroupBy(r => r, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                problems.Add($"tab '{ids[i].Groups[1].Value}' fetches {dupe.Key} {dupe.Count()} times");
            }
        }

        Assert.True(problems.Count == 0,
            string.Join("; ", problems) + " — several panels over one read is what fanout() is for.");

        /* And fanout carries the same empty-state rule the two descriptor helpers do, so routing a panel through
           it is never the way to lose the sentence. */
        Assert.Contains("function fanout(read, params, specs)", js, StringComparison.Ordinal);
        Assert.Contains("a data panel must explain its own empty state.", js, StringComparison.Ordinal);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every read name the module mentions. Read names live in their own <c>get_*</c> namespace plus
    /// three one-off verbs; nothing else in this module is a string literal of that shape.</summary>
    private static HashSet<string> ReadNamesIn(string js) =>
        Regex.Matches(js, "\"(get_[a-z0-9_]+|audit_config|list_servers|compare_analysis)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The (read, param-keys) pairs the module sends. Both descriptor forms are covered: the positional helpers
    /// (<c>table("T", "read", { ... })</c>) and the direct <c>readTool("read", { ... })</c> calls in the
    /// composites. The params object is matched non-greedily up to its closing brace, which is exact here because
    /// no params object in this file nests another.
    /// </summary>
    private static IEnumerable<(string Read, string[] Keys)> ParamsSentIn(string js)
    {
        foreach (Match m in Regex.Matches(js, "\"(get_[a-z0-9_]+|audit_config)\",\\s*\\{([^{}]*)\\}", RegexOptions.Singleline))
        {
            var keys = Regex.Matches(m.Groups[2].Value, @"(?:^|[,{]\s*)([a-z_][a-z0-9_]*)\s*(?::|,|\})")
                .Select(k => k.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            yield return (m.Groups[1].Value, keys);
        }
    }

    private static string ReadRepoFile(string relative, [CallerFilePath] string thisFile = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate).Replace("\r\n", "\n", StringComparison.Ordinal);
            }
        }

        throw new FileNotFoundException($"Could not locate {relative} walking up from {thisFile}");
    }
}
