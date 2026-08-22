/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lite.Tests.Helpers;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the parity contract of the extracted tempdb_stats definition: two result sets collapse
/// to exactly one row (zeros when empty — matching the original collector), and the payload
/// order matches the tempdb_stats schema.
/// </summary>
public sealed class TempDbStatsCollectorDefinitionTests
{
    [Fact]
    public void PayloadColumns_MatchSchemaOrder()
    {
        var names = TempDbStatsCollector.Instance.PayloadColumns.Select(c => c.Name).ToArray();

        Assert.Equal(
            new[]
            {
                "user_object_reserved_mb",
                "internal_object_reserved_mb",
                "version_store_reserved_mb",
                "total_reserved_mb",
                "unallocated_mb",
                "total_sessions_using_tempdb",
                "top_session_id",
                "top_session_tempdb_mb",
                /* #2515, APPENDED. Both stores generate their DDL from this list in order and both row
                   writers are positional, so the ceiling could only ever go last — inserting it beside
                   unallocated_mb, where it belongs semantically, would re-map every historical row. */
                "max_size_mb",
            },
            names);
    }

    [Fact]
    public void Query_TargetsBothTempDbDmvs()
    {
        var queryText = TempDbStatsCollector.Instance.BuildQuery(CollectorTestContext.Make(new RecordingCollectorDeltaCalculator())).Text;
        Assert.Contains("tempdb.sys.dm_db_file_space_usage", queryText, System.StringComparison.Ordinal);
        Assert.Contains("sys.dm_db_session_space_usage", queryText, System.StringComparison.Ordinal);
        Assert.Equal("tempdb_stats", TempDbStatsCollector.Instance.Name);
        Assert.Equal("tempdb_stats", TempDbStatsCollector.Instance.TargetTable);
    }

    /// <summary>
    /// #2515: the ceiling comes from tempdb's own catalog, and the two things that make it the RIGHT
    /// ceiling are both in the query rather than in the reader — so they can only be pinned here.
    ///
    /// <para>LOG files are excluded because <c>dm_db_file_space_usage</c>, which supplies every other
    /// column, reports DATA allocation: folding the log's cap into the same denominator would understate
    /// usage on every server, not just Azure. And <c>max_size</c> is an <c>int</c> of 8 KB pages that tops
    /// out at 16 TB per file, so a wide tempdb can overflow a plain <c>SUM</c> — the widen has to happen
    /// before the sum, not after it.</para>
    /// </summary>
    [Fact]
    public void Query_ReadsTheCeilingFromTheRowsFilesOnly_AndSumsItWideEnough()
    {
        var queryText = TempDbStatsCollector.Instance.BuildQuery(CollectorTestContext.Make(new RecordingCollectorDeltaCalculator())).Text;

        Assert.Contains("tempdb.sys.database_files AS df", queryText, System.StringComparison.Ordinal);
        Assert.Contains("WHERE df.type = 0 /*ROWS*/", queryText, System.StringComparison.Ordinal);
        Assert.Contains("SUM(CONVERT(bigint, df.max_size))", queryText, System.StringComparison.Ordinal);

        /* -1 on any one data file means tempdb as a whole grows without limit, and MIN is what finds it. */
        Assert.Contains("WHEN MIN(df.max_size) = -1", queryText, System.StringComparison.Ordinal);

        /* House convention, and it is load-bearing on a query that now carries a second aggregate. */
        Assert.Contains("OPTION(RECOMPILE)", queryText, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_CombinesTwoResultSets_IntoOneRow()
    {
        using var reader = FakeCollectorDataReader.WithResultSets(
            new[] { new object[] { 1.5m, 2.5m, 3.5m, 7.5m, 10.0m, 65536.0m } },
            new[] { new object[] { 55, 12.25m, 9L } });

        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        var rows = await TempDbStatsCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(new TempDbStatsCollector.Row(1.5m, 2.5m, 3.5m, 7.5m, 10.0m, 9L, 55, 12.25m, 65536.0m), row);
    }

    /// <summary>
    /// #2515: a tempdb with no ROWS files visible makes the ceiling subquery return NULL, and NULL is not a
    /// ceiling of zero. It has to land on 0 — the "not measured" state every consumer answers by dividing by
    /// the allocation, exactly as it did before this column existed. A zero cap would divide the alert's
    /// percentage by nothing at all.
    /// </summary>
    [Fact]
    public async Task ReadAsync_NullCeiling_ReadsAsNotMeasured_NotAsAZeroCap()
    {
        using var reader = FakeCollectorDataReader.WithResultSets(
            new[] { new object[] { 1.5m, 2.5m, 3.5m, 7.5m, 10.0m, System.DBNull.Value } },
            new[] { new object[] { 55, 12.25m, 9L } });

        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        var rows = await TempDbStatsCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        Assert.Equal(0m, Assert.Single(rows).MaxSizeMb);
    }

    /// <summary>
    /// And the unlimited answer survives the read AS -1 rather than being flattened to 0. They take the same
    /// denominator, but they are different facts — "this tempdb has no ceiling" versus "nobody looked" — and
    /// the alert detail says which.
    /// </summary>
    [Fact]
    public async Task ReadAsync_UnlimitedCeiling_StaysMinusOne()
    {
        using var reader = FakeCollectorDataReader.WithResultSets(
            new[] { new object[] { 1.5m, 2.5m, 3.5m, 7.5m, 10.0m, -1m } },
            new[] { new object[] { 55, 12.25m, 9L } });

        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        var rows = await TempDbStatsCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        Assert.Equal(-1m, Assert.Single(rows).MaxSizeMb);
    }

    [Fact]
    public async Task ReadAsync_EmptyResultSets_StillYieldsOneZeroRow()
    {
        using var reader = FakeCollectorDataReader.WithResultSets(
            System.Array.Empty<object[]>(),
            System.Array.Empty<object[]>());

        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        var rows = await TempDbStatsCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(default(TempDbStatsCollector.Row), row);
    }

    [Fact]
    public void WritePayload_EmitsSchemaOrder_NoDeltas()
    {
        var deltas = new RecordingCollectorDeltaCalculator();
        var writer = new RecordingCollectorRowWriter();
        var row = new TempDbStatsCollector.Row(1.5m, 2.5m, 3.5m, 7.5m, 10.0m, 9L, 55, 12.25m, 65536.0m);

        TempDbStatsCollector.Instance.WritePayload(row, writer, CollectorTestContext.Make(deltas));

        Assert.Equal(new object?[] { 1.5m, 2.5m, 3.5m, 7.5m, 10.0m, 9L, 55, 12.25m, 65536.0m }, writer.Values);
        Assert.Empty(deltas.Calls);
    }
}
