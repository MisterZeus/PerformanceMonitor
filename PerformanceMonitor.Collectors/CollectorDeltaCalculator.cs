/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Concurrent;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// Calculates delta values for cumulative metrics between collection intervals, caching previous
/// values in memory. The shared implementation of <see cref="ICollectorDeltaCalculator"/> both
/// SKUs run (extracted verbatim from Lite's DeltaCalculator, which now derives from this), so the
/// baseline / counter-reset / gap-policy semantics can never drift between portable Lite and the
/// Darling service. Hosts that survive restarts by re-seeding baselines from their own store call
/// the protected <see cref="Seed"/> (Lite: DuckDB; Darling: Postgres).
/// </summary>
public class CollectorDeltaCalculator : ICollectorDeltaCalculator
{
    /// <summary>
    /// How far back a restart re-seed reads when restoring baselines from a host's own store.
    ///
    /// <para>A correctness bound before it is a performance one. Every delta call site in this
    /// assembly passes <c>maxGapSeconds: 300</c> — all 36 of them — and the gap policy in
    /// <see cref="CalculateDeltaWithInterval"/> discards any baseline older than that and returns 0
    /// instead. A seed row from outside a ~5-minute window therefore cannot produce a delta no matter
    /// what it cost to find, so reading it is work whose result is thrown away.</para>
    ///
    /// <para>Fifteen minutes rather than five: the seed runs at startup and the first collection lands
    /// some seconds after it, so the window needs slack over the policy it serves, and a window that
    /// merely errs generous costs nothing (a row the policy rejects seeds a baseline that is
    /// immediately re-based, which is what an unseeded key does anyway). It still sits well inside one
    /// store chunk, which is the property that matters: it lets TimescaleDB exclude the rest of a
    /// multi-hundred-GB hypertable rather than scan every chunk on a 30-second command timeout — the
    /// field failure in #1772.</para>
    /// </summary>
    public static readonly TimeSpan SeedLookback = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The cutoff a seed read binds to its <c>collection_time &gt;= $1</c> bound, defined once so the
    /// two hosts cannot drift. Naive UTC by the product-wide storage convention — Kind is stripped
    /// deliberately, because Npgsql 6+ rejects a <c>Kind=Utc</c> value against a <c>timestamp</c>
    /// column, and DuckDB stores the same naive-UTC values.
    /// </summary>
    public static DateTime SeedCutoff()
        => DateTime.SpecifyKind(DateTime.UtcNow - SeedLookback, DateTimeKind.Unspecified);

    /// <summary>
    /// Cache structure: serverId -> collectorName -> key -> (previousValue, timestamp)
    /// </summary>
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, ConcurrentDictionary<string, (long Value, DateTime? Timestamp)>>> _cache = new();

    /// <summary>
    /// Removes all cached entries for a server (e.g., when the server tab is closed).
    /// Next collection will re-seed from database if needed.
    /// </summary>
    public void ClearServer(int serverId)
    {
        _cache.TryRemove(serverId, out _);
    }

    /// <summary>
    /// Calculates the delta between the current value and the previous cached value.
    /// First-ever sighting (no baseline): returns 0 and stores the value as the new baseline.
    /// Counter reset (value decreased): returns 0 to avoid inflated deltas from plan cache churn.
    /// Gap detection: if collectionTime and maxGapSeconds are provided and the gap since the
    /// last cached value exceeds maxGapSeconds, returns 0 to avoid inflated deltas after restarts.
    /// Thread-safe via atomic AddOrUpdate.
    /// </summary>
    public long CalculateDelta(int serverId, string collectorName, string key, long currentValue,
        DateTime? collectionTime = null, int maxGapSeconds = 0)
        => CalculateDeltaWithInterval(serverId, collectorName, key, currentValue, out _, collectionTime, maxGapSeconds);

    /// <summary>
    /// Same as <see cref="CalculateDelta"/>, but also reports the number of seconds between the
    /// previous cached collection and the current one via <paramref name="intervalSeconds"/>.
    /// The interval is 0 on the first sighting of a key or after a gap reset (no prior baseline to
    /// measure against), mirroring the delta's own 0 in those cases. Callers can divide a delta by
    /// this interval to derive a per-second rate (e.g. CPU-ms per wall-clock second).
    /// Thread-safe via atomic AddOrUpdate.
    /// </summary>
    public long CalculateDeltaWithInterval(int serverId, string collectorName, string key, long currentValue,
        out int intervalSeconds, DateTime? collectionTime = null, int maxGapSeconds = 0)
    {
        var serverCache = _cache.GetOrAdd(serverId, _ => new ConcurrentDictionary<string, ConcurrentDictionary<string, (long Value, DateTime? Timestamp)>>());
        var collectorCache = serverCache.GetOrAdd(collectorName, _ => new ConcurrentDictionary<string, (long Value, DateTime? Timestamp)>());

        long delta = 0;
        int interval = 0;

        collectorCache.AddOrUpdate(
            key,
            /* Add: first time seeing this key — store the baseline only and return 0.
               All callers track cumulative counters (perfmon, wait stats, file IO, etc.). */
            _ =>
            {
                delta = 0;
                interval = 0;
                return (currentValue, collectionTime);
            },
            /* Update: compute delta atomically */
            (_, previous) =>
            {
                /* Gap detection: if too much time has passed since the last cached value,
                   treat this as a new baseline to avoid inflated deltas after app restarts */
                if (maxGapSeconds > 0 && collectionTime.HasValue && previous.Timestamp.HasValue
                    && (collectionTime.Value - previous.Timestamp.Value).TotalSeconds > maxGapSeconds)
                {
                    delta = 0;
                    interval = 0;
                    return (currentValue, collectionTime);
                }

                /* Seconds between the previous and current collection, when both timestamps exist —
                   the wall-clock span this delta accrued over. */
                interval = (collectionTime.HasValue && previous.Timestamp.HasValue)
                    ? (int)(collectionTime.Value - previous.Timestamp.Value).TotalSeconds
                    : 0;

                delta = currentValue < previous.Value
                    ? 0              /* counter reset (plan cache eviction/re-entry) — not real new work */
                    : currentValue - previous.Value;
                return (currentValue, collectionTime);
            });

        intervalSeconds = interval;
        return delta;
    }

    /// <summary>
    /// Seeds a single value into the cache without computing a delta — the restart-survival hook
    /// hosts use to restore baselines from their own store.
    /// </summary>
    protected void Seed(int serverId, string collectorName, string key, long value, DateTime? timestamp = null)
    {
        var serverCache = _cache.GetOrAdd(serverId, _ => new ConcurrentDictionary<string, ConcurrentDictionary<string, (long Value, DateTime? Timestamp)>>());
        var collectorCache = serverCache.GetOrAdd(collectorName, _ => new ConcurrentDictionary<string, (long Value, DateTime? Timestamp)>());
        collectorCache[key] = (value, timestamp);
    }
}
