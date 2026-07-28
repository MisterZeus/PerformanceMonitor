/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// Names the directories sitting in the INSTALL directory that are not part of the product's layout,
/// with what they cost in disk — and deletes none of them, ever.
///
/// <para><b>Why this exists separately from the store-copy report.</b> #1775 reports store-shaped
/// directories beside the DATA directory, which lives under <c>%ProgramData%\PerformanceMonitorDarling</c>.
/// A production field instance's seven hand-made directories are under the INSTALL directory — a different
/// parent entirely — so that report was structurally incapable of seeing them and stayed silent. They are
/// also almost certainly not store copies: seven copies of a ~280 GB store cannot fit on a 500 GB volume,
/// so they are small hand-made snapshots carrying no <c>PG_VERSION</c>, invisible to a store-shaped test
/// even in the right parent. Different class, different place, so: different report.</para>
///
/// <para><b>This report carries no store semantics.</b> It cannot say what a directory is, only that the
/// product did not put it there — so it says exactly that and nothing more. No cluster language, no
/// which-one-is-live warnings; those belong to the store-copy report, where they are true.</para>
///
/// <para><b>It never deletes.</b> Same absolutism as the store-copy report, and for a stronger reason here:
/// this report identifies by ELIMINATION against a known layout rather than by any positive structural
/// test, so a directory it cannot account for may be anything at all — an operator's snapshot, a deploy
/// tool's staging folder, someone's notes. Reporting is the only verdict that stays harmless when the
/// classification is wrong, and here it will sometimes be wrong by construction.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DarlingInstallDirectoryReport
{
    /// <summary>
    /// Its own budget rather than one shared with the store-copy report, deliberately. The two scan
    /// disjoint trees for different reasons, and a single multi-hundred-GB store copy is quite capable of
    /// spending a shared budget by itself — which would leave this report's directories unmeasured because
    /// of work done somewhere else entirely. Bounding each report separately costs a worse case of ten
    /// seconds instead of five at startup, and buys that one report's cost can never degrade another
    /// report's content.
    /// </summary>
    private static readonly TimeSpan s_sizeProbeBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The directories the product's own layout puts at the top level of the install directory.
    ///
    /// <para>Enumerated from the shipped layout rather than remembered, and each entry is cited:</para>
    /// <list type="bullet">
    /// <item><c>viewer</c> — the packaging step copies the viewer publish into it
    /// (<c>.github/workflows/build.yml</c> "Stage Darling for signing", and the same layout in
    /// <c>nightly.yml</c>).</item>
    /// <item><c>runtimes</c> — .NET's native-asset directory, present in a measured
    /// <c>dotnet publish</c> of the service.</item>
    /// <item><c>wwwroot</c> — the web dashboard's static assets, a csproj <c>Content</c> copy
    /// (<c>PerformanceMonitor.Darling.Service.csproj</c>).</item>
    /// <item><c>pg-runtime</c> — extracted from <c>pg-runtime.zip</c> beside the service exe on first run
    /// (<c>DarlingManagedPostgres</c>, <c>AppContext.BaseDirectory</c> + "pg-runtime").</item>
    /// <item><c>pg-runtime-prev</c> — the rescued previous runtime
    /// (<c>DarlingStoreUpgrade.PreviousRuntimeRootFor</c>, which appends
    /// <see cref="DarlingStoreUpgrade.PreviousRuntimeSuffix"/> to the runtime root).</item>
    /// </list>
    ///
    /// <para>Satellite-resource directories (<c>cs</c>, <c>de</c>, <c>ja</c>, <c>zh-Hans</c> and the rest)
    /// are deliberately NOT listed: see <see cref="IsSatelliteResourceDirectory"/>. The service log
    /// directory is not listed either, and that is not an omission — logs live under
    /// <c>%ProgramData%\PerformanceMonitorDarling\logs</c>, not here (<c>uninstall-darling.ps1</c> says so
    /// explicitly).</para>
    /// </summary>
    private static readonly string[] s_productDirectories =
    [
        "viewer",
        "runtimes",
        "wwwroot",
        "pg-runtime",
        "pg-runtime" + DarlingStoreUpgrade.PreviousRuntimeSuffix,
    ];

    /// <summary>
    /// Reports every top-level directory of <paramref name="installDirectory"/> that the product's layout
    /// does not account for. Never throws: a report that cannot be produced must not cost the service its
    /// start.
    /// </summary>
    internal static void Report(string installDirectory, ILogger logger)
        => Report(installDirectory, logger, s_sizeProbeBudget);

    /// <summary>
    /// The budget is a parameter here purely so a test can force the exhausted path, which is otherwise
    /// unreachable without a directory large enough to spend five real seconds. It has to be reachable: the
    /// property that a spent budget degrades a directory's SIZE and never drops the directory itself is the
    /// one this report cannot afford to get wrong, and a pin that cannot enter the branch does not pin it.
    /// Production has exactly one caller, and it passes <see cref="s_sizeProbeBudget"/>.
    /// </summary>
    internal static void Report(string installDirectory, ILogger logger, TimeSpan sizeProbeBudget)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
            if (!Directory.Exists(root))
            {
                return;
            }

            var deadline = DateTime.UtcNow + sizeProbeBudget;
            var found = new List<(string Path, long Bytes, bool Measured)>();

            foreach (var candidate in Directory.GetDirectories(root))
            {
                if (IsProductDirectory(candidate) || IsSatelliteResourceDirectory(candidate))
                {
                    continue;
                }

                var bytes = DarlingStoreUpgrade.MeasureDirectoryBytes(candidate, deadline, out var measured);
                found.Add((candidate, bytes, measured));
            }

            if (found.Count == 0)
            {
                return;
            }

            long total = 0;
            var allMeasured = true;
            foreach (var (_, bytes, measured) in found)
            {
                total += bytes;
                allMeasured &= measured;
            }

            found.Sort(static (left, right) => right.Bytes.CompareTo(left.Bytes));

            logger.LogWarning(
                "{Count} director(ies) in the install directory {Root} are not part of the product's layout, and are holding {Approximately}{Size}. NONE of them is deleted automatically — the product does not know what they are, only that it did not put them there. Remove the ones you no longer need: a major store upgrade in copy mode needs roughly twice the data directory in free space.",
                found.Count, root, allMeasured ? string.Empty : "at least ", DarlingStoreUpgrade.FormatBytes(total));

            foreach (var (path, bytes, measured) in found)
            {
                /* An exhausted budget degrades a directory's SIZE and never its presence in this list. The
                   whole point of the report is that the operator learns these exist; letting a slow walk
                   silence one would be the report failing at the only job it has. */
                if (!measured)
                {
                    logger.LogWarning(
                        "Directory not part of the product's layout: {Path} (at least {Size}; the {Budget}-second size probe did not finish walking it). It is never deleted automatically.",
                        path, DarlingStoreUpgrade.FormatBytes(bytes), (int)sizeProbeBudget.TotalSeconds);
                    continue;
                }

                logger.LogWarning(
                    "Directory not part of the product's layout: {Path} ({Size}). It is never deleted automatically.",
                    path, DarlingStoreUpgrade.FormatBytes(bytes));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Could not check the install directory {Root} for directories outside the product's layout ({Message}).",
                installDirectory, ex.Message);
        }
    }

    private static bool IsProductDirectory(string candidate)
    {
        var name = Path.GetFileName(candidate);
        foreach (var known in s_productDirectories)
        {
            if (string.Equals(name, known, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True for a .NET satellite-resource directory — one holding nothing but <c>*.resources.dll</c>.
    ///
    /// <para>Structural rather than a list of culture names, and that is the load-bearing choice. A
    /// measured publish ships thirteen of them today (<c>cs</c>, <c>de</c>, <c>es</c>, <c>fr</c>,
    /// <c>it</c>, <c>ja</c>, <c>ko</c>, <c>pl</c>, <c>pt-BR</c>, <c>ru</c>, <c>tr</c>, <c>zh-Hans</c>,
    /// <c>zh-Hant</c>), but that set is decided by whichever cultures our DEPENDENCIES localize into — a
    /// package update can add one without a line of our code changing. Hardcoding the thirteen would mean
    /// the product starts reporting its own shipped directory as something it did not install, which is the
    /// exact credibility failure this whole report has to avoid. Every one of the thirteen was verified to
    /// contain only <c>.resources.dll</c> files and nothing else.</para>
    ///
    /// <para>An EMPTY directory is not one of these. Something the product shipped would have files in it,
    /// so an empty directory is left to be reported — which is the safe direction: it is named, not
    /// deleted.</para>
    /// </summary>
    private static bool IsSatelliteResourceDirectory(string candidate)
    {
        try
        {
            var any = false;
            foreach (var file in Directory.EnumerateFiles(candidate, "*", SearchOption.AllDirectories))
            {
                any = true;
                if (!file.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return any;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            /* Unreadable means unclassifiable, and unclassifiable must fall to REPORTED rather than
               silently excused — a directory the service cannot even enumerate is more worth an
               operator's attention, not less. */
            return false;
        }
    }
}
