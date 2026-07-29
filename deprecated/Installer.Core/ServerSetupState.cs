using System.Diagnostics.CodeAnalysis;

namespace Installer.Core;

/// <summary>
/// The state of the "add / edit a monitored server" flow: connect to a server, discover whether
/// PerformanceMonitor is installed on it, and install or upgrade. The Dashboard's AddServerDialog aliases
/// this as <c>DialogState</c> and drives its UI from it.
///
/// This enum and the predicates over it (<see cref="ServerSetupPolicy"/>) live here, in Installer.Core,
/// for one reason: they are pure decisions, and Installer.Core is CI-tested while the WPF dialog is not.
/// Every bug in this flow that survived review came in one of two shapes, and one of them was "a predicate
/// over the state that failed to enumerate every state" — a class the matrix tests over this enum make
/// impossible to reintroduce silently.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores",
    Justification = "Connected_* is a deliberate, readable grouping convention for this flow's states; it " +
                    "carried over verbatim from the dialog's original private enum and reads better than " +
                    "the un-underscored form.")]
public enum ServerSetupState
{
    /// <summary>Nothing checked yet — a fresh dialog, or one whose fields were just edited.</summary>
    Initial,

    /// <summary>Connected; no PerformanceMonitor database present. The offer is "Install Now" (or Skip).</summary>
    Connected_NoDatabase,

    /// <summary>Connected; PerformanceMonitor installed but behind this build. The offer is "Upgrade Now".</summary>
    Connected_NeedsUpgrade,

    /// <summary>Connected; PerformanceMonitor installed and current. The offer is "Reinstall Objects".</summary>
    Connected_Current,

    /// <summary>
    /// Connected, but the installed status could not be trusted — a failed re-read, an unsupported or
    /// unreadable version, a database newer than the binary, or a verdict demoted because the server-name
    /// box changed after it was checked. Installing is blocked; the install button is hidden.
    /// </summary>
    Connected_StatusUnknown,

    /// <summary>An install or upgrade is running. The ONLY state that consumes the destructive checkboxes.</summary>
    Installing,

    /// <summary>A run finished. May carry a repair handoff ("Upgrade Now") for the still-pending upgrade.</summary>
    InstallComplete,

    /// <summary>Post-install, SQL auth: capturing the ongoing monitoring credentials. May also carry the handoff.</summary>
    MonitoringCredentials
}
