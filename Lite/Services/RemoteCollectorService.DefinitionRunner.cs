/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Models;

namespace PerformanceMonitorLite.Services;

public partial class RemoteCollectorService
{
    /// <summary>
    /// Runs a shared collector definition (PerformanceMonitor.Collectors) against one server:
    /// SQL phase (definition reads/filters rows) and storage phase (appender write with the
    /// standard prefix columns) are timed separately, preserving the #1180 fetch-side metrics.
    /// Collectors migrate onto this runner one PR at a time (headless plan v5.1); it reproduces
    /// the hand-rolled per-collector loop byte-for-byte at the storage layer.
    /// </summary>
    private async Task<int> RunCollectorDefinitionAsync<TRow>(
        ICollectorDefinition<TRow> definition,
        ServerConnection server,
        CancellationToken cancellationToken)
    {
        var serverId = GetServerId(server);
        var collectionTime = DateTime.UtcNow;
        _lastSqlMs = 0;
        _lastDuckDbMs = 0;
        _lastCollectionNote = null;

        var status = _serverManager.GetConnectionStatus(server.Id);
        var target = new CollectorTargetInfo
        {
            IsAzureSqlDb = status.SqlEngineEdition == 5,
            IsAzureManagedInstance = status.SqlEngineEdition == 8,
            IsAwsRds = status.IsAwsRds,
            SqlMajorVersion = status.SqlMajorVersion,
            HasMsdbAccess = status.HasMsdbAccess,
        };

        /* Some collectors don't exist on some targets (e.g. ring buffers on Azure SQL DB) —
           skip the cycle entirely, matching the original hand-rolled collectors. */
        if (!definition.AppliesTo(target))
        {
            return 0;
        }

        /* Watermark = the host store's latest already-collected value of the definition's time
           column (Darling reads Postgres here instead) — feeds server-side filters + client dedup. */
        DateTime? watermark = definition.WatermarkColumn is null
            ? null
            : await GetLastCollectedTimeAsync(serverId, definition.TargetTable, definition.WatermarkColumn, cancellationToken);

        /* Numeric (bigint) watermark = the host store's latest already-collected value of the definition's
           monotonic identity column (job_history's instance_id) — the bigint twin of the timestamp watermark
           above, for exact-and-complete dedup that survives server-side purges. Null for every collector that
           declares no numeric watermark (the common case), so no extra query runs for them. */
        long? numericWatermark = definition.NumericWatermarkColumn is null
            ? null
            : await GetLastCollectedInstanceIdAsync(serverId, definition.TargetTable, definition.NumericWatermarkColumn, cancellationToken);

        /* Only when the watermark came back null (hot store empty): tell a TRUE first run from a store merely
           emptied by archival, so a definition like default_trace_events doesn't re-scan source data already
           in the parquet archive (CollectorContext.HasCollectedBefore). Skipped in the common (non-null
           watermark) path — no extra query. */
        bool hasCollectedBefore = definition.WatermarkColumn is not null
            && watermark is null
            && await HasPriorCollectorSuccessAsync(serverId, definition.Name, cancellationToken);

        var context = new CollectorContext
        {
            ServerId = serverId,
            ServerName = GetServerNameForStorage(server),
            CollectionTime = collectionTime,
            Deltas = _deltaCalculator,
            Target = target,
            Watermark = watermark,
            NumericWatermark = numericWatermark,
            HasCollectedBefore = hasCollectedBefore,
            IgnoredWaitTypes = _ignoredWaitTypes.Value,
            ExcludedDatabases = server.ExcludedDatabases?.ToArray() ?? Array.Empty<string>(),
            PerfmonCounterOverride = GetPerfmonCounterOverride(),
        };

        /* Two accumulators, not one contiguous read-then-write pair: the enumeration and Azure paths now
           FLUSH each database's rows before reading the next (#1556), so SQL and storage slices interleave.
           _lastSqlMs / _lastDuckDbMs stay the #1180 fetch/store split — now sums of interleaved slices. */
        long sqlMs = 0;
        long storageMs = 0;
        var rowsWritten = 0;

        if (definition.RunsPerDatabase(context.Target))
        {
            /* Azure SQL DB scopes some DMVs to the connected database — run the query once per
               database, skipping (and debug-logging) databases that error, matching the original
               hand-rolled collectors.

               Definitions with a database-scoped watermark (the XE ring-buffer collectors, whose
               per-database sessions dispatch independently) get the query rebuilt per database
               against that database's own newest already-collected value — the single server-wide
               watermark would let one busy database's newer event silence another database's older
               event still sitting in its ring buffer. Everything else keeps the build-once plan. */
            var plan = definition.PerDatabaseWatermarkColumn is null || definition.WatermarkColumn is null
                ? definition.BuildQuery(context)
                : null;
            var commandTimeout = definition.CommandTimeoutSecondsOverride ?? CommandTimeoutSeconds;
            var databases = await GetAzureDatabaseListAsync(server, cancellationToken);

            var attempted = 0;
            var failed = 0;
            Exception? firstFailure = null;

            /* One DuckDB connection for the whole body; one appender per database on it (disposing an
               appender flushes that database — commit-1..N-1 semantics on abort). */
            using var duckConnection = _duckDb.CreateConnection();
            await duckConnection.OpenAsync(cancellationToken);

            foreach (var databaseName in databases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempted++;
                try
                {
                    /* The authoritative database_name for XE rows read on this path — see
                       CollectorContext.CurrentDatabaseName. */
                    context.CurrentDatabaseName = databaseName;

                    var dbPlan = plan;
                    if (dbPlan is null)
                    {
                        /* Null (no rows for this database yet) falls back to the definition's
                           documented first-run window, per database. This is the XE ring-buffer path
                           (deadlocks / BPR), NOT query_store — no 24h clamp here. */
                        context.Watermark = await GetLastCollectedTimeForDatabaseAsync(
                            serverId, definition.TargetTable, definition.WatermarkColumn!,
                            definition.PerDatabaseWatermarkColumn!, databaseName, cancellationToken);
                        dbPlan = definition.BuildQuery(context);
                    }

                    var sqlSlice = Stopwatch.StartNew();
                    List<TRow> batch;
                    using (var dbConnection = await OpenAzureDatabaseConnectionAsync(server, databaseName, cancellationToken))
                    using (var dbCommand = CreateCollectorCommand(dbPlan, dbConnection, commandTimeout))
                    using (var dbReader = await dbCommand.ExecuteReaderAsync(cancellationToken))
                    {
                        batch = await definition.ReadAsync(dbReader, context, cancellationToken);
                    }
                    sqlMs += sqlSlice.ElapsedMilliseconds;

                    /* Flush this database before reading the next — peak memory is one database's rows. */
                    if (batch.Count > 0)
                    {
                        var storageSlice = Stopwatch.StartNew();
                        rowsWritten += WriteBatch(duckConnection, definition, batch, serverId, context.ServerName, collectionTime, context);
                        storageMs += storageSlice.ElapsedMilliseconds;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    /* OOM is filtered OUT of this per-database skip and propagates: it is fatal, not a
                       routine one-database miss. */
                    failed++;
                    firstFailure ??= ex;
                    _logger?.LogDebug("Skipping database '{Database}' for {Collector}: {Error}", databaseName, definition.Name, ex.Message);
                }
            }

            context.CurrentDatabaseName = null;

            /* One database failing is routine (offline, mid-restore, a permissions oddity) and stays a
               debug-logged skip. EVERY database failing is a systemic fault — before this check the
               cycle recorded SUCCESS with zero rows, the silent-empty shape this codebase keeps paying
               for (#1506's empty-list finding, #1535's invisible sessions). Rethrow the first failure so
               RunCollectorAsync classifies it (PERMISSIONS / transient / ERROR) instead. */
            if (attempted > 0 && failed == attempted && firstFailure is not null)
            {
                _logger?.LogWarning("{Collector} failed in all {Count} database(s) on '{Server}'; surfacing the first failure",
                    definition.Name, attempted, server.DisplayName);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
            }
        }
        else
        {
            using var sqlConnection = await CreateConnectionAsync(server, cancellationToken);

            var enumerationPlan = definition.BuildEnumerationQuery(context);
            if (enumerationPlan is not null)
            {
                /* Enumeration shape (the [db].sys.sp_executesql idiom): list items first, then
                   run one query per item ON THE SAME CONNECTION; an item that fails with a
                   SqlException is skipped with a warning, matching the original collectors. */
                var listSlice = Stopwatch.StartNew();
                var items = new List<string>();
                /* Enumeration always uses the host default timeout, matching the originals —
                   the per-collector override applies only to the heavy per-item commands. */
                using (var enumerationCommand = CreateCollectorCommand(enumerationPlan, sqlConnection, CommandTimeoutSeconds))
                using (var enumerationReader = await enumerationCommand.ExecuteReaderAsync(cancellationToken))
                {
                    while (await enumerationReader.ReadAsync(cancellationToken))
                    {
                        items.Add(enumerationReader.GetString(0));
                    }
                }
                sqlMs += listSlice.ElapsedMilliseconds;

                if (items.Count == 0)
                {
                    /* No items → no storage phase, matching the original's early return. The cycle still
                       records SUCCESS/0 rows (nothing failed), but it leaves a note on the collection_log
                       row so that row is distinguishable from a healthy collector whose databases were
                       just quiet — the silent-empty shape this codebase keeps paying for (#1837). */
                    _lastCollectionNote = EnumeratedCollectorDriver.EmptyEnumerationMessage;
                    return 0;
                }

                /* Optional quick scalar probe (e.g. query_store's live PRODUCTVERSION check,
                   deliberately probed per cycle rather than trusting cached status). Best-effort
                   on a 10-second budget, matching the original; failure leaves the definition on
                   its documented default via a null EnumerationProbeResult. */
                var probeSlice = Stopwatch.StartNew();
                var probePlan = definition.BuildEnumerationProbe(context);
                if (probePlan is not null)
                {
                    try
                    {
                        using var probeCommand = CreateCollectorCommand(probePlan, sqlConnection, 10);
                        var probeResult = await probeCommand.ExecuteScalarAsync(cancellationToken);
                        if (probeResult is not null && probeResult != DBNull.Value)
                        {
                            context.EnumerationProbeResult = probeResult;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogDebug("Enumeration probe for {Collector} failed; using defaults: {Error}",
                            definition.Name, ex.Message);
                    }
                }
                sqlMs += probeSlice.ElapsedMilliseconds;

                var itemTimeout = definition.CommandTimeoutSecondsOverride ?? CommandTimeoutSeconds;

                /* One DuckDB connection for the whole body; the driver writes one appender per database
                   on it, flushing each before reading the next. */
                using var duckConnection = _duckDb.CreateConnection();
                await duckConnection.OpenAsync(cancellationToken);

                var driverResult = await EnumeratedCollectorDriver.RunAsync<TRow>(
                    items,
                    /* Per-database watermark refresh + the 24h catch-up clamp, computed INSIDE the loop —
                       this is the per-item cutoff site the plan's LOUD FLAG requires the clamp to live at.
                       Only query_store (the sole enumeration collector with a per-database timestamp
                       watermark) reaches this; the two snapshot collectors are watermark-less. */
                    perItemWatermark: definition.PerDatabaseWatermarkColumn is null || definition.WatermarkColumn is null
                        ? null
                        : async (item, ct) =>
                        {
                            var raw = await GetLastCollectedTimeForDatabaseAsync(
                                serverId, definition.TargetTable, definition.WatermarkColumn!,
                                definition.PerDatabaseWatermarkColumn!, item, ct);
                            var clamped = WatermarkPolicy.ClampCatchup(raw, collectionTime);
                            if (raw.HasValue && clamped != raw)
                            {
                                _logger?.LogWarning(
                                    "{Collector} on '{Server}' database [{Database}] catch-up clamped to {Hours}h (stored watermark {Raw:o} is older) — a bounded, logged history hole.",
                                    definition.Name, server.DisplayName, item, WatermarkPolicy.MaxCatchup.TotalHours, raw.Value);
                            }
                            context.Watermark = clamped;
                        },
                    readItem: async (item, ct) =>
                    {
                        var batch = new List<TRow>();
                        using var itemCommand = CreateCollectorCommand(definition.BuildPerItemQuery(item, context), sqlConnection, itemTimeout);
                        using var itemReader = await itemCommand.ExecuteReaderAsync(ct);
                        await definition.ReadItemAsync(item, itemReader, batch, context, ct);
                        return batch;
                    },
                    writeBatch: (batch, ct) => Task.FromResult(WriteBatch(duckConnection, definition, batch, serverId, context.ServerName, collectionTime, context)),
                    onItemComplete: (item, batchCount, itemSqlMs, itemStorageMs) =>
                    {
                        /* Per-DATABASE line for non-empty batches (#1565): the per-server summary blends
                           every database into one number, hiding a single busy database's burst behind
                           quiet siblings. Quiet databases (0 rows) stay silent. */
                        if (batchCount > 0)
                        {
                            _logger?.LogInformation("  [{Server}] {Collector} [{Database}] => {Rows} rows (sql:{SqlMs}ms, duckdb:{DuckMs}ms)",
                                server.DisplayName, definition.Name, item, batchCount, itemSqlMs, itemStorageMs);
                        }

                        var capHit = definition.PerItemRowCountWarnThreshold is int cap && batchCount >= cap;
                        if (capHit || context.PerItemTextBudgetExceeded)
                        {
                            _logger?.LogWarning(
                                "{Collector} on '{Server}' database [{Database}] hit its per-database collection bound ({Reason}) — oldest rows dropped this cycle.",
                                definition.Name, server.DisplayName, item,
                                capHit ? $"row cap {definition.PerItemRowCountWarnThreshold}" : "256MB text budget");
                        }
                    },
                    onItemError: (item, ex) =>
                        _logger?.LogWarning("Failed to collect {Collector} from [{Database}] on '{Server}': {Message}",
                            definition.Name, item, server.DisplayName, ex.Message),
                    cancellationToken);

                rowsWritten = driverResult.Rows;
                sqlMs += driverResult.SqlMs;
                storageMs += driverResult.StorageMs;
            }
            else
            {
                /* Plain single-query path — unchanged: read all rows, then write them in one batch
                   (supplemental never runs for per-database collectors). Routed through WriteBatch so
                   all three paths share one writer. */
                var sqlSlice = Stopwatch.StartNew();
                var plan = definition.BuildQuery(context);
                List<TRow> rows;
                using (var command = CreateCollectorCommand(plan, sqlConnection, definition.CommandTimeoutSecondsOverride ?? CommandTimeoutSeconds))
                using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    rows = await definition.ReadAsync(reader, context, cancellationToken);
                }

                /* Optional best-effort second query on the same connection (e.g. server_properties'
                   WS5 health probe). Failure-isolated: it can never fail the primary rows. Skipped
                   when the primary produced no rows, matching the originals (which only ran their
                   second query after a successful primary read). */
                var supplementalPlan = definition.BuildSupplementalQuery(context);
                if (supplementalPlan is not null && rows.Count > 0)
                {
                    try
                    {
                        using var supplementalCommand = CreateCollectorCommand(supplementalPlan, sqlConnection, CommandTimeoutSeconds);
                        using var supplementalReader = await supplementalCommand.ExecuteReaderAsync(cancellationToken);
                        await definition.ApplySupplementalAsync(rows, supplementalReader, context, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Supplemental query for {Collector} failed; continuing without it", definition.Name);
                    }
                }
                sqlMs += sqlSlice.ElapsedMilliseconds;

                var storageSlice = Stopwatch.StartNew();
                using var duckConnection = _duckDb.CreateConnection();
                await duckConnection.OpenAsync(cancellationToken);
                rowsWritten = WriteBatch(duckConnection, definition, rows, serverId, context.ServerName, collectionTime, context);
                storageMs += storageSlice.ElapsedMilliseconds;
            }
        }

        _lastSqlMs = sqlMs;
        _lastDuckDbMs = storageMs;

        _logger?.LogDebug("Collected {RowCount} {Collector} rows for server '{Server}'", rowsWritten, definition.Name, server.DisplayName);
        return rowsWritten;
    }

    /// <summary>
    /// Writes ONE batch (one enumerated item / one database, or the whole result set for a plain
    /// collector) to DuckDB via a single appender on the caller's already-open connection (#1556). The
    /// three collection paths route through here so the storage logic — the prefix columns, the positional
    /// payload — lives once. Disposing the appender FLUSHES the batch, so on a mid-run abort the batches
    /// already written stay committed (commit-1..N-1). An empty batch opens no appender and returns 0
    /// (rows_collected = Σ non-empty batch counts). Synchronous (the DuckDB appender API is), returning the
    /// count so the driver can await it as a completed task.
    /// </summary>
    private static int WriteBatch<TRow>(
        DuckDBConnection duckConnection,
        ICollectorDefinition<TRow> definition,
        List<TRow> rows,
        int serverId,
        string serverName,
        DateTime collectionTime,
        CollectorContext context)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var rowsWritten = 0;
        using (var appender = duckConnection.CreateAppender(definition.TargetTable))
        {
            var writer = new AppenderCollectorRowWriter();

            foreach (var item in rows)
            {
                var row = appender.CreateRow();

                if (definition.IncludesCollectionId)
                {
                    row.AppendValue(GenerateCollectionId()); /* collection_id BIGINT */
                }

                row.AppendValue(collectionTime)              /* collection_time TIMESTAMP */
                   .AppendValue(serverId)                    /* server_id INTEGER */
                   .AppendValue(serverName);                 /* server_name VARCHAR */

                writer.CurrentRow = row;
                definition.WritePayload(item, writer, context);
                row.EndRow();

                rowsWritten++;
            }
        }

        return rowsWritten;
    }

    private static SqlCommand CreateCollectorCommand(CollectorQuery plan, SqlConnection connection, int commandTimeoutSeconds)
    {
        var command = new SqlCommand(plan.Text, connection) { CommandTimeout = commandTimeoutSeconds };

        foreach (var parameter in plan.Parameters)
        {
            command.Parameters.Add(ToSqlParameter(parameter));
        }

        return command;
    }

    private static SqlParameter ToSqlParameter(CollectorParameter parameter) => parameter.Type switch
    {
        CollectorParameterType.DateTime2 => new SqlParameter(parameter.Name, SqlDbType.DateTime2) { Value = parameter.Value ?? DBNull.Value },
        CollectorParameterType.NVarChar128 => new SqlParameter(parameter.Name, SqlDbType.NVarChar, 128) { Value = parameter.Value ?? DBNull.Value },
        CollectorParameterType.Int32 => new SqlParameter(parameter.Name, SqlDbType.Int) { Value = parameter.Value ?? DBNull.Value },
        CollectorParameterType.BigInt => new SqlParameter(parameter.Name, SqlDbType.BigInt) { Value = parameter.Value ?? DBNull.Value },
        _ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter.Type, "Unmapped collector parameter type"),
    };
}
