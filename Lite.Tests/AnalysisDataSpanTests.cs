/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using PerformanceMonitorLite.Analysis;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// #1809: the Recommendations 24-hour warm-up must survive the 512 MB archive/reset. The sufficiency
/// check measured MIN..MAX(collection_time) on the RAW hot table, so every
/// <c>ArchiveAllAndResetAsync</c> restarted the measured span — on a multi-server install that resets
/// more often than daily, warm-up never completed, even though the archived history was sitting right
/// there in parquet and every other surface could read it through the <c>v_</c> views.
///
/// <para>Two layers: the behavioral fixture proves the span reads hot + archived parquet through
/// <c>v_wait_stats</c> (and that a genuinely young install still gates), and the sweep guard holds the
/// WHOLE analysis pipeline to the view tier — the fact collector's window reads had the same defect,
/// masked until now by the broken gate blocking analysis entirely after each reset. Fixing the gate
/// without the reads would have analyzed a thin post-reset hot window, reintroducing the exact
/// fraction-of-period distortion the 24-hour floor exists to prevent.</para>
/// </summary>
public sealed class AnalysisDataSpanTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _archivePath;

    public AnalysisDataSpanTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LiteTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.duckdb");
        _archivePath = Path.Combine(_tempDir, "archive");
        Directory.CreateDirectory(_archivePath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            /* Best-effort cleanup */
        }
    }

    [Fact]
    public async Task DataSpan_CountsArchivedParquetHistory_AcrossAnArchiveReset()
    {
        var initializer = new DuckDbInitializer(_dbPath);
        await initializer.InitializeAsync();

        using (var connection = new DuckDBConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            /* 28 hours of history collected BEFORE the reset... */
            await ExecuteAsync(connection, @"
INSERT INTO wait_stats (collection_id, collection_time, server_id, server_name, wait_type)
VALUES
    (1, now() - INTERVAL 30 HOUR, 1, 'S1', 'SOS_SCHEDULER_YIELD'),
    (2, now() - INTERVAL 3 HOUR, 1, 'S1', 'SOS_SCHEDULER_YIELD')");

            /* ...archived to parquet and wiped from the hot store, the way ArchiveAllAndResetAsync
               does it (COPY whole table, then reset)... */
            var parquetPath = Path.Combine(_archivePath, "20260101_0000_wait_stats.parquet").Replace("\\", "/");
            await ExecuteAsync(connection, $"COPY wait_stats TO '{parquetPath}' (FORMAT PARQUET)");
            await ExecuteAsync(connection, "DELETE FROM wait_stats");

            /* ...and only 2 hours of post-reset hot data. The raw-table read this test guards against
               sees 2 hours here and blocks analysis forever. */
            await ExecuteAsync(connection, @"
INSERT INTO wait_stats (collection_id, collection_time, server_id, server_name, wait_type)
VALUES
    (3, now() - INTERVAL 2 HOUR, 1, 'S1', 'SOS_SCHEDULER_YIELD'),
    (4, now(), 1, 'S1', 'SOS_SCHEDULER_YIELD')");
        }

        await initializer.CreateArchiveViewsAsync();

        var analysis = new AnalysisService(initializer);
        var spanHours = await analysis.GetTotalDataSpanHoursAsync(serverId: 1);

        Assert.True(spanHours >= 29,
            $"span must count hot + archived history (~30h), got {spanHours:F1}h — the check is reading the raw table again");
    }

    [Fact]
    public async Task DataSpan_StillGatesAGenuinelyYoungInstall()
    {
        var initializer = new DuckDbInitializer(_dbPath);
        await initializer.InitializeAsync();

        using (var connection = new DuckDBConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await ExecuteAsync(connection, @"
INSERT INTO wait_stats (collection_id, collection_time, server_id, server_name, wait_type)
VALUES
    (1, now() - INTERVAL 2 HOUR, 1, 'S1', 'SOS_SCHEDULER_YIELD'),
    (2, now(), 1, 'S1', 'SOS_SCHEDULER_YIELD')");
        }

        await initializer.CreateArchiveViewsAsync();

        var analysis = new AnalysisService(initializer);
        var spanHours = await analysis.GetTotalDataSpanHoursAsync(serverId: 1);

        Assert.InRange(spanHours, 1.5, 3);
    }

    /// <summary>
    /// The sweep guard: NO archivable table may be read raw anywhere in the analysis pipeline — every
    /// read goes through its <c>v_</c> archive view, or the #1809 class of defect returns one query at
    /// a time. Driven by <see cref="ArchiveService.ArchivableTables"/>, the same catalog-derived list
    /// the archiver itself uses, so a new collector's table is guarded the day it exists.
    /// </summary>
    [Fact]
    public void AnalysisPipeline_NeverReadsAnArchivableTableRaw()
    {
        var analysisDir = Path.Combine(FindRepoDirectory(Path.Combine("Lite", "Analysis")));
        var sources = Directory.GetFiles(analysisDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(sources);

        var offenders = new System.Collections.Generic.List<string>();
        foreach (var (table, _) in ArchiveService.ArchivableTables)
        {
            var raw = new Regex($@"\bFROM\s+{Regex.Escape(table)}\b");
            foreach (var file in sources)
            {
                foreach (Match match in raw.Matches(File.ReadAllText(file)))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Analysis reads archivable tables raw (use the v_ archive view so history survives the 512 MB reset):\n"
            + string.Join("\n", offenders.Distinct()));
    }

    private static async Task ExecuteAsync(DuckDBConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static string FindRepoDirectory(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException($"Could not locate {relativePath} walking up from {AppContext.BaseDirectory}");
    }
}
