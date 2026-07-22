namespace Installer.Core;

/// <summary>
/// The pure decisions of the add/edit-server flow, extracted out of the (untested) WPF dialog so CI can
/// pin them. Every method here is a total function of <see cref="ServerSetupState"/> (plus, for
/// <see cref="DeriveConnectedState"/>, two version strings) with no UI dependency — which is exactly what
/// lets <c>ServerSetupPolicyTests</c> assert the full state×predicate matrix.
///
/// The bug class this closes: a predicate over the state that silently omits a state. It happened more than
/// once — <see cref="IsServerVerdictState"/> listing three states while <see cref="PromisesExistingInstall"/>
/// listed five and the two disagreeing about the same server; the destructive-checkbox rule missing an exit.
/// A switch that forgets a state compiles fine and fails only in the UI; a matrix test over the enum does not.
/// </summary>
public static class ServerSetupPolicy
{
    /// <summary>
    /// Does this state assert a discovered fact ABOUT the server currently on screen — its installed
    /// version, whether it needs an upgrade, whether it is current? Those verdicts must not be rendered
    /// once the server-name box no longer matches the server they were read from. This is the set the
    /// stale-verdict guard demotes.
    /// </summary>
    public static bool IsServerVerdictState(ServerSetupState state) =>
        state is ServerSetupState.Connected_NoDatabase
              or ServerSetupState.Connected_NeedsUpgrade
              or ServerSetupState.Connected_Current;

    /// <summary>
    /// Did the button the user actually clicked promise to act on an EXISTING installation? Only
    /// <see cref="ServerSetupState.Connected_NoDatabase"/> says "Install Now"; every other launchable state
    /// promises otherwise — including <see cref="ServerSetupState.InstallComplete"/> and
    /// <see cref="ServerSetupState.MonitoringCredentials"/>, where the repair handoff deliberately puts a
    /// live "Upgrade Now" in front of the user. Those two are NOT verdict states, so they are the pair
    /// <see cref="IsServerVerdictState"/> and this predicate legitimately disagree on — a real bug came from
    /// making them agree.
    /// </summary>
    public static bool PromisesExistingInstall(ServerSetupState launchState) => launchState switch
    {
        ServerSetupState.Connected_NeedsUpgrade => true,      // "Upgrade Now"
        ServerSetupState.Connected_Current => true,           // "Reinstall Objects"
        ServerSetupState.InstallComplete => true,             // repair handoff: "Upgrade Now"
        ServerSetupState.MonitoringCredentials => true,       // repair handoff, SQL auth: "Upgrade Now"
        _ => false,                                           // Connected_NoDatabase: "Install Now"; and the non-launchable states
    };

    /// <summary>
    /// The ONE state that consumes the destructive checkboxes (clean install → DROP DATABASE, repair, reset
    /// schedule → TRUNCATE): <see cref="ServerSetupState.Installing"/>, which is about to read them. Every
    /// other transition must clear them, because consent for a destructive option is per-run and per-server
    /// and a tick must never survive onto a server it was not set for. Keeping this as a single predicate —
    /// rather than clearing the ticks at each exit — is what stops the next exit from being the one that
    /// forgets. It reopened as a bug more than once when it was enforced exit-by-exit.
    /// </summary>
    public static bool StateConsumesModeSelections(ServerSetupState state) =>
        state == ServerSetupState.Installing;

    /// <summary>True only while a run is in progress. The backstop against starting a second install.</summary>
    public static bool IsInstalling(ServerSetupState state) =>
        state == ServerSetupState.Installing;

    /// <summary>
    /// Which connected state the FACTS imply. Used by detection and — crucially — re-derived from the
    /// install-time re-read before the install begins, never restored from "the state we came from" (the
    /// box stays editable, so the state we came from may describe a different server).
    ///
    /// <paramref name="installedVersion"/> null means no database (fresh install). Otherwise the choice is
    /// pending-upgrade vs current, decided by <see cref="RepairOutcome.HasPendingUpgrade"/> so the unknown
    /// sentinel (which sorts below every real version) correctly reads as "an upgrade is pending".
    /// </summary>
    public static ServerSetupState DeriveConnectedState(string? installedVersion, string appVersion)
    {
        if (installedVersion == null)
        {
            return ServerSetupState.Connected_NoDatabase;
        }

        return RepairOutcome.HasPendingUpgrade(installedVersion, appVersion)
            ? ServerSetupState.Connected_NeedsUpgrade
            : ServerSetupState.Connected_Current;
    }
}
