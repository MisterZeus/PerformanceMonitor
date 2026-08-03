/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using PerformanceMonitor.Ui;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins <see cref="ServerOverviewFilter"/> — the pure server-search match rule shared by Lite's Overview and
/// the Darling viewer's sidebar tree (the web viewer mirrors the same rule in JavaScript). Mirror of
/// Lite.Tests' copy (the ServerOverviewSort precedent: the shared pure function is tested identically in each
/// suite). The rule is: empty query matches everything; otherwise a case-insensitive substring against ANY
/// supplied field.
/// </summary>
public class ServerOverviewFilterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceQuery_MatchesEverything(string? query)
    {
        Assert.True(ServerOverviewFilter.Matches(query, "sql-prod-01"));
        Assert.True(ServerOverviewFilter.Matches(query));                 // even with no fields at all
        Assert.True(ServerOverviewFilter.Matches(query, (string?)null));  // and with only null fields
    }

    [Fact]
    public void Matches_IsCaseInsensitiveSubstring_NotPrefix()
    {
        Assert.True(ServerOverviewFilter.Matches("prod", "SQL-PROD-01"));   // case-insensitive
        Assert.True(ServerOverviewFilter.Matches("PROD", "sql-prod-01"));
        Assert.True(ServerOverviewFilter.Matches("od-0", "sql-prod-01"));   // interior substring, not just prefix
    }

    [Fact]
    public void Matches_AnyField_NullOrEmptyFieldsSkipped()
    {
        /* The viewer passes display name, instance name, then each tag name — a hit on any is a match. */
        Assert.True(ServerOverviewFilter.Matches("production", "sql-01", "sql-01.corp", "Production"));
        Assert.True(ServerOverviewFilter.Matches("corp", "sql-01", "sql-01.corp"));
        Assert.False(ServerOverviewFilter.Matches("dev", "sql-01", "sql-01.corp", "Production"));

        /* Null/empty fields never throw and never match. */
        Assert.True(ServerOverviewFilter.Matches("sql", null, "", "sql-01"));
        Assert.False(ServerOverviewFilter.Matches("sql", null, ""));
    }

    [Fact]
    public void Matches_QueryIsTrimmed_SoRawBoxValueBehavesLikeNormalized()
    {
        Assert.True(ServerOverviewFilter.Matches("  prod  ", "sql-prod-01"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("prod", "prod")]
    [InlineData("  prod  ", "prod")]
    public void Normalize_TrimsAndCollapsesEmptyToNull(string? input, string? expected)
    {
        Assert.Equal(expected, ServerOverviewFilter.Normalize(input));
    }
}
