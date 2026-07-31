/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// A one-number ratchet over the #1902 conversion, so the backlog can only shrink.
///
/// <para><b>What it guards.</b> A live class's <c>finally</c> that cleans up on the BODY's connection and throws
/// straight out of the finally reports the teardown's error instead of the test's, and abandons every statement
/// after the throwing one (#1794's shape, demonstrated end to end in #1896). #1902 is the backlog of those, being
/// converted onto <see cref="LiveStoreCleanup"/> in batches. This test counts what is left.</para>
///
/// <para><b>Why a ceiling rather than a list.</b> A named allowlist of eighty-odd sites is a merge-conflict
/// generator that every batch has to edit by hand, and a list that is edited by hand is a list people delete
/// entries from for the wrong reasons. A single number cannot be partially wrong: a new unconverted teardown
/// pushes the count above the ceiling and the build goes red, and a batch that lands has to lower it, which is
/// the one edit worth forcing. It is deliberately not an equality assertion while the backlog is non-zero —
/// converting MORE than a batch promised should never fail a build.</para>
///
/// <para><b>When the ceiling reaches zero</b> the remaining assertion becomes the real invariant — no live class
/// cleans up on the body's connection — and #1902 closes. Until then this is the thing that stops the backlog
/// growing behind the batches, which is the failure mode a purely manual sweep has: sixty classes, three PRs,
/// and any pull request in between free to add a sixty-first.</para>
/// </summary>
public sealed class LiveCleanupConversionRatchetTests
{
    /// <summary>
    /// Unconverted teardowns remaining. MUST ONLY EVER GO DOWN.
    ///
    /// <para>126 when #1902 was filed; 87 after batch one (the config-writing and server-registering classes,
    /// which were the highest blast radius — a leaked <c>config_mute_rules</c> row mutes another test's alerts
    /// and a leaked <c>collect.servers</c> row is a phantom server in every fleet read). Batches two and three
    /// take the rest.</para>
    /// </summary>
    private const int Ceiling = 87;

    [Fact]
    public void UnconvertedLiveTeardowns_NeverExceedTheRatchet()
    {
        var directory = FindTestProjectDirectory();
        Assert.True(directory is not null,
            "could not locate Darling/Darling.Tests by walking up from the test output directory.");

        var offenders = Offenders(directory!);

        Assert.True(offenders.Count <= Ceiling,
            $"{offenders.Count} live-test teardowns still clean up on the body's connection, above the #1902 "
            + $"ratchet of {Ceiling}. A NEW one was added: convert it to LiveStoreCleanup.RunAsync (or "
            + "RunOwnedAsync when the cleanup must use connections the test already holds) rather than raising "
            + "the ceiling — the ceiling only ever goes down."
            + Environment.NewLine + string.Join(Environment.NewLine, offenders.Take(20)));
    }

    /// <summary>
    /// The other direction: when a batch lands, the ceiling must come down with it. Without this a batch could
    /// convert forty teardowns and leave the ratchet where it was, which quietly re-opens the room for forty new
    /// ones — the guard would still be green and would be guarding nothing.
    /// </summary>
    [Fact]
    public void TheRatchet_IsNotLeftSlackAfterABatch()
    {
        var directory = FindTestProjectDirectory();
        Assert.True(directory is not null, "could not locate Darling/Darling.Tests.");

        var offenders = Offenders(directory!);

        Assert.True(offenders.Count == Ceiling,
            $"the #1902 ratchet is set to {Ceiling} but only {offenders.Count} unconverted teardowns remain. "
            + "Lower Ceiling to " + offenders.Count.ToString(CultureInfo.InvariantCulture)
            + " so the slack cannot be spent on new ones.");
    }

    /// <summary>
    /// Every <c>finally</c> in a shared-store live class whose body does not go through
    /// <see cref="LiveStoreCleanup"/>, reported as <c>file:line</c>.
    ///
    /// <para>Own-store classes are exempt for the same reason #1776 exempts them: they mint and drop their own
    /// database, so an abandoned teardown cannot reach anyone else. File and process teardown is excluded because
    /// it is not store state and has nothing to do with a connection.</para>
    /// </summary>
    private static List<string> Offenders(string directory)
    {
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(directory, "*.cs").OrderBy(p => p, StringComparer.Ordinal))
        {
            var source = File.ReadAllText(path);
            if (!source.Contains("[Collection(\"live-postgres\")]", StringComparison.Ordinal))
            {
                continue;
            }

            if (source.Contains("#1776 own-store", StringComparison.Ordinal)
                || source.Contains("ScratchPostgres", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = source.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim() != "finally")
                {
                    continue;
                }

                var window = string.Join("\n", lines.Skip(i + 1).Take(13));
                if (window.Contains("LiveStoreCleanup", StringComparison.Ordinal))
                {
                    continue;
                }

                if (window.Contains("File.", StringComparison.Ordinal)
                    || window.Contains("Directory.", StringComparison.Ordinal)
                    || window.Contains("Kill", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(path)}:{i + 1}");
            }
        }

        return offenders;
    }

    /// <summary>
    /// Walks up from the test output directory to the repo root (the directory holding
    /// <c>PerformanceMonitor.sln</c>) and returns this project's source directory. Same walk-up idiom as
    /// <c>LivePostgresCollectionHygieneTests.FindTestProjectDirectory</c>.
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
