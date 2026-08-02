/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using PerformanceMonitor.Analysis;
using PerformanceMonitor.Analysis.Baselines;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1743 phase 1: the robust-statistics upgrade, pinned to the MEASUREMENTS that justified it.
/// Two real datasets carry the calibration: DARLING01's live store after weeks of HammerDB bursts
/// (the self-poisoned-baseline pathology — mean 17x the median, stddev 11,994 against a MAD of 11,
/// where the engine was fully blind to a 26x workload surge), and the production fleet's apex
/// replica (the realistic form — a busy tenant's OWN history inflating its stddev enough to mask a
/// genuine sustained 2-3x evening surge at every classical threshold). The numbers in these tests
/// are those measurements verbatim, not invented fixtures — if a refactor moves any of these
/// verdicts, it has undone the finding, not broken a style preference.
/// </summary>
public sealed class RobustBaselineTests
{
    /* ── the DARLING01 calibration set (issue #1743 comment, measured 2026-07-31) ── */

    private static BaselineBucket Darling01BatchBaseline() => new()
    {
        HourOfDay = -1,
        DayOfWeek = -1,
        Tier = BaselineTier.Flat,
        Mean = 1107,
        StdDev = 11994,
        Median = 64,
        Mad = 11,
        SampleCount = 29408,
        DistinctDays = 21,
        AbsStdDevFloor = 0, // batch requests: server-relative, no absolute floor
    };

    [Fact]
    public void Darling01_TwentySixTimesSurge_ClassicalZBlind_ModifiedZUnmissable()
    {
        var baseline = Darling01BatchBaseline();
        const double loadAverage = 1682; // the HammerDB window's real average, 26x idle

        /* The shipped-blindness pin: z against the burst-poisoned mean/stddev registers under a
           tenth of a sigma, so the classical gate at ANY sane threshold cannot fire. */
        var classical = AnomalyGate.EvaluateZScore(
            baseline.Mean, baseline.EffectiveStdDev, baseline.IsTrustworthy, loadAverage,
            AnomalyThresholds.DefaultDeviationThreshold,
            AnomalyThresholds.BatchRequestFloor, AnomalyThresholds.BatchRequestFallback,
            AnomalyThresholds.SigmaDisplayCap);
        Assert.False(classical.Fire);
        Assert.True(classical.Sigma < 0.1, $"measured 0.0σ; got {classical.Sigma}");

        /* The same value against median/MAD is 99σ — unmissable (display-capped at 25). */
        var modifiedZ = BaselineMath.ModifiedZScore(baseline, loadAverage);
        Assert.True(modifiedZ > 90 && modifiedZ < 110, $"measured 99.2; got {modifiedZ:F1}");

        var robust = AnomalyGate.EvaluateZScore(
            baseline, loadAverage,
            AnomalyThresholds.DefaultDeviationThreshold,
            AnomalyThresholds.ModifiedZThreshold,
            AnomalyThresholds.BatchRequestFloor, AnomalyThresholds.BatchRequestFallback,
            AnomalyThresholds.SigmaDisplayCap);
        Assert.True(robust.Fire);
        Assert.Equal(AnomalyThresholds.SigmaDisplayCap, robust.Sigma); // capped for display
        Assert.False(robust.LowQualityBaseline);
    }

    [Fact]
    public void Darling01_IdleNoise_StaysUnderTheMagnitudeFloor()
    {
        /* The floors compose with the robust statistic instead of competing: from a median of 64
           with MAD 11, the modified-z fires from ~97/sec — which the 500/sec magnitude floor
           correctly clamps, so quiet-metric noise stays out exactly as the issue comment argued. */
        var baseline = Darling01BatchBaseline();
        const double noisyIdle = 160; // ~5.9σ modified — well over threshold, under the floor

        var decision = AnomalyGate.EvaluateZScore(
            baseline, noisyIdle,
            AnomalyThresholds.DefaultDeviationThreshold,
            AnomalyThresholds.ModifiedZThreshold,
            AnomalyThresholds.BatchRequestFloor, AnomalyThresholds.BatchRequestFallback,
            AnomalyThresholds.SigmaDisplayCap);
        Assert.False(decision.Fire);
    }

    /* ── the apex fleet measurement (52 production replicas, 2026-08-02) ── */

    private static BaselineBucket ApexBatchBaseline() => new()
    {
        HourOfDay = -1,
        DayOfWeek = -1,
        Tier = BaselineTier.Flat,
        Mean = 869,
        StdDev = 466,
        Median = 764,
        Mad = 148,
        SampleCount = 16298,
        DistinctDays = 17,
        AbsStdDevFloor = 0,
    };

