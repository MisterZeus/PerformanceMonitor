/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PerformanceMonitor.Analysis;
using PerformanceMonitorLite.Analysis;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Mcp;
using PerformanceMonitorLite.Models;
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// Pins the read-only MCP copy-paste remediation command on Lite's get_analysis_findings: the tool
/// must return <c>remediation_command</c> — the SAME text the viewer cards render via the shared
/// <see cref="FactRemediation.RenderCopyPasteCommand"/> — for a finding whose persisted
/// <see cref="RemediationAction"/> was hydrated from <c>remediation_action_json</c> (the drill-down
/// is GONE on this read path, which is exactly why the older drill-down-sourced
/// <c>suggested_remediation_sql</c> stays omitted here). A DESTRUCTIVE shape (RCSI) proves the
/// two-sided risk-disclosure comment header rides along inside the command; a non-remediable finding
/// proves the field is null. The tool stays STRICTLY read-only — it renders advisory text and never
/// executes anything. The renderer's per-shape output is pinned separately by
/// <c>LiteRecommendationsReaderTests</c>; this pins the MCP ENVELOPE carrying it, mirroring the
/// Darling gated e2e assertion in <c>DarlingMcpToolsTests</c>.
/// </summary>
public sealed class McpAnalysisFindingsCommandTests : IClassFixture<SharedDuckDbFixture>, IDisposable
{
    private readonly string _tempDir;
    private readonly DuckDbInitializer _duckDb;
    private readonly ServerManager _serverManager;
    private readonly int _serverId;

    public McpAnalysisFindingsCommandTests(SharedDuckDbFixture fixture)
    {
        fixture.ResetData();
        _duckDb = fixture.DuckDb;

        /* The temp dir stays test-local for the ServerManager's config directory;
           only the database is shared through the class fixture. */
        _tempDir = Path.Combine(Path.GetTempPath(), "McpRemediationTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        /* A real ServerManager with one enabled, Windows-auth server — AddServer never touches the
           credential store, so no DPAPI / OS keychain side effects (the McpStatusEnvelopeTests pattern). */
        _serverManager = new ServerManager(configDir);
        var server = new ServerConnection { ServerName = "TestServer", DisplayName = "TestServer" };
        _serverManager.AddServer(server);

        /* The server_id the tool resolves to — the SAME derivation ServerResolver uses. */
        _serverId = RemoteCollectorService.GetDeterministicHashCode(
            RemoteCollectorService.GetServerNameForStorage(server));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task GetAnalysisFindings_EmitsRemediationCommand_FromPersistedAction_WithRiskHeader_AndNullWhenNone()
    {
        var store = new FindingStore(_duckDb);
        var analysisTime = DateTime.UtcNow;
        var context = new AnalysisContext
        {
            ServerId = _serverId,
            ServerName = "TestServer",
            TimeRangeStart = analysisTime.AddHours(-4),
            TimeRangeEnd = analysisTime
        };

        /* A DESTRUCTIVE RCSI finding: the persisted DB_CONFIG action carries a per-database
           RcsiTarget, so the shared renderer prepends the two-sided disclosure header above the
           enabling ALTER — proving the risk disclosure rides along through the MCP envelope. */
        var destructive = MakeFinding(
            findingId: 900001, analysisTime, severity: 2.5,
            rootFactKey: "DB_CONFIG", storyPathHash: "reco_rcsi_hash",
            remediation: new RemediationAction(
                "DB_CONFIG", "set", Array.Empty<ForcePlanTarget>(),
                RcsiTargets: new[] { new RcsiTarget("StackOverflow", new RcsiInactionFigures(50, 3, 70)) }));

        /* A non-remediable finding: no persisted action, so remediation_command must be null. */
        var nonRemediable = MakeFinding(
            findingId: 900002, analysisTime, severity: 1.0,
            rootFactKey: "SOS_SCHEDULER_YIELD", storyPathHash: "reco_none_hash",
            remediation: null);

        await store.InsertFindingsAsync(
            new List<AnalysisFinding> { destructive, nonRemediable }, context);

        var json = await McpAnalysisTools.GetAnalysisFindings(
            new AnalysisService(_duckDb), _serverManager, "TestServer", 24);

        using var doc = JsonDocument.Parse(json);
        var findings = doc.RootElement.GetProperty("findings").EnumerateArray().ToList();
        Assert.Equal(2, findings.Count);

        /* The field is present on EVERY finding (JsonOptions does not ignore nulls). */
        Assert.All(findings, f => Assert.True(f.TryGetProperty("remediation_command", out _),
            "every finding must expose remediation_command"));

        /* Destructive finding: the full command carries the two-sided risk-disclosure header,
           then the enabling ALTER — byte-identical to the shared renderer the viewer cards use. */
        var rcsi = findings.Single(f => f.GetProperty("story_path_hash").GetString() == "reco_rcsi_hash");
        var command = rcsi.GetProperty("remediation_command").GetString();
        Assert.False(string.IsNullOrEmpty(command));
        Assert.StartsWith("/*", command, StringComparison.Ordinal);
        Assert.Contains("Risks of MAKING this change:", command!, StringComparison.Ordinal);
        Assert.Contains("Risks of NOT making this change:", command!, StringComparison.Ordinal);
        Assert.Contains("ALTER DATABASE [StackOverflow] SET READ_COMMITTED_SNAPSHOT ON;", command!, StringComparison.Ordinal);
        Assert.True(
            command!.IndexOf("*/", StringComparison.Ordinal) <
            command.IndexOf("ALTER DATABASE", StringComparison.Ordinal),
            "the disclosure comment header must precede the ALTER statement");
        Assert.Equal(FactRemediation.RenderCopyPasteCommand(destructive.Remediation), command);

        /* Non-remediable finding: the field is present but null (no command). */
        var none = findings.Single(f => f.GetProperty("story_path_hash").GetString() == "reco_none_hash");
        Assert.Equal(JsonValueKind.Null, none.GetProperty("remediation_command").ValueKind);
    }

    private AnalysisFinding MakeFinding(
        long findingId, DateTime analysisTime, double severity,
        string rootFactKey, string storyPathHash, RemediationAction? remediation) =>
        new AnalysisFinding
        {
            FindingId = findingId,
            AnalysisTime = analysisTime,
            ServerId = _serverId,
            ServerName = "TestServer",
            TimeRangeStart = analysisTime.AddHours(-4),
            TimeRangeEnd = analysisTime,
            Severity = severity,
            Confidence = 0.9,
            Category = "config",
            StoryPath = rootFactKey,
            StoryPathHash = storyPathHash,
            StoryText = "planted finding",
            RootFactKey = rootFactKey,
            RootFactValue = 1.0,
            FactCount = 1,
            IncidentId = "mcp-remediation-test",
            Remediation = remediation
        };
}
