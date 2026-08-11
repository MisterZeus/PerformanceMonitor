/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitor.Darling.Service.Mcp;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the read side of pg_statement_stats: rate columns come from deltas, cumulative-only columns
/// are read as the latest reading rather than summed, and the grouping matches the collector's
/// identity.
/// </summary>
public class DarlingPgStatementReaderTests
{
    private static string Sql => DarlingPgStatementReader.PgTopQueriesSql;

    /// <summary>
    /// calls, total time and rows have per-interval deltas in the store, so they are SUMmed. Summing
    /// their cumulative counterparts instead would multiply each query's whole lifetime by the number
    /// of snapshots in the window.
    /// </summary>
    [Fact]
    public void SumsTheDeltaColumnsForRateMetrics()
    {
        Assert.Contains("SUM(delta_calls)", Sql, StringComparison.Ordinal);
        Assert.Contains("SUM(delta_total_exec_time_ms)", Sql, StringComparison.Ordinal);
        Assert.Contains("SUM(delta_rows)", Sql, StringComparison.Ordinal);

        Assert.DoesNotContain("SUM(calls)", Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SUM(rows_returned)", Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SUM(total_exec_time_ms)", Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The block, WAL and peak-memory columns have no deltas in the store, so they are read as MAX —
    /// the latest cumulative reading, which is at least a true number. Summing them across snapshots
    /// would be arithmetically meaningless, and this is the specific mistake most likely to be made
    /// when someone extends this query later.
    /// </summary>
    [Fact]
    public void ReadsCumulativeOnlyColumnsAsLatestNotAsSum()
    {
        foreach (var column in new[]
                 {
                     "shared_blks_hit", "shared_blks_read", "storage_blks_read", "orcache_blks_hit",
                     "temp_blks_read", "temp_blks_written", "wal_bytes", "max_exec_peakmem_bytes",
                 })
        {
            Assert.Contains($"MAX({column})", Sql, StringComparison.Ordinal);
            Assert.DoesNotContain($"SUM({column})", Sql, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Grouped by the collector's identity, not by queryid alone: the same normalized statement against
    /// a different database is a separate pg_stat_statements entry with its own counters.
    /// </summary>
    [Fact]
    public void GroupsByQueryIdentityNotQueryIdAlone()
    {
        Assert.Contains("GROUP BY queryid, database_id", Sql, StringComparison.Ordinal);
    }

    /// <summary>Total time is the currency: heaviest first, and bounded.</summary>
    [Fact]
    public void OrdersByTotalTimeAndBoundsTheResult()
    {
        Assert.Contains("ORDER BY SUM(delta_total_exec_time_ms) DESC", Sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Shapes that did not execute in the window are excluded. pg_stat_statements retains an entry long
    /// after its last execution, so without this the result is padded with idle shapes showing zero.
    /// </summary>
    [Fact]
    public void ExcludesShapesThatDidNotRunInTheWindow()
    {
        Assert.Contains("HAVING SUM(delta_total_exec_time_ms) > 0", Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UsesTheStandardServerAndWindowParameters()
    {
        Assert.Contains("server_id = $1", Sql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", Sql, StringComparison.Ordinal);
        Assert.Contains("collection_time <= $3", Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsThePostgresStatementTable()
    {
        Assert.Contains("FROM pg_statement_stats", Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("v_query_stats", Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Aurora I/O split columns are selected — they are the reason this reads
    /// aurora_stat_statements rather than the vanilla view, and dropping them would silently reduce
    /// this to a worse version of the community query.
    /// </summary>
    [Fact]
    public void SelectsTheAuroraIoSourceSplit()
    {
        Assert.Contains("storage_blks_read", Sql, StringComparison.Ordinal);
        Assert.Contains("orcache_blks_hit", Sql, StringComparison.Ordinal);
    }
}
