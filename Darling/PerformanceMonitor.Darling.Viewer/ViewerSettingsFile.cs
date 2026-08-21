/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Text.Json;
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The one read and the one write behind both of the viewer's per-user JSON settings files —
/// <see cref="ViewerAppSettingsStore"/> (viewer-settings.json) and <see cref="ViewerPreferencesStore"/>
/// (viewer-preferences.json) — sitting on the shared <see cref="SettingsFileGuard"/> (#2434).
///
/// <para>Both stores previously ended their load in a bare <c>catch</c> whose only output was a
/// <see cref="System.Diagnostics.Debug"/> trace. <c>Debug.WriteLine</c> carries
/// <see cref="System.Diagnostics.ConditionalAttribute"/>("DEBUG") and is removed by the compiler from a
/// Release build, so in the viewer anyone actually runs, a settings file that could not be read produced
/// no record at all — not a log line, not a dialog, nothing. And neither Save merged: each serialized its
/// whole in-memory object over the file, so a load that had silently fallen back to defaults, followed by
/// any save at all, replaced every setting in the file with a default. Changing the time-display dropdown
/// once was enough.</para>
///
/// <para>So there are two rules here, and the second is the one that turns an annoyance into data loss.
/// A file that is present and unreadable is always reported, to the log the viewer now has
/// (<see cref="ViewerLogger"/> — the "the viewer writes no application log of its own" comment these
/// stores carried predates it). And nothing replaces a file it could not read until a copy of it exists:
/// when even the copy cannot be made, the save is refused and says so, because leaving the file alone
/// beats replacing it when the alternative is permanent.</para>
///
/// <para>An ABSENT file goes through both paths in total silence. A first run has nothing to preserve and
/// nothing to explain, and a warning there would be pure noise — keeping absent apart from unreadable is
/// half the reason the guard exists.</para>
/// </summary>
internal static class ViewerSettingsFile
{
    /// <summary>
    /// Reads <paramref name="filePath"/> into <typeparamref name="T"/>, substituting a default instance
    /// when there is nothing usable to read. The returned <c>State</c> is what a caller needs to decide
    /// whether the defaults it just got are a legitimate first run or a configuration that is still on
    /// disk and could not be understood; the log line for the second case is written here, so a call site
    /// that never looks at the state is still not silent.
    /// </summary>
    internal static SettingsObjectRead<T> Load<T>(string filePath, string logSource, JsonSerializerOptions options)
        where T : class, new()
    {
        var read = SettingsFileGuard.ReadObject<T>(filePath, options);

        if (read.State == SettingsFileState.Unreadable)
        {
            ViewerLogger.Error(logSource,
                $"'{filePath}' could not be read ({read.Problem}), so every setting it holds is at its " +
                "default for this session. The file has not been changed; the next save copies it aside " +
                "before replacing it.");
        }

        return read.Value is null ? read with { Value = new T() } : read;
    }

    /// <summary>
    /// Serializes <paramref name="value"/> over <paramref name="filePath"/>, and returns whether it
    /// actually reached disk.
    ///
    /// <para>The bool is the point. This is a whole-object replacement, so a save that fails silently and
    /// a save that worked are indistinguishable to a caller that cannot ask — which is how a UI ends up
    /// saying it saved something it did not. Every failure here is logged AND reported, and the two call
    /// sites that persist a setting from an ordinary click surface it to the user rather than dropping
    /// it into a log nobody reads after a dialog said it worked.</para>
    ///
    /// <para>It returns false rather than throwing because the handlers that call it — a dropdown
    /// selection change, a sidebar group collapsing — do not wrap it, and a full disk is not a reason to
    /// take the viewer down.</para>
    /// </summary>
    internal static bool Save<T>(string filePath, T value, string logSource, JsonSerializerOptions options)
    {
        var permit = SettingsFileGuard.PermitReplace(filePath, DateTime.Now);

        if (!permit.Allowed)
        {
            ViewerLogger.Error(logSource,
                $"'{filePath}' could not be read ({permit.Problem}) and no copy of it could be made, so " +
                "it has been left untouched rather than overwritten with defaults, and nothing was saved. " +
                "Fix the file, or move it aside by hand, and try again.");
            return false;
        }

        if (permit.QuarantinedTo is not null)
        {
            ViewerLogger.Warn(logSource,
                $"'{Path.GetFileName(filePath)}' could not be read ({permit.Problem}), so this save " +
                "rewrites it from defaults. The unreadable original was copied to " +
                $"'{Path.GetFileName(permit.QuarantinedTo)}' first — the settings it held are recoverable " +
                "from there.");
        }

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(filePath, JsonSerializer.Serialize(value, options));
            return true;
        }
        catch (Exception ex)
        {
            ViewerLogger.Error(logSource, $"'{filePath}' could not be written, so nothing was saved", ex);
            return false;
        }
    }
}
