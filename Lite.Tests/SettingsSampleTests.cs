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
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// Keeps <c>Lite\config\settings.sample.json</c> honest against the code that actually reads
/// settings.json, in both directions: every key a loader reads is documented, and every key the
/// sample documents is really read.
///
/// <para><b>Why this exists.</b> The file this replaces (#2418) shipped nowhere, seeded nothing and
/// was never read, so nothing could tell it had gone stale — and it had: four of its eight keys were
/// read by no code anywhere in the repo, including <c>"theme"</c>, which the loader has always spelled
/// <c>color_theme</c>. A stale reference is worse than none, because it is what someone finds when they
/// go looking for what settings.json can hold, and it teaches wrong keys with nothing to signal that it
/// is wrong. The only thing that makes a reference file worth keeping is a check that fails when it
/// drifts, so this is that check.</para>
///
/// <para><b>Derived from the shipped source, not a copy.</b> The key list is regexed out of the real
/// <c>App.xaml.cs</c> and <c>Mcp\McpSettings.cs</c>, copied beside the test binary by the csproj. A
/// hand-maintained list here would be a third thing to keep in sync and would rot the same way the
/// sample did.</para>
///
/// <para><b>Scoping.</b> Each <c>TryGetProperty</c> is attributed to the most recent <c>*.json</c>
/// string literal above it, which is the shape every loader in these files has: open the file, then
/// read keys out of it. That means a loader added later for a DIFFERENT json file is excluded
/// automatically instead of needing an exemption — which matters, because App.xaml.cs already reads
/// servers.json, ignored_wait_types.json and collection_schedule.json elsewhere.</para>
/// </summary>
public sealed class SettingsSampleTests
{
    /// <summary>
    /// The two source files that read settings.json. Both are copied to <c>Fixtures\</c> by the csproj.
    /// If a THIRD loader appears, add it here — a reader this list does not know about is a key the
    /// sample can omit forever without failing anything.
    /// </summary>
    private static readonly string[] ReaderSources = { "App.xaml.cs", "McpSettings.cs" };

    /// <summary>
    /// Keys deliberately documented in the sample that no loader reads. EMPTY, and it should stay that
    /// way: a key with no reader is the exact defect this test exists to catch. The seam is here so that
    /// adding one has to be a deliberate, commented act rather than a silent omission — the old file's
    /// dead <c>"theme"</c> is discussed in a sample COMMENT, which carries the warning without claiming
    /// to be a live key.
    /// </summary>
    private static readonly HashSet<string> SampleOnlyKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Keys a loader reads that the sample deliberately leaves undocumented. EMPTY. If a key is ever
    /// too dangerous to publish, exempt it here WITH the reason — do not quietly drop it from the
    /// sample, because an undocumented knob is how #2418 started.
    /// </summary>
    private static readonly HashSet<string> LoaderOnlyKeys = new(StringComparer.Ordinal);

    [Fact]
    public void Sample_DocumentsEveryKeyTheLoadersRead()
    {
        var read = ReadKeysFromLoaders();
        var documented = SampleKeys();

        var missing = read.Keys
            .Where(k => !documented.Contains(k) && !LoaderOnlyKeys.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "settings.json keys read by Lite but absent from config\\settings.sample.json: "
                + string.Join(", ", missing.Select(k => $"{k} (in {read[k]})"))
                + ". Document them in the sample, or exempt them in LoaderOnlyKeys with a reason.");
    }

    [Fact]
    public void Sample_DocumentsNoKeyTheLoadersIgnore()
    {
        var read = ReadKeysFromLoaders();
        var documented = SampleKeys();

        var dead = documented
            .Where(k => !read.ContainsKey(k) && !SampleOnlyKeys.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            dead.Count == 0,
            "config\\settings.sample.json documents keys nothing reads: " + string.Join(", ", dead)
                + ". Either the loader lost them or the sample invented them; a reader who copies one "
                + "gets a setting that silently does nothing.");
    }

    [Fact]
    public void Sample_ParsesAsCommentedJson_WithNoDuplicateKeys()
    {
        using var doc = ParseSample();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = doc.RootElement
            .EnumerateObject()
            .Where(p => !seen.Add(p.Name))
            .Select(p => p.Name)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            "config\\settings.sample.json repeats keys: " + string.Join(", ", duplicates)
                + ". A duplicate silently documents two different defaults for one setting.");
    }

    /// <summary>
    /// Guards the guard: the regex above is the whole test, so a restructured loader that stops matching
    /// would make BOTH symmetry checks pass on an empty set. Lite reads dozens of keys and always will;
    /// the floor is deliberately far below today's count so it pins "the extraction still works" rather
    /// than becoming a second thing to bump on every new setting.
    /// </summary>
    [Fact]
    public void KeyExtraction_StillFindsTheLoaders()
    {
        var read = ReadKeysFromLoaders();

        Assert.True(
            read.Count >= 50,
            $"Only {read.Count} settings.json keys were found across {string.Join(", ", ReaderSources)}. "
                + "The loaders were restructured and the extraction no longer sees them, which would make "
                + "the symmetry tests pass vacuously.");

        /* Anchors: one no-UI key (the reason the sample is kept at all) and one from each source file,
           so a fixture that silently stopped being copied fails here rather than passing on the other. */
        Assert.Contains("analysis_timeout_seconds", read.Keys);
        Assert.Contains("alerts_enabled", read.Keys);
        Assert.Contains("mcp_port", read.Keys);
    }

    /// <summary>
    /// key -> the source file it was found in, for a failure message that names where to look.
    /// </summary>
    private static Dictionary<string, string> ReadKeysFromLoaders()
    {
        /* Alternation, evaluated left to right in one pass, so the "which file is open" state and the
           key hits stay in source order. */
        var scanner = new Regex(
            "\"(?<file>[A-Za-z0-9_.\\-]+\\.json)\"|TryGetProperty\\(\"(?<key>[^\"]+)\"",
            RegexOptions.CultureInvariant);

        var keys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in ReaderSources)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
            Assert.True(
                File.Exists(path),
                $"{name} was not copied beside the test binary — check the csproj None/Link item.");

            var openFile = string.Empty;
            foreach (Match match in scanner.Matches(File.ReadAllText(path)))
            {
                if (match.Groups["file"].Success)
                {
                    openFile = match.Groups["file"].Value;
                }
                else if (openFile == "settings.json")
                {
                    keys.TryAdd(match.Groups["key"].Value, name);
                }
            }
        }

        return keys;
    }

    private static HashSet<string> SampleKeys()
    {
        using var doc = ParseSample();
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static JsonDocument ParseSample()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings.sample.json");
        Assert.True(
            File.Exists(path),
            "settings.sample.json was not copied beside the test binary — check the csproj None/Link item.");

        /* The sample is JSONC on purpose: the comments ARE the documentation. Lite's own loader parses
           settings.json with default options and would reject them, which is why the sample's header
           says to copy keys out of it rather than the file itself. */
        return JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = false });
    }
}
