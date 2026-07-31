/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using PerformanceMonitorLite.Database;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// Lite's half of #1912: repair the Query Store rows collected before #1907, in the hot DuckDB table AND in
/// the parquet archive that unions into the read views.
///
/// <para>Before #1907 the collector stored Query Store's FLUSHED and still-IN-MEMORY slice of one interval as
/// two rows. They are ADDITIVE, so the read-side dedup — which keeps one row per key — reports a fraction of
/// the interval's work. #1907 made that choice deterministic; this makes it correct.</para>
///
/// <para><b>Lite gets a FULL repair where Darling gets a bounded one.</b> Darling can only reach rows still in
/// its 4-day raw tier, because its long tier is a materialized rollup that cannot be rebuilt from raw that no
/// longer exists. Lite's long tier IS the parquet archive — the same rows, rewritable — so the repair reaches
/// the whole history. Measured on a real archive: the Query Store files totalled ~46 MB across three monthly
/// parquets, 6.6–8.5% of their rows carried the split signature, and the largest (28 MB / 533,109 rows)
/// rewrote in under a second.</para>
///
/// <para><b>Operator-invoked, never automatic.</b> It rewrites files on disk, so it runs when a person asks
/// for it, after a survey they can read — the same principle as Darling's <c>--collapse-legacy-slices</c>.</para>
/// </summary>
public sealed class QueryStoreSliceRepairService
{
    private const string Table = "query_store_stats";

    private readonly DuckDbInitializer _duckDb;
    private readonly string _archivePath;
    private readonly ILogger<QueryStoreSliceRepairService>? _logger;

    public QueryStoreSliceRepairService(DuckDbInitializer duckDb, string archivePath, ILogger<QueryStoreSliceRepairService>? logger = null)
    {
        _duckDb = duckDb ?? throw new ArgumentNullException(nameof(duckDb));
        _archivePath = archivePath ?? throw new ArgumentNullException(nameof(archivePath));
        _logger = logger;
    }

    /// <summary>
    /// The dedup key at its FULLEST — the read side's partition plus the two columns every stored row carries.
    /// <c>collection_time</c> is what makes it the pre-fix signature rather than merely "the same interval":
    /// since #1907 the collector emits at most one row per interval per cycle, so no correctly-collected row
    /// can join such a group, which is why the repair is idempotent and safe to re-run.
    /// </summary>
    private static readonly string[] FullKeyColumns =
    [
        "server_id",
        "database_name",
        "query_id",
        "plan_id",
        "runtime_stats_interval_id",
        "first_execution_time",
        "execution_type_desc",
        "replica_role",
        "collection_time",
    ];

    /// <summary>
    /// The key columns that actually exist in <paramref name="available"/>.
    ///
    /// <para><b>This is why the archive needs per-file handling rather than one key.</b> Archive files are
    /// written per month and the SCHEMA CHANGED underneath them: <c>runtime_stats_interval_id</c> arrived with
    /// #1841 tier 2 and <c>replica_role</c> with #1844/#1872, so a file written before those has neither — a
    /// real one on this machine (June 2026) is missing both. It is also why the archive views read with
    /// <c>union_by_name</c>. Naming a column an old file does not have would fail the rewrite outright; worse,
    /// silently assuming the newest schema on an old file would group on a key that is not the era's dedup key
    /// at all, and quietly combine rows that are not slices of one interval.</para>
    /// </summary>
    internal static IReadOnlyList<string> KeyColumnsFor(IEnumerable<string> available)
    {
        ArgumentNullException.ThrowIfNull(available);

        var present = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        return FullKeyColumns.Where(present.Contains).ToList();
    }

    /// <summary>
    /// How one column combines, mirroring <c>QueryStoreCollector.BuildPayloadBody</c> and Darling's
    /// <c>QueryStoreSliceRepair</c>: the additive counter sums, every <c>avg_</c> takes the count-WEIGHTED
    /// mean, <c>min_</c>/<c>max_</c> take the extreme, <c>last_execution_time</c> is the interval's span end.
    ///
    /// <para>The weighted mean is the part that is easy to get wrong and impossible to see afterwards: Query
    /// Store stores an average and a count but never a total, so <c>avg * count</c> is what recovers a slice's
    /// total. A plain average of the slice averages weights a 25-execution sliver the same as a
    /// 100-execution flush.</para>
    /// </summary>
    internal static string CombineExpression(string column)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (string.Equals(column, "execution_count", StringComparison.OrdinalIgnoreCase))
        {
            return "SUM(execution_count)";
        }

