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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1915: the CHUNK-GEOMETRY half of the coverage invariant, against a real TimescaleDB.
///
/// <para>A healthy store must never read as coverage-SHORT. Since #1877 a positively measured shortfall
/// RE-HOLDS a policy that is already armed, so on a healthy store that is a purge stopped for no reason — and
/// because holding lets the source grow deeper, the gap widens instead of closing and never self-releases.</para>
///
/// <para><b>Ordering alone does not get you there.</b> #1905 walks the policy list and proves every consumer's
/// horizon outlives its source's. That is necessary and not sufficient, because <c>drop_chunks</c> removes only
/// chunks whose WHOLE range is past the cutoff — so actual retention always runs LONGER than nominal, by up to
/// one chunk interval, and by a different amount on each side. Get the geometry wrong and a source can end up
/// holding data older than anything its consumer kept, with both horizons perfectly ordered.</para>
///
/// <para><b>Why this cannot be a unit test.</b> Every number it depends on is the ENGINE's. Materialization
/// hypertables take a chunk interval nobody in this codebase sets, and the rule is not the one people remember.
/// Measured on PostgreSQL 18.4 / TimescaleDB 2.28.1 by moving <see cref="TimescaleSupport.ChunkIntervalDays"/>
/// and re-reading every relation: a materialization hypertable's chunk interval is <b>10x the RAW hypertable's
/// chunk interval</b> — 1 day of raw gives 10 days, 7 days of raw gives 70 — uniformly, at every depth of the
/// hierarchy and regardless of bucket width. It is NOT the widely-repeated "10x the bucket width" (a 1-hour
/// aggregate would then be 10 hours, and it is not), and it does NOT compound down a chain (the three-level
/// day-grain daily reads the same 10x as its parents, not 10x theirs). A C# test can assert what we declare;
/// only a live store can say what the engine chose.</para>
///
/// <para>One consequence is load-bearing and worth stating: because that rule is uniform, EVERY aggregate in
/// this store shares one chunk interval. So aggregate-to-aggregate pairs are always on identical grids, and
/// raw-to-aggregate pairs are the only ones the arithmetic has to carry — which is exactly the split the two
/// counts below pin.</para>
///
/// <para><b>#1776 own-store</b> — mints its own scratch database rather than sharing the live fixture, so it is
/// deliberately NOT in the <c>live-postgres</c> collection. It creates continuous aggregates and mutates a chunk
/// interval, neither of which the shared fixture may inherit.</para>
/// </summary>
public sealed class RetentionChunkGeometryLiveTests
{
    /// <summary>
    /// Every relation's configured chunk interval, whether it is a raw hypertable or an aggregate's
    /// materialization hypertable, keyed by the name <see cref="TimescaleSupport.RetentionPolicies"/> uses.
    ///
    /// <para>Continuous aggregates are reached through <c>continuous_aggregates</c> to their materialization
    /// hypertable, because that is the relation whose chunks <c>drop_chunks</c> actually removes — the view has
    /// no chunks of its own. <c>dimension_number = 1</c> is the time dimension; Darling range-partitions on time
    /// only, so there is never a second one, and pinning it keeps a future space partition from doubling rows.</para>
    /// </summary>
    private const string ChunkGeometrySql = @"
SELECT d.hypertable_name AS relation, d.time_interval
FROM timescaledb_information.dimensions AS d
WHERE d.hypertable_schema = 'collect'
AND   d.dimension_number = 1
UNION ALL
SELECT c.view_name AS relation, d.time_interval
FROM timescaledb_information.continuous_aggregates AS c
JOIN timescaledb_information.dimensions AS d
  ON  d.hypertable_schema = c.materialization_hypertable_schema
  AND d.hypertable_name = c.materialization_hypertable_name
WHERE c.view_schema = 'collect'
AND   d.dimension_number = 1";

