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
using System.Text.Json;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Mcp;
using PerformanceMonitorLite.Models;
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// Pins #2012's ENVELOPE half on Lite's <c>get_top_queries_by_cpu</c> — the reader-level count is
/// pinned in <c>QueryStatsModuleAttributionReaderTests</c>, but the tool's own wiring
/// (<c>distinct_texts</c> riding into the JSON, <c>text_note</c> fired only on a blended group) ran
/// in no test, and Lite's tool body is a twin COPY of Darling's, whose envelope is pinned by a
/// gated live test. Same fixture pattern as <c>McpAnalysisFindingsCommandTests</c>: a real
/// <c>ServerManager</c> so the resolver derives the same server id the seeded rows carry.
/// </summary>
public sealed class McpTopQueriesDistinctTextsTests : IClassFixture<SharedDuckDbFixture>, IDisposable
{
    private readonly string _tempDir;
    private readonly DuckDbInitializer _duckDb;
    private readonly ServerManager _serverManager;
    private readonly int _serverId;
    private long _nextId = 1;

    public McpTopQueriesDistinctTextsTests(SharedDuckDbFixture fixture)
    {
        fixture.ResetData();
        _duckDb = fixture.DuckDb;

        _tempDir = Path.Combine(Path.GetTempPath(), "McpDistinctTexts_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));

        _serverManager = new ServerManager(Path.Combine(_tempDir, "config"));
        var server = new ServerConnection { ServerName = "TestServer", DisplayName = "TestServer" };
        _serverManager.AddServer(server);
        _serverId = RemoteCollectorService.GetDeterministicHashCode(
            RemoteCollectorService.GetServerNameForStorage(server));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task Envelope_CarriesDistinctTexts_AndNotesOnlyTheBlendedGroup()
    {
        var collected = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-90), DateTimeKind.Unspecified);

        /* The #2012 collision shape: one query_hash, two different statements (INSERT...EXEC
           callees); plus a stable single-text hash. */
        await SeedAsync(collected, "0xBLEND", "0xH1", 300_000, "INSERT INTO #items EXEC dbo.inner_v3");
        await SeedAsync(collected, "0xBLEND", "0xH2", 200_000, "INSERT INTO #items EXEC dbo.inner_v4");
        await SeedAsync(collected, "0xONETEXT", "0xH3", 100_000, "SELECT stable");

        var json = await McpQueryTools.GetTopQueriesByCpu(
            new LocalDataService(_duckDb), _serverManager, "TestServer");

        using var doc = JsonDocument.Parse(json);
        var queries = doc.RootElement.GetProperty("queries").EnumerateArray().ToList();
        Assert.Equal(2, queries.Count);

        var blend = queries.Single(q => q.GetProperty("query_hash").GetString() == "0xBLEND");
        Assert.Equal(2, blend.GetProperty("distinct_texts").GetInt64());
        Assert.Contains("INSERT...EXEC", blend.GetProperty("text_note").GetString(), StringComparison.Ordinal);

        var single = queries.Single(q => q.GetProperty("query_hash").GetString() == "0xONETEXT");
        Assert.Equal(1, single.GetProperty("distinct_texts").GetInt64());
        Assert.Equal(JsonValueKind.Null, single.GetProperty("text_note").ValueKind);
    }

    private async Task SeedAsync(DateTime collected, string queryHash, string sqlHandle, long elapsedUs, string queryText)
    {
        using var readLock = _duckDb.AcquireReadLock();
        using var connection = _duckDb.CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO query_stats
    (collection_id, collection_time, server_id, server_name, database_name,
     query_hash, sql_handle, last_execution_time, delta_execution_count,
     delta_worker_time, delta_elapsed_time, query_text)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)";
        cmd.Parameters.Add(new DuckDBParameter { Value = _nextId++ });
        cmd.Parameters.Add(new DuckDBParameter { Value = collected });
        cmd.Parameters.Add(new DuckDBParameter { Value = _serverId });
        cmd.Parameters.Add(new DuckDBParameter { Value = "TestServer" });
        cmd.Parameters.Add(new DuckDBParameter { Value = "TestDb" });
        cmd.Parameters.Add(new DuckDBParameter { Value = queryHash });
        cmd.Parameters.Add(new DuckDBParameter { Value = sqlHandle });
        cmd.Parameters.Add(new DuckDBParameter { Value = collected });
        cmd.Parameters.Add(new DuckDBParameter { Value = 5L });
        cmd.Parameters.Add(new DuckDBParameter { Value = elapsedUs });
        cmd.Parameters.Add(new DuckDBParameter { Value = elapsedUs });
        cmd.Parameters.Add(new DuckDBParameter { Value = queryText });
        await cmd.ExecuteNonQueryAsync();
    }
}
