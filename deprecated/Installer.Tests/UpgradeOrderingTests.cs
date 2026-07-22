using System.Text.RegularExpressions;
using Installer.Core;
using Installer.Core.Models;
using Installer.Tests.Helpers;

namespace Installer.Tests;

/// <summary>
/// Tests the upgrade folder discovery and ordering logic.
/// Uses temp directories to simulate various upgrade folder configurations.
/// </summary>
public class UpgradeOrderingTests
{
    [Fact]
    public void ReturnsCorrectUpgradesForVersionRange()
    {
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("1.3.0", "2.0.0", "01_schema.sql")
            .WithUpgrade("2.0.0", "2.1.0", "01_columns.sql")
            .WithUpgrade("2.1.0", "2.2.0", "01_compress.sql");

        var upgrades = ScriptProvider.FromDirectory(dir.RootPath).GetApplicableUpgrades("1.3.0", "2.2.0");

        Assert.Equal(3, upgrades.Count);
        Assert.Equal("1.3.0-to-2.0.0", upgrades[0].FolderName);
        Assert.Equal("2.0.0-to-2.1.0", upgrades[1].FolderName);
        Assert.Equal("2.1.0-to-2.2.0", upgrades[2].FolderName);
    }

    [Fact]
    public void SkipsAlreadyAppliedUpgrades()
    {
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("1.3.0", "2.0.0", "01_schema.sql")
            .WithUpgrade("2.0.0", "2.1.0", "01_columns.sql")
            .WithUpgrade("2.1.0", "2.2.0", "01_compress.sql");

        var upgrades = ScriptProvider.FromDirectory(dir.RootPath).GetApplicableUpgrades("2.0.0", "2.2.0");

        Assert.Equal(2, upgrades.Count);
        Assert.Equal("2.0.0-to-2.1.0", upgrades[0].FolderName);
        Assert.Equal("2.1.0-to-2.2.0", upgrades[1].FolderName);
    }

    [Fact]
    public void AlreadyAtTargetVersion_ReturnsEmpty()
    {
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("2.0.0", "2.1.0", "01_columns.sql")
            .WithUpgrade("2.1.0", "2.2.0", "01_compress.sql");

        var upgrades = ScriptProvider.FromDirectory(dir.RootPath).GetApplicableUpgrades("2.2.0", "2.2.0");

        Assert.Empty(upgrades);
    }

    [Fact]
    public void FourPartVersion_NormalizedToThreePart()
    {
        // The installer normalizes 4-part "2.2.0.0" (from DB) to 3-part "2.2.0" (folder names)
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("2.1.0", "2.2.0", "01_compress.sql");

        var upgrades = ScriptProvider.FromDirectory(dir.RootPath).GetApplicableUpgrades("2.1.0.0", "2.2.0");

        Assert.Single(upgrades);
        Assert.Equal("2.1.0-to-2.2.0", upgrades[0].FolderName);
    }

    [Fact]
    public void MalformedFolderNames_Skipped()
    {
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("2.0.0", "2.1.0", "01_columns.sql")
            .WithMalformedUpgradeFolder("not-a-version")
            .WithMalformedUpgradeFolder("foo-to-bar");

        var upgrades = ScriptProvider.FromDirectory(dir.RootPath).GetApplicableUpgrades("2.0.0", "2.2.0");

        Assert.Single(upgrades);
        Assert.Equal("2.0.0-to-2.1.0", upgrades[0].FolderName);
    }

    [Fact]
    public void MissingUpgradeTxt_FolderSkipped()
    {
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("2.0.0", "2.1.0", "01_columns.sql")
            .WithUpgradeNoManifest("2.1.0", "2.2.0");

        var upgrades = ScriptProvider.FromDirectory(dir.RootPath).GetApplicableUpgrades("2.0.0", "2.2.0");

        Assert.Single(upgrades);
        Assert.Equal("2.0.0-to-2.1.0", upgrades[0].FolderName);
    }

    [Fact]
    public void NoUpgradesFolder_ReturnsEmpty()
    {
        using var dir = new TempDirectoryBuilder();
        // Don't create any upgrade folders

        var upgrades = ScriptProvider.FromDirectory(dir.RootPath).GetApplicableUpgrades("2.0.0", "2.2.0");

        Assert.Empty(upgrades);
    }

