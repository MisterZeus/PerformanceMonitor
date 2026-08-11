/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// Reads the PostgreSQL statement store (<c>pg_statement_stats</c>) — the top-queries surface for an
/// Aurora target.
/// </summary>
public static class DarlingPgStatementReader
{
    /// <summary>
    /// One row per query shape over the window.
    /// <para><c>QueryId</c> rather than text: the collector does not store statement text yet, and
    /// <c>queryid</c> is the join key anyway. It is stable within a major version but NOT across one, so
    /// a consumer must not treat it as a permanent identifier.</para>
    /// </summary>
    public sealed record PgStatementRow(
        long QueryId,
        long DatabaseId,
        long Calls,
        long TotalExecTimeMs,
        long RowsReturned,
        double MaxExecTimeMs,
        long SharedBlocksHit,
        long SharedBlocksRead,
        long StorageBlocksRead,
        long OrcacheBlocksHit,
        long TempBlocksRead,
        long TempBlocksWritten,
        long WalBytes,
        long MaxPeakMemBytes);

    /// <summary>
    /// Aggregated from the DELTA columns. Summing the cumulative counters instead would multiply each
    /// query's entire lifetime history by the number of snapshots in the window.
    /// <para>The block and WAL columns are cumulative-only in the store — no deltas are kept for them —
    /// so they are reported as the window's MAX rather than a sum: the max is the latest cumulative
    /// reading, which is at least a true number, where a sum across snapshots would be meaningless.
    /// That is a real limitation of this first cut and the reason the ratio below is computed from
    /// maxima consistently rather than mixing a delta numerator with a cumulative denominator.</para>
    /// <para>$1 server_id, $2/$3 window (naive UTC).</para>
    /// </summary>
    public const string PgTopQueriesSql = """
        SELECT
            queryid,
            database_id,
            CAST(SUM(delta_calls) AS bigint) AS calls,
            CAST(SUM(delta_total_exec_time_ms) AS bigint) AS total_exec_time_ms,
            CAST(SUM(delta_rows) AS bigint) AS rows_returned,
            MAX(max_exec_time_ms) AS max_exec_time_ms,
            CAST(MAX(shared_blks_hit) AS bigint) AS shared_blks_hit,
            CAST(MAX(shared_blks_read) AS bigint) AS shared_blks_read,
            CAST(MAX(storage_blks_read) AS bigint) AS storage_blks_read,
            CAST(MAX(orcache_blks_hit) AS bigint) AS orcache_blks_hit,
            CAST(MAX(temp_blks_read) AS bigint) AS temp_blks_read,
            CAST(MAX(temp_blks_written) AS bigint) AS temp_blks_written,
            CAST(MAX(wal_bytes) AS bigint) AS wal_bytes,
            CAST(MAX(max_exec_peakmem_bytes) AS bigint) AS max_exec_peakmem_bytes
        FROM pg_statement_stats
        WHERE server_id = $1
        AND   collection_time >= $2
        AND   collection_time <= $3
        GROUP BY queryid, database_id
        HAVING SUM(delta_total_exec_time_ms) > 0
        ORDER BY SUM(delta_total_exec_time_ms) DESC
        LIMIT 50
        """;

    public static async Task<List<PgStatementRow>> GetPgTopQueriesAsync(
        NpgsqlDataSource postgres, int serverId, DateTime startUtc, DateTime endUtc,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<PgStatementRow>();
        await using var command = postgres.CreateCommand(PgTopQueriesSql);
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(startUtc);
        command.Parameters.AddWithValue(endUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PgStatementRow(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
                reader.IsDBNull(10) ? 0 : reader.GetInt64(10),
                reader.IsDBNull(11) ? 0 : reader.GetInt64(11),
                reader.IsDBNull(12) ? 0 : reader.GetInt64(12),
                reader.IsDBNull(13) ? 0 : reader.GetInt64(13)));
        }

        return rows;
    }
}
