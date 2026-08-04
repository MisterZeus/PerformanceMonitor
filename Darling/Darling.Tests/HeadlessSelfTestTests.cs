/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the headless <c>--test</c> flag's PURE contracts (#2005) — the parts the development Mac can't
/// exercise live (the console attach and the ladder run ride CI's build plus the box dogfood): the flag
/// detection, the explicit-config-path rule (an explicit <c>--config</c> pair wins; the startup
/// first-non-option convention applies otherwise, with <c>--</c> flags never mistaken for file paths),
/// and the exit-code rule (0 unless a layer FAILED — Skipped and NotReached are not failures).
/// </summary>
public sealed class HeadlessSelfTestTests
{
    [Theory]
    [InlineData(new[] { "--test" }, true)]
    [InlineData(new[] { "--TEST" }, true)]
    [InlineData(new[] { "C:\\cfg\\darling.json", "--test" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--open-server", "prod-01" }, false)]
    [InlineData(new[] { "test" }, false)]
    public void IsRequested_MatchesTheFlagExactly_CaseInsensitive(string[] args, bool expected)
        => Assert.Equal(expected, HeadlessSelfTest.IsRequested(args));

    [Fact]
    public void ExplicitConfigPath_ConfigPairWins_OverPositional()
    {
        var args = new[] { "C:\\positional\\darling.json", "--test", "--config", "C:\\explicit\\darling.json" };
        Assert.Equal("C:\\explicit\\darling.json", HeadlessSelfTest.ExplicitConfigPath(args));
    }

    [Fact]
    public void ExplicitConfigPath_PositionalConvention_SkipsFlagsAndOptionPairs()
    {
        /* The startup rule, extended: --test and the upgrade-takeover flag are bare flags, --open-server
           consumes its value, and the first REAL argument is the config path. */
        var args = new[] { "--test", "--upgrade-takeover", "--open-server", "prod-01", "C:\\cfg\\darling.json" };
        Assert.Equal("C:\\cfg\\darling.json", HeadlessSelfTest.ExplicitConfigPath(args));
    }

    [Fact]
    public void ExplicitConfigPath_NoPathAnywhere_IsNull_SoTheRemainingRulesApply()
    {
        Assert.Null(HeadlessSelfTest.ExplicitConfigPath(new[] { "--test" }));
        Assert.Null(HeadlessSelfTest.ExplicitConfigPath(Array.Empty<string>()));

        /* A trailing --config with no value cannot dereference past the end. */
        Assert.Null(HeadlessSelfTest.ExplicitConfigPath(new[] { "--test", "--config" }));
    }

    [Fact]
    public void ExitCodeFor_ZeroUnlessALayerFailed()
    {
        static StoreConnectionSelfTest.LayerResult Layer(StoreConnectionSelfTest.LayerOutcome outcome)
            => new("layer", outcome, "detail", TimeSpan.Zero);

        Assert.Equal(0, HeadlessSelfTest.ExitCodeFor(new[]
        {
            Layer(StoreConnectionSelfTest.LayerOutcome.Passed),
            Layer(StoreConnectionSelfTest.LayerOutcome.Skipped),
        }));

        /* Skipped / NotReached are not failures on their own — but NotReached only ever accompanies a
           Failed layer, which is what flips the code. */
        Assert.Equal(1, HeadlessSelfTest.ExitCodeFor(new[]
        {
            Layer(StoreConnectionSelfTest.LayerOutcome.Passed),
            Layer(StoreConnectionSelfTest.LayerOutcome.Failed),
            Layer(StoreConnectionSelfTest.LayerOutcome.NotReached),
        }));
    }
}
