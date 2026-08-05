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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Models;

namespace PerformanceMonitorLite.Services;

public partial class RemoteCollectorService
{
    /* ── #2058: Lite's Query Store backfill worker — the twin of Darling's QueryStoreBackfill ──
       The stored contract (collector_state identity, hole codec, tail/hole semantics) is the SHARED
       QueryStoreBackfillState; what is per-SKU here is the HORIZON and the host plumbing:

       - Horizon: Lite has no CAGGs or tiered retention to respect, so the staging boundary is the
         resolved query_store RETENTION itself (per-server schedule → default 30 days) — derived,
         never a second hand-maintained number. A backfilled row lands with a BACKDATED
         collection_time (the slice ceiling), so retention purges it on the same clock as live
         rows, and the parquet archive sweeps it like any other aged row (the v_ views union hot +
         archive, so a deep-backfilled row reads identically wherever it currently lives). That is
         why Lite can safely dig ~30 days where Darling stops at its raw tier's 3.
       - Tick: rides CollectionBackgroundService's IfDue ladder (Lite's idiom — archival, retention,
         analysis all live there), one byte-budgeted slice per server per due-tick, sequentially:
         sequence is the concurrency bound, and the slice's own SQL connection never touches the
         collection paths.
       - The live watermark is untouched by construction: MAX(last_execution_time) cannot see the
         OLDER rows backfill ships, so the two paths never race (the #1960 constraint). */

    /// <summary>Fallback horizon when the schedule resolve fails — the shipped query_store
    /// retention default.</summary>
    private const int BackfillFallbackRetentionDays = 30;

