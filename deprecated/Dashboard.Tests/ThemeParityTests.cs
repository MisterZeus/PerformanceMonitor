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
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// Cross-app theme-parity guard. The WPF brush/color dictionaries are NOT shared: Dashboard and Lite
/// each ship their own Themes/{Dark,Light,CoolBreeze}Theme.xaml — six separate files, hand-synced with
/// no enforcement. Their shared palette keys currently match, but nothing keeps them in sync, so a color
/// tweak applied to one app can silently drift the two apart.
///
/// For each theme this parses the x:Key -> color map from both apps' same-theme file and asserts that
/// every key present in BOTH (the intersection) resolves to the same color. Keys that exist in only one
/// app (e.g. Dashboard's TimeRangeSlicer-only brushes) are allowed and ignored — only the overlap is
/// asserted, so either app may add app-specific resources without tripping the guard.
/// </summary>
public class ThemeParityTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("CoolBreeze")]
    public void SharedThemeKeys_HaveIdenticalColors_AcrossApps(string theme)
    {
        var repoRoot = FindRepoRoot();
        Assert.True(repoRoot is not null,
            $"Could not locate the repo root (a directory containing both {DashboardThemesPath} and Lite\\Themes) " +
            $"by walking up from {AppContext.BaseDirectory}.");

        var dashboardFile = Path.Combine(repoRoot!, DashboardThemesPath, $"{theme}Theme.xaml");
        var liteFile = Path.Combine(repoRoot!, "Lite", "Themes", $"{theme}Theme.xaml");
        Assert.True(File.Exists(dashboardFile), $"Dashboard theme file not found: {dashboardFile}");
        Assert.True(File.Exists(liteFile), $"Lite theme file not found: {liteFile}");

        var dashboard = LoadColorMap(dashboardFile);
        var lite = LoadColorMap(liteFile);

        var shared = dashboard.Keys
            .Intersect(lite.Keys)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        // Guard against a parser regression (e.g. a XAML namespace change) silently turning this into a
        // vacuous pass: the two apps share dozens of palette keys today, so an empty intersection is a bug.
        Assert.True(shared.Count > 0,
            $"No shared color/brush keys parsed for the {theme} theme — the parser or the theme files changed shape.");

        var mismatches = shared
            .Where(k => !string.Equals(dashboard[k], lite[k], StringComparison.Ordinal))
            .Select(k => $"  {k}: Dashboard={dashboard[k]}  Lite={lite[k]}")
            .ToList();

        Assert.True(mismatches.Count == 0,
            $"{theme} theme: {mismatches.Count} shared color/brush key(s) differ between Dashboard\\Themes\\{theme}Theme.xaml " +
            $"and Lite\\Themes\\{theme}Theme.xaml. Re-sync the two palettes:\n" + string.Join("\n", mismatches));
    }

    /// <summary>
    /// Parses a theme ResourceDictionary into a map of x:Key -> normalized #AARRGGBB color. Handles the two
    /// resource forms used in these files: standalone &lt;Color x:Key="..."&gt;#hex&lt;/Color&gt; and
    /// &lt;SolidColorBrush x:Key="..." Color="#hex | {StaticResource SomeColor}"/&gt;, resolving a brush's
    /// {StaticResource} reference back to the underlying &lt;Color&gt; hex. Entries whose color cannot be
    /// resolved to a concrete value (e.g. a reference to a non-Color resource) are omitted from the map.
    /// First definition wins, so the top-of-file palette is preferred over any template-local re-key.
    /// </summary>
    private static Dictionary<string, string> LoadColorMap(string xamlPath)
    {
        var doc = XDocument.Load(xamlPath);

        // Pass 1: literal <Color> resources.
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "Color"))
        {
            var key = (string?)el.Attribute(X + "Key");
            if (key is null || colors.ContainsKey(key))
            {
                continue;
            }
            colors[key] = el.Value.Trim();
        }

        // Pass 2: <SolidColorBrush> resources (Color is an unprefixed attribute, so no namespace).
        var brushes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "SolidColorBrush"))
        {
            var key = (string?)el.Attribute(X + "Key");
            var color = (string?)el.Attribute("Color");
            if (key is null || color is null || brushes.ContainsKey(key))
            {
                continue;
            }
            brushes[key] = color.Trim();
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in colors)
        {
            var norm = NormalizeColor(pair.Value);
            if (norm is not null)
            {
                map[pair.Key] = norm;
            }
        }
        foreach (var pair in brushes)
        {
            var resolved = ResolveColor(pair.Value, colors);
            var norm = resolved is null ? null : NormalizeColor(resolved);
            if (norm is not null)
            {
                map[pair.Key] = norm;
            }
        }
        return map;
    }

    private static readonly Regex StaticResourceRef =
        new(@"^\{StaticResource\s+([^}]+)\}$", RegexOptions.Compiled);

    /// <summary>
    /// Resolves a brush Color value to a literal color token, following one or more {StaticResource X} hops
    /// into the &lt;Color&gt; table. Returns null when the chain dead-ends at a key that is not a &lt;Color&gt;.
    /// </summary>
    private static string? ResolveColor(string value, Dictionary<string, string> colors, int depth = 0)
    {
        if (depth > 10)
        {
            return null;
        }

        var trimmed = value.Trim();
        var match = StaticResourceRef.Match(trimmed);
        if (!match.Success)
        {
            return trimmed;
        }

        var refKey = match.Groups[1].Value.Trim();
        return colors.TryGetValue(refKey, out var inner)
            ? ResolveColor(inner, colors, depth + 1)
            : null;
    }

    /// <summary>
    /// Normalizes a color token to canonical upper-case #AARRGGBB so equivalent spellings (#RGB, #ARGB,
    /// #RRGGBB, #AARRGGBB — WPF treats a missing alpha as fully opaque) compare equal. Non-hex named colors
    /// (e.g. White, Transparent) are upper-cased as-is so they still compare. Returns null for empty input.
    /// </summary>
    private static string? NormalizeColor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var c = raw.Trim();
        var match = Regex.Match(c, "^#([0-9A-Fa-f]+)$");
        if (!match.Success)
        {
            return c.ToUpperInvariant();
        }

        var h = match.Groups[1].Value.ToUpperInvariant();
        h = h.Length switch
        {
            3 => "FF" + string.Concat(h.Select(ch => $"{ch}{ch}")), // #RGB -> #FFRRGGBB
            4 => string.Concat(h.Select(ch => $"{ch}{ch}")),        // #ARGB -> #AARRGGBB
            6 => "FF" + h,                                          // #RRGGBB -> #FFRRGGBB
            _ => h,
        };

        // Anything that did not land on a clean 8-digit ARGB (e.g. a malformed 5/7-digit token) falls back
        // to the upper-cased original so it still compares deterministically rather than being dropped.
        return h.Length == 8 ? "#" + h : c.ToUpperInvariant();
    }

    /// <summary>
    /// Walks up from the test output directory to the repo root — the directory that contains both
    /// Dashboard\Themes and Lite\Themes. Mirrors Installer.Tests' FindInstallDirectory walk-up.
    /// </summary>
    /// <summary>Repo-relative path to the Dashboard's theme folder — under deprecated/ since #1612.</summary>
    private static readonly string DashboardThemesPath = Path.Combine("deprecated", "Dashboard", "Themes");

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, DashboardThemesPath)) &&
                Directory.Exists(Path.Combine(dir.FullName, "Lite", "Themes")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
