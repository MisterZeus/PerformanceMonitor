/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// #1812: the anomalous-jobs read only trusts a FRESH latest running_jobs snapshot. A stopped
/// collector, missed cycles, or the 512 MB reset (parquet still serving the newest rows) left a stale
/// "latest" that read as NOW — and because the engine's per-run cooldown key expires each pass, the
/// same historical run re-alerted every cooldown, forever. These drive the real adapter against a real
/// DuckDB store: a stale snapshot returns the no-evidence result with the row read skipped; a fresh one
/// returns the rows; and the effective-cadence hook genuinely widens the bound (a slow-profile install
/// must not read its own normal cadence as staleness).
/// </summary>
public sealed class LiteAnomalousJobsFreshnessTests : IDisposable
{
    private const int ServerId = 7;
    private readonly string _tempDir;
    private readonly string _dbPath;

    public LiteAnomalousJobsFreshnessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LiteTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.duckdb");
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

    private async Task<LocalDataService> SeedAsync(TimeSpan snapshotAge)
    {
        var initializer = new DuckDbInitializer(_dbPath);
        await initializer.InitializeAsync();

        using (var connection = new DuckDBConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using var insert = connection.CreateCommand();
            insert.CommandText = @"
INSERT INTO running_jobs (collection_time, server_id, server_name, job_name, job_id, job_enabled, start_time, current_duration_seconds, avg_duration_seconds, p95_duration_seconds, successful_run_count, is_running_long, percent_of_average)
VALUES ($1, $2, 'S1', 'Nightly ETL', 'job-1', true, $3, 3600, 900, 1200, 42, true, 400.0)";
            var snapshotTime = DateTime.UtcNow - snapshotAge;
            insert.Parameters.Add(new DuckDBParameter { Value = snapshotTime });
            insert.Parameters.Add(new DuckDBParameter { Value = ServerId });
            insert.Parameters.Add(new DuckDBParameter { Value = snapshotTime.AddMinutes(-60) });
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await initializer.CreateArchiveViewsAsync();
        return new LocalDataService(initializer);
    }

    [Fact]
    public async Task StaleSnapshot_IsNoEvidence_AndSkipsTheRowRead()
    {
        /* Two hours old against the default 2-minute cadence (bound = 10 minutes): stale. */
        var adapter = new LiteAlertReadAdapter(await SeedAsync(TimeSpan.FromHours(2)));

        var result = await adapter.GetAnomalousJobsAsync(ServerId.ToString(), multiplier: 3);

        Assert.False(result.SnapshotIsFresh);
        Assert.Empty(result.Jobs);
    }

    [Fact]
    public async Task FreshSnapshot_ReturnsTheAnomalousRows()
    {
        var adapter = new LiteAlertReadAdapter(await SeedAsync(TimeSpan.FromMinutes(1)));

        var result = await adapter.GetAnomalousJobsAsync(ServerId.ToString(), multiplier: 3);

        Assert.True(result.SnapshotIsFresh);
        var job = Assert.Single(result.Jobs);
        Assert.Equal("Nightly ETL", job.JobName);
    }

    [Fact]
    public async Task EffectiveCadence_WidensTheBound_SoASlowProfileIsNotReadAsStale()
    {
        /* The same two-hour-old snapshot, but the server's effective running_jobs cadence is 60
           minutes (the relaxed profile ships 30): bound = 180 minutes, so this snapshot is CURRENT
           for that install. A fixed bound would either blind the fast profile or gag the slow one —
           which is why the bound derives from the cadence. */
        var adapter = new LiteAlertReadAdapter(await SeedAsync(TimeSpan.FromHours(2)), _ => 60);

        var result = await adapter.GetAnomalousJobsAsync(ServerId.ToString(), multiplier: 3);

        Assert.True(result.SnapshotIsFresh);
        Assert.Single(result.Jobs);
    }

    [Fact]
    public async Task EmptyStore_IsNoEvidence()
    {
        var initializer = new DuckDbInitializer(_dbPath);
        await initializer.InitializeAsync();
        await initializer.CreateArchiveViewsAsync();
        var adapter = new LiteAlertReadAdapter(new LocalDataService(initializer));

        var result = await adapter.GetAnomalousJobsAsync(ServerId.ToString(), multiplier: 3);

        Assert.False(result.SnapshotIsFresh);
        Assert.Empty(result.Jobs);
    }
}
