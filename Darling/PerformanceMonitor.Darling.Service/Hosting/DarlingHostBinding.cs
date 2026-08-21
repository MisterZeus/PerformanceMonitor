/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Darling.Service.Hosting;

/// <summary>
/// The surface-agnostic bind/auth helpers shared by the two optional Kestrel hosts — the MCP host
/// (<see cref="Mcp.DarlingMcpHostService"/>) and the web host (<see cref="Mcp.DarlingWebHostService"/>).
/// Extracted so the LAN-exposure decision ladder, the in-app CIDR check, the loopback-listener collision
/// guard, and the constant-time token compare live ONCE, with ONE test suite, instead of being copy-pasted
/// per surface (darling-network-endpoints anti-drift). The MCP host is refactored onto this class (its
/// behavior byte-for-byte unchanged — its pre-existing tests stay green untouched, the refactor proof); the
/// web host is built on it from the start.
///
/// <para>Everything here is PURE (no logger, no server). It used to also host the best-effort firewall
/// reconcile; that moved to <see cref="DarlingFirewallCheck"/> when the runtime stopped writing rules and
/// started only verifying them (#1771). The (mode, reason) split lets the caller map a reason to a log
/// severity (Round-4 #7) while the pure ladder stays free of side effects.</para>
/// </summary>
internal static class DarlingHostBinding
{
    /// <summary>The effective bind. <see cref="LoopbackOnly"/> is the secure default;
    /// <see cref="NetworkAndLoopback"/> binds the LAN interface behind the surface's auth gate.</summary>
    internal enum BindMode
    {
        LoopbackOnly,
        NetworkAndLoopback,
    }

    /// <summary>WHY the bind resolved as it did — the caller maps this to a severity (Round-4 #7).
    /// Member order is pinned equal to the MCP host's nested <c>McpBindReason</c> (a test enforces it) so the
    /// MCP forwarders can cast between them.</summary>
    internal enum BindReason
    {
        /// <summary>No network block, or a loopback/absent listen — the byte-for-byte-today loopback server.</summary>
        LoopbackByDefault,

        /// <summary>All preconditions met: non-loopback listen + managed + token present + valid allowFrom CIDR.</summary>
        NetworkExposed,

        /// <summary>network.* is set but postgres.managed = false and the process is not containerized —
        /// network exposure is managed-mode-or-container only (a warning). In a container (#1804) the
        /// compose port mapping is the boundary the BYO reverse-proxy rule was standing in for, and a
        /// loopback bind would be unreachable through it, so exposure is honored there.</summary>
        ManagedModeRequired,

        /// <summary>Exposed + managed but the listen value is not a parseable IP — fail-closed to loopback.</summary>
        ListenInvalid,

        /// <summary>Exposed + managed but no bearer token — fail-closed to loopback.</summary>
        TokenMissing,

        /// <summary>Exposed + managed + token but allowFrom is missing/not a valid CIDR or its family does not match the listen — fail-closed to loopback.</summary>
        AllowFromInvalid,
    }

    /// <summary>The (mode, reason) pair returned by <see cref="ResolveBind"/>.</summary>
    internal readonly record struct BindDecision(BindMode Mode, BindReason Reason);

    /// <summary>
    /// Whether this process runs inside a container (#1804) — the official .NET images set
    /// <c>DOTNET_RUNNING_IN_CONTAINER=true</c>, and the Dockerfile shipped with the compose distribution
    /// rides those images. IMPURE (environment read), which is why it lives beside — never inside — the
    /// pure <see cref="ResolveBind"/> ladder: the callers read it once and pass it in, so the decision
    /// table stays testable without environment games.
    /// </summary>
    internal static bool IsRunningInContainer
        => string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// PURE resolution of an effective host bind from the surface-agnostic inputs. Returns
    /// <see cref="BindMode.NetworkAndLoopback"/> ONLY when <paramref name="listen"/> is a genuine network
    /// (non-loopback) address (via <see cref="DarlingNetwork.IsExposedListenAddress"/> — so <c>127.0.0.1</c>
    /// stays loopback, never a network bind/collision) that ALSO parses as an IP, AND <paramref name="managed"/>
    /// is true, AND a bearer token is present (<paramref name="tokenPresent"/> — presence only; the host
    /// decrypts later), AND <paramref name="allowFrom"/> is a valid CIDR of the SAME address family as the
    /// listen. Otherwise loopback-only with the specific reason. Never throws; never consults a logger.
    /// <paramref name="networkConfigured"/> is "the surface's network block has any field set" — used only for
    /// the BYO "network.* is ignored" notice (the network path never runs in BYO).
    /// </summary>
    internal static BindDecision ResolveBind(
        string? listen, string? allowFrom, bool tokenPresent, bool networkConfigured, bool managed,
        bool inContainer = false)
    {
        var exposed = DarlingNetwork.IsExposedListenAddress(listen);

        /* #1804: a containerized BYO deployment (compose) is exposure-capable — the port mapping is the
           boundary the BYO reverse-proxy rule was standing in for, and a loopback bind would be dead
           through it. The token/CIDR preconditions below apply identically either way. */
        var exposureCapable = managed || inContainer;

        if (!exposed)
        {
            /* Not exposed = the secure default. The one exception worth a word: a BYO store with a network
               block set at all (even a loopback/partial one) is ignored -> the managed-mode notice. */
            if (!exposureCapable && networkConfigured)
            {
                return new BindDecision(BindMode.LoopbackOnly, BindReason.ManagedModeRequired);
            }

            return new BindDecision(BindMode.LoopbackOnly, BindReason.LoopbackByDefault);
        }

        /* Exposed. Managed-or-container only: uncontained BYO never binds the network path, and this
           dominates a missing/invalid listen/token/allowFrom so the operator sees the actionable
           "managed only" notice first. */
        if (!exposureCapable)
        {
            return new BindDecision(BindMode.LoopbackOnly, BindReason.ManagedModeRequired);
        }

        /* The listen must be a parseable IP. IsExposedListenAddress treats a non-IP value (localhost, a
           hostname, "*") as "exposed" so it is never silently bound; here it degrades to loopback rather than
           throwing when the host later does IPAddress.Parse. exposed => listen is non-empty. */
        if (!IPAddress.TryParse(listen!.Trim(), out var listenIp))
        {
            return new BindDecision(BindMode.LoopbackOnly, BindReason.ListenInvalid);
        }

        /* Token presence only (no decryption here — that is an effectful, Windows-only step the host does). */
        if (!tokenPresent)
        {
            return new BindDecision(BindMode.LoopbackOnly, BindReason.TokenMissing);
        }

        /* allowFrom must be a valid CIDR (host bits zeroed) whose address family matches the listen: a
           mismatched family would bind one family while the in-app CIDR check rejects the other, 403-ing every
           network client — fail-closed but silently non-functional, so degrade with a clear reason instead. */
        if (string.IsNullOrWhiteSpace(allowFrom) || !IPNetwork.TryParse(allowFrom.Trim(), out var cidr))
        {
            return new BindDecision(BindMode.LoopbackOnly, BindReason.AllowFromInvalid);
        }

        if (cidr.BaseAddress.AddressFamily != listenIp.AddressFamily)
        {
            return new BindDecision(BindMode.LoopbackOnly, BindReason.AllowFromInvalid);
        }

        return new BindDecision(BindMode.NetworkAndLoopback, BindReason.NetworkExposed);
    }

