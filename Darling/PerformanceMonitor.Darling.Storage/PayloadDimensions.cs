/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PerformanceMonitor.Collectors;

namespace PerformanceMonitor.Darling.Storage;

/// <summary>
/// The hash-keyed dimension tables that hold query_stats' / procedure_stats' large text payloads
/// ONCE instead of inline on every collected row (#1767).
///
/// <para>Measured on a production field instance: query_stats (176 GB) and procedure_stats (58 GB)
/// were 94% of a 250 GB store, and 99.9% of a COMPRESSED query_stats chunk was the text/plan TOAST —
/// columnar compression crushes the monitoring numerics (665x) but TOAST compression is byte-level
/// and has no cross-row awareness, so re-collecting the same cached plan every cycle stores it again
/// every cycle. A dedup PoC over a real 1-hour window (514,896 rows) measured 3,166 MB of inline
/// payload collapsing to ~23 MB — and the ratio IMPROVES as the window widens, because distinct
/// plans grow far slower than rows.</para>
///
/// <para><b>The key is a CONTENT digest, not one of SQL Server's hash columns.</b> That is a
/// deliberate departure from the issue's sketch, for three reasons the alternatives cannot satisfy:
/// <list type="number">
/// <item><c>query_plan_hash</c> is a plan-SHAPE hash. The same shape recompiled with different
/// sniffed parameter values carries the same hash and DIFFERENT XML, so a shape-keyed dim with
/// ON CONFLICT DO NOTHING would pin the first XML forever and serve stale
/// <c>ParameterCompiledValue</c>s — the single most diagnostic part of a plan for this product.</item>
/// <item><c>query_hash</c> is a query-SHAPE hash: literal variants collapse, pinning one arbitrary
/// literal set forever, where every reader today shows the LATEST text.</item>
/// <item><c>plan_handle</c> does not determine the XML at all. query_stats captures the STATEMENT
/// plan via <c>dm_exec_text_query_plan(plan_handle, statement_start_offset, statement_end_offset)</c>
/// and the offsets are not stored; this codebase already recorded that exact failure — "keying on
/// plan_handle alone cross-contaminated multi-statement plans (one plan_handle, many statements at
/// different offsets)" (QueryStatsCollector's delta key).</item>
/// </list>
/// A content digest is correct by construction and rests on no SQL Server cache-internals
/// assumption. It costs two <c>bytea</c> columns per query_stats row (~1% of the payload it
/// removes) and preserves the dedup ratio, because the repetition being collapsed comes from
/// re-collecting the SAME cached plan each cycle — byte-identical XML, hence one digest.</para>
///
/// <para><b>Migration is ZERO-REWRITE</b> (the ~234 GB payload rewrite rejected in #1759): the dims
/// and the new writer ship, the inline columns REMAIN, and new rows simply leave them NULL. Every
/// reader coalesces the inline column with the dim join for the transition, and raw retention ages
/// the inline copies out on its own. Dropping the columns is a later, separate decision.</para>
/// </summary>
public static class PayloadDimensions
{
    /// <summary>One diverted payload column: where it lives, and where its content goes instead.</summary>
    /// <param name="TargetTable">The fact table whose collector emits this payload column.</param>
    /// <param name="PayloadColumn">The inline column name in the collector definition (kept, written NULL on new rows).</param>
    /// <param name="DimTable">The dimension table that stores the content once, keyed by digest.</param>
    /// <param name="DigestColumn">The fact column carrying the digest that resolves back to <paramref name="DimTable"/>.</param>
    public sealed record PayloadDimension(
        string TargetTable,
        string PayloadColumn,
        string DimTable,
        string DigestColumn);

    /// <summary>The dimension table holding distinct query text, keyed by its content digest.</summary>
    public const string QueryTextDimTable = "query_text_dim";

    /// <summary>The dimension table holding distinct plan XML, keyed by its content digest.</summary>
    public const string QueryPlanDimTable = "query_plan_dim";

    /// <summary>The digest primary-key column both dimension tables share.</summary>
    public const string DigestColumn = "digest";

    /// <summary>
    /// The GC watermark column both dimension tables share — the newest collection that referenced
    /// this content. See <see cref="GcSql"/> for why this, and not an orphan scan, is the sweep.
    /// </summary>
    public const string LastSeenColumn = "last_seen";

