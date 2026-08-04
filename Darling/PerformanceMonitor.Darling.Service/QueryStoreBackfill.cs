/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
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
using Npgsql;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Storage;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// #2022 — Query Store phase 2 (of #1960): the newest-first backfill worker for the history the
/// live path never takes. Phase 1 made the LIVE path hole-free, but two bounded windows still
/// discard history by design: first contact takes only the trailing 60 minutes of a ~30-day
/// catalog, and post-outage catch-up is clamped to 24h (the #1556 incident fix) as a bounded,
/// logged hole. One mechanism fills both:
///
/// <para><b>The tail (first contact).</b> The backfill ceiling is DERIVED, exactly like the live
/// watermark: MIN(last_execution_time) over the rows already stored for a database. Everything at
/// or above that boundary shipped complete (both of phase 1's bounded cuts — TOP ... WITH TIES and
/// the byte budget — finish the boundary tie group), so each slice ships the newest missing chunk
/// strictly BELOW it, and the write itself advances the boundary downward. A pre-existing store
/// whose history already reaches the horizon marks itself done on the first look without shipping
/// a row. The live watermark — MAX() — cannot see backfilled (older) rows by construction, which
/// is the #1960 design constraint: the two paths can never race for the same boundary.</para>
///
/// <para><b>Clamp holes (post-outage).</b> An interior gap is invisible to MIN/MAX, so the runner
/// records it at the moment the 24h clamp fires — (raw watermark, clamped floor), merged wider on
/// a repeat clamp — under this worker's own <c>collector_state</c> rows
/// (<see cref="StateCollectorName"/>; deliberately NOT the definition's StateKeys machinery, so
/// query_store itself still declares none). The worker services the hole newest-first and shrinks
/// its ceiling as slices land, deleting the record when the hole is filled, empty, or expired.</para>
///
/// <para><b>The horizon.</b> Slices carry a BACKDATED collection_time (the slice ceiling), so the
/// rows land in time buckets adjacent to their own activity — readers window on collection_time,
/// hourly CAGGs bucket on it, and retention drops on it. That is exactly why this stage refuses to
/// dig below <see cref="Horizon"/> (derived from the raw tier, not hand-maintained — the #1937
/// rule): inside it, every CAGG's 3-day <c>start_offset</c> re-materializes the touched buckets on
/// its next scheduled run and the 4-day raw retention cannot immediately drop what was just
/// shipped; below it, neither holds, and that deeper stage is a separate decision (#2022's own
/// staging note). Re-shipped interval rows are already deduped by every reader (#1907/#1841), so a
/// boundary overlap is waste at worst, never a double-count.</para>
///
/// <para><b>Pacing.</b> One slice per server per tick on the worker's OWN loop (the command-loop
/// precedent), each slice bounded by the same per-database byte budget as the live path, on its own
/// SQL and store connections, never touching the sweep gate — backfill can be slow forever without
/// delaying collection. Azure SQL DB targets are deferred (per-database connections need the Azure
/// branch's plumbing); Lite is deferred with it — both are follow-up scope, not accidents.</para>
/// </summary>
public sealed class QueryStoreBackfill
{
    /// <summary>The collector_state owner name for this worker's rows — distinct from the
    /// query_store definition on purpose, so the definition keeps declaring NO state keys and the
    /// state-contract pins stay honest.</summary>
    public const string StateCollectorName = "query_store_backfill";

    /// <summary>State key prefix marking a database's first-contact tail as drained (value: when).</summary>
    public const string DoneKeyPrefix = "done:";

    /// <summary>State key prefix for a recorded clamp hole (value: <see cref="EncodeHole"/>).</summary>
    public const string HoleKeyPrefix = "hole:";

    /// <summary>
    /// How far below now a backfill slice may reach — the raw tier's read horizon
    /// (<see cref="RetentionTierRouter.RawMaxAge"/>, raw retention minus the route margin), derived
    /// so it can never drift from the retention/CAGG numbers it exists to respect.
    /// </summary>
    public static readonly TimeSpan Horizon = RetentionTierRouter.RawMaxAge;

