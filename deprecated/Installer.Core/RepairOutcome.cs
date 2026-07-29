namespace Installer.Core;

/// <summary>
/// Decides whether a repair's failed install files are the EXPECTED kind.
///
/// A repair runs the install scripts without the migrations. Those scripts compile against the CURRENT
/// schema, and <c>ALTER PROCEDURE</c> binds columns at compile time -- e.g.
/// <c>install/23_process_blocked_process_xml.sql</c> reads
/// <c>collect.blocking_BlockedProcessReport.monitor_loop</c>, a column the 3.0.0-to-3.1.0 migration adds.
/// So on a database with a PENDING upgrade, some procedures simply cannot compile until it runs
/// (Msg 207, "Invalid column name"). A failed <c>CREATE OR ALTER</c> leaves the old body intact, nothing
/// is damaged, and the upgrade's own install pass recompiles them.
///
/// Both conditions matter, and getting either wrong is dangerous in a different direction:
///
/// <list type="bullet">
/// <item>Treating those failures as a FAILURE sends the operator reaching for a destructive reinstall,
/// and makes a %ERRORLEVEL% gate reject a good repair.</item>
/// <item>Treating them as EXPECTED when there is NO pending upgrade reports SUCCESS over genuinely
/// broken objects, and blames a migration that does not exist.</item>
/// </list>
///
/// Shared so the Dashboard and the CLI cannot drift: this is the contract the CHANGELOG states.
/// </summary>
public static class RepairOutcome
{
    /// <summary>
    /// True when a repair's install-file failures are the expected uncompilable-until-upgraded kind, and
    /// the run should therefore be reported as a success with a "now run the upgrade" handoff.
    /// </summary>
    /// <param name="repairRan">A repair actually ran against an existing installation.</param>
    /// <param name="installedVersion">The version recorded on the server.</param>
    /// <param name="targetVersion">This binary's version.</param>
    /// <param name="criticalFileFailed">
    /// A critical file (01_/02_/03_) failed, which aborts the whole pass -- so the repair reinstalled
    /// nothing and genuinely failed, whatever the version situation.
    /// </param>
    public static bool FailuresAreExpected(
        bool repairRan,
        string? installedVersion,
        string? targetVersion,
        bool criticalFileFailed)
    {
        if (!repairRan || criticalFileFailed)
        {
            return false;
        }

        /*
        The "unknown" sentinel is a GUESS, not a version -- GetInstalledVersionAsync returns it when the
        database is clearly installed but its recorded version cannot be read. It sorts below every real
        version, so trusting it here would answer "yes, an upgrade is pending" unconditionally, and every
        REAL repair failure on such a server would be reported as expected and exit 0. Concretely: a
        schema-current 3.1.0 server whose history rows are all FAILED, with four genuinely broken
        procedures, would pass a %ERRORLEVEL% gate while telling the operator to ignore the errors.
        */
        if (string.Equals(installedVersion?.Trim(), InstallationService.UnknownVersionSentinel, StringComparison.Ordinal))
        {
            return false;
        }

        var installed = ScriptProvider.TryParseVersionCore(installedVersion);
        var target = ScriptProvider.TryParseVersionCore(targetVersion);

        /* No comparable versions means no basis for the "expected" story. */
        if (installed == null || target == null)
        {
            return false;
        }

        /* The expected failures only exist when there is a migration still to run. */
        return installed < target;
    }

    /// <summary>
    /// True when there is an upgrade to run AFTER this repair — i.e. what the "now run the upgrade"
    /// handoff should key on.
    ///
    /// This is a DIFFERENT question from <see cref="FailuresAreExpected"/>, and the difference is the
    /// unknown sentinel. "May I excuse these file failures?" must answer NO for an unreadable version
    /// (we cannot certify a success we cannot explain). "Is there an upgrade to run next?" must answer
    /// YES for the same input — the version is unknown, so every hop may be pending. Reusing one boolean
    /// for both told the operator "already at the current version, so there is no upgrade to apply" for
    /// a server with eleven pending hops, and withheld the handoff button that would have applied them.
    /// </summary>
    public static bool HasPendingUpgrade(string? installedVersion, string? targetVersion)
    {
        var installed = ScriptProvider.TryParseVersionCore(installedVersion);
        var target = ScriptProvider.TryParseVersionCore(targetVersion);

        return installed != null && target != null && installed < target;
    }

    /// <summary>
    /// True when the recorded version could not be read, so it is unknown which migrations have run.
    /// Callers must say so rather than asserting the server is current.
    /// </summary>
    public static bool IsVersionUnknown(string? installedVersion) =>
        string.Equals(
            installedVersion?.Trim(),
            InstallationService.UnknownVersionSentinel,
            StringComparison.Ordinal);
}
