using Installer.Core;

namespace Installer.Tests;

/// <summary>
/// Pins InstalledVersionClassifier — the decision that separates "nothing is installed, do a clean
/// install" from "something is installed, work out which migrations still need to run".
///
/// It exists as a pure function precisely so these can run in CI. The SQL that feeds it needs a live
/// SQL Server, and that suite (VersionDetectionTests) is excluded from the CI filter — so before this
/// split, the single most consequential branch in the installer had no test that could fail a PR.
/// </summary>
public class InstalledVersionClassifierTests
{
    [Fact]
    public void NoDatabase_IsACleanInstall()
    {
        Assert.Null(InstalledVersionClassifier.Classify(
            databaseExists: false, historyTableExists: false, collectTableCount: 0, latestSuccessVersion: null));
    }

    [Fact]
    public void EmptyPreCreatedDatabase_IsACleanInstall()
    {
        /*
        Someone created the database themselves (commonly to control the data/log file paths) and then ran
        the installer. Nothing is installed. Answering "installed" here would attempt migrations against
        tables that do not exist, and every one of them would fail.
        */
        Assert.Null(InstalledVersionClassifier.Classify(
            databaseExists: true, historyTableExists: false, collectTableCount: 0, latestSuccessVersion: null));
    }

    [Fact]
    public void LedgerLostButCollectObjectsPresent_IsUnknown_NotAFreshInstall()
    {
        /*
        THE STRANDING BUG. A PerformanceMonitor database whose config.installation_history was dropped.
        Answering "clean install" made the install scripts run over the LIVE schema, skip every migration,
        and stamp the target version SUCCESS -- and since version detection reads back the most recent
        SUCCESS row, those hops were never offered again. Permanently stranded, with no failed migration
        anywhere in sight.
        */
        Assert.Equal(
            InstallationService.UnknownVersionSentinel,
            InstalledVersionClassifier.Classify(
                databaseExists: true, historyTableExists: false, collectTableCount: 62, latestSuccessVersion: null));
    }

    [Fact]
    public void LedgerPresentWithSuccessRow_IsThatVersion()
    {
        Assert.Equal(
            "3.0.0",
            InstalledVersionClassifier.Classify(
                databaseExists: true, historyTableExists: true, collectTableCount: 62, latestSuccessVersion: "3.0.0"));
    }

    [Fact]
    public void LedgerPresentButNoSuccessRow_IsUnknown_Regression538()
    {
        /*
        #538: a prior install that never wrote a SUCCESS row. Treating it as a fresh install would drop
        the database. "Unknown" means attempt every upgrade, which is safe -- every upgrade script is
        IF NOT EXISTS-guarded and replays cleanly.
        */
        Assert.Equal(
            InstallationService.UnknownVersionSentinel,
            InstalledVersionClassifier.Classify(
                databaseExists: true, historyTableExists: true, collectTableCount: 62, latestSuccessVersion: null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void LedgerPresentButVersionIsBlank_IsUnknown_NotAnAnswer(string blank)
    {
        /*
        installer_version is NOT NULL, so a row hand-edited to '' comes back as "" rather than null -- and
        the "unreadable version" message we show an operator invites exactly that edit. "" is not a version:
        FilterUpgrades resolves it to ZERO applicable hops, which reads as "already current" and strands
        every pending migration, silently and permanently. Blank is absence of an answer, not an answer.

        InstallGuard rejects "" on every path today, so this is the second lock on the same door. The ledger
        is where a second lock earns its keep.
        */
        Assert.Equal(
            InstallationService.UnknownVersionSentinel,
            InstalledVersionClassifier.Classify(
                databaseExists: true, historyTableExists: true, collectTableCount: 62, latestSuccessVersion: blank));
    }

    [Fact]
    public void TheSentinelSortsBelowEveryRealVersion_SoEveryHopIsOffered()
    {
        /* Its whole job. If it ever stopped sorting below a real version, hops would be skipped silently. */
        var sentinel = ScriptProvider.TryParseVersionCore(InstallationService.UnknownVersionSentinel);

        Assert.NotNull(sentinel);
        Assert.True(sentinel < ScriptProvider.TryParseVersionCore("1.2.0"));
        Assert.True(sentinel < ScriptProvider.TryParseVersionCore("3.1.0"));
    }

    [Fact]
    public void TheSentinelIsNotARealShippedVersion()
    {
        /*
        It used to be "1.0.0" -- a shipped tag. The "your recorded version is unreadable" message tells
        operators to correct the SUCCESS row by hand, and "1.0.0" is exactly what someone would type to
        force a full re-run; they would then be told their version could not be read.
        */
        Assert.Equal("0.0.0", InstallationService.UnknownVersionSentinel);
    }
}
