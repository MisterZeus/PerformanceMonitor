/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Npgsql;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Common;
using PerformanceMonitor.Darling.Storage;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// Per-run outcome the worker logs (mirrors Lite's fetch/store phase split, #1180). <paramref name="Note"/>
/// annotates a run that SUCCEEDED but is worth explaining on its collection_log row — today only the
/// empty-enumeration case (see <see cref="EnumeratedCollectorDriver.EmptyEnumerationMessage"/>). It is the
/// Darling twin of Lite's <c>_lastCollectionNote</c>; null (the default) leaves the row's message column
/// null exactly as before.
/// </summary>
public sealed record CollectorRunResult(int Rows, long SqlMs, long StorageMs, string? Note = null);

/// <summary>
/// Runs a shared collector definition against one monitored server and binary-COPYs the rows
/// into Postgres — the Darling counterpart of Lite's RemoteCollectorService.DefinitionRunner,
/// ported semantics-for-semantics: AppliesTo skip, host-store watermarks, the three execution
/// paths (per-database Azure connections; enumeration with the optional scalar probe; plain
/// single query with best-effort supplemental), cancellation-aware per-item catches, and the
/// separated SQL/storage phase timing. The definitions and the delta/ignored-wait/schedule
/// defaults are the shared brain; only the storage engine differs.
/// </summary>
public sealed class DarlingCollectorRunner
{
    private readonly NpgsqlDataSource _postgres;
    private readonly CollectorDeltaCalculator _deltas;
    private readonly ILogger? _logger;

    /* Feeds CollectorContext.CapturePlanXml on every cycle — the query_stats / query_store
       collectors capture the execution plan when true (darling.json "capturePlans", default true).
       Lite never sets the context flag; this is what makes Darling the plan-capturing SKU. Read
       through a provider (not a captured bool) so a control-plane store reload of config_service's
       capture_plans is honored on the NEXT cycle without reconstructing the runner. */
    private readonly Func<bool> _capturePlans;

    /* Feeds CollectorContext.CollectSchemaChangeEvents on every cycle — the default_trace_events
       collector drops its Object:Created/Altered/Deleted (schema DDL) slice when false (darling.json
       "collectSchemaChangeEvents", default true). Lite never sets the context flag, so it keeps
       collecting Object DDL. Read through a provider (not a captured bool) for symmetry with
       _capturePlans, so a future live reload is honored on the NEXT cycle without rebuilding. */
    private readonly Func<bool> _collectSchemaChanges;

    /* Azure SQL DB logins without master access fall back to single-database mode, throttled per
       server so master isn't retried every cycle (#857 — mirrors Lite).

       Stores WHEN the verdict was formed, not just that it was: it expires after
       AzureMasterRecheckInterval, and OnServerReconnected drops it outright. Both escape hatches
       exist because this used to latch until the process was restarted, so a transient Azure error
       could permanently demote a healthy server to single-database collection (#1506). */
    private readonly ConcurrentDictionary<int, DateTime> _azureMasterInaccessibleSince = new();

    /// <summary>
    /// When a server's live query_store collection last failed a per-database item — the backfill
    /// worker's yield-to-live signal (#2111), read through <see cref="LastQueryStoreItemFailureUtc"/>
    /// and judged by <see cref="QueryStoreBackfillState.ShouldYieldToLive"/>. Stamped only for
    /// query_store (the one collector with a backfill worker to yield); in-memory on purpose — a
    /// service restart forgetting the stamps just means one backfill slice races one live cycle once.
    /// </summary>
    private readonly ConcurrentDictionary<int, DateTime> _lastQueryStoreItemFailureUtc = new();

    /// <summary>The #2111 yield-to-live read side: null when the server has never failed a live
    /// query_store item this process lifetime.</summary>
    public DateTime? LastQueryStoreItemFailureUtc(int serverId)
        => _lastQueryStoreItemFailureUtc.TryGetValue(serverId, out var failure) ? failure : null;

    /// <summary>
    /// Consecutive live query_store failures per DATABASE — the adaptive-shrink signal (#2111
    /// promoted from reserve): a member whose window keeps exceeding the command timeout gets a
    /// progressively narrower catch-up window (<see cref="QueryStoreBackfillState.AdaptiveSpan"/>)
    /// until one fits, and the skipped range rides the same hole records the clamp already writes.
    /// Reset on the database's next successful item; in-memory like the yield stamps and for the
    /// same reason — a restart forgetting the count costs one full-width attempt.
    /// </summary>
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

    private static readonly TimeSpan AzureMasterRecheckInterval = TimeSpan.FromMinutes(15);

    public const int CommandTimeoutSeconds = 60;

    /// <param name="capturePlans">
    /// Live provider for the plan-capture flag; null defaults to always-on (Darling's SKU default).
    /// The worker passes <c>() =&gt; config.CapturePlans</c> so a store reload takes effect next cycle;
    /// tests pass a constant lambda.
    /// </param>
    /// <param name="collectSchemaChanges">
    /// Live provider for the schema-change (Object DDL) collection flag; null defaults to on (today's
    /// behavior). The worker passes <c>() =&gt; config.CollectSchemaChangeEvents</c> so a noisy/benchmark box
    /// can suppress the default-trace Object:Created/Deleted flood; tests pass a constant lambda.
    /// </param>
    public DarlingCollectorRunner(NpgsqlDataSource postgres, CollectorDeltaCalculator deltas, ILogger? logger = null, Func<bool>? capturePlans = null, Func<bool>? collectSchemaChanges = null)
    {
        _postgres = postgres ?? throw new ArgumentNullException(nameof(postgres));
        _deltas = deltas ?? throw new ArgumentNullException(nameof(deltas));
        _logger = logger;
        _capturePlans = capturePlans ?? (() => true);
        _collectSchemaChanges = collectSchemaChanges ?? (() => true);
    }

