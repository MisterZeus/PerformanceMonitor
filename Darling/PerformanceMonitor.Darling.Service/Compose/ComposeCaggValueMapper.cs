/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// Rewrites a measure's aggregate expression to read a continuous aggregate's PRE-aggregated columns instead of the
/// raw per-sweep delta column, when <see cref="ComposeSourceRouter"/> has chosen a CAGG tier. The value stays
/// identical because the CAGG summed exactly what the raw path would: <c>SUM(delta_x)</c> becomes
/// <c>SUM(x_sum)</c>, <c>MIN</c>/<c>MAX</c> the pre-computed <c>x_min</c>/<c>x_max</c>, and <c>AVG(delta_x)</c> the
/// exact <c>SUM(x_sum)/SUM(sample_count)</c> (the deltas are never NULL, so <c>count(*)</c> is the divisor — proven
/// exact in review). A Sum-mode ratio maps both operands the same way.
///
/// <para>Scope of this increment: the two DELTA tables (query_stats, procedure_stats) — Cumulative scalars and
/// their Sum-ratios, the CAGG columns of which follow the fixed "strip <c>delta_</c>, append <c>_sum</c>/<c>_min</c>
/// /<c>_max</c>" naming. Query Store (execution-weighted means, gauge-only aggregates) and every non-delta case
/// return false from <see cref="CanRemap"/>, so the compiler falls those panels back to raw — a later increment.</para>
/// </summary>
public static class ComposeCaggValueMapper
{
    /// <summary>The compiler's fact-table alias (mirrors <c>ComposeCompiler</c>'s private <c>FactAlias</c>).</summary>
    private const string F = "f";

    /// <summary>The delta tables whose CAGG columns follow the strip-<c>delta_</c> naming this mapper handles.</summary>
    private static bool IsDeltaCaggTable(string sourceTable) =>
        sourceTable is "query_stats" or "procedure_stats";

    /// <summary>
    /// Whether <paramref name="measure"/> at <paramref name="aggregate"/> can be re-expressed against its CAGG. The
    /// compiler MUST fall back to raw when this is false (there is no correct rollup column to read). True only for
    /// a delta-table Cumulative scalar (Sum/Avg/Min/Max) or a delta-table Sum-mode ratio.
    /// </summary>
    public static bool CanRemap(ComposeMeasure measure, ComposeAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(measure);

        if (!IsDeltaCaggTable(measure.SourceTable))
        {
            return false;
        }

        if (measure.Kind == MeasureKind.Ratio)
        {
            /* Only the execution-weighted "avg per execution" ratios, which are SUM(delta)/SUM(delta) — both
               operands re-aggregate additively on the CAGG. Weighted/Avg modes are Query Store / gauge only. */
            return measure.RatioMode == MeasureRatioMode.Sum;
        }

        return measure.Archetype == MeasureArchetype.Cumulative
            && aggregate is ComposeAggregate.Sum
                or ComposeAggregate.Avg
                or ComposeAggregate.Min
                or ComposeAggregate.Max;
    }

    /// <summary>
    /// The native (double-precision, NOT yet unit-scaled — the caller applies the same unit conversion as the raw
    /// path) aggregate expression reading the CAGG columns. Precondition: <see cref="CanRemap"/> is true for
    /// <paramref name="plan"/>'s measure + aggregate.
    /// </summary>
    public static string BuildCaggNativeExpr(PanelPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var measure = plan.Measure;

        if (measure.Kind == MeasureKind.Ratio)
        {
            var numerator = MeasureCatalog.Measure(measure.NumeratorKey!)!;
            var denominator = MeasureCatalog.Measure(measure.DenominatorKey!)!;
            return $"(CAST(SUM({F}.{SumColumn(numerator)}) AS double precision) " +
                   $"/ NULLIF(SUM({F}.{SumColumn(denominator)}), 0))";
        }

        var baseColumn = CaggBase(measure.DeltaColumn!);
        return plan.Aggregate switch
        {
            ComposeAggregate.Sum => $"CAST(SUM({F}.{baseColumn}_sum) AS double precision)",
            ComposeAggregate.Min => $"CAST(MIN({F}.{baseColumn}_min) AS double precision)",
            ComposeAggregate.Max => $"CAST(MAX({F}.{baseColumn}_max) AS double precision)",
            /* AVG(delta) == SUM(delta)/COUNT(delta) == SUM(x_sum)/SUM(sample_count), exact (deltas never NULL). */
            ComposeAggregate.Avg =>
                $"(CAST(SUM({F}.{baseColumn}_sum) AS double precision) / NULLIF(SUM({F}.sample_count), 0))",
            _ => throw new InvalidOperationException(
                $"aggregate {plan.Aggregate} is not CAGG-remappable — the caller must gate on CanRemap first."),
        };
    }

    /// <summary>A Cumulative measure's CAGG SUM column: its delta column with <c>delta_</c> stripped + <c>_sum</c>.</summary>
    private static string SumColumn(ComposeMeasure measure) => CaggBase(measure.AggregationColumn!) + "_sum";

    /// <summary>The CAGG base name for a <c>delta_*</c> column (the reshape's <c>sum(delta_x) AS x_sum</c> naming).</summary>
    private static string CaggBase(string deltaColumn) =>
        deltaColumn.StartsWith("delta_", StringComparison.Ordinal) ? deltaColumn["delta_".Length..] : deltaColumn;
}
