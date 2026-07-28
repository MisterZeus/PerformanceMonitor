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
/// Every test class that touches the shared <c>DARLING_TEST_PG</c> store must either serialize against the other
/// live classes or say in writing why it does not (#1776).
///
/// <para><b>The failure this prevents is a moving one.</b> Six classes connected to the shared store without
/// <c>[Collection("live-postgres")]</c>, so they ran in parallel with the sixty-odd classes that carry it — and
/// with each other. Three consecutive full-suite runs against one long-lived database failed on a DIFFERENT
/// unrelated class each time, which is the expensive shape of flake: it looks exactly like the change under test
/// broke something it never touched. <b>CI cannot catch this</b>, because it creates a throwaway cluster per run,
/// so the cost lands entirely on local development and shows up as someone else's problem in someone else's pull
/// request. That is precisely why it survived so long, and why the guard has to be a test rather than a habit.</para>
///
/// <para><b>The exemption is real and must stay available.</b> A class that mints its own database (through
/// <c>ScratchPostgres</c>) or stands up its own cluster does not race the shared store, and serializing it would
/// cost suite time for no safety at all. So this does not demand the attribute — it demands a DECISION, recorded
/// either as the attribute or as an <c>#1776 own-store</c> comment explaining the exemption.</para>
///
/// <para><b>Why the match is the quoted literal and not a substring.</b> <c>DARLING_TEST_PGRUNTIME</c> and
/// <c>DARLING_TEST_PGRUNTIME_OLD</c> are DIFFERENT variables that merely share the prefix, naming an assembled
/// runtime directory rather than a store. #1776's original sweep matched on substring and so listed three classes
/// that never read <c>DARLING_TEST_PG</c> at all, reaching the right answer for the wrong reason. Matching the
/// quoted literal keeps those out, and keeps out prose mentions in doc comments (which write the name bare) so a
/// class that only DESCRIBES the variable is not dragged in.</para>
/// </summary>
public sealed class LivePostgresCollectionHygieneTests
{
    /// <summary>The env-var read as it appears in code — quoted, so <c>DARLING_TEST_PGRUNTIME</c> cannot match.</summary>
    private const string SharedStoreVariable = "\"DARLING_TEST_PG\"";

    private const string LiveCollectionAttribute = "[Collection(\"live-postgres\")]";

    /// <summary>The recorded-exemption marker. Prose, deliberately: the point is that a human wrote down why.</summary>
    private const string OwnStoreMarker = "#1776 own-store";

    /// <summary>Top-level class declarations. Nested types are indented and correctly not matched.</summary>
    private static readonly Regex ClassDeclaration =
        new(@"^(?:public|internal)\s+(?:sealed\s+|static\s+|abstract\s+|partial\s+)*class\s+(\w+)",
            RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// How far above a class declaration to look for its attribute or exemption marker. Generous enough for an
    /// attribute sitting above a long doc comment, tight enough that it cannot reach into the previous class's body.
    /// </summary>
    private const int HeaderLookbackLines = 25;

    [Fact]
    public void EveryClassUsingTheSharedStore_IsSerializedOrDocumentsWhyNot()
    {
        var directory = FindTestProjectDirectory();

        /* FAIL rather than skip when the source tree cannot be found — the DocCommentHygieneTests rule: a guard
           that silently skips is a guard that silently stops guarding. */
        Assert.True(directory is not null,
            "Could not locate the Darling.Tests source directory (walked up from the test binary looking for "
            + "PerformanceMonitor.sln). This test scans source, so it cannot run without it — fix the walk-up "
            + "rather than skipping, or the rule stops being enforced without anyone noticing.");

        var offenders = new List<string>();

        /* Recurse, and skip build output. The project is flat today, so TopDirectoryOnly would be equivalent —
           but it would also mean the first test file someone puts in a subfolder escapes the rule silently, which
           is the failure mode this whole test exists to prevent. Scoping to THIS project is correct rather than
           lazy: [Collection] groups classes within an assembly, so a class elsewhere could not join the live
           collection even if it wanted to, and the quoted literal appears nowhere else in the repo. */
        foreach (var file in Directory.EnumerateFiles(directory!, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
                || file.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (!text.Contains(SharedStoreVariable, StringComparison.Ordinal))
            {
                continue;
            }

            var declarations = ClassDeclaration.Matches(text);
            for (var i = 0; i < declarations.Count; i++)
            {
                var declaration = declarations[i];
                var bodyEnd = i + 1 < declarations.Count ? declarations[i + 1].Index : text.Length;
                var body = text[declaration.Index..bodyEnd];

                if (!body.Contains(SharedStoreVariable, StringComparison.Ordinal))
                {
                    continue;
                }

                var header = HeaderAbove(text, declaration.Index);
                if (header.Contains(LiveCollectionAttribute, StringComparison.Ordinal)
                    || header.Contains(OwnStoreMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                var line = text.AsSpan(0, declaration.Index).Count('\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{line}  {declaration.Groups[1].Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These classes read the shared DARLING_TEST_PG store but neither serialize against the other live "
            + "classes nor record why they do not:\n\n"
            + string.Join("\n", offenders)
            + "\n\nPick one, deliberately:\n"
            + $"  - Add {LiveCollectionAttribute} if the class touches the SHARED database. Rolling the writes "
            + "back does not exempt it: an uncommitted write still holds its row locks and still fires triggers.\n"
            + $"  - Add an \"{OwnStoreMarker}\" comment if the class mints its own database or cluster and so "
            + "cannot race the shared one. Serializing those is pure slowdown.\n\n"
            + "If the class is mostly pure with one live test, prefer SPLITTING the live test into its own "
            + "...LivePostgresTests class (the shape more than forty files here already use) over serializing "
            + "every pure test alongside it.");
    }

    /// <summary>
    /// The class's own attribute/comment region: the <see cref="HeaderLookbackLines"/> lines immediately above the
    /// declaration. Bounded so it cannot reach back into the previous class's body and read ITS attribute.
    /// </summary>
    private static string HeaderAbove(string text, int declarationIndex)
    {
        var start = declarationIndex;
        for (var lines = 0; lines < HeaderLookbackLines && start > 0; lines++)
        {
            var previous = text.LastIndexOf('\n', start - 1);
            if (previous < 0)
            {
                start = 0;
                break;
            }

            start = previous;
        }

        return text[start..declarationIndex];
    }

    /// <summary>
    /// Walks up from the test output directory to the repo root (the directory holding
    /// <c>PerformanceMonitor.sln</c>) and returns this project's source directory. Same walk-up idiom as
    /// <c>DocCommentHygieneTests.FindRepoRoot</c>.
    /// </summary>
    private static string? FindTestProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PerformanceMonitor.sln")))
            {
                var source = Path.Combine(directory.FullName, "Darling", "Darling.Tests");
                return Directory.Exists(source) ? source : null;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
