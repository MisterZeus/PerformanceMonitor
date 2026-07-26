/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using PerformanceMonitorLite.Analysis;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// #1319 global database filter (Lite). Two layers: the pure positional-<c>IN</c> clause builder
/// (<see cref="LocalDataService.BuildDbInClause"/>, whose param-index math is the risky part) and the
/// collected-database picker source (<see cref="LocalDataService.GetCollectedDatabaseNamesAsync"/>)
/// exercised against a temp DuckDB.
/// </summary>
public class DatabaseFilterTests : IClassFixture<SharedDuckDbFixture>, IDisposable
{
    private readonly DuckDbInitializer _duckDb;
    private DuckDBConnection? _seedConn;

    public DatabaseFilterTests(SharedDuckDbFixture fixture)
    {
        fixture.ResetData();
        _duckDb = fixture.DuckDb;
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

    // ── BuildDbInClause: empty short-circuits, N databases → N params at the right positional indices ──

    [Fact]
    public void BuildDbInClause_NullSelection_EmitsNoClauseAndNoValues()
    {
        var clause = LocalDataService.BuildDbInClause(null, "database_name", 4, out var values);

        Assert.Equal("", clause);
        Assert.Empty(values);
    }

    [Fact]
    public void BuildDbInClause_EmptySelection_EmitsNoClauseAndNoValues()
    {
        var clause = LocalDataService.BuildDbInClause(new List<string>(), "database_name", 4, out var values);

        Assert.Equal("", clause);
        Assert.Empty(values);
    }

    [Fact]
    public void BuildDbInClause_SingleDatabase_EmitsOneParamAtFirstIndex()
    {
        var clause = LocalDataService.BuildDbInClause(new[] { "AppDB1" }, "database_name", 6, out var values);

        Assert.Equal(" AND database_name IN ($6)", clause);
        Assert.Single(values);
        Assert.Equal("AppDB1", values[0]);
    }

    [Fact]
    public void BuildDbInClause_MultipleDatabases_NumbersParamsSequentiallyFromFirstIndex()
    {
        var clause = LocalDataService.BuildDbInClause(new[] { "A", "B", "C" }, "database_name", 4, out var values);

        Assert.Equal(" AND database_name IN ($4, $5, $6)", clause);
        Assert.Equal(3, values.Count);
        Assert.Equal("A", values[0]);
        Assert.Equal("B", values[1]);
        Assert.Equal("C", values[2]);
    }

    [Fact]
    public void BuildDbInClause_HonorsColumnQualifierAndFirstIndex()
    {
        var clause = LocalDataService.BuildDbInClause(new[] { "X", "Y" }, "qs.database_name", 2, out var values);

        Assert.Equal(" AND qs.database_name IN ($2, $3)", clause);
        Assert.Equal(2, values.Count);
    }

    // ── GetCollectedDatabaseNamesAsync ──

    [Fact]
    public async Task GetCollectedDatabaseNames_UnionsBothStores_StripsSystemDbs_DistinctAndSorted()
    {
        using var seeder = new TestDataSeeder(_duckDb);
        await seeder.ClearTestDataAsync();
        await seeder.SeedTestServerAsync();

        // database_config: two user DBs + a system DB (master) that MUST be stripped.
        await seeder.SeedDatabaseConfigAsync(
            ("master", true, false, false, "CHECKSUM"),
            ("AppDB1", true, false, false, "CHECKSUM"),
            ("AppDB2", true, false, false, "CHECKSUM"));

        // database_size_stats: a duplicate (AppDB2 → deduped by UNION), a new user DB (AppDB3),
        // and a system DB (tempdb → stripped).
        await SeedSizeStatsDatabaseAsync("AppDB2", 10);
        await SeedSizeStatsDatabaseAsync("AppDB3", 11);
        await SeedSizeStatsDatabaseAsync("tempdb", 2);

        var dataService = new LocalDataService(_duckDb);
        var names = await dataService.GetCollectedDatabaseNamesAsync(TestDataSeeder.TestServerId);

        Assert.Equal(new[] { "AppDB1", "AppDB2", "AppDB3" }, names);
    }

    [Fact]
    public async Task GetCollectedDatabaseNames_NothingCollected_ReturnsEmpty()
    {
        using var seeder = new TestDataSeeder(_duckDb);
        await seeder.ClearTestDataAsync();
        await seeder.SeedTestServerAsync();

        var dataService = new LocalDataService(_duckDb);
        var names = await dataService.GetCollectedDatabaseNamesAsync(TestDataSeeder.TestServerId);

        Assert.Empty(names);
    }

    private async Task SeedSizeStatsDatabaseAsync(string databaseName, int databaseId)
    {
        using var readLock = _duckDb.AcquireReadLock();
        var connection = await SeedConnectionAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO database_size_stats
    (collection_id, collection_time, server_id, server_name,
     database_name, database_id, file_id, file_type_desc, file_name, physical_name,
     total_size_mb, used_size_mb)
VALUES ($1, $2, $3, $4, $5, $6, 1, 'ROWS', $7, $8, 100, 80)";
        cmd.Parameters.Add(new DuckDBParameter { Value = (long)(-2_000_000 - databaseId) });
        cmd.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow });
        cmd.Parameters.Add(new DuckDBParameter { Value = TestDataSeeder.TestServerId });
        cmd.Parameters.Add(new DuckDBParameter { Value = TestDataSeeder.TestServerName });
        cmd.Parameters.Add(new DuckDBParameter { Value = databaseName });
        cmd.Parameters.Add(new DuckDBParameter { Value = databaseId });
        cmd.Parameters.Add(new DuckDBParameter { Value = $"{databaseName}.mdf" });
        cmd.Parameters.Add(new DuckDBParameter { Value = $"D:\\Data\\{databaseName}.mdf" });
        await cmd.ExecuteNonQueryAsync();
    }
}