    /// <summary>
    /// Every diverted payload column, across both fact tables. <c>procedure_stats</c> deliberately
    /// has no text entry: it has no <c>query_text</c> column at all (verified against
    /// ProcedureStatsCollector.PayloadColumns) — its entire payload share is plan XML. Both fact
    /// tables share ONE plan dimension: identical plan XML collected from two different DMVs, or
    /// from two different servers running the same vendor application, is the same content and
    /// stores once fleet-wide.
    /// </summary>
    public static readonly IReadOnlyList<PayloadDimension> All = new[]
    {
        new PayloadDimension("query_stats", "query_text", QueryTextDimTable, "query_text_digest"),
        new PayloadDimension("query_stats", "query_plan_xml", QueryPlanDimTable, "query_plan_digest"),
        new PayloadDimension("procedure_stats", "query_plan_xml", QueryPlanDimTable, "query_plan_digest"),
    };

    /// <summary>The distinct dimension tables, in creation order.</summary>
    public static readonly IReadOnlyList<string> DimTables = new[] { QueryTextDimTable, QueryPlanDimTable };

    /// <summary>The diverted columns for one fact table (empty for every table that has none).</summary>
    public static IReadOnlyList<PayloadDimension> ForTable(string targetTable)
        => All.Where(d => string.Equals(d.TargetTable, targetTable, StringComparison.Ordinal)).ToArray();

    /// <summary>The payload column a dimension table stores (the content, not the key).</summary>
    public static string PayloadColumnOf(string dimTable)
        => dimTable switch
        {
            QueryTextDimTable => "query_text",
            QueryPlanDimTable => "query_plan_xml",
            _ => throw new ArgumentOutOfRangeException(nameof(dimTable), dimTable, "Not a payload dimension table"),
        };

