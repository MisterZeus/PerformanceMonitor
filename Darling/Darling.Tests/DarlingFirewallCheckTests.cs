/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Linq;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Service.Mcp;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1771 — the service used to try to CREATE its own firewall rules from an unprivileged virtual account.
/// Every attempt failed "Access is denied" on a hardened box, and on a fresh networked install (the normal
/// deployment mode) the rule was never created at all, so remote clients were blocked outright.
///
/// <para>Rule management moved to the elevated install path; the runtime only VERIFIES. These pin both
/// halves: the read-only probe/verdict/remedy logic, the pure desired-state planner the elevated verb runs,
/// and — via source scans — the seams that would silently regress (a restored runtime write, a renamed rule
/// prefix that orphans rules on uninstall, an installer that stops calling the verb).</para>
/// </summary>
public class DarlingFirewallCheckTests
{
    /* ---- the probe: read-only, and shaped so "absent" is an ANSWER, not a failure ---- */

    [Fact]
    public void BuildProbeCommand_ReadsOnly_AndNeverWrites()
    {
        var command = DarlingFirewallCheck.BuildProbeCommand("PerformanceMonitor Darling MCP (port 5152)");

        Assert.Contains("Get-NetFirewallRule", command, StringComparison.Ordinal);
        Assert.Contains("-DisplayName 'PerformanceMonitor Darling MCP (port 5152)'", command, StringComparison.Ordinal);

        /* The whole point of #1771: the running service issues NO firewall write. */
        Assert.DoesNotContain("New-NetFirewallRule", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-NetFirewallRule", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-NetFirewallRule", command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProbeCommand_TreatsAnAbsentRuleAsAnAnswer_NotAFailure()
    {
        var command = DarlingFirewallCheck.BuildProbeCommand("PerformanceMonitor Darling Web (port 5153)");

        /* An exact DisplayName matching nothing is an ObjectNotFound ERROR from Get-NetFirewallRule (verified
           on Windows 11 26200), and a suppressed error still exits 1 — the same trap the disable builder
           documents. Without BOTH of these, every absent rule would read as a broken probe and warn. */
        Assert.Contains("-ErrorAction SilentlyContinue", command, StringComparison.Ordinal);
        Assert.Contains("exit 0", command, StringComparison.Ordinal);

        /* One DisplayName can match several rules (Windows stores one per profile), and a single match has no
           .Count without the array wrapper. */
        Assert.Contains("@(Get-NetFirewallRule", command, StringComparison.Ordinal);
        Assert.Contains(DarlingFirewallCheck.ProbeSentinel, command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rule'; whoami; '")]
    [InlineData("$(whoami)")]
    [InlineData("x'; New-NetFirewallRule -DisplayName 'pwn' -RemoteAddress Any; '")]
    public void BuildProbeCommand_KeepsAnyRuleNameInsideOneSingleQuotedLiteral(string hostile)
    {
        var command = DarlingFirewallCheck.BuildProbeCommand(hostile);

        /* Inside '…' PowerShell expands nothing, so the ONE way out is an unescaped quote: an odd total means
           the value broke out. Same property the write builders carry (#1646), enforced on the read path too. */
        Assert.True((command.Split('\'').Length - 1) % 2 == 0, $"unbalanced quoting: {command}");
        Assert.Contains(
            "-DisplayName '" + hostile.Replace("'", "''", StringComparison.Ordinal) + "'",
            command,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DarlingFirewallProbe=0", false)]
    [InlineData("DarlingFirewallProbe=1", true)]
    [InlineData("DarlingFirewallProbe=3", true)]
    [InlineData("noise before\r\nDarlingFirewallProbe=2\r\nnoise after", true)]
    public void TryParseProbeOutput_ReadsTheCount(string output, bool expected)
        => Assert.Equal(expected, DarlingFirewallCheck.TryParseProbeOutput(output));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Get-NetFirewallRule : The term is not recognized")]
    [InlineData("DarlingFirewallProbe=")]
    [InlineData("DarlingFirewallProbe=x")]
    public void TryParseProbeOutput_IsNullWhenItCannotTell(string? output)
    {
        /* Null must NOT collapse into "absent": that would manufacture a "rule is MISSING" warning out of a
           broken probe. It becomes ProbeFailed, which says exactly what happened. */
        Assert.Null(DarlingFirewallCheck.TryParseProbeOutput(output));
        Assert.Equal(
            FirewallRuleVerdict.ProbeFailed,
            DarlingFirewallCheck.Classify(exposed: true, DarlingFirewallCheck.TryParseProbeOutput(output)));
    }

    /* ---- the verdict table: the loopback-vs-networked gate ---- */

    [Theory]
    [InlineData(true, true, FirewallRuleVerdict.ExposedRulePresent)]
    [InlineData(true, false, FirewallRuleVerdict.ExposedRuleMissing)]
    [InlineData(false, true, FirewallRuleVerdict.LoopbackStaleRule)]
    [InlineData(false, false, FirewallRuleVerdict.LoopbackNoRule)]
    public void Classify_MapsExposureAndPresence(bool exposed, bool present, FirewallRuleVerdict expected)
        => Assert.Equal(expected, DarlingFirewallCheck.Classify(exposed, present));

    [Fact]
    public void Classify_LoopbackWithNoRule_IsTheSilentHealthyDefault()
    {
        /* The constraint from #1771: a loopback-only install needs no rule and must produce NO warning. The
           verdict itself carries that (it is the one with no remedy and no log line). */
        var verdict = DarlingFirewallCheck.Classify(exposed: false, present: false);

        Assert.Equal(FirewallRuleVerdict.LoopbackNoRule, verdict);
        Assert.Null(DarlingFirewallCheck.BuildRemedyCommand(verdict, "PerformanceMonitor Darling MCP (port 5152)", 5152, null));
    }

    /* ---- the remedy: a WARN that names the exact command ---- */

    [Fact]
    public void BuildRemedyCommand_ForAMissingRule_IsTheExactScopedOpenCommand()
    {
        var remedy = DarlingFirewallCheck.BuildRemedyCommand(
            FirewallRuleVerdict.ExposedRuleMissing, "PerformanceMonitor Darling MCP (port 5152)", 5152, "192.168.1.0/24");

        Assert.NotNull(remedy);
        Assert.Contains("New-NetFirewallRule", remedy, StringComparison.Ordinal);
        Assert.Contains("-LocalPort 5152", remedy, StringComparison.Ordinal);
        Assert.Contains("-RemoteAddress '192.168.1.0/24'", remedy, StringComparison.Ordinal);

        /* Built by the SAME builder the elevated verb runs, so the printed command and the applied one cannot
           drift into being different rules. */
        Assert.Equal(
            DarlingManagedPostgres.BuildFirewallEnableCommand("PerformanceMonitor Darling MCP (port 5152)", 5152, "192.168.1.0/24"),
            remedy);
    }

    [Fact]
    public void BuildRemedyCommand_ForAStaleRule_RemovesOnly()
    {
        var remedy = DarlingFirewallCheck.BuildRemedyCommand(
            FirewallRuleVerdict.LoopbackStaleRule, "PerformanceMonitor Darling Web (port 5153)", 5153, null);

        Assert.NotNull(remedy);
        Assert.Contains("Remove-NetFirewallRule", remedy, StringComparison.Ordinal);
        Assert.DoesNotContain("New-NetFirewallRule", remedy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FirewallRuleVerdict.ExposedRulePresent)]
    [InlineData(FirewallRuleVerdict.LoopbackNoRule)]
    [InlineData(FirewallRuleVerdict.ProbeFailed)]
    public void BuildRemedyCommand_IsNullWhenThereIsNothingToFix(FirewallRuleVerdict verdict)
        => Assert.Null(DarlingFirewallCheck.BuildRemedyCommand(verdict, "PerformanceMonitor Darling store (port 5641)", 5641, "10.0.0.0/8"));

    [Fact]
    public void BuildRemedyCommand_RefusesToBuildAnOpenCommandWithoutACidr()
    {
        /* An exposed-but-CIDR-less config is one the service fail-closes anyway. Emitting an open command with
           an empty -RemoteAddress would hand the operator a rule that opens the port to the world. */
        Assert.Null(DarlingFirewallCheck.BuildRemedyCommand(
            FirewallRuleVerdict.ExposedRuleMissing, "PerformanceMonitor Darling MCP (port 5152)", 5152, null));
        Assert.Null(DarlingFirewallCheck.BuildRemedyCommand(
            FirewallRuleVerdict.ExposedRuleMissing, "PerformanceMonitor Darling MCP (port 5152)", 5152, "   "));
    }

    /* ---- no retry spam: report once per state, not once per supervisor tick ---- */

    [Fact]
    public void ShouldReport_OnlyWhenTheStateChanges()
    {
        const string rule = "PerformanceMonitor Darling MCP (port 5152)";

        /* First observation always reports. */
        Assert.True(DarlingFirewallCheck.ShouldReport(null, null, rule, FirewallRuleVerdict.ExposedRuleMissing));

        /* The supervisor re-attempts a failed start every 30s; the same verdict must stay quiet. */
        Assert.False(DarlingFirewallCheck.ShouldReport(rule, FirewallRuleVerdict.ExposedRuleMissing, rule, FirewallRuleVerdict.ExposedRuleMissing));

        /* A real transition reports again — an admin created the rule. */
        Assert.True(DarlingFirewallCheck.ShouldReport(rule, FirewallRuleVerdict.ExposedRuleMissing, rule, FirewallRuleVerdict.ExposedRulePresent));

        /* A port change makes a DIFFERENT rule; the new rule's state has never been stated. */
        Assert.True(DarlingFirewallCheck.ShouldReport(
            rule, FirewallRuleVerdict.ExposedRulePresent, "PerformanceMonitor Darling MCP (port 6000)", FirewallRuleVerdict.ExposedRulePresent));
    }

    /* ---- the elevated planner: desired state per surface, from darling.json alone ---- */

    [Fact]
    public void PlanFirewallRules_LoopbackOnlyManagedConfig_OpensNothing()
    {
        var plans = DarlingCliCommands.PlanFirewallRules(ManagedConfig());

        Assert.All(plans, p => Assert.Equal(DarlingCliCommands.FirewallRuleAction.Remove, p.Action));

        /* Removal is idempotent, so the loopback default is a clean no-op that also sweeps up a rule left
           behind by an exposure that was turned off. */
        Assert.All(plans, p => Assert.Null(p.Cidr));
    }

    [Fact]
    public void PlanFirewallRules_ExposedMcp_OpensTheScopedRule()
    {
        var config = ManagedConfig();
        config.Mcp.Network = new McpNetworkConfig { Listen = "192.168.1.205", AllowFrom = "192.168.1.0/24", Token = "t" };

        var mcp = Assert.Single(PlansFor(config, "MCP"));

        Assert.Equal(DarlingCliCommands.FirewallRuleAction.Open, mcp.Action);
        Assert.Equal(DarlingMcpHostService.McpFirewallRuleName(config.Mcp.Port), mcp.RuleName);
        Assert.Equal(config.Mcp.Port, mcp.Port);
        Assert.Equal("192.168.1.0/24", mcp.Cidr);

        /* The other two surfaces are untouched by an MCP exposure. */
        Assert.Equal(DarlingCliCommands.FirewallRuleAction.Remove, Assert.Single(PlansFor(config, "web dashboard")).Action);
        Assert.Equal(DarlingCliCommands.FirewallRuleAction.Remove, Assert.Single(PlansFor(config, "store")).Action);
    }

    [Fact]
    public void PlanFirewallRules_UsesTheParsersCanonicalCidr_NotTheRawString()
    {
        /* IPNetwork.TryParse MASKS host bits rather than rejecting them, so "validate then use the original"
           would open a rule for a CIDR the parser had already decided meant something else. */
        var config = ManagedConfig();
        config.Mcp.Network = new McpNetworkConfig { Listen = "192.168.1.205", AllowFrom = "192.168.1.77/24", Token = "t" };

        Assert.Equal("192.168.1.0/24", Assert.Single(PlansFor(config, "MCP")).Cidr);
    }

    [Theory]
    [InlineData(null, "t")]              /* exposed listen, NO allowFrom  -> the service fail-closes to loopback */
    [InlineData("not-a-cidr", "t")]      /* exposed listen, junk allowFrom -> fail-closed                        */
    [InlineData("2001:db8::/32", "t")]   /* address family disagrees with an IPv4 listen -> fail-closed          */
    [InlineData("192.168.1.0/24", null)] /* no token -> the endpoint refuses to expose                           */
    public void PlanFirewallRules_NeverOpensAPortTheServiceWillNotExpose(string? allowFrom, string? token)
    {
        /* THE gate. The planner asks the SAME resolver the running service fail-closes on, so a config the
           service degrades to loopback gets no open port — and the service's own start-up check reaches the
           same verdict, instead of flagging a rule the installer had just created. */
        var config = ManagedConfig();
        config.Mcp.Network = new McpNetworkConfig { Listen = "192.168.1.205", AllowFrom = allowFrom, Token = token };

        var mcp = Assert.Single(PlansFor(config, "MCP"));
        Assert.Equal(DarlingCliCommands.FirewallRuleAction.Remove, mcp.Action);
        Assert.Null(mcp.Cidr);
        Assert.NotNull(mcp.Note); /* and it says WHY, rather than silently doing nothing */
    }

    [Fact]
    public void PlanFirewallRules_ByoMode_OpensNothingAndSkipsTheStore()
    {
        /* LAN exposure for MCP/web is managed-mode only, and in BYO the operator's own PostgreSQL governs the
           store's exposure — so the installer must not open ports for a store it does not run. */
        var config = ManagedConfig();
        config.Postgres!.Managed = false;
        config.Mcp.Network = new McpNetworkConfig { Listen = "192.168.1.205", AllowFrom = "192.168.1.0/24", Token = "t" };

        var all = DarlingCliCommands.PlanFirewallRules(config);

        Assert.DoesNotContain(all, p => p.Surface == "store");
        Assert.All(all, p => Assert.Equal(DarlingCliCommands.FirewallRuleAction.Remove, p.Action));
        Assert.NotNull(Assert.Single(PlansFor(config, "MCP")).Note);
    }

    [Fact]
    public void PlanFirewallRules_CoversAllThreeSurfaces_WithDistinctPortSpecificRules()
    {
        /* The issue named 5152/5153, but the bundled store had the identical structural bug on its own port. */
        var all = DarlingCliCommands.PlanFirewallRules(ManagedConfig());

        Assert.Equal(3, all.Count);
        Assert.Equal(3, all.Select(p => p.RuleName).Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, p => Assert.StartsWith(DarlingFirewallCheck.RuleNamePrefix, p.RuleName, StringComparison.Ordinal));
        Assert.All(all, p => Assert.Contains($"(port {p.Port})", p.RuleName, StringComparison.Ordinal));
    }

    /* ---- drift guards: the seams a rename or a deleted call would break silently ---- */

    [Fact]
    public void EveryRuleNameBuilder_CarriesTheSharedPrefix_ThatUninstallSweepsBy()
    {
        Assert.StartsWith(DarlingFirewallCheck.RuleNamePrefix, DarlingManagedPostgres.StoreFirewallRuleName(5641), StringComparison.Ordinal);
        Assert.StartsWith(DarlingFirewallCheck.RuleNamePrefix, DarlingMcpHostService.McpFirewallRuleName(5152), StringComparison.Ordinal);
        Assert.StartsWith(DarlingFirewallCheck.RuleNamePrefix, DarlingWebHostService.WebFirewallRuleName(5153), StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallScript_SweepsByTheSharedPrefix()
    {
        /* Uninstall removes rules by "<prefix>*" so a rule from a PREVIOUS port is cleared too. That literal
           lives in PowerShell, where no compiler checks it against the C# builders — this is that check. */
        var script = ReadRepoFile(Path.Combine("Darling", "tools", "uninstall-darling.ps1"));

        Assert.Contains($"Remove-NetFirewallRule -DisplayName '{DarlingFirewallCheck.RuleNamePrefix}*'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallScript_ActuallyInvokesTheFirewallVerb()
    {
        /* If the installer stops calling the verb, nothing creates the rules and #1771 is back — with the
           runtime no longer even attempting it either.
           This pins the INVOCATION, not a mention: the script also names --configure-firewall in its help
           block and in the "re-run this by hand" error message, so a `Contains("--configure-firewall")` stays
           green with the real call deleted. A mutation run caught exactly that, which is why the assertion is
           the exact invocation form plus proof the wrapper is called somewhere other than its own definition. */
        var script = ReadRepoFile(Path.Combine("Darling", "tools", "install-darling.ps1"));

        Assert.Contains("& $serviceExe --configure-firewall", script, StringComparison.Ordinal);

        var mentions = script.Split("Invoke-FirewallReconcile", StringSplitOptions.None).Length - 1;
        Assert.True(mentions >= 2, $"Invoke-FirewallReconcile is defined but never called (found {mentions} occurrence(s))");
    }

    [Theory]
    [InlineData("DarlingMcpHostService.cs")]
    [InlineData("DarlingWebHostService.cs")]
    public void RuntimeHosts_VerifyTheFirewall_AndNeverWriteIt(string fileName)
    {
        /* The regression this whole change exists to prevent: a host that goes back to issuing the write it
           cannot perform, producing access-denied on every start of every hardened install. */
        var source = ReadRepoFile(Path.Combine("Darling", "PerformanceMonitor.Darling.Service", "Mcp", fileName));

        Assert.Contains("DarlingFirewallCheck.CheckAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildFirewallEnableCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildFirewallDisableCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReconcileFirewallAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStoreBootstrap_VerifiesTheFirewall_AndNeverWritesItAtRuntime()
    {
        /* DarlingManagedPostgres still OWNS the pure command builders (the elevated verb calls them), so this
           pins the runtime CALL SITE rather than the file's vocabulary: the bootstrap checks, and the old
           "try to configure it myself" entry point is gone. */
        var source = ReadRepoFile(Path.Combine("Darling", "PerformanceMonitor.Darling.Service", "DarlingManagedPostgres.cs"));

        Assert.Contains("CheckStoreFirewallAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryConfigureStoreFirewallAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigureFirewallVerb_IsDispatchable()
    {
        /* A verb missing from the allow-list is classified UnknownOption and never reaches its handler — the
           installer would then get "Unknown option" and exit non-zero on every install. */
        Assert.True(DarlingCliCommands.IsConfigureFirewallVerb("--configure-firewall"));
        Assert.True(DarlingCliCommands.IsConfigureFirewallVerb("--CONFIGURE-FIREWALL"));
        Assert.True(DarlingCliCommands.IsKnownVerb("--configure-firewall"));
        Assert.Equal(StartupAction.RunKnownVerb, DarlingCliCommands.ClassifyStartupArgs(["--configure-firewall"]));
        Assert.Contains("--configure-firewall", DarlingCliCommands.UsageText(), StringComparison.Ordinal);
    }

    /* ---- helpers ---- */

    /// <summary>A managed, fully loopback-only config — the default install, and the baseline every exposure
    /// case above mutates one field of.</summary>
    private static DarlingConfig ManagedConfig()
    {
        var config = new DarlingConfig();
        config.Postgres!.Managed = true;
        return config;
    }

    private static System.Collections.Generic.IEnumerable<DarlingCliCommands.FirewallRulePlan> PlansFor(DarlingConfig config, string surface)
        => DarlingCliCommands.PlanFirewallRules(config).Where(p => p.Surface == surface);

    private static string ReadRepoFile(string relativePath)
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root, relativePath);
        Assert.True(File.Exists(path), $"expected {path} to exist");
        return File.ReadAllText(path);
    }

    /// <summary>Walks up from the test output directory to the repo root (the directory holding
    /// <c>PerformanceMonitor.sln</c>) — the same idiom <c>DocCommentHygieneTests</c> uses.</summary>
    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PerformanceMonitor.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
