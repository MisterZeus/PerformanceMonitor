/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// The persisted per-database plan-XML watermark (#2164) — the highest <c>plan_id</c> whose execution-plan
/// XML has actually been stored for a database, so collection stops re-shipping plans the store already
/// holds. 97% of the plan XML shipped in a three-hour fleet window was for plans held for over an hour, and
/// because streaming rows is 94-97% of a pass and costs per-row LOB bytes, not fetching beats fetching less.
///
/// <para>Owned by the HOST under its own <see cref="StateCollectorName"/>, exactly like
/// <see cref="QueryStoreBackfillState"/> and for the same reason: the query_store DEFINITION keeps declaring
/// no state keys, so <c>CollectorStateContractTests</c> stays honest and adding per-database state does not
/// silently become a two-host contract change. The keys are dynamic (one per database), which the host's
/// state read supports because it loads every row for a collector name rather than a declared key list — the
/// definition's <c>StateKeys</c> could not express these anyway.</para>
///
/// <para>Lives in the shared collectors project rather than either host because it is watermark-shaped state
/// that must decode identically wherever it is read: a row written by Darling today has to keep meaning the
/// same thing after an upgrade, and Lite reads the same definition.</para>
/// </summary>
public static class QueryStorePlanXmlState
{
    /// <summary>
    /// The collector_state owner name for these rows — deliberately NOT the query_store definition's name,
    /// which is the seam that lets the definition declare no state keys while the host still persists
    /// per-database state for it.
    /// </summary>
    public const string StateCollectorName = "query_store_plan_xml";

    /// <summary>
    /// State key prefix; the remainder is the database name, because <c>plan_id</c> is only unique within one
    /// database's Query Store and means nothing across databases.
    /// </summary>
    public const string WatermarkKeyPrefix = "planwm:";

    /// <summary>
    /// How long a watermark may stand before one pass ignores it and refetches every plan's XML. The stamp it
    /// is measured against dates the last FULL fetch, not the last advance (see <see cref="Format"/>), so
    /// this really is one expensive pass per database per horizon.
    ///
    /// <para>It carries three guarantees, which is why a permanent watermark is wrong:</para>
    ///
    /// <para>1. In-place XML rewrites. plan_id is monotonic and a plan's identity is stable (0 of 38,420
    /// plan_ids changed their plan hash in a day of fleet data), but nothing guarantees a feature like
    /// memory-grant feedback never edits grant values inside the XML of a plan that keeps its id. The
    /// expiry means that question does not have to be load-bearing.</para>
    ///
    /// <para>2. A Query Store RESET. Clearing Query Store restarts plan_id at 1, so every new plan sorts
    /// below a stale watermark and its XML would be suppressed. This horizon is the whole recovery
    /// mechanism, because there is deliberately no reset DETECTION: the tempting test — "the highest plan_id
    /// seen this pass is below the standing watermark" — is TRUE in any ordinary window where no new plan
    /// compiled, which on a steady workload is most of them, so it would drop the watermark constantly and
    /// defeat the optimization. Exact detection needs the server's live MAX(plan_id), which the payload does
    /// not carry.</para>
    ///
    /// <para>3. The dormant-plan gap: plan_id is monotonic in COMPILE order, which is not the same as "we
    /// have stored it", so a plan compiled before monitoring began and dormant through every collected
    /// window arrives below the watermark.</para>
    ///
    /// <para>ONE DAY, not a week: the redundancy removed is per-pass (a 15-minute cadence re-ships a plan
    /// ~96 times a day), so a daily full fetch already eliminates ~99% of it and a weekly one adds almost
    /// nothing — while buying 7x the exposure on all three guarantees above, including a reset blackout
    /// measured in days.</para>
    /// </summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(1);

    /// <summary>The state key for one database.</summary>
    public static string KeyFor(string databaseName) => WatermarkKeyPrefix + databaseName;

    /// <summary>
    /// The watermark to apply for one database, or 0 — meaning "fetch every plan's XML" — for an absent,
    /// malformed, EXPIRED or future-stamped one. Zero is the documented conservative path: absent is what a
    /// first run, a restarted host and a broken store all look like, and all three must refetch rather than
    /// skip. A future stamp means the clock moved backwards, which would otherwise pin the watermark for as
    /// long as the skew lasts.
    /// </summary>
    public static long Resolve(IReadOnlyDictionary<string, string> state, string databaseName, DateTime utcNow)
    {
        if (!TryParse(state, databaseName, out var planId, out var stamped))
        {
            return 0;
        }

        if (stamped > utcNow || utcNow - stamped >= RefreshAfter)
        {
            return 0;
        }

        return planId;
    }

    /// <summary>
    /// The stored stamp — when this database last did a FULL plan-XML fetch — with no expiry applied, so a
    /// write-back can carry it forward across an advance instead of renewing the refresh horizon. Null when
    /// there is nothing parseable to carry, which the caller treats as "stamp now".
    /// </summary>
    public static DateTime? ResolveStamp(IReadOnlyDictionary<string, string> state, string databaseName) =>
        TryParse(state, databaseName, out _, out var stamped) ? stamped : null;

    /// <summary>
    /// Formats a watermark for storage: highest stored plan_id plus the stamp dating the last FULL fetch.
    /// The stamp is a parameter rather than "now" precisely because it must survive advances — re-stamping
    /// on every advance would push the horizon out forever on any database that keeps compiling plans, which
    /// is the busy ones where a stale plan matters most, and the bounded refresh would never fire.
    /// </summary>
    public static string Format(long planId, DateTime fullFetchAtUtc) =>
        planId.ToString(CultureInfo.InvariantCulture) + ":" +
        new DateTimeOffset(DateTime.SpecifyKind(fullFetchAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

    private static bool TryParse(
        IReadOnlyDictionary<string, string> state, string databaseName, out long planId, out DateTime stamped)
    {
        planId = 0;
        stamped = default;

        if (state is null || !state.TryGetValue(KeyFor(databaseName), out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var parts = raw.Split(':');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out planId)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stampedUnix)
            || planId <= 0)
        {
            planId = 0;
            return false;
        }

        stamped = DateTimeOffset.FromUnixTimeSeconds(stampedUnix).UtcDateTime;
        return true;
    }
}
