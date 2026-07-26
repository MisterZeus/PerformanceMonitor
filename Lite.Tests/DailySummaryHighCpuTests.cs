/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// Guards the daily-summary high-CPU count semantics. The count must use TOTAL host CPU
/// (sqlserver + other_process), matching the alert engine (CpuAlertMode.Total defaults on),
/// the Overview headline (TotalCpuPercent = sqlserver + (other_process ?? 0)), and Dashboard's
/// report.daily_summary (switched to total in #1004) -- NOT SQL-only. A sample at
/// sqlserver=79 + other_process=5 (total 84) must count; under the retired SQL-only rule it
/// would not. And other_process NULL (SQL Server on Linux, #1048) must fall back to the SQL-only
/// figure via COALESCE(.,0), mirroring Dashboard's ISNULL(total, sqlserver) fallback.
/// </summary>
public class DailySummaryHighCpuTests : IClassFixture<SharedDuckDbFixture>, IDisposable
{
    private readonly DuckDbInitializer _duckDb;
    private readonly LocalDataService _dataService;
    private DuckDBConnection? _seedConn;

    private const int ServerId = -778;
    private static readonly DateTime _day = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
    private long _nextId = -1;

    public DailySummaryHighCpuTests(SharedDuckDbFixture fixture)
    {
        fixture.ResetData();
        _duckDb = fixture.DuckDb;
        _dataService = new LocalDataService(_duckDb);
    }

    /// <summary>
    /// One connection reused for every seeded row — opening a fresh connection per
    /// single-row INSERT measured ~90ms/row and dominated this class's runtime.
    /// </summary>
    private async Task<DuckDBConnection> SeedConnectionAsync()
    {
        if (_seedConn is null)
        {
            _seedConn = _duckDb.CreateConnection();
            await _seedConn.OpenAsync();
        }
        return _seedConn;
    }

    public void Dispose() => _seedConn?.Dispose();

    private async Task SeedCpuAsync(DateTime time, int sqlCpu, int? otherCpu)
    {
        using var readLock = _duckDb.AcquireReadLock();
        var conn = await SeedConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO cpu_utilization_stats
            (collection_id, collection_time, server_id, server_name, sample_time, sqlserver_cpu_utilization, other_process_cpu_utilization)
            VALUES ($1,$2,$3,'TestServer',$2,$4,$5)";
        cmd.Parameters.Add(new DuckDBParameter { Value = _nextId-- });
        cmd.Parameters.Add(new DuckDBParameter { Value = time });
        cmd.Parameters.Add(new DuckDBParameter { Value = ServerId });
        cmd.Parameters.Add(new DuckDBParameter { Value = sqlCpu });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)otherCpu ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task HighCpuEvents_CountsTotalHostCpu_WithLinuxFallback()
    {
        // total >= 80  => counted
        await SeedCpuAsync(_day.AddHours(1), 85, 0);    // 85 from SQL alone
        await SeedCpuAsync(_day.AddHours(2), 79, 5);    // 84 total -- excluded under the old SQL-only rule
        await SeedCpuAsync(_day.AddHours(3), 60, 25);   // 85 total -- mostly non-SQL load
        // total < 80  => excluded
        await SeedCpuAsync(_day.AddHours(4), 50, 10);   // 60 total
        await SeedCpuAsync(_day.AddHours(5), 78, 1);    // 79 total -- just under the line
        // other_process NULL (Linux): fall back to SQL-only
        await SeedCpuAsync(_day.AddHours(6), 82, null); // 82 + COALESCE(NULL,0) = 82  => counted
        await SeedCpuAsync(_day.AddHours(7), 70, null); // 70 + COALESCE(NULL,0) = 70  => excluded

        var summary = await _dataService.GetDailySummaryAsync(ServerId, _day);

        Assert.NotNull(summary);
        // Total-CPU rows: 85, 84, 85, 82 = 4. (Under the retired SQL-only rule this would be 2: only 85 and 82.)
        Assert.True(
            summary!.HighCpuEvents == 4,
            $"Expected 4 high-CPU events under total-CPU semantics (SQL-only would give 2), got {summary.HighCpuEvents}");
    }
}
