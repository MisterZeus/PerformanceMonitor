/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Common;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Service.Mcp;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #2495: the window ANCHOR. Every windowed read took <c>hours_back</c> measured from now, so nothing
/// could ask about a past incident — and widening <c>hours_back</c> until the incident falls inside is
/// NOT the same question, because for an aggregate read a wider window is a different answer rather than
/// the same answer with more rows.
///
/// <para>These are the ungated halves: the shared resolver's contract, and the two surfaces agreeing about
/// which reads carry the parameter. The live half — seed a past window, read it anchored, read it
/// unanchored, prove the two disagree — is <see cref="AsOfWindowAnchorLivePostgresTests"/>.</para>
/// </summary>
public sealed class AsOfWindowAnchorTests
{
    /* ── the resolver's contract ── */

    [Fact]
    public void NoAnchor_ResolvesToNow_WhichIsThePreChangeBehaviour()
    {
        var before = DateTime.UtcNow;
        Assert.Null(McpHelpers.ResolveAsOf(null, out var end));
        var after = DateTime.UtcNow;

        Assert.InRange(end, before, after);
        Assert.Equal(DateTimeKind.Utc, end.Kind);
    }

    [Fact]
    public void BlankAnchor_IsTreatedAsAbsent_NotAsAParseFailure()
    {
        var before = DateTime.UtcNow;
        Assert.Null(McpHelpers.ResolveAsOf("   ", out var end));
        Assert.True(end >= before);
    }

    /// <summary>
    /// The four accepted spellings all land on ONE instant. The offset-less form matters most: it is read
    /// as UTC, never as the SERVICE HOST's local time — the store is UTC throughout and the caller is an
    /// agent on some other machine, so a local-time reading would silently shift the window by the host's
    /// offset with nothing in the result to show for it.
    /// </summary>
    [Theory]
    [InlineData("2026-08-18T14:30:00Z")]
    [InlineData("2026-08-18T14:30:00")]
    [InlineData("2026-08-18T16:30:00+02:00")]
    [InlineData("2026-08-18T09:30:00-05:00")]
    public void EveryAcceptedSpelling_ResolvesToTheSameUtcInstant(string asOf)
    {
        Assert.Null(McpHelpers.ResolveAsOf(asOf, out var end));
        Assert.Equal(new DateTime(2026, 8, 18, 14, 30, 0, DateTimeKind.Utc), end);
        Assert.Equal(DateTimeKind.Utc, end.Kind);
    }