    public async Task<CollectorRunResult> RunAsync<TRow>(
        ICollectorDefinition<TRow> definition,
        ServerRuntime server,
        CancellationToken cancellationToken)
    {
        var collectionTime = DateTime.UtcNow;

        /* Some collectors don't exist on some targets (e.g. ring buffers on Azure SQL DB) —
           skip the cycle entirely, matching Lite. */
        if (!definition.AppliesTo(server.Target))
        {
            return new CollectorRunResult(0, 0, 0);
        }

        /* Watermark = the newest already-collected value of the definition's time column,
           read from Postgres (Lite reads DuckDB here). */
        DateTime? watermark = definition.WatermarkColumn is null
            ? null
            : await GetLastCollectedTimeAsync(server.ServerId, definition.TargetTable, definition.WatermarkColumn, cancellationToken);

        /* Numeric (bigint) watermark = the newest already-collected value of the definition's monotonic
           identity column (job_history's instance_id), read from Postgres — the bigint twin of the timestamp
           watermark above. Null for every collector that declares no numeric watermark (the common case),
           so no extra query runs for them. */
        long? numericWatermark = definition.NumericWatermarkColumn is null
            ? null
            : await GetLastCollectedInstanceIdAsync(server.ServerId, definition.TargetTable, definition.NumericWatermarkColumn, cancellationToken);

        /* Only when the watermark came back null: tell a TRUE first run from a store merely emptied by
           retention, so default_trace_events uses a bounded window instead of re-scanning all .trc history
           (CollectorContext.HasCollectedBefore). Skipped in the common (non-null watermark) path. */
        bool hasCollectedBefore = definition.WatermarkColumn is not null
            && watermark is null
            && await HasPriorCollectorSuccessAsync(server.ServerId, definition.Name, cancellationToken);

        /* Per-server state the definition declared keys for — the watermark's sibling for facts no MAX()
           over the collected rows can produce (default_trace_events' last-seen trace FILE, #1962). No
           declared keys (every other collector) means no query runs. Mirrors Lite. */
        var collectorState = definition.StateKeys.Count == 0
            ? null
            : await GetCollectorStateAsync(server.ServerId, definition.Name, cancellationToken);

        var context = new CollectorContext
        {
            ServerId = server.ServerId,
            ServerName = server.StorageName,
            CollectionTime = collectionTime,
            Deltas = _deltas,
            Target = server.Target,
            Watermark = watermark,
            NumericWatermark = numericWatermark,
            HasCollectedBefore = hasCollectedBefore,
            State = collectorState ?? CollectorContext.NoState,
            IgnoredWaitTypes = IgnoredWaitDefaults.All,
            ExcludedDatabases = server.Config.ExcludedDatabases?.ToArray() ?? Array.Empty<string>(),
            PerfmonCounterOverride = null,
            CapturePlanXml = _capturePlans(),
            CollectSchemaChangeEvents = _collectSchemaChanges(),
        };

        /* Two accumulators, not one contiguous read-then-write pair: the enumeration and Azure paths now
           FLUSH each database's rows before reading the next (#1556), so SQL and storage slices interleave.
           Wall-clock (sqlMs + storageMs) and rows_collected totals stay coherent — collection_log is
           unchanged; only the split is now a sum of interleaved slices. */
        long sqlMs = 0;
        long storageMs = 0;
        var rowsWritten = 0;

        /* The collection_log note for this run (#1837) — null on every ordinary path. Only the enumeration
           branch sets it, but it is declared here so the note reaches the single success return below when
           items WERE found and merely some of their probes failed. Lite's twin is _lastCollectionNote. */
        string? collectionNote = null;

        if (definition.RunsPerDatabase(context.Target))
        {
            /* Azure SQL DB scopes some DMVs to the connected database — run the query once per
               database, skipping (and debug-logging) databases that error, matching Lite.

               Definitions with a database-scoped watermark (the XE ring-buffer collectors, whose
               per-database sessions dispatch independently) get the query rebuilt per database
               against that database's own newest already-collected value — the single server-wide
               watermark would let one busy database's newer event silence another database's older
               event still sitting in its ring buffer (#1535). Everything else keeps the
               build-once plan.

               Honor CommandTimeoutSecondsOverride here (#1556): this path previously passed the constant
               60s cap where Lite's twin already honored the override, a latent bug — index_object_stats
               needs 300s per database on Azure, so on a large Azure database its per-database read would
               have timed out at 60s. */
            var plan = definition.PerDatabaseWatermarkColumn is null || definition.WatermarkColumn is null
                ? definition.BuildQuery(context)
                : null;
            var perDbTimeout = definition.CommandTimeoutSecondsOverride ?? CommandTimeoutSeconds;
            var databases = await GetAzureDatabaseListAsync(server, cancellationToken);

            var attempted = 0;
            var failed = 0;
            Exception? firstFailure = null;

            /* #1875: this path reads the trailing probe-failure set once PER DATABASE, so the note and the
               log cap are decided for the cycle after the loop rather than inside it — see
               CycleProbeFailures for why neither generalizes from the single-read plain path. */
            var cycleProbeFailures = new CycleProbeFailures();

            /* One pooled store connection for the whole body; one binary COPY per database on it
               (completing an importer commits that database — commit-1..N-1 semantics on abort). */
            await using var pgConnection = await _postgres.OpenConnectionAsync(cancellationToken);

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
                           documented first-run window, per database. No clamp is applied HERE because
                           this branch also serves the XE ring-buffer collectors (deadlocks / BPR),
                           where flooring a stale watermark would WRONGLY truncate legitimate catch-up
                           — those sources roll past 24h on their own. query_store also reaches this
                           branch on Azure SQL DB (#1836) and does need the bound, so it applies
                           WatermarkPolicy.ClampCatchup inside its own cutoff computation: the clamp
                           travels with the collector that needs it instead of with the path. */
                        context.Watermark = await GetLastCollectedTimeForDatabaseAsync(
                            server.ServerId, definition.TargetTable, definition.WatermarkColumn!,
                            definition.PerDatabaseWatermarkColumn!, databaseName, cancellationToken);

                        /* #2111 adaptive shrink, Azure arm — tighten BEFORE BuildQuery: the
                           definition's own clamp only floors OLDER watermarks, so a tighter one
                           passes through untouched. The skipped range is recorded as a hole here
                           (wider than the clamp's own record would be, so the block below firing
                           too would merge, not conflict). */
                        var azureFailures = ConsecutiveQueryStoreItemFailures(server.ServerId, databaseName);
                        if (azureFailures > 0
                            && string.Equals(definition.Name, QueryStoreCollector.Instance.Name, StringComparison.Ordinal))
                        {
                            var adaptiveSpan = QueryStoreBackfillState.AdaptiveSpan(WatermarkPolicy.MaxCatchup, azureFailures);
                            var tighterFloor = collectionTime - adaptiveSpan;
                            if (context.Watermark is DateTime azureRaw)
                            {
                                if (azureRaw < tighterFloor)
                                {
                                    _logger?.LogWarning(
                                        "query_store on '{Server}' database [{Database}] adaptive catch-up shrink: {Failures} consecutive failed cycles — window narrowed to {Minutes:F0}m; the skipped range rides the backfill hole.",
                                        server.Config.DisplayName, databaseName, azureFailures, adaptiveSpan.TotalMinutes);
                                    await RecordQueryStoreBackfillHoleAsync(server.ServerId, databaseName, azureRaw, tighterFloor, cancellationToken);
                                    context.Watermark = tighterFloor;
                                }
                            }
                            else
                            {
                                /* Never-succeeded database: tighten the first-run fallback too (the
                                   review catch); no hole — pre-watermark history is the tail's job. */
                                _logger?.LogWarning(
                                    "query_store on '{Server}' database [{Database}] adaptive first-contact shrink: {Failures} consecutive failed cycles — first-run window narrowed to {Minutes:F0}m.",
                                    server.Config.DisplayName, databaseName, azureFailures, adaptiveSpan.TotalMinutes);
                                context.Watermark = tighterFloor;
                            }
                        }

                        dbPlan = definition.BuildQuery(context);

                        /* The definition clamped its own cutoff — surface the same WARNING the
                           enumeration path emits, so the bounded history hole stays LOGGED and does
                           not become the one silent hole in a policy whose whole premise is that it
                           is visible. Mirrors Lite. */
                        if (context.CatchupClampApplied)
                        {
                            _logger?.LogWarning(
                                "{Collector} on '{Server}' database [{Database}] catch-up clamped to {Hours}h (stored watermark {Raw:o} is older) — a bounded, logged history hole.",
                                definition.Name, server.Config.DisplayName, databaseName, WatermarkPolicy.MaxCatchup.TotalHours, context.Watermark);

                            /* #2058 (the Azure arm of #2022's hole recording): context.Watermark still
                               holds the RAW value here — the definition clamped only its own cutoff
                               parameter — so the hole is (raw, re-derived clamp floor), same merge
                               semantics as the enumerated site. Only query_store both clamps AND has a
                               backfill worker; the name guard keeps the XE collectors that share this
                               branch from growing backfill state they have no worker for. */
                            if (context.Watermark.HasValue
                                && string.Equals(definition.Name, QueryStoreCollector.Instance.Name, StringComparison.Ordinal)
                                && WatermarkPolicy.ClampCatchup(context.Watermark, collectionTime) is DateTime azureClampedFloor)
                            {
                                await RecordQueryStoreBackfillHoleAsync(
                                    server.ServerId, databaseName, context.Watermark.Value, azureClampedFloor, cancellationToken);
                            }
                        }
                    }

                    var sqlSlice = Stopwatch.StartNew();
                    List<TRow> batch;
                    using (var dbConnection = await OpenAzureDatabaseConnectionAsync(server, databaseName, cancellationToken))
                    using (var dbCommand = CreateCollectorCommand(dbPlan, dbConnection, perDbTimeout))
                    using (var dbReader = await dbCommand.ExecuteReaderAsync(cancellationToken))
                    {
                        batch = await definition.ReadAsync(dbReader, context, cancellationToken);

                        /* #1875: the payload path's probe-failure contract, on the path that used to
                           ignore it. blocked_process_report is the declaring collector that also runs per
                           database (Azure SQL DB, #1535), so before this its batch produced the trailing
                           set and the loop simply never advanced the reader to it — the rows were built
                           and dropped. Read HERE, still inside the reader and inside the per-database
                           try, so a diagnostics fault stays a one-database skip like any other. */
                        if (definition.EmitsProbeFailures)
                        {
                            cycleProbeFailures.Add(
                                await EnumeratedCollectorDriver.ReadPayloadProbeFailuresAsync(dbReader, cancellationToken));
                        }
                    }
                    sqlMs += sqlSlice.ElapsedMilliseconds;

                    /* Flush this database before reading the next — peak memory is one database's rows. */
                    if (batch.Count > 0)
                    {
                        var storageSlice = Stopwatch.StartNew();
                        rowsWritten += await WriteBatchAsync(pgConnection, definition, batch, server, collectionTime, context, cancellationToken);
                        storageMs += storageSlice.ElapsedMilliseconds;
                    }

                    /* Same per-database bounded-cycle WARNING the enumeration path emits from
                       onItemComplete, mirroring Lite. Reachable here since #1836 put query_store — the
                       only collector that declares either bound — on this branch for Azure SQL DB;
                       without it a database whose cycle was cut at the bound would look like a clean
                       collection. Since #1960 a bound DEFERS the backlog to the next cycle's resume
                       from the shipped boundary rather than dropping it — this log is how a long
                       catch-up stays observable. Read after the flush, as on the other path: the
                       context signal stays this database's until the next read resets it. */
                    var capHit = definition.PerItemRowCountWarnThreshold is int cap && batch.Count >= cap;
                    if (capHit || context.PerItemTextBudgetExceeded)
                    {
                        _logger?.LogWarning(
                            "{Collector} on '{Server}' database [{Database}] hit its per-database collection bound ({Reason}) — shipped {ShippedMB:F1}MB up to {Boundary}; the backlog resumes from that boundary next cycle.",
                            definition.Name, server.Config.DisplayName, databaseName,
                            capHit ? $"row cap {definition.PerItemRowCountWarnThreshold}" : "text byte budget",
                            context.PerItemTextBytesShipped / (1024.0 * 1024.0),
                            context.PerItemShippedBoundary?.ToString("o") ?? "n/a");
                    }

                    /* #2111: success resets the adaptive-shrink count on the Azure arm too. */
                    if (string.Equals(definition.Name, QueryStoreCollector.Instance.Name, StringComparison.Ordinal))
                    {
                        OnQueryStoreItemSucceeded(server.ServerId, databaseName);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    /* OOM is filtered OUT of this per-database skip and propagates: it is fatal, not a
                       routine one-database miss. */
                    failed++;
                    firstFailure ??= ex;

                    /* #2111: the yield-to-live stamp + adaptive-shrink count for the Azure SQL DB
                       arm — query_store reaches THIS per-database loop there, not the enumeration
                       path's onItemError, and without the stamp the backfill worker would never
                       yield on an Azure target (the review catch on #2112). Same query_store-only
                       guard as the hole recording above. */
                    if (string.Equals(definition.Name, QueryStoreCollector.Instance.Name, StringComparison.Ordinal))
                    {
                        OnQueryStoreItemFailed(server.ServerId, databaseName);
                    }

                    _logger?.LogDebug("Skipping database '{Database}' for {Collector}: {Error}", databaseName, definition.Name, ex.Message);
                }
            }

            context.CurrentDatabaseName = null;

            /* #1875: ONE note for the cycle and ONE capped log burst, composed from every database's
               failures together. Assigned unconditionally — a cycle where nothing failed composes null,
               which is exactly what this path carried before. */
            collectionNote = cycleProbeFailures.Note;
            LogEnumerationProbeFailures(definition, server, cycleProbeFailures.Failures);

            /* One database failing is routine (offline, mid-restore, a permissions oddity) and stays a
               debug-logged skip. EVERY database failing is a systemic fault — before this check the run
               recorded SUCCESS with zero rows, which on the XE collectors also made the SESSION_MISSING
               classification (RunXeTolerantAsync → the Capture Down self-alert) unreachable on Azure.
               Rethrow the first failure so RunOneAsync classifies it (SESSION_MISSING / PERMISSIONS /
               ERROR) instead. Mirrors Lite's definition runner. */
            if (attempted > 0 && failed == attempted && firstFailure is not null)
            {
                _logger?.LogWarning("{Collector} failed in all {Count} database(s) on '{Server}'; surfacing the first failure",
                    definition.Name, attempted, server.Config.DisplayName);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
            }
        }
        else
        {
            using var sqlConnection = new SqlConnection(server.ConnectionString);
            await sqlConnection.OpenAsync(cancellationToken);

            var enumerationPlan = definition.BuildEnumerationQuery(context);
            if (enumerationPlan is not null)
            {
                /* Enumeration shape (the [db].sys.sp_executesql idiom): list items first, then
                   run one query per item ON THE SAME CONNECTION; an item that fails is skipped
                   with a warning, matching Lite. */
                var listSlice = Stopwatch.StartNew();
                EnumerationOutcome enumeration;
                using (var enumerationCommand = CreateCollectorCommand(enumerationPlan, sqlConnection, CommandTimeoutSeconds))
                using (var enumerationReader = await enumerationCommand.ExecuteReaderAsync(cancellationToken))
                {
                    /* Shared read (#1837): the item list, then the OPTIONAL second result set of items the
                       enumeration could not probe. Both hosts route through it so the item read, the
                       failure read, and the note wording cannot drift. */
                    enumeration = await EnumeratedCollectorDriver.ReadEnumerationAsync(enumerationReader, cancellationToken);
                }
                sqlMs += listSlice.ElapsedMilliseconds;

                var items = enumeration.Items;
                collectionNote = enumeration.Note;
                LogEnumerationProbeFailures(definition, server, enumeration.ProbeFailures);

                if (items.Count == 0)
                {
                    /* Nothing failed outright, so this stays SUCCESS/0 rows — but the note (the
                       empty-enumeration breadcrumb, the probe-failure summary, or both) rides onto the
                       collection_log row so it is distinguishable from a healthy collector whose databases
                       were simply quiet (#1837). Mirrors Lite's _lastCollectionNote. */
                    return new CollectorRunResult(0, sqlMs, 0, enumeration.Note);
                }

                /* Optional quick scalar probe (query_store's live PRODUCTVERSION check) —
                   best-effort on a 10-second budget; failure leaves the documented default. */
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

                /* One pooled store connection for the whole body; the driver writes one binary COPY per
                   database on it, flushing each before reading the next. */
                await using var pgConnection = await _postgres.OpenConnectionAsync(cancellationToken);

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
                                server.ServerId, definition.TargetTable, definition.WatermarkColumn!,
                                definition.PerDatabaseWatermarkColumn!, item, ct);
                            var clamped = WatermarkPolicy.ClampCatchup(raw, collectionTime);
                            if (raw.HasValue && clamped != raw)
                            {
                                _logger?.LogWarning(
                                    "{Collector} on '{Server}' database [{Database}] catch-up clamped to {Hours}h (stored watermark {Raw:o} is older) — a bounded, logged history hole.",
                                    definition.Name, server.Config.DisplayName, item, WatermarkPolicy.MaxCatchup.TotalHours, raw.Value);

                                /* #2022: the clamp opens a hole (raw, clamped) the live path will never
                                   revisit — its next cutoff IS the clamped floor. Record it for the
                                   backfill worker, merged wider with any hole already pending for this
                                   database. Only query_store reaches this lambda today; the name guard
                                   keeps a future enumeration collector from inheriting backfill state
                                   it has no worker for. */
                                if (clamped.HasValue
                                    && string.Equals(definition.Name, QueryStoreCollector.Instance.Name, StringComparison.Ordinal))
                                {
                                    await RecordQueryStoreBackfillHoleAsync(server.ServerId, item, raw.Value, clamped.Value, ct);
                                }
                            }

                            /* #2111 adaptive shrink (promoted from reserve on field evidence — a member
                               whose 1h window intermittently exceeds the command timeout stays stuck for
                               hours): after N consecutive live failures the window halves per failure
                               toward 15 minutes, and the range the tighter floor skips rides the SAME
                               hole records the clamp writes — deferred to the trickle, never dropped.
                               Success resets the count, so a recovered member is back at full width
                               next cycle. */
                            var failures = ConsecutiveQueryStoreItemFailures(server.ServerId, item);
                            if (failures > 0
                                && string.Equals(definition.Name, QueryStoreCollector.Instance.Name, StringComparison.Ordinal))
                            {
                                var span = QueryStoreBackfillState.AdaptiveSpan(WatermarkPolicy.MaxCatchup, failures);
                                var tighterFloor = collectionTime - span;
                                if (clamped is DateTime current)
                                {
                                    if (current < tighterFloor)
                                    {
                                        _logger?.LogWarning(
                                            "query_store on '{Server}' database [{Database}] adaptive catch-up shrink: {Failures} consecutive failed cycles — window narrowed to {Minutes:F0}m; the skipped range rides the backfill hole.",
                                            server.Config.DisplayName, item, failures, span.TotalMinutes);
                                        await RecordQueryStoreBackfillHoleAsync(server.ServerId, item, current, tighterFloor, ct);
                                        clamped = tighterFloor;
                                    }
                                }
                                else
                                {
                                    /* Never-succeeded database (null watermark): the definition's 60-minute
                                       first-run fallback is MaxCatchup-sized, so it can be exactly the window
                                       that cannot fit — tighten it the same way (the review catch on the first
                                       cut, which gated shrink on a non-null watermark and left first contact
                                       retrying the full width forever). No hole record: pre-watermark history
                                       is the backfill TAIL's job by design. */
                                    _logger?.LogWarning(
                                        "query_store on '{Server}' database [{Database}] adaptive first-contact shrink: {Failures} consecutive failed cycles — first-run window narrowed to {Minutes:F0}m.",
                                        server.Config.DisplayName, item, failures, span.TotalMinutes);
                                    clamped = tighterFloor;
                                }
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
                    writeBatch: (batch, ct) => WriteBatchAsync(pgConnection, definition, batch, server, collectionTime, context, ct),
                    onItemComplete: (item, batchCount, itemSqlMs, itemStorageMs) =>
                    {
                        /* #2111: a completed item resets the adaptive-shrink count — recovery returns
                           the member to the full catch-up width on its next cycle. */
                        if (string.Equals(definition.Name, QueryStoreCollector.Instance.Name, StringComparison.Ordinal))
                        {
                            OnQueryStoreItemSucceeded(server.ServerId, item);
                        }

                        /* Per-DATABASE line for non-empty batches (#1565): the per-server summary blends
                           every database into one number, which hid a single busy database's 50s burst
                           behind four quiet siblings. Quiet databases (0 rows — the 2-of-3 cycles between
                           Query Store's 900s flushes) stay silent. */
                        if (batchCount > 0)
                        {
                            _logger?.LogInformation("  [{Server}] {Collector} [{Database}] => {Rows} rows (sql:{SqlMs}ms, pg:{PgMs}ms)",
                                server.Config.DisplayName, definition.Name, item, batchCount, itemSqlMs, itemStorageMs);
                        }

                        var capHit = definition.PerItemRowCountWarnThreshold is int cap && batchCount >= cap;
                        if (capHit || context.PerItemTextBudgetExceeded)
                        {
                            _logger?.LogWarning(
                                "{Collector} on '{Server}' database [{Database}] hit its per-database collection bound ({Reason}) — shipped {ShippedMB:F1}MB up to {Boundary}; the backlog resumes from that boundary next cycle.",
                                definition.Name, server.Config.DisplayName, item,
                                capHit ? $"row cap {definition.PerItemRowCountWarnThreshold}" : "text byte budget",
                                context.PerItemTextBytesShipped / (1024.0 * 1024.0),
                                context.PerItemShippedBoundary?.ToString("o") ?? "n/a");
                        }
                    },
                    onItemError: (item, ex) =>
                    {
                        /* #2111: stamp the yield-to-live signal (any database's live failure vouches
                           for the whole replica being contended) + the per-database adaptive-shrink
                           count. */
                        if (string.Equals(definition.Name, QueryStoreCollector.Instance.Name, StringComparison.Ordinal))
                        {
                            OnQueryStoreItemFailed(server.ServerId, item);
                        }

                        _logger?.LogWarning("Failed to collect {Collector} from [{Database}] on '{Server}': {Message}",
                            definition.Name, item, server.Config.DisplayName, ex.Message);
                    },
                    cancellationToken);

                rowsWritten = driverResult.Rows;
                sqlMs += driverResult.SqlMs;
                storageMs += driverResult.StorageMs;
            }
            else
            {
                /* Plain single-query path — unchanged: read all rows, then write them in one batch
                   (supplemental never runs for per-database collectors). Routed through WriteBatchAsync
                   so all three paths share one writer. */
                var sqlSlice = Stopwatch.StartNew();
                var plan = definition.BuildQuery(context);
                List<TRow> rows;
                using (var command = CreateCollectorCommand(plan, sqlConnection, definition.CommandTimeoutSecondsOverride ?? CommandTimeoutSeconds))
                using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    rows = await definition.ReadAsync(reader, context, cancellationToken);

                    /* #1851: a definition that declares it may hand back an OPTIONAL trailing
                       (item_name, error_text) result set naming items its own server-side cursor
                       reached but could not probe — database_size_stats' mid-restore / inaccessible
                       databases, which used to vanish into an empty CATCH. Read through the SAME
                       shared machinery as the enumeration path's failures (#1837), so the note wording
                       and the log cap cannot drift between the two channels or between the two hosts.
                       Read HERE, still inside the reader, and before the storage phase below: it
                       touches only the note, never `rows`, so the payload and its delta ordering are
                       exactly what they were. */
                    if (definition.EmitsProbeFailures)
                    {
                        var probes = await EnumeratedCollectorDriver.ReadPayloadProbeFailuresAsync(reader, cancellationToken);
                        collectionNote = probes.Note;
                        LogEnumerationProbeFailures(definition, server, probes.ProbeFailures);
                    }
                }

