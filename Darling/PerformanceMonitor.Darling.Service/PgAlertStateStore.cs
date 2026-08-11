/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using PerformanceMonitor.Alerting;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// Postgres-backed <see cref="IAlertStateStore"/> over the V3 <c>config_edge_trigger_watermarks</c>
/// table — the Darling twin of Lite's <c>DuckDbAlertHistoryStore</c> watermark methods, with
/// DuckDB's <c>INSERT OR REPLACE</c> upsert expressed as <c>INSERT ... ON CONFLICT ... DO UPDATE</c>
/// on the same (server_id, metric_name) primary key. Count metrics (blocking/deadlock) live in
/// the INTEGER <c>watermark</c> column with <c>watermark_time</c> NULL; the failed-job metric
/// reserves the 'Failed Agent Job' row, keeps <c>watermark</c> at 0 (the column is NOT NULL) and
/// stores the SERVER-LOCAL run time in <c>watermark_time</c> — Lite's exact row shapes, so a
/// future cross-store viewer reads both identically. All methods are failure-isolated like Lite's
/// (a broken store logs and degrades: loads return null, saves drop the write — the in-memory
/// watermark still gates the current process). <c>serverKey</c> is the storage-name hash string,
/// parsed to the <c>server_id</c> int (the DarlingAlertReadAdapter convention).
/// </summary>
public sealed class PgAlertStateStore : IAlertStateStore
{
    /* Lite's reserved failed-job row key — DuckDbAlertHistoryStore.FailedJobWatermarkMetric. */
    private const string FailedJobWatermarkMetric = "Failed Agent Job";

    private readonly NpgsqlDataSource _postgres;
    private readonly ILogger? _logger;

    public PgAlertStateStore(NpgsqlDataSource postgres, ILogger? logger = null)
    {
        _postgres = postgres ?? throw new ArgumentNullException(nameof(postgres));
        _logger = logger;
    }

