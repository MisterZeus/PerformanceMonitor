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
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace PerformanceMonitor.Darling.Storage;

/// <summary>
/// The staged, disk-preflighted backfill of the query-acceleration rollups (#1759 Phase 2) — the capacity
/// operation that turns Phase 1's correct-but-slow raw fallback back into an accelerated read, and lets the
/// #1680 arming gate release the held raw purges by itself.
///
/// <para><b>Why this is an operator verb and not a startup step.</b> The arming gate is all-or-nothing: a rollup
/// either covers raw or it does not, so a store that has been collecting for a year has to materialize the WHOLE
/// history before the first purge arms and reclaims anything. Peak disk therefore comes BEFORE any relief. Doing
/// that automatically at service start, on the exact stores worst affected — one of which was already down to
/// roughly 150 GB free — is a plausible disk-exhaustion event. So: explicit invocation, a preflight that refuses
/// with numbers rather than filling the volume, and progress an operator can watch.</para>
///
/// <para><b>Slicing is supervision, not re-implementation.</b> A manual <c>refresh_continuous_aggregate</c>
/// already batches internally (2.28.1 defaults: 10 buckets per batch, each committing via
/// <c>SPI_commit_and_chain</c>), so the memory and lock-duration arguments for hand-rolled slicing are already
/// handled upstream. The outer slices here exist for four things the engine's batching does not give: progress
/// an operator can see on a multi-hour run, a resume point, a lock window bounded to ONE source chunk (the
/// concurrent-compression deadlock watch from #1778), and a per-slice DATA-based convergence check. A batch cap
/// hit is LOG-only and silent to the client, so a returned success proves nothing.</para>
/// </summary>
public static class RollupBackfill
{
    /// <summary>How much history one refresh call covers. Deliberately ONE source chunk
    /// (<see cref="TimescaleSupport.ChunkIntervalDays"/>): the slice's read set is then a single chunk, which
    /// is what keeps the lock window short enough not to sit across a compression job (#1778). Slices are
    /// midnight-aligned, so they always land on bucket boundaries for both the hourly and daily grains — an
    /// unaligned window would be silently widened to bucket boundaries by TimescaleDB anyway, making progress
    /// arithmetic lie.</summary>
    public static readonly TimeSpan SliceWidth = TimeSpan.FromDays(TimescaleSupport.ChunkIntervalDays);

    /// <summary>Measured compression throughput on the field host class this verb exists for (~16 MB/s). Used
    /// ONLY to turn an estimate into a duration an operator can plan around — it is a budgeting figure, never a
    /// gate.</summary>
    public const double FieldThroughputBytesPerSecond = 16.0 * 1024 * 1024;

    /// <summary>Estimates are estimates: require this much more free space than the estimate says is needed, so
    /// a rollup that materializes denser than its sample does not run the volume to zero mid-pass.</summary>
    public const double SafetyFactor = 1.25;

    /// <summary>Free space never to consume, on top of the estimate. PostgreSQL needs room for WAL, temp files
    /// and the compression of what is being written; a volume driven to zero takes the store down, which is a
    /// far worse outcome than a refused backfill.</summary>
    public const long ReserveBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// The rollups to backfill, in DEPENDENCY ORDER: every hourly rollup first, then the dailies that are
    /// sourced FROM those hourlies.
    ///
    /// <para>The order is load-bearing, not cosmetic. A daily continuous aggregate reads its hourly one, so
    /// refreshing a daily over a range whose hourly has not been materialized reads an empty source and
    /// materializes nothing — while REPORTING success, and while CONSUMING the invalidation records that
    /// covered the range. A later correct-order pass then no-ops over the hole. Inverting this list does not
    /// merely reorder work; it can leave permanent under-coverage that looks like completion.</para>
    /// </summary>
    public static readonly RollupBackfillTarget[] Targets =
    {
        new(TimescaleSupport.QueryStatsHourlyView, "query_stats", IsDaily: false),
        new(TimescaleSupport.QueryStatsDbHourlyView, "query_stats", IsDaily: false),
        new(TimescaleSupport.ProcedureStatsHourlyView, "procedure_stats", IsDaily: false),
        new(TimescaleSupport.QueryStoreStatsHourlyView, "query_store_stats", IsDaily: false),
        new(TimescaleSupport.QueryStatsDailyView, "query_stats", IsDaily: true),
        new(TimescaleSupport.QueryStatsDbDailyView, "query_stats", IsDaily: true),
        new(TimescaleSupport.ProcedureStatsDailyView, "procedure_stats", IsDaily: true),
        new(TimescaleSupport.QueryStoreStatsDailyView, "query_store_stats", IsDaily: true),
    };

