/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.Threading.Tasks;
using PerformanceMonitor.Alerting;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// Lite's <see cref="IAlertStateStore"/> (Phase-5 forwarding): the shared engine's restart-surviving
/// watermarks (#1145) over the existing <see cref="DuckDbAlertHistoryStore"/> watermark methods —
/// same <c>config_edge_trigger_watermarks</c> rows, same INSERT OR REPLACE upserts, so watermarks
/// persisted by the pre-forwarding loop seed the engine unchanged across the upgrade.
/// <para>
/// THREADING: the engine is invoked on the WPF dispatcher (the 30-second overview sweep), and
/// DuckDB.NET's I/O is synchronous under its async facade — so every store call is wrapped in
/// <c>Task.Run</c>, exactly like <see cref="LiteAlertReadAdapter"/>, keeping the #1202 hitch class
/// off the UI thread.
/// </para>
/// <para>
/// LOADS are per-key (the engine seeds each server once, before its first sweep — the per-key twin
/// of the deleted bulk <c>SeedEdgeTriggerWatermarksAsync</c>), served by filtering the store's
/// existing bulk reads: the watermark table holds at most a few rows per server, each server is
/// seeded once per process, and reusing the existing store methods means no new SQL surface.
/// Restored-watermark behavior is identical — the engine loads before first use per key, so a
/// restart cannot re-alert events still lingering in the rolling window.
/// </para>
/// <para>
/// <c>serverKey</c> is Lite's deterministic storage-name hash rendered as a string (the same value
/// the read adapter parses) — mapped back to the DuckDB <c>server_id</c> int here.
/// </para>
/// </summary>
public sealed class LiteAlertStateStore : IAlertStateStore
{
    private readonly DuckDbAlertHistoryStore _store;

    public LiteAlertStateStore(DuckDbAlertHistoryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<int?> LoadEdgeTriggerWatermarkAsync(string serverKey, string metricName)
    {
        var serverId = ParseServerKey(serverKey);
        return Task.Run(async () =>
        {
            var rows = await _store.LoadEdgeTriggerWatermarksAsync();
            foreach (var (rowServerId, rowMetric, watermark) in rows)
            {
                if (rowServerId == serverId && rowMetric == metricName)
                {
                    return (int?)watermark;
                }
            }
            return null;
        });
    }

    public Task SaveEdgeTriggerWatermarkAsync(string serverKey, string metricName, int watermark)
    {
        var serverId = ParseServerKey(serverKey);
        return Task.Run(() => _store.SaveEdgeTriggerWatermarkAsync(serverId, metricName, watermark));
    }

    public Task<DateTime?> LoadFailedJobWatermarkAsync(string serverKey)
    {
        var serverId = ParseServerKey(serverKey);
        return Task.Run(async () =>
        {
            var rows = await _store.LoadFailedJobWatermarksAsync();
            foreach (var (rowServerId, watermark) in rows)
            {
                if (rowServerId == serverId)
                {
                    /* Server-local basis preserved end-to-end — see the store's doc comment. */
                    return (DateTime?)watermark;
                }
            }
            return null;
        });
    }

    public Task SaveFailedJobWatermarkAsync(string serverKey, DateTime watermark)
    {
        var serverId = ParseServerKey(serverKey);
        return Task.Run(() => _store.SaveFailedJobWatermarkAsync(serverId, watermark));
    }
    /// <summary>
    /// #2166: not persisted in Lite yet. Deliberately a no-op rather than a throw or a silent in-memory
    /// cache — with no memory the engine sees every deviation as new, which IS Lite's pre-#2166 behavior
    /// (fire on the cooldown). An in-memory cache would be worse than nothing here: it would go quiet
    /// until the next app restart and then re-fire every parked database, which is the exact failure the
    /// persistence requirement exists to prevent. Lite parity follows in its own change.
    /// </summary>
    public Task SaveDatabaseStateAlertedAsync(string serverKey, string databaseName, string effectiveState) =>
        Task.CompletedTask;

    /// <summary>
    /// #2166: nothing is stored, so nothing needs clearing. No-op for the same reason as its sibling above.
    /// </summary>
    public Task ClearDatabaseStateAlertedAsync(string serverKey, string databaseName) =>
        Task.CompletedTask;


    private static int ParseServerKey(string serverKey) =>
        int.Parse(serverKey, CultureInfo.InvariantCulture);
}
