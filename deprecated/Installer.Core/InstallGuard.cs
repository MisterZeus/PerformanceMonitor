namespace Installer.Core;

/// <summary>Why installing against a server would be unsafe.</summary>
public enum InstallBlock
{
    /// <summary>Safe to install.</summary>
    None,

    /// <summary>
    /// This binary's own version will not parse. It is what a FRESH install writes to
    /// <c>installation_history.installer_version</c>, so letting it through poisons a brand-new server's
    /// ledger at birth — after which every surface refuses to touch that server again.
    /// </summary>
    UnreadableBuildVersion,

    /// <summary>
    /// The version recorded on the server will not parse, so we cannot tell which migrations still apply.
    /// </summary>
    UnreadableInstalledVersion,

    /// <summary>
    /// The server is on a NEWER build than this binary. Installing would run our older scripts over it —
    /// reverting every <c>CREATE OR ALTER</c> procedure and view to their older definitions — and then
    /// record the LOWER version as SUCCESS. A silent downgrade.
    /// </summary>
    InstalledIsNewerThanBuild,
}

/// <summary>
/// The decision behind both installers' pre-install blocks.
///
/// Two of these cases are invisible to every other guard: they produce ZERO upgrade hops AND ZERO
/// failures, so the migration-failure abort never fires and nothing downstream notices. That is what
/// makes them dangerous, and why the decision lives here — shared and pinned — rather than hand-copied
/// into a WPF code-behind and a CLI Main.
/// </summary>
public static class InstallGuard
{
    /// <param name="installedVersion">The version recorded on the server; null when nothing is installed.</param>
    /// <param name="buildVersion">The version of the binary about to run.</param>
    public static InstallBlock Check(string? installedVersion, string? buildVersion)
    {
        var build = ScriptProvider.TryParseVersionCore(buildVersion);

        /*
        Checked first, and independently of whether anything is installed: a fresh install WRITES this
        value, so an unreadable one is fatal even on an empty server.
        */
        if (build == null)
        {
            return InstallBlock.UnreadableBuildVersion;
        }

        if (installedVersion == null)
        {
            /* Nothing installed: a fresh install is safe. */
            return InstallBlock.None;
        }

        var installed = ScriptProvider.TryParseVersionCore(installedVersion);

        if (installed == null)
        {
            return InstallBlock.UnreadableInstalledVersion;
        }

        if (installed > build)
        {
            return InstallBlock.InstalledIsNewerThanBuild;
        }

        return InstallBlock.None;
    }
}
