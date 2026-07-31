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
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// #1832: Lite's data root used to BE the installer's install root, so re-running Setup.exe deleted the
/// store, the archive, the logs, and settings.json. The data now lives in a sibling directory and startup
/// moves it there once.
///
/// Two invariants carry the whole fix and both are easy to break by accident: the migration must never
/// overwrite anything already in the new root (that would destroy the data of anyone who already
/// migrated), and it must never move the updater's own files out of the install root (that would break
/// the installed app). Everything below is a temp-directory exercise of one of those.
/// </summary>
public class DataRootMigrationTests
{
    private static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pmlite_{tag}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Writes a file, creating any missing parent directories.</summary>
    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>The shape of a real pre-fix install: our data sitting alongside the updater's files.</summary>
    private static void PopulateLegacyRoot(string root)
    {
        WriteFile(root, Path.Combine("config", "settings.json"), "{\"alerts_enabled\":true}");
        WriteFile(root, Path.Combine("config", "servers.json"), "[]");
        WriteFile(root, Path.Combine("archive", "2026-07.parquet"), "archive-bytes");
        WriteFile(root, Path.Combine("logs", "lite_20260729.log"), "log-line");
        WriteFile(root, "monitor.duckdb", "duckdb-bytes");
        WriteFile(root, "monitor.duckdb.wal", "wal-bytes");
        WriteFile(root, "alert_state.json", "{}");

        /* The updater's files. These must survive untouched. */
        WriteFile(root, "Update.exe", "velopack");
        WriteFile(root, "velopack.log", "velopack");
        WriteFile(root, Path.Combine("current", "PerformanceMonitorLite.exe"), "app");
        WriteFile(root, Path.Combine("packages", "PerformanceMonitorLite-3.3.0-full.nupkg"), "pkg");
    }

    private static void AssertUpdaterFilesIntact(string legacyRoot)
    {
        Assert.True(File.Exists(Path.Combine(legacyRoot, "Update.exe")));
        Assert.True(File.Exists(Path.Combine(legacyRoot, "velopack.log")));
        Assert.True(File.Exists(Path.Combine(legacyRoot, "current", "PerformanceMonitorLite.exe")));
        Assert.True(File.Exists(Path.Combine(legacyRoot, "packages", "PerformanceMonitorLite-3.3.0-full.nupkg")));
    }

