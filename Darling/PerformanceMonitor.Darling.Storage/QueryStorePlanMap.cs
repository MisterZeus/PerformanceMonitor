/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Globalization;

namespace PerformanceMonitor.Darling.Storage;

/// <summary>
/// The <c>(server_id, database_name, plan_id) → digest</c> map that lets Query Store facts reference plan XML
/// they no longer carry (#2210). Query Store plan XML was stored INLINE on <c>query_store_stats</c> — measured
/// at 43 GB total, 3,743 MB of plan XML per day, and 871,196 XML-carrying rows against 175,328 distinct
/// <c>(database_name, plan_id)</c>, so **5.0x** of it was the same plans re-shipped. The plan fetch now lands
/// each plan once, in <c>plan_id</c> order, and this map is how a fact row finds its content.
///
/// <para>Facts deliberately do NOT gain a digest column, which is why this table exists rather than a
/// <see cref="PayloadDimensions"/> entry: a fact row is written when the runtime stats arrive, and under the
/// new ordering that is potentially several budgeted cycles BEFORE its plan's XML is fetched, so there is no
/// digest to write at fact time. The map's absence of a row IS the pending state — distinguishable from "never
/// collected" by whether the plan_id sits above the database's watermark — and a reader with no map row renders
/// "plan not yet collected" instead of resolving to nothing.</para>
///
/// <para><b>THIS TABLE'S last_seen IS LOAD-BEARING, AND IT IS THE ONLY PROTECTION QUERY STORE DIGESTS HAVE.</b>
/// The dimension GC does not enumerate references — an anti-join per dim row against two hypertables is not
/// affordable at this size — so it sweeps on <c>last_seen</c>, which the write path refreshes on every cycle
/// that references a digest. That worked precisely BECAUSE plan XML was re-shipped every pass; ending the
/// re-shipping ends the liveness signal, and a plan fetched once would have its dim row collected while live
/// facts still referenced it. Hence <see cref="TouchSql"/>, which asserts liveness for plans the batch no
/// longer carries.</para>
///
/// <para>The GC's second belt does not cover this either: its cutoff is clamped to one day before the oldest
/// surviving DIGEST-CARRYING fact (<c>DarlingRetention.ComputeDimensionCutoff</c>), and Query Store facts carry
/// no digest, so the measured floor is blind to them. Do not add a Query Store entry to
/// <see cref="PayloadDimensions.All"/> to "fix" that — the entry means "this fact column holds a digest",
/// which is not true here, and the clamp would still be measuring a column that does not exist.
/// <see cref="TouchSql"/> is the whole of the protection.</para>
/// </summary>
public static class QueryStorePlanMap
{
    /// <summary>The map table. Not a hypertable: one row per distinct plan per database, ~175k rows/day of
    /// churn on the measured fleet and near-static once a catalog is warm, so it is dimension-shaped rather
    /// than time-series and is pruned on <see cref="LastSeenColumn"/> rather than by drop_chunks.</summary>
    public const string TableName = "collect.query_store_plan_map";

    /// <summary>The liveness column, swept by the prune and stamped by <see cref="TouchSql"/>.</summary>
    public const string LastSeenColumn = "last_seen";

    public const string CreateTableSql = @"CREATE TABLE IF NOT EXISTS collect.query_store_plan_map (
    server_id integer NOT NULL,
    database_name text NOT NULL,
    plan_id bigint NOT NULL,
    digest bytea NOT NULL,
    last_seen timestamp NOT NULL,
    PRIMARY KEY (server_id, database_name, plan_id)
);
CREATE INDEX IF NOT EXISTS idx_query_store_plan_map_last_seen ON collect.query_store_plan_map(last_seen);
CREATE INDEX IF NOT EXISTS idx_query_store_plan_map_digest ON collect.query_store_plan_map(digest);";

    /// <summary>
    /// Records what the plan fetch landed: one row per plan, carrying the digest of the content written to
    /// <c>query_plan_dim</c>. The conflict arm advances <c>digest</c> as well as <c>last_seen</c>, because a
    /// plan whose XML is rewritten in place keeps its <c>plan_id</c> while its content digest changes — the
    /// case the watermark's refresh horizon exists to catch, and this is where the corrected content gets
    /// pointed at.
    ///
    /// <para>Ordered by the conflict key. Same reason as <see cref="DarlingModuleMap.RefreshSql"/>: concurrent
    /// batch upserts that take row locks in different relative orders deadlock (#1801), and a plan fetch runs
    /// per database against a fleet of servers. Checked, not assumed — do not drop the ORDER BY on the belief
    /// that a single-row-per-plan insert cannot conflict with anything.</para>
    /// </summary>
    public const string UpsertSql = @"INSERT INTO collect.query_store_plan_map
    (server_id, database_name, plan_id, digest, last_seen)
SELECT server_id, database_name, plan_id, digest, stamped
FROM unnest($1::integer[], $2::text[], $3::bigint[], $4::bytea[], $5::timestamp[])
     AS batch(server_id, database_name, plan_id, digest, stamped)
ORDER BY server_id, database_name, plan_id
ON CONFLICT (server_id, database_name, plan_id) DO UPDATE SET
    digest = EXCLUDED.digest,
    last_seen = EXCLUDED.last_seen
