/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Npgsql;
using PerformanceMonitor.Common;
using PerformanceMonitor.Darling.Service.Mcp;
using PerformanceMonitor.Darling.Storage;
using Xunit;

using Reader = PerformanceMonitor.Darling.Service.Mcp.DarlingAlertReader;

namespace Darling.Tests;

/// <summary>
/// Pins the alerts MCP slice — the three READS (get_alert_history, get_alert_settings, get_mute_rules) plus the
/// three Darling-only WRITES (update_alert_settings, create_mute_rule, delete_mute_rule) over the Postgres store.
/// Ungated: the tool surface is EXACTLY the six names (all static, on a [McpServerToolType] class, returning
/// Task&lt;string&gt;); each read param contract matches Lite's (plus the fleet-only optional server_name on
/// get_alert_history); the write tools require exactly their target (settings_json / rule_id); the read SQL is
/// Postgres-dialect + positional-param + excludes dismissed rows; the advertised tools/list schema is Gemini-clean
/// (#1074) with the expected required-param set; and update_alert_settings VALIDATES a partial update BEFORE any
/// write — a bad or unknown field returns {status:"invalid"} without ever opening a connection. The live
/// tune / CRUD round-trip (and the config_version self-bump) is gated below.
/// </summary>
public sealed class DarlingMcpAlertToolsSurfaceAndSqlTests
{
    /// <summary>A dead data source (unroutable port) — proves the validate-before-write path bails on a bad
    /// partial update WITHOUT ever opening a connection (the call returns before touching the store).</summary>
    private const string DeadStore = "Host=127.0.0.1;Port=1;Username=none;Password=none;Database=none;Timeout=1";

    private static readonly string[] AlertToolSurface =
    {
        "create_mute_rule",
        "delete_mute_rule",
        "get_alert_history",
        "get_alert_settings",
        "get_mute_rules",
        "update_alert_settings",
    };

    private static MethodInfo[] ToolMethods() => typeof(DarlingMcpAlertTools)
        .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
        .ToArray();

