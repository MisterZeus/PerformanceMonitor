using Installer.Core;

namespace Installer.Tests;

/// <summary>
/// The state×predicate matrix for the add/edit-server flow. This is the test that pays for extracting
/// ServerSetupState / ServerSetupPolicy out of the (un-CI-tested) WPF dialog: every predicate is asserted
/// for EVERY state, so a predicate that silently omits a state — the exact bug that recurred in the dialog
/// (IsServerVerdictState listing three states while PromisesExistingInstall listed five; the destructive-
/// checkbox rule missing an exit) — now fails a test instead of only misbehaving in the UI.
///
/// Each predicate has one [InlineData] per enum member, and a guard test asserts the enum still has exactly
/// the eight members these tables were written against, so adding a ninth forces this file to be updated.
/// </summary>
public class ServerSetupPolicyTests
{
    [Fact]
    public void TheEnumHasExactlyTheEightStatesTheseTablesCover()
    {
        // If this fails, a state was added or removed: extend every [Theory] below to cover it, on purpose.
        Assert.Equal(8, Enum.GetValues<ServerSetupState>().Length);
    }

    // ---- IsServerVerdictState: states that assert a discovered fact about the server on screen ----

    [Theory]
    [InlineData(ServerSetupState.Connected_NoDatabase, true)]
    [InlineData(ServerSetupState.Connected_NeedsUpgrade, true)]
    [InlineData(ServerSetupState.Connected_Current, true)]
    [InlineData(ServerSetupState.Initial, false)]
    [InlineData(ServerSetupState.Connected_StatusUnknown, false)]   // where a demotion LANDS -- not a verdict
    [InlineData(ServerSetupState.Installing, false)]
    [InlineData(ServerSetupState.InstallComplete, false)]
    [InlineData(ServerSetupState.MonitoringCredentials, false)]
    public void IsServerVerdictState_IsExactlyTheThreeConnectedVerdicts(ServerSetupState state, bool expected)
    {
        Assert.Equal(expected, ServerSetupPolicy.IsServerVerdictState(state));
    }

    // ---- PromisesExistingInstall: did the launched button act on an EXISTING install? ----

    [Theory]
    [InlineData(ServerSetupState.Connected_NeedsUpgrade, true)]     // "Upgrade Now"
    [InlineData(ServerSetupState.Connected_Current, true)]          // "Reinstall Objects"
    [InlineData(ServerSetupState.InstallComplete, true)]            // repair handoff: "Upgrade Now"
    [InlineData(ServerSetupState.MonitoringCredentials, true)]      // repair handoff, SQL auth: "Upgrade Now"
    [InlineData(ServerSetupState.Connected_NoDatabase, false)]      // "Install Now"
    [InlineData(ServerSetupState.Initial, false)]
    [InlineData(ServerSetupState.Connected_StatusUnknown, false)]
    [InlineData(ServerSetupState.Installing, false)]
    public void PromisesExistingInstall_IncludesTheTwoRepairHandoffStates(ServerSetupState state, bool expected)
    {
        Assert.Equal(expected, ServerSetupPolicy.PromisesExistingInstall(state));
    }

    // ---- StateConsumesModeSelections: the ONLY state allowed to keep the destructive checkboxes ----

    [Theory]
    [InlineData(ServerSetupState.Installing, true)]
    [InlineData(ServerSetupState.Initial, false)]
    [InlineData(ServerSetupState.Connected_NoDatabase, false)]
    [InlineData(ServerSetupState.Connected_NeedsUpgrade, false)]
    [InlineData(ServerSetupState.Connected_Current, false)]
    [InlineData(ServerSetupState.Connected_StatusUnknown, false)]
    [InlineData(ServerSetupState.InstallComplete, false)]
    [InlineData(ServerSetupState.MonitoringCredentials, false)]
    public void StateConsumesModeSelections_IsInstallingAndNothingElse(ServerSetupState state, bool expected)
    {
        // Every state but Installing must clear clean-install (DROP DATABASE) / repair / reset-schedule
        // (TRUNCATE). A regression here is how a live destructive tick survived onto a server that never
        // consented -- the bug that reopened repeatedly when the rule was enforced exit-by-exit.
        Assert.Equal(expected, ServerSetupPolicy.StateConsumesModeSelections(state));
    }

