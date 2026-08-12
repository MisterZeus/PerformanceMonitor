/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Text.RegularExpressions;
using PerformanceMonitor.Darling.Service.Mcp;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the blocking read: chains assembled from the stored edge list, roots attributed, and the cycle
/// case that the chain query structurally cannot see.
///
/// <para>Two of these pin defects that live probing found and that no C# test could have caught on its own:
/// the missing <c>WITH RECURSIVE</c> (a runtime error on the first call, because PostgreSQL scopes the
/// keyword to the whole WITH clause) and the unaliased output columns. Both were correct-looking text.</para>
/// </summary>
public class DarlingPgBlockingReaderTests
{
    private static string ChainsSql => DarlingPgBlockingReader.PgBlockingChainsSql;

    private static string CyclesSql => DarlingPgBlockingReader.PgBlockingCyclesSql;

    /// <summary>
    /// <c>WITH RECURSIVE</c>, not <c>WITH</c>. PostgreSQL scopes <c>RECURSIVE</c> to the entire WITH clause
    /// rather than to the one CTE that needs it, so a self-referencing CTE behind a plain <c>WITH</c> fails
    /// with <c>relation "chain" does not exist</c> — at runtime, on the first call, never at build time.
    /// Both queries here recurse.
    /// </summary>
    [Fact]
    public void BothRecursiveQueriesDeclareWithRecursive()
    {
        Assert.StartsWith("WITH RECURSIVE", ChainsSql.TrimStart(), StringComparison.Ordinal);
        Assert.StartsWith("WITH RECURSIVE", CyclesSql.TrimStart(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A root is a backend that blocks something and is not itself blocked — found by absence, which is the
    /// only way to find it and the reason the collector stores the whole edge set per capture rather than
    /// only the pairs someone asked about.
    /// </summary>
    [Fact]
    public void FindsRootsByAbsenceOfAnUpstreamBlocker()
    {
        Assert.Contains("WHERE NOT EXISTS (", ChainsSql, StringComparison.Ordinal);
        Assert.Contains("upstream.blocked_pid = e.blocking_pid", ChainsSql, StringComparison.Ordinal);
    }

    /// <summary>
    /// BOTH recursions must be depth-capped. A cycle in the edge set would otherwise run an uncapped
    /// recursive CTE until it exhausted memory — and cycles are reachable, since PostgreSQL only resolves
    /// them after <c>deadlock_timeout</c> and a capture can land inside that window.
    /// </summary>
    [Fact]
    public void BothRecursionsAreDepthCapped()
    {
        Assert.Contains("depth < 32", ChainsSql, StringComparison.Ordinal);
        Assert.Contains("depth < 32", CyclesSql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cycle walk must also stop when it returns to where it started, not lean on the depth cap alone.
    /// The cap bounds the damage; this is what makes the query correct rather than merely finite.
    /// </summary>
    [Fact]
    public void TheCycleWalkStopsOnClosingTheLoop()
    {
        Assert.Contains("w.at_pid <> w.start_pid", CyclesSql, StringComparison.Ordinal);
        /* And the detection itself: reachable from yourself. */
        Assert.Contains("WHERE w.at_pid = w.start_pid", CyclesSql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Recurrence is counted on the synthetic backend id, NOT the pid. That is the whole reason the collector
    /// computes the id: a pid is reused, so a 30-day count keyed on it silently merges two different
    /// backends, and "the same stuck session all afternoon" and "a succession of different ones" have
    /// different remedies.
    /// </summary>
    [Fact]
    public void RecurrenceIsKeyedOnTheBackendIdNotThePid()
    {
        Assert.Contains(
            "count(DISTINCT collection_id) AS samples_as_root", ChainsSql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY blocking_backend_id", ChainsSql, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP BY blocking_pid", ChainsSql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Direct victims and total victims are different numbers and both are reported: one root blocking
    /// thirty sessions directly is a different shape of problem from a thirty-deep chain, and a single
    /// count cannot tell them apart.
    /// </summary>
    [Fact]
    public void SeparatesDirectVictimsFromTotalVictims()
    {
        Assert.Contains(
            "count(DISTINCT blocked_pid)::int AS total_victims", ChainsSql, StringComparison.Ordinal);
        Assert.Contains("FILTER (WHERE depth = 1)", ChainsSql, StringComparison.Ordinal);
        Assert.Contains("max(depth)::int AS max_depth", ChainsSql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ordered worst-first, not newest-first. Under a row limit a newest-first ordering answers a different
    /// question than the one asked, and can omit the incident entirely.
    /// </summary>
    [Fact]
    public void OrdersWorstFirstSoARowLimitCannotHideTheIncident()
    {
        Assert.Contains(
            "ORDER BY s.total_victims DESC, s.max_depth DESC, d.collection_time DESC",
            ChainsSql,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every output column of both queries carries an explicit alias. The C# readers are positional so this
    /// changes no behaviour — but three unaliased <c>coalesce()</c> expressions came back as three columns
    /// all named "coalesce", which makes the query unreadable in the one tool anyone debugging it will
    /// actually reach for.
    /// </summary>
    [Fact]
    public void EveryOutputColumnIsAliased()
    {
        foreach (var (name, sql) in new[] { ("chains", ChainsSql), ("cycles", CyclesSql) })
        {
            /* The final SELECT is the last one in the text; its items are the output columns. Any that is a
               bare function call with no AS is the hazard. */
            var lastSelect = sql.LastIndexOf("SELECT", StringComparison.Ordinal);
            Assert.True(lastSelect > 0, $"the {name} query must have a final SELECT");
            var finalSelect = sql[lastSelect..];

            Assert.False(
                Regex.IsMatch(
                    finalSelect,
                    @"^\s+(?:coalesce|count|min|max|array_agg)\(.*\)\s*,?\s*$",
                    RegexOptions.Multiline),
                $"the {name} query has an unaliased function in its output list — it comes back named after "
                + "the function, and several such columns collide into identical headings");
        }
    }

    /// <summary>
    /// The capture-count read must come from <c>collection_log</c>, not from the edge table. The edge table
    /// cannot tell "no blocking" from "not collected" — both are an absence of rows — and that distinction
    /// is the difference between an all-clear and knowing nothing at all.
    /// </summary>
    [Fact]
    public void CaptureCountsComeFromTheCollectionLog()
    {
        var sql = DarlingPgBlockingReader.PgBlockingCaptureCountsSql;

        Assert.Contains("FROM collection_log", sql, StringComparison.Ordinal);
        Assert.Contains("l.collector_name = 'pg_blocking'", sql, StringComparison.Ordinal);
        Assert.Contains("l.status = 'SUCCESS'", sql, StringComparison.Ordinal);
        /* Both halves of the denominator: captures that found blocking, and captures at all. */
        Assert.Contains("count(*) FILTER (WHERE l.rows_collected > 0)", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every parameter is positional and bound, never interpolated — including the row limit, which was a
    /// hardcoded <c>LIMIT 50</c> in an earlier PostgreSQL reader and had to be corrected.
    /// </summary>
    [Fact]
    public void TheRowLimitIsAParameter()
    {
        Assert.Contains("LIMIT $4", ChainsSql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $4", CyclesSql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both queries scope to one server and one window. A read that forgot either would silently mix
    /// servers' pids together, and a pid is only unique within one instance.
    /// </summary>
    [Fact]
    public void BothQueriesScopeToOneServerAndWindow()
    {
        foreach (var sql in new[] { ChainsSql, CyclesSql })
        {
            Assert.Contains("WHERE server_id = $1", sql, StringComparison.Ordinal);
            Assert.Contains("collection_time >= $2", sql, StringComparison.Ordinal);
            Assert.Contains("collection_time <= $3", sql, StringComparison.Ordinal);
        }
    }
}