    [Fact]
    public void ApexFridaySurge_MaskedFromClassicalZ_CaughtByModifiedZ()
    {
        /* The realistic production form of the pathology: apex's Friday-evening sustained surge
           (samples 1,533-1,799/sec against a median of 764) read 1.4-2.0 CLASSICAL sigmas — the
           busy tenant's own history inflates its stddev into self-masking — while the modified-z
           read 3.5-4.7 and fired. Values verbatim from the prod store's 24h backtest. */
        var baseline = ApexBatchBaseline();
        const double surgeSample = 1547; // the 19:25 UTC sample

        var classical = AnomalyGate.EvaluateZScore(
            baseline.Mean, baseline.EffectiveStdDev, baseline.IsTrustworthy, surgeSample,
            AnomalyThresholds.DefaultDeviationThreshold,
            AnomalyThresholds.BatchRequestFloor, AnomalyThresholds.BatchRequestFallback,
            AnomalyThresholds.SigmaDisplayCap);
        Assert.False(classical.Fire, "the classical gate measured 1.5σ here — under 2.0");

        var robust = AnomalyGate.EvaluateZScore(
            baseline, surgeSample,
            AnomalyThresholds.DefaultDeviationThreshold,
            AnomalyThresholds.ModifiedZThreshold,
            AnomalyThresholds.BatchRequestFloor, AnomalyThresholds.BatchRequestFallback,
            AnomalyThresholds.SigmaDisplayCap);
        Assert.True(robust.Fire, "the modified-z measured 3.6 here — over 3.5, over the 500 floor");
        Assert.InRange(robust.Sigma, 3.5, 3.7);
    }

    /* ── degradation, floors, and trust interplay ── */

    [Fact]
    public void RobustlessBucket_DegradesToTheClassicalGate_NeverMisfires()
    {
        /* A bucket without robust statistics (a rollup-bound metric, a pre-#1743 cached map, or a
           pooled-synthesis tier) reports EffectiveRobustSigma 0 — the bucket overload must return
           EXACTLY the classical verdict, not silence and not a judgment against zeroed fields. */
        var bucket = new BaselineBucket
        {
            Tier = BaselineTier.Full, HourOfDay = 12, DayOfWeek = 3,
            Mean = 100, StdDev = 10, Median = 0, Mad = 0,
            SampleCount = 50, DistinctDays = 5, AbsStdDevFloor = 0,
        };
        Assert.Equal(0, bucket.EffectiveRobustSigma);

        var viaBucket = AnomalyGate.EvaluateZScore(
            bucket, 700,
            AnomalyThresholds.DefaultDeviationThreshold, AnomalyThresholds.ModifiedZThreshold,
            AnomalyThresholds.BatchRequestFloor, AnomalyThresholds.BatchRequestFallback,
            AnomalyThresholds.SigmaDisplayCap);
        var classical = AnomalyGate.EvaluateZScore(
            bucket.Mean, bucket.EffectiveStdDev, bucket.IsTrustworthy, 700,
            AnomalyThresholds.DefaultDeviationThreshold,
            AnomalyThresholds.BatchRequestFloor, AnomalyThresholds.BatchRequestFallback,
            AnomalyThresholds.SigmaDisplayCap);

        Assert.Equal(classical, viaBucket);
        Assert.True(viaBucket.Fire); // 60σ over a real baseline, floor cleared — both paths agree
    }

    [Fact]
    public void MadCollapse_OnABoundedMetric_TheAbsoluteFloorKeepsSigmaSane()
    {
        /* Fleet-measured, MAD hit zero only on idle-box CPU — exactly where the bounded-metric
           absolute dispersion floor applies. A 3-point CPU wobble over a MAD-collapsed baseline
           must not manufacture a fire: sigma is judged against the 5.0-point floor, not zero. */
        var bucket = new BaselineBucket
        {
            Tier = BaselineTier.Flat, HourOfDay = -1, DayOfWeek = -1,
            Mean = 2.1, StdDev = 0.4, Median = 2.0, Mad = 0.0,
            SampleCount = 7000, DistinctDays = 4,
            AbsStdDevFloor = 5.0, // BaselineMath.AbsStdDevFloorFor(MetricNames.Cpu)
        };
        Assert.Equal(5.0, bucket.EffectiveRobustSigma);

        var decision = AnomalyGate.EvaluateZScore(
            bucket, 5.0,
            AnomalyThresholds.DefaultDeviationThreshold, AnomalyThresholds.ModifiedZThreshold,
            AnomalyThresholds.CpuFloorPct, AnomalyThresholds.CpuFallbackPct,
            AnomalyThresholds.SigmaDisplayCap);
        Assert.False(decision.Fire);
    }

