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
using System.Text.RegularExpressions;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Repo-wide documentation hygiene (#1745). One rule today: no member carries two stacked
/// <c>&lt;summary&gt;</c> blocks.
///
/// <para><b>Why this needs a test rather than a review.</b> XML documentation takes the LAST summary, so
/// IntelliSense, generated docs and every analyzer render the correct text. The build is clean, nothing
/// warns, and no behavioral test can see it. The only reader misled is a human reading top-down — who gets
/// the WRONG description first, attached to the wrong member. In #1739 the same region was read at least
/// three times by two people over several hours, one of whom quoted a paragraph out of it, and neither saw
/// the duplicate; a regex found it in one pass. That is a property of the defect, not of the readers.</para>
///
/// <para><b>It is not cosmetic.</b> Of the eight sites this rule found on dev, SEVEN were a real doc block
/// displaced off its member by an insertion — so the member the summary described was left with no
/// documentation at all, and a different member acquired a description of something else. Only one was a
/// superseded duplicate safe to simply delete. A blind "remove the extra summary" sweep would have destroyed
/// documentation at seven of the eight.</para>
///
/// <para><b>Coverage limit, stated rather than assumed.</b> CI path filters are per-project, so this runs on
/// any pull request that trips the <c>darling</c> or <c>core</c> filter, and on every nightly and release
/// build — but a change touching ONLY Lite or Installer will not run it, and would be caught on the next
/// nightly instead. It lives here because the repo's other source-parsing pins do
/// (<c>HostHeaderGuardTests</c>, <c>DarlingStoreUpgradeTests</c>), which is where someone looks for this
/// kind of guard.</para>
/// </summary>
public sealed class DocCommentHygieneTests
{
    /// <summary>
    /// A <c>&lt;/summary&gt;</c> closed and immediately reopened. Deliberately narrow: it matches only the
    /// stacked-block shape, never a legitimate <c>&lt;summary&gt;</c> followed by <c>&lt;param&gt;</c>,
    /// <c>&lt;returns&gt;</c> or <c>&lt;remarks&gt;</c>.
    /// </summary>
    private static readonly Regex StackedSummary =
        new(@"</summary>\s*\r?\n\s*///\s*<summary>", RegexOptions.Compiled);

    [Fact]
    public void NoMemberCarriesTwoStackedSummaryBlocks()
    {
        var root = FindRepoRoot();

        /* FAIL rather than skip when the tree cannot be found. A guard that silently skips is a guard that
           silently stops guarding, which is the failure this whole rule exists to prevent — if the output
           layout ever changes, this should go red and get fixed, not evaporate. */
        Assert.True(root is not null,
            "Could not locate the repository root (walked up from the test binary looking for " +
            "PerformanceMonitor.sln). This test scans the source tree, so it cannot run without it — fix the " +
            "walk-up rather than skipping, or the rule stops being enforced without anyone noticing.");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
                || file.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match match in StackedSummary.Matches(text))
            {
                /* Line number of the match start, so the failure names somewhere you can actually open. */
                var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                offenders.Add($"{Path.GetRelativePath(root!, file)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Stacked <summary> blocks found. Each is a member carrying two summaries; XML docs take the LAST " +
            "one, so tooling looks correct and only a human reading the file is misled.\n\n" +
            "DO NOT just delete the first summary — check first whether it belongs to a DIFFERENT member that " +
            "an insertion pushed it away from. Seven of the eight found in #1745 were displaced doc blocks " +
            "whose real member had been left undocumented, and deleting them would have lost the documentation " +
            "rather than deduplicating it.\n\n" +
            string.Join("\n", offenders));
    }

    /// <summary>
    /// Walks up from the test output directory to the repo root — the directory holding
    /// <c>PerformanceMonitor.sln</c>. Same walk-up idiom as <c>ThemeParityTests.FindRepoRoot</c>.
    /// </summary>
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