    /// <summary>Candidate databases come from rows this window fresh — a database that stopped
    /// shipping Query Store rows entirely ages out of the backfill scan with them.</summary>
    private static readonly TimeSpan CandidateWindow = TimeSpan.FromDays(7);

    private readonly NpgsqlDataSource _postgres;
    private readonly DarlingCollectorRunner _runner;
    private readonly CollectorDeltaCalculator _deltas;
    private readonly ILogger? _logger;
    private readonly Func<bool> _capturePlans;

    public QueryStoreBackfill(
        NpgsqlDataSource postgres,
        DarlingCollectorRunner runner,
        CollectorDeltaCalculator deltas,
        ILogger? logger,
        Func<bool>? capturePlans = null)
    {
        _postgres = postgres ?? throw new ArgumentNullException(nameof(postgres));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _deltas = deltas ?? throw new ArgumentNullException(nameof(deltas));
        _logger = logger;
        _capturePlans = capturePlans ?? (() => true);
    }

    /// <summary>
    /// Runs AT MOST one backfill slice for one server: the first database found with a pending
    /// hole or an undrained first-contact tail gets one byte-budgeted slice; everything else waits
    /// for a later tick. Returns true when a slice (or an exhaustion probe) ran, false when the
    /// server had no backfill work — the common steady state, costing one candidate query and a
    /// few MIN() lookups.
    /// </summary>
    public async Task<bool> RunServerSliceAsync(ServerRuntime server, CancellationToken cancellationToken)
    {
        /* Azure SQL DB reaches Query Store through per-database connections (#1836) — deferred
           scope for this worker; the tail/hole state model is connection-shape-agnostic, so the
           Azure arm can be added without changing what is stored. */
        if (server.Target.IsAzureSqlDb || !QueryStoreCollector.Instance.AppliesTo(server.Target))
        {
            return false;
        }

        var state = await _runner.GetCollectorStateAsync(server.ServerId, StateCollectorName, cancellationToken);
        var databases = await GetCandidateDatabasesAsync(server.ServerId, cancellationToken);

        var nowUtc = DateTime.UtcNow;
        var floorLimit = nowUtc - Horizon;

        foreach (var databaseName in databases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            /* Holes before the tail: a recorded outage gap is the history closest to expiring. */
            if (state.TryGetValue(HoleKeyPrefix + databaseName, out var encoded)
                && TryDecodeHole(encoded, out var holeFrom, out var holeTo))
            {
                if (holeTo <= floorLimit)
                {
                    /* The whole hole sank below the horizon before it was serviced — expired, and
                       deliberately NOT dug after: the staging rule above. */
                    await _runner.DeleteCollectorStateKeyAsync(server.ServerId, StateCollectorName, HoleKeyPrefix + databaseName, cancellationToken);
                    continue;
                }

                var holeFloor = holeFrom > floorLimit ? holeFrom : floorLimit;
                await RunSliceAsync(server, databaseName, holeFloor, holeTo, isHole: true, cancellationToken);
                return true;
            }

            if (state.ContainsKey(DoneKeyPrefix + databaseName))
            {
                continue;
            }

            /* The derived ceiling: everything at or above the stored MIN shipped complete. Null
               means the live path has not made first contact for this database yet — its 60-minute
               first window establishes the ceiling this worker digs below. */
            var storedFloor = await GetStoredFloorAsync(server.ServerId, databaseName, cancellationToken);
            if (storedFloor is null)
            {
                continue;
            }

            if (storedFloor <= floorLimit)
            {
                /* History already reaches the horizon — the pre-existing-store case, marked done
                   without shipping a row so the steady state never re-probes it. */
                await SaveStateAsync(server.ServerId, DoneKeyPrefix + databaseName, nowUtc.ToString("o", CultureInfo.InvariantCulture), cancellationToken);
                continue;
            }

            await RunSliceAsync(server, databaseName, floorLimit, storedFloor.Value, isHole: false, cancellationToken);
            return true;
        }

        return false;
    }

