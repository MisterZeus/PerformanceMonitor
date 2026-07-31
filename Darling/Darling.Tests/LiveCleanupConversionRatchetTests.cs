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
using Xunit;

namespace Darling.Tests;

/// <summary>
/// No live test cleans up on the connection its own body used (#1902).
///
/// <para><b>The defect this closes.</b> A <c>finally</c> that runs its teardown on the BODY's connection and
/// throws straight out of the finally reports the teardown's error instead of the test's — a throw from a
/// finally REPLACES the exception already in flight — and abandons every statement after the throwing one,
/// leaving debris the next run inherits as an unrelated flake. The two halves compound: it is the body's
/// failure that closes the connection, so the teardown fails BECAUSE of the thing it then hides. #1896
/// demonstrated it end to end — with a body failure and a cleanup failure forced into one test, the old shape
/// reported the cleanup's <c>42883</c> and lost the body's exception entirely.</para>
///
/// <para><b>This started as a ratchet and is now an invariant.</b> Through batches one and two it was a
/// ceiling — 126 sites, then 87, then 19 — that had to come DOWN with each batch and could never go up, so the
/// backlog could not regrow behind the conversions. Batch three took the last of them, so the number is zero
/// and the assertion now says what it always meant: there are none. The ceiling constant is gone with it, on
/// the grounds that a number which can only be zero is a worse way of writing zero.</para>
///
/// <para><b>What counts as compliant.</b> Only going through <see cref="LiveStoreCleanup"/> — either
/// <c>RunAsync</c>, which opens its own connection, or <c>RunOwnedAsync</c> for the few teardowns that MUST
/// use resources the test already holds (a session-scoped <c>lock_timeout</c>, a blocking transaction's own
/// rollback, a store that owns its data source). Opening a fresh connection BY HAND is deliberately not
/// accepted: it is half the fix, it still throws from the finally, and an exemption shaped "this one is
/// correct by hand" is one a later incorrect site inherits. The two live classes that had been doing exactly
/// that were converted rather than exempted.</para>
/// </summary>
public sealed class LiveCleanupConversionRatchetTests
{
    [Fact]
    public void NoLiveTestCleansUpOnItsOwnBodysConnection()
    {
        var directory = FindTestProjectDirectory();
        Assert.True(directory is not null,
            "could not locate Darling/Darling.Tests by walking up from the test output directory.");

        var offenders = Offenders(directory!);

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} live-test teardown(s) do not go through LiveStoreCleanup (#1902). Wrap the "
            + "finally body in LiveStoreCleanup.RunAsync(connectionString, bodySucceeded, ...) — or "
            + "RunOwnedAsync when the cleanup must use connections the test already holds — and set "
            + "bodySucceeded as the last statement of the try. Opening a connection by hand is not enough: it "
            + "leaves the throw-from-finally that replaces the body's exception."
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Every <c>finally</c> in a shared-store live class whose body does not go through
    /// <see cref="LiveStoreCleanup"/>, reported as <c>file:line</c>.
    ///
    /// <para>Own-store classes are exempt for the same reason #1776 exempts them: they mint and drop their own
    /// database, so an abandoned teardown cannot reach anyone else. File and process teardown is excluded
    /// because it is not store state and has nothing to do with a connection.</para>
    ///
    /// <para><b>The block is brace-matched, not a fixed line window.</b> Batches one and two scanned thirteen
    /// lines after the <c>finally</c>, which a long explanatory comment pushes the actual statements past — so
    /// the richest teardown in the suite (the retention test's, which carries a twelve-line comment) counted as
    /// an offender while being fully converted. The same blind spot works the other way and matters more: an
    /// genuinely unconverted teardown could hide behind a comment of its own. Matching braces reads the block
    /// the compiler reads.</para>
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
                if (lines[i].Trim() != "finally" || i + 1 >= lines.Length || lines[i + 1].Trim() != "{")
                {
                    continue;
                }

                var block = BlockAt(lines, i + 1);
                if (block.Contains("LiveStoreCleanup", StringComparison.Ordinal))
                {
                    continue;
                }

                if (block.Contains("File.", StringComparison.Ordinal)
                    || block.Contains("Directory.", StringComparison.Ordinal)
                    || block.Contains("Kill", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(path)}:{i + 1}");
            }
        }

        return offenders;
    }

    /// <summary>The text of the brace-delimited block whose opening brace is on <paramref name="openLine"/>.</summary>
    private static string BlockAt(string[] lines, int openLine)
    {
        var depth = 0;
        for (var i = openLine; i < lines.Length; i++)
        {
            depth += lines[i].Count(c => c == '{') - lines[i].Count(c => c == '}');
            if (depth == 0)
            {
                return string.Join("\n", lines.Skip(openLine).Take(i - openLine + 1));
            }
        }

        /* Unbalanced braces mean the parse is wrong, and a parse that cannot find the end of a block must not
           quietly report that block as clean. Returning the remainder makes it fail loudly instead. */
        return string.Join("\n", lines.Skip(openLine));
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