    [Fact]
    public void DateOnly_IsMidnightUtc()
    {
        Assert.Null(McpHelpers.ResolveAsOf("2026-08-18", out var end));
        Assert.Equal(new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc), end);
    }

    /// <summary>
    /// An anchor we cannot use is REFUSED, following <see cref="McpHelpers.ValidateTop"/>. Silently falling
    /// back to now is the one outcome this parameter exists to prevent: a read answering "the last 4 hours"
    /// when it was asked for "the 4 hours ending Tuesday 03:00" is indistinguishable from a correct answer.
    /// </summary>
    [Theory]
    [InlineData("last tuesday")]
    [InlineData("2026-13-45")]
    [InlineData("4 hours ago")]
    public void AnUnusableAnchor_IsRefused_NotSilentlyTreatedAsNow(string asOf)
    {
        var error = McpHelpers.ResolveAsOf(asOf, out _);
        Assert.NotNull(error);
        Assert.Contains("Invalid as_of", error, StringComparison.Ordinal);
        Assert.Contains(asOf, error, StringComparison.Ordinal);
    }

    [Fact]
    public void AFutureAnchor_IsRefused_BecauseTheStoreCannotHoldDataNotYetCollected()
    {
        var error = McpHelpers.ResolveAsOf(DateTime.UtcNow.AddHours(2).ToString("o"), out _);
        Assert.NotNull(error);
        Assert.Contains("future", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The future refusal has a clock-skew allowance, and it is not a grace period for asking about the
    /// future: an agent that computes "now" from its own clock and sends it must not be refused because
    /// that clock runs a minute fast, on a stored read whose newest row is minutes old anyway.
    /// </summary>
    [Fact]
    public void AClientClockRunningSlightlyFast_IsStillAccepted()
    {
        var justInside = DateTime.UtcNow + McpHelpers.AsOfFutureTolerance - TimeSpan.FromMinutes(1);
        Assert.Null(McpHelpers.ResolveAsOf(justInside.ToString("o"), out _));
    }

    /// <summary>
    /// There is deliberately NO lower bound. An anchor older than anything the store holds is a legitimate
    /// question whose honest answer is the read's own <c>empty</c> / <c>unavailable</c> status — and the
    /// caller knows the anchor they sent, so that status is unambiguous. A hardcoded floor would have to
    /// guess at retention, which is per-deployment, per-server and per-collector.
    /// </summary>
    [Fact]
    public void AnAnchorOlderThanAnyRetention_IsAccepted_AndLeftToTheReadsOwnMissVocabulary()
    {
        Assert.Null(McpHelpers.ResolveAsOf("1999-01-01T00:00:00Z", out var end));
        Assert.Equal(1999, end.Year);
    }

    /* ── the two knobs together ── */

    [Fact]
    public void ValidateWindow_ReportsTheSpanBeforeTheAnchor_SoTheOldMessageIsUnchanged()
    {
        /* A caller who sent both wrong is told about hours_back first, exactly as before as_of existed. */
        var zeroHours = McpHelpers.ValidateWindow(0, "not a date", out _);
        Assert.NotNull(zeroHours);
        Assert.Contains("hours_back", zeroHours, StringComparison.Ordinal);

        var tooManyHours = McpHelpers.ValidateWindow(McpHelpers.MaxHoursBack + 1, "not a date", out _);
        Assert.NotNull(tooManyHours);
        Assert.Contains("exceeds maximum", tooManyHours, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateWindow_WithNoAnchor_IsExactlyTheOldValidateHoursBack()
    {
        foreach (var hours in new[] { -1, 0, 1, 24, McpHelpers.MaxHoursBack, McpHelpers.MaxHoursBack + 1 })
        {
            Assert.Equal(McpHelpers.ValidateHoursBack(hours), McpHelpers.ValidateWindow(hours, null, out _));
        }
    }

    [Fact]
    public void TheWindow_IsHoursBackEndingAtTheAnchor()
    {
        Assert.Null(McpHelpers.ValidateWindow(3, "2026-08-18T15:30:00Z", out var end));

        Assert.Equal(new DateTime(2026, 8, 18, 15, 30, 0, DateTimeKind.Utc), end);
        Assert.Equal(new DateTime(2026, 8, 18, 12, 30, 0, DateTimeKind.Utc), end.AddHours(-3));
    }

    /* ── the two surfaces carry the same convention ── */

    /// <summary>
    /// A read reachable over MCP but not over <c>/api/read/{name}</c> is the failure mode the catalog exists
    /// to prevent: the descriptor is the ONLY input truth for the web surface, so a parameter missing from it
    /// is simply unreachable there, and an unknown query key is ignored rather than rejected — the panel
    /// quietly gets the read's default window and nothing anywhere says so.
    /// </summary>
    [Fact]
    public void EveryDispatchedReadWhoseToolTakesAnAnchor_AdvertisesItInTheCatalog()
    {
        var missing = DarlingWebEndpoints.BuildReadDispatch().Keys
            .Where(ToolTakesAnAnchor)
            .Where(name => !DarlingWebEndpoints.CatalogDescriptors[name].Params.Any(p => p.Name == "as_of"))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "these reads take as_of over MCP but do not advertise it in the catalog, so it cannot be sent " +
            "over /api/read: " + string.Join(", ", missing));
    }

    /// <summary>The other direction: nothing advertises a parameter its tool would ignore.</summary>
    [Fact]
    public void NoCatalogEntry_AdvertisesAnAnchorItsToolDoesNotTake()
    {
        var extra = DarlingWebEndpoints.BuildReadDispatch().Keys
            .Where(name => DarlingWebEndpoints.CatalogDescriptors[name].Params.Any(p => p.Name == "as_of"))
            .Where(name => !ToolTakesAnAnchor(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(extra.Length == 0, "catalog advertises as_of for reads whose tool ignores it: " + string.Join(", ", extra));
    }

    /// <summary>
    /// The convention itself: a read that windows takes the anchor. The exclusions are named rather than
    /// discovered, so removing one from the list is a deliberate act — and adding a new windowed read
    /// without an anchor fails here rather than shipping half the convention.
    /// </summary>
    [Fact]
    public void EveryWindowedRead_TakesTheAnchor_ExceptTheNamedExclusions()
    {
        /* analyze_server / get_analysis_* / compare_analysis window INSIDE the analysis engine (they hand it
           a bare hours_back), so anchoring them is an engine change rather than a read-surface one, and
           analyze_server also PERSISTS what it finds. get_pvs_stats and get_fleet_overview mix a
           latest-snapshot measurement with a windowed one: anchoring only the windowed half would return a
           result whose two halves describe different instants, which is worse than not offering it.
           get_store_metrics windows in days over the store's own growth series. */
        var excluded = new[]
        {
            "analyze_server", "get_analysis_facts", "get_analysis_findings", "compare_analysis",
            "get_pvs_stats", "get_fleet_overview", "get_store_metrics",
        };

        var unanchored = DarlingWebEndpoints.BuildReadDispatch().Keys
            .Where(name => DarlingWebEndpoints.CatalogDescriptors[name].Params.Any(p => p.Name == "hours"))
            .Where(name => !DarlingWebEndpoints.CatalogDescriptors[name].Params.Any(p => p.Name == "as_of"))
            .Where(name => !excluded.Contains(name, StringComparer.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unanchored.Length == 0,
            "these reads window but cannot be anchored, and are not on the named exclusion list: " +
            string.Join(", ", unanchored));
    }

    private static bool ToolTakesAnAnchor(string readName)
    {
        var method = typeof(DarlingMcpDataTools).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            .FirstOrDefault(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)
                .Cast<ModelContextProtocol.Server.McpServerToolAttribute>()
                .Any(a => a.Name == readName));

        return method is not null && method.GetParameters().Any(p => p.Name == "as_of");
    }
}

/// <summary>
/// Gated (DARLING_TEST_PG) proof of the whole point: rows seeded in a PAST window come back when the read
/// is anchored there and do NOT come back on the default anchor — and widening <c>hours_back</c> instead is
/// a demonstrably different answer, not the same one with more rows.
/// </summary>
[Collection("live-postgres")]
public sealed class AsOfWindowAnchorLivePostgresTests
{
    private const string ServerName = "darling-asof-anchor-e2e";
    private static readonly int ServerId = ServerIdHelper.GetDeterministicHashCode(ServerName);

    /* The incident: 30 hours ago, i.e. OUTSIDE every default window on the surface (the widest default is
       24). "Now" carries a different wait type so the two windows are told apart by content, not by count. */
    private const string IncidentWait = "PAGEIOLATCH_SH";
    private const string RecentWait = "CXPACKET";

    private static string? ConnectionString => Environment.GetEnvironmentVariable("DARLING_TEST_PG");

    [Fact]
    public async Task AnAnchoredRead_SeesThePastWindow_AndTheDefaultAnchorDoesNot()
    {
        var cs = ConnectionString;
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "Set DARLING_TEST_PG to a Postgres connection string to run the live as_of test.");

        var ct = TestContext.Current.CancellationToken;
        using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);
        await DeleteRowsAsync(connection, ct);

        await using var postgres = NpgsqlDataSource.Create(cs!);

        var bodySucceeded = false;
        try
        {
            await RegisterServerAsync(connection, ct);

            var now = Truncate(DateTime.UtcNow);
            var incident = now.AddHours(-30);
            var anchor = incident.AddMinutes(30).ToString("o");   /* the incident sits inside a 4-hour window ending here */

            await PlantWaitAsync(connection, incident, IncidentWait, 900_000L, ct);
            await PlantWaitAsync(connection, incident.AddMinutes(5), IncidentWait, 800_000L, ct);
            await PlantWaitAsync(connection, now.AddMinutes(-10), RecentWait, 1_000L, ct);

            /* 1. The default anchor is unchanged: the last 24 hours, which the incident is not in. */
            var live = await DarlingMcpDataTools.GetWaitStats(postgres, ServerName);
            Assert.Contains(RecentWait, live, StringComparison.Ordinal);
            Assert.DoesNotContain(IncidentWait, live, StringComparison.Ordinal);

            /* 2. Anchored at the incident, the SAME read returns the incident and nothing since. This is
                  the capability the issue says was unreachable. */
            var anchored = await DarlingMcpDataTools.GetWaitStats(postgres, ServerName, 4, 20, anchor);
            Assert.Contains(IncidentWait, anchored, StringComparison.Ordinal);
            Assert.DoesNotContain(RecentWait, anchored, StringComparison.Ordinal);

            /* 3. And widening hours_back is NOT the same question. A 48-hour window reaches the incident,
                  but it reaches everything since too — the aggregate now describes both, so the top-N and
                  the totals are answers to a question nobody asked. */
            var widened = await DarlingMcpDataTools.GetWaitStats(postgres, ServerName, 48);
            Assert.Contains(IncidentWait, widened, StringComparison.Ordinal);
            Assert.Contains(RecentWait, widened, StringComparison.Ordinal);
            Assert.NotEqual(WaitTypesIn(anchored).Count, WaitTypesIn(widened).Count);

            /* 4. The collection log — the read the gap was noticed on — moves with the anchor too, and its
                  two-branch miss still tells a quiet window from a server that never collected. */
            await PlantCollectionLogAsync(connection, incident, ct);
            var logAnchored = await DarlingMcpDataTools.GetCollectionLog(postgres, ServerName, 4, 200, anchor);
            Assert.Contains("wait_stats", logAnchored, StringComparison.Ordinal);
            Assert.Equal("empty", StatusOf(await DarlingMcpDataTools.GetCollectionLog(postgres, ServerName)));

            /* 5. Refusals reach the caller as the tool's own message, not as a silently-different answer. */
            var future = await DarlingMcpDataTools.GetWaitStats(postgres, ServerName, 4, 20, DateTime.UtcNow.AddDays(1).ToString("o"));
            Assert.Contains("future", future, StringComparison.Ordinal);
            Assert.StartsWith("Invalid as_of", await DarlingMcpDataTools.GetWaitStats(postgres, ServerName, 4, 20, "last tuesday"), StringComparison.Ordinal);

            /* 6. An anchor older than anything the store holds is the read's honest empty, not a refusal. */
            Assert.Equal(
                "unavailable",
                StatusOf(await DarlingMcpDataTools.GetWaitStats(postgres, ServerName, 4, 20, "1999-01-01T00:00:00Z")));

            bodySucceeded = true;
        }
        finally
        {
            await LiveStoreCleanup.RunAsync(cs!, bodySucceeded, async (cleanup, cleanupCt) =>
                await DeleteRowsAsync(cleanup, cleanupCt));
        }
    }

    private static System.Collections.Generic.List<string> WaitTypesIn(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("waits").EnumerateArray()
            .Select(w => w.GetProperty("wait_type").GetString()!)
            .ToList();
    }

    private static string StatusOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("status").GetString()!;
    }

    private static DateTime Truncate(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, DateTimeKind.Utc);

    private static DateTime Naive(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    private static async Task RegisterServerAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO servers (server_id, server_name, display_name, is_enabled, sql_major_version, created_date, modified_date)
VALUES ($1, $2, $3, TRUE, 15, $4, $4)
ON CONFLICT (server_id) DO UPDATE SET is_enabled = TRUE, sql_major_version = 15;", connection);
        command.Parameters.AddWithValue(ServerId);
        command.Parameters.AddWithValue(ServerName);
        command.Parameters.AddWithValue(ServerName);
        command.Parameters.AddWithValue(Naive(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task PlantWaitAsync(NpgsqlConnection connection, DateTime at, string waitType, long deltaMs, CancellationToken ct)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO wait_stats (collection_id, collection_time, server_id, server_name, wait_type, delta_wait_time_ms, delta_signal_wait_time_ms, delta_waiting_tasks)
VALUES ($1,$2,$3,$4,$5,$6,$7,$8)", connection);
        command.Parameters.AddWithValue(CollectionIdGenerator.Next());
        command.Parameters.AddWithValue(Naive(at));
        command.Parameters.AddWithValue(ServerId);
        command.Parameters.AddWithValue(ServerName);
        command.Parameters.AddWithValue(waitType);
        command.Parameters.AddWithValue(deltaMs);
        command.Parameters.AddWithValue(deltaMs / 10);
        command.Parameters.AddWithValue(50L);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task PlantCollectionLogAsync(NpgsqlConnection connection, DateTime at, CancellationToken ct)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO collection_log (log_id, server_id, server_name, collector_name, collection_time, duration_ms, status, error_message, rows_collected, sql_duration_ms, duckdb_duration_ms)
VALUES ($1, $2, $3, $4, $5, 120, 'SUCCESS', NULL, 7, 90, 30)", connection);
        command.Parameters.AddWithValue(CollectionIdGenerator.Next());
        command.Parameters.AddWithValue(ServerId);
        command.Parameters.AddWithValue(ServerName);
        command.Parameters.AddWithValue("wait_stats");
        command.Parameters.AddWithValue(Naive(at));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteRowsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var sql = string.Join(" ", new[] { "wait_stats", "collection_log" }
            .Select(table => $"DELETE FROM {table} WHERE server_id = {ServerId};"));
        sql += $" DELETE FROM servers WHERE server_id = {ServerId};";
        using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