        if (string.Equals(column, "last_execution_time", StringComparison.OrdinalIgnoreCase))
        {
            return "MAX(last_execution_time)";
        }

        if (column.StartsWith("avg_", StringComparison.OrdinalIgnoreCase))
        {
            return $"CAST(SUM(CAST({column} AS DOUBLE) * execution_count) / NULLIF(SUM(execution_count), 0) AS BIGINT)";
        }

        if (column.StartsWith("min_", StringComparison.OrdinalIgnoreCase))
        {
            return $"MIN({column})";
        }

        if (column.StartsWith("max_", StringComparison.OrdinalIgnoreCase))
        {
            return $"MAX({column})";
        }

        /* Attributes of the interval rather than measurements of it — query text, hashes, the forced-plan
           flags. Every slice carries the same value, so ANY_VALUE is correct; DuckDB has it, which is why this
           does not need Postgres' bool_or special case. */
        return $"ANY_VALUE({column})";
    }

    /// <summary>What a survey found, in the hot store and across the archive.</summary>
    public sealed record Survey(long HotGroups, long HotRows, IReadOnlyList<ArchiveFileSurvey> Archive)
    {
        public long HotRowsRemoved => HotRows - HotGroups;

        public long ArchiveRowsRemoved => Archive.Sum(a => a.RowsRemoved);

        public bool HasWork => HotGroups > 0 || Archive.Any(a => a.Groups > 0);
    }

    /// <summary>One archive file's share of the work.</summary>
    public sealed record ArchiveFileSurvey(string Path, long Rows, long Groups, long SplitRows, bool HasIntervalId)
    {
        public long RowsRemoved => SplitRows - Groups;
    }

    public async Task<Survey> SurveyAsync(CancellationToken cancellationToken = default)
    {
        using var readLock = _duckDb.AcquireReadLock();
        using var connection = _duckDb.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var hotColumns = await ColumnsOfTableAsync(connection, Table, cancellationToken);
        var hotKey = KeyColumnsFor(hotColumns);

        long groups = 0;
        long rows = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $@"
SELECT
    COUNT(*) AS groups,
    CAST(COALESCE(SUM(c), 0) AS BIGINT) AS rows_in_groups
FROM (SELECT COUNT(*) AS c FROM {Table} GROUP BY {string.Join(", ", hotKey)} HAVING COUNT(*) > 1)";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                groups = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                rows = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            }
        }

        var archive = new List<ArchiveFileSurvey>();
        foreach (var file in ArchiveFiles())
        {
            archive.Add(await SurveyArchiveFileAsync(connection, file, cancellationToken));
        }

        return new Survey(groups, rows, archive);
    }

    /// <summary>The archived Query Store parquet files, oldest name first.</summary>
    private IEnumerable<string> ArchiveFiles()
        => Directory.Exists(_archivePath)
            ? Directory.GetFiles(_archivePath, $"*_{Table}.parquet").OrderBy(f => f, StringComparer.Ordinal)
            : [];

    private static async Task<ArchiveFileSurvey> SurveyArchiveFileAsync(
        DuckDBConnection connection, string file, CancellationToken cancellationToken)
    {
        var columns = await ColumnsOfParquetAsync(connection, file, cancellationToken);
        var key = KeyColumnsFor(columns);

        using var command = connection.CreateCommand();
        command.CommandText = $@"
SELECT
    (SELECT COUNT(*) FROM read_parquet('{EscapePath(file)}')) AS total_rows,
    COUNT(*) AS groups,
    CAST(COALESCE(SUM(c), 0) AS BIGINT) AS rows_in_groups
FROM (SELECT COUNT(*) AS c FROM read_parquet('{EscapePath(file)}') GROUP BY {string.Join(", ", key)} HAVING COUNT(*) > 1)";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new ArchiveFileSurvey(file, 0, 0, 0, key.Contains("runtime_stats_interval_id"));
        }

        return new ArchiveFileSurvey(
            file,
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
            key.Contains("runtime_stats_interval_id"));
    }

    /// <summary>
    /// Collapses the hot table and every archive file that carries split slices. Returns rows removed.
    ///
    /// <para>Each archive file is rewritten to a temp sibling and swapped in only after the rewrite succeeded
    /// AND its row count checks out, so an interruption leaves the original in place rather than a truncated
    /// archive. The COPY carries <c>COMPRESSION ZSTD</c> because that is what wrote these files — omitting it
    /// silently switches codec and inflates them (measured: 28 MB becoming 69 MB).</para>
    /// </summary>
    public async Task<long> RepairAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var survey = await SurveyAsync(cancellationToken);
        long removed = 0;

        using var readLock = _duckDb.AcquireReadLock();
        using var connection = _duckDb.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        if (survey.HotGroups > 0)
        {
            var hotColumns = await ColumnsOfTableAsync(connection, Table, cancellationToken);
            var key = KeyColumnsFor(hotColumns);
            var projection = BuildProjection(hotColumns, key);

            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
CREATE OR REPLACE TEMP TABLE qs_slice_repair AS
SELECT {projection}
FROM {Table}
GROUP BY {string.Join(", ", key)}
HAVING COUNT(*) > 1";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            /* DELETE by the key, then re-insert the combined rows. IS NOT DISTINCT FROM because the key
               carries NULLABLE columns — runtime_stats_interval_id is NULL on every row collected before
               #1841 tier 2 — and = against NULL is NULL, which would skip exactly the oldest rows. */
            var match = string.Join(" AND ", key.Select(k => $"t.{k} IS NOT DISTINCT FROM r.{k}"));
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM {Table} AS t WHERE EXISTS (SELECT 1 FROM qs_slice_repair AS r WHERE {match})";
                removed += Convert.ToInt64(await command.ExecuteNonQueryAsync(cancellationToken), CultureInfo.InvariantCulture);
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"INSERT INTO {Table} SELECT * FROM qs_slice_repair";
                removed -= Convert.ToInt64(await command.ExecuteNonQueryAsync(cancellationToken), CultureInfo.InvariantCulture);
            }

            transaction.Commit();
            progress?.Report($"Hot store: {survey.HotRowsRemoved:N0} row(s) removed from {survey.HotGroups:N0} split interval(s).");
        }

        var rewroteArchive = false;
        foreach (var file in survey.Archive.Where(a => a.Groups > 0))
        {
            removed += await RewriteArchiveFileAsync(connection, file, progress, cancellationToken);
            rewroteArchive = true;
        }

        if (rewroteArchive)
        {
            await FlushExternalFileCacheAsync(connection, cancellationToken);

            /* Rebuild the archive views as well — the method the archive cycle already calls after it writes
               new files, for the same reason: the views ARE the app's read path onto the archive. */
            await _duckDb.CreateArchiveViewsAsync();
            progress?.Report("Archive views rebuilt so readers pick up the repaired files.");
        }

        return removed;
    }

    private async Task<long> RewriteArchiveFileAsync(
        DuckDBConnection connection, ArchiveFileSurvey file, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var columns = await ColumnsOfParquetAsync(connection, file.Path, cancellationToken);
        var key = KeyColumnsFor(columns);
        var projection = BuildProjection(columns, key);

        var temp = file.Path + ".repair-tmp";
        var beforeBytes = new FileInfo(file.Path).Length;

        await ArchiveService.WithRaisedCopyMemoryLimit(connection, async () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $@"
COPY (
    SELECT {projection}
    FROM read_parquet('{EscapePath(file.Path)}')
    GROUP BY {string.Join(", ", key)}
) TO '{EscapePath(temp)}' (FORMAT PARQUET, COMPRESSION ZSTD)";
            await command.ExecuteNonQueryAsync(cancellationToken);
        });

        /* Verify before swapping: a rewrite that lost rows for any reason must not replace the original. */
        long rewrittenRows;
        using (var verify = connection.CreateCommand())
        {
            verify.CommandText = $"SELECT COUNT(*) FROM read_parquet('{EscapePath(temp)}')";
            rewrittenRows = Convert.ToInt64(await verify.ExecuteScalarAsync(cancellationToken) ?? 0L, CultureInfo.InvariantCulture);
        }

        var expected = file.Rows - file.RowsRemoved;
        if (rewrittenRows != expected)
        {
            File.Delete(temp);
            throw new InvalidOperationException(
                $"Archive repair of {Path.GetFileName(file.Path)} produced {rewrittenRows:N0} rows where {expected:N0} were expected; the original was left untouched.");
        }

        File.Delete(file.Path);
        File.Move(temp, file.Path);

        var afterBytes = new FileInfo(file.Path).Length;
        var name = Path.GetFileName(file.Path);

        progress?.Report($"Archive {name}: {file.RowsRemoved:N0} row(s) removed ({FormatBytes(beforeBytes)} -> {FormatBytes(afterBytes)}).");

        /* The size can GROW despite the repair removing rows, and an operator watching their archive get
           bigger after a row-removing repair deserves the reason rather than a support ticket: parquet size is
           dominated by per-column compression of row groups, and the rewrite re-lays those out. Measured on a
           real archive file, 22,760 rows removed and the file still went 28.0 MB to 31.6 MB. */
        if (afterBytes > beforeBytes)
        {
            progress?.Report(
                $"  ({name} grew despite losing rows — parquet size follows row-group layout and per-column " +
                "compression, not row count. The data is correct and smaller; its encoding is simply less lucky.)");
        }

        _logger?.LogInformation(
            "#1912 archive repair: {File} {Removed} rows removed, {Before} -> {After} bytes",
            name, file.RowsRemoved, beforeBytes, afterBytes);

        return file.RowsRemoved;
    }

    /// <summary>
    /// Drops DuckDB's cached view of every external file, by toggling the cache off and back on.
    ///
    /// <para><b>Without this an in-place archive rewrite leaves the store unable to read its own archive.</b>
    /// DuckDB caches a parquet file's state against its PATH at INSTANCE scope — not per connection — so once
    /// a file has been read, replacing the bytes at that path makes every subsequent read fail with
    /// "No magic bytes found at end of file", on new connections too, until the process restarts. Reproduced
    /// standalone rather than inferred, and the instance scope is the part that matters: opening a fresh
    /// connection looks like a fix on a separate in-memory instance and is not one here.</para>
    ///
    /// <para>The toggle is the flush. <c>PRAGMA clear_cache</c> does not exist and
    /// <c>enable_object_cache</c> is a different cache that does not help; turning
    /// <c>enable_external_file_cache</c> off evicts the entries, and turning it back on restores normal
    /// behavior — verified, including that a subsequent connection reads the repaired file correctly and the
    /// setting is left enabled.</para>
    /// </summary>
    private static async Task FlushExternalFileCacheAsync(DuckDBConnection connection, CancellationToken cancellationToken)
    {
        foreach (var state in new[] { "false", "true" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SET enable_external_file_cache={state}";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>The full projection: key columns as-is, everything else combined, in the source's own order.</summary>
    private static string BuildProjection(IReadOnlyList<string> columns, IReadOnlyList<string> key)
    {
        var keySet = new HashSet<string>(key, StringComparer.OrdinalIgnoreCase);
        return string.Join(
            ", ",
            columns.Select(c => keySet.Contains(c) ? c : $"{CombineExpression(c)} AS {c}"));
    }

    private static async Task<IReadOnlyList<string>> ColumnsOfTableAsync(
        DuckDBConnection connection, string table, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}' ORDER BY ordinal_position";
        return await ReadStringsAsync(command, cancellationToken);
    }

    /// <summary>
    /// A parquet file's columns, read through DESCRIBE rather than <c>parquet_schema()</c> — the latter
    /// returns the schema TREE, whose root node is not a column and which therefore yields a phantom name.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ColumnsOfParquetAsync(
        DuckDBConnection connection, string file, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"DESCRIBE SELECT * FROM read_parquet('{EscapePath(file)}')";
        return await ReadStringsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(DuckDBCommand command, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(0));
        }
        return values;
    }

    private static string EscapePath(string path) => path.Replace("'", "''", StringComparison.Ordinal);

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024L
            ? $"{bytes / (1024.0 * 1024.0):N1} MB"
            : $"{bytes / 1024.0:N0} KB";
}
