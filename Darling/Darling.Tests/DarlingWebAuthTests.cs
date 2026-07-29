/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using PerformanceMonitor.Darling.Service.Mcp;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// #1562 web dashboard browser auth (network mode). The route decision is a PURE function over the resolved
/// facts (<see cref="DarlingWebHostService.DecideWebAuth"/>) so the whole matrix pins without a server, and the
/// HMAC session cookie's sign/verify/tamper/expiry round-trip pins the token→cookie exchange. The verdict enum
/// is internal, so the matrix compares its name (an internal type cannot appear in a public test signature).
/// </summary>
public sealed class DarlingWebAuthTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] OtherKey = RandomNumberGenerator.GetBytes(32);

    private static IPNetwork Cidr => IPNetwork.Parse("192.168.1.0/24");

    /* ---- ROUTE AUTH MATRIX ---- */

    /* #1649: loopback no longer passes tokenless. This method only runs in NETWORK mode, and the surface
       stopped being read-only at Custom Views v2 (view CRUD + /api/compose/run), so a local process could
       read and mutate with no credential while LAN-exposed. Loopback is now exempt from the CIDR test ONLY
       — it authenticates like any other remote, mirroring the MCP host. The three loopback rows below are
       the behavior change: same inputs, ShowLogin instead of Allow. */
    [Theory]
    [InlineData("127.0.0.1", false, false, "ShowLogin")]              // loopback, no credential -> login form, NOT a free pass
    [InlineData("::1", false, false, "ShowLogin")]                    // IPv6 loopback
    [InlineData("::ffff:127.0.0.1", false, false, "ShowLogin")]       // IPv4-mapped loopback
    [InlineData("127.0.0.1", true, false, "Allow")]                   // loopback + valid cookie -> in
    [InlineData("127.0.0.1", false, true, "SetCookieAndRedirect")]    // loopback + valid token -> cookie + 302, same exchange as a LAN client
    [InlineData("::ffff:127.0.0.1", false, true, "SetCookieAndRedirect")] // the IPv4-mapped form takes the same path (no unwrap drift)
    [InlineData("192.168.1.50", false, true, "SetCookieAndRedirect")] // in-CIDR + valid token -> cookie + 302
    [InlineData("192.168.1.50", true, false, "Allow")]                // in-CIDR + valid cookie
    [InlineData("192.168.1.50", true, true, "Allow")]                 // cookie is checked before the token
    [InlineData("10.0.0.5", true, true, "Forbid")]                    // out-of-CIDR, even WITH a cookie+token
    [InlineData("192.168.1.50", false, false, "ShowLogin")]           // in-CIDR, no cookie/token (or expired/tampered cookie -> hasValidCookie=false)
    public void DecideWebAuth_RouteMatrix(string remote, bool hasValidCookie, bool hasValidToken, string expected)
    {
        var action = DarlingWebHostService.DecideWebAuth(IPAddress.Parse(remote), Cidr, hasValidCookie, hasValidToken);
        Assert.Equal(expected, action.ToString());
    }

    [Fact]
    public void DecideWebAuth_NullRemote_Forbids()
        => Assert.Equal("Forbid", DarlingWebHostService.DecideWebAuth(null, Cidr, hasValidCookie: true, hasValidToken: true).ToString());

    /* ---- Host-header allowlist: the DNS-rebinding guard (security review M1; #1576 extends it to BOTH modes) ----
       The pure decision is unchanged; #1576 wires it into the loopback-only pipeline too (null listen IP), which
       these null-listenIp rows pin: loopback Hosts pass, a rebound foreign Host fails. */

    [Theory]
    [InlineData("localhost", "192.168.1.205", true)]      // loopback name
    [InlineData("LOCALHOST", "192.168.1.205", true)]      // case-insensitive
    [InlineData("127.0.0.1", "192.168.1.205", true)]      // loopback IP
    [InlineData("::1", "192.168.1.205", true)]            // IPv6 loopback
    [InlineData("192.168.1.205", "192.168.1.205", true)] // the configured listen IP (the real operator, direct)
    [InlineData("", "192.168.1.205", true)]              // empty Host (HTTP/1.0 / probes) is allowed
    [InlineData("evil.com", "192.168.1.205", false)]      // rebound foreign hostname -> rejected
    [InlineData("192.168.1.205", null, false)]            // loopback-only mode: the listen IP is NOT an allowed host
    [InlineData("attacker.internal", null, false)]        // rebound name in loopback-only mode -> rejected
    /* #1576: loopback-only mode (null listen IP) — loopback Hosts pass, a rebound foreign Host is rejected. */
    [InlineData("localhost", null, true)]                 // loopback name passes in loopback-only mode
    [InlineData("127.0.0.1", null, true)]                 // loopback IP passes in loopback-only mode
    [InlineData("::1", null, true)]                       // IPv6 loopback passes in loopback-only mode
    [InlineData("", null, true)]                          // empty Host still allowed in loopback-only mode
    [InlineData("evil.com", null, false)]                 // rebound foreign host in loopback-only mode -> rejected
    public void IsAllowedHost_RejectsReboundForeignHosts(string host, string? listenIp, bool expected)
    {
        var ip = listenIp is null ? null : IPAddress.Parse(listenIp);
        Assert.Equal(expected, DarlingWebHostService.IsAllowedHost(host, ip));
    }

    /* ---- Open-redirect guard on the token-strip 302 (security review M2) ---- */

    [Theory]
    [InlineData("/dashboard", "/dashboard")]            // ordinary path untouched
    [InlineData("/", "/")]                              // root
    [InlineData("", "/")]                               // empty -> root
    [InlineData("//evil.com/", "/evil.com/")]           // protocol-relative -> collapsed single slash
    [InlineData("/\\evil.com", "/evil.com")]            // slash-backslash trick -> collapsed
    [InlineData("///a", "/a")]                          // longer leading run
    [InlineData("/fleet?x=1", "/fleet?x=1")]            // preserved query on a safe path
    public void SanitizeRedirectPath_ForcesSingleSlashSiteRelative(string input, string expected)
        => Assert.Equal(expected, DarlingWebHostService.SanitizeRedirectPath(input));

    /* ---- SESSION COOKIE sign / verify / tamper / expiry ---- */

    [Fact]
    public void SessionCookie_SignedThenVerified_RoundTrips()
    {
        var now = DateTimeOffset.UtcNow;
        var cookie = DarlingWebHostService.BuildSessionCookieValue(Key, now.AddHours(12));
        Assert.True(DarlingWebHostService.TryValidateSessionCookie(cookie, Key, now));
    }

    [Fact]
    public void SessionCookie_WrongKey_Fails()
    {
        var now = DateTimeOffset.UtcNow;
        var cookie = DarlingWebHostService.BuildSessionCookieValue(Key, now.AddHours(12));
        /* A restart regenerates the signing key, which is exactly this case — old sessions no longer verify. */
        Assert.False(DarlingWebHostService.TryValidateSessionCookie(cookie, OtherKey, now));
    }

    [Fact]
    public void SessionCookie_Expired_Fails()
    {
        /* Signed 24h ago with a 12h life -> expired 12h before "now". */
        var signedAt = DateTimeOffset.UtcNow.AddHours(-24);
        var cookie = DarlingWebHostService.BuildSessionCookieValue(Key, signedAt.AddHours(12));
        Assert.False(DarlingWebHostService.TryValidateSessionCookie(cookie, Key, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SessionCookie_TamperedSignature_Fails()
    {
        var now = DateTimeOffset.UtcNow;
        var cookie = DarlingWebHostService.BuildSessionCookieValue(Key, now.AddHours(12));

        /* Tamper the FIRST character of the base64url signature (the char right after the '.'), not the last.
           The signature is base64url(HMAC-SHA256) = 43 chars for 32 bytes; the LAST char encodes only 4 data
           bits + 2 padding bits, so flipping it (e.g. 'A'->'B') can leave the decoded bytes identical — a
           no-op "tamper" that validation rightly accepts, which made the old last-char flip flaky ~1 run in 16.
           The first signature char is fully data-bearing (6 bits), so any flip always changes the decoded MAC. */
        var dot = cookie.IndexOf('.', StringComparison.Ordinal);
        var sigStart = dot + 1;
        var tampered = cookie[..sigStart] + (cookie[sigStart] == 'A' ? 'B' : 'A') + cookie[(sigStart + 1)..];
        Assert.False(DarlingWebHostService.TryValidateSessionCookie(tampered, Key, now));
    }

    [Fact]
    public void SessionCookie_TamperedExpiry_Fails()
    {
        var now = DateTimeOffset.UtcNow;
        var cookie = DarlingWebHostService.BuildSessionCookieValue(Key, now.AddHours(1));
        var signatureWithDot = cookie.Substring(cookie.IndexOf('.'));
        /* Extend the expiry 10 years out but keep the OLD signature — the HMAC is over the expiry, so it fails. */
        var forgedExpiry = now.AddYears(10).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        Assert.False(DarlingWebHostService.TryValidateSessionCookie(forgedExpiry + signatureWithDot, Key, now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-dot")]
    [InlineData(".onlysignature")]
    [InlineData("12345.")]                 // empty signature part
    [InlineData("notanumber.YWJjZA")]      // non-numeric expiry
    public void SessionCookie_Malformed_Fails(string? value)
        => Assert.False(DarlingWebHostService.TryValidateSessionCookie(value, Key, DateTimeOffset.UtcNow));
}