    /// <summary>
    /// One byte-budgeted, newest-first slice for one database: probe PRODUCTVERSION (the same
    /// version gates as the live path, so the reader ordinals cannot differ), run the backfill
    /// window query, read through the SAME budget machinery, COPY through the SAME writer with the
    /// slice ceiling as the backdated collection_time, then advance the boundary — derived for the
    /// tail, shrunk-and-saved for a hole. An empty slice means Query Store retains nothing in the
    /// window: the tail marks done, the hole record deletes.
    /// </summary>
    private async Task RunSliceAsync(
        ServerRuntime server, string databaseName, DateTime floorUtc, DateTime ceilingUtc, bool isHole, CancellationToken cancellationToken)
    {
        var definition = QueryStoreCollector.Instance;
        var context = new CollectorContext
        {
            ServerId = server.ServerId,
            ServerName = server.StorageName,
            CollectionTime = DateTime.UtcNow,
            Deltas = _deltas,
            Target = server.Target,
            ExcludedDatabases = server.Config.ExcludedDatabases?.ToArray() ?? Array.Empty<string>(),
            CapturePlanXml = _capturePlans(),
        };

        using var sqlConnection = new SqlConnection(server.ConnectionString);
        await sqlConnection.OpenAsync(cancellationToken);

        /* Same best-effort 10-second PRODUCTVERSION probe as the live enumeration path — the
           version gates shape the SELECT, and the fallback default is the conservative one. */
        var probePlan = definition.BuildEnumerationProbe(context);
        if (probePlan is not null)
        {
            try
            {
                using var probeCommand = DarlingCollectorRunner.CreateCollectorCommand(probePlan, sqlConnection, 10);
                var probeResult = await probeCommand.ExecuteScalarAsync(cancellationToken);
                if (probeResult is not null && probeResult != DBNull.Value)
                {
                    context.EnumerationProbeResult = probeResult;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug("Backfill version probe on '{Server}' failed; using defaults: {Error}",
                    server.Config.DisplayName, ex.Message);
            }
        }

        var plan = definition.BuildBackfillPerItemQuery(databaseName, context, floorUtc, ceilingUtc);
        var rows = new List<QueryStoreCollector.Row>();
        using (var command = DarlingCollectorRunner.CreateCollectorCommand(
                   plan, sqlConnection, definition.CommandTimeoutSecondsOverride ?? DarlingCollectorRunner.CommandTimeoutSeconds))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await definition.ReadItemAsync(databaseName, reader, rows, context, cancellationToken);
        }

        if (rows.Count == 0)
        {
            /* Query Store retains nothing inside the window — the monitored catalog is shorter
               than the horizon (or the hole's span was never persisted at the source). Terminal
               for this range, and cheaper to record than to re-ask every tick. */
            if (isHole)
            {
                await _runner.DeleteCollectorStateKeyAsync(server.ServerId, StateCollectorName, HoleKeyPrefix + databaseName, cancellationToken);
            }
            else
            {
                await SaveStateAsync(server.ServerId, DoneKeyPrefix + databaseName, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), cancellationToken);
            }

            _logger?.LogInformation(
                "query_store backfill on '{Server}' [{Database}]: nothing retained below {Ceiling:o} — {Range} complete.",
                server.Config.DisplayName, databaseName, ceilingUtc, isHole ? "hole" : "tail");
            return;
        }

        /* Backdated so the rows land beside their own activity — see the class doc's horizon
           contract. The ceiling, not each row's interval, keeps the write one batch. */
        var written = await _runner.WriteBackfillBatchAsync(definition, rows, server, ceilingUtc, context, cancellationToken);

