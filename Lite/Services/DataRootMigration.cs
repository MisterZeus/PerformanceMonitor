/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// What <see cref="DataRootMigration.Migrate"/> did.
/// </summary>
internal enum DataRootMigrationStatus
{
    /// <summary>The legacy root is absent, or holds none of Lite's artifacts. A fresh install lands here.</summary>
    NothingToMigrate,

    /// <summary>The new root already holds a complete store, so the legacy root was left entirely alone.</summary>
    AlreadyMigrated,

    /// <summary>Every artifact Lite owns in the legacy root moved across.</summary>
    Migrated,

    /// <summary>At least one move failed. What failed stays in the legacy root and is retried next launch.</summary>
    PartiallyMigrated
}

/// <summary>
/// The outcome of one migration attempt. <see cref="Kept"/> names artifacts that existed in BOTH roots —
/// the new root's copy wins and the legacy one is never touched.
/// </summary>
internal sealed class DataRootMigrationResult
{
    public DataRootMigrationStatus Status { get; init; }
    public IReadOnlyList<string> Moved { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Kept { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Failed { get; init; } = Array.Empty<string>();
}

/// <summary>
/// #1832: moves Lite's per-user data out of <c>%LOCALAPPDATA%\PerformanceMonitorLite</c> — which is also
/// Velopack's install root — into the sibling <c>%LOCALAPPDATA%\PerformanceMonitorLite-Data</c>.
///
/// Re-running Setup.exe over an existing install renames the install root aside and deletes it, so every
/// release installed that way destroyed the DuckDB store, the Parquet archive, the logs, and settings.json.
/// In-app updates never did (Velopack updates the <c>current\</c> subfolder in place), which is why the
/// data loss looked random.
///
/// The migration is deliberately an explicit allow-list rather than "move the folder contents": the legacy
/// root ALSO holds Velopack's own <c>Update.exe</c>, <c>current\</c>, <c>packages\</c> and
/// <c>velopack.log</c>. Moving those would break the installed app and its updater. Only the artifacts
/// listed here belong to us, and only those move.
/// </summary>
internal static class DataRootMigration
{
    /// <summary>The Velopack install root, which is where data used to live.</summary>
    internal const string LegacyRootName = "PerformanceMonitorLite";

    /// <summary>The new per-user data root. A LOCAL sibling — the DuckDB store must never roam.</summary>
    internal const string DataRootName = "PerformanceMonitorLite-Data";

    /// <summary>Signpost left in the legacy root so someone browsing it can find their data.</summary>
    internal const string MarkerFileName = "DATA-MOVED.txt";

    /// <summary>
    /// Directories Lite owns under its data root. <c>monitor.duckdb.tmp</c> is DuckDB's scratch directory —
    /// it only survives an unclean shutdown, and it belongs with the database file it spilled for.
    /// </summary>
    private static readonly string[] s_directories = { "config", "archive", "logs", "monitor.duckdb.tmp" };

    /// <summary>Files Lite owns directly under its data root.</summary>
    private static readonly string[] s_files = { "monitor.duckdb", "monitor.duckdb.wal", "alert_state.json" };

    /// <summary>
    /// The two artifacts that make a root "the live install": user settings and the store. If BOTH are
    /// already in the new root, a previous launch finished the job and the legacy root is stale.
    /// </summary>
    private static bool HasCompleteStore(string root) =>
        File.Exists(Path.Combine(root, "config", "settings.json"))
        && File.Exists(Path.Combine(root, "monitor.duckdb"));

    /// <summary>
    /// Moves everything Lite owns from <paramref name="legacyRoot"/> to <paramref name="newRoot"/>, then
    /// leaves a marker behind. Never deletes the legacy root itself — Velopack owns it.
    ///
    /// Nothing in the new root is ever overwritten: an artifact present in both roots is left in both, with
    /// the new root's copy live. Per-artifact rather than all-or-nothing, so a run interrupted halfway (a
    /// locked store file, a killed process) finishes on the next launch instead of stranding the store in a
    /// folder the next Setup.exe will delete.
    ///
    /// Runs before the logger is initialized, so <paramref name="log"/> is the only channel out. Never
    /// throws: a failed migration must not stop the app from starting.
    /// </summary>
    internal static DataRootMigrationResult Migrate(string legacyRoot, string newRoot, Action<string> log)
    {
        var moved = new List<string>();
        var kept = new List<string>();
        var failed = new List<string>();

        try
        {
            if (!Directory.Exists(legacyRoot)
                || string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(newRoot)),
                    StringComparison.OrdinalIgnoreCase))
            {
                return new DataRootMigrationResult { Status = DataRootMigrationStatus.NothingToMigrate };
            }

            var pending = new List<(string Name, bool IsDirectory)>();
            foreach (var name in s_directories)
            {
                if (Directory.Exists(Path.Combine(legacyRoot, name))) pending.Add((name, true));
            }
            foreach (var name in s_files)
            {
                if (File.Exists(Path.Combine(legacyRoot, name))) pending.Add((name, false));
            }

            if (pending.Count == 0)
            {
                return new DataRootMigrationResult { Status = DataRootMigrationStatus.NothingToMigrate };
            }

            if (HasCompleteStore(newRoot))
            {
                log($"Data root '{newRoot}' is already populated, so the copy still in '{legacyRoot}' was left " +
                    $"untouched and is NOT in use ({string.Join(", ", Names(pending))}). Delete it by hand once " +
                    "you are satisfied nothing is missing.");
                return new DataRootMigrationResult
                {
                    Status = DataRootMigrationStatus.AlreadyMigrated,
                    Kept = Names(pending)
                };
            }

            Directory.CreateDirectory(newRoot);

            foreach (var (name, isDirectory) in pending)
            {
                var source = Path.Combine(legacyRoot, name);
                var target = Path.Combine(newRoot, name);

                if (isDirectory ? Directory.Exists(target) : File.Exists(target))
                {
                    kept.Add(name);
                    continue;
                }

                try
                {
                    if (isDirectory)
                    {
                        Directory.Move(source, target);
                    }
                    else
                    {
                        File.Move(source, target);
                    }

                    moved.Add(name);
                }
                catch (Exception ex)
                {
                    failed.Add(name);
                    log($"Could not move '{source}' to '{target}': {ex.Message}. It stays where it is and the " +
                        "move is retried on the next start.");
                }
            }

            if (moved.Count == 0 && failed.Count == 0)
            {
                return new DataRootMigrationResult
                {
                    Status = DataRootMigrationStatus.AlreadyMigrated,
                    Kept = kept
                };
            }

            if (moved.Count > 0)
            {
                log($"Moved Lite's data out of the install directory '{legacyRoot}' and into '{newRoot}' (#1832): " +
                    $"{string.Join(", ", moved)}. Re-running Setup.exe deletes the install directory, which is why " +
                    "data kept there did not survive an installer upgrade.");
            }

            if (kept.Count > 0)
            {
                log($"Left in '{legacyRoot}' because '{newRoot}' already had a copy: {string.Join(", ", kept)}.");
            }

            /* Only claim the move in the marker once nothing is still behind — a partial run retries next
               launch and writes the marker then. */
            if (failed.Count == 0)
            {
                TryWriteMarker(legacyRoot, newRoot, moved, log);
            }

            return new DataRootMigrationResult
            {
                Status = failed.Count > 0 ? DataRootMigrationStatus.PartiallyMigrated : DataRootMigrationStatus.Migrated,
                Moved = moved,
                Kept = kept,
                Failed = failed
            };
        }
        catch (Exception ex)
        {
            log($"Data directory migration failed: {ex.Message}. Lite starts against '{newRoot}' regardless; " +
                $"anything left in '{legacyRoot}' is untouched.");
            return new DataRootMigrationResult
            {
                Status = failed.Count > 0 || moved.Count > 0
                    ? DataRootMigrationStatus.PartiallyMigrated
                    : DataRootMigrationStatus.NothingToMigrate,
                Moved = moved,
                Kept = kept,
                Failed = failed
            };
        }
    }

