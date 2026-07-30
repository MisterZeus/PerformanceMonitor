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
using System.Text.Json;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// Single source for the configured "ignored" (benign/idle) wait types — the per-user editable
/// %LOCALAPPDATA%\PerformanceMonitorLite-Data\config\ignored_wait_types.json, falling back to the copy
/// bundled next to the exe. Used by BOTH the collector (skip at collection) and the wait-stats tab
/// queries (skip at display) so the two can't drift. Display-side filtering is what hides waits already
/// collected before the filter was active — copying the JSON only stops NEW collection, it can't remove
/// existing DuckDB rows (#1240).
/// </summary>
public static class IgnoredWaitTypes
{
    /// <summary>
    /// Loads the ignored wait-type set (case-insensitive): the per-user copy first, then the bundled
    /// copy next to the exe. Best-effort — returns an empty set if neither file is present or parsing
    /// fails (callers warn when the result is empty).
    /// </summary>
    public static HashSet<string> Load()
    {
        var waits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var configPath = Path.Combine(App.ConfigDirectory, "ignored_wait_types.json");
        if (!File.Exists(configPath))
        {
            configPath = Path.Combine(AppContext.BaseDirectory, "config", "ignored_wait_types.json");
        }

        if (!File.Exists(configPath))
        {
            return waits;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("ignored_waits", out var waitsArray))
            {
                foreach (var wait in waitsArray.EnumerateArray())
                {
                    var waitType = wait.GetString();
                    if (!string.IsNullOrEmpty(waitType))
                    {
                        waits.Add(waitType);
                    }
                }
            }
        }
        catch
        {
            /* Best-effort; callers warn when the resulting set is empty. */
        }

        return waits;
    }

    /// <summary>
    /// Builds a SQL predicate <c>AND wait_type NOT IN ('A','B',...)</c> excluding the ignored types, or an
    /// empty string when there is nothing to exclude. Names are sanitized to [A-Za-z0-9_] before being
    /// inlined, so the literals are injection-safe (the source is controlled config and SQL Server
    /// wait_type names are always identifiers). Applied at display time so benign waits already in the
    /// DuckDB don't surface in the wait-stats tab/picker.
    /// </summary>
    public static string BuildExclusionClause(IReadOnlyCollection<string>? ignored)
    {
        if (ignored == null || ignored.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var wait in ignored)
        {
            if (string.IsNullOrEmpty(wait) || !IsSafeIdentifier(wait))
            {
                continue;
            }
            if (sb.Length > 0)
            {
                sb.Append(',');
            }
            sb.Append('\'').Append(wait).Append('\'');
        }

        return sb.Length == 0 ? string.Empty : $"AND wait_type NOT IN ({sb})";
    }

    private static bool IsSafeIdentifier(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }
        return true;
    }
}
