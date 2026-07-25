/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Net;

namespace PerformanceMonitor.Common;

/// <summary>
/// The DNS-rebinding guard every embedded HTTP surface in the product installs as its FIRST middleware:
/// Darling's web dashboard (#1576), Darling's MCP host, and Lite's MCP host (both #1648). Authored ONCE here,
/// in the library both apps already reference, because it had drifted — the web host got the guard, the two MCP
/// hosts did not, and an MCP surface that is no longer read-only (custom-view CRUD, <c>add_servers</c> /
/// <c>remove_server</c>, alert-config writes) sat tokenless on loopback behind no Host check at all.
///
/// <para><b>What it defends:</b> a loopback MCP/web bind is tokenless by design, so a browser running ON the
/// host that loads attacker content can be DNS-rebound to <c>127.0.0.1:{port}</c> and reach the whole surface
/// same-origin. The <c>application/json</c> content type would normally force a CORS preflight, but under a
/// rebind the browser considers the request same-origin, so no preflight applies — the Host header is what is
/// left to check, and per the MCP spec checking it is the application's job (the SDK does not).</para>
///
/// <para>PURE (no HTTP types, no logger) so it unit-tests without a server, and so this library keeps no
/// ASP.NET Core dependency — each host owns only the two lines that read <c>Request.Host.Host</c> and return
/// 400.</para>
/// </summary>
public static class HostHeaderGuard
{
    /// <summary>
    /// Is this request's <c>Host</c> header one this server actually binds? Allowed: an absent/empty Host
    /// (HTTP/1.0 and some health probes), the name <c>localhost</c>, any loopback IP literal
    /// (<c>127.0.0.1</c>, <c>::1</c>, and the IPv4-mapped-IPv6 form), and — only when LAN-exposed — the
    /// configured listen IP in <paramref name="networkListenIp"/>. Everything else is rejected, which is
    /// exactly the rebound foreign hostname resolving to 127.0.0.1. Pass <c>null</c> for
    /// <paramref name="networkListenIp"/> in loopback-only mode (Lite always; Darling until an operator opts
    /// into LAN exposure), where only the loopback Hosts pass.
    /// </summary>
    /// <param name="host">The request's Host header with any port suffix already split off.</param>
    /// <param name="networkListenIp">The configured LAN listen address, or null in loopback-only mode.</param>
    public static bool IsAllowedHost(string? host, IPAddress? networkListenIp)
    {
        if (string.IsNullOrEmpty(host))
        {
            return true;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var hostIp))
        {
            var ip = hostIp.IsIPv4MappedToIPv6 ? hostIp.MapToIPv4() : hostIp;
            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }

            if (networkListenIp is not null && ip.Equals(networkListenIp))
            {
                return true;
            }
        }

        return false;
    }
}
