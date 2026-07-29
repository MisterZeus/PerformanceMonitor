using Installer.Core;

namespace Installer.Tests;

/// <summary>
/// Pins RepairOutcome, which both the Dashboard and the CLI depend on and which the CHANGELOG states as
/// a contract: a repair with a pending upgrade exits 0 even with failed files; a repair without one does
/// not. Getting it wrong is dangerous in BOTH directions -- reporting a good repair as a failure sends
/// the operator reaching for a destructive reinstall, and reporting a broken one as a success ships
/// genuinely uncompiled procedures past a CI gate.
/// </summary>
public class RepairOutcomeTests
{
    [Fact]
    public void PendingUpgrade_FailuresAreExpected()
    {
        // The install scripts cannot compile against a schema the pending migration has not touched yet.
        Assert.True(RepairOutcome.FailuresAreExpected(
            repairRan: true, installedVersion: "3.0.0", targetVersion: "3.1.0", criticalFileFailed: false));
    }

    [Fact]
    public void NoPendingUpgrade_FailuresAreReal()
    {
        // Nothing to blame: there is no migration-added column to be missing, so these are real errors.
        Assert.False(RepairOutcome.FailuresAreExpected(
            repairRan: true, installedVersion: "3.1.0", targetVersion: "3.1.0", criticalFileFailed: false));
    }

    [Fact]
    public void ServerNewerThanBinary_FailuresAreReal()
    {
        Assert.False(RepairOutcome.FailuresAreExpected(
            repairRan: true, installedVersion: "3.2.0", targetVersion: "3.1.0", criticalFileFailed: false));
    }

    [Fact]
    public void CriticalFileFailed_IsNeverExpected()
    {
        // A critical file aborts the whole pass, so the repair reinstalled nothing and really did fail --
        // regardless of whether an upgrade is pending.
        Assert.False(RepairOutcome.FailuresAreExpected(
            repairRan: true, installedVersion: "3.0.0", targetVersion: "3.1.0", criticalFileFailed: true));
    }

    [Fact]
    public void NotARepair_IsNeverExpected()
    {
        Assert.False(RepairOutcome.FailuresAreExpected(
            repairRan: false, installedVersion: "3.0.0", targetVersion: "3.1.0", criticalFileFailed: false));
    }

    [Theory]
    [InlineData(null, "3.1.0")]
    [InlineData("3.0.0", null)]
    [InlineData("Unreachable", "3.1.0")]
    [InlineData("3.0.0", "not-a-version")]
    public void UncomparableVersions_AreNeverExpected(string? installed, string? target)
    {
        // No comparable versions means no basis for the "expected failure" story.
        Assert.False(RepairOutcome.FailuresAreExpected(
            repairRan: true, installedVersion: installed, targetVersion: target, criticalFileFailed: false));
    }

    [Fact]
    public void UnknownVersionSentinel_IsNeverExpected()
    {
        /*
        The sentinel is GetInstalledVersionAsync's guess for "installed, but I cannot read the version" -- not
        a fact. It sorts below every real version, so trusting it would answer "an upgrade is pending"
        unconditionally and report every REAL repair failure as expected, exiting 0. A schema-current
        3.1.0 server whose history rows are all FAILED, with genuinely broken procedures, must not pass.
        */
        Assert.False(RepairOutcome.FailuresAreExpected(
            repairRan: true,
            installedVersion: InstallationService.UnknownVersionSentinel,
            targetVersion: "3.1.0",
            criticalFileFailed: false));
    }

    [Fact]
    public void UnknownSentinel_AnswersTheTwoQuestionsDIFFERENTLY()
    {
        /*
        The whole point of splitting them. For an unreadable version:
          - "may I excuse these file failures?"  -> NO. We cannot certify a success we cannot explain.
          - "is there an upgrade to run next?"   -> YES. Every hop may be pending.

        Answering both with one flag printed "already at the current version, so there is no upgrade to
        apply" for a server with eleven migrations waiting, and withheld the button that would have
        applied them.
        */
        const string sentinel = InstallationService.UnknownVersionSentinel;

        Assert.False(RepairOutcome.FailuresAreExpected(
            repairRan: true, installedVersion: sentinel, targetVersion: "3.1.0", criticalFileFailed: false));

        Assert.True(RepairOutcome.HasPendingUpgrade(sentinel, "3.1.0"));
        Assert.True(RepairOutcome.IsVersionUnknown(sentinel));
    }

    [Theory]
    [InlineData("3.1.0", "3.1.0", false)]
    [InlineData("3.0.0", "3.1.0", true)]
    [InlineData("3.2.0", "3.1.0", false)]
    [InlineData("Unreachable", "3.1.0", false)]
    [InlineData(null, "3.1.0", false)]
    public void HasPendingUpgrade_KeysOnlyOnVersionOrder(string? installed, string target, bool expected)
    {
        Assert.Equal(expected, RepairOutcome.HasPendingUpgrade(installed, target));
    }

    [Theory]
    [InlineData("3.1.0")]
    [InlineData("Unreachable")]
    [InlineData(null)]
    public void IsVersionUnknown_OnlyTheSentinel(string? installed)
    {
        Assert.False(RepairOutcome.IsVersionUnknown(installed));
    }

    [Theory]
    [InlineData("3.0.0", "3.1.0.0")]
    [InlineData("2.9", "3.1.0")]
    [InlineData("3.0.0", "3.1.0+abc123")]
    [InlineData("3.0.0-rc1", "3.1.0")]
    public void SemVerSuffixesAndPartLengths_StillCompare(string installed, string target)
    {
        Assert.True(RepairOutcome.FailuresAreExpected(
            repairRan: true, installedVersion: installed, targetVersion: target, criticalFileFailed: false));
    }
}
