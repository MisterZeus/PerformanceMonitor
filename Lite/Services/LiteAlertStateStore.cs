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
    /// #2203: records the state a database was just alerted about, so the next evaluation can tell a NEW
    /// deviation from the one it already reported. This is the Lite half of #2166 — until it existed,
    /// <c>alreadyAnnounced</c> was always false here and a database parked OFFLINE for a month alerted every
    /// cooldown forever, which is the complaint #2166 was filed about.
    ///
    /// <para>UPDATE, never upsert, for the reason #2166 established on the Darling side: an INSERT would have
    /// to invent <c>expected_state</c> (NOT NULL), and the only value available is the state being alerted ON
    /// — so a database first observed SUSPECT would get SUSPECT written as its accepted baseline, stop
    /// deviating, be read as recovered while still corrupt, and never alert again. Nothing is lost by
    /// skipping the no-row case: a database with no baseline was first observed in an integrity state, and
    /// those are never edge-suppressed, so this memory is never consulted for them.</para>
    /// </summary>
    public Task SaveDatabaseStateAlertedAsync(string serverKey, string databaseName, string effectiveState)
    {
        var serverId = ParseServerKey(serverKey);
        return Task.Run(() => _store.SaveDatabaseStateAlertedAsync(serverId, databaseName, effectiveState));
    }

    /// <summary>
    /// #2203: forgets what <see cref="SaveDatabaseStateAlertedAsync"/> recorded, on the falling edge. Without
    /// it the memory is permanent and each database can only ever announce once: park a database OFFLINE,
    /// restore it, park it again weeks later, and the stale memory swallows the second parking — the repeat
    /// soft-delete workflow this alert exists for.
    ///
    /// <para>This is the IMMEDIATE path only. It runs off the engine's in-memory active set, which empties on
    /// restart, so it cannot be the whole answer — the store-derived clear in
    /// <c>LocalDataService.GetDatabaseStateDeviationsAsync</c> is what owns the invariant across restarts.
    /// Both exist for the same reason they do in Darling: a recovery inside one process should not wait for
    /// the next cycle's sweep.</para>
    /// </summary>
    public Task ClearDatabaseStateAlertedAsync(string serverKey, string databaseName)
    {
        var serverId = ParseServerKey(serverKey);
        return Task.Run(() => _store.ClearDatabaseStateAlertedAsync(serverId, databaseName));
    }

    private static int ParseServerKey(string serverKey) =>
        int.Parse(serverKey, CultureInfo.InvariantCulture);
}