    /// <summary>
    /// Runs AT MOST one backfill slice per enabled server: the first database found with a pending
    /// hole or an undrained first-contact tail gets one byte-budgeted slice; everything else waits
    /// for a later tick. Per-server failures log and skip — one unreachable server never stalls the
    /// sweep. Called from CollectionBackgroundService on its own due-cadence.
    /// </summary>
    public async Task RunQueryStoreBackfillTickAsync(CancellationToken cancellationToken)
    {
        foreach (var server in _serverManager.GetEnabledServers())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await RunQueryStoreBackfillSliceAsync(server, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("query_store backfill slice on '{Server}' failed: {Message}",
                    server.DisplayName, ex.Message);
            }
        }
    }

    /// <summary>One server's scan-and-slice — the twin of Darling's RunServerSliceAsync, on Lite's
    /// plumbing (DuckDB reads, ServerConnection credentials, the shared appender write).</summary>
    internal async Task<bool> RunQueryStoreBackfillSliceAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        var status = _serverManager.GetConnectionStatus(server.Id);
        var target = new CollectorTargetInfo
        {
            IsAzureSqlDb = status.SqlEngineEdition == 5,
            IsAzureManagedInstance = status.SqlEngineEdition == 8,
            IsAwsRds = status.IsAwsRds,
            SqlMajorVersion = status.SqlMajorVersion,
            HasMsdbAccess = status.HasMsdbAccess,
        };

        if (!QueryStoreCollector.Instance.AppliesTo(target))
        {
            return false;
        }

        var serverId = GetDeterministicHashCode(GetServerNameForStorage(server));
        var state = await GetCollectorStateAsync(serverId, QueryStoreBackfillState.StateCollectorName, cancellationToken);
        var databases = await GetBackfillCandidateDatabasesAsync(serverId, cancellationToken);

        var nowUtc = DateTime.UtcNow;
        var floorLimit = nowUtc - BackfillHorizonFor(server);

        foreach (var databaseName in databases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            /* Holes before the tail: a recorded outage gap is the history closest to expiring. */
            if (state.TryGetValue(QueryStoreBackfillState.HoleKeyPrefix + databaseName, out var encoded)
                && QueryStoreBackfillState.TryDecodeHole(encoded, out var holeFrom, out var holeTo))
            {
                if (holeTo <= floorLimit)
                {
                    await DeleteCollectorStateKeyAsync(serverId, QueryStoreBackfillState.StateCollectorName, QueryStoreBackfillState.HoleKeyPrefix + databaseName, cancellationToken);
                    continue;
                }

                var holeFloor = holeFrom > floorLimit ? holeFrom : floorLimit;
                await RunBackfillSliceAsync(server, serverId, target, databaseName, holeFloor, holeTo, isHole: true, cancellationToken);
                return true;
            }

            if (state.ContainsKey(QueryStoreBackfillState.DoneKeyPrefix + databaseName))
            {
                continue;
            }

            /* The derived ceiling: everything at or above the stored MIN shipped complete. Null
               means the live path has not made first contact for this database yet. */
            var storedFloor = await GetMinCollectedTimeForDatabaseAsync(
                serverId, QueryStoreCollector.Instance.TargetTable, "last_execution_time", "database_name", databaseName, cancellationToken);
            if (storedFloor is null)
            {
                continue;
            }

            if (storedFloor <= floorLimit)
            {
                await SaveCollectorStateAsync(serverId, QueryStoreBackfillState.StateCollectorName,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [QueryStoreBackfillState.DoneKeyPrefix + databaseName] = nowUtc.ToString("o", CultureInfo.InvariantCulture)
                    }, cancellationToken);
                continue;
            }

            await RunBackfillSliceAsync(server, serverId, target, databaseName, floorLimit, storedFloor.Value, isHole: false, cancellationToken);
            return true;
        }

        return false;
    }

    /// <summary>Lite's staging boundary: the resolved query_store retention for this server (its
    /// own schedule override or the default), floored at 1 day — see the partial doc for why
    /// retention IS the right horizon here.</summary>
    internal TimeSpan BackfillHorizonFor(ServerConnection server)
    {
        var days = _scheduleManager.GetScheduleForServer(server.Id, QueryStoreCollector.Instance.Name)?.RetentionDays
            ?? BackfillFallbackRetentionDays;
        return TimeSpan.FromDays(Math.Max(1, days));
    }

    private async Task RunBackfillSliceAsync(
        ServerConnection server, int serverId, CollectorTargetInfo target, string databaseName,
        DateTime floorUtc, DateTime ceilingUtc, bool isHole, CancellationToken cancellationToken)
    {
        var definition = QueryStoreCollector.Instance;
        var context = new CollectorContext
        {
            ServerId = serverId,
            ServerName = GetServerNameForStorage(server),
            CollectionTime = DateTime.UtcNow,
            Deltas = _deltaCalculator,
            Target = target,
            ExcludedDatabases = server.ExcludedDatabases?.ToArray() ?? Array.Empty<string>(),
            /* Lite never captures plan XML — its byte budget is query text alone. */
        };

        var timeout = definition.CommandTimeoutSecondsOverride ?? CommandTimeoutSeconds;
        var rows = new List<QueryStoreCollector.Row>();

        if (target.IsAzureSqlDb)
        {
            /* Azure arm: the window travels as command parameters on a per-database connection —
               same contract as Darling's, same shared BuildBackfillQuery. */
            context.CurrentDatabaseName = databaseName;
            var azurePlan = definition.BuildBackfillQuery(context, floorUtc, ceilingUtc);
            using var dbConnection = await OpenAzureDatabaseConnectionAsync(server, databaseName, cancellationToken);
            using var dbCommand = new SqlCommand(azurePlan.Text, dbConnection) { CommandTimeout = timeout };
            AddCollectorParameters(dbCommand, azurePlan);
            using var dbReader = await dbCommand.ExecuteReaderAsync(cancellationToken);
            rows = await definition.ReadAsync(dbReader, context, cancellationToken);
        }
        else
        {
            using var sqlConnection = new SqlConnection(_serverManager.CredentialResolver.GetConnectionString(server));
            await sqlConnection.OpenAsync(cancellationToken);

            /* Same best-effort 10-second PRODUCTVERSION probe as the live enumeration path. */
            var probePlan = definition.BuildEnumerationProbe(context);
            if (probePlan is not null)
            {
                try
                {
                    using var probeCommand = new SqlCommand(probePlan.Text, sqlConnection) { CommandTimeout = 10 };
                    var probeResult = await probeCommand.ExecuteScalarAsync(cancellationToken);
                    if (probeResult is not null && probeResult != DBNull.Value)
                    {
                        context.EnumerationProbeResult = probeResult;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug("Backfill version probe on '{Server}' failed; using defaults: {Error}",
                        server.DisplayName, ex.Message);
                }
            }

            var plan = definition.BuildBackfillPerItemQuery(databaseName, context, floorUtc, ceilingUtc);
            using var command = new SqlCommand(plan.Text, sqlConnection) { CommandTimeout = timeout };
            AddCollectorParameters(command, plan);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await definition.ReadItemAsync(databaseName, reader, rows, context, cancellationToken);
        }

        if (rows.Count == 0)
        {
            if (isHole)
            {
                await DeleteCollectorStateKeyAsync(serverId, QueryStoreBackfillState.StateCollectorName, QueryStoreBackfillState.HoleKeyPrefix + databaseName, cancellationToken);
            }
            else
            {
                await SaveCollectorStateAsync(serverId, QueryStoreBackfillState.StateCollectorName,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [QueryStoreBackfillState.DoneKeyPrefix + databaseName] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                    }, cancellationToken);
            }

            _logger?.LogInformation(
                "query_store backfill on '{Server}' [{Database}]: nothing retained below {Ceiling:o} — {Range} complete.",
                server.DisplayName, databaseName, ceilingUtc, isHole ? "hole" : "tail");
            return;
        }

        /* Backdated to the slice ceiling — rows land beside their own activity, and retention/
           archival age them on the same clock as live rows. One batch, the shared appender path. */
        int written;
        using (var duckConnection = _duckDb.CreateConnection())
        {
            await duckConnection.OpenAsync(cancellationToken);
            written = WriteBatch(duckConnection, definition, rows, serverId, context.ServerName, ceilingUtc, context);
        }

        var boundary = context.PerItemShippedBoundary;
        if (isHole)
        {
            if (boundary is null || boundary <= floorUtc)
            {
                await DeleteCollectorStateKeyAsync(serverId, QueryStoreBackfillState.StateCollectorName, QueryStoreBackfillState.HoleKeyPrefix + databaseName, cancellationToken);
            }
            else
            {
                await SaveCollectorStateAsync(serverId, QueryStoreBackfillState.StateCollectorName,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [QueryStoreBackfillState.HoleKeyPrefix + databaseName] = QueryStoreBackfillState.EncodeHole(floorUtc, boundary.Value)
                    }, cancellationToken);
            }
        }
        else if (boundary is not null && boundary <= floorUtc)
        {
            await SaveCollectorStateAsync(serverId, QueryStoreBackfillState.StateCollectorName,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [QueryStoreBackfillState.DoneKeyPrefix + databaseName] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                }, cancellationToken);
        }

        _logger?.LogInformation(
            "query_store backfill on '{Server}' [{Database}]: shipped {Rows} rows ({ShippedMB:F1}MB) down to {Boundary:o} ({Range}, ceiling {Ceiling:o}).",
            server.DisplayName, databaseName, written,
            context.PerItemTextBytesShipped / (1024.0 * 1024.0),
            boundary ?? floorUtc, isHole ? "hole" : "tail", ceilingUtc);
    }

    /// <summary>Binds a CollectorQuery's parameters onto a SqlCommand — Lite's slice commands share
    /// the definition's typed parameters the same way the runner's command factory does.</summary>
    private static void AddCollectorParameters(SqlCommand command, CollectorQuery plan)
    {
        foreach (var p in plan.Parameters)
        {
            command.Parameters.Add(new SqlParameter(p.Name, System.Data.SqlDbType.DateTime2) { Value = p.Value ?? DBNull.Value });
        }
    }

    /// <summary>Databases that shipped query_store rows recently — the backfill universe, derived
    /// from the store so no live enumeration is needed.</summary>
    private async Task<List<string>> GetBackfillCandidateDatabasesAsync(int serverId, CancellationToken cancellationToken)
    {
        var databases = new List<string>();
        try
        {
            using var conn = _duckDb.CreateConnection();
            await conn.OpenAsync(cancellationToken);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT database_name FROM query_store_stats WHERE server_id = $1 AND collection_time > $2 ORDER BY database_name";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = serverId });
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = DateTime.UtcNow.AddDays(-7) });
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0))
                {
                    databases.Add(reader.GetString(0));
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "query_store backfill candidate read failed; skipping this tick");
        }

        return databases;
    }

    /// <summary>MIN(last_execution_time) stored for one database — the derived backfill ceiling,
    /// the mirror of <see cref="GetLastCollectedTimeForDatabaseAsync"/>. Null skips this tick;
    /// failure never invents a boundary.</summary>
    private async Task<DateTime?> GetMinCollectedTimeForDatabaseAsync(
        int serverId, string tableName, string columnName, string databaseColumnName, string databaseName, CancellationToken cancellationToken)
    {
        try
        {
            using var conn = _duckDb.CreateConnection();
            await conn.OpenAsync(cancellationToken);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT MIN({columnName}) FROM {tableName} WHERE server_id = $1 AND {databaseColumnName} = $2";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = serverId });
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = databaseName });
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            if (result is DateTime dt)
            {
                return dt;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "query_store backfill floor read failed for [{Database}]; skipping this tick", databaseName);
        }

        return null;
    }

    /// <summary>Deletes ONE collector_state key — the retirement path for a serviced or expired
    /// hole record (#2058), the DuckDB twin of Darling's DeleteCollectorStateKeyAsync. Best-effort:
    /// a failed delete leaves the row and the next tick re-derives the same verdict.</summary>
    protected async Task DeleteCollectorStateKeyAsync(
        int serverId, string collectorName, string stateKey, CancellationToken cancellationToken)
    {
        try
        {
            using var conn = _duckDb.CreateConnection();
            await conn.OpenAsync(cancellationToken);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM collector_state WHERE server_id = $1 AND collector_name = $2 AND state_key = $3";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = serverId });
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = collectorName });
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter { Value = stateKey });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Deleting collector state {Key} for {Collector} failed; next tick re-derives", stateKey, collectorName);
        }
    }

    /// <summary>Records a clamp-opened Query Store hole for the backfill worker (#2058), under the
    /// WORKER's collector_state name — merged wider with any pending hole so a repeat outage cannot
    /// overwrite an unserviced one. Best-effort: a lost record is a lost backfill opportunity,
    /// never wrong data — the clamp WARNING already disclosed the hole. Darling's twin lives in
    /// DarlingCollectorRunner.</summary>
    private async Task RecordQueryStoreBackfillHoleAsync(
        int serverId, string databaseName, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        try
        {
            var key = QueryStoreBackfillState.HoleKeyPrefix + databaseName;
            var existing = await GetCollectorStateAsync(serverId, QueryStoreBackfillState.StateCollectorName, cancellationToken);
            var merged = QueryStoreBackfillState.MergeHole(existing.TryGetValue(key, out var encoded) ? encoded : null, fromUtc, toUtc);
            await SaveCollectorStateAsync(
                serverId, QueryStoreBackfillState.StateCollectorName,
                new Dictionary<string, string>(StringComparer.Ordinal) { [key] = QueryStoreBackfillState.EncodeHole(merged.FromUtc, merged.ToUtc) },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Recording query_store backfill hole for [{Database}] failed; the clamp WARNING remains the disclosure", databaseName);
        }
    }
}