WHERE query_store_plan_map.last_seen IS NULL OR EXCLUDED.last_seen >= query_store_plan_map.last_seen";

    /// <summary>
    /// The liveness assertion, and the reason this whole design is safe: for the distinct
    /// <c>(database_name, plan_id)</c> a runtime-stats batch just wrote, refresh BOTH this map row's
    /// <c>last_seen</c> and the dimension row's, so neither can age out while facts still point at the plan.
    /// The batch already carries those two columns, so nothing extra is collected to make this work.
    ///
    /// <para>Both timestamps are stamped by the SAME pass, which is what makes the map-prune-versus-dim-GC race
    /// structurally impossible rather than carefully avoided: a map row's <c>last_seen</c> can never be older
    /// than the newest fact batch that referenced it, so the prune cannot take a row that live facts are
    /// touching.</para>
    ///
    /// <para>Guarded at one hour like the dimension upsert's own conflict arm, and for the same reason — the
    /// horizons are multi-day, so an update per row per hour is enough freshness and the write amplification
    /// stays bounded on a hot catalog. The margin arithmetic already accounts for this trailing hour.</para>
    ///
    /// <para>The CTE is ordered by the map's primary key for the #1801 reason above — this is the one statement
    /// in the design that touches many rows across two tables on every cycle of every server, so it is the most
    /// likely place for an unordered-batch deadlock to form. Being precise about how much that buys, because
    /// <see cref="DarlingModuleMap.RefreshSql"/> can claim more than this can: an <c>ORDER BY</c> inside a CTE
    /// feeding <c>UPDATE ... FROM</c> is NOT a guaranteed lock-acquisition order in Postgres the way ordering an
    /// <c>INSERT ... ON CONFLICT</c>'s input is — the planner may reorder. It makes the common plan deterministic
    /// rather than making the deadlock impossible. If one is observed, the fix is to drive the update from an
    /// explicitly ordered <c>SELECT ... FOR UPDATE</c>, not to widen this comment.</para>
    /// </summary>
    public const string TouchSql = @"WITH touched AS (
    SELECT m.server_id, m.database_name, m.plan_id, m.digest
    FROM collect.query_store_plan_map AS m
    JOIN unnest($1::integer[], $2::text[], $3::bigint[])
         AS batch(server_id, database_name, plan_id)
      ON  batch.server_id = m.server_id
      AND batch.database_name = m.database_name
      AND batch.plan_id = m.plan_id
    WHERE m.last_seen < $4::timestamp - interval '1 hour'
    ORDER BY m.server_id, m.database_name, m.plan_id
),
map_touch AS (
    UPDATE collect.query_store_plan_map AS m
    SET last_seen = $4::timestamp
    FROM touched AS t
    WHERE m.server_id = t.server_id
      AND m.database_name = t.database_name
      AND m.plan_id = t.plan_id
    RETURNING t.digest
)
UPDATE collect.query_plan_dim AS d
SET last_seen = $4::timestamp
WHERE d.digest IN (SELECT digest FROM map_touch)
  AND d.last_seen < $4::timestamp - interval '1 hour'";

    /// <summary>
    /// Days of margin the map prune adds past the fact-retention horizon. **Strictly less than the dimension
    /// GC's margin**, which is <c>ChunkIntervalDays + 1</c> — see <see cref="MarginOrderingHolds"/> for why
    /// that direction is the safe one and not merely a convention.
    /// </summary>
    public const int PruneMarginDays = 1;

    /// <summary>
    /// The invariant the two horizons must satisfy: the DIMENSION must outlive the MAP, because the two bad
    /// end-states are not symmetric.
    ///
    /// <para>A pruned map row whose dim row survives is a plan rendering "not collected" plus some dim bytes
    /// that go unreclaimed until the dim's own horizon passes — visibly degraded, self-correcting, no wrong
    /// answers. A pruned DIM row whose map row survives is a reader resolving a live fact to absent content,
    /// which is the silent-missing-plans failure this entire design exists to prevent. Ordering the margins
    /// makes the recoverable end-state the only reachable one.</para>
    ///
    /// <para>Pinned as a function rather than asserted in a comment so a future change to either margin — or to
    /// <c>ChunkIntervalDays</c>, which is where the dim's margin comes from — fails a test instead of quietly
    /// inverting the ordering.</para>
    /// </summary>
    public static bool MarginOrderingHolds(int chunkIntervalDays) =>
        PruneMarginDays < chunkIntervalDays + 1;

    /// <summary>
    /// Retires map rows whose facts have all aged out: an index range scan on <see cref="LastSeenColumn"/>
    /// against the fact horizon plus <see cref="PruneMarginDays"/>, time-sliced like every sibling purge.
    ///
    /// <para>Timestamp-driven, NOT an existence check against <c>query_store_stats</c>. An anti-join against a
    /// 43 GB hypertable per map row is exactly the cost this architecture avoids, and it is unnecessary here
    /// because <see cref="TouchSql"/> keeps <c>last_seen</c> current for anything live — the same argument the
    /// dimension GC already rests on, applied to one more timestamped table.</para>
    ///
    /// <para>A plan whose query goes quiet needs no special handling: it stops being touched, its facts age out
    /// within retention, and the margin ordering retires the map row before the dim row it points at.</para>
    /// </summary>
    public static string PruneSql(int chunkIntervalDays) =>
        "DELETE FROM collect.query_store_plan_map WHERE " + LastSeenColumn + " < $1" +
        " AND " + LastSeenColumn + " >= (SELECT min(" + LastSeenColumn + ") FROM collect.query_store_plan_map WHERE " +
        LastSeenColumn + " < $1)" +
        " AND " + LastSeenColumn + " < (SELECT min(" + LastSeenColumn + ") FROM collect.query_store_plan_map WHERE " +
        LastSeenColumn + " < $1) + INTERVAL '" +
        chunkIntervalDays.ToString(CultureInfo.InvariantCulture) + " days'";
}
