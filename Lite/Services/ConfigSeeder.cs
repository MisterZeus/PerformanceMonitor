/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;
using System.IO;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// Seeds the per-user config directory (%LOCALAPPDATA%\PerformanceMonitorLite-Data\config) from the config
/// files bundled next to the exe, on first run. Without this a fresh install / nightly extract starts
/// with an empty config dir, so the per-user ignored_wait_types.json never exists and wait-stat
/// filtering silently becomes a no-op — every benign wait (SOS_WORK_DISPATCHER, DISPATCHER_QUEUE_SEMAPHORE,
/// CLR_AUTO_EVENT, ...) then floods collection and the wait stats tab (#1240).
/// </summary>
public static class ConfigSeeder
{
    /// <summary>
    /// Copies each named file from <paramref name="bundledDir"/> into <paramref name="userDir"/> when
    /// the destination does not already exist. Existing (user-edited) files are never overwritten.
    /// Per-file failures are swallowed so a locked/unreadable bundle file can't fault startup. Returns
    /// the number of files actually seeded.
    /// </summary>
    public static int SeedMissing(string bundledDir, string userDir, IEnumerable<string> fileNames)
    {
        var seeded = 0;
        if (string.IsNullOrEmpty(bundledDir) || string.IsNullOrEmpty(userDir) || !Directory.Exists(bundledDir))
        {
            return seeded;
        }

        foreach (var name in fileNames)
        {
            var src = Path.Combine(bundledDir, name);
            var dest = Path.Combine(userDir, name);

            // Never clobber a file the user may have edited, and skip anything not in the bundle.
            if (File.Exists(dest) || !File.Exists(src))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(userDir);
                File.Copy(src, dest);
                seeded++;
            }
            catch
            {
                /* Non-fatal: LoadIgnoredWaitTypes falls back to the bundled copy if seeding fails. */
            }
        }

        return seeded;
    }
}
