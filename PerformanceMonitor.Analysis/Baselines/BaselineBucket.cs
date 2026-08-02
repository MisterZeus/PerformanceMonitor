/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

// Sub-namespace ON PURPOSE: the deprecated Full Dashboard keeps its OWN BaselineBucket/BaselineTier in
// PerformanceMonitorDashboard.Analysis, and several Dashboard files import PerformanceMonitor.Analysis
// alongside it. `using` is not recursive, so a plain `using PerformanceMonitor.Analysis;` does NOT pull
// in this .Baselines sub-namespace — which is what keeps the (untouched) Dashboard free of a CS0104
// ambiguous-reference break. Do NOT hoist these types up to PerformanceMonitor.Analysis. The two ACTIVE
// apps (Lite + Darling) consume this single shared copy; the Dashboard's frozen copy stays as-is.
namespace PerformanceMonitor.Analysis.Baselines;

/// <summary>
/// Represents the computed baseline statistics for a single time bucket. Shared by the two active
/// baseline providers (Lite <c>BaselineProvider</c>, Darling <c>PgBaselineProvider</c>) so the model
/// and its z-score floor semantics cannot drift; the store-specific baseline SQL stays per-provider.
/// </summary>
public class BaselineBucket
{
    public int HourOfDay { get; init; }
    public int DayOfWeek { get; init; }
    public double Mean { get; init; }
    public double StdDev { get; init; }
    public long SampleCount { get; init; }
    public BaselineTier Tier { get; init; }

    /// <summary>
    /// Robust center (#1743): the bucket's median. Unlike <see cref="Mean"/> it is not dragged by
    /// burst history — measured on a HammerDB store whose mean sat 17x above its median, and on the
    /// production fleet where a busy tenant's real 2-3x evening surge was invisible to the classical
    /// z at any threshold because the server's own history had inflated its stddev. Medians CANNOT
    /// be pooled from per-bucket medians, so collapsed tiers carry values the provider computed
    /// exactly per tier (GROUPING SETS), never a synthesis — see BaselineMath.SelectBucket.
    /// </summary>
    public double Median { get; init; }

    /// <summary>
    /// Robust dispersion (#1743): the median absolute deviation around <see cref="Median"/>, RAW
    /// (unscaled). Consumers convert through the 0.6745 consistency constant via
    /// <see cref="EffectiveRobustSigma"/> — storing raw keeps both providers' SQL trivially
    /// comparable to hand checks. Same per-tier exactness rule as Median.
    /// </summary>
    public double Mad { get; init; }

    /// <summary>
    /// Distinct calendar days observed in this bucket — the baseline-QUALITY signal the old
    /// quantity-only warmup gate lacked (a bucket with many samples but few distinct days is one
    /// busy day, not a trend). Carried through collapse by CollapseToHourOnly (SUM — each calendar
    /// day lands in exactly one day-of-week bucket, so the sum is exact) and CollapseToFlat (MAX —
    /// a calendar day recurs across the 24 hour buckets; MAX is a ~5 ceiling, not the true pooled
    /// distinct-day count, but a cheap proxy that avoids a second global query).
    /// </summary>
    public long DistinctDays { get; init; }

    /// <summary>
    /// Absolute dispersion floor for BOUNDED metrics (CPU %, memory %, I/O ms) so a
    /// variance-collapsed baseline can't manufacture a giant z-score. 0 for server-relative
    /// metrics (batch/query-duration/sessions/waits/blocking), which have no universal floor and
    /// rely on the detector magnitude floors + the quality gate instead. Set per metric by the provider.
    /// </summary>
    public double AbsStdDevFloor { get; init; }

    // Baseline-quality tier gates (see IsTrustworthy). Day-mins are tier-aware: a Full (hour × dow)
    // bucket only sees ~5 same-weekday dates in a 30-day window, so its day-min is a modest 3. The Flat
    // tier's DistinctDays is a MAX-over-hour-buckets proxy (CollapseToFlat) capped at that SAME ~5
    // ceiling, so it can't demand more than a Full bucket — a >=15 floor was structurally unreachable and
    // left the Flat trust branch permanently dead, so match Full at 3. Sample-mins mirror the provider's
    // per-tier selection floors.
    private const long FullSampleMin = 10;
    private const long FullDayMin = 3;
    private const long HourOnlySampleMin = 10;
    private const long HourOnlyDayMin = 10;
    private const long FlatSampleMin = 3;
    private const long FlatDayMin = 3;

