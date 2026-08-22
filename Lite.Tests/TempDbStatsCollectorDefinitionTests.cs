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

    [Fact]
    public async Task ReadAsync_CombinesTwoResultSets_IntoOneRow()
    {
        using var reader = FakeCollectorDataReader.WithResultSets(
            new[] { new object[] { 1.5m, 2.5m, 3.5m, 7.5m, 10.0m } },
            new[] { new object[] { 55, 12.25m, 9L } });

        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        var rows = await TempDbStatsCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(new TempDbStatsCollector.Row(1.5m, 2.5m, 3.5m, 7.5m, 10.0m, 9L, 55, 12.25m), row);
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
        var row = new TempDbStatsCollector.Row(1.5m, 2.5m, 3.5m, 7.5m, 10.0m, 9L, 55, 12.25m);

        TempDbStatsCollector.Instance.WritePayload(row, writer, CollectorTestContext.Make(deltas));

        Assert.Equal(new object?[] { 1.5m, 2.5m, 3.5m, 7.5m, 10.0m, 9L, 55, 12.25m }, writer.Values);
        Assert.Empty(deltas.Calls);
    }

    /// <summary>
    /// #2512: the whole pipeline on an AZURE SQL DATABASE target, which this collector was gated off
    /// until the gate's stated reason was checked and found false.
    ///
    /// <para>Three things the gate meant nobody had ever exercised, and each fails differently:</para>
    /// <list type="number">
    /// <item><b>The query is target-independent.</b> If Azure needed a variant, <c>BuildQuery</c> would
    /// have to branch on the target — it does not, and the measurement says it does not need to.</item>
    /// <item><b>Column typing.</b> <c>sys.dm_db_session_space_usage.session_id</c> is <c>smallint</c>, so
    /// the driver hands back a <c>short</c>. The definition reads it through
    /// <c>Convert.ToInt32(GetValue(0))</c> rather than <c>GetInt32</c> precisely for that, and the payload
    /// column is <c>top_session_id INTEGER</c> — so the widening has to happen and has to be pinned. A
    /// fixture that feeds an <c>int</c> (as the parity pin above does) can never see this.</item>
    /// <item><b>The fan-out shape.</b> <c>RunsPerDatabase</c> is false, so on Azure SQL DB this takes the
    /// plain single-connection path rather than the per-database loop <c>file_io_stats</c> and
    /// <c>index_object_stats</c> take. Per #2220 a registration that names a database is scoped to that
    /// database, so this collects one tempdb snapshot per registration — not N of them.</item>
    /// </list>
    ///
    /// <para>Values are the ones actually measured on <c>GP_S_Gen5_2</c> (EngineEdition 5) on 2026-08-22,
    /// not invented ones, so the row this asserts is a row the platform really produced.</para>
    /// </summary>
    [Fact]
    public async Task AzureSqlDb_MeasuredValues_ComposeThroughToThePayload()
    {
        var azure = new CollectorTargetInfo { IsAzureSqlDb = true, SqlMajorVersion = 12 };
        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator(), isAzureSqlDb: true);

        /* One query for every target — no Azure variant, which is the claim the gate rested on. */
        Assert.Equal(
            TempDbStatsCollector.Instance.BuildQuery(CollectorTestContext.Make(new RecordingCollectorDeltaCalculator())).Text,
            TempDbStatsCollector.Instance.BuildQuery(context).Text,
            System.StringComparer.Ordinal);

        /* Plain path, not the Azure per-database loop. */
        Assert.False(TempDbStatsCollector.Instance.RunsPerDatabase(azure));
        Assert.Null(TempDbStatsCollector.Instance.BuildEnumerationQuery(context));

        using var reader = FakeCollectorDataReader.WithResultSets(
            /* result set 1: user 5.44 / internal 1.81 / version 0.00 / total 7.25 / unallocated 54.19 MB */
            new[] { new object[] { 5.44m, 1.81m, 0.00m, 7.25m, 54.19m } },
            /* result set 2: session 74 as SMALLINT, 0.13 MB, 1 session over threshold as COUNT_BIG */
            new[] { new object[] { (short)74, 0.13m, 1L } });

        var rows = await TempDbStatsCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(new TempDbStatsCollector.Row(5.44m, 1.81m, 0.00m, 7.25m, 54.19m, 1L, 74, 0.13m), row);

        var writer = new RecordingCollectorRowWriter();
        TempDbStatsCollector.Instance.WritePayload(row, writer, context);

        /* Positional AND typed: 74 must arrive as int, not short, or the INTEGER column takes a
           narrowed write on the Darling COPY path. */
        Assert.Equal(TempDbStatsCollector.Instance.PayloadColumns.Count, writer.Values.Count);
        Assert.Equal(new object?[] { 5.44m, 1.81m, 0.00m, 7.25m, 54.19m, 1L, 74, 0.13m }, writer.Values);
        Assert.IsType<int>(writer.Values[6]);
        Assert.IsType<long>(writer.Values[5]);
    }
}
