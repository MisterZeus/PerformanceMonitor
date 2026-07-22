using Installer.Core;

namespace Installer.Tests;

/// <summary>
/// Pins InstallGuard, the decision behind both installers' pre-install blocks.
///
/// Both of the worst defects found while building this feature lived here, and BOTH are invisible to
/// every other guard: a version we cannot compare, or a server NEWER than this build, produces ZERO
/// upgrade hops and ZERO failures -- so the migration-failure abort never fires and nothing downstream
/// notices. The install then runs this binary's older scripts over the newer database, reverting every
/// CREATE OR ALTER procedure and view, and records the LOWER version as SUCCESS. Version detection reads
/// back the most recent SUCCESS row, so that silently strands every migration in between.
///
/// Lives in Installer.Tests, not Dashboard.Tests, because Dashboard.Tests is not wired into CI -- tests
/// that cannot fail a PR are decoration.
/// </summary>
public class InstallGuardTests
{
    [Fact]
    public void NoDatabase_IsAllowed()
    {
        /* Nothing installed: a fresh install is safe. */
        Assert.Equal(InstallBlock.None, InstallGuard.Check(null, "3.1.0"));
    }

    [Theory]
    [InlineData("3.1.0", "3.1.0")]
    [InlineData("3.0.0", "3.1.0")]
    [InlineData("3.1.0.0", "3.1.0")]
    [InlineData("2.9", "3.1.0")]
    [InlineData("3.0.0", "3.1.0+abc123")]
    [InlineData("3.0.0", "3.2.0-rc1")]
    public void SameOrOlderThanThisBuild_IsAllowed(string installed, string build)
    {
        Assert.Equal(InstallBlock.None, InstallGuard.Check(installed, build));
    }

    [Theory]
    [InlineData("3.2.0", "3.1.0")]
    [InlineData("3.1.1", "3.1.0")]
    [InlineData("4.0.0", "3.1.0.0")]
    [InlineData("3.2.0", "3.1.0+abc123")]
    public void NewerThanThisBuild_IsBlocked(string installed, string build)
    {
        /* The silent downgrade: older scripts over a newer schema, then the LOWER version recorded. */
        Assert.Equal(InstallBlock.InstalledIsNewerThanBuild, InstallGuard.Check(installed, build));
    }

    [Theory]
    [InlineData("Unreachable")]
    [InlineData("Not installed")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnreadableInstalledVersion_IsBlocked(string installed)
    {
        Assert.Equal(InstallBlock.UnreadableInstalledVersion, InstallGuard.Check(installed, "3.1.0"));
    }

    [Fact]
    public void UnreadableBuildVersion_IsBlocked_AndBlamesTheBuild()
    {
        /* Nothing the user does to the database fixes a malformed InformationalVersion. */
        Assert.Equal(InstallBlock.UnreadableBuildVersion, InstallGuard.Check("3.0.0", "not-a-version"));
    }

    [Fact]
    public void UnreadableBuildVersion_IsBlocked_EvenOnAFreshServer()
    {
        /*
        Regression: the build-version check used to sit BELOW the no-database early return, so a fresh
        server sailed through -- and the build version is exactly what a fresh install writes to
        installation_history.installer_version. That poisoned the ledger at birth, after which every
        surface refuses to touch the server ever again.
        */
        Assert.Equal(InstallBlock.UnreadableBuildVersion, InstallGuard.Check(null, "not-a-version"));
    }
}
