/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Net;
using PerformanceMonitor.Common;
using PerformanceMonitor.Darling.Service.Mcp;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1648: the DNS-rebinding guard on Darling's two Kestrel hosts. Two halves, because the defect had two halves.
///
/// <para><b>The decision</b> — <see cref="HostHeaderGuard.IsAllowedHost"/>, now shared with Lite. The web host's
/// own matrix (<see cref="DarlingWebAuthTests"/>) still calls <c>DarlingWebHostService.IsAllowedHost</c> and is
/// deliberately left untouched: it passing unchanged IS the proof that lifting the body into
/// <see cref="HostHeaderGuard"/> (via <c>DarlingHostBinding</c>) kept that host byte-for-byte. What is added
/// here is the forwarders-agree check, so a future edit cannot make one host's guard diverge from the other's.</para>
///
/// <para><b>The wiring</b> — the actual bug. The MCP host's decision function was never the problem; the
/// middleware simply was not installed, in either mode, so a pure-function test would have passed on the
/// vulnerable build. These tests parse the REAL host sources (copied beside the test binary by the csproj) and
/// assert the guard is the FIRST <c>_app.Use</c> after <c>builder.Build()</c> and sits AHEAD of the
/// <c>if (networkMode)</c> gates — i.e. it runs in loopback mode too, which is the mode that was exposed.</para>
/// </summary>
public sealed class HostHeaderGuardTests
{
    /* ---------------- the pure decision, both modes ---------------- */

