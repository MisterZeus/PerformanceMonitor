/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.Threading;
using PerformanceMonitor.Notifications;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the history stores' text fallback (#1830). The inputs are the REAL display strings the
/// alert producers emit — each row here names the producer that emits it — because the defect this
/// parser replaces was precisely a fallback whose happy-path tests used conveniently parseable
/// strings while production text carried labels.
/// </summary>
public sealed class AlertValueParserTests
{
    [Theory]
    [InlineData("87% (Total CPU)", 87)]      /* High CPU — the #1830 field case, stored 0 before */
    [InlineData("92% (SQL CPU)", 92)]        /* High CPU, SQL-only mode */
    [InlineData("+3 more incident(s) this cycle", 3)] /* per-event overflow trailer */
    [InlineData("92.5%", 92.5)]              /* plain percent — the old fallback's happy path */
    [InlineData("80%", 80)]
    [InlineData("1074", 1074)]               /* bare count (blocking/deadlocks) */
    [InlineData("-5", -5)]
    public void ParseOrDefault_ExtractsTheLeadingNumber(string text, double expected)
    {
        Assert.Equal(expected, AlertValueParser.ParseOrDefault(text));
    }

    [Theory]
    [InlineData("PRIMARY")]                  /* AG state strings — genuinely no number */
    [InlineData("Online")]
    [InlineData("caught up")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseOrDefault_NoNumber_ReturnsTheFallback(string? text)
    {
        Assert.Equal(0, AlertValueParser.ParseOrDefault(text));
        Assert.Equal(-1, AlertValueParser.ParseOrDefault(text, fallback: -1));
    }

    [Fact]
    public void ParseOrDefault_HonorsTheCurrentCulture_CommaDecimal()
    {
        /* The producers format with CurrentCulture, so the fallback must parse with it too — a
           de-DE "92,5%" is 92.5 there, and hardening to InvariantCulture would break exactly the
           locales the reporter of #1830 uses. */
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(92.5, AlertValueParser.ParseOrDefault("92,5% (SQL CPU)"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
