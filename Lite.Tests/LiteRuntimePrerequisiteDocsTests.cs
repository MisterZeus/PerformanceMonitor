/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// #2489: Lite ships TWO artifacts whose .NET prerequisites are OPPOSITE, and the docs had them
/// backwards. <c>PerformanceMonitorLite-win-Setup.exe</c> is packed from a <c>--self-contained</c>
/// publish and needs no runtime at all, yet it was the one artifact carrying a runtime requirement in
/// the README; the portable ZIP is a framework-dependent publish that needs TWO runtimes, and had no
/// prerequisites documented anywhere.
///
/// The reason this is worth a guard rather than a one-time correction is that nothing in the product
/// forces the two facts to agree. Both are decided in files nobody edits while writing docs — the
/// publish shape lives in the workflows, and the framework list is a build OUTPUT that changes when a
/// package reference changes (ASP.NET Core is on that list only because
/// <c>ModelContextProtocol.AspNetCore</c> drags the framework reference in transitively). A prose
/// sentence cannot notice either one moving.
///
/// So every assertion here is DERIVED from the shipped artifact, never from a list kept beside it:
/// the required runtimes come from the built <c>PerformanceMonitorLite.runtimeconfig.json</c>, and
/// which artifact is self-contained comes from parsing the <c>dotnet publish</c> lines in the two
/// workflows that build them. Drop the MCP package and the ASP.NET Core sentence has to go; make the
/// ZIP self-contained and its prerequisites section has to go; bump to .NET 11 and every version
/// number in the docs has to move. A framework this file has no mapping for is a hard failure, because
/// an undocumentable prerequisite is exactly the state that produced #2489.
///
/// Note for whoever touches the CI path filters: <c>README.md</c> and <c>Lite/README.md</c> are named
/// explicitly in build.yml's <c>lite</c> filter. They have to be. Every area filter carves markdown
/// out (<c>dir/**/!(*.md)</c>), and a docs-only PR additionally engages the fast path that skips .NET
/// setup entirely — so without those entries this guard would be unrunnable on precisely the change it
/// exists to catch.
/// </summary>
public sealed class LiteRuntimePrerequisiteDocsTests
{
    private const string RootReadmePath = "README.md";
    private const string LiteReadmePath = "Lite/README.md";
    private const string ShippedNoticePath = "Lite/READ-ME-FIRST.txt";
    private const string BuildWorkflowPath = ".github/workflows/build.yml";
    private const string NightlyWorkflowPath = ".github/workflows/nightly.yml";

    private const string PublishCommand = "dotnet publish Lite/PerformanceMonitorLite.csproj";
    private const string SetupExeName = "PerformanceMonitorLite-win-Setup.exe";

    /// <summary>
    /// Shared-framework name to the name of the runtime a human downloads to satisfy it, as a format
    /// string over the major version. This is a TRANSLATION table, not an inventory: the set of
    /// frameworks is read out of the built runtimeconfig, and a name missing from here fails the suite
    /// rather than being skipped.
    ///
    /// <c>Microsoft.NETCore.App</c> maps to nothing on purpose. Both installers below contain it, so
    /// naming it in the docs would send an operator to a third download they do not need.
    /// </summary>
    private static readonly Dictionary<string, string> InstallerNameFormats = new(StringComparer.Ordinal)
    {
        ["Microsoft.NETCore.App"] = string.Empty,
        ["Microsoft.WindowsDesktop.App"] = ".NET Desktop Runtime {0}",
        ["Microsoft.AspNetCore.App"] = "ASP.NET Core Runtime {0}",
    };

    /// <summary>One shared framework the framework-dependent build names, and the major version it wants.</summary>
    private sealed record FrameworkRequirement(string Name, int Major, string SourcePath);