    /// <summary>Network mode: the configured listen IP is a legitimate Host, loopback still is, nothing else.</summary>
    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.53", true)]          // the whole 127/8 loopback range
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]    // IPv4-mapped loopback
    [InlineData("192.168.1.205", true)]       // the configured listen IP: the real operator, direct
    [InlineData("", true)]                    // absent Host (HTTP/1.0, some health probes)
    [InlineData("evil.com", false)]           // the rebound foreign hostname
    [InlineData("192.168.1.206", false)]      // a LAN IP we do NOT bind
    [InlineData("darling.internal", false)]
    public void IsAllowedHost_NetworkMode_AllowsLoopbackAndTheListenIp_RejectsEverythingElse(string host, bool expected)
        => Assert.Equal(expected, HostHeaderGuard.IsAllowedHost(host, IPAddress.Parse("192.168.1.205")));

    /// <summary>Loopback-only mode (null listen IP) — the default, and the mode the MCP hosts were exposed in.</summary>
    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("", true)]
    [InlineData("evil.com", false)]           // the attack: a domain re-resolved to 127.0.0.1
    [InlineData("attacker.internal", false)]
    [InlineData("192.168.1.205", false)]      // no listen IP configured, so a LAN IP is NOT an allowed Host
    public void IsAllowedHost_LoopbackOnlyMode_AllowsOnlyLoopback(string host, bool expected)
        => Assert.Equal(expected, HostHeaderGuard.IsAllowedHost(host, networkListenIp: null));

    /// <summary>
    /// The web host's forwarder must stay identical to the shared decision — that is the whole point of lifting
    /// it out. Compared across the full matrix rather than spot-checked, in both modes.
    /// </summary>
    [Fact]
    public void DarlingWebHostServiceForwarder_AgreesWithTheSharedGuard_AcrossTheWholeMatrix()
    {
        string[] hosts = ["localhost", "LOCALHOST", "127.0.0.1", "::1", "::ffff:127.0.0.1", "192.168.1.205", "", "evil.com", "10.0.0.5"];
        IPAddress?[] listens = [null, IPAddress.Parse("192.168.1.205")];

        foreach (var listen in listens)
        {
            foreach (var host in hosts)
            {
                Assert.Equal(
                    HostHeaderGuard.IsAllowedHost(host, listen),
                    DarlingWebHostService.IsAllowedHost(host, listen));
            }
        }
    }

    /* ---------------- the wiring: the guard is installed FIRST, in BOTH modes ---------------- */

    private static string ReadHostSource(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        Assert.True(File.Exists(path), $"{fileName} was not copied beside the test binary — check the csproj None/Link item.");

        var source = File.ReadAllText(path);
        /* Guard the guard: if the host were renamed or restructured past recognition, the assertions below
           could pass vacuously on an empty/mismatched parse. Pin the anchors they all key off. */
        Assert.Contains("_app = builder.Build();", source, StringComparison.Ordinal);
        Assert.Contains("_app.Use(", source, StringComparison.Ordinal);
        return source;
    }

    /// <summary>The text of the first middleware registered after the app is built.</summary>
    private static string FirstMiddlewareAfterBuild(string source)
    {
        var afterBuild = source[(source.IndexOf("_app = builder.Build();", StringComparison.Ordinal) + 1)..];
        var firstUse = afterBuild.IndexOf("_app.Use(", StringComparison.Ordinal);
        Assert.True(firstUse >= 0, "no middleware is registered after builder.Build()");

        /* The first Use's lambda body — long enough to cover the guard's if/return, short enough that a
           SECOND Use's body cannot bleed in and satisfy the assertion by accident. */
        var body = afterBuild[firstUse..];
        var nextUse = body.IndexOf("_app.Use(", 1, StringComparison.Ordinal);
        return nextUse > 0 ? body[..nextUse] : body;
    }

    [Theory]
    [InlineData("DarlingMcpHostService.cs")]
    [InlineData("DarlingWebHostService.cs")]
    public void EachHost_InstallsTheHostHeaderGuard_AsItsFirstMiddleware(string fileName)
    {
        var first = FirstMiddlewareAfterBuild(ReadHostSource(fileName));

        Assert.Contains("IsAllowedHost(context.Request.Host.Host", first, StringComparison.Ordinal);
        Assert.Contains("Status400BadRequest", first, StringComparison.Ordinal);
    }

    /// <summary>
    /// The #1648 defect exactly: the MCP host's access-control middleware sat inside <c>if (networkMode)</c>, so
    /// the loopback bind — tokenless, and no longer read-only — got no guard at all. Assert the Host check is
    /// registered BEFORE that conditional, which is what makes it unconditional.
    /// </summary>
    [Theory]
    [InlineData("DarlingMcpHostService.cs")]
    [InlineData("DarlingWebHostService.cs")]
    public void EachHost_RunsTheGuardInBothModes_NotOnlyWhenNetworkExposed(string fileName)
    {
        var source = ReadHostSource(fileName);
        var afterBuild = source[(source.IndexOf("_app = builder.Build();", StringComparison.Ordinal) + 1)..];

        var guard = afterBuild.IndexOf("IsAllowedHost(context.Request.Host.Host", StringComparison.Ordinal);
        var networkOnly = afterBuild.IndexOf("if (networkMode)", StringComparison.Ordinal);

        Assert.True(guard >= 0, "the Host-header guard middleware is missing");
        Assert.True(networkOnly >= 0, "expected a network-mode-only middleware block to exist after Build()");
        Assert.True(
            guard < networkOnly,
            "the Host-header guard must be registered BEFORE the network-mode-only block — inside it, the " +
            "tokenless loopback bind is left unguarded, which is issue #1648.");
    }

    /// <summary>And it must precede the endpoint mapping, or a request reaches a handler before being judged.</summary>
    [Fact]
    public void McpHost_RunsTheGuardBeforeMapMcp()
    {
        var source = ReadHostSource("DarlingMcpHostService.cs");
        var guard = source.IndexOf("IsAllowedHost(context.Request.Host.Host", StringComparison.Ordinal);
        var mapMcp = source.IndexOf("_app.MapMcp();", StringComparison.Ordinal);

        Assert.True(mapMcp >= 0, "MapMcp is no longer called — this guard needs rewriting");
        Assert.True(guard >= 0 && guard < mapMcp, "the Host-header guard must run before MapMcp registers the MCP endpoints");
    }
}
