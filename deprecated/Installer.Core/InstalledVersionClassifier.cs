namespace Installer.Core;

/// <summary>
/// Turns what we can SEE on a server into the version we act on. This is the single most consequential
/// decision in the installer: it is what separates "nothing is installed, do a clean install" from
/// "something is installed, work out which migrations still need to run".
///
/// Getting it wrong in one direction strands migrations forever. A PerformanceMonitor database whose
/// ledger was lost — someone dropped <c>config.installation_history</c>, or a restore left it behind —
/// used to answer "no version" and therefore read as a FRESH INSTALL: the install scripts ran over the
/// live schema, every migration was skipped, and the target version was stamped SUCCESS. Version
/// detection reads back the most recent SUCCESS row, so those hops were never offered again.
///
/// Getting it wrong in the other direction breaks a legitimate install: an EMPTY database that someone
/// pre-created (commonly to control the data/log file paths) genuinely has nothing installed, and
/// attempting migrations against tables that do not exist would fail.
///
/// Split out of <see cref="InstallationService.GetInstalledVersionAsync"/> so it can be pinned by unit
/// tests that CI actually runs — the SQL that feeds it needs a live SQL Server, and that test suite is
/// excluded from CI.
/// </summary>
public static class InstalledVersionClassifier
{
    /// <param name="databaseExists">The PerformanceMonitor database exists on the server.</param>
    /// <param name="historyTableExists"><c>config.installation_history</c> exists.</param>
    /// <param name="collectTableCount">
    /// How many tables live in the <c>collect</c> schema. The discriminator between a PerformanceMonitor
    /// database that lost its ledger and an empty database someone pre-created for us.
    /// </param>
    /// <param name="latestSuccessVersion">
    /// <c>installer_version</c> of the most recent SUCCESS row, or null when there is none.
    /// </param>
    /// <returns>
    /// null when nothing is installed (do a clean install), a real version when we know it, or
    /// <see cref="InstallationService.UnknownVersionSentinel"/> when something IS installed but its
    /// version cannot be read — meaning "attempt every upgrade", the safe direction, since every
    /// upgrade script is <c>IF NOT EXISTS</c>-guarded and replays cleanly.
    /// </returns>
    public static string? Classify(
        bool databaseExists,
        bool historyTableExists,
        long collectTableCount,
        string? latestSuccessVersion)
    {
        if (!databaseExists)
        {
            /* Nothing there at all. */
            return null;
        }

        if (!historyTableExists)
        {
            /*
            No ledger. If the database holds collect objects it is a PerformanceMonitor database that lost
            its history -- answering null here is the stranding bug above. If it holds none, it is an empty
            database someone pre-created, and a clean install is right.
            */
            return collectTableCount > 0 ? InstallationService.UnknownVersionSentinel : null;
        }

        /*
        The ledger exists. A SUCCESS row is the answer; without one we know something was installed but
        not what, so fall back to "attempt every upgrade" rather than treating it as a fresh install and
        dropping the database (#538).

        Blank counts as absent, not as an answer. installer_version is NOT NULL, so a row hand-edited to ''
        -- and the block message we show an operator literally invites them to edit that row -- came back as
        "" rather than null, and "" is not a version: FilterUpgrades turns it into ZERO hops, which reads as
        "nothing to do" and strands every migration. InstallGuard rejects "" on every path today, so this is
        the second lock on the same door, and the ledger is exactly where a second lock is worth having.
        */
        return string.IsNullOrWhiteSpace(latestSuccessVersion)
            ? InstallationService.UnknownVersionSentinel
            : latestSuccessVersion;
    }
}