                /* Optional best-effort second query on the same connection (server_properties'
                   health probe). Failure-isolated; skipped on an empty primary, matching Lite. */
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
                await using var pgConnection = await _postgres.OpenConnectionAsync(cancellationToken);
                rowsWritten = await WriteBatchAsync(pgConnection, definition, rows, server, collectionTime, context, cancellationToken);
                storageMs += storageSlice.ElapsedMilliseconds;
            }
        }

        /* Persist what the definition observed, AFTER the cycle completed — including a cycle that wrote
           zero rows, which is exactly the case a row-derived watermark cannot cover (#1962). A cycle that
           threw never reaches here, so the older state survives and the next run takes its conservative
           path. Outside the storage-phase timer: this is host bookkeeping, not collected data. */
        if (context.PendingState.Count > 0)
        {
            await SaveCollectorStateAsync(server.ServerId, definition.Name, context.PendingState, cancellationToken);
        }

        _logger?.LogDebug("Collected {RowCount} {Collector} rows for server '{Server}'",
            rowsWritten, definition.Name, server.Config.DisplayName);
        return new CollectorRunResult(rowsWritten, sqlMs, storageMs, collectionNote);
    }

    /// <summary>
    /// Writes the per-item app-log lines for probe failures, capped at
    /// <see cref="EnumeratedCollectorDriver.MaxLoggedProbeFailures"/> with the suppressed remainder
    /// reported as a count. The collection_log row already carries the summary note; this is where the
    /// actual per-database error text lands, and it is why that note says "see the app log". Lite's twin
    /// is <c>RemoteCollectorService.LogEnumerationProbeFailures</c> — same shared templates.
    ///
    /// <para>Serves BOTH channels: an enumeration's second result set (#1837) and a payload collector's
    /// trailing one (#1851). Named for the shared template it writes, which reports the failing step as
    /// an enumeration probe — accurate for both, since a payload collector reaches this only by
    /// enumerating and probing databases inside its own server-side cursor.</para>
    /// </summary>
    private void LogEnumerationProbeFailures<TRow>(
        ICollectorDefinition<TRow> definition,
        ServerRuntime server,
        IReadOnlyList<EnumerationProbeFailure> probeFailures)
    {
        if (probeFailures.Count == 0)
        {
            return;
        }

        var shown = Math.Min(probeFailures.Count, EnumeratedCollectorDriver.MaxLoggedProbeFailures);
        for (var i = 0; i < shown; i++)
        {
            _logger?.LogWarning(EnumeratedCollectorDriver.ProbeFailureLogTemplate,
                definition.Name, server.Config.DisplayName, probeFailures[i].Item, probeFailures[i].Error);
        }

        if (probeFailures.Count > shown)
        {
            _logger?.LogWarning(EnumeratedCollectorDriver.ProbeFailureOverflowLogTemplate,
                definition.Name, server.Config.DisplayName, probeFailures.Count, probeFailures.Count - shown, shown);
        }
    }

    /// <summary>
    /// Writes ONE batch (one enumerated item / one database, or the whole result set for a plain
    /// collector) to Postgres as a single binary COPY on the caller's already-open connection (#1556).
    /// The three collection paths route through here so the storage logic — the prefix columns, the
    /// naive-UTC stamp, the positional payload — lives once. A batch is atomic and independent: on a
    /// mid-run abort the batches already written stay committed (commit-1..N-1). An empty batch opens
    /// no COPY and returns 0 (rows_collected = Σ non-empty batch counts).
    ///
    /// <para>Collectors that divert large text payloads into the hash-keyed dimension tables (#1767 —
    /// query_stats, procedure_stats) wrap the COPY and the dimension upsert in ONE explicit
    /// transaction, so no reader can observe a fact row whose digest has no dimension row. Every
    /// other collector keeps the pre-#1767 path exactly, where completing the importer is itself the
    /// commit.</para>
    /// </summary>
    private async Task<int> WriteBatchAsync<TRow>(
        NpgsqlConnection pgConnection,
        ICollectorDefinition<TRow> definition,
        List<TRow> rows,
        ServerRuntime server,
        DateTime collectionTime,
        CollectorContext context,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var rowsWritten = 0;
        var writer = new PgCollectorRowWriter();

        /* #1767: which payload columns (if any) store a content digest and send their text to a
           dimension table instead of inline onto every row. Derived from the same schema
           CopyCommandFor derives its column list from, so the two cannot disagree. */
        var diversionPlan = PayloadDimensions.DiversionPlanFor(definition);
        var dimensions = new PayloadDimensionBatch();
        if (diversionPlan.Count > 0)
        {
            writer.UseDimensions(diversionPlan, dimensions);
        }

        /* Only the diverting collectors need a transaction; everything else keeps the pre-#1767
           single-COPY commit and pays nothing. */
        await using var transaction = diversionPlan.Count > 0
            ? await pgConnection.BeginTransactionAsync(cancellationToken)
            : null;

        /* Naive-UTC storage — see PgCollectorRowWriter. */
        var storedCollectionTime = DateTime.SpecifyKind(collectionTime, DateTimeKind.Unspecified);

        using (var importer = await pgConnection.BeginBinaryImportAsync(
            PgCollectorRowWriter.CopyCommandFor(definition), cancellationToken))
        {
            writer.Importer = importer;

            foreach (var row in rows)
            {
                await importer.StartRowAsync(cancellationToken);

                if (definition.IncludesCollectionId)
                {
                    writer.Value(CollectionIdGenerator.Next());
                }

                writer.Value(storedCollectionTime)
                      .Value(server.ServerId)
                      .Value(server.StorageName);

                writer.BeginPayload();
                definition.WritePayload(row, writer, context);
                writer.EndPayload(definition.PayloadColumns.Count);
                rowsWritten++;
            }

            await importer.CompleteAsync(cancellationToken);
        }

        if (transaction is not null)
        {
            await PayloadDimensionWriter.FlushAsync(
                pgConnection, transaction, dimensions, storedCollectionTime, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return rowsWritten;
    }

    /// <summary>
    /// Runs a collector definition against one monitored server's LIVE connection and RETURNS the shredded
    /// rows WITHOUT writing them to the store — the read-only "fetch phase only" twin of <see cref="RunAsync"/>,
    /// for an on-demand read (the live Current Active Queries snapshot the <c>fetch_active_queries</c> command
    /// serves). It builds the SAME <see cref="CollectorContext"/> the scheduled sweep builds (the shared delta
    /// calculator, the live capture-plan / schema-change providers, the server's excluded databases, the
    /// ignored-wait defaults), so the live query is byte-identical to the collector's, then opens ONE SqlClient
    /// connection and runs the definition's single query and shredder. It deliberately supports ONLY the
    /// single-statement path (no per-database enumeration, no per-item enumeration, no supplemental query): it
    /// exists for <see cref="QuerySnapshotsCollector"/>, whose Azure variant already reads what it needs from one
    /// connection. A collector that does not apply to the target yields an empty list (mirrors
    /// <see cref="RunAsync"/>). Cancellation is honored; a <c>SqlException</c> propagates to the caller, which
    /// maps it to a legible command outcome (timeout / permission / error) exactly as the actual-plan handler does.
    /// </summary>
    public async Task<List<TRow>> FetchRowsAsync<TRow>(
        ICollectorDefinition<TRow> definition,
        ServerRuntime server,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (!definition.AppliesTo(server.Target))
        {
            return new List<TRow>();
        }

        var context = new CollectorContext
        {
            ServerId = server.ServerId,
            ServerName = server.StorageName,
            CollectionTime = DateTime.UtcNow,
            Deltas = _deltas,
            Target = server.Target,
            Watermark = null,
            NumericWatermark = null,
            HasCollectedBefore = false,
            IgnoredWaitTypes = IgnoredWaitDefaults.All,
            ExcludedDatabases = server.Config.ExcludedDatabases?.ToArray() ?? Array.Empty<string>(),
            PerfmonCounterOverride = null,
            CapturePlanXml = _capturePlans(),
            CollectSchemaChangeEvents = _collectSchemaChanges(),
        };

        var plan = definition.BuildQuery(context);

        using var connection = new SqlConnection(server.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = CreateCollectorCommand(plan, connection, commandTimeoutSeconds);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await definition.ReadAsync(reader, context, cancellationToken);
    }

    /// <summary>
    /// Gets the most recent value of a timestamp column from Postgres for incremental collection.
    /// Returns null on first run or if the query fails (caller uses a fallback window) — the
    /// Postgres twin of Lite's GetLastCollectedTimeAsync.
    /// </summary>
    public async Task<DateTime?> GetLastCollectedTimeAsync(
        int serverId, string tableName, string columnName, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(
                $"SELECT MAX({columnName}) FROM {tableName} WHERE server_id = $1", connection);
            command.Parameters.AddWithValue(serverId);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is DateTime dt)
            {
                return dt;
            }
        }
        catch
        {
            /* If the Postgres query fails, caller uses fallback window */
        }
        return null;
    }

    /// <summary>
    /// The stored per-server state for one collector's declared keys (#1962) — the sibling of
    /// <see cref="GetLastCollectedTimeAsync"/> for state no MAX() over the collected rows can produce.
    /// Read only for the collectors that declare keys, so it costs the rest nothing. An empty result on
    /// failure is the SAFE direction: every definition treats absent state as its conservative path
    /// (default_trace_events re-reads the whole rollover set), so a broken read costs time, never events.
    /// Lite's twin is <c>RemoteCollectorService.GetCollectorStateAsync</c> — same table, same columns.
    /// </summary>
    public async Task<Dictionary<string, string>> GetCollectorStateAsync(
        int serverId, string collectorName, CancellationToken cancellationToken)
    {
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(
                "SELECT state_key, state_value FROM collector_state WHERE server_id = $1 AND collector_name = $2", connection);
            command.Parameters.AddWithValue(serverId);
            command.Parameters.AddWithValue(collectorName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(1))
                {
                    state[reader.GetString(0)] = reader.GetString(1);
                }
            }
        }
        catch (Exception ex)
        {
            /* Fail toward "no state" — the definition's conservative path, never a wrong-but-plausible one. */
            _logger?.LogDebug(ex, "Reading collector state for {Collector} failed; using the no-state path", collectorName);
        }
        return state;
    }

    /// <summary>
    /// Upserts what the definition observed this cycle (<see cref="CollectorContext.PendingState"/>),
    /// after the cycle completed — so a cycle that collected zero rows still records what it saw, which is
    /// the whole point of keeping this state off the payload. Best-effort: a failed write leaves the older
    /// value, and the next cycle re-derives from it or falls back.
    /// </summary>
    public async Task SaveCollectorStateAsync(
        int serverId, string collectorName, IReadOnlyDictionary<string, string> state, CancellationToken cancellationToken)
    {
        if (state.Count == 0)
        {
            return;
        }

        try
        {
            await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
            foreach (var entry in state)
            {
                /* One statement per key: Npgsql's positional parameters cannot span a multi-statement
                   batch (they bind to the FIRST statement and the rest fail silently), and this loop
                   runs over a single declared key today. */
                using var command = new NpgsqlCommand(@"
INSERT INTO collector_state (server_id, collector_name, state_key, state_value, updated_at)
VALUES ($1, $2, $3, $4, $5)
ON CONFLICT (server_id, collector_name, state_key)
DO UPDATE SET state_value = EXCLUDED.state_value, updated_at = EXCLUDED.updated_at", connection);
                command.Parameters.AddWithValue(serverId);
                command.Parameters.AddWithValue(collectorName);
                command.Parameters.AddWithValue(entry.Key);
                command.Parameters.AddWithValue(entry.Value);
                /* Naive UTC, Kind-Unspecified — the product-wide PG timestamp discipline
                   (PgAlertStateStore.NaiveUtcNow, DarlingObservability, the storedCollectionTime below).
                   updated_at is `timestamp` WITHOUT time zone, and binding a Kind=Utc DateTime does not
                   fail: Npgsql infers `timestamptz` from the Kind, PostgreSQL casts it into the column,
                   and the cast renders it in the SERVER's zone — so the row lands silently offset by the
                   server's UTC offset (measured at exactly 4h on an America/New_York store) while every
                   other timestamp in the store is UTC. Nothing throws and nothing logs; the column simply
                   disagrees with the rest of the store. */
                command.Parameters.AddWithValue(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Storing collector state for {Collector} failed; next cycle uses the older value", collectorName);
        }
    }

    /// <summary>
    /// Records a clamp-opened Query Store hole for the #2022 backfill worker, under the WORKER's
    /// collector_state name (not the definition's — query_store still declares no state keys).
    /// Merged wider with any pending hole so a repeat outage cannot overwrite an unserviced one.
    /// Best-effort: a lost record means a lost backfill opportunity, never wrong data — the live
    /// path's own WARNING already disclosed the hole.
    /// </summary>
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
            _logger?.LogDebug(ex, "Recording query_store backfill hole for [{Database}] failed; the live WARNING remains the disclosure", databaseName);
        }
    }

    /// <summary>
    /// Deletes ONE collector_state key — the backfill worker's retirement path for a serviced or
    /// expired hole record (#2022). Best-effort like its siblings: a failed delete leaves the row,
    /// and the worker's scan re-derives the same verdict next tick.
    /// </summary>
    public async Task DeleteCollectorStateKeyAsync(
        int serverId, string collectorName, string stateKey, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(
                "DELETE FROM collector_state WHERE server_id = $1 AND collector_name = $2 AND state_key = $3", connection);
            command.Parameters.AddWithValue(serverId);
            command.Parameters.AddWithValue(collectorName);
            command.Parameters.AddWithValue(stateKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Deleting collector state {Key} for {Collector} failed; next tick re-derives", stateKey, collectorName);
        }
    }

    /// <summary>
    /// The #2022 backfill write entry: the SAME private COPY writer every live path routes through
    /// (dimension diversion, positional contract, naive-UTC stamp), on its own store connection.
    /// <paramref name="collectionTime"/> is the slice's BACKDATED ceiling — see QueryStoreBackfill's
    /// horizon contract for why that is safe only inside the raw tier's window.
    /// </summary>
    public async Task<int> WriteBackfillBatchAsync<TRow>(
        ICollectorDefinition<TRow> definition, List<TRow> rows, ServerRuntime server,
        DateTime collectionTime, CollectorContext context, CancellationToken cancellationToken)
    {
        await using var pgConnection = await _postgres.OpenConnectionAsync(cancellationToken);
        return await WriteBatchAsync(pgConnection, definition, rows, server, collectionTime, context, cancellationToken);
    }

    /// <summary>
    /// Postgres twin of Lite's GetLastCollectedTimeForDatabaseAsync: the newest already-collected
    /// value for ONE database, for definitions with a PerDatabaseWatermarkColumn (Azure SQL DB
    /// per-database XE capture, #1535). Null on first run for that database or on failure — the
    /// caller falls back to the definition's documented window.
    /// </summary>
    public async Task<DateTime?> GetLastCollectedTimeForDatabaseAsync(
        int serverId, string tableName, string columnName, string databaseColumnName, string databaseName, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(
                $"SELECT MAX({columnName}) FROM {tableName} WHERE server_id = $1 AND {databaseColumnName} = $2", connection);
            command.Parameters.AddWithValue(serverId);
            command.Parameters.AddWithValue(databaseName);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is DateTime dt)
            {
                return dt;
            }
        }
        catch
        {
            /* If the Postgres query fails, caller uses fallback window */
        }
        return null;
    }

    /// <summary>
    /// Gets the most recent value of a monotonic bigint identity column from Postgres for incremental
    /// collection — the numeric twin of <see cref="GetLastCollectedTimeAsync"/> (job_history dedups on
    /// <c>instance_id</c>, sysjobhistory's IDENTITY bigint). Returns null on first run or if the query
    /// fails (caller uses its documented first-run/fallback path).
    /// </summary>
    public async Task<long?> GetLastCollectedInstanceIdAsync(
        int serverId, string tableName, string columnName, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(
                $"SELECT MAX({columnName}) FROM {tableName} WHERE server_id = $1", connection);
            command.Parameters.AddWithValue(serverId);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not null && result != DBNull.Value)
            {
                return Convert.ToInt64(result);
            }
        }
        catch
        {
            /* If the Postgres query fails, caller uses fallback window */
        }
        return null;
    }

    /// <summary>
    /// Whether a prior SUCCESS row exists in collection_log for this collector+server — the "has collected
    /// before" signal (<see cref="CollectorContext.HasCollectedBefore"/>), consulted only when the watermark
    /// is null. Returns false on any failure, which errs toward the all-history first run (correct for a
    /// genuinely fresh store).
    /// </summary>
    public async Task<bool> HasPriorCollectorSuccessAsync(int serverId, string collectorName, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(
                "SELECT EXISTS(SELECT 1 FROM collection_log WHERE server_id = $1 AND collector_name = $2 AND status = 'SUCCESS')", connection);
            command.Parameters.AddWithValue(serverId);
            command.Parameters.AddWithValue(collectorName);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is bool b && b;
        }
        catch
        {
            /* Fail toward first-run (all-history) — matches a fresh store with no log yet. */
            return false;
        }
    }

    /// <summary>
    /// Drops any cached master-inaccessible verdict for a server that has just reconnected.
    ///
    /// Azure SQL DB reports "this login may not read master" and "you cannot reach this server right
    /// now" with overlapping error numbers, so a verdict formed while a server was failing is not
    /// trustworthy. The moment it answers again is the moment to discard that verdict and re-probe.
    /// Without this, a transient outage permanently misfiles a login that CAN read master, and
    /// database-scoped collection stays degraded until the service restarts (#1506).
    /// </summary>
    public void OnServerReconnected(int serverId)
    {
        if (_azureMasterInaccessibleSince.TryRemove(serverId, out _))
        {
            _logger?.LogInformation("[server_id {ServerId}] reconnected — re-probing master for database-scoped collectors.", serverId);
        }
    }

    /// <summary>
    /// Lists databases on an Azure SQL DB logical server, mirroring Lite's #857 behavior: try
    /// master enumeration first (with the per-server exclusion filter), and on a master-access
    /// error fall back to the connection's own database, throttling re-probes per server.
    /// </summary>
    internal async Task<List<string>> GetAzureDatabaseListAsync(ServerRuntime server, CancellationToken cancellationToken)
    {
        var targetDb = new SqlConnectionStringBuilder(server.ConnectionString).InitialCatalog;

        /* Skip the throttle when there is nothing to fall back TO — see Lite's twin. With no target
           database the fallback can only throw, so honouring the throttle would guarantee 15 minutes
           of failure without ever attempting to recover. */
        var hasFallback = SingleDbOrEmpty(targetDb).Count > 0;

        if (hasFallback && IsMasterProbeThrottled(server.ServerId))
        {
            return FallbackDatabaseList(server, targetDb, reason: "master previously inaccessible", quiet: true);
        }

        var masterConnectionString = new SqlConnectionStringBuilder(server.ConnectionString)
        {
            InitialCatalog = "master",
        }.ConnectionString;

        var (exclusionClause, exclusionParameters) = DatabaseExclusionFilter.Build(
            server.Config.ExcludedDatabases, "name");

        var databases = new List<string>();
        try
        {
            using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync(cancellationToken);
            using var command = new SqlCommand(
                $"SELECT name FROM sys.databases WHERE state_desc = N'ONLINE' AND database_id > 0 {exclusionClause} ORDER BY name;",
                connection)
            { CommandTimeout = CommandTimeoutSeconds };
            foreach (var parameter in exclusionParameters)
            {
                command.Parameters.Add(ToSqlParameter(parameter));
            }
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                databases.Add(reader.GetString(0));
            }

            _azureMasterInaccessibleSince.TryRemove(server.ServerId, out _);
            return databases;
        }
        catch (SqlException ex) when (ShouldFallBackToSingleDatabaseError(ex.Number))
        {
            _azureMasterInaccessibleSince[server.ServerId] = DateTime.UtcNow;

            return FallbackDatabaseList(server, targetDb, reason: $"master DB inaccessible (SQL error {ex.Number})");
        }
    }

    /// <summary>
    /// True while a recent master-inaccessible verdict still stands. It expires so a server whose
    /// access was restored recovers on its own rather than staying degraded until restart (#1506).
    /// </summary>
    private bool IsMasterProbeThrottled(int serverId)
    {
        if (!_azureMasterInaccessibleSince.TryGetValue(serverId, out var deniedAt))
        {
            return false;
        }

        if (DateTime.UtcNow - deniedAt < AzureMasterRecheckInterval)
        {
            return true;
        }

        _azureMasterInaccessibleSince.TryRemove(serverId, out _);
        return false;
    }

    /// <summary>
    /// The database list to use when master cannot be enumerated: the connection's own catalog.
    ///
    /// When there isn't one, database-scoped collectors have nowhere to read from. That used to be a
    /// warning and an empty list, which made every one of them report success having collected zero
    /// rows. Throwing puts the failure where it can actually be seen (#1506).
    /// </summary>
    /// <param name="quiet">
    /// Set on the throttled path, which runs for every database-scoped collector on every cycle. Only
    /// forming the verdict is worth an Information line; re-reading it is not.
    /// </param>
    private List<string> FallbackDatabaseList(ServerRuntime server, string? targetDb, string reason, bool quiet = false)
    {
        var fallback = SingleDbOrEmpty(targetDb);

        if (fallback.Count == 0)
        {
            throw new InvalidOperationException(
                $"{reason}, and this connection has no target database to fall back to (it resolves to " +
                $"master). Set a database for '{server.Config.DisplayName}' so database-scoped collectors " +
                $"have something to read.");
        }

        if (quiet)
        {
            _logger?.LogDebug("[{Server}] {Reason} — collecting from '{Database}' only.",
                server.Config.DisplayName, reason, targetDb);
        }
        else
        {
            _logger?.LogInformation("[{Server}] {Reason} — collecting from '{Database}' only.",
                server.Config.DisplayName, reason, targetDb);
        }

        return fallback;
    }

    internal async Task<SqlConnection> OpenAzureDatabaseConnectionAsync(ServerRuntime server, string databaseName, CancellationToken cancellationToken)
    {
        var connectionString = new SqlConnectionStringBuilder(server.ConnectionString)
        {
            InitialCatalog = databaseName,
        }.ConnectionString;

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static List<string> SingleDbOrEmpty(string? targetDb)
    {
        if (string.IsNullOrEmpty(targetDb) || string.Equals(targetDb, "master", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string>();
        }
        return new List<string> { targetDb };
    }

    /// <summary>
    /// Whether master enumeration failed in a way that means database-scoped collectors should fall back
    /// to the connection's own catalog (#857). Deliberately broader than "this login cannot read master":
    /// a 40615 firewall rejection at the logical server says nothing about the login's rights, but the
    /// fallback still works, because Azure evaluates DATABASE-level firewall rules first and a user
    /// database can be reachable while master is not (#1631). The list — and the reason a reachability
    /// error must never be read as a rights verdict (#1506) — is owned by
    /// <see cref="SqlErrorClassification"/>, shared with Lite so the two cannot drift. This bug reached
    /// Darling because the list was duplicated here.
    /// </summary>
    internal static bool ShouldFallBackToSingleDatabaseError(int errorNumber) =>
        SqlErrorClassification.ShouldFallBackToSingleDatabase(errorNumber);

    /* Internal, not private: QueryStoreBackfill (#2022) builds its slice commands through the same
       parameter mapping so the two paths cannot drift on a type. */
    internal static SqlCommand CreateCollectorCommand(CollectorQuery plan, SqlConnection connection, int commandTimeoutSeconds)
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
        CollectorParameterType.NVarChar260 => new SqlParameter(parameter.Name, SqlDbType.NVarChar, 260) { Value = parameter.Value ?? DBNull.Value },
        CollectorParameterType.Int32 => new SqlParameter(parameter.Name, SqlDbType.Int) { Value = parameter.Value ?? DBNull.Value },
        CollectorParameterType.BigInt => new SqlParameter(parameter.Name, SqlDbType.BigInt) { Value = parameter.Value ?? DBNull.Value },
        _ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter.Type, "Unmapped collector parameter type"),
    };
}