    /// <summary>
    /// PURE severity map for a <see cref="ResolveBind"/> reason (Round-4 #7): the fail-closed degrades
    /// (<see cref="BindReason.ListenInvalid"/>/<see cref="BindReason.TokenMissing"/>/
    /// <see cref="BindReason.AllowFromInvalid"/>) are <see cref="LogLevel.Critical"/>, the BYO "ignored"
    /// notice (<see cref="BindReason.ManagedModeRequired"/>) is <see cref="LogLevel.Warning"/>, and the
    /// non-degrade reasons (<see cref="BindReason.NetworkExposed"/>/<see cref="BindReason.LoopbackByDefault"/>)
    /// are silent (null).
    /// </summary>
    internal static LogLevel? MapBindReasonSeverity(BindReason reason) => reason switch
    {
        BindReason.ListenInvalid => LogLevel.Critical,
        BindReason.TokenMissing => LogLevel.Critical,
        BindReason.AllowFromInvalid => LogLevel.Critical,
        BindReason.ManagedModeRequired => LogLevel.Warning,
        _ => null,
    };

    /// <summary>
    /// Whether to ALSO bind the two loopback families beside the network listener. Skipped when the listen
    /// value is itself a loopback address (already covered) or a wildcard (<c>0.0.0.0</c> covers IPv4 loopback,
    /// <c>::</c> the IPv6) — binding an explicit loopback on the same port then would collide (WSAEADDRINUSE).
    /// For a specific LAN IP the loopback binds are added so a local client resolving "localhost" still reaches
    /// the server (which, in network mode, now also requires the surface's auth gate).
    /// </summary>
    internal static bool ShouldAddLoopbackListeners(IPAddress listenIp)
        => !(IPAddress.IsLoopback(listenIp)
             || listenIp.Equals(IPAddress.Any)
             || listenIp.Equals(IPAddress.IPv6Any));

    /// <summary>
    /// PURE in-app CIDR check: is <paramref name="remoteIp"/> allowed? Loopback (<c>127.0.0.0/8</c> or
    /// <c>::1</c>, incl. an IPv4-mapped-IPv6 form) is ALWAYS allowed — it is not in
    /// <paramref name="allowedCidr"/>, so otherwise the loopback bind's local clients would be rejected.
    /// Everything else must fall inside the CIDR. A null remote (unverifiable origin) fails closed.
    /// </summary>
    internal static bool IsRemoteAddressAllowed(IPAddress? remoteIp, IPNetwork allowedCidr)
    {
        if (remoteIp is null)
        {
            return false;
        }

        var ip = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;
        return IPAddress.IsLoopback(ip) || allowedCidr.Contains(ip);
    }

    /// <summary>
    /// PURE Host-header allowlist — the DNS-rebinding guard both Darling hosts install as their FIRST
    /// middleware, in BOTH bind modes. The decision itself lives in
    /// <see cref="PerformanceMonitor.Common.HostHeaderGuard"/> so Lite's MCP host runs the SAME code (#1648);
    /// this forwarder keeps it reachable where the rest of the two hosts' bind/auth helpers live, so neither
    /// host reaches past this class. Pass <paramref name="networkListenIp"/> = null in loopback-only mode.
    /// </summary>
    internal static bool IsAllowedHost(string? host, IPAddress? networkListenIp)
        => HostHeaderGuard.IsAllowedHost(host, networkListenIp);

    /// <summary>
    /// PURE constant-time token comparison. Both tokens are hashed to a fixed 32 bytes first, so the compare
    /// is constant-time regardless of length and leaks neither token nor its length. An empty
    /// <paramref name="expected"/> never authorizes (the caller should also guard). Used by the MCP host's
    /// Bearer-header check and the web host's <c>?token=</c> check alike.
    /// </summary>
    internal static bool FixedTimeTokenEquals(string? presented, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(expectedHash, presentedHash);
    }
}