    /* ─────────────────────────── the probe ─────────────────────────── */

    /// <summary>
    /// One round trip per rollup answering everything the plan needs: how far back raw reaches (the target),
    /// how far back the rollup has materialized (the resume point), and the size + bucket count of what it HAS
    /// materialized (the calibration sample for the estimate).
    ///
    /// <para>The size comes from the MATERIALIZATION hypertable, resolved through
    /// <c>timescaledb_information.continuous_aggregates</c>, because <c>pg_total_relation_size</c> on a
    /// hypertable's parent reports almost nothing — the chunks are separate relations. Reached through a
    /// scalar subquery over the information view rather than a direct reference, so a store without the
    /// extension fails the whole probe cleanly instead of erroring halfway.</para>
    /// </summary>
    public static string ProbeSql(string view, string rawTable) => $@"
SELECT
    (SELECT min(collection_time) FROM collect.{rawTable}) AS raw_oldest,
    (SELECT min(bucket) FROM collect.{view}) AS coverage_oldest,
    (SELECT count(*) FROM collect.{view}) AS materialized_buckets,
    (SELECT hypertable_size(format('%I.%I', c.materialization_hypertable_schema, c.materialization_hypertable_name)::regclass)
     FROM timescaledb_information.continuous_aggregates AS c
     WHERE c.view_schema = 'collect' AND c.view_name = '{view}') AS materialized_bytes";

    /// <summary>
    /// A BOUNDED refresh over one slice. Both bounds are bound parameters and explicitly cast:
    /// <c>window_start</c>/<c>window_end</c> are declared <c>"any"</c> on the 2.28.1 signature, so an untyped
    /// literal leaves PostgreSQL with no type to resolve the polymorphic argument against.
    ///
    /// <para>Deliberately the NON-forced form. On an aggregate created <c>WITH NO DATA</c> the plain refresh is
    /// enough — creation writes an infinite <c>[-infinity, +infinity]</c> invalidation ("initially, everything
    /// is invalid") that <c>WITH NO DATA</c> does not skip — and <c>force</c> only exists from TimescaleDB
    /// 2.18, so an older bring-your-own store raises 42883 on the 4-argument call. Where force IS needed is
    /// REPAIR, and the caller escalates to it only on a MEASURED shortfall (see
    /// <see cref="RunSliceAsync"/>), never speculatively: a refresh consumes invalidations, so a pass cut short
    /// leaves a region whose entries are gone and a later plain refresh no-ops over the hole while reporting
    /// success.</para>
    /// </summary>
    public static string RefreshSliceSql(string view, bool force = false) => force
        ? $"CALL refresh_continuous_aggregate('collect.{view}'::regclass, $1::timestamp, $2::timestamp, true)"
        : $"CALL refresh_continuous_aggregate('collect.{view}'::regclass, $1::timestamp, $2::timestamp)";

    /// <summary>The rollup's oldest materialized bucket — the DATA-based convergence signal. A refresh CALL that
    /// returns without error proves nothing: a batch-cap stop is logged server-side and is completely silent to
    /// the client.</summary>
    public static string CoverageFloorSql(string view) => $"SELECT min(bucket) FROM collect.{view}";

    /* ─────────────────────────── the plan + preflight (pure) ─────────────────────────── */