    [Fact]
    public void ToolSurface_ExactlyTheSixAlertTools()
    {
        var toolMethods = ToolMethods();
        var names = toolMethods
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AlertToolSurface, names);
        Assert.NotNull(typeof(DarlingMcpAlertTools).GetCustomAttribute<McpServerToolTypeAttribute>());
        Assert.All(toolMethods, m => Assert.True(m.IsStatic, $"{m.Name} must be static"));
        Assert.All(toolMethods, m => Assert.True(m.ReturnType == typeof(Task<string>), $"{m.Name} must return Task<string>"));
    }

    private static (string Name, bool Optional)[] McpParams(string toolName)
    {
        var method = ToolMethods().Single(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name == toolName);
        return method.GetParameters()
            .Where(p => p.GetCustomAttribute<DescriptionAttribute>() is not null)
            .Select(p => (p.Name!, p.HasDefaultValue))
            .ToArray();
    }

    [Theory]
    [InlineData("get_alert_history", "server_name,hours_back,limit")]
    [InlineData("get_mute_rules", "enabled_only")]
    [InlineData("update_alert_settings", "settings_json")]
    [InlineData("create_mute_rule", "server_name,metric_name,database_pattern,query_text_pattern,wait_type_pattern,job_name_pattern,reason,expires_at")]
    [InlineData("delete_mute_rule", "rule_id")]
    public void ParamContract_MatchesContract(string toolName, string expectedCsv)
    {
        Assert.Equal(expectedCsv.Split(','), McpParams(toolName).Select(p => p.Name).ToArray());
    }

    [Fact]
    public void ParamContract_AlertSettings_TakesNoInputParameters()
    {
        /* Only the injected NpgsqlDataSource, which is not [Description]-decorated — an empty input schema. */
        Assert.Empty(McpParams("get_alert_settings"));
    }

    [Fact]
    public void ParamContract_ReadsAndCreate_AreAllOptional()
    {
        /* The reads auto-select/omit their params; create_mute_rule's scope/pattern fields are ALL optional (a
           rule with no fields mutes everything). Only update_alert_settings + delete_mute_rule require input. */
        foreach (var tool in new[] { "get_alert_history", "get_mute_rules", "create_mute_rule" })
            Assert.All(McpParams(tool), p => Assert.True(p.Optional, $"{tool}.{p.Name} must be optional"));
    }

    [Theory]
    [InlineData("update_alert_settings", "settings_json")]
    [InlineData("delete_mute_rule", "rule_id")]
    public void ParamContract_WriteTools_RequireTheirTarget(string toolName, string requiredCsv)
    {
        var required = McpParams(toolName).Where(p => !p.Optional).Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(requiredCsv.Split(',').OrderBy(n => n, StringComparer.Ordinal).ToArray(), required);
    }

    /* ---------------- read SQL pins ---------------- */

    [Fact]
    public void AlertHistorySql_ReadsLog_ExcludesDismissed_ServerScoped()
    {
        var sql = Reader.AlertHistorySql;
        Assert.Contains("FROM config_alert_log", sql, StringComparison.Ordinal);
        Assert.Contains("dismissed = FALSE", sql, StringComparison.Ordinal);
        Assert.Contains("server_id = $2", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY alert_time DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $3", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AlertHistoryAllServersSql_ReadsLog_ExcludesDismissed_NoServerFilter()
    {
        var sql = Reader.AlertHistoryAllServersSql;
        Assert.Contains("FROM config_alert_log", sql, StringComparison.Ordinal);
        Assert.Contains("dismissed = FALSE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("server_id =", sql, StringComparison.Ordinal);   /* fleet-wide */
        Assert.Contains("LIMIT $2", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AlertSettingsSql_ReadsSingleGlobalRow()
    {
        var sql = Reader.AlertSettingsSelectSql;
        Assert.Contains("FROM config_alert_settings", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE id = 1", sql, StringComparison.Ordinal);
        Assert.Contains("cpu_threshold_percent", sql, StringComparison.Ordinal);
        Assert.Contains("delivery_mode", sql, StringComparison.Ordinal);
        Assert.Contains("notify_connection_changes", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(Reader.AlertHistorySql))]
    [InlineData(nameof(Reader.AlertHistoryAllServersSql))]
    [InlineData(nameof(Reader.AlertSettingsSelectSql))]
    public void Reads_ArePostgresDialect_NoTsqlIsms(string sqlName)
    {
        var sql = sqlName switch
        {
            nameof(Reader.AlertHistorySql) => Reader.AlertHistorySql,
            nameof(Reader.AlertHistoryAllServersSql) => Reader.AlertHistoryAllServersSql,
            _ => Reader.AlertSettingsSelectSql,
        };
        var lower = sql.ToLowerInvariant();
        Assert.DoesNotContain("getdate", lower);
        Assert.DoesNotContain("convert(", lower);
        Assert.DoesNotContain("top (", lower);
        Assert.DoesNotContain("isnull(", lower);
        Assert.DoesNotContain("N'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
    }

    /* ---------------- advertised MCP schema ---------------- */

    private static Dictionary<string, ModelContextProtocol.Protocol.Tool> BuildToolSchemas()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(NpgsqlDataSource), _ => null!);
        services.AddMcpServer().WithGeminiCompatibleTools<DarlingMcpAlertTools>();
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerTool>().ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool);
    }

    [Fact]
    public void AdvertisedSchema_IsGeminiClean_ForAllSixTools()
    {
        var tools = BuildToolSchemas();
        Assert.Equal(6, tools.Count);
        var violations = tools.Values.SelectMany(t => DarlingMcpSchemaAssert.Violations(t.Name, t.InputSchema)).ToList();
        Assert.True(violations.Count == 0, "Gemini-incompatible schema keywords leaked:\n" + string.Join("\n", violations));
    }

    [Theory]
    [InlineData("get_alert_history", "")]
    [InlineData("get_alert_settings", "")]
    [InlineData("get_mute_rules", "")]
    [InlineData("update_alert_settings", "settings_json")]
    [InlineData("create_mute_rule", "")]
    [InlineData("delete_mute_rule", "rule_id")]
    public void AdvertisedSchema_RequiredParams_MatchTheContract(string toolName, string expectedCsv)
    {
        var expected = expectedCsv.Length == 0 ? Array.Empty<string>() : expectedCsv.Split(',');
        var required = DarlingMcpSchemaAssert.RequiredOf(BuildToolSchemas()[toolName].InputSchema)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal).ToArray(), required);
    }

    /* ---------------- validate BEFORE write (no connection opened) ---------------- */

    [Theory]
    [InlineData("{\"cpu\":{\"threshold_percent\":900}}")]     // out of range (1-100)
    [InlineData("{\"cpu\":{\"threshold_percent\":0}}")]       // below min
    [InlineData("{\"cpu\":{\"mode\":\"bogus\"}}")]            // bad enum
    [InlineData("{\"delivery\":{\"mode\":\"Nope\"}}")]        // bad enum
    [InlineData("{\"cooldown_minutes\":0}")]                  // below min (1-120)
    [InlineData("{\"analysis\":{\"notify_severity\":9.9}}")]  // above max (0.0-2.0)
    [InlineData("{\"long_running_job\":{\"multiplier\":1}}")] // below min (2-20)
    [InlineData("{\"cpu\":{\"threshold_percent\":\"90\"}}")]  // wrong type (string, not int)
    [InlineData("{\"alerts_enabled\":\"yes\"}")]              // wrong type (string, not bool)
    [InlineData("{\"excluded_databases\":\"tempdb\"}")]       // wrong type (string, not array)
    [InlineData("{\"cpu\":{\"nonsense\":1}}")]                // unknown NESTED field
    [InlineData("{\"nonsense\":1}")]                          // unknown TOP-LEVEL field
    [InlineData("{\"cpu\":\"notanobject\"}")]                 // a group must be an object
    [InlineData("{}")]                                        // nothing to update
    [InlineData("not json")]                                  // not valid JSON
    [InlineData("[1,2,3]")]                                   // valid JSON but not an object
    public async Task UpdateAlertSettings_BadInput_ReturnsInvalid_WithoutTouchingTheStore(string settingsJson)
    {
        /* Validation runs BEFORE persistence, so every bad input returns 'invalid' without ever opening a
           connection (the dead store would throw if it were reached). */
        await using var dead = NpgsqlDataSource.Create(DeadStore);
        var result = await DarlingMcpAlertTools.UpdateAlertSettings(dead, settingsJson);
        Assert.Equal("invalid", DarlingMcpTestData.StatusOf(result));
    }

    [Fact]
    public async Task DeleteMuteRule_BlankId_ReturnsInvalid_WithoutTouchingTheStore()
    {
        await using var dead = NpgsqlDataSource.Create(DeadStore);
        var result = await DarlingMcpAlertTools.DeleteMuteRule(dead, "   ");
        Assert.Equal("invalid", DarlingMcpTestData.StatusOf(result));
    }

    [Fact]
    public async Task CreateMuteRule_BadExpiresAt_ReturnsInvalid_WithoutTouchingTheStore()
    {
        /* The only create_mute_rule input that can fail validation is a malformed expires_at — it is rejected
           before the store insert (the dead store would throw if reached). */
        await using var dead = NpgsqlDataSource.Create(DeadStore);
        var result = await DarlingMcpAlertTools.CreateMuteRule(dead, expires_at: "not-a-timestamp");
        Assert.Equal("invalid", DarlingMcpTestData.StatusOf(result));
    }
}

/// <summary>
/// Gated (DARLING_TEST_PG) live round-trips for the alert tools. The READ test plants an alert-log row, seeds the
/// single alert-settings row, and plants a mute rule, then asserts each read surfaces its data. The WRITE test
/// proves update_alert_settings flips a threshold AND self-bumps config_version (the reload beacon), and that
/// create_mute_rule → get_mute_rules → delete_mute_rule round-trips. Both connect as the DARLING_TEST_PG owner
/// (a THROWAWAY dev Postgres) and are own-scoped / restore what they touch, so a shared store is left as it was.
/// </summary>
[Collection("live-postgres")]
public sealed class DarlingMcpAlertToolsLivePostgresTests
{
    private const string ServerName = "darling-mcp-alerts-e2e";
    private static readonly int ServerId = ServerIdHelper.GetDeterministicHashCode(ServerName);
    private const string MuteRuleId = "darling-mcp-alerts-e2e-rule";
    private static string? ConnectionString => Environment.GetEnvironmentVariable("DARLING_TEST_PG");

    [Fact]
    public async Task AlertTools_ReadPlantedRows_AgainstDevPostgres()
    {
        var cs = ConnectionString;
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "Set DARLING_TEST_PG to a Postgres connection string to run the live alert-tools test.");

        var ct = TestContext.Current.CancellationToken;
        using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);
        await DeleteRowsAsync(connection, ct);
        await using var postgres = NpgsqlDataSource.Create(cs!);

        var bodySucceeded = false;
        try
        {
            await DarlingMcpTestData.RegisterServerAsync(connection, ServerId, ServerName, ct);
            var when = DarlingMcpTestData.TruncateToSeconds(DateTime.UtcNow).AddMinutes(-5);

            await DarlingMcpTestData.ExecAsync(connection, ct,
                @"INSERT INTO config_alert_log (alert_time, server_id, server_name, metric_name, current_value, threshold_value, alert_sent, notification_type, send_error, muted, detail_text)
VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)",
                when, ServerId, ServerName, "High CPU", 92.5, 80.0, true, "email", null, false, "CPU sustained above threshold");

            /* Seed the single global settings row — every column has a default, so id alone suffices. */
            await DarlingMcpTestData.ExecAsync(connection, ct,
                "INSERT INTO config_alert_settings (id) VALUES (1) ON CONFLICT (id) DO NOTHING");

            await DarlingMcpTestData.ExecAsync(connection, ct,
                @"INSERT INTO config_mute_rules (id, enabled, created_at_utc, expires_at_utc, reason, server_name, metric_name, database_pattern, query_text_pattern, wait_type_pattern, job_name_pattern)
VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)",
                MuteRuleId, true, DarlingMcpTestData.Naive(DateTime.UtcNow), null, "e2e rule", ServerName, "High CPU", null, null, null, null);

            /* Alert history — server-scoped + fleet-wide both surface the planted alert. */
            var scoped = await DarlingMcpAlertTools.GetAlertHistory(postgres, ServerName);
            DarlingMcpTestData.AssertEnvelope(scoped, ServerName, "alerts");
            Assert.Contains("High CPU", scoped, StringComparison.Ordinal);

            var fleet = await DarlingMcpAlertTools.GetAlertHistory(postgres);
            Assert.False(fleet.StartsWith("Error during", StringComparison.Ordinal), fleet);
            Assert.Contains("(all servers)", fleet, StringComparison.Ordinal);
            Assert.Contains("High CPU", fleet, StringComparison.Ordinal);

            /* Alert settings — the seeded row round-trips its default thresholds. */
            var settings = await DarlingMcpAlertTools.GetAlertSettings(postgres);
            Assert.False(settings.StartsWith("Error during", StringComparison.Ordinal), settings);
            Assert.Contains("threshold_percent", settings, StringComparison.Ordinal);
            Assert.Contains("delivery", settings, StringComparison.Ordinal);

            /* Mute rules — the planted rule surfaces. */
            var mutes = await DarlingMcpAlertTools.GetMuteRules(postgres);
            Assert.False(mutes.StartsWith("Error during", StringComparison.Ordinal), mutes);
            Assert.Contains(MuteRuleId, mutes, StringComparison.Ordinal);

            bodySucceeded = true;
        }
        finally
        {
            await LiveStoreCleanup.RunAsync(cs!, bodySucceeded, async (cleanup, cleanupCt) =>
                await DeleteRowsAsync(cleanup, cleanupCt));
        }
    }

    [Fact]
    public async Task AlertWriteTools_TuneSettings_AndMuteRuleRoundTrip_AgainstDevPostgres()
    {
        var cs = ConnectionString;
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "Set DARLING_TEST_PG to a Postgres connection string to run the live alert-write-tools test.");

        var ct = TestContext.Current.CancellationToken;
        using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);
        await using var postgres = NpgsqlDataSource.Create(cs!);

        /* Seed the two singleton rows the writes touch (a no-op if they already exist on a shared store). */
        await DarlingMcpTestData.ExecAsync(connection, ct, "INSERT INTO config_service (id) VALUES (1) ON CONFLICT (id) DO NOTHING");
        await DarlingMcpTestData.ExecAsync(connection, ct, "INSERT INTO config_alert_settings (id) VALUES (1) ON CONFLICT (id) DO NOTHING");

        var originalThreshold = Convert.ToInt32(await ScalarAsync(connection, ct, "SELECT cpu_threshold_percent FROM config_alert_settings WHERE id = 1"));
        var versionBefore = Convert.ToInt64(await ScalarAsync(connection, ct, "SELECT config_version FROM config_service WHERE id = 1"));
        var newThreshold = originalThreshold == 91 ? 71 : 91;                 // a distinct, in-range value
        var muteTag = "mcp_alert_write_e2e_" + Guid.NewGuid().ToString("N");  // own-scoped cleanup tag

        var bodySucceeded = false;
        try
        {
            /* update_alert_settings — a PARTIAL update flips ONE threshold; the response echoes the full new settings. */
            var updated = await DarlingMcpAlertTools.UpdateAlertSettings(postgres, $"{{\"cpu\":{{\"threshold_percent\":{newThreshold}}}}}");
            Assert.Equal("updated", DarlingMcpTestData.StatusOf(updated));
            using (var doc = JsonDocument.Parse(updated))
            {
                Assert.Equal(newThreshold, doc.RootElement.GetProperty("settings").GetProperty("cpu").GetProperty("threshold_percent").GetInt32());
                Assert.Contains("cpu_threshold_percent", doc.RootElement.GetProperty("updated_fields").EnumerateArray().Select(e => e.GetString()));
            }

            /* The store row actually changed, and the config-table trigger self-bumped config_version (the service's
               reload beacon) — so the running service hot-reloads the change within one sweep. */
            Assert.Equal(newThreshold, Convert.ToInt32(await ScalarAsync(connection, ct, "SELECT cpu_threshold_percent FROM config_alert_settings WHERE id = 1")));
            var versionAfter = Convert.ToInt64(await ScalarAsync(connection, ct, "SELECT config_version FROM config_service WHERE id = 1"));
            Assert.True(versionAfter > versionBefore, "config_version should self-bump on a config_alert_settings write");

            /* An unknown field writes NOTHING (validated before the UPDATE). */
            Assert.Equal("invalid", DarlingMcpTestData.StatusOf(await DarlingMcpAlertTools.UpdateAlertSettings(postgres, "{\"cpu\":{\"bogus\":1}}")));

            /* create_mute_rule → get_mute_rules → delete_mute_rule round-trip (own-scoped by the GUID reason tag). */
            var created = await DarlingMcpAlertTools.CreateMuteRule(postgres, server_name: "e2e-write-server", metric_name: "High CPU", reason: muteTag);
            Assert.Equal("created", DarlingMcpTestData.StatusOf(created));
            string ruleId;
            using (var doc = JsonDocument.Parse(created))
            {
                var rule = doc.RootElement.GetProperty("mute_rule");
                ruleId = rule.GetProperty("id").GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(ruleId));
                Assert.Equal("High CPU", rule.GetProperty("metric_name").GetString());
            }

            Assert.Contains(ruleId, await DarlingMcpAlertTools.GetMuteRules(postgres), StringComparison.Ordinal);

            Assert.Equal("deleted", DarlingMcpTestData.StatusOf(await DarlingMcpAlertTools.DeleteMuteRule(postgres, ruleId)));
            Assert.DoesNotContain(ruleId, await DarlingMcpAlertTools.GetMuteRules(postgres), StringComparison.Ordinal);
            Assert.Equal("not_found", DarlingMcpTestData.StatusOf(await DarlingMcpAlertTools.DeleteMuteRule(postgres, ruleId)));

            bodySucceeded = true;
        }
        finally
        {
            /* Restore the singleton threshold and drop any leftover test mute rule (own-scoped by the GUID reason).

               The RESTORE is why this teardown matters more than most (#1902): config_alert_settings is a
               SINGLETON the whole store shares, so abandoning this leaves every later test — and every later
               run on a reused database — reading a CPU threshold this test invented. */
            await LiveStoreCleanup.RunAsync(cs!, bodySucceeded, async (cleanup, cleanupCt) =>
            {
                await DarlingMcpTestData.ExecAsync(cleanup, cleanupCt, "UPDATE config_alert_settings SET cpu_threshold_percent = $1 WHERE id = 1", originalThreshold);
                await DarlingMcpTestData.ExecAsync(cleanup, cleanupCt, "DELETE FROM config_mute_rules WHERE reason = $1", muteTag);
            });
        }
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, CancellationToken ct, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(ct);
    }

    private static async Task DeleteRowsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var sql = $"DELETE FROM config_alert_log WHERE server_id = {ServerId};"
            + $" DELETE FROM config_mute_rules WHERE id = '{MuteRuleId}';"
            + $" DELETE FROM servers WHERE server_id = {ServerId};";
        using var cleanup = new NpgsqlCommand(sql, connection);
        await cleanup.ExecuteNonQueryAsync(ct);
    }
}