    [Fact]
    public void IsInstalling_MatchesTheInstallingState()
    {
        foreach (var state in Enum.GetValues<ServerSetupState>())
        {
            Assert.Equal(state == ServerSetupState.Installing, ServerSetupPolicy.IsInstalling(state));
        }
    }

    // ---- The relationships between the predicates, pinned because getting them "consistent" was a bug ----

    [Fact]
    public void RepairHandoffStates_PromiseAnExistingInstall_ButAreNotVerdictStates()
    {
        // The deliberate disagreement. InstallComplete / MonitoringCredentials carry a live "Upgrade Now"
        // (so they promise an existing install) but assert no fresh verdict about the box (so they are not
        // verdict states). A round of review "fixed" this by making them agree and reintroduced a bug.
        foreach (var handoff in new[] { ServerSetupState.InstallComplete, ServerSetupState.MonitoringCredentials })
        {
            Assert.True(ServerSetupPolicy.PromisesExistingInstall(handoff));
            Assert.False(ServerSetupPolicy.IsServerVerdictState(handoff));
        }
    }

    [Fact]
    public void Installing_IsNeitherAVerdictNorAPromiseState()
    {
        // It consumes the ticks; it must not also be treated as a server verdict or a launched promise.
        Assert.True(ServerSetupPolicy.StateConsumesModeSelections(ServerSetupState.Installing));
        Assert.False(ServerSetupPolicy.IsServerVerdictState(ServerSetupState.Installing));
        Assert.False(ServerSetupPolicy.PromisesExistingInstall(ServerSetupState.Installing));
    }

    // ---- DeriveConnectedState: which connected state the FACTS imply ----

    [Fact]
    public void DeriveConnectedState_NullVersion_IsNoDatabase()
    {
        Assert.Equal(
            ServerSetupState.Connected_NoDatabase,
            ServerSetupPolicy.DeriveConnectedState(installedVersion: null, appVersion: "3.1.0"));
    }

    [Fact]
    public void DeriveConnectedState_OlderVersion_NeedsUpgrade()
    {
        Assert.Equal(
            ServerSetupState.Connected_NeedsUpgrade,
            ServerSetupPolicy.DeriveConnectedState("3.0.0", "3.1.0"));
    }

    [Fact]
    public void DeriveConnectedState_SameVersion_IsCurrent()
    {
        Assert.Equal(
            ServerSetupState.Connected_Current,
            ServerSetupPolicy.DeriveConnectedState("3.1.0", "3.1.0"));
    }

    [Fact]
    public void DeriveConnectedState_UnknownSentinel_NeedsUpgrade()
    {
        // The sentinel sorts below every real version, so "unknown installed version" correctly reads as
        // "an upgrade is pending" -- attempt every hop, the safe direction.
        Assert.Equal(
            ServerSetupState.Connected_NeedsUpgrade,
            ServerSetupPolicy.DeriveConnectedState(InstallationService.UnknownVersionSentinel, "3.1.0"));
    }

    [Fact]
    public void DeriveConnectedState_NewerThanBinary_IsCurrent_BecauseBlockingItIsInstallGuardsJob()
    {
        // Not "NeedsUpgrade" -- there is no hop to a lower version. Classifying a newer-than-binary database
        // as Current is correct HERE; refusing to install over it is a separate decision (InstallGuard),
        // made before this state is ever rendered. DeriveConnectedState only answers "is an upgrade pending".
        Assert.Equal(
            ServerSetupState.Connected_Current,
            ServerSetupPolicy.DeriveConnectedState("3.2.0", "3.1.0"));
    }
}
