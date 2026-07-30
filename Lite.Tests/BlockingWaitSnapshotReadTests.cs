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
using PerformanceMonitorLite.Tests;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Real-DuckDB round-trip pins for <see cref="LocalDataService.GetCurrentBlockingWaitAsync"/> — the read
/// behind #1839's total-blocked-wait alert. The load-bearing property is that it sums ONE snapshot: the
/// alert is level-triggered and must clear when the current snapshot drops below the threshold, so a read
/// that accidentally summed a window would keep the alert latched on blocking that already ended.
/// </summary>
public sealed class BlockingWaitSnapshotReadTests : IClassFixture<SharedDuckDbFixture>, IDisposable
{
    private const int ServerId = 9931;

    private readonly DuckDbInitializer _duckDb;
    private DuckDBConnection? _seedConn;
    private long _nextId = 1;

    public BlockingWaitSnapshotReadTests(SharedDuckDbFixture fixture)
    {
        fixture.ResetData();
        _duckDb = fixture.DuckDb;
    }

    public void Dispose() => _seedConn?.Dispose();

    private async Task<DuckDBConnection> SeedConnectionAsync()
    {
        if (_seedConn is null)
        {
            _seedConn = _duckDb.CreateConnection();
            await _seedConn.OpenAsync();
        }
        return _seedConn;
    }

    /* Floored to whole seconds: a DuckDB TIMESTAMP is microsecond-resolution, so a raw DateTime's
       100ns ticks do not survive the round trip and the snapshot-time assertion would fail on noise. */
    private static readonly DateTime OlderSnapshot = SecondFloor(DateTime.UtcNow.AddMinutes(-10));
    private static readonly DateTime NewestSnapshot = OlderSnapshot.AddMinutes(9);

    private static DateTime SecondFloor(DateTime t) =>
        DateTime.SpecifyKind(new DateTime(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Unspecified);

    private async Task SeedAsync(DateTime collectionTime, int blockedSpid, long waitTimeMs, int serverId = ServerId)
    {
        using var readLock = _duckDb.AcquireReadLock();
        var connection = await SeedConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO dmv_blocking_snapshots
    (collection_id, collection_time, server_id, server_name, event_time, database_name,
     blocked_spid, blocking_spid, wait_time_ms, lock_mode, monitor_loop)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)";
        cmd.Parameters.Add(new DuckDBParameter { Value = _nextId++ });
        cmd.Parameters.Add(new DuckDBParameter { Value = collectionTime });
        cmd.Parameters.Add(new DuckDBParameter { Value = serverId });
        cmd.Parameters.Add(new DuckDBParameter { Value = "TestSrv" });
        cmd.Parameters.Add(new DuckDBParameter { Value = collectionTime });
        cmd.Parameters.Add(new DuckDBParameter { Value = "StackOverflow" });
        cmd.Parameters.Add(new DuckDBParameter { Value = blockedSpid });
        cmd.Parameters.Add(new DuckDBParameter { Value = blockedSpid + 100 });
        cmd.Parameters.Add(new DuckDBParameter { Value = waitTimeMs });
        cmd.Parameters.Add(new DuckDBParameter { Value = "X" });
        cmd.Parameters.Add(new DuckDBParameter { Value = 1 });
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task SumsOnlyTheNewestSnapshot_NotTheHistory()
    {
        /* The older snapshot's 900s of blocking is over. Including it would both inflate the total and
           make the alert unable to clear. */
        await SeedAsync(OlderSnapshot, blockedSpid: 55, waitTimeMs: 900_000);
        await SeedAsync(NewestSnapshot, blockedSpid: 55, waitTimeMs: 400_000);
        await SeedAsync(NewestSnapshot, blockedSpid: 56, waitTimeMs: 345_000);

        var result = await new LocalDataService(_duckDb).GetCurrentBlockingWaitAsync(ServerId);

        Assert.NotNull(result);
        Assert.Equal(NewestSnapshot, result!.Value.SnapshotTime);
        Assert.Equal(745_000L, result.Value.TotalWaitMs);
        Assert.Equal(2, result.Value.BlockedSessionCount);
    }

    [Fact]
    public async Task CountsDistinctBlockedSessions_NotRows()
    {
        /* One blocked session can appear on several rows of a snapshot (one per blocker in the chain);
           the alert text says "across N blocked session(s)", so N must be sessions, not pairs. */
        await SeedAsync(NewestSnapshot, blockedSpid: 55, waitTimeMs: 10_000);
        await SeedAsync(NewestSnapshot, blockedSpid: 55, waitTimeMs: 20_000);

        var result = await new LocalDataService(_duckDb).GetCurrentBlockingWaitAsync(ServerId);

        Assert.NotNull(result);
        Assert.Equal(30_000L, result!.Value.TotalWaitMs);
        Assert.Equal(1, result.Value.BlockedSessionCount);
    }

    [Fact]
    public async Task ReturnsNull_WhenTheServerHasNoSnapshot()
    {
        /* Another server's blocking must not leak in — the engine reads null as "no evidence". */
        await SeedAsync(NewestSnapshot, blockedSpid: 55, waitTimeMs: 900_000, serverId: ServerId + 1);

        Assert.Null(await new LocalDataService(_duckDb).GetCurrentBlockingWaitAsync(ServerId));
    }
}
