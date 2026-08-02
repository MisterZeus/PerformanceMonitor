/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitor.Analysis.Baselines;

namespace PerformanceMonitor.Analysis;

/// <summary>
/// The shared z-score anomaly gate — the one place the "interaction trap" decision lives so the
/// three triplicated detectors (Lite <c>AnomalyDetector</c>, Darling <c>PgAnomalyDetector</c>,
/// Dashboard <c>SqlServerAnomalyDetector</c>) cannot drift.
///
/// <para>
/// The #1486 absolute-magnitude floors and the baseline-quality gate (BaselineBucket.IsTrustworthy)
/// are COMPLEMENTARY, not both-mandatory — if they were AND-ed, a young store with a thin baseline
/// would go blind (the z-path is untrustworthy AND a trivial value clears no floor). Instead:
/// </para>
/// <list type="bullet">
///   <item>Trustworthy baseline → trust the z-score, with the magnitude floor as a sanity ceiling
///     (a huge z on a trivial value still doesn't fire).</item>
///   <item>Untrustworthy baseline → do NOT trust z (a z-score against a non-existent baseline is
///     meaningless); fire only on the HIGHER absolute-fallback bar. Not silence — absolute rules
///     preserve new-deployment coverage.</item>
/// </list>
/// The displayed sigma is always computed from whatever dispersion the baseline has (capped), so a
/// fallback-fired anomaly still shows how far above baseline it landed.
/// </summary>
public static class AnomalyGate
{
    /// <summary>Outcome of a z-vs-absolute-fallback decision.</summary>
    /// <param name="Fire">Whether the anomaly should be emitted.</param>
    /// <param name="Sigma">Capped deviation-in-sigmas for display (0 when the baseline has no dispersion).</param>
    /// <param name="LowQualityBaseline">True when the decision took the absolute-fallback path (thin baseline).</param>
    /// <param name="FallbackExceedance">Peak ÷ the absolute-fallback bar — set only on the low-quality
    /// path (0 otherwise). On that path the stored <c>Sigma</c> is the real (small) z, which the scorer's
    /// 2σ gate would zero out; the scorer grades the fire off THIS exceedance instead so the finding
    /// still surfaces. See <c>FactScorer.ScoreAnomalyFact</c>.</param>
    /// <param name="ThresholdUsed">#1743: the firing cutoff this decision was judged against —
    /// the (knob-scaled) classical threshold on the classical path, the (knob-scaled) modified-z
    /// cutoff on the robust path. The scorer anchors its severity ramp here so a family that fires
    /// at 5σ doesn't score saturated-flat against a ramp built for 2σ fires.</param>
    public readonly record struct ZDecision(bool Fire, double Sigma, bool LowQualityBaseline, double FallbackExceedance, double ThresholdUsed = 0);

    /// <summary>
    /// Decides whether a z-score anomaly fires. Callers pass the baseline's <paramref name="mean"/>,
    /// its <paramref name="effectiveStdDev"/> (already floored), and its <paramref name="isTrustworthy"/>
    /// flag (BaselineBucket carries all three; it is triplicated, so this helper takes primitives).
    /// </summary>
    /// <param name="magnitudeFloor">#1486 sanity ceiling — in the z-path the observed peak must also
    /// clear this, so a giant z on a trivial value can't surface.</param>
    /// <param name="absoluteFallbackBar">The higher bar the observed peak must clear when the baseline
    /// is untrustworthy. Should be strictly above <paramref name="magnitudeFloor"/>.</param>
    /// <param name="sigmaCap">Display cap for the reported sigma (#1486's 25σ cap).</param>
    public static ZDecision EvaluateZScore(
        double mean,
        double effectiveStdDev,
        bool isTrustworthy,
        double peak,
        double deviationThreshold,
        double magnitudeFloor,
        double absoluteFallbackBar,
        double sigmaCap)
    {
        var sigma = effectiveStdDev > 0
            ? Math.Min((peak - mean) / effectiveStdDev, sigmaCap)
            : 0.0;

        if (isTrustworthy)
        {
            // effectiveStdDev > 0 is guaranteed when trustworthy (IsTrustworthy requires it).
            var deviation = (peak - mean) / effectiveStdDev;
            var fire = deviation >= deviationThreshold && peak >= magnitudeFloor;
            return new ZDecision(fire, Math.Min(deviation, sigmaCap), LowQualityBaseline: false, FallbackExceedance: 0.0, ThresholdUsed: deviationThreshold);
        }

        // Untrustworthy baseline → absolute-threshold fallback (NOT silence). The exceedance (>= 1.0 on a
        // fire) is carried so the scorer can grade it off the absolute bar instead of the untrustworthy z.
        return new ZDecision(
            peak >= absoluteFallbackBar,
            sigma,
            LowQualityBaseline: true,
            FallbackExceedance: absoluteFallbackBar > 0 ? peak / absoluteFallbackBar : 0.0,
            ThresholdUsed: deviationThreshold);
    }

    /// <summary>
    /// #1743: the robust-first gate. When the bucket carries robust statistics (a provider that
    /// computes exact per-tier median/MAD), the deviation is the MODIFIED z-score against
    /// median/EffectiveRobustSigma at <paramref name="modifiedZThreshold"/> — fleet-measured to
    /// catch sustained deviations a burst-inflated stddev masks (a busy tenant's real 2-3x evening
    /// surge read 1.4-2.0 classical sigmas and 3.5-4.7 robust ones), while the SAME magnitude
    /// floors, trust gate, and absolute-fallback interplay apply unchanged — the floors are what
    /// keep a MAD-collapsed quiet metric sane, and the trust/fallback complementarity must never
    /// AND into blindness (see class remarks). A bucket WITHOUT robust statistics (the rollup-bound
    /// metrics, a pre-#1743 cached map, the pooled-synthesis fallback tiers) reports
    /// EffectiveRobustSigma 0 and degrades to the classical gate at
    /// <paramref name="classicalDeviationThreshold"/> — never silence, never a misfire against
    /// zeroed robust fields.
    /// </summary>
    public static ZDecision EvaluateZScore(
        BaselineBucket baseline,
        double peak,
        double classicalDeviationThreshold,
        double modifiedZThreshold,
        double magnitudeFloor,
        double absoluteFallbackBar,
        double sigmaCap)
    {
        var robustSigma = baseline.EffectiveRobustSigma;
        if (robustSigma <= 0)
        {
            return EvaluateZScore(
                baseline.Mean, baseline.EffectiveStdDev, baseline.IsTrustworthy, peak,
                classicalDeviationThreshold, magnitudeFloor, absoluteFallbackBar, sigmaCap);
        }

        var sigma = Math.Min((peak - baseline.Median) / robustSigma, sigmaCap);

        if (baseline.IsTrustworthy)
        {
            var deviation = (peak - baseline.Median) / robustSigma;
            var fire = deviation >= modifiedZThreshold && peak >= magnitudeFloor;
            return new ZDecision(fire, sigma, LowQualityBaseline: false, FallbackExceedance: 0.0, ThresholdUsed: modifiedZThreshold);
        }

        return new ZDecision(
            peak >= absoluteFallbackBar,
            sigma,
            LowQualityBaseline: true,
            FallbackExceedance: absoluteFallbackBar > 0 ? peak / absoluteFallbackBar : 0.0,
            ThresholdUsed: modifiedZThreshold);
    }
}