    private static string[] Names(List<(string Name, bool IsDirectory)> items)
    {
        var names = new string[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            names[i] = items[i].Name;
        }

        return names;
    }

    /// <summary>
    /// Drops a plain-text signpost in the legacy root. The old root is NOT deleted — it is the Velopack
    /// install directory and still holds Update.exe, current\ and packages\.
    /// </summary>
    private static void TryWriteMarker(string legacyRoot, string newRoot, List<string> moved, Action<string> log)
    {
        try
        {
            var marker = Path.Combine(legacyRoot, MarkerFileName);
            var text = new StringBuilder();
            text.AppendLine("Performance Monitor Lite data has MOVED.");
            text.AppendLine();
            text.AppendLine("This folder is the application install directory. The installer owns it: running");
            text.AppendLine("Setup.exe again renames it aside and deletes it, which used to destroy the");
            text.AppendLine("monitoring history stored here (issue #1832).");
            text.AppendLine();
            text.AppendLine("Your data now lives in:");
            text.AppendLine();
            text.AppendLine("    " + newRoot);
            text.AppendLine();
            text.Append("Moved on ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)).AppendLine(":");
            foreach (var name in moved)
            {
                text.Append("    ").AppendLine(name);
            }

            text.AppendLine();
            text.AppendLine("Nothing was deleted. This file is only a signpost and is safe to remove.");

            File.WriteAllText(marker, text.ToString());
        }
        catch (Exception ex)
        {
            /* The signpost is a courtesy. Failing to write it must not fail the migration that already
               succeeded. */
            log($"Could not write '{MarkerFileName}' in '{legacyRoot}': {ex.Message}");
        }
    }
}