        var boundary = context.PerItemShippedBoundary;
        if (isHole)
        {
            if (boundary is null || boundary <= floorUtc)
            {
                await _runner.DeleteCollectorStateKeyAsync(server.ServerId, StateCollectorName, HoleKeyPrefix + databaseName, cancellationToken);
            }
            else
            {
                /* Shrink the ceiling to the oldest shipped row; the from-side stays at the floor we
                   actually used (anything below it is horizon-expired either way). */
                await SaveStateAsync(server.ServerId, HoleKeyPrefix + databaseName, EncodeHole(floorUtc, boundary.Value), cancellationToken);
            }
        }
        else if (boundary is not null && boundary <= floorUtc)
        {
            await SaveStateAsync(server.ServerId, DoneKeyPrefix + databaseName, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), cancellationToken);
        }

        _logger?.LogInformation(
            "query_store backfill on '{Server}' [{Database}]: shipped {Rows} rows ({ShippedMB:F1}MB) down to {Boundary:o} ({Range}, ceiling {Ceiling:o}).",
            server.Config.DisplayName, databaseName, written,
            context.PerItemTextBytesShipped / (1024.0 * 1024.0),
            boundary ?? floorUtc, isHole ? "hole" : "tail", ceilingUtc);
    }

    /// <summary>Encodes a hole range as <c>from|to</c> in round-trip format — deliberately not
    /// JSON, so the state row stays greppable and the codec dependency-free.</summary>
    public static string EncodeHole(DateTime fromUtc, DateTime toUtc)
        => fromUtc.ToString("o", CultureInfo.InvariantCulture) + "|" + toUtc.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Decodes <see cref="EncodeHole"/>; false on any malformed value, which the scan
    /// treats as "no hole recorded" — the conservative direction (the tail logic still runs).</summary>
    public static bool TryDecodeHole(string encoded, out DateTime fromUtc, out DateTime toUtc)
    {
        fromUtc = default;
        toUtc = default;

        var split = encoded.Split('|');
        if (split.Length != 2)
        {
            return false;
        }

        return DateTime.TryParseExact(split[0], "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out fromUtc)
            && DateTime.TryParseExact(split[1], "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out toUtc)
            && fromUtc < toUtc;
    }

    /// <summary>Merges a newly-clamped hole into whatever is already recorded — a repeat outage
    /// WIDENS the range rather than overwriting it, so the earlier hole cannot be lost.</summary>
    public static (DateTime FromUtc, DateTime ToUtc) MergeHole(string? existingEncoded, DateTime fromUtc, DateTime toUtc)
    {
        if (existingEncoded is not null && TryDecodeHole(existingEncoded, out var f, out var t))
        {
            return (fromUtc < f ? fromUtc : f, toUtc > t ? toUtc : t);
        }

        return (fromUtc, toUtc);
    }

    private Task SaveStateAsync(int serverId, string key, string value, CancellationToken cancellationToken)
        => _runner.SaveCollectorStateAsync(
            serverId, StateCollectorName, new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value }, cancellationToken);

    /// <summary>Databases that shipped query_store rows recently — the backfill universe. The
    /// store already knows them, so no live enumeration (and no probing of QS-ineligible
    /// databases) is needed.</summary>
    private async Task<List<string>> GetCandidateDatabasesAsync(int serverId, CancellationToken cancellationToken)
    {
        var databases = new List<string>();
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(
                "SELECT DISTINCT database_name FROM query_store_stats WHERE server_id = $1 AND collection_time > $2 ORDER BY database_name", connection);
            command.Parameters.AddWithValue(serverId);
            command.Parameters.AddWithValue(DateTime.SpecifyKind(DateTime.UtcNow - CandidateWindow, DateTimeKind.Unspecified));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0))
                {
                    databases.Add(reader.GetString(0));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "query_store backfill candidate read failed; skipping this tick");
        }

        return databases;
    }

    /// <summary>MIN(last_execution_time) stored for one database — the derived backfill ceiling,
    /// the mirror of the runner's MAX() watermark reads. Null (no rows / failure) skips the
    /// database this tick; failure never invents a boundary.</summary>
    private async Task<DateTime?> GetStoredFloorAsync(int serverId, string databaseName, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(
                "SELECT MIN(last_execution_time) FROM query_store_stats WHERE server_id = $1 AND database_name = $2", connection);
            command.Parameters.AddWithValue(serverId);
            command.Parameters.AddWithValue(databaseName);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is DateTime dt)
            {
                return dt;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "query_store backfill floor read failed for [{Database}]; skipping this tick", databaseName);
        }

        return null;
    }
}