    [Fact]
    public void UntrustworthyBaseline_RobustPath_FiresOnlyOnTheAbsoluteBar()
    {
        /* The trust/fallback complementarity is unchanged by the robust statistic: a thin baseline
           does not trust ANY deviation score and fires only on the higher absolute bar. */
        var bucket = new BaselineBucket
        {
            Tier = BaselineTier.Full, HourOfDay = 9, DayOfWeek = 2,
            Mean = 100, StdDev = 10, Median = 95, Mad = 8,
            SampleCount = 4, DistinctDays = 1, AbsStdDevFloor = 0, // below Full trust floors
        };
        Assert.False(bucket.IsTrustworthy);

        var under = AnomalyGate.EvaluateZScore(
            bucket, 2000,
            AnomalyThresholds.DefaultDeviationThreshold, AnomalyThresholds.ModifiedZThreshold,
            AnomalyThresholds.BatchRequestFloor, AnomalyThresholds.BatchRequestFallback,
            AnomalyThresholds.SigmaDisplayCap);
        Assert.False(under.Fire); // enormous modified-z, but under the 5000 fallback bar
        Assert.True(under.LowQualityBaseline);

        var over = AnomalyGate.EvaluateZScore(
            bucket, 6000,
            AnomalyThresholds.DefaultDeviationThreshold, AnomalyThresholds.ModifiedZThreshold,
            AnomalyThresholds.BatchRequestFloor, AnomalyThresholds.BatchRequestFallback,
            AnomalyThresholds.SigmaDisplayCap);
        Assert.True(over.Fire);
        Assert.True(over.FallbackExceedance >= 1.0);
    }

    /* ── tier selection over exact (GROUPING SETS) sentinel buckets ── */

    [Fact]
    public void SelectBucket_PrefersTheExactSentinelTier_OverPooledSynthesis()
    {
        /* Medians cannot be pooled, so when the provider supplies exact hour-only/flat tiers under
           sentinel keys, SelectBucket must hand back THOSE (robust fields intact) instead of
           synthesizing a pooled bucket whose Median/Mad would be zero. */
        var map = new Dictionary<(int, int), BaselineBucket>
        {
            // Sparse full bucket: below CollapseThreshold, so selection must move down a tier.
            [(14, 2)] = new BaselineBucket { HourOfDay = 14, DayOfWeek = 2, Tier = BaselineTier.Full, Mean = 100, StdDev = 5, Median = 99, Mad = 4, SampleCount = 4, DistinctDays = 2 },
            // A sibling full bucket the POOLED path would have folded in.
            [(14, 3)] = new BaselineBucket { HourOfDay = 14, DayOfWeek = 3, Tier = BaselineTier.Full, Mean = 300, StdDev = 5, Median = 299, Mad = 4, SampleCount = 40, DistinctDays = 5 },
            // The provider's EXACT hour-only tier for hour 14.
            [(14, -1)] = new BaselineBucket { HourOfDay = 14, DayOfWeek = -1, Tier = BaselineTier.HourOnly, Mean = 250, StdDev = 80, Median = 240, Mad = 60, SampleCount = 44, DistinctDays = 12 },
            // The provider's EXACT flat tier.
            [(-1, -1)] = new BaselineBucket { HourOfDay = -1, DayOfWeek = -1, Tier = BaselineTier.Flat, Mean = 180, StdDev = 90, Median = 170, Mad = 70, SampleCount = 900, DistinctDays = 17 },
        };

        var selected = BaselineMath.SelectBucket(map, 14, 2);
        Assert.Equal(BaselineTier.HourOnly, selected.Tier);
        Assert.Equal(240, selected.Median); // the exact tier's robust center, not a zeroed synthesis
        Assert.Equal(60, selected.Mad);

        /* An hour with no data at all falls through to the exact flat sentinel. */
        var flat = BaselineMath.SelectBucket(map, 3, 6);
        Assert.Equal(BaselineTier.Flat, flat.Tier);
        Assert.Equal(170, flat.Median);
    }

    [Fact]
    public void SelectBucket_WithoutSentinels_PooledFallback_ExcludesNothingAndZeroesRobustFields()
    {
        /* A pre-#1743 map (no sentinel keys): pooling still works exactly as before, and the
           synthesized bucket's zeroed robust fields are what routes the gate down its classical
           path — degradation, not misfire. */
        var map = new Dictionary<(int, int), BaselineBucket>
        {
            [(14, 2)] = new BaselineBucket { HourOfDay = 14, DayOfWeek = 2, Tier = BaselineTier.Full, Mean = 100, StdDev = 5, SampleCount = 6, DistinctDays = 2 },
            [(14, 3)] = new BaselineBucket { HourOfDay = 14, DayOfWeek = 3, Tier = BaselineTier.Full, Mean = 100, StdDev = 5, SampleCount = 6, DistinctDays = 2 },
        };

        var selected = BaselineMath.SelectBucket(map, 14, 2);
        Assert.Equal(BaselineTier.HourOnly, selected.Tier);
        Assert.Equal(12, selected.SampleCount);
        Assert.Equal(0, selected.Median);
        Assert.Equal(0, selected.EffectiveRobustSigma);
    }