    /// <summary>
    /// What one rollup needs, from its probe row. Returns a plan with <see cref="RollupBackfillPlan.IsComplete"/>
    /// when there is nothing to do — an empty raw table (a fresh store), or coverage that already reaches raw
    /// (which is what makes re-running this verb converge to a no-op instead of re-materializing forever).
    ///
    /// <para>The estimate is CALIBRATED from what the rollup has already materialized (bytes per bucket, scaled
    /// by the buckets to add) whenever there is a sample — which on the stores this exists for there always is,
    /// since their refresh policies have been materializing a trailing 3-day window all along. With no sample at
    /// all it falls back to a fraction of raw's own bytes over the same span and says so, so an operator can
    /// tell a measured number from a bounding one.</para>
    /// </summary>
    public static RollupBackfillPlan Plan(
        string view,
        DateTime? rawOldestUtc,
        DateTime? coverageOldestUtc,
        long materializedBuckets,
        long materializedBytes,
        long rawBytes,
        TimeSpan bucketWidth)
    {
        if (rawOldestUtc is not DateTime rawOldest)
        {
            return RollupBackfillPlan.Complete(view, "raw holds no rows, so there is nothing to materialize");
        }

        /* Slices start at midnight so every window opens on a bucket boundary for both grains. */
        var from = rawOldest.Date;
        var to = coverageOldestUtc ?? DateTime.SpecifyKind(DateTime.UtcNow, rawOldest.Kind);

        if (coverageOldestUtc is DateTime coverage && coverage <= rawOldest)
        {
            return RollupBackfillPlan.Complete(view, "coverage already reaches raw's oldest row");
        }

        /* And the range CLOSES on one too. A refresh window narrower than a single bucket is rejected outright
           — TimescaleDB raises 22023 "refresh window too small" — and the ragged tail of a day-wide slice is
           exactly that for a DAILY rollup, whose bucket IS a day. The end is a coverage floor or "now", so it
           is an arbitrary instant and lands mid-bucket most of the time; caught on the gated live leg as a
           slice failure that aborted the daily tier. Truncating instead of rounding up is deliberate: the live
           edge belongs to the refresh policy, and this verb is only ever trying to extend the OLD end. */
        var span = to - from;
        if (span > TimeSpan.Zero && bucketWidth > TimeSpan.Zero)
        {
            to = from + TimeSpan.FromTicks(span.Ticks / bucketWidth.Ticks * bucketWidth.Ticks);
            span = to - from;
        }

        if (span <= TimeSpan.Zero)
        {
            return RollupBackfillPlan.Complete(view, "coverage already reaches raw's oldest row");
        }

        var bucketsToAdd = (long)Math.Ceiling(span / bucketWidth);
        var slices = (long)Math.Ceiling(span / SliceWidth);

        long estimatedBytes;
        bool calibrated;
        if (materializedBuckets > 0 && materializedBytes > 0)
        {
            estimatedBytes = (long)Math.Ceiling((double)materializedBytes / materializedBuckets * bucketsToAdd);
            calibrated = true;
        }
        else
        {
            /* No sample to calibrate from. Bound it instead: the rollup collapses many per-sweep raw rows into
               one row per (dimensions, bucket), so it is materially smaller than raw for the same span — but
               "materially" is not a number we can measure here, and guessing LOW is the failure that fills a
               volume. UncalibratedFractionOfRaw is therefore an upper bound, not an expectation, and the caller
               reports the estimate as uncalibrated so the operator reads it as one. */
            estimatedBytes = (long)Math.Ceiling(rawBytes * UncalibratedFractionOfRaw);
            calibrated = false;
        }

        return new RollupBackfillPlan(view, from, to, bucketsToAdd, slices, estimatedBytes, calibrated, IsComplete: false, SkipReason: null);
    }

    /// <summary>
    /// The bounding fraction of raw's own bytes used when a rollup has materialized NOTHING to calibrate
    /// against. Deliberately generous: an over-estimate refuses a backfill that would in fact have fit (the
    /// operator grows the disk or re-runs after the policy has materialized a sample), while an under-estimate
    /// fills a production volume. Those two mistakes are not symmetric.
    /// </summary>
    public const double UncalibratedFractionOfRaw = 0.5;

    /// <summary>
    /// Is there room? Requires the estimate plus <see cref="SafetyFactor"/> headroom AND
    /// <see cref="ReserveBytes"/> left untouched. Pure, so the refusal is unit-testable without a full volume.
    /// </summary>
    public static bool HasRoom(long estimatedBytes, long freeBytes) =>
        freeBytes >= RequiredBytes(estimatedBytes);

    /// <summary>Free space this backfill requires: the estimate, scaled for error, plus the untouchable reserve.</summary>
    public static long RequiredBytes(long estimatedBytes) =>
        (long)Math.Ceiling(estimatedBytes * SafetyFactor) + ReserveBytes;

    /// <summary>How long the materialization is expected to take at the measured field throughput. Budgeting
    /// only.</summary>
    public static TimeSpan EstimatedDuration(long estimatedBytes) =>
        TimeSpan.FromSeconds(estimatedBytes / FieldThroughputBytesPerSecond);

    /* ─────────────────────────── the slice runner ─────────────────────────── */

