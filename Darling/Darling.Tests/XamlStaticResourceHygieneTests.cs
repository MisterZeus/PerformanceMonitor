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
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #2114: a <c>{StaticResource Key}</c> whose key is not defined anywhere in the SAME app's XAML is
/// not a style nit — inside a DataGrid cell template it throws <c>XamlParseException</c> during
/// measure, and WPF's re-attempted template realization stack-overflows the process
/// (<c>0xc00000fd</c>, uncatchable, no log). The field crash was exactly that: #1980 ported the
/// Query Store grid's inline plan button into Lite still carrying the VIEWER's <c>DarkButton</c>
/// key. This scan enforces the cross-app boundary: every StaticResource key an app references must
/// be defined in that app's own XAML tree. Scope is per-APP, not per-file — WPF resolves through
/// merged dictionaries and control ancestry that a text scan cannot model, so this deliberately
/// catches only the definitely-broken class (key defined NOWHERE in the app) and never false-fails
/// a key that lives in another file of the same app.
/// </summary>
public sealed class XamlStaticResourceHygieneTests
{
    /* Each app scope: its XAML subtrees. Shared control libraries would join the scope of every
       app that references them; today neither app consumes XAML from outside its own tree. */
    private static readonly (string App, string[] Roots)[] Scopes =
    {
        ("Lite", new[] { "Lite" }),
        ("Darling.Viewer", new[] { Path.Combine("Darling", "PerformanceMonitor.Darling.Viewer") }),
    };

    private static readonly Regex Reference = new(
        @"\{StaticResource\s+(?<key>[A-Za-z0-9_.]+)\s*\}", RegexOptions.Compiled);

    private static readonly Regex Definition = new(
        @"x:Key\s*=\s*""(?<key>[A-Za-z0-9_.]+)""", RegexOptions.Compiled);

    [Fact]
    public void EveryStaticResourceKey_IsDefinedInTheSameAppsXamlTree()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null,
            "Could not locate the repository root (walked up from the test binary looking for " +
            "PerformanceMonitor.sln). This test scans the source tree, so it cannot run without it — fix the " +
            "walk-up rather than skipping, or the rule stops being enforced without anyone noticing.");

        var offenders = new List<string>();
        foreach (var (app, roots) in Scopes)
        {
            var files = roots
                .Select(r => Path.Combine(root!, r))
                .Where(Directory.Exists)
                .SelectMany(r => Directory.EnumerateFiles(r, "*.xaml", SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .ToList();
            Assert.NotEmpty(files);

            var defined = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                foreach (Match m in Definition.Matches(File.ReadAllText(file)))
                    defined.Add(m.Groups["key"].Value);
            }

            /* System-supplied keys referenced by name, never defined in app XAML. */
            defined.Add("SystemParameters.VerticalScrollBarWidthKey");

            foreach (var file in files)
            {
                foreach (Match m in Reference.Matches(File.ReadAllText(file)))
                {
                    var key = m.Groups["key"].Value;
                    if (!defined.Contains(key))
                        offenders.Add($"{Path.GetRelativePath(root!, file)}: StaticResource {key} ({app} scope)");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "StaticResource keys referenced but defined nowhere in the same app's XAML — inside a cell " +
            "template this is the #2114 uncatchable stack-overflow crash, not a cosmetic miss. Define the key " +
            "in the app, use an app-local style, or drop the explicit Style:\n" + string.Join("\n", offenders));
    }

    /// <summary>Same walk-up idiom as <c>DocCommentHygieneTests.FindRepoRoot</c>.</summary>
    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PerformanceMonitor.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