    /// <summary>
    /// The frameworks the FRAMEWORK-DEPENDENT Lite build asks the host for, read from its own
    /// <c>runtimeconfig.json</c> under <c>Lite/bin</c>.
    ///
    /// Reading the build output rather than the csproj is deliberate. The csproj never mentions
    /// <c>Microsoft.AspNetCore.App</c>; that framework arrives transitively and only the SDK's own
    /// output says so, which is the whole reason the requirement was a surprise. A self-contained
    /// runtimeconfig carries <c>includedFrameworks</c> instead of <c>frameworks</c> and is skipped —
    /// it states what is bundled, which by definition is not a prerequisite.
    /// </summary>
    private static IReadOnlyList<FrameworkRequirement> FrameworkDependentRequirements()
    {
        var binRoot = Path.Combine(ParitySource.RepoRoot(), "Lite", "bin");
        Assert.True(
            Directory.Exists(binRoot),
            $"{binRoot} does not exist, so this guard cannot read what Lite actually asks the .NET host for. " +
            "Lite.Tests references Lite, so building the suite builds it - if this fires, the build layout moved.");

        var configs = Directory.GetFiles(binRoot, "PerformanceMonitorLite.runtimeconfig.json", SearchOption.AllDirectories);
        var requirements = new List<FrameworkRequirement>();

        foreach (var configPath in configs)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));

            if (!document.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions) ||
                !runtimeOptions.TryGetProperty("frameworks", out var frameworks))
            {
                /* Self-contained output (includedFrameworks), or a shape this does not understand. */
                continue;
            }

            foreach (var framework in frameworks.EnumerateArray())
            {
                var name = framework.GetProperty("name").GetString();
                var version = framework.GetProperty("version").GetString();
                Assert.False(string.IsNullOrWhiteSpace(name), $"Nameless framework entry in {configPath}");
                Assert.False(string.IsNullOrWhiteSpace(version), $"Versionless framework entry in {configPath}");

                var major = int.Parse(version!.Split('.')[0], CultureInfo.InvariantCulture);
                requirements.Add(new FrameworkRequirement(name!, major, configPath));
            }
        }

        Assert.True(
            requirements.Count > 0,
            $"No framework-dependent PerformanceMonitorLite.runtimeconfig.json found under {binRoot}. " +
            $"Searched {configs.Length} runtimeconfig file(s). Without one, nothing here is derived and the " +
            "docs would be guarded by a list instead of by the build.");

        return requirements;
    }

    /// <summary>
    /// Every <c>dotnet publish</c> of Lite in one workflow, as output directory to "was it published
    /// <c>--self-contained</c>". This is what makes "Setup.exe needs no runtime" a derived claim rather
    /// than a remembered one.
    /// </summary>
    private static Dictionary<string, bool> LitePublishShapes(string workflowPath)
    {
        var shapes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in ParitySource.ReadFile(workflowPath).Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.Contains(PublishCommand, StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var outputFlag = Array.IndexOf(tokens, "-o");
            Assert.True(
                outputFlag >= 0 && outputFlag + 1 < tokens.Length,
                $"A Lite publish in {workflowPath} has no '-o <dir>' this guard can read: {line}");

            shapes[tokens[outputFlag + 1]] = line.Contains("--self-contained", StringComparison.Ordinal);
        }

        Assert.True(shapes.Count > 0, $"No '{PublishCommand}' line found in {workflowPath}.");
        return shapes;
    }

    /// <summary>The last path segment of a workflow path token, quotes and a trailing <c>/*</c> removed.</summary>
    private static string DirectoryLeaf(string token) =>
        Path.GetFileName(token.Trim('\'', '"').TrimEnd('*').TrimEnd('/', '\\'));

    /// <summary>Every line of a doc that mentions <paramref name="needle"/>.</summary>
    private static string[] LinesMentioning(string docPath, string needle) =>
        ParitySource.ReadFile(docPath)
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    [Fact]
    public void TheBuiltRuntimeconfig_NamesOnlyFrameworksThisGuardCanTranslateToADownload()
    {
        /* The gate that keeps the rest of this file honest. If a new shared framework shows up in the
           runtimeconfig, somebody has to decide what an operator downloads for it and say so in the
           docs - silently skipping it is how the ASP.NET Core requirement went undocumented for a
           release in the first place. */
        var requirements = FrameworkDependentRequirements();

        var unmapped = requirements
            .Select(r => r.Name)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !InstallerNameFormats.ContainsKey(name))
            .ToArray();

        Assert.True(
            unmapped.Length == 0,
            $"Lite's runtimeconfig now names framework(s) this guard has no download mapping for: " +
            $"{string.Join(", ", unmapped)}. Add the mapping to InstallerNameFormats AND the sentence to " +
            $"{RootReadmePath}, {LiteReadmePath} and {ShippedNoticePath} - a prerequisite nobody documents is #2489.");

        var majors = requirements.Select(r => r.Major).Distinct().ToArray();
        Assert.True(
            majors.Length == 1,
            $"Lite's runtimeconfig files disagree about the .NET major version ({string.Join(", ", majors)}), " +
            "so there is no single number the docs could be correct about.");
    }

    [Fact]
    public void EveryRuntimeTheZipNeeds_IsNamedInBothReadmesAndInTheNoticeThatShipsBesideTheExe()
    {
        /* The ZIP is the artifact with prerequisites, so all three places a reader can land - the root
           README, Lite's own README, and the text file inside the ZIP itself - have to name the same
           downloads. The set and the version come from the build, so dropping the MCP package or moving
           to a new .NET major turns this red until the prose catches up. */
        var requirements = FrameworkDependentRequirements();
        var docs = new[] { RootReadmePath, LiteReadmePath, ShippedNoticePath };

        var expected = requirements
            .Where(r => InstallerNameFormats[r.Name].Length > 0)
            .Select(r => string.Format(CultureInfo.InvariantCulture, InstallerNameFormats[r.Name], r.Major))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            expected.Length >= 2,
            $"Expected the framework-dependent build to need at least the Desktop and ASP.NET Core runtimes; " +
            $"derived only: {string.Join(", ", expected)}");

        foreach (var doc in docs)
        {
            var text = ParitySource.ReadFile(doc);
            foreach (var installer in expected)
            {
                Assert.True(
                    text.Contains(installer, StringComparison.OrdinalIgnoreCase),
                    $"{doc} never names '{installer}', which the built runtimeconfig says the portable ZIP " +
                    "cannot start without. The .NET host reports only the FIRST missing framework, so a " +
                    "half-documented pair costs the reader a second identical failure.");
            }
        }
    }

    [Fact]
    public void TheSetupExeIsSelfContained_SoNoDocLineMayHangARuntimeRequirementOnIt()
    {
        /* THE #2489 defect, in one assertion. README.md:95 read "(requires .NET 10 Desktop Runtime)" on
           the Setup.exe download line - the one artifact that needs nothing - which both burdened the
           recommended path with a download and left the impression the requirement had been handled. */
        var shapes = LitePublishShapes(BuildWorkflowPath);
        var selfContained = shapes.Where(kv => kv.Value).Select(kv => kv.Key).ToArray();

        Assert.True(
            selfContained.Length == 1,
            $"Expected exactly one self-contained Lite publish in {BuildWorkflowPath} (the Velopack one); " +
            $"found {selfContained.Length}: {string.Join(", ", selfContained)}. The docs' claim that " +
            "Setup.exe needs no runtime is derived from there being one.");

        var mentions = LinesMentioning(RootReadmePath, SetupExeName)
            .Concat(LinesMentioning(LiteReadmePath, SetupExeName))
            .ToArray();

        Assert.True(mentions.Length > 0, $"No doc line mentions {SetupExeName}; the download instructions moved.");

        foreach (var line in mentions)
        {
            Assert.True(
                line.Contains("self-contained", StringComparison.OrdinalIgnoreCase),
                $"A line naming {SetupExeName} does not say it is self-contained, so a reader cannot tell it " +
                $"from the ZIP: {line.Trim()}");

            Assert.False(
                line.Contains("requires", StringComparison.OrdinalIgnoreCase),
                $"A line naming {SetupExeName} states a requirement, but it is packed from a " +
                $"--self-contained publish ({selfContained[0]}) and has none: {line.Trim()}");

            Assert.False(
                line.Contains("Desktop Runtime", StringComparison.OrdinalIgnoreCase),
                $"A line naming {SetupExeName} points at the Desktop Runtime. That download belongs to the " +
                $"portable ZIP; Setup.exe carries its own runtime: {line.Trim()}");
        }
    }

    [Fact]
    public void SetupExeIsPackedFromTheSelfContainedPublish_AndTheZipFromTheFrameworkDependentOne()
    {
        /* Both doc claims rest on which publish feeds which artifact, and that wiring is three hops of
           YAML away from the sentence it justifies. Derived on both ends rather than pinned as literals:
           the Velopack pack source and the release ZIP source each have to keep matching the publish
           directory whose shape the docs describe. Repoint either one and the docs become wrong
           silently, which is the failure this whole file exists for. */
        var shapes = LitePublishShapes(BuildWorkflowPath);

        var selfContainedDir = Assert.Single(shapes.Where(kv => kv.Value)).Key;
        var frameworkDependentDir = Assert.Single(shapes.Where(kv => !kv.Value)).Key;

        var workflow = ParitySource.ReadFile(BuildWorkflowPath);

        var packLine = workflow.Split('\n')
            .Select(l => l.Trim())
            .Single(l => l.Contains("vpk pack -u PerformanceMonitorLite", StringComparison.Ordinal));

        var packTokens = packLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var packFlag = Array.IndexOf(packTokens, "-p");
        Assert.True(packFlag >= 0 && packFlag + 1 < packTokens.Length, $"No '-p <dir>' on the vpk pack line: {packLine}");

        Assert.Equal(
            DirectoryLeaf(selfContainedDir),
            DirectoryLeaf(packTokens[packFlag + 1]),
            ignoreCase: true);

        var zipLines = workflow.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains("Compress-Archive", StringComparison.Ordinal) &&
                        l.Contains("PerformanceMonitorLite-", StringComparison.Ordinal))
            .ToArray();

        Assert.True(zipLines.Length > 0, $"Nothing in {BuildWorkflowPath} builds a PerformanceMonitorLite ZIP.");

        foreach (var zipLine in zipLines)
        {
            var zipTokens = zipLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var pathFlag = Array.IndexOf(zipTokens, "-Path");
            Assert.True(pathFlag >= 0 && pathFlag + 1 < zipTokens.Length, $"No '-Path <dir>' on: {zipLine}");

            /* Release re-zips from signed/Lite, the SignPath round-trip of publish/Lite; same leaf, and
               that is the property worth asserting - the zip must never come from the velopack tree. */
            Assert.Equal(
                DirectoryLeaf(frameworkDependentDir),
                DirectoryLeaf(zipTokens[pathFlag + 1]),
                ignoreCase: true);
        }
    }

    [Fact]
    public void TheNightlyZipIsFrameworkDependentToo_SoItsPrerequisitesAreTheSame()
    {
        /* The nightly is the UAT download, and it publishes Lite itself rather than reusing build.yml's
           step. If the two workflows ever disagree about the publish shape, the one set of prerequisites
           the docs state is wrong for one of them. */
        var releaseShapes = LitePublishShapes(BuildWorkflowPath);
        var nightlyShapes = LitePublishShapes(NightlyWorkflowPath);

        var frameworkDependentDir = Assert.Single(releaseShapes.Where(kv => !kv.Value)).Key;

        Assert.True(
            nightlyShapes.TryGetValue(frameworkDependentDir, out var nightlySelfContained),
            $"{NightlyWorkflowPath} does not publish Lite to {frameworkDependentDir}, so the nightly ZIP no " +
            "longer shares the release ZIP's documented prerequisites.");

        Assert.False(
            nightlySelfContained,
            $"{NightlyWorkflowPath} now publishes {frameworkDependentDir} self-contained while " +
            $"{BuildWorkflowPath} does not. Two shapes, one set of documented prerequisites.");
    }

    [Fact]
    public void TheZipsPrerequisites_HaveTheirOwnSectionInLitesReadme()
    {
        /* Lite/README.md had no prerequisites section at all - zero hits for "prerequisite", ".NET 10",
           "Desktop Runtime" or "ASP.NET". Someone reading the Lite folder had nowhere to learn any of
           this, which is half of #2489. */
        var liteReadme = ParitySource.ReadFile(LiteReadmePath);

        Assert.True(
            liteReadme.Contains("## Prerequisites", StringComparison.Ordinal),
            $"{LiteReadmePath} has no '## Prerequisites' section. The root README links to its anchor.");

        Assert.True(
            liteReadme.Contains("framework-dependent", StringComparison.OrdinalIgnoreCase),
            $"{LiteReadmePath} does not say the portable ZIP is framework-dependent, which is the reason it " +
            "has prerequisites at all.");

        foreach (var doc in new[] { RootReadmePath, LiteReadmePath })
        {
            Assert.True(
                LinesMentioning(doc, "PerformanceMonitorLite-<version>.zip").Length > 0,
                $"{doc} never names the portable ZIP, so its prerequisites are attached to nothing.");
        }
    }

    [Fact]
    public void TheRuntimeNotice_ShipsInsideTheZipBesideTheExe()
    {
        /* Lite cannot pre-check the way Darling's install-darling.ps1 does: there is no install script,
           and the host error precedes our code. The unzipped folder is the only surface left, so the
           notice has to actually be copied to the publish output - a file that exists in the repo and
           never ships is worse than none, because the READMEs promise it is there. */
        var noticePath = Path.Combine(ParitySource.RepoRoot(), ShippedNoticePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(noticePath), $"{ShippedNoticePath} is missing.");

        var csproj = ParitySource.ReadFile("Lite/PerformanceMonitorLite.csproj");
        var noticeFileName = Path.GetFileName(ShippedNoticePath);

        var itemIndex = csproj.IndexOf($"<None Update=\"{noticeFileName}\">", StringComparison.Ordinal);
        Assert.True(
            itemIndex >= 0,
            $"PerformanceMonitorLite.csproj has no <None Update=\"{noticeFileName}\"> item, so it never lands " +
            "beside the exe and both READMEs promise a file that is not in the ZIP.");

        var itemEnd = csproj.IndexOf("</None>", itemIndex, StringComparison.Ordinal);
        Assert.True(itemEnd > itemIndex, $"Unterminated <None> item for {noticeFileName}.");

        Assert.True(
            csproj[itemIndex..itemEnd].Contains("<CopyToOutputDirectory>", StringComparison.Ordinal),
            $"{noticeFileName} is declared but not copied to the output directory.");

        /* And it has to say the same thing the READMEs do - it is the copy a stranded reader gets. */
        var notice = ParitySource.ReadFile(ShippedNoticePath);
        Assert.True(
            notice.Contains(SetupExeName, StringComparison.Ordinal),
            $"{ShippedNoticePath} does not tell Setup.exe users they need nothing, so it reads as a demand " +
            "for runtimes the self-contained install does not want.");
    }
}
