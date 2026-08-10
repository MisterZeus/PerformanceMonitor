/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Common;
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

    /// <summary>#2148: per-server abandonment guards, the Darling loop's exact shape — keyed by server
    /// id so one wedged server never blocks its neighbors, never pruned (one small object per server
    /// ever monitored).</summary>
    private readonly ConcurrentDictionary<int, AbandonableStep> _backfillSliceSteps = new();

    /// <summary>#2148: the hard ceiling ONE server's slice may hold the tick — a healthy slice is one
    /// 30s-capped statement plus DuckDB writes, so this is a defect signal, never jitter. Per SERVER
    /// deliberately (review catch, round 2): a shared tick-level deadline would both stall every
    /// server's backfill behind one wedge AND false-trip as fleet size grows.</summary>
    private static readonly TimeSpan BackfillSliceDeadline = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Runs AT MOST one backfill slice per enabled server: the first database found with a pending
    /// hole or an undrained first-contact tail gets one byte-budgeted slice; everything else waits
    /// for a later tick. Per-server failures log and skip, and per-server WEDGES are abandoned and
    /// quarantined (#2148) — one stuck server never stalls the sweep in either failure mode. Called
    /// from CollectionBackgroundService on its own due-cadence.
    /// </summary>
    public async Task RunQueryStoreBackfillTickAsync(CancellationToken cancellationToken)
    {
        foreach (var server in _serverManager.GetEnabledServers())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var step = _backfillSliceSteps.GetOrAdd(server.Id, static _ => new AbandonableStep());
            var result = await step.RunAsync(
                () => RunQueryStoreBackfillSliceAsync(server, cancellationToken),
                BackfillSliceDeadline, cancellationToken,
                onLateFault: ex => _logger?.LogError(ex,
                    "query_store backfill slice on '{Server}' faulted AFTER being abandoned — this is the wedge's own exception (#2148)",
                    server.DisplayName));

            switch (result.Outcome)
            {
                case AbandonableStepOutcome.Cancelled:
                    return;
                case AbandonableStepOutcome.Faulted when result.Exception is OperationCanceledException:
                    return;
                case AbandonableStepOutcome.Faulted:
                    _logger?.LogWarning("query_store backfill slice on '{Server}' failed: {Message}",
                        server.DisplayName, result.Exception!.Message);
                    break;
                case AbandonableStepOutcome.Abandoned:
                    _logger?.LogError(
                        "query_store backfill slice on '{Server}' exceeded {Deadline}s and was ABANDONED — " +
                        "other servers' backfill continues; this server is quarantined until the wedged task " +
                        "ends. Defect signal: report with this log (#2148).",
                        server.DisplayName, (int)BackfillSliceDeadline.TotalSeconds);
                    break;
                case AbandonableStepOutcome.SkippedStillRunning:
                    _logger?.LogError(
                        "query_store backfill slice on '{Server}' skipped — a previously-abandoned slice is still wedged (#2148).",
                        server.DisplayName);
                    break;
            }
        }
    }

    /// <summary>
    /// When a server's live query_store collection last failed a per-database item — the yield-to-
    /// live signal (#2111), stamped by the definition runner's item-error path and judged by
    /// <see cref="QueryStoreBackfillState.ShouldYieldToLive"/>. In-memory on purpose — a restart
    /// forgetting the stamps just means one backfill slice races one live cycle once.
    /// </summary>
    private readonly ConcurrentDictionary<int, DateTime> _lastQueryStoreItemFailureUtc = new();

    /// <summary>Consecutive live query_store failures per (server, database) — the adaptive-shrink
    /// signal (#2111 promoted); see Darling's twin for the semantics. Reset on the database's next
    /// successful item.</summary>
    private readonly ConcurrentDictionary<(int ServerId, string Database), int> _consecutiveQueryStoreItemFailures = new();

    private int ConsecutiveQueryStoreItemFailures(int serverId, string database)
        => _consecutiveQueryStoreItemFailures.TryGetValue((serverId, database), out var count) ? count : 0;

    private void OnQueryStoreItemFailed(int serverId, string database)
    {
        _lastQueryStoreItemFailureUtc[serverId] = DateTime.UtcNow;
        _consecutiveQueryStoreItemFailures.AddOrUpdate((serverId, database), 1, static (_, current) => current + 1);
    }

    private void OnQueryStoreItemSucceeded(int serverId, string database)
        => _consecutiveQueryStoreItemFailures.TryRemove((serverId, database), out _);

    /// <summary>Consecutive failed backfill slices per server — the shrink signal's backfill half;
    /// any completed slice resets it.</summary>
    private readonly ConcurrentDictionary<int, int> _consecutiveSliceFailures = new();

    /// <summary>Runs one slice with the failure accounting wrapped around it — the caller's outer
    /// catch still logs the throw exactly as before.</summary>
    private async Task RunCountedBackfillSliceAsync(
        ServerConnection server, int serverId, CollectorTargetInfo target, string databaseName,
        DateTime floorUtc, DateTime ceilingUtc, bool isHole, CancellationToken cancellationToken)
    {
        try
        {
            await RunBackfillSliceAsync(server, serverId, target, databaseName, floorUtc, ceilingUtc, isHole, cancellationToken);
            _consecutiveSliceFailures.TryRemove(serverId, out _);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _consecutiveSliceFailures.AddOrUpdate(serverId, 1, static (_, current) => current + 1);
            throw;
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

        /* #2111 yield-to-live: a backfill slice scans the same QS internal tables the live sweep
           reads — when the live path is failing on this server, running a slice anyway is the
           contention that keeps it failing. Skip the server this tick; the hole waits, live
           recovers, backfill resumes. Same policy, same window as Darling's worker. */
        if (QueryStoreBackfillState.ShouldYieldToLive(
            _lastQueryStoreItemFailureUtc.TryGetValue(serverId, out var lastLiveFailure) ? lastLiveFailure : null,
            DateTime.UtcNow))
        {
            _logger?.LogDebug(
                "query_store backfill on '{Server}': yielding to the live path (recent live query_store failure)",
                server.DisplayName);
            return false;
        }
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
                await RunCountedBackfillSliceAsync(server, serverId, target, databaseName, holeFloor, holeTo, isHole: true, cancellationToken);
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

            await RunCountedBackfillSliceAsync(server, serverId, target, databaseName, floorLimit, storedFloor.Value, isHole: false, cancellationToken);
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
        /* #2102: one slice queries at most the top MaxSliceSpan of the remaining range. The byte
           budget bounds what SHIPS, not what the query aggregates and sorts — an unchunked wide
           window on a big database times out at the command timeout every tick and the range never
           drains, the same row-cap-is-not-a-cost-cap flaw that wedged the live path. */
        /* #2111 adaptive shrink: after consecutive failed slices this server digs in narrower
           chunks until one fits its command timeout; a completed slice resets to full width. */
        var sliceSpan = QueryStoreBackfillState.AdaptiveSpan(
            QueryStoreBackfillState.MaxSliceSpan,
            _consecutiveSliceFailures.TryGetValue(serverId, out var recentFailures) ? recentFailures : 0);
        var sliceFloor = QueryStoreBackfillState.BoundSliceFloor(floorUtc, ceilingUtc, sliceSpan);

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
            var azurePlan = definition.BuildBackfillQuery(context, sliceFloor, ceilingUtc);
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

            var plan = definition.BuildBackfillPerItemQuery(databaseName, context, sliceFloor, ceilingUtc);
            using var command = new SqlCommand(plan.Text, sqlConnection) { CommandTimeout = timeout };
            AddCollectorParameters(command, plan);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await definition.ReadItemAsync(databaseName, reader, rows, context, cancellationToken);
        }

        if (rows.Count == 0)
        {
            if (sliceFloor > floorUtc)
            {
                /* Only this CHUNK is quiet — the range below it is unexplored, so this is an
                   advance, not a terminal verdict (#2102). The persisted hole ceiling shrinks past
                   the quiet chunk; a derived-boundary tail converts its remainder to a hole record,
                   because MIN over stored rows cannot walk through quiet space (an empty chunk
                   ships nothing, so the derived ceiling would re-ask the same chunk forever). The
                   tail marks done in the same breath — the hole owns the rest of the dig, and the
                   scan services holes first. */
                var advance = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [QueryStoreBackfillState.HoleKeyPrefix + databaseName] = QueryStoreBackfillState.EncodeHole(floorUtc, sliceFloor)
                };
                if (!isHole)
                {
                    advance[QueryStoreBackfillState.DoneKeyPrefix + databaseName] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                }

                await SaveCollectorStateAsync(serverId, QueryStoreBackfillState.StateCollectorName, advance, cancellationToken);

                _logger?.LogInformation(
                    "query_store backfill on '{Server}' [{Database}]: quiet chunk {Floor:o}..{Ceiling:o}, continuing below ({Range}).",
                    server.DisplayName, databaseName, sliceFloor, ceilingUtc, isHole ? "hole" : "tail");
                return;
            }

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
            /* A chunked slice's rows all sit at or above its own chunk floor, so a missing shipped
               boundary falls back to the chunk floor rather than deleting (#2102) — deletion under
               a bounded window would orphan the unexplored range below it. */
            var shippedTo = boundary ?? sliceFloor;
            if (shippedTo <= floorUtc)
            {
                await DeleteCollectorStateKeyAsync(serverId, QueryStoreBackfillState.StateCollectorName, QueryStoreBackfillState.HoleKeyPrefix + databaseName, cancellationToken);
            }
            else
            {
                await SaveCollectorStateAsync(serverId, QueryStoreBackfillState.StateCollectorName,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [QueryStoreBackfillState.HoleKeyPrefix + databaseName] = QueryStoreBackfillState.EncodeHole(floorUtc, shippedTo)
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
