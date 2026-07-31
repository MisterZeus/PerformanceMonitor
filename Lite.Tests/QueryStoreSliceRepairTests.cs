/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Services;
using PerformanceMonitorLite.Tests;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// #1912 for Lite: the pre-#1907 split slices are collapsed in the hot DuckDB table AND in the parquet
/// archive that unions into the read views.
///
/// <para>Lite gets a FULL repair where Darling gets a bounded one, because Lite's long tier IS the archive —
/// the same rows, rewritable — rather than a materialized rollup that cannot be rebuilt from raw that no
/// longer exists.</para>
///
/// <para><b>Every archive here is a scratch file this test wrote.</b> The measurements that justified this
/// work were taken against a real archive by COPYING it; the shipped code only ever rewrites an archive when
/// an operator asks, and the tests never point at one.</para>
/// </summary>
public sealed class QueryStoreSliceRepairTests : IClassFixture<SharedDuckDbFixture>, IDisposable
{
    private const int ServerId = 9121;

    private readonly DuckDbInitializer _duckDb;
    private readonly string _archivePath;
    private DuckDBConnection? _seed;
    private long _nextId = 1;

    public QueryStoreSliceRepairTests(SharedDuckDbFixture fixture)
    {
        fixture.ResetData();
        _duckDb = fixture.DuckDb;
        _archivePath = Path.Combine(Path.GetTempPath(), $"pm-1912-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_archivePath);
    }

    public void Dispose()
    {
        _seed?.Dispose();
        try
        {
            if (Directory.Exists(_archivePath))
            {
                Directory.Delete(_archivePath, recursive: true);
            }
        }
        catch (IOException)
        {
            /* A scratch directory that outlives one test run is noise, not a failure. */
        }
    }

    /// <summary>
    /// The key must be built from the columns a file ACTUALLY has. Archive files are monthly and the schema
    /// moved underneath them — <c>runtime_stats_interval_id</c> arrived with #1841 tier 2,
    /// <c>replica_role</c> with #1844/#1872 — so a file written before those carries neither. A real one on
    /// the author's machine (June 2026) is missing both.
    ///
    /// <para>WATCHED (mutation): make <c>KeyColumnsFor</c> return the full key regardless of what is present
    /// and the old-era assertions here fail — which is the cheap version of what the mutation would do in the
    /// field, where naming an absent column fails the rewrite outright and, worse, assuming the newest schema
    /// silently groups on a key that is not that era's dedup key at all.</para>
    /// </summary>
    [Fact]
    public void KeyColumns_AdaptToTheFilesOwnEra_RatherThanAssumingTheNewestSchema()
    {
        var modern = QueryStoreSliceRepairService.KeyColumnsFor(
        [
            "server_id", "database_name", "query_id", "plan_id", "runtime_stats_interval_id",
            "first_execution_time", "execution_type_desc", "replica_role", "collection_time", "execution_count",
        ]);

        Assert.Contains("runtime_stats_interval_id", modern);
        Assert.Contains("replica_role", modern);
        Assert.Equal(9, modern.Count);

        /* The pre-tier-2, pre-replica era: the tier-1 proxy key is all there has ever been for these rows. */
        var legacy = QueryStoreSliceRepairService.KeyColumnsFor(
        [
            "server_id", "database_name", "query_id", "plan_id",
            "first_execution_time", "execution_type_desc", "collection_time", "execution_count",
        ]);

        Assert.DoesNotContain("runtime_stats_interval_id", legacy);
        Assert.DoesNotContain("replica_role", legacy);
        Assert.Contains("first_execution_time", legacy);
        Assert.Contains("collection_time", legacy);
        Assert.Equal(7, legacy.Count);
    }

    /// <summary>
    /// The combining rules, which are the part that is wrong-but-invisible if they drift: a plain average of
    /// slice averages weights a 25-execution sliver the same as a 100-execution flush, and nothing downstream
    /// can tell afterwards.
    /// </summary>
    [Fact]
    public void CombineRules_MirrorTheCollectorsOwnAggregation()
    {
        Assert.Equal("SUM(execution_count)", QueryStoreSliceRepairService.CombineExpression("execution_count"));
        Assert.Equal("MAX(last_execution_time)", QueryStoreSliceRepairService.CombineExpression("last_execution_time"));
        Assert.Equal("MIN(min_duration_us)", QueryStoreSliceRepairService.CombineExpression("min_duration_us"));
        Assert.Equal("MAX(max_duration_us)", QueryStoreSliceRepairService.CombineExpression("max_duration_us"));
        Assert.Equal("ANY_VALUE(query_text)", QueryStoreSliceRepairService.CombineExpression("query_text"));

        var weighted = QueryStoreSliceRepairService.CombineExpression("avg_duration_us");
        Assert.Contains("SUM(CAST(avg_duration_us AS DOUBLE) * execution_count)", weighted, StringComparison.Ordinal);
        Assert.Contains("NULLIF(SUM(execution_count), 0)", weighted, StringComparison.Ordinal);
        Assert.DoesNotContain("AVG(", weighted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HotStore_CollapsesSplitSlices_LeavesCorrectlyCollectedRowsAlone_AndIsIdempotent()
    {
        var t = new DateTime(2026, 6, 12, 10, 0, 0, DateTimeKind.Unspecified);

        /* One interval stored as its two tied slices — the pre-#1907 shape, with the live SQL 2022 repro's
           numbers: 100 flushed + 25 in memory, truth 125, weighted mean 1871. */
        await SeedAsync(t, queryId: 1, planId: 11, intervalId: 5001, executionCount: 100, avgDurationUs: 1778);
        await SeedAsync(t, queryId: 1, planId: 11, intervalId: 5001, executionCount: 25, avgDurationUs: 2245);

        /* A correctly-collected interval that must come through untouched. */
        await SeedAsync(t, queryId: 2, planId: 22, intervalId: 5002, executionCount: 55, avgDurationUs: 500);

        var service = new QueryStoreSliceRepairService(_duckDb, _archivePath);

        var survey = await service.SurveyAsync();
        Assert.Equal(1, survey.HotGroups);
        Assert.Equal(2, survey.HotRows);
        Assert.Equal(1, survey.HotRowsRemoved);
        Assert.True(survey.HasWork);

        Assert.Equal(1, (await service.RepairAsync()).RowsRemoved);

        Assert.Equal(125L, await ScalarAsync("SELECT execution_count FROM query_store_stats WHERE query_id = 1"));
        Assert.Equal(1871L, await ScalarAsync("SELECT avg_duration_us FROM query_store_stats WHERE query_id = 1"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM query_store_stats WHERE query_id = 1"));

        /* Untouched, values intact. */
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM query_store_stats WHERE query_id = 2"));
        Assert.Equal(55L, await ScalarAsync("SELECT execution_count FROM query_store_stats WHERE query_id = 2"));

        /* Idempotent: the signature cannot match what the repair produced. */
        var second = await service.SurveyAsync();
        Assert.False(second.HasWork);
        Assert.Equal(0, (await service.RepairAsync()).RowsRemoved);
    }

    /// <summary>
    /// The archive rewrite, on an OLD-ERA file — no <c>runtime_stats_interval_id</c>, no <c>replica_role</c>,
    /// exactly the shape the real June 2026 archive has. The era-appropriate key has to engage or the rewrite
    /// either fails outright or groups on the wrong thing.
    /// </summary>
    [Fact]
    public async Task Archive_RewritesAnOldEraFile_OnTheKeyThatEraActuallyHad()
    {
        var file = Path.Combine(_archivePath, $"202605_query_store_stats.parquet");
        await WriteLegacyArchiveAsync(file);

        var service = new QueryStoreSliceRepairService(_duckDb, _archivePath);

        var survey = await service.SurveyAsync();
        var archived = Assert.Single(survey.Archive);
        Assert.False(archived.HasIntervalId);
        Assert.Equal(3, archived.Rows);
        Assert.Equal(1, archived.Groups);
        Assert.Equal(2, archived.SplitRows);
        Assert.Equal(1, archived.RowsRemoved);

        Assert.Equal(1, (await service.RepairAsync()).RowsRemoved);

        /* The split pair became one row carrying the summed count and the weighted mean; the third row, a
           different interval, is still there untouched. */
        var rows = await QueryArchiveAsync(file, "SELECT query_id, execution_count, avg_duration_us FROM read_parquet('{0}') ORDER BY query_id");
        Assert.Equal(2, rows.Count);
        Assert.Equal([1L, 125L, 1871L], rows[0]);
        Assert.Equal([2L, 55L, 500L], rows[1]);

        /* Still readable as parquet, and the file survived the swap. */
        Assert.True(File.Exists(file));
        Assert.Empty(Directory.GetFiles(_archivePath, "*.repair-tmp"));
    }

    /// <summary>
    /// A file that fails verification must leave the ORIGINAL intact — no backup copy is kept, so
    /// verify-before-promote IS the safety.
    ///
    /// <para>The failure is induced honestly rather than by mocking: a second archive file is left holding a
    /// column set the projection cannot combine, so its rewrite throws mid-run. The assertions are that the
    /// good file was still repaired, the bad file is byte-identical to what it was, and no temp file is left
    /// behind. Startup logs the failure and retries next launch; until then the union_by_name view keeps
    /// reading the un-rewritten file with #1907's read-side tie-break resolving it deterministically, so a
    /// half-repaired archive is a delay rather than a corruption.</para>
    /// </summary>
    [Fact]
    public async Task ArchiveRewrite_ThatFailsVerification_LeavesTheOriginalUntouched()
    {
        var good = Path.Combine(_archivePath, "202605_query_store_stats.parquet");
        await WriteLegacyArchiveAsync(good);

        /* A file whose execution_count is TEXT: the weighted-mean expression cannot combine it, so the COPY
           throws — a real failure of the real code path, not a stubbed one. */
        var bad = Path.Combine(_archivePath, "202604_query_store_stats.parquet");
        await WriteUncombinableArchiveAsync(bad);

        var badBefore = await File.ReadAllBytesAsync(bad);
        var service = new QueryStoreSliceRepairService(_duckDb, _archivePath);

        var result = await service.RepairAsync();

        /* Per-FILE failure, not a thrown run: the bad file is reported, the good one is still repaired. */
        Assert.False(result.FullyRepaired);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("202604_query_store_stats.parquet", failure, StringComparison.Ordinal);

        /* The bad file is exactly as it was — same bytes, not merely same length. */
        Assert.Equal(badBefore, await File.ReadAllBytesAsync(bad));

        /* No temp file survives a failure. */
        Assert.Empty(Directory.GetFiles(_archivePath, "*.repair-tmp"));

        /* And the file that CAN be repaired was, since the run processes files independently. */
        var rows = await QueryArchiveAsync(good, "SELECT query_id, execution_count FROM read_parquet('{0}') ORDER BY query_id");
        Assert.Equal(2, rows.Count);
        Assert.Equal([1L, 125L], rows[0]);
    }

    /* ─────────────────────────── helpers ─────────────────────────── */

    private async Task<DuckDBConnection> SeedConnectionAsync()
    {
        if (_seed is null)
        {
            _seed = _duckDb.CreateConnection();
            await _seed.OpenAsync();
        }
        return _seed;
    }

    private async Task SeedAsync(
        DateTime collectionTime, long queryId, long planId, long? intervalId, long executionCount, long avgDurationUs)
    {
        using var readLock = _duckDb.AcquireReadLock();
        var connection = await SeedConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO query_store_stats
    (collection_id, collection_time, server_id, server_name, database_name,
     query_id, plan_id, execution_type_desc, first_execution_time, last_execution_time,
     query_text, query_hash, execution_count, avg_duration_us, min_duration_us, max_duration_us,
     query_plan_hash, is_forced_plan, force_failure_count, runtime_stats_interval_id)
VALUES ($1, $2, $3, 'SRV', 'DB', $4, $5, 'Regular', $2, $2, 'SELECT 1', '0xH', $6, $7, 998, 4708, '0xP', false, 0, $8)";
        cmd.Parameters.Add(new DuckDBParameter { Value = _nextId++ });
        cmd.Parameters.Add(new DuckDBParameter { Value = collectionTime });
        cmd.Parameters.Add(new DuckDBParameter { Value = ServerId });
        cmd.Parameters.Add(new DuckDBParameter { Value = queryId });
        cmd.Parameters.Add(new DuckDBParameter { Value = planId });
        cmd.Parameters.Add(new DuckDBParameter { Value = executionCount });
        cmd.Parameters.Add(new DuckDBParameter { Value = avgDurationUs });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)intervalId ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Writes a parquet file in the PRE-tier-2 shape: no runtime_stats_interval_id, no replica_role. Built by
    /// listing the columns explicitly rather than by copying the current table, so the fixture cannot silently
    /// acquire whatever columns the schema grows next — the whole point is that it is an OLD file.
    /// </summary>
    private async Task WriteLegacyArchiveAsync(string file)
    {
        using var readLock = _duckDb.AcquireReadLock();
        var connection = await SeedConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
COPY (
    SELECT * FROM (VALUES
        (1::BIGINT, TIMESTAMP '2026-05-10 10:00:00', {ServerId}::INTEGER, 'SRV', 'DB', 1::BIGINT, 11::BIGINT, 'Regular',
         TIMESTAMP '2026-05-10 09:55:00', TIMESTAMP '2026-05-10 10:00:00', 'SELECT 1', '0xH', 100::BIGINT, 1778::BIGINT, 998::BIGINT, 4708::BIGINT),
        (2::BIGINT, TIMESTAMP '2026-05-10 10:00:00', {ServerId}::INTEGER, 'SRV', 'DB', 1::BIGINT, 11::BIGINT, 'Regular',
         TIMESTAMP '2026-05-10 09:55:00', TIMESTAMP '2026-05-10 10:00:00', 'SELECT 1', '0xH', 25::BIGINT, 2245::BIGINT, 998::BIGINT, 5100::BIGINT),
        (3::BIGINT, TIMESTAMP '2026-05-10 10:05:00', {ServerId}::INTEGER, 'SRV', 'DB', 2::BIGINT, 22::BIGINT, 'Regular',
         TIMESTAMP '2026-05-10 10:01:00', TIMESTAMP '2026-05-10 10:05:00', 'SELECT 2', '0xH2', 55::BIGINT, 500::BIGINT, 400::BIGINT, 600::BIGINT)
    ) AS t(collection_id, collection_time, server_id, server_name, database_name, query_id, plan_id,
           execution_type_desc, first_execution_time, last_execution_time, query_text, query_hash,
           execution_count, avg_duration_us, min_duration_us, max_duration_us)
) TO '{file.Replace("'", "''", StringComparison.Ordinal)}' (FORMAT PARQUET, COMPRESSION ZSTD)";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Reads the archive on a FRESH connection, deliberately.
    ///
    /// <para>DuckDB caches a parquet file's state per connection per path, so a connection that read a file
    /// before the repair replaced it fails on it afterwards with "No magic bytes found at end of file" — while
    /// a new connection reads the repaired file perfectly. Verified standalone. That is why the service rebuilds
    /// the archive views after rewriting, and why this reader does not reuse the seeding connection: using the
    /// stale one here would be testing DuckDB's cache rather than the repair.</para>
    /// </summary>
    /// <summary>
    /// An archive file the collapse cannot combine: <c>execution_count</c> is TEXT, so the weighted-mean
    /// expression fails on it. It still carries the split signature, so the run reaches the rewrite and
    /// throws there — which is the point, since a file that simply had no work would never be touched.
    /// </summary>
    private async Task WriteUncombinableArchiveAsync(string file)
    {
        using var readLock = _duckDb.AcquireReadLock();
        var connection = await SeedConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
COPY (
    SELECT * FROM (VALUES
        (1::BIGINT, TIMESTAMP '2026-04-10 10:00:00', {ServerId}::INTEGER, 'DB', 1::BIGINT, 11::BIGINT, 'Regular',
         TIMESTAMP '2026-04-10 09:55:00', 'not-a-number', 1778::BIGINT),
        (2::BIGINT, TIMESTAMP '2026-04-10 10:00:00', {ServerId}::INTEGER, 'DB', 1::BIGINT, 11::BIGINT, 'Regular',
         TIMESTAMP '2026-04-10 09:55:00', 'also-not', 2245::BIGINT)
    ) AS t(collection_id, collection_time, server_id, database_name, query_id, plan_id,
           execution_type_desc, first_execution_time, execution_count, avg_duration_us)
) TO '{file.Replace("'", "''", StringComparison.Ordinal)}' (FORMAT PARQUET, COMPRESSION ZSTD)";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<object[]>> QueryArchiveAsync(string file, string sqlTemplate)
    {
        using var readLock = _duckDb.AcquireReadLock();
        using var connection = _duckDb.CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            sqlTemplate,
            file.Replace("'", "''", StringComparison.Ordinal));

        var rows = new List<object[]>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new object[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values[i] = Convert.ToInt64(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
            }
            rows.Add(values);
        }
        return rows;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        using var readLock = _duckDb.AcquireReadLock();
        var connection = await SeedConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? 0L : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