    /* ── honest confidence ── */

    [Fact]
    public void Confidence_TierAndDensity_UntrustworthyIsZero()
    {
        var fullDense = new BaselineBucket { Tier = BaselineTier.Full, Mean = 100, StdDev = 10, SampleCount = 20, DistinctDays = 5 };
        Assert.Equal(1.0, fullDense.Confidence, precision: 3);

        var fullThin = new BaselineBucket { Tier = BaselineTier.Full, Mean = 100, StdDev = 10, SampleCount = 10, DistinctDays = 3 };
        Assert.Equal(0.5, fullThin.Confidence, precision: 3);

        var flat = new BaselineBucket { Tier = BaselineTier.Flat, Mean = 100, StdDev = 10, SampleCount = 600, DistinctDays = 17 };
        Assert.Equal(0.7, flat.Confidence, precision: 3);

        var untrustworthy = new BaselineBucket { Tier = BaselineTier.Full, Mean = 100, StdDev = 10, SampleCount = 4, DistinctDays = 1 };
        Assert.Equal(0.0, untrustworthy.Confidence);
    }

    /* ── the scorer keeps the catches the detector makes ── */

    private static double Score(Fact fact)
    {
        new FactScorer().ScoreAll(new List<Fact> { fact });
        return fact.BaseSeverity;
    }

    [Fact]
    public void WaitProfileScoring_GradesOffModifiedZ_SoTheMaskedSurgeClassSurvives()
    {
        /* Without this arm, a wait-profile fact fired at modified-z 6 with a ratio of 1.5 would be
           zeroed by the ratio floor immediately after being caught — the fleet backtest's entire
           modz-only class (1,281 samples the ratio never saw) silently dropped at scoring. */
        var caught = new Fact
        {
            Source = "anomaly",
            Key = "ANOMALY_WAIT_PROFILE",
            Value = 1,
            Metadata = new Dictionary<string, double> { ["modified_z"] = 6.0, ["ratio"] = 1.5, ["is_new"] = 0 },
        };
        Assert.True(Score(caught) >= 0.5);

        var underCutoff = new Fact
        {
            Source = "anomaly",
            Key = "ANOMALY_WAIT_PROFILE",
            Value = 1,
            Metadata = new Dictionary<string, double> { ["modified_z"] = 4.0, ["ratio"] = 1.5, ["is_new"] = 0 },
        };
        Assert.Equal(0.0, Score(underCutoff));

        /* Pre-#1743 facts (no modified_z) keep the ratio ramp; the is_new sentinel ratio must keep
           scoring through the ratio path even though its fact now carries a modified_z. */
        var legacy = new Fact
        {
            Source = "anomaly",
            Key = "ANOMALY_WAIT_PROFILE",
            Value = 1,
            Metadata = new Dictionary<string, double> { ["ratio"] = 6.0 },
        };
        Assert.True(Score(legacy) > 0.5);

        var isNew = new Fact
        {
            Source = "anomaly",
            Key = "ANOMALY_WAIT_PROFILE",
            Value = 1,
            Metadata = new Dictionary<string, double> { ["modified_z"] = 2.0, ["ratio"] = AnomalyThresholds.NoBaselineRatio, ["is_new"] = 1 },
        };
        Assert.True(Score(isNew) > 0.5);
    }

    /* ── the heavy-tail threshold routing ── */

    [Fact]
    public void ModifiedZThresholds_HeavyTailFamiliesAtFive_EverythingElseAtThreePointFive()
    {
        Assert.Equal(5.0, AnomalyThresholds.ModifiedZThresholdFor(MetricNames.WaitStats));
        Assert.Equal(5.0, AnomalyThresholds.ModifiedZThresholdFor(MetricNames.WaitMsPerSec));
        Assert.Equal(5.0, AnomalyThresholds.ModifiedZThresholdFor(MetricNames.QueryDuration));
        Assert.Equal(3.5, AnomalyThresholds.ModifiedZThresholdFor(MetricNames.BatchRequests));
        Assert.Equal(3.5, AnomalyThresholds.ModifiedZThresholdFor(MetricNames.Cpu));
        Assert.Equal(3.5, AnomalyThresholds.ModifiedZThresholdFor(MetricNames.SessionCount));
    }
}
