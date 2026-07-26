/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// The service-orchestrated in-place upgrade of an existing managed store (#1706) — the runtime half of
/// the ConfMarker/migrations discipline. Before this existed a field store ran whatever PostgreSQL and
/// TimescaleDB it was first initialized with, FOREVER: a deploy swaps the app binaries but
/// <see cref="DarlingManagedPostgres"/>'s runtime probe short-circuits on an already-extracted
/// <c>pg-runtime\pgsql\bin\pg_ctl.exe</c>, so a package carrying a newer runtime changed nothing. That is
/// the drift #1705 caught the hard way (a store still on its original extension while the bundle shipped
/// 2.28.1, which is how the <c>scheduled =&gt;</c> signature gap survived).
///
/// <para><b>The stamp is the trigger.</b> Every extraction records the SHA256 of the zip it came from in
/// <see cref="RuntimeStampFileName"/>. A start whose zip hash differs from the stamp means the package
/// carries a new runtime, and that ONE signal covers both halves of the problem: a TimescaleDB-only bump
/// (same PostgreSQL major, new extension) and a PostgreSQL MAJOR jump (17 to 18). Hashing 50 MB once per
/// service start is cheap next to being wrong about what is on disk.</para>
///
/// <para><b>Why both runtimes must survive.</b> <c>pg_upgrade</c> needs the OLD binaries and the NEW
/// binaries present at the same time — it starts each cluster with its own postmaster. So the swap
/// RESCUES the current <c>pg-runtime\pgsql</c> to <c>pg-runtime-prev\pgsql</c> before extracting the new
/// zip, and that rescued tree (not just <c>bin</c>: the old postmaster loads <c>$libdir</c> relative to
/// itself, so <c>lib</c> and <c>share</c> come too) is <c>--old-bindir</c>'s home. Rescue BEFORE extract is
/// not an ordering preference; get it backwards and the old binaries are gone and the store is
/// unupgradeable.</para>
///
/// <para><b>The TimescaleDB bridge is mandatory, not hygiene.</b> pg_upgrade's binary-upgrade dump
/// recreates the extension pinned at the version the OLD cluster has installed, and every TimescaleDB
/// function resolves to a VERSIONED library (<c>$libdir/timescaledb-2.28.1</c>). The new runtime ships
/// exactly one such library. A store sitting on 2.17.2 therefore fails the upgrade with a missing-library
/// error unless its extension is first updated, ON THE OLD CLUSTER, to the version the new bundle carries
/// — which is also what Timescale documents ("the version of TimescaleDB must be the same before and after
/// the PostgreSQL upgrade"). Hence: bridge first, upgrade second.</para>
///
/// <para><b>Failure isolation.</b> Copy mode leaves the old data directory byte-for-byte untouched, so
/// every failure path reverts to the old runtime + old data directory and the store keeps running on its
/// original major. A failure also RECORDS the failing zip's hash in <see cref="RuntimeBlockedFileName"/>
/// so the next start does not retry the same known-bad package on a loop — a different zip clears it.
/// Nothing here ever leaves the store down.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DarlingStoreUpgrade
{
    /// <summary>Records the SHA256 of the zip the extracted runtime came from — the change detector.</summary>
    public const string RuntimeStampFileName = "pg-runtime.stamp";

    /// <summary>
    /// Records the SHA256 of a zip whose upgrade FAILED. A start that sees the same hash skips the swap
    /// (logging why) instead of retrying a known-bad package every time the service restarts; shipping a
    /// different zip changes the hash and re-arms the attempt.
    /// </summary>
    public const string RuntimeBlockedFileName = "pg-runtime.blocked";

    /// <summary>Suffix on the runtime root holding the rescued previous runtime (pg_upgrade's --old-bindir).</summary>
    public const string PreviousRuntimeSuffix = "-prev";

    /// <summary>
    /// How many service starts the pre-upgrade data directory is kept before deletion. TWO, not one: the
    /// first start after the upgrade is the one that proves the new cluster serves real collection, and a
    /// store that came up, misbehaved, and got restarted still has its rollback copy on that second start.
    /// </summary>
    public const int RollbackRetentionStarts = 2;

    /// <summary>Slack above the measured 2x requirement for copy mode, so a copy never fills the volume dead.</summary>
    private const long CopyHeadroomSlackBytes = 1L * 1024 * 1024 * 1024;

    /* pg_upgrade on a large field store is genuinely long-running in copy mode, and the store is offline
       for the duration — but a partial copy is worse than a slow one, so the budget is generous rather
       than tight. initdb/pg_controldata are quick; the bridge has to tolerate a big catalog rewrite. */
    private static readonly TimeSpan s_pgUpgradeTimeout = TimeSpan.FromHours(4);

    /// <summary>
    /// The DRY RUN gets a far tighter budget than the real pass. <c>--check</c> copies nothing — it reads
    /// catalogs and compares settings — so minutes is generous where the copy legitimately needs hours. The
    /// asymmetry matters because this is the step that runs FIRST and with the store already offline: giving
    /// a stuck check the copy's budget would hold the store down for hours before the revert, which is a
    /// worse outcome than any upgrade is worth.
    /// </summary>
    private static readonly TimeSpan s_pgUpgradeCheckTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan s_initDbTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_toolTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_bridgeTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan s_analyzeTimeout = TimeSpan.FromHours(2);

    private readonly ILogger _logger;

    public DarlingStoreUpgrade(ILogger logger)
        => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /* ============================ outcome ============================ */

    internal enum StoreUpgradeStatus
    {
        /// <summary>Nothing to do — the data directory already matches the bundled major.</summary>
        None,

        /// <summary>The data directory was upgraded to the bundled major and verified.</summary>
        Succeeded,

        /// <summary>A step failed; the store reverted to its previous runtime + data directory and keeps running.</summary>
        Failed,

        /// <summary>Only the TimescaleDB extension moved (same PostgreSQL major) — the #1705 case.</summary>
        ExtensionUpdated,
    }

    /// <summary>
    /// What one start's upgrade attempt did, carried out of the bootstrap so the worker can raise a real
    /// self-alert once the store (and therefore the alert engine) is up. The store is DOWN while the
    /// upgrade runs, so the start of the work can only be a log line; both terminal states happen with a
    /// live store and are alertable.
    /// </summary>
    internal sealed record StoreUpgradeOutcome(
        StoreUpgradeStatus Status,
        int FromMajor,
        int ToMajor,
        string? FromTimescale,
        string? ToTimescale,
        string? FailedStep,
        string? Message,
        bool UsedLinkMode)
    {
        public static StoreUpgradeOutcome None { get; } =
            new(StoreUpgradeStatus.None, 0, 0, null, null, null, null, false);
    }

    /* ============================ pure helpers (unit-tested) ============================ */

    /// <summary>
    /// Parses the major from a PostgreSQL tool's <c>--version</c> line ("pg_ctl (PostgreSQL) 18.4" =&gt; 18).
    /// Tolerates the beta/rc forms ("18beta1") and a bare major ("18"). Null when nothing parses, which the
    /// caller treats as "cannot determine" and refuses to act on — guessing a major is how you point 18
    /// binaries at a 17 data directory.
    /// </summary>
    internal static int? ParsePostgresMajor(string? versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput))
        {
            return null;
        }

        /* Walk to the LAST parenthesised group's tail — the version always trails the product name — then
           take the leading digit run of the first token that starts with a digit. */
        foreach (var token in versionOutput.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 0 || !char.IsDigit(token[0]))
            {
                continue;
            }

            var digits = 0;
            while (digits < token.Length && char.IsDigit(token[digits]))
            {
                digits++;
            }

            if (int.TryParse(token[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major > 0)
            {
                return major;
            }
        }

        return null;
    }

    /// <summary>
    /// The major recorded in a data directory's <c>PG_VERSION</c> (a bare "17"). Same refuse-on-garbage
    /// posture as <see cref="ParsePostgresMajor"/>.
    /// </summary>
    internal static int? ParseDataDirectoryMajor(string? pgVersionFileContent)
        => ParsePostgresMajor(pgVersionFileContent?.Trim());

    /// <summary>
    /// The <c>default_version</c> a runtime's <c>share\extension\timescaledb.control</c> declares — the
    /// exact version the bundled library file is named for, and therefore the version the old cluster's
    /// extension must be bridged TO before pg_upgrade can resolve its functions.
    /// </summary>
    internal static string? ParseTimescaleDefaultVersion(string? controlFileText)
    {
        if (string.IsNullOrWhiteSpace(controlFileText))
        {
            return null;
        }

        foreach (var rawLine in controlFileText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#')
                || !line.StartsWith("default_version", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            var value = line[(equals + 1)..].Trim().Trim('\'', '"').Trim();
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    /// <summary>How pg_upgrade should move the data files, and why.</summary>
    internal enum FileTransferMode
    {
        /// <summary>The safe default: the old cluster survives the upgrade completely intact.</summary>
        Copy,

        /// <summary>Hard links — fast and nearly free on space, but the old cluster is unusable once the
        /// new one starts, so there is no rollback but a restore from backup.</summary>
        Link,

        /// <summary>Neither is safe on this volume — do not upgrade, keep running the old major.</summary>
        Abort,
    }

    internal sealed record TransferDecision(FileTransferMode Mode, string Reason);

    /// <summary>
    /// Chooses the pg_upgrade file-transfer mode from measured space (PURE, so the arithmetic is pinned by
    /// tests rather than discovered on a field box at 3am). Copy needs room for a SECOND full copy of the
    /// data directory plus slack; when that is not there, hard links need only the new cluster's own
    /// catalogs, so link mode is offered — but only when the volume actually supports hard links, and
    /// always as a LOUD downgrade because it trades the rollback away. Neither affordable means abort, which
    /// leaves the store exactly as it was: running, on the old major.
    /// </summary>
    internal static TransferDecision DecideTransferMode(long dataDirectoryBytes, long freeBytes, bool hardLinksSupported)
    {
        var copyNeeds = dataDirectoryBytes + dataDirectoryBytes + CopyHeadroomSlackBytes;
        if (freeBytes >= copyNeeds)
        {
            return new TransferDecision(
                FileTransferMode.Copy,
                $"{FormatBytes(freeBytes)} free covers the {FormatBytes(copyNeeds)} a copy needs (data {FormatBytes(dataDirectoryBytes)} x2 + 1 GB slack)");
        }

        /* Link mode still writes a fresh cluster's catalogs and the copied non-relation files; a tenth of
           the data directory plus the slack is a deliberately conservative floor for that. */
        var linkNeeds = (dataDirectoryBytes / 10) + CopyHeadroomSlackBytes;
        if (!hardLinksSupported)
        {
            return new TransferDecision(
                FileTransferMode.Abort,
                $"only {FormatBytes(freeBytes)} free (a copy needs {FormatBytes(copyNeeds)}) and this volume does not support hard links, so link mode is unavailable");
        }

        if (freeBytes >= linkNeeds)
        {
            return new TransferDecision(
                FileTransferMode.Link,
                $"only {FormatBytes(freeBytes)} free (a copy needs {FormatBytes(copyNeeds)}) — falling back to hard-link mode, which does NOT leave a rollback copy");
        }

        return new TransferDecision(
            FileTransferMode.Abort,
            $"only {FormatBytes(freeBytes)} free — not even hard-link mode's {FormatBytes(linkNeeds)} is available");
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.#} GB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB");
    }

    /// <summary>The locale/encoding/checksum identity of an existing cluster, read from it rather than assumed.</summary>
    internal sealed record ClusterIdentity(
        string Encoding,
        string Collate,
        string Ctype,
        string? LocaleProvider,
        string? Locale,
        bool DataChecksums);

    /// <summary>
    /// Builds the <c>initdb</c> arguments that reproduce <paramref name="identity"/> on the new cluster.
    /// EVERY locale/encoding/checksum knob is passed EXPLICITLY, never left to the new major's defaults,
    /// because those defaults move: PostgreSQL 18 flipped initdb to enable data checksums by default (a
    /// documented incompatibility), and pg_upgrade hard-refuses a checksum mismatch between clusters. The
    /// same reasoning covers the locale provider, which gained a "builtin" option in 17. Pure, so the exact
    /// argument string is pinned by tests.
    /// </summary>
    internal static string BuildInitDbArguments(
        string newDataDirectory, string userName, string passwordFilePath, ClusterIdentity identity, int newMajor)
    {
        var builder = new StringBuilder();
        builder.Append("-D \"").Append(newDataDirectory).Append('"');
        builder.Append(" -U ").Append(userName);
        builder.Append(" -A scram-sha-256");
        builder.Append(" --pwfile=\"").Append(passwordFilePath).Append('"');
        builder.Append(" -E ").Append(identity.Encoding);

        switch (identity.LocaleProvider)
        {
            case "i":
                builder.Append(" --locale-provider=icu");
                if (!string.IsNullOrWhiteSpace(identity.Locale))
                {
                    builder.Append(" --icu-locale=").Append(identity.Locale);
                }

                break;

            case "b":
                builder.Append(" --locale-provider=builtin");
                if (!string.IsNullOrWhiteSpace(identity.Locale))
                {
                    builder.Append(" --builtin-locale=").Append(identity.Locale);
                }

                break;

            case "c":
                builder.Append(" --locale-provider=libc");
                break;

            default:
                /* Unknown/absent provider column (a cluster older than the provider split): let the
                   lc-collate/lc-ctype pair below carry the locale, which is what those clusters used. */
                break;
        }

        /* Always explicit, for every provider: even ICU and builtin clusters carry an LC_COLLATE/LC_CTYPE
           pair that pg_upgrade compares. */
        builder.Append(" --lc-collate=").Append(identity.Collate);
        builder.Append(" --lc-ctype=").Append(identity.Ctype);

        if (identity.DataChecksums)
        {
            builder.Append(" --data-checksums");
        }
        else if (newMajor >= 18)
        {
            /* --no-data-checksums exists only from 18, which is also the first major whose default is ON,
               so an older new-cluster needs no flag to land checksum-less. */
            builder.Append(" --no-data-checksums");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Server options pg_upgrade passes to BOTH clusters it starts, quiescing TimescaleDB for the duration
    /// of the binary upgrade.
    ///
    /// <para>This is not tuning, it is a deadlock avoidance. TimescaleDB's scheduler background worker
    /// connects to databases and takes locks on its own schedule — including on <c>template0</c>, which
    /// timescale/timescaledb#1593 documents deadlocking against a restore that needs the same lock, in a
    /// cycle PostgreSQL's deadlock detector does not break. pg_upgrade is exactly that kind of workload: it
    /// starts each cluster and connects database by database. Nothing the scheduler could do during an
    /// upgrade window is wanted anyway — the jobs it would run operate on data being copied out from under
    /// them — so it is turned off for the duration and comes back with the normal start afterwards.</para>
    /// </summary>
    /// <summary>
    /// Server options pg_upgrade passes to BOTH clusters it starts, for the upgrade window only. The
    /// store's own postgresql.conf is never edited; <c>-c</c> on the command line simply outranks it, the
    /// same mechanism <see cref="DarlingManagedPostgres.BuildServerRuntimeOptions"/> already uses to force
    /// loopback at a normal start.
    ///
    /// <para><b>timescaledb.max_background_workers=0</b> — deadlock avoidance, not tuning. TimescaleDB's
    /// scheduler background worker connects to databases and takes locks on its own schedule, including on
    /// <c>template0</c>, which timescale/timescaledb#1593 documents deadlocking against a restore needing
    /// the same lock, in a cycle PostgreSQL's deadlock detector does not break. pg_upgrade is exactly that
    /// workload. Nothing the scheduler could do during an upgrade is wanted anyway — its jobs would operate
    /// on data being copied out from under them.</para>
    ///
    /// <para><b>listen_addresses=localhost</b> — the IPv4/IPv6 loopback trap, caught in pg_upgrade's own
    /// diagnostics: <c>connection to server at "localhost" (::1), port 50432 failed</c>. pg_upgrade has no
    /// Unix sockets on Windows, so it connects to its clusters BY NAME, and Windows resolves
    /// <c>localhost</c> to <c>::1</c> first. The managed v1 conf block pins
    /// <c>listen_addresses = '127.0.0.1'</c> — IPv4 ONLY — so on a real managed store there is nothing on
    /// <c>::1</c> for it to reach. Restoring the stock <c>localhost</c> value for the upgrade window binds
    /// both families, which is what pg_upgrade expects. This is the same trap
    /// <see cref="DarlingManagedPostgres.BuildConnectionString"/> already documents and dodges by using the
    /// literal <c>127.0.0.1</c> rather than the name — the lesson simply had never been applied to a tool
    /// that dials on our behalf.</para>
    /// </summary>
    internal const string QuiesceTimescaleServerOptions =
        "-c timescaledb.max_background_workers=0 -c listen_addresses=localhost";

    /// <summary>
    /// The ports pg_upgrade runs its throwaway old/new postmasters on. pg_upgrade defaults BOTH to
    /// <c>50432</c>, a fixed well-known value — so two upgrades on one host, or any leftover postmaster from
    /// an interrupted one, land on the same port and pg_upgrade silently talks to a STRANGER'S cluster. That
    /// is not hypothetical: it was caught here as <c>FATAL: role "darling" does not exist</c> coming back
    /// from a postmaster this service never started. These are deliberately obscure and distinct from each
    /// other, and from the store's own configured port. They are internal to the upgrade window — nothing
    /// connects to them but pg_upgrade itself.
    /// </summary>
    internal const int UpgradeOldClusterPort = 55432;
    internal const int UpgradeNewClusterPort = 55433;

    /// <summary>
    /// The pg_upgrade command line. <c>--check</c> first as a dry run (it validates locale/encoding/
    /// checksum compatibility and the loadable-library set WITHOUT touching either cluster), then the real
    /// pass. <c>-o</c>/<c>-O</c> carry <paramref name="serverOptions"/> to the old and new clusters
    /// respectively. Pure so every form is pinned by tests.
    /// </summary>
    internal static string BuildPgUpgradeArguments(
        string oldBinDirectory,
        string newBinDirectory,
        string oldDataDirectory,
        string newDataDirectory,
        string userName,
        FileTransferMode mode,
        bool checkOnly,
        int jobs,
        string? serverOptions = null)
    {
        var builder = new StringBuilder();
        builder.Append("--old-bindir \"").Append(oldBinDirectory).Append('"');
        builder.Append(" --new-bindir \"").Append(newBinDirectory).Append('"');
        builder.Append(" --old-datadir \"").Append(oldDataDirectory).Append('"');
        builder.Append(" --new-datadir \"").Append(newDataDirectory).Append('"');
        builder.Append(" --username ").Append(userName);

        /* Never the default 50432 — see UpgradeOldClusterPort. */
        builder.Append(" --old-port ").Append(UpgradeOldClusterPort.ToString(CultureInfo.InvariantCulture));
        builder.Append(" --new-port ").Append(UpgradeNewClusterPort.ToString(CultureInfo.InvariantCulture));

        if (mode == FileTransferMode.Link)
        {
            builder.Append(" --link");
        }

        if (checkOnly)
        {
            builder.Append(" --check");
        }
        else if (jobs > 1)
        {
            /* Jobs only help the real pass; --check does no file work. */
            builder.Append(" --jobs ").Append(jobs.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(serverOptions))
        {
            /* -o = the OLD cluster's postmaster, -O = the NEW one. Both, because pg_upgrade starts both
               and the setting has to hold on whichever side is being connected to. Quoted as one argument;
               the value itself must contain no interior double quotes (the builders here never emit any). */
            builder.Append(" -o \"").Append(serverOptions).Append('"');
            builder.Append(" -O \"").Append(serverOptions).Append('"');
        }

        return builder.ToString();
    }

    /* ============================ runtime stamp + swap ============================ */

    /// <summary>SHA256 of a file as lowercase hex — the runtime zip's identity.</summary>
    internal static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    internal static string PreviousRuntimeRootFor(string runtimeRoot)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot)) + PreviousRuntimeSuffix;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>
    /// Whether <paramref name="directory"/>'s volume actually supports hard links, answered by MAKING one
    /// rather than by inferring from the file-system name: ReFS and some redirected/network volumes report
    /// plausibly and then refuse. Both probe files are removed. Never throws — an unanswerable probe is a
    /// "no", which only ever costs an upgrade that would have needed link mode anyway.
    /// </summary>
    internal static bool SupportsHardLinks(string directory)
    {
        var source = Path.Combine(directory, $"pm-hardlink-probe-{Guid.NewGuid():N}.tmp");
        var link = source + ".link";
        try
        {
            File.WriteAllText(source, "probe");
            var created = CreateHardLinkW(link, source, IntPtr.Zero);
            return created && File.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            TryDeleteFile(link);
            TryDeleteFile(source);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            /* A probe leftover is harmless. */
        }
    }

    /// <summary>Total bytes of every file under <paramref name="directory"/>; unreadable entries are skipped.</summary>
    internal static long MeasureDirectoryBytes(string directory)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    /* A file that vanished mid-walk does not change the order of magnitude. */
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            /* Partial measurement still beats no measurement; the caller's slack absorbs it. */
        }

        return total;
    }

    /* ============================ runtime advance (called from EnsureRuntimeAsync) ============================ */

    /// <summary>What <see cref="TryAdvanceRuntimeAsync"/> did with the shipped zip.</summary>
    internal sealed record RuntimeAdvance(bool Swapped, string? PreviousBinDirectory, string? ZipHash);

    /// <summary>
    /// Reconciles the EXTRACTED runtime against the SHIPPED zip. When the zip's hash differs from the stamp
    /// the package carries a new runtime, so the current <c>pgsql</c> tree is rescued to
    /// <c>&lt;runtimeRoot&gt;-prev</c> (whole tree — the old postmaster resolves <c>$libdir</c> relative to
    /// its own binaries) and the new zip is extracted in its place. Returns the rescued bin directory, which
    /// is pg_upgrade's <c>--old-bindir</c>.
    ///
    /// <para>Refuses to swap under a LIVE postmaster: the running server holds its binaries open, and
    /// stopping a server this service did not start is not its call (the adopt-never-stop rule). The swap
    /// simply waits for the next service-owned start.</para>
    /// </summary>
    internal async Task<RuntimeAdvance> TryAdvanceRuntimeAsync(
        string runtimeRoot,
        string runtimeZipPath,
        string dataDirectory,
        Func<string, CancellationToken, Task<bool>> isServerRunningAsync,
        CancellationToken cancellationToken)
    {
        var pgsqlDirectory = Path.Combine(runtimeRoot, "pgsql");
        var binDirectory = Path.Combine(pgsqlDirectory, "bin");
        var stampPath = Path.Combine(runtimeRoot, RuntimeStampFileName);
        var blockedPath = Path.Combine(runtimeRoot, RuntimeBlockedFileName);

        var zipHash = await Task.Run(() => ComputeFileHash(runtimeZipPath), cancellationToken);
        var stamp = ReadTrimmedOrNull(stampPath);

        if (string.Equals(stamp, zipHash, StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeAdvance(false, null, zipHash);
        }

        var blocked = ReadTrimmedOrNull(blockedPath);
        if (string.Equals(blocked, zipHash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "The shipped Postgres runtime ({Zip}) is the SAME package whose store upgrade already failed on this host, so it will not be retried — the store keeps running its current runtime. Check the earlier upgrade failure, then ship a corrected package (a different zip re-arms the attempt) or delete {Blocked} to force a retry.",
                runtimeZipPath, blockedPath);
            return new RuntimeAdvance(false, null, zipHash);
        }

        if (await isServerRunningAsync(binDirectory, cancellationToken))
        {
            _logger.LogWarning(
                "The package ships a different Postgres runtime, but a postmaster is already running on {DataDirectory} — this service does not stop a server it did not start, so the runtime update is deferred to the next service-owned start.",
                dataDirectory);
            return new RuntimeAdvance(false, null, zipHash);
        }

        var previousRoot = PreviousRuntimeRootFor(runtimeRoot);
        var previousPgsql = Path.Combine(previousRoot, "pgsql");

        _logger.LogWarning(
            "The package ships a different Postgres runtime than the one extracted on this host — rescuing the current runtime to {Previous} and extracting the new one. This is the store runtime update (#1706); if the PostgreSQL major changed, an in-place pg_upgrade follows.",
            previousPgsql);

        if (Directory.Exists(previousRoot))
        {
            Directory.Delete(previousRoot, recursive: true);
        }

        Directory.CreateDirectory(previousRoot);
        Directory.Move(pgsqlDirectory, previousPgsql);

        try
        {
            await Task.Run(
                () => System.IO.Compression.ZipFile.ExtractToDirectory(runtimeZipPath, runtimeRoot, overwriteFiles: true),
                cancellationToken);

            if (!File.Exists(Path.Combine(binDirectory, "pg_ctl.exe")))
            {
                throw new InvalidOperationException(
                    $"Extracted {runtimeZipPath} but {binDirectory}\\pg_ctl.exe is missing — the archive does not contain pgsql\\bin.");
            }
        }
        catch (Exception)
        {
            /* The new runtime is not usable; put the old one back so the store still boots, and let the
               caller's existing error path report. Nothing has touched the data directory yet. Move-aside
               rather than delete-first, for the reason spelled out in RevertRuntime: a partial delete
               would leave an unbootable runtime behind. */
            var failedExtract = pgsqlDirectory + ".failed";
            TryDeleteDirectory(failedExtract);
            if (Directory.Exists(pgsqlDirectory))
            {
                Directory.Move(pgsqlDirectory, failedExtract);
            }

            Directory.Move(previousPgsql, pgsqlDirectory);
            TryDeleteDirectory(failedExtract);
            TryDeleteDirectory(previousRoot);
            throw;
        }

        File.WriteAllText(stampPath, zipHash);
        return new RuntimeAdvance(true, Path.Combine(previousPgsql, "bin"), zipHash);
    }

    /// <summary>
    /// Restores the rescued runtime over the newly-extracted one — the revert half of
    /// <see cref="TryAdvanceRuntimeAsync"/>. The path the service starts from is unchanged
    /// (<c>&lt;runtimeRoot&gt;\pgsql\bin</c>), so a caller holding that bin directory keeps working; only
    /// the binaries behind it go back to the previous major.
    /// </summary>
    internal void RevertRuntime(string runtimeRoot, string zipHash)
    {
        var pgsqlDirectory = Path.Combine(runtimeRoot, "pgsql");
        var previousRoot = PreviousRuntimeRootFor(runtimeRoot);
        var previousPgsql = Path.Combine(previousRoot, "pgsql");

        try
        {
            if (!Directory.Exists(previousPgsql))
            {
                _logger.LogCritical(
                    "Cannot revert the store runtime: the rescued copy {Previous} is gone. The service will start whatever is in {Current}.",
                    previousPgsql, pgsqlDirectory);
                return;
            }

            /* MOVE the failed runtime aside, never delete-then-move. A delete can partially succeed — a
               postmaster that has not finished exiting still holds its binaries — and the swallowed
               failure would leave a half-emptied pgsql directory that the following Move then refuses to
               overwrite. The store is then left with a runtime missing pg_ctl.exe: a self-inflicted
               unbootable install, produced by the very path that exists to make failure safe. A rename
               either works completely or fails without touching anything. */
            var failedRuntime = pgsqlDirectory + ".failed";
            TryDeleteDirectory(failedRuntime);
            if (Directory.Exists(pgsqlDirectory))
            {
                Directory.Move(pgsqlDirectory, failedRuntime);
            }

            Directory.Move(previousPgsql, pgsqlDirectory);
            TryDeleteDirectory(failedRuntime);
            TryDeleteDirectory(previousRoot);

            /* Record the failing package so the next start does not run the same doomed upgrade again, and
               drop the stamp so the runtime on disk is not claimed to be the shipped one. */
            File.WriteAllText(Path.Combine(runtimeRoot, RuntimeBlockedFileName), zipHash);
            TryDeleteFile(Path.Combine(runtimeRoot, RuntimeStampFileName));

            _logger.LogWarning("Reverted to the previous Postgres runtime — the store continues on its existing major.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                "Could not revert the store runtime ({Message}). Restore {Previous} over {Current} by hand before restarting the service.",
                ex.Message, previousPgsql, pgsqlDirectory);
        }
    }

    private static string? ReadTrimmedOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            /* Best effort — the caller logs the consequence that matters. */
        }
    }

    /* ============================ retained rollback copies ============================ */

    internal static string RetainedDataDirectoryFor(string dataDirectory, int oldMajor)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)) + "-old-" + oldMajor.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Ages out the pre-upgrade data directory kept for rollback: each service start bumps its counter, and
    /// the copy is deleted once it has survived <see cref="RollbackRetentionStarts"/> starts — with a log
    /// line naming the space reclaimed, because a silently vanishing multi-GB directory is its own support
    /// call. Runs on EVERY start, not only upgrade starts, since that is what makes the countdown advance.
    /// </summary>
    internal void SweepRetainedDataDirectories(string dataDirectory)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)));
            var prefix = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory))) + "-old-";
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                return;
            }

            foreach (var retained in Directory.GetDirectories(parent, prefix + "*"))
            {
                var counterPath = retained + ".starts";
                var starts = int.TryParse(ReadTrimmedOrNull(counterPath), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed + 1
                    : 1;

                if (starts < RollbackRetentionStarts)
                {
                    File.WriteAllText(counterPath, starts.ToString(CultureInfo.InvariantCulture));
                    _logger.LogInformation(
                        "Keeping the pre-upgrade store data directory {Path} for {Remaining} more service start(s) as a rollback copy.",
                        retained, RollbackRetentionStarts - starts);
                    continue;
                }

                var reclaimed = MeasureDirectoryBytes(retained);
                Directory.Delete(retained, recursive: true);
                TryDeleteFile(counterPath);
                _logger.LogInformation(
                    "Deleted the pre-upgrade store data directory {Path} after {Starts} service starts on the upgraded store, reclaiming {Size}.",
                    retained, RollbackRetentionStarts, FormatBytes(reclaimed));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not sweep retained pre-upgrade data directories ({Message}) — they are safe to delete by hand.", ex.Message);
        }
    }

    /* ============================ the upgrade ============================ */

    /// <summary>Everything the orchestration needs, gathered once so the flow reads as steps rather than plumbing.</summary>
    internal sealed record UpgradeContext(
        string OldBinDirectory,
        string NewBinDirectory,
        string RuntimeRoot,
        string ZipHash,
        string DataDirectory,
        int Port,
        string UserName,
        string Password,
        int OldMajor,
        int NewMajor,
        string BundledTimescaleVersion,
        Action<string> AppendManagedConf);

    /// <summary>
    /// The in-place major upgrade, start to finish. Each step is labelled, and ANY failure lands in one
    /// place: revert the runtime, drop the half-built new cluster, leave the old data directory (untouched
    /// in copy mode) exactly where it was, and return a Failed outcome so the caller keeps running the store
    /// on its existing major. The one thing this must never do is leave the store down.
    /// </summary>
    internal async Task<StoreUpgradeOutcome> UpgradeDataDirectoryAsync(UpgradeContext context, CancellationToken cancellationToken)
    {
        var newDataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.DataDirectory))
            + "-upgrade-" + context.NewMajor.ToString(CultureInfo.InvariantCulture);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.DataDirectory)))!;
        var passwordFile = Path.Combine(parent, "pg-upgrade-pwfile.tmp");
        var passFile = Path.Combine(parent, "pg-upgrade-pgpass.tmp");

        var step = "preflight";
        var oldStarted = false;
        string? fromTimescale = null;
        var mode = FileTransferMode.Copy;

        _logger.LogWarning(
            "STORE UPGRADE STARTING: PostgreSQL {Old} -> {New} on {DataDirectory}. Collection is paused until this finishes; the store is offline for the duration.",
            context.OldMajor, context.NewMajor, context.DataDirectory);

        try
        {
            /* ---- 1. space + hard-link capability, measured before anything is touched ---- */
            step = "disk-headroom";
            var dataBytes = MeasureDirectoryBytes(context.DataDirectory);
            var free = new DriveInfo(Path.GetPathRoot(parent)!).AvailableFreeSpace;
            var decision = DecideTransferMode(dataBytes, free, SupportsHardLinks(parent));
            mode = decision.Mode;

            if (mode == FileTransferMode.Abort)
            {
                throw new InvalidOperationException(
                    $"not enough disk space to upgrade safely: {decision.Reason}. Free space on the store volume and restart the service.");
            }

            if (mode == FileTransferMode.Link)
            {
                _logger.LogCritical(
                    "STORE UPGRADE USING HARD-LINK MODE: {Reason}. Hard-link mode does NOT leave a usable rollback copy of the pre-upgrade store — once the upgraded server starts, the only way back is a restore from backup. Take one now if you do not have a recent one.",
                    decision.Reason);
            }
            else
            {
                _logger.LogInformation("Store upgrade file transfer: copy mode — {Reason}.", decision.Reason);
            }

            /* ---- 2. start the OLD cluster on its OLD binaries: crash recovery if needed, and the only
                    place the cluster's real locale/encoding/checksum identity and its TimescaleDB version
                    can be read rather than assumed ---- */
            step = "start-old-cluster";
            await StartClusterAsync(context.OldBinDirectory, context.DataDirectory, context.Port, cancellationToken);
            oldStarted = true;

            step = "read-cluster-identity";
            var ownerConnection = DarlingManagedPostgres.BuildConnectionString(context.Port, context.Password);
            var identity = await ReadClusterIdentityAsync(ownerConnection, cancellationToken);
            _logger.LogInformation(
                "Old cluster identity: encoding {Encoding}, collate {Collate}, ctype {Ctype}, locale provider {Provider}, data checksums {Checksums} — the new cluster is initialized to match.",
                identity.Encoding, identity.Collate, identity.Ctype, identity.LocaleProvider ?? "(none)", identity.DataChecksums ? "on" : "off");

            /* ---- 3. the TimescaleDB bridge: pg_upgrade recreates the extension pinned at the version the
                    OLD cluster has, and the NEW runtime ships exactly one versioned library. Mismatch here
                    is a guaranteed upgrade failure later, so it is a hard gate, not best-effort. ---- */
            step = "timescaledb-bridge";
            fromTimescale = await BridgeTimescaleAsync(
                context.Port, context.UserName, context.Password, context.BundledTimescaleVersion, cancellationToken);

            /* ---- 4. stop the old cluster cleanly — pg_upgrade refuses to run against a live or
                    unclean-shutdown cluster ---- */
            step = "stop-old-cluster";
            await StopClusterAsync(context.OldBinDirectory, context.DataDirectory, cancellationToken);
            oldStarted = false;

            /* ---- 5. initdb the new cluster to the OLD cluster's identity, then give it the managed conf
                    BEFORE pg_upgrade runs: shared_preload_libraries = 'timescaledb' must already be set or
                    pg_upgrade's internal restore cannot load the extension it is restoring ---- */
            step = "initdb-new-cluster";
            TryDeleteDirectory(newDataDirectory);
            File.WriteAllText(passwordFile, context.Password + "\n");
            DarlingFileSecurity.HardenFile(passwordFile, allowInteractiveRead: false);

            var (initExit, initOutput) = await DarlingManagedPostgres.RunToolAsync(
                Path.Combine(context.NewBinDirectory, "initdb.exe"),
                BuildInitDbArguments(newDataDirectory, context.UserName, passwordFile, identity, context.NewMajor),
                s_initDbTimeout,
                cancellationToken);
            if (initExit != 0)
            {
                throw new InvalidOperationException($"initdb of the new cluster failed (exit {initExit}):\n{initOutput}");
            }

            step = "conf-new-cluster";
            context.AppendManagedConf(newDataDirectory);

            /* ---- 6. pg_upgrade: --check first (it validates the locale/encoding/checksum match and the
                    loadable-library set without touching either cluster), then the real pass ---- */
            step = "pg_upgrade-check";
            WritePgPassFile(passFile, context.UserName, context.Password);
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PGPASSFILE"] = passFile };

            var checkExit = await DarlingManagedPostgres.RunDetachingToolAsync(
                Path.Combine(context.NewBinDirectory, "pg_upgrade.exe"),
                BuildPgUpgradeArguments(
                    context.OldBinDirectory, context.NewBinDirectory, context.DataDirectory, newDataDirectory,
                    context.UserName, mode, checkOnly: true, jobs: 1, QuiesceTimescaleServerOptions),
                s_pgUpgradeCheckTimeout,
                cancellationToken,
                environment,
                parent);
            if (checkExit != 0)
            {
                throw new InvalidOperationException(
                    $"pg_upgrade --check failed (exit {checkExit}) — the clusters are not compatible and NOTHING has been changed.\n{ReadPgUpgradeLogTail(newDataDirectory)}");
            }

            step = "pg_upgrade";
            var jobs = Math.Clamp(Environment.ProcessorCount, 1, 4);
            var upgradeExit = await DarlingManagedPostgres.RunDetachingToolAsync(
                Path.Combine(context.NewBinDirectory, "pg_upgrade.exe"),
                BuildPgUpgradeArguments(
                    context.OldBinDirectory, context.NewBinDirectory, context.DataDirectory, newDataDirectory,
                    context.UserName, mode, checkOnly: false, jobs, QuiesceTimescaleServerOptions),
                s_pgUpgradeTimeout,
                cancellationToken,
                environment,
                parent);
            if (upgradeExit != 0)
            {
                throw new InvalidOperationException(
                    $"pg_upgrade failed (exit {upgradeExit}).\n{ReadPgUpgradeLogTail(newDataDirectory)}");
            }

            /* ---- 7. swap the directories so the configured path holds the upgraded cluster. The
                    credential/cert/log files live BESIDE the data directory, so they are untouched. ---- */
            step = "swap-data-directories";
            var retained = RetainedDataDirectoryFor(context.DataDirectory, context.OldMajor);
            TryDeleteDirectory(retained);
            Directory.Move(context.DataDirectory, retained);
            try
            {
                Directory.Move(newDataDirectory, context.DataDirectory);
            }
            catch (Exception)
            {
                /* Put the original back rather than leave the configured path empty. */
                Directory.Move(retained, context.DataDirectory);
                throw;
            }

            if (mode == FileTransferMode.Link)
            {
                /* Hard-link mode leaves an old directory that SHARES its files with the new cluster — it is
                   not a rollback copy and keeping it invites someone to try. Delete it now, loudly. */
                TryDeleteDirectory(retained);
                _logger.LogWarning(
                    "Removed the pre-upgrade data directory immediately: hard-link mode shares its files with the upgraded cluster, so it was never a usable rollback copy.");
            }
            else
            {
                File.WriteAllText(retained + ".starts", "0");
                _logger.LogInformation(
                    "Pre-upgrade data directory kept at {Path} as a rollback copy for {Starts} service starts.",
                    retained, RollbackRetentionStarts);
            }

            _logger.LogWarning(
                "STORE UPGRADE COMPLETE: PostgreSQL {Old} -> {New}, TimescaleDB {FromTs} -> {ToTs}. Verifying the upgraded store now.",
                context.OldMajor, context.NewMajor, fromTimescale ?? "(none)", context.BundledTimescaleVersion);

            return new StoreUpgradeOutcome(
                StoreUpgradeStatus.Succeeded, context.OldMajor, context.NewMajor,
                fromTimescale, context.BundledTimescaleVersion, null, null, mode == FileTransferMode.Link);
        }
        catch (OperationCanceledException)
        {
            /* Service shutdown mid-upgrade. Copy mode has not touched the old data directory, so the revert
               below restores the previous runtime and the next start simply tries again. */
            await TryStopAsync(context, oldStarted);
            TryDeleteDirectory(newDataDirectory);
            RevertRuntimeForCancel(context);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                "STORE UPGRADE FAILED at step '{Step}': {Message}. Reverting to PostgreSQL {Old} — the store keeps running on its existing major and NO data has been lost (the pre-upgrade data directory was never modified).",
                step, ex.Message, context.OldMajor);

            await TryStopAsync(context, oldStarted);
            TryDeleteDirectory(newDataDirectory);
            RevertRuntime(context.RuntimeRoot, context.ZipHash);

            return new StoreUpgradeOutcome(
                StoreUpgradeStatus.Failed, context.OldMajor, context.NewMajor,
                fromTimescale, context.BundledTimescaleVersion, step, ex.Message, false);
        }
        finally
        {
            TryDeleteFile(passwordFile);
            TryDeleteFile(passFile);
        }
    }

    private async Task TryStopAsync(UpgradeContext context, bool oldStarted)
    {
        if (!oldStarted)
        {
            return;
        }

        try
        {
            await StopClusterAsync(context.OldBinDirectory, context.DataDirectory, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not stop the old cluster after a failed upgrade ({Message}); the next start adopts it.", ex.Message);
        }
    }

    private void RevertRuntimeForCancel(UpgradeContext context)
    {
        /* A cancelled upgrade is not a BAD package, so revert the binaries without recording the block —
           the next start should try again. */
        var blockedPath = Path.Combine(context.RuntimeRoot, RuntimeBlockedFileName);
        RevertRuntime(context.RuntimeRoot, context.ZipHash);
        TryDeleteFile(blockedPath);
    }

    /// <summary>
    /// Starts a cluster with a specific runtime, loopback-only and with no network/TLS overrides — the
    /// upgrade window is not the time to reconcile exposure. Uses the detaching runner because pg_ctl's
    /// spawned postmaster outlives it and inherits redirected handles (the redirect-on-start hang).
    /// </summary>
    private async Task StartClusterAsync(string binDirectory, string dataDirectory, int port, CancellationToken cancellationToken)
    {
        var serverLog = Path.Combine(
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)))!,
            DarlingManagedPostgres.ServerLogFileName);

        var exitCode = await DarlingManagedPostgres.RunDetachingToolAsync(
            Path.Combine(binDirectory, "pg_ctl.exe"),
            $"-D \"{dataDirectory}\" -o \"-p {port} -c listen_addresses=127.0.0.1\" -l \"{serverLog}\" -w -t 120 start",
            TimeSpan.FromMinutes(5),
            cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"could not start the cluster in {dataDirectory} with the runtime at {binDirectory} (pg_ctl exit {exitCode})");
        }
    }

    private async Task StopClusterAsync(string binDirectory, string dataDirectory, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await DarlingManagedPostgres.RunToolAsync(
            Path.Combine(binDirectory, "pg_ctl.exe"),
            $"stop -D \"{dataDirectory}\" -m fast -w -t 120",
            s_toolTimeout,
            cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"could not cleanly stop the cluster in {dataDirectory} (pg_ctl exit {exitCode}): {output}");
        }
    }

    /// <summary>
    /// Reads the cluster's locale/encoding/checksum identity from the LIVE server. <c>template0</c> is the
    /// pristine record of what initdb was told (a user database may have been created with anything), and
    /// the <c>to_jsonb</c> lookups make the query tolerant of columns that only exist in some majors
    /// (<c>datlocale</c> in 17+, <c>daticulocale</c> in 15/16) instead of failing on the ones it lacks.
    /// </summary>
    internal static async Task<ClusterIdentity> ReadClusterIdentityAsync(string ownerConnectionString, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(ownerConnectionString) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            SELECT pg_encoding_to_char(d.encoding) AS encoding,
                   d.datcollate,
                   d.datctype,
                   to_jsonb(d) ->> 'datlocprovider' AS locprovider,
                   COALESCE(to_jsonb(d) ->> 'datlocale', to_jsonb(d) ->> 'daticulocale') AS locale,
                   current_setting('data_checksums') AS data_checksums
            FROM pg_database AS d
            WHERE d.datname = 'template0'
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("template0 is missing from pg_database — the cluster's locale identity cannot be read.");
        }

        return new ClusterIdentity(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            !reader.IsDBNull(5) && string.Equals(reader.GetString(5), "on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Updates the TimescaleDB extension to <paramref name="targetVersion"/> in EVERY database that has it
    /// — template1 included, because pg_upgrade carries template1 across too and a stale extension there
    /// fails the upgrade just as surely as one in the store database. Each database gets a FRESH,
    /// UNPOOLED connection whose FIRST statement is the ALTER: TimescaleDB refuses the update once its
    /// library has been loaded into the session, which is why its own documentation calls for a new
    /// connection. Returns the version found before the update (null when the extension is absent
    /// everywhere), for the alert text.
    /// </summary>
    internal async Task<string?> BridgeTimescaleAsync(
        int port, string userName, string password, string targetVersion, CancellationToken cancellationToken)
    {
        var ownerConnection = DarlingManagedPostgres.BuildConnectionString(port, password);
        var databases = new List<string>();

        var listBuilder = new NpgsqlConnectionStringBuilder(ownerConnection) { Database = "postgres", Pooling = false };
        await using (var connection = new NpgsqlConnection(listBuilder.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT datname FROM pg_database WHERE datallowconn ORDER BY datname", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                databases.Add(reader.GetString(0));
            }
        }

        string? observedBefore = null;

        foreach (var database in databases)
        {
            var perDatabase = new NpgsqlConnectionStringBuilder(ownerConnection)
            {
                Database = database,
                Pooling = false,
                /* The store's search_path is irrelevant here and a missing schema on a maintenance database
                   would only add noise. */
                SearchPath = null,
                CommandTimeout = (int)s_bridgeTimeout.TotalSeconds,
            };

            await using var connection = new NpgsqlConnection(perDatabase.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            string? installed;
            await using (var probe = new NpgsqlCommand(
                "SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'", connection))
            {
                installed = await probe.ExecuteScalarAsync(cancellationToken) as string;
            }

            if (installed is null)
            {
                continue;
            }

            observedBefore ??= installed;

            if (string.Equals(installed, targetVersion, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "TimescaleDB in database '{Database}' is already at {Version} — no bridge needed.", database, installed);
                continue;
            }

            _logger.LogWarning(
                "Bridging TimescaleDB in database '{Database}': {From} -> {To}. This must happen on the OLD cluster: pg_upgrade recreates the extension pinned at whatever version is installed, and the new runtime ships exactly one version-suffixed library.",
                database, installed, targetVersion);

            /* The FIRST statement on this connection, deliberately — TimescaleDB rejects the update once
               its library has been loaded into the session. The probe above reads pg_extension, a plain
               catalog table, which does not load it. */
            await using (var update = new NpgsqlCommand("ALTER EXTENSION timescaledb UPDATE", connection)
            {
                CommandTimeout = (int)s_bridgeTimeout.TotalSeconds,
            })
            {
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var verify = new NpgsqlCommand(
                "SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'", connection);
            var after = await verify.ExecuteScalarAsync(cancellationToken) as string;
            if (!string.Equals(after, targetVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"TimescaleDB in database '{database}' is {after ?? "(absent)"} after ALTER EXTENSION UPDATE, but the bundled runtime ships {targetVersion}. " +
                    "pg_upgrade would fail on the version-suffixed library, so the upgrade stops here.");
            }

            _logger.LogInformation("TimescaleDB in database '{Database}' is now {Version}.", database, after);
        }

        return observedBefore;
    }

    /// <summary>libpq's password file, so pg_upgrade's internal connections authenticate without an
    /// environment variable carrying the superuser password through the process table. Same
    /// hardened-temp-file discipline as initdb's <c>--pwfile</c>, deleted in the caller's finally.</summary>
    private static void WritePgPassFile(string path, string userName, string password)
    {
        /* Backslash-escape the pgpass field separators. The generated password is alphanumeric, so this is
           belt-and-braces for a hand-restored credential. */
        var escaped = password.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal);
        File.WriteAllText(path, $"*:*:*:{userName}:{escaped}\n");
        DarlingFileSecurity.HardenFile(path, allowInteractiveRead: false);
    }

    /// <summary>
    /// pg_upgrade's own logs, which it writes under the NEW data directory and removes on success — so
    /// anything found here belongs to a failure and is exactly what explains it. The tool's console output
    /// is deliberately not captured (its child postmasters inherit redirected handles and never close them),
    /// making these files the whole diagnostic story.
    /// </summary>
    private static string ReadPgUpgradeLogTail(string newDataDirectory)
    {
        try
        {
            var outputRoot = Path.Combine(newDataDirectory, "pg_upgrade_output.d");
            if (!Directory.Exists(outputRoot))
            {
                return "(pg_upgrade left no output directory)";
            }

            var builder = new StringBuilder();
            foreach (var file in Directory.GetFiles(outputRoot, "*.txt", SearchOption.AllDirectories))
            {
                AppendTail(builder, file);
            }

            foreach (var file in Directory.GetFiles(outputRoot, "*.log", SearchOption.AllDirectories))
            {
                AppendTail(builder, file);
            }

            return builder.Length == 0 ? $"(no readable logs under {outputRoot})" : builder.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"(could not read the pg_upgrade logs: {ex.Message})";
        }

        static void AppendTail(StringBuilder builder, string file)
        {
            try
            {
                var lines = File.ReadAllLines(file);
                if (lines.Length == 0)
                {
                    return;
                }

                var take = Math.Min(30, lines.Length);
                builder.Append("--- ").Append(file).Append(" ---\n");
                builder.Append(string.Join('\n', lines[^take..])).Append('\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                /* One unreadable log does not hide the others. */
            }
        }
    }

    /* ============================ post-start: verify, update, analyze ============================ */

    /// <summary>
    /// Everything that needs a LIVE server, run once the normal bootstrap has started it (so the server is
    /// owned by this process and shutdown still stops it — starting it here would silently turn the store
    /// into an "adopted" one this service refuses to stop).
    ///
    /// <para>Three jobs. It VERIFIES the upgrade actually landed — server major, extension version, and a
    /// real read of a collector table, because "pg_upgrade exited 0" and "the store works" are different
    /// claims. It applies the SAME-MAJOR TimescaleDB update, which is the #1705 case on its own: a runtime
    /// bump that changes only the extension needs no pg_upgrade, just the ALTER on a fresh connection. And
    /// it runs the post-upgrade analyze staging: PostgreSQL 18 carries most optimizer statistics across, so
    /// this fills the documented gaps (extended statistics, extension-owned statistics) rather than
    /// re-analyzing a whole store that already has them.</para>
    /// </summary>
    internal async Task<StoreUpgradeOutcome> CompleteAfterStartAsync(
        StoreUpgradeOutcome outcome,
        string ownerConnectionString,
        string newBinDirectory,
        int port,
        string userName,
        string password,
        int bundledMajor,
        string bundledTimescaleVersion,
        CancellationToken cancellationToken)
    {
        var upgraded = outcome.Status == StoreUpgradeStatus.Succeeded;

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(ownerConnectionString) { Pooling = false };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            if (upgraded)
            {
                await using var version = new NpgsqlCommand("SHOW server_version_num", connection);
                var raw = await version.ExecuteScalarAsync(cancellationToken) as string;
                var liveMajor = int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var num) ? num / 10000 : 0;
                if (liveMajor != bundledMajor)
                {
                    _logger.LogCritical(
                        "Store upgrade verification: the running server reports major {Live}, not the expected {Expected}. The data directory swap may not have taken.",
                        liveMajor, bundledMajor);
                }
                else
                {
                    _logger.LogInformation("Store upgrade verified: the running server is PostgreSQL major {Major}.", liveMajor);
                }
            }

            /* The same-major extension update (#1705). After a pg_upgrade the bridge already moved it, so
               this is a no-op there; on a TimescaleDB-only runtime bump it IS the whole upgrade. First
               statement on this fresh connection, for the same alone-first reason as the bridge. */
            string? installed;
            await using (var probe = new NpgsqlCommand(
                "SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'", connection))
            {
                installed = await probe.ExecuteScalarAsync(cancellationToken) as string;
            }

            var extensionMoved = false;

            /* An empty bundled version means the runtime carries no timescaledb.control to read — a
               plain-PostgreSQL bundle. Never "update" the extension toward a version we cannot name. */
            if (installed is not null
                && !string.IsNullOrEmpty(bundledTimescaleVersion)
                && !string.Equals(installed, bundledTimescaleVersion, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "The store's TimescaleDB is {Installed} but the bundled runtime ships {Bundled} — updating the extension now (#1705: a store otherwise keeps whatever extension it was created with, forever).",
                    installed, bundledTimescaleVersion);

                await using (var update = new NpgsqlCommand("ALTER EXTENSION timescaledb UPDATE", connection)
                {
                    CommandTimeout = (int)s_bridgeTimeout.TotalSeconds,
                })
                {
                    await update.ExecuteNonQueryAsync(cancellationToken);
                }

                await using var verify = new NpgsqlCommand(
                    "SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'", connection);
                var after = await verify.ExecuteScalarAsync(cancellationToken) as string;
                if (string.Equals(after, bundledTimescaleVersion, StringComparison.Ordinal))
                {
                    _logger.LogWarning("TimescaleDB extension updated: {From} -> {To}.", installed, after);
                    extensionMoved = true;
                }
                else
                {
                    _logger.LogCritical(
                        "TimescaleDB is {After} after ALTER EXTENSION UPDATE, but the bundled runtime ships {Bundled}. The store still runs, but its extension does not match its binaries — investigate before the next release.",
                        after ?? "(absent)", bundledTimescaleVersion);
                }

                outcome = outcome with
                {
                    Status = upgraded ? outcome.Status : StoreUpgradeStatus.ExtensionUpdated,
                    FromTimescale = outcome.FromTimescale ?? installed,
                    ToTimescale = bundledTimescaleVersion,
                };
            }

            if (upgraded)
            {
                await VerifySentinelReadAsync(connection, cancellationToken);
            }

            if (upgraded || extensionMoved)
            {
                _logger.LogInformation(
                    "Store runtime is current: PostgreSQL major {Major}, TimescaleDB {Timescale}.",
                    bundledMajor, installed is null ? "(not installed)" : bundledTimescaleVersion);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            /* Verification is diagnosis, not a gate: the data is already migrated and the server is up, so
               a failed check must not take collection down — it must be LOUD. */
            _logger.LogCritical(
                "Post-upgrade store verification failed ({Message}). The store is running; confirm its version and TimescaleDB state by hand.",
                ex.Message);
        }

        if (upgraded)
        {
            await RunAnalyzeInStagesAsync(newBinDirectory, port, userName, password, bundledMajor, cancellationToken);
        }

        return outcome;
    }

    /// <summary>
    /// Proves the upgraded store can actually be READ, not merely connected to — a collector table's row
    /// count exercises the restored catalog, the TimescaleDB chunk machinery behind a hypertable, and the
    /// search path in one query. A store that upgraded but cannot read its own history is the failure this
    /// exists to catch.
    /// </summary>
    private async Task VerifySentinelReadAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var exists = new NpgsqlCommand("SELECT to_regclass('collect.collection_log') IS NOT NULL", connection);
        if (await exists.ExecuteScalarAsync(cancellationToken) is not true)
        {
            _logger.LogWarning(
                "Post-upgrade sentinel read skipped: collect.collection_log does not exist yet (a store upgraded before its first migration).");
            return;
        }

        await using var count = new NpgsqlCommand("SELECT count(*) FROM collect.collection_log", connection);
        var rows = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken) ?? 0L, CultureInfo.InvariantCulture);
        _logger.LogInformation(
            "Post-upgrade sentinel read OK: collect.collection_log returned {Rows:N0} rows through the upgraded cluster.", rows);
    }

    /// <summary>
    /// The documented post-pg_upgrade statistics step. PostgreSQL 18 transfers most optimizer statistics, so
    /// this is the targeted follow-up its documentation calls for — <c>--missing-stats-only</c> touches only
    /// relations that came across without any, instead of re-analyzing a store that already has them.
    /// Best-effort: the store is up and correct with or without it, and a slow analyze must never look like
    /// a failed upgrade.
    /// </summary>
    private async Task RunAnalyzeInStagesAsync(
        string newBinDirectory, int port, string userName, string password, int bundledMajor, CancellationToken cancellationToken)
    {
        var vacuumdb = Path.Combine(newBinDirectory, "vacuumdb.exe");
        if (!File.Exists(vacuumdb))
        {
            _logger.LogWarning("vacuumdb.exe is not in the bundled runtime — skipping the post-upgrade analyze; the planner will catch up via autovacuum.");
            return;
        }

        var passFile = Path.Combine(Path.GetTempPath(), $"pm-darling-analyze-{Guid.NewGuid():N}.tmp");
        try
        {
            WritePgPassFile(passFile, userName, password);

            var arguments = new StringBuilder();
            arguments.Append("--host 127.0.0.1 --port ").Append(port.ToString(CultureInfo.InvariantCulture));
            arguments.Append(" --username ").Append(userName);
            arguments.Append(" --all --analyze-in-stages");
            if (bundledMajor >= 18)
            {
                /* --missing-stats-only arrived with the statistics-preserving pg_upgrade in 18; on anything
                   older the staged analyze has to do the whole store. */
                arguments.Append(" --missing-stats-only");
            }

            _logger.LogInformation("Running the post-upgrade analyze staging (statistics PostgreSQL {Major} did not carry across).", bundledMajor);

            var (exitCode, output) = await DarlingManagedPostgres.RunToolAsync(
                vacuumdb,
                arguments.ToString(),
                s_analyzeTimeout,
                cancellationToken,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PGPASSFILE"] = passFile });

            if (exitCode == 0)
            {
                _logger.LogInformation("Post-upgrade analyze complete.");
            }
            else
            {
                _logger.LogWarning(
                    "Post-upgrade analyze reported exit {ExitCode} ({Output}). The store is fully usable; autovacuum will build the remaining statistics.",
                    exitCode, output);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Post-upgrade analyze could not run ({Message}); autovacuum will build the statistics.", ex.Message);
        }
        finally
        {
            TryDeleteFile(passFile);
        }
    }
}