    /// <summary>
    /// For every (source, consumer) pair where BOTH carry a retention policy, the geometry must guarantee the
    /// consumer still reaches at least as far back as its source once <c>drop_chunks</c> rounding is accounted
    /// for — and the check must have teeth, proven by breaking it.
    ///
    /// <para><b>The two ways a pair can be safe</b>, both real in this schema and neither implying the other:</para>
    ///
    /// <para>IDENTICAL GRIDS. TimescaleDB derives chunk boundaries deterministically from the epoch, so two
    /// relations with the same chunk interval sit on the same boundaries. The consumer's cutoff is older than the
    /// source's (ordering, #1905), so every chunk position the source keeps, the consumer keeps too — its
    /// retained set is a SUPERSET. This is what saves the interval layer against the interval-grain daily, where
    /// the arithmetic below would not: both are 10-day-chunked, and 10d of consumer horizon does not exceed
    /// 7d + 10d.</para>
    ///
    /// <para>ARITHMETIC. When the grids differ, no superset argument is available and the worst case has to be
    /// paid outright: the source can retain up to one of ITS chunk intervals past its nominal horizon, while the
    /// consumer can round to almost exactly its own. So the consumer's horizon must reach the source's horizon
    /// PLUS the source's chunk interval. This is what covers the raw tiers, whose 1-day chunks sit under
    /// 10-day-chunked aggregates.</para>
    ///
    /// <para>WATCHED (mutation): <c>set_chunk_time_interval</c> on any aggregate, to anything finer than its
    /// source's, turns this red — which is the whole scenario the issue was filed about, since re-chunking a CAGG
    /// for compression or pruning reasons has no obvious connection to retention safety. The final leg proves
    /// that by doing it, so the guard cannot rot into one that would accept anything.</para>
    /// </summary>
    [Fact]
    public async Task EveryConsumersChunkGeometry_KeepsItCoveringItsSource_AndTheCheckHasTeeth()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(baseConnectionString),
            "Set DARLING_TEST_PG to a Postgres connection string (with TimescaleDB installed) to run the live #1915 chunk-geometry test (it mints its own scratch database).");

        var ct = TestContext.Current.CancellationToken;

        await using var scratch = await ScratchPostgres.CreateAsync(baseConnectionString!, ct);
        await using var connection = new NpgsqlConnection(scratch.ConnectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);

        Assert.True(await TimescaleSupport.TryEnableAsync(connection, null, ct));
        await TimescaleSupport.ConvertToHypertablesAsync(connection, null, ct);
        await TimescaleSupport.EnsureContinuousAggregatesAsync(connection, null, ct);

        var geometry = await ReadChunkGeometryAsync(connection, ct);

        /* ── 1. THE REAL SCHEMA IS SAFE. ── */
        var judged = JudgePairs(geometry, out var unsafePairs, out var missing);

        Assert.True(missing.Count == 0,
            "no chunk interval could be read for: " + string.Join(", ", missing) +
            ". Every relation carrying a retention policy is a hypertable or an aggregate over one, so a missing " +
            "row means the geometry query no longer finds what the policy list names — and an unreadable pair is " +
            "a pair this guard silently stopped judging.");

        Assert.True(unsafePairs.Count == 0,
            "chunk geometry lets a source outlive its own consumer:" + Environment.NewLine +
            string.Join(Environment.NewLine, unsafePairs) + Environment.NewLine +
            "drop_chunks removes only chunks whose whole range is past the cutoff, so a relation retains up to " +
            "one chunk interval MORE than its horizon says. When that rounding runs further on the source than " +
            "on the consumer, the source holds history the consumer never kept — the gate measures that as a " +
            "coverage shortfall and, since #1877, STOPS a purge that was running correctly, on a healthy store, " +
            "without ever self-releasing.");

        /* Anti-vacuity: both regimes must actually be exercised, or a change that quietly stopped judging pairs
           would be indistinguishable from a schema that is in order. The floors rise as policies are added. */
        Assert.True(judged.IdenticalGrid >= 2,
            $"only {judged.IdenticalGrid} pair(s) were carried by the identical-grid argument; the corrected " +
            "Query Store chain has at least 2, and it is the argument the arithmetic cannot make for them.");
        Assert.True(judged.Arithmetic >= 4,
            $"only {judged.Arithmetic} pair(s) were carried by the arithmetic; the raw tiers alone contribute 4.");

        /* ── 2. AND THE CHECK HAS TEETH. Re-chunk one aggregate FINER than its source and the same judgement
               must now refuse it. Without this leg a guard that judged nothing would pass part 1 forever. ── */
        var victim = TimescaleSupport.QueryStoreStatsIntervalDailyView;
        await SetChunkIntervalAsync(connection, victim, TimeSpan.FromDays(1), ct);

        var broken = await ReadChunkGeometryAsync(connection, ct);
        Assert.Equal(TimeSpan.FromDays(1), broken[victim]);

        JudgePairs(broken, out var nowUnsafe, out _);
        Assert.Contains(nowUnsafe, p => p.Contains(victim, StringComparison.Ordinal));
    }

    /// <summary>
    /// Walks every (source, consumer) pair that has a horizon on both sides and sorts it into the two safety
    /// regimes, collecting the ones neither argument can carry. Pure, so the "has teeth" leg can re-run it over
    /// a deliberately broken geometry without touching the store again.
    /// </summary>
    private static (int IdenticalGrid, int Arithmetic) JudgePairs(
        IReadOnlyDictionary<string, TimeSpan> geometry,
        out List<string> unsafePairs,
        out List<string> missing)
    {
        var horizons = TimescaleSupport.RetentionPolicies
            .ToDictionary(p => p.Relation, p => ParseInterval(p.DropAfter), StringComparer.Ordinal);

        unsafePairs = new List<string>();
        missing = new List<string>();
        var identicalGrid = 0;
        var arithmetic = 0;

        foreach (var (source, dropAfter, _, coverage) in TimescaleSupport.RetentionPolicies)
        {
            foreach (var consumer in coverage)
            {
                /* Self-coverage is the #1757 leaf rule, and a consumer with no policy is kept indefinitely —
                   neither is a geometry question. #1905's walk owns both cases. */
                if (string.Equals(consumer, source, StringComparison.Ordinal) ||
                    !horizons.TryGetValue(consumer, out var consumerHorizon))
                {
                    continue;
                }

                if (!geometry.TryGetValue(source, out var sourceChunk))
                {
                    missing.Add(source);
                    continue;
                }

                if (!geometry.TryGetValue(consumer, out var consumerChunk))
                {
                    missing.Add(consumer);
                    continue;
                }

                var sourceHorizon = ParseInterval(dropAfter);

                if (sourceChunk == consumerChunk)
                {
                    /* Identical grids: the consumer's older cutoff keeps a superset of the source's chunks, so
                       ordering alone finishes the argument. Ordering is #1905's, but assert it here too rather
                       than inherit it silently — this regime is unsound without it. */
                    if (consumerHorizon > sourceHorizon)
                    {
                        identicalGrid++;
                        continue;
                    }

                    unsafePairs.Add(
                        $"  {source} ({sourceHorizon.TotalDays}d, {sourceChunk.TotalDays}d chunks) -> {consumer} " +
                        $"({consumerHorizon.TotalDays}d, same chunks): identical grids, but the consumer does not " +
                        "outlive the source, so it keeps no superset of anything");
                    continue;
                }

                if (consumerHorizon >= sourceHorizon + sourceChunk)
                {
                    arithmetic++;
                    continue;
                }

                unsafePairs.Add(
                    $"  {source} ({sourceHorizon.TotalDays}d horizon, {sourceChunk.TotalDays}d chunks) -> " +
                    $"{consumer} ({consumerHorizon.TotalDays}d horizon, {consumerChunk.TotalDays}d chunks): the " +
                    $"grids differ, so the consumer must reach {(sourceHorizon + sourceChunk).TotalDays}d " +
                    $"(the source's horizon plus one of its chunks) and reaches only {consumerHorizon.TotalDays}d");
            }
        }

        return (identicalGrid, arithmetic);
    }

    private static async Task<IReadOnlyDictionary<string, TimeSpan>> ReadChunkGeometryAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        var geometry = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

        await using var command = new NpgsqlCommand(ChunkGeometrySql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!await reader.IsDBNullAsync(1, ct))
            {
                geometry[reader.GetString(0)] = reader.GetTimeSpan(1);
            }
        }

        return geometry;
    }

    /// <summary>
    /// Re-chunks an aggregate by targeting its MATERIALIZATION hypertable, which is the relation that actually
    /// holds chunks — passing the view name is rejected on some versions, and this resolves what the engine
    /// itself considers the partitioned relation rather than guessing at an internal name.
    /// </summary>
    private static async Task SetChunkIntervalAsync(
        NpgsqlConnection connection, string view, TimeSpan interval, CancellationToken ct)
    {
        string materialized;
        await using (var find = new NpgsqlCommand(@"
SELECT format('%I.%I', c.materialization_hypertable_schema, c.materialization_hypertable_name)
FROM timescaledb_information.continuous_aggregates AS c
WHERE c.view_schema = 'collect' AND c.view_name = $1", connection))
        {
            find.Parameters.AddWithValue(view);
            materialized = (string)(await find.ExecuteScalarAsync(ct))!;
        }

        await using var set = new NpgsqlCommand(
            $"SELECT set_chunk_time_interval('{materialized}', $1)", connection);
        set.Parameters.AddWithValue(interval);
        await set.ExecuteNonQueryAsync(ct);
    }

    /// <summary>"4 days" / "21 days" -> a <see cref="TimeSpan"/>, matching the literal the policy is created
    /// with rather than a second representation that could drift from it.</summary>
    private static TimeSpan ParseInterval(string interval)
    {
        var parts = interval.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            && parts[1].StartsWith("day", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromDays(count)
            : TimeSpan.Zero;
    }
}