    public static BaselineBucket Empty => new()
    {
        HourOfDay = -1, DayOfWeek = -1, Mean = 0, StdDev = 0,
        SampleCount = 0, DistinctDays = 0, Tier = BaselineTier.Flat
    };

    /// <summary>
    /// Returns the effective stddev with a proportional minimum floor plus, for bounded metrics,
    /// an absolute floor — both prevent division-by-zero AND a variance-collapsed baseline from
    /// producing a giant z-score. When both mean and stddev are 0 (zero activity), returns 0 —
    /// callers should skip scoring (or fall back to the absolute-threshold path).
    /// </summary>
    public double EffectiveStdDev
    {
        get
        {
            if (Mean == 0 && StdDev <= 0) return 0; // Zero activity — skip scoring
            return Math.Max(Math.Max(StdDev, Mean * 0.01), AbsStdDevFloor);
        }
    }

    /// <summary>
    /// The robust twin of <see cref="EffectiveStdDev"/> (#1743): MAD scaled to sigma-equivalent
    /// units through the 0.6745 consistency constant, with the SAME floor semantics — the
    /// proportional 1%-of-center minimum and, for bounded metrics, the absolute floor. The floors
    /// are what let a modified z-score stay sane when a quiet metric's MAD collapses toward zero
    /// (measured fleet-wide: MAD hit zero only on idle-box CPU, exactly where the magnitude floors
    /// already clamp). Returns 0 for zero-activity buckets — callers skip scoring, matching
    /// EffectiveStdDev's contract.
    /// </summary>
    public double EffectiveRobustSigma
    {
        get
        {
            if (Median == 0 && Mad <= 0) return 0; // Zero activity — skip scoring
            return Math.Max(Math.Max(Mad / 0.6745, Median * 0.01), AbsStdDevFloor);
        }
    }

    /// <summary>
    /// Honest baseline confidence (#1743), replacing the hardcoded 1.0 the FactScorer multiplied
    /// by since #1606. Two quality signals the model already carries, made explicit: the tier the
    /// selection collapsed to (a Flat verdict rests on a coarser claim than a Full one) and how
    /// close the bucket is to its tier's trust floors. An untrustworthy bucket scores 0 — its
    /// verdicts already route to the absolute-fallback path, which does not use confidence.
    /// </summary>
    public double Confidence
    {
        get
        {
            if (!IsTrustworthy) return 0.0;
            var tierFactor = Tier switch
            {
                BaselineTier.Full => 1.0,
                BaselineTier.HourOnly => 0.85,
                _ => 0.7,
            };
            /* Density: saturates at 2x the tier's sample floor, so a just-trustworthy bucket
               reads ~0.5 density and a comfortably-dense one reads 1.0. */
            var (sampleMin, _) = TrustFloors;
            var density = Math.Min(1.0, SampleCount / (2.0 * sampleMin));
            return tierFactor * density;
        }
    }

    private (long SampleMin, long DayMin) TrustFloors => Tier switch
    {
        BaselineTier.Full => (FullSampleMin, FullDayMin),
        BaselineTier.HourOnly => (HourOnlySampleMin, HourOnlyDayMin),
        _ => (FlatSampleMin, FlatDayMin),
    };

    /// <summary>
    /// Whether this baseline is dense enough to trust a z-score / ratio against. Requires real
    /// dispersion, the tier's sample floor, AND enough DISTINCT days. A low-quality baseline is NOT
    /// silenced — the detector falls back to an absolute-threshold bar instead. This gate and the
    /// #1486 magnitude floors are COMPLEMENTARY, not both-mandatory: a trustworthy baseline trusts z
    /// with the magnitude floor as a sanity ceiling; an untrustworthy one fires only on the higher
    /// absolute bar — they must never AND into blindness on a young store.
    /// </summary>
    public bool IsTrustworthy
    {
        get
        {
            if (EffectiveStdDev <= 0) return false;
            var (sampleMin, dayMin) = TrustFloors;
            return SampleCount >= sampleMin && DistinctDays >= dayMin;
        }
    }
}

public enum BaselineTier
{
    Full,     // hour + day-of-week (168 buckets)
    HourOnly, // hour only (24 buckets)
    Flat      // global mean/stddev
}
