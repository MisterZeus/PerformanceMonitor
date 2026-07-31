using System.Text.RegularExpressions;

namespace Installer.Tests;

/// <summary>
/// Source guard for the #1907 slice aggregation in <c>install/09_collect_query_store.sql</c>.
///
/// <para><c>sys.query_store_runtime_stats</c> returns the FLUSHED slice and the still-IN-MEMORY slice of one
/// <c>runtime_stats_interval_id</c> as two ADDITIVE rows. Verified on SQL Server 2022 (16.0.4255.1): 100
/// executions flushed plus 25 in memory came back as two rows while <c>sys.dm_exec_procedure_stats</c> — a
/// separate source read at the same instant — reported 125. The proc used to select them straight through,
/// so the Dashboard grids showed a fraction of an interval's work.</para>
///
/// <para>This is a SOURCE guard because there is nothing else that could catch a regression here. The
/// aggregation lives inside a dynamically assembled <c>@sql</c> string, so the compiler sees nothing; the
/// <c>sql-validation</c> workflow installs the proc on four SQL Server versions but only proves it COMPILES,
/// which it would with any arithmetic at all; and the tests that would execute it are the DB-touching classes
/// CI deliberately filters out. A guard that reads the file is the only thing standing between this fix and a
/// silent regression, which is exactly the situation the repo's other source-parsing pins exist for.</para>
///
/// <para>The shared collector's half of the same fix (<c>QueryStoreCollector.BuildPayloadBody</c>, which
/// serves Lite and Darling) is pinned separately and far more thoroughly in
/// <c>Lite.Tests/QueryStoreCollectorDefinitionTests.cs</c>. This file exists so the deprecated Dashboard's
/// copy cannot drift away from it unnoticed.</para>
/// </summary>
public class QueryStoreSliceAggregationSqlTests
{
    private static string CollectorSql()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PerformanceMonitor.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "could not locate the repository root from " + AppContext.BaseDirectory);

        var path = Path.Combine(dir!.FullName, "install", "09_collect_query_store.sql");
        Assert.True(File.Exists(path), "expected the Query Store collector script at " + path);
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The slices are combined on the natural key of the view, and the counters combine the way the source's
    /// own semantics require: SUM for the additive counter, extremes for the extremes, the interval's span
    /// from its slices' span.
    /// </summary>
    [Fact]
    public void CollectorGroupsRuntimeStatsOnTheIntervalIdentity()
    {
        var sql = CollectorSql();

        Assert.Contains("rs.runtime_stats_interval_id,", sql, StringComparison.Ordinal);
        Assert.Contains("count_executions = SUM(rs.count_executions),", sql, StringComparison.Ordinal);
        Assert.Contains("first_execution_time = MIN(rs.first_execution_time),", sql, StringComparison.Ordinal);
        Assert.Contains("last_execution_time = MAX(rs.last_execution_time),", sql, StringComparison.Ordinal);

        /* min_dop / max_dop are the pair with no avg_ sibling, so they are the pair a mechanical edit is most
           likely to hand the wrong aggregate. */
        Assert.Contains("min_dop = MIN(rs.min_dop),", sql, StringComparison.Ordinal);
        Assert.Contains("max_dop = MAX(rs.max_dop)", sql, StringComparison.Ordinal);

        Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"GROUP BY\s+rs\.plan_id,\s+rs\.runtime_stats_interval_id,\s+rs\.execution_type_desc"),
            sql);
    }

    /// <summary>
    /// EVERY <c>avg_*</c> column the aggregate produces must be the count-WEIGHTED mean.
    ///
    /// <para>Query Store stores an average and a count but never a total, so <c>avg * count</c> is the only
    /// way to recover a slice's total. The wrong forms are not errors and do not read as wrong — a bare
    /// <c>AVG(rs.avg_duration)</c> looks perfectly natural and silently weights a 25-execution sliver the same
    /// as a 100-execution flush. The columns are discovered FROM THE FILE rather than listed here, so a newly
    /// added one is covered the moment it appears instead of when someone remembers this test.</para>
    /// </summary>
    [Fact]
    public void EveryAveragedColumnUsesTheCountWeightedMean()
    {
        var sql = CollectorSql();

        /* Only the aggregating derived table. The outer projection references the same names as plain
           columns, which is correct there and must not be read as an un-weighted aggregate. */
        var open = sql.IndexOf("FROM\n            (", StringComparison.Ordinal);
        if (open < 0)
        {
            open = sql.IndexOf("FROM\r\n            (", StringComparison.Ordinal);
        }

        Assert.True(open > 0, "could not locate the slice-aggregating derived table in the collector script");

        var close = sql.IndexOf(") AS rs", open, StringComparison.Ordinal);
        Assert.True(close > open, "the slice-aggregating derived table is not closed with ') AS rs'");

        var aggregate = sql[open..close];

        var averages = Regex.Matches(aggregate, @"(avg_[a-z0-9_]+) = ")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        /* duration, cpu_time, logical_io_reads, logical_io_writes, physical_io_reads, clr_time,
           query_max_used_memory, rowcount, num_physical_io_reads, log_bytes_used, tempdb_space_used. */
        Assert.Equal(11, averages.Count);

        foreach (var column in averages)
        {
            Assert.Contains(
                $"{column} = SUM(rs.{column} * rs.count_executions) / NULLIF(SUM(rs.count_executions), 0),",
                aggregate,
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain("AVG(rs.avg_", aggregate, StringComparison.Ordinal);
        Assert.DoesNotContain("MAX(rs.avg_", aggregate, StringComparison.Ordinal);
        Assert.DoesNotContain("MIN(rs.avg_", aggregate, StringComparison.Ordinal);
    }

    /// <summary>
    /// The incremental cutoff is asked at INTERVAL grain, never per slice.
    ///
    /// <para>This is the assertion that would catch the most tempting wrong edit. A per-slice
    /// <c>WHERE rs.last_execution_time >= @cutoff_time</c> reads like the obvious filter and reintroduces the
    /// whole defect: the flushed slice is STATIC, so once the growing in-memory slice pushes the watermark past
    /// it the flushed slice stops qualifying and the SUM silently becomes the sliver alone.</para>
    /// </summary>
    [Fact]
    public void TheCutoffIsAppliedToTheIntervalRatherThanTheSlice()
    {
        var sql = CollectorSql();

        Assert.Matches(new Regex(@"HAVING\s+MAX\(rs\.last_execution_time\) >= @cutoff_time"), sql);

        /* The pre-filter is a prune, not a semantic: its interval list is a superset of what the HAVING keeps,
           so it can never subtract a row, and without it the aggregate runs over the whole retained Query
           Store every cycle. */
        Assert.Contains("WHERE rs.runtime_stats_interval_id IN", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE f.last_execution_time >= @cutoff_time", sql, StringComparison.Ordinal);

        Assert.DoesNotContain("WHERE rs.last_execution_time >= @cutoff_time", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>replica_group_id</c> enters the grouping key only where the column exists.
    ///
    /// <para>Naming a column that does not exist in a GROUP BY fails the whole batch rather than yielding a
    /// NULL, so an ungated reference would break Query Store collection outright on every pre-2022 server.
    /// It has to be in the key where it DOES exist: two replicas' rows for one interval are different work,
    /// and grouping without it would sum a secondary's executions into the primary's.</para>
    /// </summary>
    [Fact]
    public void ReplicaGroupIdIsGatedBehindItsVersionFlag()
    {
        var sql = CollectorSql();

        Assert.Contains("@replica_group_available bit = 0", sql, StringComparison.Ordinal);

        /* Every mention of the column sits inside an IF on that flag — asserted by requiring that the flag is
           tested at least as often as the column is named. */
        var columnMentions = Regex.Matches(sql, @"rs\.replica_group_id").Count;
        var gateTests = Regex.Matches(sql, @"IF @replica_group_available = 1").Count;

        Assert.True(columnMentions > 0, "the grouping key must carry replica_group_id where it exists");
        Assert.True(
            gateTests >= columnMentions,
            $"replica_group_id is named {columnMentions} time(s) but the version gate is tested only " +
            $"{gateTests} time(s) — an ungated reference fails the whole batch on a pre-2022 server.");

        /* The gate itself must admit Azure SQL Database, which under-reports PRODUCTVERSION as 12 while
           running an evergreen engine — the same rule QueryStoreCollector.hasReplicaAttribution uses. */
        Assert.Matches(new Regex(@"@product_version >= 16\s+OR @engine = 5"), sql);
    }
}
