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
/// #1922: <c>CREATE EXTENSION IF NOT EXISTS timescaledb</c> TERMINATES THE BACKEND when the library is on
/// disk but absent from <c>shared_preload_libraries</c> — it is not an ordinary ERROR.
/// <c>TimescaleSupport.TryEnableAsync</c> turns that into <c>false</c> like any other failure, so its
/// contract reads as "carry on in plain-PostgreSQL mode" while the connection it was handed is dead. Anything
/// that keeps using that connection then fails with <c>InvalidOperationException: Connection is not open</c>
/// from a frame several calls downstream, naming the real cause nowhere: the #1794 masking signature, in
/// setup rather than teardown.
///
/// <para>Both invariants below are source-parsed, and that is the honest way to hold them. Reproducing the
/// crash needs a PostgreSQL deliberately misconfigured in one specific way, which no CI rig is and none
/// should be made to be; what actually regresses is a call site being written the unsafe way, and that is
/// textual.</para>
/// </summary>
public sealed class TimescaleEnableConnectionSafetyTests
{
    /// <summary>
    /// Every test-side call goes through <see cref="LiveTimescaleProbe"/> (which risks only its own
    /// connection) or is asserted true on the spot.
    ///
    /// <para>The <c>Assert.True</c> sites are deliberately still allowed rather than swept: on a dead
    /// connection they fail immediately with their own message ("the rig is expected to have TimescaleDB"),
    /// which is a true and useful diagnosis rather than a masked one. What is banned is the shape that
    /// masks — taking the result and carrying on with the same connection.</para>
    /// </summary>
    [Fact]
    public void NoLiveTest_KeepsUsingAConnectionItHandedToTryEnableAsync()
    {
        var directory = FindTestProjectDirectory();
        Assert.True(directory is not null, "could not locate Darling/Darling.Tests from the test output directory.");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory!, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);

            /* The probe is where the guarded call is SUPPOSED to live, and this file names the method in
               prose throughout. */
            if (name is nameof(LiveTimescaleProbe) + ".cs" or nameof(TimescaleEnableConnectionSafetyTests) + ".cs")
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains("TimescaleSupport.TryEnableAsync(", StringComparison.Ordinal))
                {
                    continue;
                }

                /* A doc comment mentioning it is not a call. */
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("///", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!IsCheckedOnTheSpot(lines, i))
                {
                    offenders.Add($"{name}:{i + 1}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "these call TimescaleSupport.TryEnableAsync on a connection they keep using, so an unpreloaded " +
            "TimescaleDB masks the real cause behind \"Connection is not open\" (#1922). Route them through " +
            "LiveTimescaleProbe.TryEnableAsync, or assert the result immediately — either inline in " +
            $"Assert.True(...) or captured and asserted within the next three lines: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// The product half of the same invariant, and the reason #1922 was fixed test-side ONLY.
    ///
    /// <para><c>DarlingWorker</c> is safe by construction: it opens a DEDICATED connection for the TimescaleDB
    /// block and gates every subsequent call on the returned flag, so a <c>false</c> return means nothing
    /// touches that connection again before it is disposed. That is a property of how the block happens to be
    /// written, not something the compiler holds — move one call out from under the flag and the service
    /// acquires exactly the masking the tests just shed. This pins it so "the product is safe" stays a fact
    /// rather than a thing that was true once.</para>
    /// </summary>
    [Fact]
    public void DarlingWorker_TouchesTheTimescaleConnectionOnlyBehindTheAvailabilityFlag()
    {
        var worker = File.ReadAllText(FindRepoFile(Path.Combine(
            "Darling", "PerformanceMonitor.Darling.Service", "DarlingWorker.cs")));

        /* The block: from the dedicated connection to the catch that degrades to plain-PostgreSQL mode. */
        var open = worker.IndexOf("await using var timescaleConnection", StringComparison.Ordinal);
        Assert.True(open >= 0, "DarlingWorker no longer opens a dedicated connection for the TimescaleDB block.");

        var close = worker.IndexOf("catch (Exception ex) when (ex is not OperationCanceledException)", open, StringComparison.Ordinal);
        Assert.True(close > open, "could not find the end of DarlingWorker's TimescaleDB block.");

        var block = worker[open..close];

        var gate = block.IndexOf("if (_timescaleAvailable)", StringComparison.Ordinal);
        Assert.True(gate >= 0, "DarlingWorker's TimescaleDB block no longer gates on _timescaleAvailable.");

        /* Every TimescaleSupport call in the block except the enable itself must sit after the gate. */
        var ungated = Regex.Matches(block, @"TimescaleSupport\.(\w+)")
            .Where(m => m.Groups[1].Value != "TryEnableAsync" && m.Index < gate)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.True(ungated.Count == 0,
            "these run on the TimescaleDB connection before _timescaleAvailable has been checked, so a " +
            "CREATE EXTENSION that killed the backend would surface as \"Connection is not open\" instead of " +
            $"the real cause (#1922): {string.Join(", ", ungated)}");

        /* And the connection stays dedicated — a shared one would carry the damage out of the block whatever
           the gate does. */
        Assert.Contains("await using var timescaleConnection = await postgres.OpenConnectionAsync", worker, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the call on <paramref name="index"/> has its result checked before the connection could be
    /// used again — either asserted inline, or captured and asserted within the next few lines.
    ///
    /// <para>Both shapes are accepted deliberately. An inline-only rule would flag this, which is safe:</para>
    /// <code>
    /// var enabled = await TimescaleSupport.TryEnableAsync(connection, null, ct);
    /// Assert.True(enabled, "the rig is expected to have TimescaleDB");
    /// </code>
    /// <para>and a guard that fires on a safe shape is worse than no guard — the next person routes around it
    /// rather than reading it. The capture form is tied to its OWN variable rather than accepting any nearby
    /// <c>Assert.True</c>, so an unrelated assertion two lines down cannot launder an unchecked call.</para>
    /// </summary>
    private static bool IsCheckedOnTheSpot(string[] lines, int index)
    {
        var line = lines[index];
        if (line.Contains("Assert.True(", StringComparison.Ordinal))
        {
            return true;
        }

        var captured = Regex.Match(line, @"var\s+(\w+)\s*=\s*await\s+TimescaleSupport\.TryEnableAsync\(");
        if (!captured.Success)
        {
            return false;
        }

        var variable = captured.Groups[1].Value;
        for (var i = index + 1; i < Math.Min(index + 4, lines.Length); i++)
        {
            if (Regex.IsMatch(lines[i], $@"Assert\.True\(\s*{Regex.Escape(variable)}\b"))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FindTestProjectDirectory()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "Darling", "Darling.Tests");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException($"Could not locate {relativePath} walking up from {AppContext.BaseDirectory}");
    }
}