    /// <summary>
    /// Refreshes ONE slice and returns the rollup's measured coverage floor afterwards, for progress output.
    ///
    /// <para><b>Deliberately no per-slice verdict.</b> The obvious check — "did the floor move to at or before
    /// this slice's start?" — is WRONG, and the gated live leg caught it: a slice's range can legitimately hold
    /// no source rows (raw's oldest row lands partway into the first slice, and any collection gap does the same
    /// mid-run), so the floor correctly stops short and the check reads a healthy slice as a failure. It fired
    /// on the very first slice of every run. A global <c>min(bucket)</c> simply cannot answer a question about
    /// one slice.</para>
    ///
    /// <para>Convergence is judged ONCE, at the end, against the only target that means anything — raw's oldest
    /// row (<see cref="RepairAsync"/>). That is the same shape #1762 settled on: re-probe coverage after
    /// refreshing and escalate only on a MEASURED shortfall.</para>
    /// </summary>
    public static async Task<DateTime?> RunSliceAsync(
        NpgsqlConnection connection, string view, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        await RefreshAsync(connection, view, fromUtc, toUtc, force: false, cancellationToken);
        return await ReadCoverageFloorAsync(connection, view, cancellationToken);
    }

    /// <summary>
    /// The escalation, run ONLY on a measured shortfall: a FORCED refresh over the whole planned range, which
    /// ignores the invalidation log and re-batches every bucket in it. Returns the floor afterwards, so the
    /// caller still judges the outcome from data.
    ///
    /// <para>This is what a plain refresh cannot do, and the reason a shortfall is not simply retried: a
    /// refresh CONSUMES invalidation records as it goes, so a pass cut short by a shutdown (or by a batch cap,
    /// which is logged server-side and completely silent to the client) leaves a region whose entries are
    /// already gone — and a later plain refresh no-ops straight over that hole while reporting success.</para>
    ///
    /// <para>Never speculative: it is strictly more work, and <c>force</c> only exists from TimescaleDB 2.18,
    /// so on an older bring-your-own store this raises 42883 — which the caller reports rather than crashing
    /// the run, and which never happens at all unless a shortfall was already measured.</para>
    /// </summary>
    public static async Task<DateTime?> RepairAsync(
        NpgsqlConnection connection, string view, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        await RefreshAsync(connection, view, fromUtc, toUtc, force: true, cancellationToken);
        return await ReadCoverageFloorAsync(connection, view, cancellationToken);
    }

    /// <summary>
    /// TimescaleDB serializes refreshes of the same aggregate and raises <see cref="ConcurrentRefreshSqlState"/>
    /// (<c>55P03 lock_not_available</c>) rather than waiting. This verb runs while the SERVICE IS UP, so the
    /// aggregate's own refresh policy — which fires roughly hourly over a trailing 3-day window — will
    /// eventually land on top of a slice. Caught live on the gated PostgreSQL 18.4 / TimescaleDB 2.28.1 leg:
    /// <c>EnsureContinuousAggregatesAsync</c> attaches the policy, the policy runs immediately, and the very
    /// next slice failed.
    ///
    /// <para>Retried rather than reported, because it is neither an error nor an ambiguous state: the policy's
    /// run is short and bounded, the collision says only "not now", and letting it abort the rollup would make
    /// a multi-hour backfill fail on a store doing exactly what it is supposed to be doing. Bounded and
    /// transient-only — every other SQLSTATE still propagates on the first occurrence, so a permission problem
    /// or a missing relation still fails fast instead of being retried into a timeout.</para>
    /// </summary>
    public const string ConcurrentRefreshSqlState = "55P03";

    /// <summary>How many times a slice waits out a concurrent policy refresh before giving up. Generous: the
    /// alternative to waiting is failing a backfill that was going to succeed.</summary>
    public const int ConcurrentRefreshAttempts = 10;

    private static readonly TimeSpan ConcurrentRefreshDelay = TimeSpan.FromSeconds(5);

