/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins for the <c>--recompress-plan-dim</c> work class (#2076) — the statement shapes whose silent
/// regression would either corrupt the conversion or quietly break its safety properties. The end-to-end
/// behavior (convert, verify, resume, idempotence) is covered by the gated live test in
/// <c>PayloadDimensionLiveTests</c>.
/// </summary>
public sealed class PlanDimRecompressionTests
{
    [Fact]
    public void Table_IsThePlanDim_TheOnlyCompressedContentDim()
    {
        /* The verb exists because of #2069's format change, which applies to exactly one dimension. Pointing
           it anywhere else (the text dim has no gz column) would fail on the first UPDATE — but fail after
           reading a batch, so pin the constant instead. */
        Assert.Equal(PayloadDimensions.QueryPlanDimTable, PlanDimRecompression.Table);
        Assert.Equal(PayloadDimensions.CompressedContentDimTable, PlanDimRecompression.Table);
    }

    [Fact]
    public void FetchBatch_SelectsOnlyUnconvertedRows_AndTheUpdateReChecksUnderTheLock()
    {
        /* The fetch predicate IS the resume mechanism: converted rows fall out of it, so an interrupted run
           continues from the remainder and a completed one converges to a no-op. */
        Assert.Contains("WHERE query_plan_xml IS NOT NULL", PlanDimRecompression.FetchBatchSql, StringComparison.Ordinal);
        Assert.Contains("AND   query_plan_gz IS NULL", PlanDimRecompression.FetchBatchSql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $1", PlanDimRecompression.FetchBatchSql, StringComparison.Ordinal);

        /* The update re-checks the text's presence under the row lock — the fetch saw it without one. */
        Assert.Contains("AND   query_plan_dim.query_plan_xml IS NOT NULL", PlanDimRecompression.UpdateBatchSql, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateBatch_IsOneUnnestStatement_SetsGzipAndNullsText_AndNeverTouchesLastSeen()
    {
        /* One statement per batch (the #1767 unnest idiom): a 1,000-row batch is one round trip. */
        Assert.Contains("FROM unnest($1::bytea[], $2::bytea[]) AS u(digest, gz)", PlanDimRecompression.UpdateBatchSql, StringComparison.Ordinal);
        Assert.Contains("SET query_plan_gz = u.gz,", PlanDimRecompression.UpdateBatchSql, StringComparison.Ordinal);
        Assert.Contains("query_plan_xml = NULL", PlanDimRecompression.UpdateBatchSql, StringComparison.Ordinal);
        Assert.Contains("WHERE query_plan_dim.digest = u.digest", PlanDimRecompression.UpdateBatchSql, StringComparison.Ordinal);

        /* Recompression is NOT a sighting: stamping last_seen would push GC-eligible rows a full retention
           window into the future, turning a storage optimization into a retention change. */
        Assert.DoesNotContain("last_seen", PlanDimRecompression.UpdateBatchSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Survey_CountsBothForms_AndTheRelationSize()
    {
        Assert.Contains("COUNT(*) FILTER (WHERE query_plan_xml IS NOT NULL AND query_plan_gz IS NULL) AS pending", PlanDimRecompression.SurveySql, StringComparison.Ordinal);
        Assert.Contains("COUNT(*) FILTER (WHERE query_plan_gz IS NOT NULL) AS converted", PlanDimRecompression.SurveySql, StringComparison.Ordinal);
        Assert.Contains("pg_total_relation_size('query_plan_dim')", PlanDimRecompression.SurveySql, StringComparison.Ordinal);
        Assert.Contains("FROM query_plan_dim", PlanDimRecompression.SurveySql, StringComparison.Ordinal);
    }

    [Fact]
    public void VacuumFull_TargetsExactlyThePlanDim_AndTheEstimateSamplesRatherThanDetoastingEverything()
    {
        /* The compaction is the step that makes the conversion visible to the OS (#2076). Exactly this
           statement — a broader VACUUM FULL would rewrite hypertables TimescaleDB manages itself. */
        Assert.Equal("VACUUM FULL query_plan_dim", PlanDimRecompression.VacuumFullSql);

        /* The preflight estimate must SAMPLE the gzip average (LIMIT) — summing octet_length across the
           dimension detoasts the whole content, minutes of read for a one-significant-digit number. */
        Assert.Contains("LIMIT 500", PlanDimRecompression.EstimateCompactedSql, StringComparison.Ordinal);
        Assert.Contains("pg_relation_size('query_plan_dim')", PlanDimRecompression.EstimateCompactedSql, StringComparison.Ordinal);
        Assert.Contains("pg_indexes_size('query_plan_dim')", PlanDimRecompression.EstimateCompactedSql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], null, false, DarlingCliCommands.RecompressVacuumMode.Auto, false)]
    [InlineData(new[] { "--dry-run" }, null, true, DarlingCliCommands.RecompressVacuumMode.Auto, false)]
    [InlineData(new[] { "--no-vacuum-full" }, null, false, DarlingCliCommands.RecompressVacuumMode.Skip, false)]
    [InlineData(new[] { "--vacuum-full" }, null, false, DarlingCliCommands.RecompressVacuumMode.Force, false)]
    [InlineData(new[] { "C:\\x\\darling.json", "--dry-run" }, "C:\\x\\darling.json", true, DarlingCliCommands.RecompressVacuumMode.Auto, false)]
    [InlineData(new[] { "--DRY-RUN", "--VACUUM-FULL" }, null, true, DarlingCliCommands.RecompressVacuumMode.Force, false)]
    [InlineData(new[] { "--vaccum-full" }, null, false, DarlingCliCommands.RecompressVacuumMode.Auto, true)]
    public void ParseRecompressArgs_PinsTheFlagGrammar_AndRefusesTypos(
        string[] rest, string? configPath, bool dryRun, DarlingCliCommands.RecompressVacuumMode mode, bool errors)
    {
        /* The typo case is the point of refusing unknown --flags: '--vaccum-full' silently treated as a
           CONFIG PATH would fail config load with a misleading message — or worse, load a default config
           and run against the wrong store. */
        var parsed = DarlingCliCommands.ParseRecompressArgs(rest);
        if (errors)
        {
            Assert.NotNull(parsed.Error);
            return;
        }

        Assert.Null(parsed.Error);
        Assert.Equal(configPath, parsed.ConfigPath);
        Assert.Equal(dryRun, parsed.DryRun);
        Assert.Equal(mode, parsed.Mode);
    }

    [Fact]
    public void Statements_ArePostgresDialect_PositionalParams_NoTsqlIsms()
    {
        foreach (var sql in new[] { PlanDimRecompression.SurveySql, PlanDimRecompression.FetchBatchSql, PlanDimRecompression.UpdateBatchSql })
        {
            Assert.DoesNotContain("getdate", sql.ToLowerInvariant());
            Assert.DoesNotContain("N'", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
        }
    }
}
