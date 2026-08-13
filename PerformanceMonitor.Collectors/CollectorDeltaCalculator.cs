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
    /// The gap past which a cached baseline is treated as too stale to subtract from, shared by every
    /// delta call site in this assembly so the policy cannot drift collector by collector.
    ///
    /// <para>One hour, chosen from measurement rather than intuition. The previous value — 300 s,
    /// hard-coded at all 41 call sites — sat almost exactly on the fleet's median sweep gap, so it
    /// fired during ordinary operation instead of after the restarts it was written for. Measured over
    /// 99,717 consecutive perfmon gaps across 52 production servers and 7 days: p50 <b>299 s</b>,
    /// p90 580 s, p99 830 s, p99.9 1,190 s, max 2,514 s. The share of ordinary gaps each candidate
    /// rejects: <b>300 s → 50.0%</b>, 600 s → 8.3%, 900 s → 0.6%, 1,800 s → 0.0%, 3,600 s → 0.0%.
    /// Half of every delta collector's output was a fabricated zero (#2233, #2234).</para>
    ///
    /// <para>An hour clears the observed maximum with room to spare while still catching what the
    /// guard is actually for: a server unreachable for hours, or a baseline restored from a store row
    /// old enough that attributing its whole accrual to one interval would read as a spike. Note the
    /// direction of the harm this replaces — a rejected gap returns 0, and a 0 is indistinguishable
    /// from a genuinely idle interval, so the guard did not merely lose data, it invented quiet.</para>
    /// </summary>
    public const int DefaultMaxGapSeconds = 3600;

    /// <summary>
    /// How far back a restart re-seed reads when restoring baselines from a host's own store.
    ///
    /// <para>Fifteen minutes, and since <see cref="DefaultMaxGapSeconds"/> became an hour this window
    /// — not the gap policy — is what bounds restart recovery. It used to be the other way round: at a
    /// 300 s policy every seed row older than five minutes was rejected on arrival, so most of this
    /// window was work whose result was thrown away. Now every row it returns can produce a real
    /// delta, which is the point.</para>
    ///
    /// <para>Left at fifteen minutes deliberately. It sits well inside one store chunk, which is the
    /// property that matters: it lets TimescaleDB exclude the rest of a multi-hundred-GB hypertable
    /// rather than scan every chunk on a 30-second command timeout — the field failure in #1772.
    /// Widening it to chase the hour-long policy would trade that back.</para>
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
    /// <para>All three of those zeros mean "no delta is knowable here", which a <c>long</c> cannot say
    /// any other way — and none of them is the same claim as "this interval was idle". Use
    /// <see cref="CalculateDeltaWithInterval"/> when a caller has to tell them apart: the reported
    /// interval is 0 in exactly these cases and non-zero whenever the delta is real, so a stored
    /// (delta, interval) pair of (0, 0) reads as unknown while (0, n) reads as genuinely idle. That
    /// pairing is what makes a zero interpretable downstream (#2234).</para>
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

                if (currentValue < previous.Value)
                {
                    /* Counter reset (plan cache eviction/re-entry): the work between the two readings
                       is unknowable, not zero. Report no interval either, so the pair stays honest —
                       a 0 delta over a REAL interval is a claim that nothing happened for that long,
                       and this is the one case where that claim would be false. That invariant
                       (interval 0 <=> no delta knowable) is what lets a reader tell a fabricated zero
                       from an idle one, and every consumer already maps 0 to NULL via
                       NULLIF(sample_interval_seconds, 0). */
                    delta = 0;
                    interval = 0;
                }
                else
                {
                    delta = currentValue - previous.Value;
                }

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