    private static async Task RefreshAsync(
        NpgsqlConnection connection, string view, DateTime fromUtc, DateTime toUtc, bool force, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                /* PreventInTransactionBlock: the CALL can never be wrapped in an explicit transaction. */
                using var refresh = new NpgsqlCommand(RefreshSliceSql(view, force), connection) { CommandTimeout = SliceTimeoutSeconds };
                refresh.Parameters.AddWithValue(fromUtc);
                refresh.Parameters.AddWithValue(toUtc);
                await refresh.ExecuteNonQueryAsync(cancellationToken);
                return;
            }
            catch (PostgresException ex)
                when (attempt < ConcurrentRefreshAttempts && ex.SqlState == ConcurrentRefreshSqlState)
            {
                await Task.Delay(ConcurrentRefreshDelay, cancellationToken);
            }
        }
    }

    /// <summary>The rollup's measured coverage floor, or null when it holds nothing.</summary>
    public static async Task<DateTime?> ReadCoverageFloorAsync(
        NpgsqlConnection connection, string view, CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        using var probe = new NpgsqlCommand(CoverageFloorSql(view), connection) { CommandTimeout = ProbeTimeoutSeconds };
        return await probe.ExecuteScalarAsync(cancellationToken) is DateTime floor ? floor : null;
    }

    /// <summary>Reads one rollup's probe row (<see cref="ProbeSql"/>).</summary>
    public static async Task<RollupBackfillProbe> ProbeAsync(
        NpgsqlConnection connection, string view, string rawTable, CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        using var command = new NpgsqlCommand(ProbeSql(view, rawTable), connection) { CommandTimeout = ProbeTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new RollupBackfillProbe(null, null, 0, 0);
        }

        return new RollupBackfillProbe(
            reader.IsDBNull(0) ? null : reader.GetDateTime(0),
            reader.IsDBNull(1) ? null : reader.GetDateTime(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt64(3));
    }

    /// <summary>Total bytes a raw hypertable occupies, for the uncalibrated estimate's bound. Zero when the
    /// relation is not a hypertable or the extension is absent — the caller then has no basis for an
    /// uncalibrated estimate and says so rather than inventing one.</summary>
    public static async Task<long> RawBytesAsync(
        NpgsqlConnection connection, string rawTable, CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        try
        {
            using var command = new NpgsqlCommand($"SELECT hypertable_size('collect.{rawTable}'::regclass)", connection)
            {
                CommandTimeout = ProbeTimeoutSeconds,
            };
            return await command.ExecuteScalarAsync(cancellationToken) is long bytes ? bytes : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    /// <summary>The store's own data directory, so free space is measured on the volume that will actually
    /// grow. Asked of the STORE rather than derived from config, so it is right in bring-your-own mode too —
    /// and so a store on ANOTHER host reports a path this machine does not have, which the caller turns into a
    /// refusal instead of measuring the wrong volume. Null when the login may not read the setting.</summary>
    public static async Task<string?> DataDirectoryAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        try
        {
            using var command = new NpgsqlCommand("SELECT current_setting('data_directory')", connection)
            {
                CommandTimeout = ProbeTimeoutSeconds,
            };
            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The midnight-aligned slices of [from, to), oldest first — the order that makes a partial run
    /// resumable, since every completed slice moves the measured floor backwards and the next run re-plans from
    /// it.</summary>
    public static IEnumerable<(DateTime FromUtc, DateTime ToUtc)> Slices(DateTime fromUtc, DateTime toUtc)
    {
        for (var start = fromUtc; start < toUtc;)
        {
            var end = start + SliceWidth;
            if (end > toUtc)
            {
                end = toUtc;
            }

            yield return (start, end);
            start = end;
        }
    }

    /// <summary>Per-slice refresh ceiling. A slice is one source chunk, so this is generous by design — it is a
    /// runaway guard, not an expectation.</summary>
    private const int SliceTimeoutSeconds = 60 * 60;

    private const int ProbeTimeoutSeconds = 300;
}

/// <summary>One rollup to backfill: the view, the RAW table whose oldest row is the coverage target, and whether
/// it is a hierarchical daily (sourced from its hourly, so it must be refreshed after one).</summary>
public sealed record RollupBackfillTarget(string View, string RawTable, bool IsDaily);

/// <summary>One rollup's measured state (<see cref="RollupBackfill.ProbeSql"/>).</summary>
public sealed record RollupBackfillProbe(
    DateTime? RawOldestUtc, DateTime? CoverageOldestUtc, long MaterializedBuckets, long MaterializedBytes);

/// <summary>
/// What one rollup's backfill will do: the window, its size in buckets and slices, and the disk it is estimated
/// to cost. <see cref="IsComplete"/> means there is nothing to do and <see cref="SkipReason"/> says why — which
/// is the normal outcome on a fresh store and on every re-run after a successful pass.
/// </summary>
public sealed record RollupBackfillPlan(
    string View,
    DateTime FromUtc,
    DateTime ToUtc,
    long BucketsToAdd,
    long Slices,
    long EstimatedBytes,
    bool Calibrated,
    bool IsComplete,
    string? SkipReason)
{
    public static RollupBackfillPlan Complete(string view, string reason) =>
        new(view, default, default, 0, 0, 0, Calibrated: true, IsComplete: true, SkipReason: reason);

    /// <summary>Human size for the estimate, e.g. "12.4 GB".</summary>
    public string EstimatedSize => FormatBytes(EstimatedBytes);

    /// <summary>Bytes as a human-readable size. Binary units, one decimal, invariant — this goes into operator
    /// output and a refusal message, so it must not vary with the machine's locale.</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