    /// <summary>A fresh install: nothing was ever written to the old location.</summary>
    [Fact]
    public void Migrate_IsANoOp_WhenTheLegacyRootDoesNotExist()
    {
        var parent = NewTempDir("noold");
        try
        {
            var legacy = Path.Combine(parent, "PerformanceMonitorLite");
            var target = Path.Combine(parent, "PerformanceMonitorLite-Data");
            var log = new List<string>();

            var result = DataRootMigration.Migrate(legacy, target, log.Add);

            Assert.Equal(DataRootMigrationStatus.NothingToMigrate, result.Status);
            Assert.Empty(result.Moved);
            Assert.Empty(log);
            /* The migration does not create the new root; App does that immediately afterwards. */
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    /// <summary>
    /// An install that has already been migrated once, or a Setup.exe install that never ran the old
    /// build: the install root exists and holds updater files, but none of ours.
    /// </summary>
    [Fact]
    public void Migrate_IsANoOp_WhenTheLegacyRootHoldsOnlyUpdaterFiles()
    {
        var parent = NewTempDir("updateronly");
        try
        {
            var legacy = Path.Combine(parent, "PerformanceMonitorLite");
            var target = Path.Combine(parent, "PerformanceMonitorLite-Data");
            WriteFile(legacy, "Update.exe", "velopack");
            WriteFile(legacy, Path.Combine("current", "PerformanceMonitorLite.exe"), "app");
            var log = new List<string>();

            var result = DataRootMigration.Migrate(legacy, target, log.Add);

            Assert.Equal(DataRootMigrationStatus.NothingToMigrate, result.Status);
            Assert.False(Directory.Exists(target));
            Assert.True(File.Exists(Path.Combine(legacy, "Update.exe")));
            Assert.True(File.Exists(Path.Combine(legacy, "current", "PerformanceMonitorLite.exe")));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    /// <summary>
    /// The upgrade case this whole change exists for: a populated old root, a new root that does not
    /// exist yet. Everything of ours moves, the updater's files stay, and a signpost is left behind.
    /// </summary>
    [Fact]
    public void Migrate_MovesEveryOwnedArtifact_AndLeavesTheUpdaterAlone()
    {
        var parent = NewTempDir("move");
        try
        {
            var legacy = Path.Combine(parent, "PerformanceMonitorLite");
            var target = Path.Combine(parent, "PerformanceMonitorLite-Data");
            PopulateLegacyRoot(legacy);
            var log = new List<string>();

            var result = DataRootMigration.Migrate(legacy, target, log.Add);

            Assert.Equal(DataRootMigrationStatus.Migrated, result.Status);
            Assert.Empty(result.Failed);

            /* Arrived, with contents. */
            Assert.Equal("duckdb-bytes", File.ReadAllText(Path.Combine(target, "monitor.duckdb")));
            Assert.Equal("wal-bytes", File.ReadAllText(Path.Combine(target, "monitor.duckdb.wal")));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(target, "alert_state.json")));
            Assert.Equal("{\"alerts_enabled\":true}", File.ReadAllText(Path.Combine(target, "config", "settings.json")));
            Assert.Equal("[]", File.ReadAllText(Path.Combine(target, "config", "servers.json")));
            Assert.Equal("archive-bytes", File.ReadAllText(Path.Combine(target, "archive", "2026-07.parquet")));
            Assert.Equal("log-line", File.ReadAllText(Path.Combine(target, "logs", "lite_20260729.log")));

            /* Departed. A move, not a copy - a leftover store would be deleted by the next Setup.exe run
               and would also double the disk footprint of a multi-GB store. */
            Assert.False(File.Exists(Path.Combine(legacy, "monitor.duckdb")));
            Assert.False(Directory.Exists(Path.Combine(legacy, "config")));
            Assert.False(Directory.Exists(Path.Combine(legacy, "archive")));
            Assert.False(Directory.Exists(Path.Combine(legacy, "logs")));

            /* The old root itself is NOT deleted - the updater owns it. */
            Assert.True(Directory.Exists(legacy));
            AssertUpdaterFilesIntact(legacy);

            var marker = Path.Combine(legacy, DataRootMigration.MarkerFileName);
            Assert.True(File.Exists(marker));
            Assert.Contains(target, File.ReadAllText(marker), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    /// <summary>
    /// Both roots complete: the new root is the live one and the old copy is stale. Nothing in the new root
    /// is touched — but the stale copy does not get to stay in the install directory either, because that
    /// is the folder Setup.exe deletes (#1842 review). It is quarantined under the new root instead.
    /// </summary>
    [Fact]
    public void Migrate_QuarantinesTheStaleCopy_WhenTheNewRootAlreadyHasAStore()
    {
        var parent = NewTempDir("both");
        try
        {
            var legacy = Path.Combine(parent, "PerformanceMonitorLite");
            var target = Path.Combine(parent, "PerformanceMonitorLite-Data");
            PopulateLegacyRoot(legacy);
            WriteFile(target, Path.Combine("config", "settings.json"), "{\"alerts_enabled\":false}");
            WriteFile(target, "monitor.duckdb", "current-duckdb");
            var log = new List<string>();

            var result = DataRootMigration.Migrate(legacy, target, log.Add);

            Assert.Equal(DataRootMigrationStatus.AlreadyMigrated, result.Status);
            Assert.Empty(result.Moved);
            Assert.Empty(result.Kept);

            /* The new root's copies win, untouched. */
            Assert.Equal("current-duckdb", File.ReadAllText(Path.Combine(target, "monitor.duckdb")));
            Assert.Equal("{\"alerts_enabled\":false}", File.ReadAllText(Path.Combine(target, "config", "settings.json")));

            /* Every one of ours is out of the deletable folder and readable in the quarantine. */
            var rescueDir = Path.Combine(target, DataRootMigration.RescuedDirName);
            Assert.Equal("duckdb-bytes", File.ReadAllText(Path.Combine(rescueDir, "monitor.duckdb")));
            Assert.Equal("archive-bytes", File.ReadAllText(Path.Combine(rescueDir, "archive", "2026-07.parquet")));
            Assert.Equal("{\"alerts_enabled\":true}", File.ReadAllText(Path.Combine(rescueDir, "config", "settings.json")));
            Assert.True(File.Exists(Path.Combine(rescueDir, "alert_state.json")));
            Assert.False(File.Exists(Path.Combine(legacy, "monitor.duckdb")));
            Assert.False(Directory.Exists(Path.Combine(legacy, "archive")));

            /* A stale archive\ must NOT quietly become the live install's archive just because the new root
               happened not to have one - that is why this path quarantines rather than merges. */
            Assert.False(Directory.Exists(Path.Combine(target, "archive")));
            Assert.False(File.Exists(Path.Combine(target, "alert_state.json")));

            AssertUpdaterFilesIntact(legacy);

            /* Silence here would strand data with no way to find out - the move has to say so, and the
               signpost points at where it went. */
            Assert.Single(log);
            Assert.Contains(rescueDir, File.ReadAllText(Path.Combine(legacy, DataRootMigration.MarkerFileName)), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    /// <summary>
    /// A mixed run: <c>monitor.duckdb</c> migrates normally while <c>config\</c> collides and is quarantined.
    /// The marker file is the only record that survives the session, and someone reads it because they are
    /// hunting for ONE artifact by name - so listing only what went live would tell the person looking for
    /// their settings.json that it was never touched.
    /// </summary>
    [Fact]
    public void Migrate_MarkerNamesTheQuarantinedArtifactsToo_NotJustTheOnesThatWentLive()
    {
        var parent = NewTempDir("markermix");
        try
        {
            var legacy = Path.Combine(parent, "PerformanceMonitorLite");
            var target = Path.Combine(parent, "PerformanceMonitorLite-Data");
            PopulateLegacyRoot(legacy);
            WriteFile(target, Path.Combine("config", "ignored_waits.json"), "[]");
            var log = new List<string>();

            var result = DataRootMigration.Migrate(legacy, target, log.Add);

            Assert.Contains("monitor.duckdb", result.Moved);
            Assert.Equal(new[] { "config" }, result.Rescued);

            var marker = File.ReadAllText(Path.Combine(legacy, DataRootMigration.MarkerFileName));
            Assert.Contains("monitor.duckdb", marker, StringComparison.Ordinal);
            Assert.Contains("config", marker, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(target, DataRootMigration.RescuedDirName), marker, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    /// <summary>
    /// The per-item loop's twin of <see cref="Migrate_EveryRescueFails_SaysSoInsteadOfClaimingAMove"/>, which
    /// only covered the already-populated branch. Every artifact collides with real content AND every rescue
    /// then fails, so moved/rescued/failed are all empty and only <c>kept</c> is not - a shape that used to
    /// hit an early return before the summary log ran.
    ///
    /// <para>Not TOTAL silence, as the review had it: <c>TryRescue</c> logs its own per-item failure either
    /// way. What the early return skipped is the line that says what to DO about it - that these are still in
    /// the folder Setup.exe deletes and want copying somewhere safe - which is the only actionable part and
    /// the only one that names the whole set. So the assertion is on that summary specifically; asserting
    /// merely that something was logged passes with the bug still in place, which is how this test was
    /// written the first time.</para>
    /// </summary>
    [Fact]
    public void Migrate_EveryItemCollidesAndEveryRescueFails_StillSaysWhatIsStillHere()
    {
        var parent = NewTempDir("silentstrand");
        try
        {
            var legacy = Path.Combine(parent, "PerformanceMonitorLite");
            var target = Path.Combine(parent, "PerformanceMonitorLite-Data");

            /* One pending artifact, colliding with real content, and its quarantine name already taken. */
            WriteFile(legacy, Path.Combine("config", "settings.json"), "{\"alerts_enabled\":true}");
            WriteFile(legacy, "Update.exe", "velopack");
            WriteFile(target, Path.Combine("config", "ignored_waits.json"), "[]");
            WriteFile(target, Path.Combine(DataRootMigration.RescuedDirName, "config", "taken.json"), "x");

            var log = new List<string>();
            var result = DataRootMigration.Migrate(legacy, target, log.Add);

            Assert.Equal(DataRootMigrationStatus.AlreadyMigrated, result.Status);
            Assert.Equal(new[] { "config" }, result.Kept);
            Assert.Empty(result.Moved);
            Assert.Empty(result.Rescued);

            /* The actionable summary must run even though nothing moved, rescued, or failed. */
            Assert.Contains(log, line => line.Contains("Left in", StringComparison.Ordinal)
                                      && line.Contains(legacy, StringComparison.Ordinal)
                                      && line.Contains("config", StringComparison.Ordinal)
                                      && line.Contains("Setup.exe deletes that folder", StringComparison.Ordinal));

            /* And the data really is still sitting there, which is why the log matters. */
            Assert.True(File.Exists(Path.Combine(legacy, "config", "settings.json")));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    /// <summary>
    /// The multi-LAUNCH hole (#1842 review, second pass). Launch 1 relocates some artifacts and fails on one,
    /// so it writes a marker naming what is still here. Launch 2 sees only the leftover pending, finishes it,
    /// and rewrites the marker - which must still name what launch 1 relocated, even though launch 2's own
    /// lists never contained it. Nothing but the filesystem remembers across launches, so the marker has to
    /// be read off the filesystem rather than off either run's bookkeeping.
    /// </summary>
    [Fact]
    public void Migrate_MarkerNamesArtifactsFromEarlierLaunches_NotJustThisRuns()
    {
        var parent = NewTempDir("multilaunch");
        try
        {
            var legacy = Path.Combine(parent, "PerformanceMonitorLite");
            var target = Path.Combine(parent, "PerformanceMonitorLite-Data");
            PopulateLegacyRoot(legacy);

            /* Launch 1: config collides with a seeded stand-in, so it is quarantined; a lock on archive\
               (simulated by an occupied quarantine name it cannot take, with a colliding target) leaves it
               behind. Everything else migrates. */
            WriteFile(target, Path.Combine("config", "ignored_waits.json"), "[]");
            WriteFile(target, Path.Combine("archive", "already-here.parquet"), "x");
            WriteFile(target, Path.Combine(DataRootMigration.RescuedDirName, "archive", "taken.parquet"), "x");

            var first = DataRootMigration.Migrate(legacy, target, _ => { });
            Assert.Contains("config", first.Rescued);
            Assert.Contains("archive", first.Kept);
            Assert.True(File.Exists(Path.Combine(legacy, DataRootMigration.MarkerFileName)));

            /* Launch 2: only the leftover is pending. Clear the collision so it can finish. */
            Directory.Delete(Path.Combine(target, "archive"), true);
            var second = DataRootMigration.Migrate(legacy, target, _ => { });
            Assert.Equal(new[] { "archive" }, second.Moved);
            Assert.Empty(second.Rescued);

            /* The rewritten marker must still name config, which only launch 1 ever touched. */
            var marker = File.ReadAllText(Path.Combine(legacy, DataRootMigration.MarkerFileName));
            Assert.Contains("config", marker, StringComparison.Ordinal);
            Assert.Contains("archive", marker, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(target, DataRootMigration.RescuedDirName), marker, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    /// <summary>
    /// Every rescue in the already-populated branch fails (the quarantine names are all taken by an earlier
    /// partial run). The log must NOT claim a move that did not happen: this runs before the logger exists,
    /// so it is the only channel out, and an operator told "moved it to the quarantine" who then finds an
    /// empty folder stops looking - with their data still in the folder Setup.exe deletes.
    /// </summary>
    [Fact]
    public void Migrate_EveryRescueFails_SaysSoInsteadOfClaimingAMove()
    {
        var parent = NewTempDir("rescuefail");
        try
        {
            var legacy = Path.Combine(parent, "PerformanceMonitorLite");
            var target = Path.Combine(parent, "PerformanceMonitorLite-Data");
            PopulateLegacyRoot(legacy);
            WriteFile(target, Path.Combine("config", "settings.json"), "{\"alerts_enabled\":false}");
            WriteFile(target, "monitor.duckdb", "current-duckdb");

            /* An earlier partial run already claimed every quarantine name, so no rescue can land. */
            var rescueDir = Path.Combine(target, DataRootMigration.RescuedDirName);
            foreach (var name in new[] { "config", "archive", "logs" })
            {
                Directory.CreateDirectory(Path.Combine(rescueDir, name));
            }
            foreach (var name in new[] { "monitor.duckdb", "monitor.duckdb.wal", "alert_state.json" })
            {
                WriteFile(rescueDir, name, "earlier-run");
            }

            var log = new List<string>();
            var result = DataRootMigration.Migrate(legacy, target, log.Add);

            Assert.Equal(DataRootMigrationStatus.AlreadyMigrated, result.Status);
            Assert.Empty(result.Rescued);
            Assert.NotEmpty(result.Kept);

            /* Nothing moved, so nothing may claim it moved - and no marker may say the folder is done with. */
            var joined = string.Join("\n", log);
            Assert.DoesNotContain("Moved it to", joined, StringComparison.Ordinal);
            Assert.Contains("could not be moved out of there", joined, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(legacy, DataRootMigration.MarkerFileName)));

            /* And the data is exactly where it was, untouched. */
            Assert.Equal("duckdb-bytes", File.ReadAllText(Path.Combine(legacy, "monitor.duckdb")));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    /// <summary>
    /// The failure the #1842 review found: <c>monitor.duckdb</c>'s move fails once, the app opens DuckDB at
    /// the new root and a fresh EMPTY database appears there, and from then on bare existence reads as
    /// "already migrated" — with the real multi-gigabyte store still in the folder Setup.exe deletes.
    /// An empty target has nothing to lose, so the real store takes its place.
    /// </summary>
    [Fact]
    public void Migrate_ReplacesAnEmptyPlaceholder_RatherThanStrandingTheRealArtifact()
    {
        var parent = NewTempDir("placeholder");
        try
        {
            var legacy = Path.Combine(parent, "PerformanceMonitorLite");
            var target = Path.Combine(parent, "PerformanceMonitorLite-Data");
            PopulateLegacyRoot(legacy);

            /* What last session left behind: a zero-byte store file and an empty config directory. */
            WriteFile(target, "monitor.duckdb", "");
            Directory.CreateDirectory(Path.Combine(target, "config"));
            var log = new List<string>();

            var result = DataRootMigration.Migrate(legacy, target, log.Add);

            Assert.Equal(DataRootMigrationStatus.Migrated, result.Status);
            Assert.Contains("monitor.duckdb", result.Moved);
            Assert.Contains("config", result.Moved);
            Assert.Empty(result.Kept);
            Assert.Empty(result.Rescued);

            Assert.Equal("duckdb-bytes", File.ReadAllText(Path.Combine(target, "monitor.duckdb")));
            Assert.Equal("{\"alerts_enabled\":true}", File.ReadAllText(Path.Combine(target, "config", "settings.json")));
            Assert.False(File.Exists(Path.Combine(legacy, "monitor.duckdb")));
            AssertUpdaterFilesIntact(legacy);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    /// <summary>An old root that only ever got partway through a first run: move what is actually there.</summary>
    [Fact]
    public void Migrate_MovesOnlyWhatExists_WhenTheLegacyRootIsPartial()
    {
        var parent = NewTempDir("partial");
        try
        {
            var legacy = Path.Combine(parent, "PerformanceMonitorLite");
            var target = Path.Combine(parent, "PerformanceMonitorLite-Data");
            WriteFile(legacy, Path.Combine("config", "settings.json"), "{}");
            WriteFile(legacy, "Update.exe", "velopack");
            var log = new List<string>();

            var result = DataRootMigration.Migrate(legacy, target, log.Add);

            Assert.Equal(DataRootMigrationStatus.Migrated, result.Status);
            Assert.Equal(new[] { "config" }, result.Moved);
            Assert.True(File.Exists(Path.Combine(target, "config", "settings.json")));
            Assert.False(Directory.Exists(Path.Combine(legacy, "config")));
            Assert.False(File.Exists(Path.Combine(target, "monitor.duckdb")));
            Assert.True(File.Exists(Path.Combine(legacy, "Update.exe")));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    /// <summary>
    /// The self-heal case. A previous run moved config\ and then died before the store followed (a locked
    /// file, a killed process). The new root now has settings.json but no store, and the store is still
    /// sitting in the folder the next Setup.exe deletes. Finish the job.
    /// </summary>
    [Fact]
    public void Migrate_FinishesAnInterruptedRun_WithoutOverwritingWhatAlreadyLanded()
    {
        var parent = NewTempDir("resume");
        try
        {
            var legacy = Path.Combine(parent, "PerformanceMonitorLite");
            var target = Path.Combine(parent, "PerformanceMonitorLite-Data");
            PopulateLegacyRoot(legacy);
            WriteFile(target, Path.Combine("config", "settings.json"), "{\"already\":\"moved\"}");
            var log = new List<string>();

            var result = DataRootMigration.Migrate(legacy, target, log.Add);

            Assert.Equal(DataRootMigrationStatus.Migrated, result.Status);

            /* The store follows its settings across. */
            Assert.Equal("duckdb-bytes", File.ReadAllText(Path.Combine(target, "monitor.duckdb")));
            Assert.False(File.Exists(Path.Combine(legacy, "monitor.duckdb")));

            /* The already-landed config is NOT clobbered by the old one - but the old one does not stay in
               the install directory either; it is quarantined where Setup.exe cannot reach it. */
            Assert.Equal("{\"already\":\"moved\"}", File.ReadAllText(Path.Combine(target, "config", "settings.json")));
            Assert.Contains("config", result.Rescued);
            Assert.Empty(result.Kept);
            Assert.False(Directory.Exists(Path.Combine(legacy, "config")));
            Assert.Equal(
                "{\"alerts_enabled\":true}",
                File.ReadAllText(Path.Combine(target, DataRootMigration.RescuedDirName, "config", "settings.json")));

            AssertUpdaterFilesIntact(legacy);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    /// <summary>
    /// The likelier half of the #1842 review's finding: <c>config\</c>'s move fails, and <c>App.xaml.cs</c>
    /// runs <c>ConfigSeeder.SeedMissing</c> straight afterwards regardless, filling a NEW config directory
    /// at the new root with defaults that do not include <c>settings.json</c>. That directory is not empty,
    /// so it is not a placeholder — but the user's real settings must still leave the deletable folder.
    /// </summary>
    [Fact]
    public void Migrate_RescuesTheRealConfig_WhenASeededStandInTookItsPlace()
    {
        var parent = NewTempDir("seeded");
        try
        {
            var legacy = Path.Combine(parent, "PerformanceMonitorLite");
            var target = Path.Combine(parent, "PerformanceMonitorLite-Data");
            PopulateLegacyRoot(legacy);

            /* What ConfigSeeder leaves: the defaults it knows about, and no settings.json. */
            WriteFile(target, Path.Combine("config", "ignored_waits.json"), "[]");
            var log = new List<string>();

            var result = DataRootMigration.Migrate(legacy, target, log.Add);

            Assert.Equal(DataRootMigrationStatus.Migrated, result.Status);
            Assert.Equal(new[] { "config" }, result.Rescued);
            Assert.Empty(result.Kept);

            /* The stand-in stays live, untouched... */
            Assert.Equal("[]", File.ReadAllText(Path.Combine(target, "config", "ignored_waits.json")));
            Assert.False(File.Exists(Path.Combine(target, "config", "settings.json")));

            /* ...and the real settings are out of the install directory and still readable. */
            Assert.Equal(
                "{\"alerts_enabled\":true}",
                File.ReadAllText(Path.Combine(target, DataRootMigration.RescuedDirName, "config", "settings.json")));
            Assert.False(Directory.Exists(Path.Combine(legacy, "config")));

            /* Everything that did not collide migrated normally. */
            Assert.Contains("monitor.duckdb", result.Moved);
            AssertUpdaterFilesIntact(legacy);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    /// <summary>
    /// A self-move would be a rename of a folder onto itself. Guarded, because getting it wrong destroys
    /// the very data this class exists to protect.
    /// </summary>
    [Fact]
    public void Migrate_RefusesToMoveARootOntoItself()
    {
        var root = NewTempDir("same");
        try
        {
            PopulateLegacyRoot(root);
            var log = new List<string>();

            var result = DataRootMigration.Migrate(root, root + Path.DirectorySeparatorChar, log.Add);

            Assert.Equal(DataRootMigrationStatus.NothingToMigrate, result.Status);
            Assert.True(File.Exists(Path.Combine(root, "monitor.duckdb")));
            Assert.True(Directory.Exists(Path.Combine(root, "config")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