    public async Task<int?> LoadEdgeTriggerWatermarkAsync(string serverKey, string metricName)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync();
            using var command = new NpgsqlCommand(@"
SELECT watermark
FROM config_edge_trigger_watermarks
WHERE server_id = $1
AND   metric_name = $2
AND   watermark_time IS NULL", connection);
            command.Parameters.AddWithValue(ParseServerKey(serverKey));
            command.Parameters.AddWithValue(metricName);

            var result = await command.ExecuteScalarAsync();
            if (result == null || result == DBNull.Value)
            {
                return null;
            }

            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            _logger?.LogError("Could not load edge-trigger watermark ({Metric}): {Message}", metricName, ex.Message);
            return null;
        }
    }

    public async Task SaveEdgeTriggerWatermarkAsync(string serverKey, string metricName, int watermark)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync();
            /* Full-row replace semantics, matching Lite's INSERT OR REPLACE: a count row always
               carries watermark_time NULL. */
            using var command = new NpgsqlCommand(@"
INSERT INTO config_edge_trigger_watermarks (server_id, metric_name, watermark, watermark_time, updated_at)
VALUES ($1, $2, $3, NULL, $4)
ON CONFLICT (server_id, metric_name) DO UPDATE SET
    watermark = EXCLUDED.watermark,
    watermark_time = NULL,
    updated_at = EXCLUDED.updated_at", connection);
            command.Parameters.AddWithValue(ParseServerKey(serverKey));
            command.Parameters.AddWithValue(metricName);
            command.Parameters.AddWithValue(watermark);
            command.Parameters.AddWithValue(NaiveUtcNow());

            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError("Could not persist edge-trigger watermark ({Metric}): {Message}", metricName, ex.Message);
        }
    }

    public async Task<DateTime?> LoadFailedJobWatermarkAsync(string serverKey)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync();
            using var command = new NpgsqlCommand(@"
SELECT watermark_time
FROM config_edge_trigger_watermarks
WHERE server_id = $1
AND   metric_name = $2
AND   watermark_time IS NOT NULL", connection);
            command.Parameters.AddWithValue(ParseServerKey(serverKey));
            command.Parameters.AddWithValue(FailedJobWatermarkMetric);

            var result = await command.ExecuteScalarAsync();
            if (result == null || result == DBNull.Value)
            {
                return null;
            }

            /* Returned in its native server-local basis — NEVER coerced to UTC (the
               IAlertStateStore contract; it compares against FailedJobInfo.RunDateTime). */
            return Convert.ToDateTime(result, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            _logger?.LogError("Could not load failed-job watermark: {Message}", ex.Message);
            return null;
        }
    }

    public async Task SaveFailedJobWatermarkAsync(string serverKey, DateTime watermark)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync();
            /* watermark (the INTEGER count column) is unused for the time-based failed-job row;
               it is non-nullable, so write 0 — Lite's exact shape. The value is server-local;
               only the Kind flag is normalized. The Kind matters, but not the way this comment
               used to claim: Npgsql does NOT reject Kind=Utc against `timestamp` on this version
               — it infers timestamptz and PostgreSQL casts into the SERVER'S zone, silently
               storing a value offset from every naive-UTC timestamp in the store (measured at
               exactly the server's UTC offset during #1969's review). The false 'it throws'
               claim here misled two reviewers in one night; the real failure mode is quiet
               zone-shift, which only a read-back value assertion catches. */
            using var command = new NpgsqlCommand(@"
INSERT INTO config_edge_trigger_watermarks (server_id, metric_name, watermark, watermark_time, updated_at)
VALUES ($1, $2, 0, $3, $4)
ON CONFLICT (server_id, metric_name) DO UPDATE SET
    watermark = 0,
    watermark_time = EXCLUDED.watermark_time,
    updated_at = EXCLUDED.updated_at", connection);
            command.Parameters.AddWithValue(ParseServerKey(serverKey));
            command.Parameters.AddWithValue(FailedJobWatermarkMetric);
            command.Parameters.AddWithValue(DateTime.SpecifyKind(watermark, DateTimeKind.Unspecified));
            command.Parameters.AddWithValue(NaiveUtcNow());

            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError("Could not persist failed-job watermark: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// #2166: stamps the alerted state onto the database's row in <c>config.database_state_expected</c>,
    /// the table that already holds this alert's per-database config.
    ///
    /// <para>UPDATE, never upsert. An INSERT here would have to supply <c>expected_state</c> (NOT NULL), and
    /// the only value available is the state being alerted ON — so a database first observed SUSPECT would
    /// get SUSPECT written as its accepted baseline. It would then stop deviating, drop out of the deviation
    /// query, be read as RECOVERED (firing a false "resolved" on a still-corrupt database), and never alert
    /// again. The seed logic refuses to baseline SUSPECT/RECOVERY_PENDING/EMERGENCY for exactly this reason;
    /// this write must not do behind its back what it declines to do in front.</para>
    ///
    /// <para>Nothing is lost by skipping the no-row case: a database with no baseline row was first observed
    /// in an integrity state, and those states are never edge-suppressed (RepeatsAreNoise is false for them),
    /// so the memory this writes is never consulted for them. The parked-database case that NEEDS the memory
    /// always has a row — it was baselined when the database was still healthy.</para>
    /// </summary>
    public async Task SaveDatabaseStateAlertedAsync(string serverKey, string databaseName, string effectiveState)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync();
            using var command = new NpgsqlCommand(@"
UPDATE config.database_state_expected
SET last_alerted_state = $3,
    last_alerted_at = (now() AT TIME ZONE 'UTC')
WHERE server_id = $1
AND   database_name = $2", connection);
            command.Parameters.AddWithValue(ParseServerKey(serverKey));
            command.Parameters.AddWithValue(databaseName);
            command.Parameters.AddWithValue(effectiveState);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            /* Same posture as the watermark writes: a failed stamp costs a duplicate alert next cycle,
               never a missed one, so it logs and continues rather than failing the sweep. */
            _logger?.LogWarning("Could not record the alerted database state for {Database}: {Message}", databaseName, ex.Message);
        }
    }

    /// <summary>
    /// #2166: forgets the alerted state when a database returns to its expected one, so the NEXT episode is
    /// a fresh transition. Without this the memory is permanent: park a database OFFLINE (alerts), bring it
    /// back (resolves), park it OFFLINE again weeks later — the stale <c>last_alerted_state</c> still reads
    /// OFFLINE, the repeat is judged already-announced, and the second parking never alerts at all. Edge
    /// triggering has to reset on the falling edge or it only ever fires once per database, forever.
    /// </summary>
    public async Task ClearDatabaseStateAlertedAsync(string serverKey, string databaseName)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync();
            using var command = new NpgsqlCommand(@"
UPDATE config.database_state_expected
SET last_alerted_state = NULL,
    last_alerted_at = NULL
WHERE server_id = $1
AND   database_name = $2", connection);
            command.Parameters.AddWithValue(ParseServerKey(serverKey));
            command.Parameters.AddWithValue(databaseName);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            /* A failed clear costs a MISSED alert on the next episode rather than a duplicate, so it is the
               more consequential of the two failures — logged at warning with the database named, same as
               its sibling, because the sweep must still finish for every other database. */
            _logger?.LogWarning("Could not clear the alerted database state for {Database}: {Message}", databaseName, ex.Message);
        }
    }

    /// <summary>Naive-UTC now, Kind-Unspecified — the product's PG timestamp discipline.</summary>
    private static DateTime NaiveUtcNow() =>
        DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    private static int ParseServerKey(string serverKey) =>
        int.Parse(serverKey, CultureInfo.InvariantCulture);
}