    /// <summary>
    /// Maps a collector's PAYLOAD COLUMN INDEX to the dimension that column diverts into — the
    /// contract the binary-COPY write path runs on, where column ORDER is everything. Derived from
    /// the shared collector definition by NAME so a column reorder cannot silently misroute a
    /// payload, and it THROWS when a declared dimension's column is absent: renaming
    /// <c>query_text</c> in the shared definition must break the build loudly, because the silent
    /// failure mode is that diversion quietly stops and every row goes back to storing its payload
    /// inline — the exact 94%-of-the-store defect this file exists to remove, reintroduced with no
    /// error anywhere.
    /// </summary>
    public static IReadOnlyDictionary<int, PayloadDimension> DiversionPlanFor(ICollectorSchemaInfo schema)
    {
        if (schema is null)
        {
            throw new ArgumentNullException(nameof(schema));
        }

        var dimensions = ForTable(schema.TargetTable);
        if (dimensions.Count == 0)
        {
            return EmptyPlan;
        }

        var plan = new Dictionary<int, PayloadDimension>(dimensions.Count);
        foreach (var dimension in dimensions)
        {
            var index = -1;
            for (var i = 0; i < schema.PayloadColumns.Count; i++)
            {
                if (string.Equals(schema.PayloadColumns[i].Name, dimension.PayloadColumn, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"Payload dimension '{dimension.DimTable}' expects column '{dimension.PayloadColumn}' on " +
                    $"'{schema.TargetTable}', which the collector definition no longer declares. Update " +
                    $"{nameof(PayloadDimensions)}.{nameof(All)} — leaving it stale silently stops diverting and " +
                    "stores the full payload inline on every row again (#1767).");
            }

            plan[index] = dimension;
        }

        return plan;
    }

    private static readonly IReadOnlyDictionary<int, PayloadDimension> EmptyPlan =
        new Dictionary<int, PayloadDimension>();

    /// <summary>
    /// The content digest: SHA-256 over the UTF-8 bytes of the payload EXACTLY as it would have been
    /// stored inline — i.e. after <c>PgCollectorRowWriter.StripEmbeddedNuls</c>, so the digest keys
    /// the bytes the dim actually holds. Callers must strip before hashing; the write path funnels
    /// both through one place so they cannot drift.
    /// </summary>
    public static byte[] Digest(string payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    /// The CREATE TABLE for one dimension table. PLAIN PostgreSQL tables, deliberately NOT
    /// hypertables: they have no time axis to partition on, and a hypertable's unique constraint
    /// must include the partitioning column, which a content-keyed dim cannot satisfy. Being plain
    /// engine objects is also why they belong in a versioned migration, unlike the TimescaleDB
    /// objects that are runtime setup.
    /// </summary>
    public static string CreateDimTable(string dimTable)
    {
        var payloadColumn = PayloadColumnOf(dimTable);
        return
            $"CREATE TABLE IF NOT EXISTS {dimTable} (\n" +
            $"    {DigestColumn} bytea NOT NULL PRIMARY KEY,\n" +
            $"    {payloadColumn} text NOT NULL,\n" +
            $"    {LastSeenColumn} timestamp NOT NULL\n" +
            ");\n" +
            $"CREATE INDEX IF NOT EXISTS idx_{dimTable}_last_seen ON {dimTable}({LastSeenColumn});";
    }

    /// <summary>
    /// The batch upsert: ONE statement, two array parameters plus the collection timestamp, so it
    /// carries no multi-statement/positional-parameter hazard (an Npgsql batch mixing the two fails
    /// SILENTLY). <c>ON CONFLICT DO NOTHING</c> is what makes the write path idempotent and is the
    /// whole dedup mechanism — the second and every later sighting of the same content writes
    /// nothing.
    ///
    /// <para>The conflict arm refreshes <see cref="LastSeenColumn"/> only when it is more than an
    /// hour stale. Without that guard every referenced dim row would take an UPDATE every collection
    /// cycle — a dead tuple per row per minute on a hot table — for a watermark whose consumer
    /// (<see cref="GcSql"/>) has a multi-day horizon. The guard caps it at one UPDATE per row per
    /// hour, 60x less churn, and stays correct as long as the GC margin exceeds an hour (it is a
    /// full day).</para>
    ///
    /// <para>Callers MUST pass digest-deduplicated arrays: Postgres raises 21000 ("ON CONFLICT DO
    /// UPDATE command cannot affect row a second time") when one statement presents the same key
    /// twice, and a 200-row batch sharing a handful of plans presents duplicates constantly. The
    /// write path accumulates into a digest-keyed dictionary, which dedups structurally.</para>
    /// </summary>
    public static string UpsertSql(string dimTable)
    {
        var payloadColumn = PayloadColumnOf(dimTable);
        return
            $"INSERT INTO {dimTable} ({DigestColumn}, {payloadColumn}, {LastSeenColumn})\n" +
            $"SELECT u.digest, u.payload, $3\n" +
            $"FROM unnest($1::bytea[], $2::text[]) AS u(digest, payload)\n" +
            $"ON CONFLICT ({DigestColumn}) DO UPDATE SET {LastSeenColumn} = EXCLUDED.{LastSeenColumn}\n" +
            $"WHERE {dimTable}.{LastSeenColumn} < EXCLUDED.{LastSeenColumn} - INTERVAL '1 hour'";
    }

    /// <summary>
    /// The dimension GC, run on the existing retention cadence.
    ///
    /// <para>The obvious sweep — delete dim rows no live fact references — is an anti-join against
    /// two hypertables per dim row and is not affordable at this size. <see cref="LastSeenColumn"/>
    /// makes it an index range scan instead, and is exactly equivalent: the write path stamps
    /// last_seen on every cycle that references a digest, so last_seen is never older than the
    /// newest fact row pointing at it. If last_seen has fallen past the fact tables' own retention
    /// horizon, every fact that could reference this content has already been purged.</para>
    ///
    /// <para>The caller passes the horizon as the fact tables' effective retention plus a margin —
    /// see DarlingRetention, which resolves the same per-collector horizon the fact purge uses, so
    /// a raised retention override can never outrun the dim GC and orphan a reader.</para>
    ///
    /// <para>Bounded rather than kept forever because the worst case compounds: ~23 MB/hour of
    /// distinct plans on the measured field instance is ~552 MB/day, ~16.6 GB/month, ~200 GB/year
    /// if nothing ever expires — a store that would eventually re-create the problem this change
    /// exists to solve. Bounded at the fact horizon, the dim holds only content still reachable
    /// from a live fact row, which is ~2 GB worst case at a 4-day horizon and far less in practice
    /// once the distinct-content set saturates.</para>
    /// </summary>
    public static string GcSql(string dimTable)
        => $"DELETE FROM {dimTable} WHERE {LastSeenColumn} < $1";
}