    [Fact]
    public void NullCurrentVersion_ReturnsEmpty()
    {
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("2.0.0", "2.1.0", "01_columns.sql");

        var upgrades = ScriptProvider.FromDirectory(dir.RootPath).GetApplicableUpgrades(null, "2.2.0");

        Assert.Empty(upgrades);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankCurrentVersion_ReturnsEmpty(string currentVersion)
    {
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("2.0.0", "2.1.0", "01_columns.sql");

        var upgrades = ScriptProvider.FromDirectory(dir.RootPath).GetApplicableUpgrades(currentVersion, "2.2.0");

        Assert.Empty(upgrades);
    }

    [Theory]
    [InlineData("Unreachable")]
    [InlineData("Not installed")]
    [InlineData("2.x")]
    public void UnparseableCurrentVersion_Throws(string currentVersion)
    {
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("2.0.0", "2.1.0", "01_columns.sql");

        var provider = ScriptProvider.FromDirectory(dir.RootPath);

        // An empty result means "no upgrades needed". If a status string silently produced one,
        // every migration would be skipped and the caller would still stamp the DB as current.
        var ex = Assert.Throws<ArgumentException>(
            () => provider.GetApplicableUpgrades(currentVersion, "2.2.0"));

        Assert.Equal("currentVersion", ex.ParamName);
    }

    [Theory]
    [InlineData("2.2.0-rc1")]
    [InlineData("2.2.0+abc123")]
    [InlineData("2.2.0-rc1+abc123")]
    public void SemVerSuffixOnTargetVersion_IsStripped(string targetVersion)
    {
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("2.0.0", "2.1.0", "01_columns.sql")
            .WithUpgrade("2.1.0", "2.2.0", "01_compress.sql");

        // A pre-release <InformationalVersion> must not make every upgrade abort. Without the
        // suffix strip, "2.2.0-rc1" fails to parse and throws.
        var upgrades = ScriptProvider.FromDirectory(dir.RootPath).GetApplicableUpgrades("2.0.0", targetVersion);

        Assert.Equal(2, upgrades.Count);
    }

    [Fact]
    public void TwoPartVersion_IsTreatedAsThreePart()
    {
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("2.0.0", "2.1.0", "01_columns.sql")
            .WithUpgrade("2.1.0", "2.2.0", "01_compress.sql");

        // Version.TryParse("2.0") leaves Build == -1, which the Version(int,int,int) ctor rejects.
        var upgrades = ScriptProvider.FromDirectory(dir.RootPath).GetApplicableUpgrades("2.0", "2.2");

        Assert.Equal(2, upgrades.Count);
    }

    [Fact]
    public void UnparseableTargetVersion_Throws()
    {
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("2.0.0", "2.1.0", "01_columns.sql");

        var provider = ScriptProvider.FromDirectory(dir.RootPath);

        var ex = Assert.Throws<ArgumentException>(
            () => provider.GetApplicableUpgrades("2.0.0", "not-a-version"));

        Assert.Equal("targetVersion", ex.ParamName);
    }

    [Theory]
    [InlineData("3.1.0", 3, 1, 0)]
    [InlineData("3.1.0.4", 3, 1, 0)]
    [InlineData("3.1", 3, 1, 0)]
    [InlineData("3.2.0-rc1", 3, 2, 0)]
    [InlineData("3.2.0+abc123", 3, 2, 0)]
    [InlineData("3.2.0-rc1+abc123", 3, 2, 0)]
    [InlineData("  3.2.0  ", 3, 2, 0)]
    public void TryParseVersionCore_ParsesRealVersions(string input, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), ScriptProvider.TryParseVersionCore(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unreachable")]
    [InlineData("Not installed")]
    [InlineData("v1.2.3")]
    [InlineData("-rc1")]
    [InlineData("+sha")]
    public void TryParseVersionCore_ReturnsNullRatherThanThrowing(string? input)
    {
        // Callers that need to DECIDE something about a version (is it newer than me? is it
        // readable?) must get null, not an exception -- unlike FilterUpgrades, which throws.
        Assert.Null(ScriptProvider.TryParseVersionCore(input));
    }

    [Fact]
    public void TryParseVersionCore_OrdersNewerAboveOlder()
    {
        // This comparison is what stops an older installer from running over a newer database and
        // recording the lower version as SUCCESS -- a silent downgrade that produces zero hops and
        // zero failures, so nothing else catches it.
        var installed = ScriptProvider.TryParseVersionCore("3.2.0");
        var binary = ScriptProvider.TryParseVersionCore("3.1.0.0");

        Assert.True(installed > binary);
    }

    [Fact]
    public async Task ExecuteAllUpgrades_UnreadableVersion_ReportsFailureRatherThanSkipping()
    {
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("2.0.0", "2.1.0", "01_columns.sql");

        // Callers abort when totalFailureCount > 0. Returning (0, 0, 0) here would read as
        // "nothing to upgrade", and the caller would go on to stamp the database as current at
        // the target version -- stranding every skipped hop. The connection string is never used:
        // discovery fails before any database work.
        var (successCount, failureCount, upgradeCount) = await InstallationService.ExecuteAllUpgradesAsync(
            ScriptProvider.FromDirectory(dir.RootPath),
            "Server=unused;Database=unused;",
            "Unreachable",
            "2.2.0",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, successCount);
        Assert.Equal(1, failureCount);
        Assert.Equal(0, upgradeCount);
    }

    [Fact]
    public void OrderedByFromVersion()
    {
        // Create folders in reverse order to verify sorting
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("2.1.0", "2.2.0", "01_c.sql")
            .WithUpgrade("1.3.0", "2.0.0", "01_a.sql")
            .WithUpgrade("2.0.0", "2.1.0", "01_b.sql");

        var upgrades = ScriptProvider.FromDirectory(dir.RootPath).GetApplicableUpgrades("1.3.0", "2.2.0");

        Assert.Equal(3, upgrades.Count);
        Assert.Equal(new Version(1, 3, 0), upgrades[0].FromVersion);
        Assert.Equal(new Version(2, 0, 0), upgrades[1].FromVersion);
        Assert.Equal(new Version(2, 1, 0), upgrades[2].FromVersion);
    }

    [Fact]
    public void DoesNotIncludeFutureUpgrades()
    {
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("2.0.0", "2.1.0", "01_a.sql")
            .WithUpgrade("2.1.0", "2.2.0", "01_b.sql")
            .WithUpgrade("2.2.0", "2.3.0", "01_c.sql");

        // Target is 2.2.0, so 2.2.0-to-2.3.0 should NOT be included
        var upgrades = ScriptProvider.FromDirectory(dir.RootPath).GetApplicableUpgrades("2.0.0", "2.2.0");

        Assert.Equal(2, upgrades.Count);
        Assert.DoesNotContain(upgrades, u => u.FolderName == "2.2.0-to-2.3.0");
    }

    [Fact]
    public void PatchVersion_GetsUpgradeFromPriorMinor()
    {
        // Regression test for #817: user on v2.4.1 should still get the
        // 2.4.0-to-2.5.0 upgrade applied (patch version within range)
        using var dir = new TempDirectoryBuilder()
            .WithUpgrade("2.3.0", "2.4.0", "01_a.sql")
            .WithUpgrade("2.4.0", "2.5.0", "01_b.sql")
            .WithUpgrade("2.5.0", "2.6.0", "01_c.sql");

        var upgrades = ScriptProvider.FromDirectory(dir.RootPath).GetApplicableUpgrades("2.4.1", "2.6.0");

        Assert.Equal(2, upgrades.Count);
        Assert.Equal("2.4.0-to-2.5.0", upgrades[0].FolderName);
        Assert.Equal("2.5.0-to-2.6.0", upgrades[1].FolderName);
    }

    [Fact]
    public void EmbeddedResources_FindsUpgradeFolders()
    {
        // Regression test for #772: MSBuild mangles embedded resource names
        // (e.g., "2.2.0-to-2.3.0" → "_2._2._0_to_2._3._0"), which broke
        // upgrade discovery when using Split('.')[0].
        var provider = ScriptProvider.FromEmbeddedResources();
        var upgrades = provider.GetApplicableUpgrades("2.2.0", "2.5.0");

        Assert.NotEmpty(upgrades);
        Assert.Contains(upgrades, u => u.FolderName == "2.2.0-to-2.3.0");
    }

    [Fact]
    public void EmbeddedUpgrades_AllDiscoveredFoldersHaveReadableManifestAndScripts()
    {
        // Self-maintaining guard for the bug class behind #772 (broken embedded-resource
        // discovery) and the 3.1.0 prep blocker (an upgrade folder shipped with no upgrade.txt,
        // which ScriptProvider silently skips). Every embedded upgrade folder the Dashboard
        // could apply must expose a readable manifest whose listed scripts all exist and read.
        var provider = ScriptProvider.FromEmbeddedResources();
        var warnings = new List<string>();

        var upgrades = provider.GetApplicableUpgrades("0.0.1", "999.0.0", warnings.Add);

        Assert.NotEmpty(upgrades);
        Assert.Empty(warnings); // nothing skipped for a missing upgrade.txt

        foreach (var upgrade in upgrades)
        {
            var manifest = provider.GetUpgradeManifest(upgrade);
            Assert.NotEmpty(manifest);

            foreach (var scriptName in manifest)
            {
                Assert.True(
                    provider.UpgradeScriptExists(upgrade, scriptName),
                    $"{upgrade.FolderName}/{scriptName} is listed in upgrade.txt but is not embedded");
                Assert.False(
                    string.IsNullOrWhiteSpace(provider.ReadUpgradeScript(upgrade, scriptName)),
                    $"{upgrade.FolderName}/{scriptName} is embedded but empty");
            }
        }
    }

    [Fact]
    public void EmbeddedUpgrades_3_0_0_To_3_1_0_DiscoverableWithManifestAndScript()
    {
        // Exercises THIS release's upgrade through the embedded-resource path the Dashboard
        // uses (a distinct code path from the CLI's filesystem provider — #772 only hit
        // embedded resources). Pins the 3.0.0 -> 3.1.0 hop the 3.1.0 prep had to repair: the
        // folder shipped without the upgrade.txt that lists its one script.
        var provider = ScriptProvider.FromEmbeddedResources();
        var warnings = new List<string>();

        var upgrades = provider.GetApplicableUpgrades("3.0.0", "3.1.0", warnings.Add);

        Assert.Contains(upgrades, u => u.FolderName == "3.0.0-to-3.1.0");
        Assert.DoesNotContain(warnings, w => w.Contains("3.0.0-to-3.1.0"));

        foreach (var upgrade in upgrades)
        {
            if (upgrade.FolderName != "3.0.0-to-3.1.0")
                continue;

            var manifest = provider.GetUpgradeManifest(upgrade);
            Assert.Contains("01_blocking_chain_monitor_loop.sql", manifest);
            Assert.True(provider.UpgradeScriptExists(upgrade, "01_blocking_chain_monitor_loop.sql"));

            var script = provider.ReadUpgradeScript(upgrade, "01_blocking_chain_monitor_loop.sql");
            Assert.Contains("USE PerformanceMonitor", script);
            Assert.Contains("ALTER TABLE collect.blocking_BlockedProcessReport", script);
            Assert.Contains("blocking_ecid", script);
            Assert.Contains("monitor_loop", script);
        }
    }

    [Theory]
    [InlineData("_2._2._0_to_2._3._0.upgrade.txt", "_2._2._0_to_2._3._0")]
    [InlineData("_2._2._0_to_2._3._0.03_add_growth_vlf_columns.sql", "_2._2._0_to_2._3._0")]
    [InlineData("_10._1._0_to_10._2._0.01_schema.sql", "_10._1._0_to_10._2._0")]
    public void EmbeddedUpgradeFolderPattern_ExtractsMangledName(string resourceSuffix, string expectedMangled)
    {
        var match = Patterns.EmbeddedUpgradeFolderPattern().Match(resourceSuffix);

        Assert.True(match.Success);
        Assert.Equal(expectedMangled, match.Groups[1].Value);
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("readme.txt")]
    [InlineData("README.md")]
    public void EmbeddedUpgradeFolderPattern_RejectsNonVersionStrings(string input)
    {
        Assert.False(Patterns.EmbeddedUpgradeFolderPattern().Match(input).Success);
    }
}
