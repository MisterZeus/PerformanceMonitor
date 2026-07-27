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
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Service.Hosting;
using PerformanceMonitor.Darling.Service.Mcp;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// What the exe should do for its command-line, decided from the FIRST argument by
/// <see cref="DarlingCliCommands.ClassifyStartupArgs"/> (#1581). Before this, an UNRECOGNIZED flag
/// (the incident's <c>Service.exe --version</c>) fell through into a real service startup, spawning a
/// second instance — the outage. Now only <see cref="StartHost"/> (no args) or a recognized verb reaches
/// the host; anything else prints and exits.
/// </summary>
public enum StartupAction
{
    /// <summary>No arguments — run the service host (also how the SCM starts it).</summary>
    StartHost,

    /// <summary><c>--version</c>/<c>-v</c> — print the product version and exit 0.</summary>
    PrintVersion,

    /// <summary><c>--help</c>/<c>-h</c> — print usage and exit 0.</summary>
    PrintHelp,

    /// <summary>A recognized one-shot verb (encrypt-password, test-connection, …) — dispatched by Program.</summary>
    RunKnownVerb,

    /// <summary>An unrecognized argument — print "unknown option" + usage to stderr and exit non-zero.</summary>
    UnknownOption,
}

/// <summary>
/// One-shot CLI verbs the service exe supports alongside the Windows-service host — currently the
/// <c>--test-connection</c> / <c>--validate-config</c> pre-flight (Stage 2). It loads darling.json,
/// validates its shape, and probes EVERY configured server for reachability + permissions, reusing the SAME
/// <see cref="DarlingServerConnector.ProbeAsync"/> path the <c>test_connect</c> command runs — so a config
/// that validates from the CLI connects identically under the running service. Pure output formatting
/// (<see cref="FormatProbeLine"/>) is split out so it is unit-testable without live SQL.
/// </summary>
public static class DarlingCliCommands
{
    /// <summary>The verb that encrypts a SQL-auth password for darling.json (reads stdin).</summary>
    public static bool IsEncryptPasswordVerb(string arg) =>
        string.Equals(arg, "--encrypt-password", StringComparison.OrdinalIgnoreCase);

    /// <summary>The verb aliases handled by <see cref="TryGetValidateConfigVerb"/>.</summary>
    public static bool IsValidateConfigVerb(string arg) =>
        string.Equals(arg, "--test-connection", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "--validate-config", StringComparison.OrdinalIgnoreCase);

    /// <summary>The verb <see cref="PrintViewerConnectionAsync"/> handles (darling-network-endpoints D8).</summary>
    public static bool IsPrintViewerConnectionVerb(string arg) =>
        string.Equals(arg, "--print-viewer-connection", StringComparison.OrdinalIgnoreCase);

    /// <summary>The verb <see cref="ConfigureNetworkAsync"/> handles — the interactive exposure wizard (#1561).</summary>
    public static bool IsConfigureNetworkVerb(string arg) =>
        string.Equals(arg, "--configure-network", StringComparison.OrdinalIgnoreCase);

    /// <summary>The verb <see cref="EnableMcpAsync"/> handles — enable the MCP endpoint in the store (+ firewall).</summary>
    public static bool IsEnableMcpVerb(string arg) =>
        string.Equals(arg, "--enable-mcp", StringComparison.OrdinalIgnoreCase);

    /// <summary>The verb <see cref="DisableMcpAsync"/> handles — disable the MCP endpoint in the store (+ firewall).</summary>
    public static bool IsDisableMcpVerb(string arg) =>
        string.Equals(arg, "--disable-mcp", StringComparison.OrdinalIgnoreCase);

    /// <summary>The verb <see cref="EnableWebAsync"/> handles — enable the web-dashboard endpoint in the store (+ firewall).</summary>
    public static bool IsEnableWebVerb(string arg) =>
        string.Equals(arg, "--enable-web", StringComparison.OrdinalIgnoreCase);

    /// <summary>The verb <see cref="DisableWebAsync"/> handles — disable the web-dashboard endpoint in the store (+ firewall).</summary>
    public static bool IsDisableWebVerb(string arg) =>
        string.Equals(arg, "--disable-web", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>--version</c>/<c>-v</c> — print the product version and exit.</summary>
    public static bool IsVersionVerb(string arg) =>
        string.Equals(arg, "--version", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "-v", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>--help</c>/<c>-h</c>/<c>-?</c>/<c>/?</c> — print usage and exit.</summary>
    public static bool IsHelpVerb(string arg) =>
        string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "-?", StringComparison.Ordinal)
        || string.Equals(arg, "/?", StringComparison.Ordinal);

    /// <summary>
    /// Every one-shot CLI verb the exe recognizes as its FIRST argument — the allow-list
    /// <see cref="ClassifyStartupArgs"/> uses and the single source of truth Program's dispatch mirrors, so the
    /// two can never drift. Excludes <c>--version</c>/<c>--help</c> (those are their own classifications).
    /// </summary>
    public static bool IsKnownVerb(string arg) =>
        IsEncryptPasswordVerb(arg)
        || IsValidateConfigVerb(arg)
        || IsPrintViewerConnectionVerb(arg)
        || IsConfigureNetworkVerb(arg)
        || IsEnableMcpVerb(arg)
        || IsDisableMcpVerb(arg)
        || IsEnableWebVerb(arg)
        || IsDisableWebVerb(arg);

    /// <summary>
    /// Classifies the exe's command line from its FIRST argument (#1581): no args → run the host; a recognized
    /// verb → dispatch it; <c>--version</c>/<c>--help</c> → print + exit; ANYTHING else → an unknown option that
    /// must NOT start the host (the incident: <c>Service.exe --version</c> used to fall through into a real
    /// startup and spawn a second instance). Pure so it pins directly.
    /// </summary>
    public static StartupAction ClassifyStartupArgs(string[]? args)
    {
        if (args is null || args.Length == 0)
        {
            return StartupAction.StartHost;
        }

        var first = args[0];
        if (IsVersionVerb(first))
        {
            return StartupAction.PrintVersion;
        }

        if (IsHelpVerb(first))
        {
            return StartupAction.PrintHelp;
        }

        if (IsKnownVerb(first))
        {
            return StartupAction.RunKnownVerb;
        }

        return StartupAction.UnknownOption;
    }

    /// <summary>
    /// The product version string for <c>--version</c> — the assembly's informational version (the csproj
    /// <c>&lt;Version&gt;</c>), with any SemVer <c>+build</c> metadata suffix stripped, falling back to the
    /// assembly version. Pure (reads this assembly's own attributes), so it pins directly.
    /// </summary>
    public static string ProductVersion()
    {
        var assembly = typeof(DarlingCliCommands).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    /// <summary>The usage text for <c>--help</c> and the unknown-option error. Pure ASCII, one verb per line.</summary>
    public static string UsageText() =>
        "PerformanceMonitor Darling service." + Environment.NewLine +
        Environment.NewLine +
        "Usage:" + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe                     Run the service (also how the Windows Service Control Manager starts it)." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --version, -v       Print the product version and exit." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --help, -h          Print this help and exit." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --test-connection   Validate darling.json and probe every configured server." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --encrypt-password  Encrypt a SQL-auth password for darling.json (reads stdin)." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --print-viewer-connection   Print a remote-viewer connection string (managed store)." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --configure-network Interactive LAN-exposure wizard." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --enable-mcp        Enable the MCP endpoint in the store and open its firewall (run elevated)." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --disable-mcp       Disable the MCP endpoint in the store and remove its firewall rule (run elevated)." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --enable-web        Enable the web dashboard in the store and open its firewall (run elevated)." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --disable-web       Disable the web dashboard in the store and remove its firewall rule (run elevated).";

    /// <summary>
    /// Loads + validates darling.json, then probes every server. Prints one PASS/FAIL line per server and a
    /// summary. Returns 0 only when the config is valid AND every server is reachable; 1 otherwise (so it is
    /// usable as a deployment gate). Store/collection are never touched — this is a pure config pre-flight.
    /// </summary>
    public static async Task<int> ValidateConfigAsync(
        string? configPath, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        DarlingConfig config;
        try
        {
            config = DarlingConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            error.WriteLine($"Could not load configuration: {ex.Message}");
            return 1;
        }

        var problems = config.Validate();
        if (problems.Count > 0)
        {
            error.WriteLine("Configuration is invalid:");
            foreach (var problem in problems)
            {
                error.WriteLine("  - " + problem);
            }

            return 1;
        }

        output.WriteLine($"Validating connectivity to {config.Servers.Count} server(s)...");

        var allReachable = true;
        foreach (var server in config.Servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await DarlingServerConnector.ProbeAsync(server, null, cancellationToken);
            output.WriteLine(FormatProbeLine(server.DisplayName, probe));
            if (!probe.Success)
            {
                allReachable = false;
            }
        }

        output.WriteLine(allReachable
            ? "All servers reachable."
            : "One or more servers failed the connection pre-flight (see above).");
        return allReachable ? 0 : 1;
    }

    /// <summary>Formats one server's probe outcome as a PASS/FAIL line (pure — unit-testable).</summary>
    public static string FormatProbeLine(string serverName, ConnectionProbeResult probe)
    {
        if (!probe.Success)
        {
            return $"  [FAIL] {serverName}: {probe.Error}";
        }

        var edition = string.IsNullOrEmpty(probe.EngineEditionDescription)
            ? DarlingServerConnector.DescribeEngineEdition(probe.EngineEdition)
            : probe.EngineEditionDescription;
        var msdb = probe.HasMsdbAccess ? "msdb access: yes" : "msdb access: NO (failed-job alerts unavailable)";
        return $"  [PASS] {serverName}: SQL major version {probe.MajorVersion}, {edition}, {msdb}";
    }

    /// <summary>
    /// Prints a paste-ready remote-viewer connection string and the server TLS certificate for the opt-in store
    /// network endpoint (darling-network-endpoints D8). It DPAPI-decrypts the credential of the role
    /// <c>postgres.network.role</c> names (default <c>viewer</c>, read-only) and reads the generated
    /// <c>server.crt</c>, so it must run ON the managed store's host under an account that can decrypt them —
    /// hence Windows-only (the caller is <c>OperatingSystem.IsWindows()</c>-guarded, mirroring
    /// <c>--encrypt-password</c>). The operator pastes the string into the VIEWER machine's darling.json
    /// (<c>postgres.managed = false</c>, into <c>postgres.connectionString</c>, consumed verbatim — no viewer
    /// code change) and saves the emitted PEM where <c>Root Certificate</c> points. Returns 0 on success; 1 on a
    /// mode/role/credential error. Managed-mode only (BYO governs its own exposure, D-BYO); network config lives
    /// out of the all-fatal <see cref="DarlingConfig.Validate"/>, so this verb never calls it.
    /// <para><b>STDOUT carries a LIVE SECRET</b> (the role password) — the verb warns (on STDERR) to redirect it
    /// to an ACL'd file or the clipboard, never scrollback / CI / a screenshare.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<int> PrintViewerConnectionAsync(
        string? configPath, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        DarlingConfig config;
        try
        {
            config = DarlingConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            error.WriteLine($"Could not load configuration: {ex.Message}");
            return 1;
        }

        var postgres = config.Postgres;
        if (postgres is null)
        {
            error.WriteLine("postgres section is required.");
            return 1;
        }

        /* Managed-mode only: the DPAPI credential files + the generated TLS cert this verb reads exist only in
           managed mode. In BYO the operator's own PostgreSQL governs exposure + credentials (D-BYO). */
        if (!postgres.Managed)
        {
            error.WriteLine(
                "--print-viewer-connection is for the managed store only. In bring-your-own mode " +
                "(postgres.connectionString), your own PostgreSQL governs network exposure and credentials — " +
                "build the remote viewer's connection string from your own role + TLS setup.");
            return 1;
        }

        /* The pg_hba login role the network exposure names — default viewer (read-only, the secure default).
           An explicitly-invalid value is a hard error: the store degrades to loopback for it, so no remote
           connection exists to print. */
        var network = postgres.Network;
        var role = DarlingNetwork.NormalizeNetworkRole(network?.Role);
        if (role is null)
        {
            error.WriteLine(
                $"postgres.network.role '{network?.Role}' is invalid — it must be \"viewer\" (default, read-only) " +
                "or \"admin\". The store degrades to loopback for an unknown role, so there is no remote connection to print.");
            return 1;
        }

        /* Warn (not fail) when the store is not actually network-exposed: the operator still gets a template,
           but the endpoint will not accept it until postgres.network.listen is set and the service restarted. */
        if (!DarlingNetwork.IsExposedListenAddress(network?.Listen))
        {
            error.WriteLine(
                "WARNING: postgres.network.listen is not a network address, so the managed store is loopback-only " +
                "right now. Set postgres.network (listen + allowFrom) and restart the service to expose it (which " +
                "also generates the TLS cert), then re-run this command.");
        }

        var host = ResolveViewerHost(network?.Listen);

        /* Decrypt the role's DPAPI-LocalMachine credential (Windows-only; the caller is IsWindows-guarded).
           The cert lives in the same directory as the credential (ParentOf(dataDirectory)). */
        var dataDirectory = DarlingManagedPostgres.ResolveDataDirectory(postgres);
        var credentialPath = string.Equals(role, "admin", StringComparison.Ordinal)
            ? DarlingManagedPostgres.AdminCredentialPathFor(dataDirectory)
            : DarlingManagedPostgres.ViewerCredentialPathFor(dataDirectory);

        if (!File.Exists(credentialPath))
        {
            error.WriteLine(
                $"The '{role}' role credential ({credentialPath}) does not exist yet. Start the PerformanceMonitor " +
                "Darling service once so its first run provisions the least-privilege roles and their credentials, " +
                "then re-run this command.");
            return 1;
        }

        string password;
        try
        {
            password = DarlingSecrets.Unprotect((await File.ReadAllTextAsync(credentialPath, cancellationToken)).Trim());
        }
        catch (Exception ex)
        {
            error.WriteLine(
                $"Could not decrypt the '{role}' credential at {credentialPath}: {ex.Message} (DPAPI-LocalMachine — " +
                "run this on the same machine as the service, under an account that can read the credential).");
            return 1;
        }

        /* The client-side Root Certificate placeholder: the operator saves the PEM below at this path on the
           VIEWER machine (a bare filename resolves beside the viewer's working directory; an absolute path
           also works). Kept as a literal so the printed string is paste-ready. */
        const string clientCertificatePath = "server.crt";
        var connectionString = BuildViewerConnectionString(host, postgres.Port, role, password, clientCertificatePath);

        /* Guidance + the live-secret warning go to STDERR, so redirecting STDOUT to a file or the clipboard
           captures the connection string + cert WITHOUT swallowing the warning (D8). */
        error.WriteLine();
        error.WriteLine(
            $"WARNING: the connection string below contains a LIVE database password (the '{role}' role), written " +
            "to STDOUT. Redirect it to an ACL'd file or pipe it to the clipboard; do not leave it in shell " +
            "scrollback, CI logs, or a screenshare.");
        error.WriteLine("  Example (file):      PerformanceMonitor.Darling.Service.exe --print-viewer-connection > viewer-connection.txt");
        error.WriteLine("  Example (clipboard): PerformanceMonitor.Darling.Service.exe --print-viewer-connection | clip");
        if (string.Equals(role, "admin", StringComparison.Ordinal))
        {
            error.WriteLine(
                "  NOTE: 'admin' is a WRITE credential holding the config-table pivot surface. Prefer the default " +
                "'viewer' (read-only) for a remote seat; if you must use 'admin', NTFS-ACL the laptop file too.");
        }

        error.WriteLine(
            $"Save the certificate block below as '{clientCertificatePath}' on the viewer machine (beside its " +
            "darling.json) and point \"Root Certificate\" at it — the store uses SSL Mode=VerifyFull, so the cert must match.");
        error.WriteLine();

        output.WriteLine(
            "# Paste into the viewer machine's darling.json -> postgres.connectionString (with postgres.managed = false):");
        output.WriteLine(connectionString);
        output.WriteLine();

        /* Emit the server cert PEM so the operator can copy it to the viewer machine. */
        var certificatePath = Path.Combine(
            Path.GetDirectoryName(credentialPath)!, DarlingManagedPostgres.ServerCertFileName);
        if (File.Exists(certificatePath))
        {
            output.WriteLine($"# Server TLS certificate ({DarlingManagedPostgres.ServerCertFileName}) — save as '{clientCertificatePath}' on the viewer machine:");
            output.WriteLine((await File.ReadAllTextAsync(certificatePath, cancellationToken)).Trim());
        }
        else
        {
            error.WriteLine(
                $"NOTE: the server TLS certificate ({certificatePath}) does not exist yet — the service generates it " +
                "on its first managed start with postgres.network exposed. Enable postgres.network, restart the " +
                "service, then re-run this command to emit the cert for verify-full.");
        }

        return 0;
    }

    /// <summary>
    /// The <c>Host=</c> value for the remote viewer connection (D8 / Round 4 #12): the bind IP itself when
    /// <paramref name="listen"/> is a concrete IP (not IPv4 loopback, not a <c>0.0.0.0</c>/<c>::</c> wildcard) —
    /// verify-full then validates it against the cert's iPAddress SAN — otherwise the machine's hostname, which
    /// the cert also carries as a dnsName SAN (the fallback for a wildcard bind, a hostname listen, or an unset
    /// listen). Pure — unit-testable.
    /// </summary>
    public static string ResolveViewerHost(string? listen)
    {
        var trimmed = listen?.Trim();
        if (!string.IsNullOrEmpty(trimmed)
            && IPAddress.TryParse(trimmed, out var ip)
            && !(ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes()[0] == 127)
            && !ip.Equals(IPAddress.Any)
            && !ip.Equals(IPAddress.IPv6Any))
        {
            return trimmed;
        }

        return Environment.MachineName;
    }

    /// <summary>
    /// The remote viewer's paste-ready Npgsql connection string (D8): the resolved host, the network role,
    /// verify-full TLS against the pinned server cert, the <c>darling</c> database, and the collect/config
    /// search path — the exact string the operator drops into the viewer machine's darling.json
    /// <c>postgres.connectionString</c> (<c>managed = false</c>, consumed verbatim). The managed role password
    /// is service-generated alphanumeric (no connection-string metacharacters), so a hand-built string is safe
    /// and yields the exact documented shape. Pure — unit-testable.
    /// </summary>
    public static string BuildViewerConnectionString(
        string host, int port, string role, string password, string rootCertificatePath) =>
        $"Host={host};Port={port};Username={role};Password={password};Database=darling;" +
        $"Search Path=collect,config,public;SSL Mode=VerifyFull;Root Certificate={rootCertificatePath}";

    /* ================================================================================================
       --configure-network: the interactive opt-in exposure wizard (#1561).

       Design invariants (the whole reason this is safe):
         - Validation is DELEGATED. Every candidate value is checked by building the SAME config object
           the service reads and running the SAME resolver it fail-closes on (the store's
           DarlingManagedPostgres.ResolveNetworkExposure, the MCP host's DarlingMcpHostService.ResolveMcpBind,
           the web host's DarlingWebHostService.ResolveWebBind).
           The wizard never re-implements CIDR / family / role / token rules — it re-prompts with the
           resolver's own degrade reason, so the wizard can never write what the service would reject.
         - The edit is comment-preserving TEXT SURGERY (DarlingNetworkConfigEditor) — the sample's
           heavily-commented documentation survives verbatim.
         - Nothing is written until the new text passes DarlingConfig.Parse AND the resolver re-check on
           the REPARSED result, and only then behind a timestamped backup. An edit never leaves an
           unparseable or fail-closed darling.json.
         - The MCP bearer / web access tokens are generated + DPAPI-protected; each plaintext is printed to
           STDOUT exactly once with the save-this warning on STDERR (the --print-viewer-connection
           secret-split posture).
         - mcp.enabled / mcp.port are control-plane after the first run (the Viewer's Settings toggle owns
           them live), so the wizard WARNS and points at Settings — it never edits them. The network block
           it writes is deliberately file-defined + restart-only.
       ================================================================================================ */

    private const string ServiceName = "PerformanceMonitor Darling";

    /// <summary>
    /// Interactive wizard that guides the operator through the opt-in store / MCP / web-dashboard LAN exposure and writes
    /// a comment-preserving, resolver-validated edit to darling.json behind a timestamped backup. Managed
    /// mode only (BYO exposure is governed by the operator's own PostgreSQL). Windows-only (it generates a
    /// DPAPI-protected token and controls the Windows service). <paramref name="input"/> is the scripted-
    /// input testability lever — the tests drive the whole flow with a <see cref="StringReader"/>. Returns
    /// 0 on a completed run (including a clean quit) and 1 on a load/parse/write error or a BYO refusal.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<int> ConfigureNetworkAsync(
        string? configPath, TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var resolvedPath = DarlingConfig.ResolveConfigPath(configPath);

        DarlingConfig config;
        try
        {
            config = DarlingConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            error.WriteLine($"Could not load configuration: {ex.Message}");
            return 1;
        }

        var postgres = config.Postgres;
        if (postgres is null)
        {
            error.WriteLine("postgres section is required.");
            return 1;
        }

        string originalText;
        try
        {
            originalText = await File.ReadAllTextAsync(resolvedPath, cancellationToken);
        }
        catch (Exception ex)
        {
            error.WriteLine($"Could not read {resolvedPath}: {ex.Message}");
            return 1;
        }

        output.WriteLine();
        output.WriteLine("PerformanceMonitor Darling — opt-in network exposure wizard (--configure-network)");
        output.WriteLine($"Config: {resolvedPath}");
        output.WriteLine();

        /* Current verdicts, straight from the resolvers the running service uses — so the operator sees the
           service's own truth (including any fail-closed degrade), not the wizard's guess. */
        var (storeCertPath, storeKeyPath) = ResolveStoreCertPaths(postgres);
        var storeNow = DarlingManagedPostgres.ResolveNetworkExposure(postgres.Network, storeCertPath, storeKeyPath);
        var mcpNow = DarlingMcpHostService.ResolveMcpBind(config.Mcp, postgres.Managed);
        var mcpNowExposed = mcpNow.Mode == DarlingMcpHostService.McpBindMode.NetworkAndLoopback;
        var mcpNowDegrade =
            mcpNow.Reason is DarlingMcpHostService.McpBindReason.NetworkExposed or DarlingMcpHostService.McpBindReason.LoopbackByDefault
                ? null
                : McpDegradeText(mcpNow.Reason, config.Mcp);
        var webNow = DarlingWebHostService.ResolveWebBind(config.Web, postgres.Managed);
        var webNowExposed = webNow.Mode == DarlingHostBinding.BindMode.NetworkAndLoopback;
        var webNowDegrade =
            webNow.Reason is DarlingHostBinding.BindReason.NetworkExposed or DarlingHostBinding.BindReason.LoopbackByDefault
                ? null
                : WebDegradeText(webNow.Reason, config.Web);

        output.WriteLine("Current exposure:");
        output.WriteLine(DarlingNetworkConfigEditor.FormatExposureState(
            "Store", storeNow.Exposed, storeNow.ListenIp, storeNow.Cidr, storeNow.Role, storeNow.DegradeReason));
        output.WriteLine(DarlingNetworkConfigEditor.FormatExposureState(
            "MCP  ", mcpNowExposed, config.Mcp.Network?.Listen, config.Mcp.Network?.AllowFrom, null, mcpNowDegrade));
        output.WriteLine(DarlingNetworkConfigEditor.FormatExposureState(
            "Web  ", webNowExposed, config.Web.Network?.Listen, config.Web.Network?.AllowFrom, null, webNowDegrade));
        output.WriteLine($"  Service: {await DescribeServiceStateAsync(cancellationToken)}");
        output.WriteLine();

        /* BYO guard — exposure is managed-mode only (same refusal shape as PrintViewerConnectionAsync). */
        if (!postgres.Managed)
        {
            output.WriteLine("Network exposure is MANAGED-MODE ONLY.");
            output.WriteLine("This darling.json uses bring-your-own PostgreSQL (postgres.connectionString), so your own");
            output.WriteLine("PostgreSQL / reverse proxy governs network exposure — the wizard cannot open the endpoints here.");

            var hasBlocks = (postgres.Network?.IsConfigured ?? false)
                || (config.Mcp.Network?.IsConfigured ?? false)
                || (config.Web.Network?.IsConfigured ?? false);
            if (hasBlocks && AskYesNo(input, output, "A network block is present but IGNORED in this mode. Remove it from darling.json?", defaultYes: false))
            {
                return await DisableExposureAsync(resolvedPath, originalText, input, output, error, cancellationToken);
            }

            return 1;
        }

        /* Surface selection — one surface, a comma combination (e.g. "1,3"), all three, or disable. */
        output.WriteLine("What would you like to configure?");
        output.WriteLine("  [1] Store   — a remote Viewer over TLS (verify-full)");
        output.WriteLine("  [2] MCP     — a LAN assistant/client behind a bearer token");
        output.WriteLine("  [3] Web     — the browser dashboard behind a token->cookie login");
        output.WriteLine("  [4] All     — every surface above (or pick a combination, e.g. 1,3)");
        output.WriteLine("  [5] Disable — remove all exposure (back to loopback-only)");
        output.WriteLine("  [q] Quit without changes");
        var choice = Prompt(input, output, "Choice", "q");
        if (choice is null || choice.Length == 0 || string.Equals(choice, "q", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("No changes made.");
            return 0;
        }

        if (choice == "5")
        {
            return await DisableExposureAsync(resolvedPath, originalText, input, output, error, cancellationToken);
        }

        if (!TryParseSurfaceChoice(choice, out var doStore, out var doMcp, out var doWeb))
        {
            output.WriteLine("Unrecognized choice; no changes made.");
            return 0;
        }

        /* Gather all inputs (delegated validation) BEFORE writing anything, so a cancel leaves the file
           untouched and a multi-surface run is all-or-nothing. */
        (string Listen, string AllowFrom, string Role)? store = null;
        if (doStore)
        {
            output.WriteLine();
            output.WriteLine("== Store exposure ==");
            store = GatherStoreInputs(input, output, error, storeCertPath, storeKeyPath);
            if (store is null)
            {
                return 1;
            }
        }

        (string Listen, string AllowFrom, string? EncryptedToken, string? PlainToken, string? GeneratedPlain)? mcp = null;
        if (doMcp)
        {
            output.WriteLine();
            output.WriteLine("== MCP exposure ==");
            if (!config.Mcp.Enabled)
            {
                output.WriteLine("NOTE: mcp.enabled is currently false. The wizard writes the network block, but the endpoint");
                output.WriteLine("      stays down until you enable MCP in the Viewer's Settings (enabled/port are control-plane");
                output.WriteLine("      after first run; the wizard never edits them).");
            }

            mcp = GatherMcpInputs(input, output, error, config.Mcp);
            if (mcp is null)
            {
                return 1;
            }
        }

        (string Listen, string AllowFrom, string? EncryptedToken, string? PlainToken, string? GeneratedPlain)? web = null;
        if (doWeb)
        {
            output.WriteLine();
            output.WriteLine("== Web dashboard exposure ==");
            if (!config.Web.Enabled)
            {
                output.WriteLine("NOTE: web.enabled is currently false. The wizard writes the network block, but the dashboard");
                output.WriteLine("      stays down until you enable it with --enable-web or the Viewer's Settings (enabled/port");
                output.WriteLine("      are control-plane after first run; the wizard never edits them).");
            }

            web = GatherWebInputs(input, output, error, config.Web);
            if (web is null)
            {
                return 1;
            }
        }

        /* Build the edit through the comment-preserving surgeon. */
        var newText = originalText;
        if (store is not null)
        {
            newText = DarlingNetworkConfigEditor.UpsertNetworkBlock(
                newText, "postgres",
                DarlingNetworkConfigEditor.BuildStoreNetworkBlock(store.Value.Listen, store.Value.AllowFrom, store.Value.Role));
        }

        if (mcp is not null)
        {
            newText = DarlingNetworkConfigEditor.UpsertNetworkBlock(
                newText, "mcp",
                DarlingNetworkConfigEditor.BuildMcpNetworkBlock(mcp.Value.Listen, mcp.Value.AllowFrom, mcp.Value.EncryptedToken, mcp.Value.PlainToken));
        }

        if (web is not null)
        {
            newText = DarlingNetworkConfigEditor.UpsertNetworkBlock(
                newText, "web",
                DarlingNetworkConfigEditor.BuildWebNetworkBlock(web.Value.Listen, web.Value.AllowFrom, web.Value.EncryptedToken, web.Value.PlainToken));
        }

        /* Guard 1: the edited text must PARSE (comments/trailing-commas tolerated). */
        DarlingConfig reparsed;
        try
        {
            reparsed = DarlingConfig.Parse(newText);
        }
        catch (Exception ex)
        {
            error.WriteLine($"Internal error: the edited darling.json did not parse ({ex.Message}). No changes were written.");
            return 1;
        }

        /* Guard 2: the resolvers must ACCEPT the reparsed result — never write a file the service would
           fail-close on. This is the same delegation the input loop used, re-run on the FINAL text. */
        if (store is not null)
        {
            var check = DarlingManagedPostgres.ResolveNetworkExposure(reparsed.Postgres.Network, storeCertPath, storeKeyPath);
            if (!check.Exposed)
            {
                error.WriteLine($"Internal error: the store block would fail-close ({check.DegradeReason}). No changes were written.");
                return 1;
            }
        }

        if (mcp is not null)
        {
            var check = DarlingMcpHostService.ResolveMcpBind(reparsed.Mcp, reparsed.Postgres.Managed);
            if (check.Mode != DarlingMcpHostService.McpBindMode.NetworkAndLoopback)
            {
                error.WriteLine($"Internal error: the MCP block would fail-close ({McpDegradeText(check.Reason, reparsed.Mcp)}). No changes were written.");
                return 1;
            }
        }

        if (web is not null)
        {
            var check = DarlingWebHostService.ResolveWebBind(reparsed.Web, reparsed.Postgres.Managed);
            if (check.Mode != DarlingHostBinding.BindMode.NetworkAndLoopback)
            {
                error.WriteLine($"Internal error: the web block would fail-close ({WebDegradeText(check.Reason, reparsed.Web)}). No changes were written.");
                return 1;
            }
        }

        /* Only now: timestamped backup + write. */
        if (!await WriteWithBackupAsync(resolvedPath, newText, output, error, cancellationToken))
        {
            return 1;
        }

        /* The generated token plaintexts — STDOUT exactly once each; the save-this warning on STDERR so a
           STDOUT redirect keeps the token without swallowing the warning (MCP first, then web, so a
           two-token capture is unambiguous by order). */
        if (mcp is not null && mcp.Value.GeneratedPlain is not null)
        {
            error.WriteLine();
            error.WriteLine("SAVE THIS NOW — your new MCP bearer token is shown ONCE (darling.json stores only its DPAPI blob).");
            error.WriteLine("Remote MCP clients send it as the header:  Authorization: Bearer <token>");
            output.WriteLine(mcp.Value.GeneratedPlain);
        }

        if (web is not null && web.Value.GeneratedPlain is not null)
        {
            error.WriteLine();
            error.WriteLine("SAVE THIS NOW — your new web dashboard access token is shown ONCE (darling.json stores only its DPAPI blob).");
            error.WriteLine("A remote browser presents it once via ?token=... and gets a session cookie back.");
            output.WriteLine(web.Value.GeneratedPlain);
        }

        PrintNextSteps(
            output,
            store is not null, postgres.Port, store?.AllowFrom,
            mcp is not null, config.Mcp.Port, mcp?.AllowFrom, config.Mcp.Enabled,
            web is not null, config.Web.Port, web?.AllowFrom, config.Web.Enabled, web?.Listen);

        await OfferRestartAsync(input, output, error, cancellationToken);
        return 0;
    }

    /// <summary>
    /// Parses the surface-selection choice: "1"/"2"/"3" (store/MCP/web), "4" = all three, or a comma
    /// combination like "1,3". STRICT — every token must be a known surface digit, so a typo ("1,shop")
    /// rejects the whole input instead of silently configuring a subset. False = unrecognized (nothing
    /// selected). Pure.
    /// </summary>
    internal static bool TryParseSurfaceChoice(string choice, out bool doStore, out bool doMcp, out bool doWeb)
    {
        doStore = false;
        doMcp = false;
        doWeb = false;

        foreach (var raw in choice.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw)
            {
                case "1": doStore = true; break;
                case "2": doMcp = true; break;
                case "3": doWeb = true; break;
                case "4": doStore = true; doMcp = true; doWeb = true; break;
                default:
                    doStore = false;
                    doMcp = false;
                    doWeb = false;
                    return false;
            }
        }

        return doStore || doMcp || doWeb;
    }

    /// <summary>
    /// The store's generated TLS cert/key paths (beside the data directory), passed to
    /// <c>ResolveNetworkExposure</c> so the whitespace-in-path degrade (a spaced path the pg_ctl -o
    /// override cannot pass to postgres) is caught pre-write, exactly as the service would. Mirrors the
    /// path idiom in <see cref="PrintViewerConnectionAsync"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (string CertPath, string KeyPath) ResolveStoreCertPaths(PostgresConfig postgres)
    {
        var dataDirectory = DarlingManagedPostgres.ResolveDataDirectory(postgres);
        var credentialDirectory = Path.GetDirectoryName(DarlingManagedPostgres.ViewerCredentialPathFor(dataDirectory))!;
        return (
            Path.Combine(credentialDirectory, DarlingManagedPostgres.ServerCertFileName),
            Path.Combine(credentialDirectory, DarlingManagedPostgres.ServerKeyFileName));
    }

    /// <summary>Best-effort Windows-service status line via Get-Service; never throws (falls back to a plain note).</summary>
    [SupportedOSPlatform("windows")]
    private static async Task<string> DescribeServiceStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, psOutput) = await DarlingManagedPostgres.RunPowerShellAsync(
                $"(Get-Service -Name '{ServiceName}' -ErrorAction SilentlyContinue).Status", cancellationToken);
            var status = psOutput.Trim();
            return exitCode == 0 && status.Length > 0 ? status : "not installed (or status unavailable)";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return "status unavailable";
        }
    }

    /// <summary>The machine's non-loopback IPv4 unicast addresses (interface name + address). Impure (queries the OS); the pure menu formatter takes its output.</summary>
    private static List<(string Name, string Address)> EnumerateLocalIPv4()
    {
        var addresses = new List<(string Name, string Address)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                var ip = unicast.Address.ToString();
                if (!ip.StartsWith("127.", StringComparison.Ordinal))
                {
                    addresses.Add((nic.Name, ip));
                }
            }
        }

        return addresses;
    }

    /// <summary>Prompts for the bind IP: pick a listed adapter, choose 0.0.0.0, or type any IP (the resolver validates it). Returns null on EOF/cancel.</summary>
    private static string? SelectListenAddress(TextReader input, TextWriter output, string surface)
    {
        var adapters = EnumerateLocalIPv4();
        output.WriteLine($"Select the {surface} bind IP (the address remote clients connect to):");
        output.WriteLine(DarlingNetworkConfigEditor.FormatAdapterMenu(adapters));
        var allInterfacesChoice = adapters.Count + 1;
        output.WriteLine($"  [{allInterfacesChoice}] 0.0.0.0  (all interfaces — connect by a cert SAN name)");
        output.WriteLine("  [c] type a custom IP");

        while (true)
        {
            var pick = Prompt(input, output, "Bind IP");
            if (pick is null)
            {
                return null;
            }

            if (pick.Length == 0)
            {
                output.WriteLine("  A bind IP is required.");
                continue;
            }

            if (int.TryParse(pick, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                if (n >= 1 && n <= adapters.Count)
                {
                    return adapters[n - 1].Address;
                }

                if (n == allInterfacesChoice)
                {
                    return "0.0.0.0";
                }

                output.WriteLine("  Not a listed number — enter a menu number or an IP.");
                continue;
            }

            if (string.Equals(pick, "c", StringComparison.OrdinalIgnoreCase))
            {
                var custom = Prompt(input, output, "Enter the bind IP");
                if (custom is null)
                {
                    return null;
                }

                if (custom.Length == 0)
                {
                    output.WriteLine("  A bind IP is required.");
                    continue;
                }

                return custom;
            }

            /* Anything else is treated as a directly-typed IP; the resolver is the arbiter of validity. */
            return pick;
        }
    }

    /// <summary>
    /// Gathers listen / allowFrom / role for the store, RE-PROMPTING with the store resolver's own degrade
    /// reason until it accepts them (or the operator cancels). The whitespace-in-path degrade is a config
    /// problem the loop cannot fix, so it is reported and the surface is abandoned. Returns null on cancel.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (string Listen, string AllowFrom, string Role)? GatherStoreInputs(
        TextReader input, TextWriter output, TextWriter error, string certPath, string keyPath)
    {
        while (true)
        {
            var listen = SelectListenAddress(input, output, "store");
            if (listen is null)
            {
                output.WriteLine("Cancelled — no changes made.");
                return null;
            }

            var allowFrom = Prompt(input, output, "Allowed remote CIDR (e.g. 192.168.1.0/24)");
            if (allowFrom is null)
            {
                output.WriteLine("Cancelled — no changes made.");
                return null;
            }

            output.WriteLine("Remote pg_hba role: 'viewer' (read-only, the secure default) or 'admin' (remote WRITES).");
            var role = Prompt(input, output, "Role", "viewer");
            if (role is null)
            {
                output.WriteLine("Cancelled — no changes made.");
                return null;
            }

            if (string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine("  WARNING: 'admin' is a remote WRITE credential holding the config-table service-credential pivot.");
                output.WriteLine("           Prefer 'viewer' unless you specifically need remote writes.");
            }

            var candidate = new PostgresNetworkConfig { Listen = listen, AllowFrom = allowFrom, Role = role };
            var decision = DarlingManagedPostgres.ResolveNetworkExposure(candidate, certPath, keyPath);
            if (decision.Exposed)
            {
                /* Write the resolver's canonical values (parsed IP, host-bits-zeroed CIDR, normalized role)
                   so the file matches what the service would compute. */
                return (decision.ListenIp!, decision.Cidr!, decision.Role!);
            }

            var reason = decision.DegradeReason ?? "the store resolver rejected these values";
            output.WriteLine($"  Not accepted: {reason}");
            if (reason.Contains("whitespace", StringComparison.OrdinalIgnoreCase))
            {
                error.WriteLine("  This is a path problem, not an input problem — move postgres.dataDirectory to a space-free path, then re-run.");
                return null;
            }

            output.WriteLine("  Let us try again.");
        }
    }

    /// <summary>
    /// Gathers the MCP token (default KEEP an existing one; else generate a fresh 32-char token and
    /// DPAPI-protect it) plus listen / allowFrom, RE-PROMPTING with the MCP resolver's degrade reason
    /// until it accepts them. Returns the fields to write plus the generated plaintext (non-null only when
    /// a token was generated, so the caller prints it once). Returns null on cancel.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (string Listen, string AllowFrom, string? EncryptedToken, string? PlainToken, string? GeneratedPlain)? GatherMcpInputs(
        TextReader input, TextWriter output, TextWriter error, McpConfig currentMcp)
    {
        string? encryptedToken = null;
        string? plainToken = null;
        string? generatedPlain = null;

        var existing = currentMcp.Network;
        var hasExistingToken = existing is not null
            && (!string.IsNullOrWhiteSpace(existing.EncryptedToken) || !string.IsNullOrWhiteSpace(existing.Token));

        if (hasExistingToken && AskYesNo(input, output, "An MCP bearer token already exists. Keep it?", defaultYes: true))
        {
            if (!string.IsNullOrWhiteSpace(existing!.EncryptedToken))
            {
                encryptedToken = existing.EncryptedToken;
            }
            else
            {
                plainToken = existing!.Token;
                output.WriteLine("  Keeping the existing PLAINTEXT token (consider regenerating to store it DPAPI-encrypted instead).");
            }
        }
        else
        {
            generatedPlain = DarlingManagedPostgres.GeneratePassword();
            encryptedToken = DarlingSecrets.Protect(generatedPlain);
        }

        while (true)
        {
            var listen = SelectListenAddress(input, output, "MCP");
            if (listen is null)
            {
                output.WriteLine("Cancelled — no changes made.");
                return null;
            }

            var allowFrom = Prompt(input, output, "Allowed remote CIDR (e.g. 192.168.1.0/24)");
            if (allowFrom is null)
            {
                output.WriteLine("Cancelled — no changes made.");
                return null;
            }

            var candidate = new McpConfig
            {
                Enabled = currentMcp.Enabled,
                Port = currentMcp.Port,
                Network = new McpNetworkConfig
                {
                    Listen = listen,
                    AllowFrom = allowFrom,
                    EncryptedToken = encryptedToken,
                    Token = plainToken,
                },
            };

            var decision = DarlingMcpHostService.ResolveMcpBind(candidate, managed: true);
            if (decision.Mode == DarlingMcpHostService.McpBindMode.NetworkAndLoopback)
            {
                return (listen, allowFrom, encryptedToken, plainToken, generatedPlain);
            }

            output.WriteLine($"  Not accepted: {McpDegradeText(decision.Reason, candidate)}");
            output.WriteLine("  Let us try again.");
        }
    }

    /// <summary>
    /// Gathers the web dashboard access token (default KEEP an existing one; else generate a fresh 32-char
    /// token and DPAPI-protect it) plus listen / allowFrom, RE-PROMPTING with the web bind resolver's degrade
    /// reason until it accepts them — the web twin of <see cref="GatherMcpInputs"/> (#1617). Returns the
    /// fields to write plus the generated plaintext (non-null only when a token was generated, so the caller
    /// prints it once). Returns null on cancel.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (string Listen, string AllowFrom, string? EncryptedToken, string? PlainToken, string? GeneratedPlain)? GatherWebInputs(
        TextReader input, TextWriter output, TextWriter error, WebConfig currentWeb)
    {
        string? encryptedToken = null;
        string? plainToken = null;
        string? generatedPlain = null;

        var existing = currentWeb.Network;
        var hasExistingToken = existing is not null
            && (!string.IsNullOrWhiteSpace(existing.EncryptedToken) || !string.IsNullOrWhiteSpace(existing.Token));

        if (hasExistingToken && AskYesNo(input, output, "A web access token already exists. Keep it?", defaultYes: true))
        {
            if (!string.IsNullOrWhiteSpace(existing!.EncryptedToken))
            {
                encryptedToken = existing.EncryptedToken;
            }
            else
            {
                plainToken = existing!.Token;
                output.WriteLine("  Keeping the existing PLAINTEXT token (consider regenerating to store it DPAPI-encrypted instead).");
            }
        }
        else
        {
            generatedPlain = DarlingManagedPostgres.GeneratePassword();
            encryptedToken = DarlingSecrets.Protect(generatedPlain);
        }

        while (true)
        {
            var listen = SelectListenAddress(input, output, "web");
            if (listen is null)
            {
                output.WriteLine("Cancelled — no changes made.");
                return null;
            }

            var allowFrom = Prompt(input, output, "Allowed remote CIDR (e.g. 192.168.1.0/24)");
            if (allowFrom is null)
            {
                output.WriteLine("Cancelled — no changes made.");
                return null;
            }

            var candidate = new WebConfig
            {
                Enabled = currentWeb.Enabled,
                Port = currentWeb.Port,
                Network = new WebNetworkConfig
                {
                    Listen = listen,
                    AllowFrom = allowFrom,
                    EncryptedToken = encryptedToken,
                    Token = plainToken,
                },
            };

            var decision = DarlingWebHostService.ResolveWebBind(candidate, managed: true);
            if (decision.Mode == DarlingHostBinding.BindMode.NetworkAndLoopback)
            {
                return (listen, allowFrom, encryptedToken, plainToken, generatedPlain);
            }

            output.WriteLine($"  Not accepted: {WebDegradeText(decision.Reason, candidate)}");
            output.WriteLine("  Let us try again.");
        }
    }

    /// <summary>Human text for a web bind degrade reason (presentation only — the resolver decides; this narrates).</summary>
    private static string WebDegradeText(DarlingHostBinding.BindReason reason, WebConfig web) => reason switch
    {
        DarlingHostBinding.BindReason.ListenInvalid =>
            $"web.network.listen '{web.Network?.Listen}' is not a valid IP address (use a specific IP, or 0.0.0.0 for all interfaces).",
        DarlingHostBinding.BindReason.TokenMissing =>
            "no access token is set (the wizard should have supplied one — this is unexpected).",
        DarlingHostBinding.BindReason.AllowFromInvalid =>
            $"web.network.allowFrom '{web.Network?.AllowFrom}' is not a valid CIDR or its address family does not match listen (e.g. 192.168.1.0/24, host bits zeroed).",
        DarlingHostBinding.BindReason.ManagedModeRequired =>
            "network exposure is managed-mode only.",
        _ => "the web bind resolver rejected these values.",
    };

    /// <summary>Human text for an MCP bind degrade reason (presentation only — the resolver decides; this narrates).</summary>
    private static string McpDegradeText(DarlingMcpHostService.McpBindReason reason, McpConfig mcp) => reason switch
    {
        DarlingMcpHostService.McpBindReason.ListenInvalid =>
            $"mcp.network.listen '{mcp.Network?.Listen}' is not a valid IP address (use a specific IP, or 0.0.0.0 for all interfaces).",
        DarlingMcpHostService.McpBindReason.TokenMissing =>
            "no bearer token is set (the wizard should have supplied one — this is unexpected).",
        DarlingMcpHostService.McpBindReason.AllowFromInvalid =>
            $"mcp.network.allowFrom '{mcp.Network?.AllowFrom}' is not a valid CIDR or its address family does not match listen (e.g. 192.168.1.0/24, host bits zeroed).",
        DarlingMcpHostService.McpBindReason.ManagedModeRequired =>
            "network exposure is managed-mode only.",
        _ => "the MCP resolver rejected these values.",
    };

    /// <summary>Removes all three network blocks (symmetric with the reconcilers), validating parse + loopback-only before the timestamped write, then offers a restart. Shared by the managed Disable choice and the BYO cleanup.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task<int> DisableExposureAsync(
        string resolvedPath, string originalText, TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var newText = DarlingNetworkConfigEditor.RemoveNetworkBlock(originalText, "postgres");
        newText = DarlingNetworkConfigEditor.RemoveNetworkBlock(newText, "mcp");
        newText = DarlingNetworkConfigEditor.RemoveNetworkBlock(newText, "web");

        if (string.Equals(newText, originalText, StringComparison.Ordinal))
        {
            output.WriteLine("No live network block found — already loopback-only. Nothing to change.");
            return 0;
        }

        DarlingConfig reparsed;
        try
        {
            reparsed = DarlingConfig.Parse(newText);
        }
        catch (Exception ex)
        {
            error.WriteLine($"Internal error: the disabled darling.json did not parse ({ex.Message}). No changes were written.");
            return 1;
        }

        var (certPath, keyPath) = ResolveStoreCertPaths(reparsed.Postgres);
        var storeStillExposed = DarlingManagedPostgres.ResolveNetworkExposure(reparsed.Postgres.Network, certPath, keyPath).Exposed;
        var mcpStillExposed = DarlingMcpHostService.ResolveMcpBind(reparsed.Mcp, reparsed.Postgres.Managed).Mode
            == DarlingMcpHostService.McpBindMode.NetworkAndLoopback;
        var webStillExposed = DarlingWebHostService.ResolveWebBind(reparsed.Web, reparsed.Postgres.Managed).Mode
            == DarlingHostBinding.BindMode.NetworkAndLoopback;
        if (storeStillExposed || mcpStillExposed || webStillExposed)
        {
            error.WriteLine("Internal error: exposure is still present after removal. No changes were written.");
            return 1;
        }

        if (!await WriteWithBackupAsync(resolvedPath, newText, output, error, cancellationToken))
        {
            return 1;
        }

        output.WriteLine("Network exposure removed. The service reconciles the endpoints OFF (pg_hba rule, firewall, ssl) on its next start.");
        await OfferRestartAsync(input, output, error, cancellationToken);
        return 0;
    }

    /// <summary>Offers to restart the service so the edit applies. Elevated: does it via a 90s-budget Restart-Service + WaitForStatus; otherwise prints the exact manual commands (non-fatal, guidance-first).</summary>
    [SupportedOSPlatform("windows")]
    private static async Task OfferRestartAsync(TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        output.WriteLine();
        if (!AskYesNo(input, output, $"Restart the '{ServiceName}' service now to apply?", defaultYes: false))
        {
            output.WriteLine("Apply later by restarting the service:");
            PrintManualRestartCommands(output);
            return;
        }

        if (!IsElevated())
        {
            output.WriteLine("This shell is not elevated, so it cannot control the service. Run these in an ELEVATED PowerShell:");
            PrintManualRestartCommands(output);
            return;
        }

        output.WriteLine($"Restarting '{ServiceName}' (this can take up to ~90 seconds)...");
        var command =
            $"Restart-Service -Name '{ServiceName}' -Force; " +
            $"(Get-Service -Name '{ServiceName}').WaitForStatus('Running', [TimeSpan]::FromSeconds(75))";
        try
        {
            var (exitCode, psOutput) = await DarlingManagedPostgres.RunPowerShellAsync(command, cancellationToken, TimeSpan.FromSeconds(90));
            if (exitCode == 0)
            {
                output.WriteLine($"Service '{ServiceName}' restarted and is Running.");
            }
            else
            {
                error.WriteLine($"Restart did not confirm Running (exit {exitCode}): {psOutput}");
                output.WriteLine("Restart it manually:");
                PrintManualRestartCommands(output);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Restart attempt failed ({ex.Message}).");
            output.WriteLine("Restart it manually:");
            PrintManualRestartCommands(output);
        }
    }

    private static void PrintManualRestartCommands(TextWriter output)
    {
        output.WriteLine($"  Restart-Service -Name '{ServiceName}' -Force");
        output.WriteLine($"  (or:  sc.exe stop \"{ServiceName}\"   then   sc.exe start \"{ServiceName}\")");
    }

    /// <summary>Prints the handoff reminders: the scoped firewall command(s), the store's --print-viewer-connection step, and the web dashboard's browser login hint.</summary>
    [SupportedOSPlatform("windows")]
    private static void PrintNextSteps(
        TextWriter output,
        bool storeConfigured, int storePort, string? storeCidr,
        bool mcpConfigured, int mcpPort, string? mcpCidr, bool mcpEnabled,
        bool webConfigured, int webPort, string? webCidr, bool webEnabled, string? webListen)
    {
        output.WriteLine();
        output.WriteLine("Next steps:");
        if (storeConfigured)
        {
            output.WriteLine("  Store firewall rule (run ELEVATED; scoped to the port + CIDR):");
            output.WriteLine("    " + DarlingManagedPostgres.BuildFirewallEnableCommand(
                $"PerformanceMonitor Darling store (port {storePort})", storePort, storeCidr!));
            output.WriteLine("  After the service restarts (which generates the TLS cert), get the remote viewer's");
            output.WriteLine("  paste-ready connection string + certificate with:");
            output.WriteLine("    PerformanceMonitor.Darling.Service.exe --print-viewer-connection");
        }

        if (mcpConfigured)
        {
            output.WriteLine("  MCP firewall rule (run ELEVATED; scoped to the port + CIDR):");
            output.WriteLine("    " + DarlingManagedPostgres.BuildFirewallEnableCommand(
                DarlingMcpHostService.McpFirewallRuleName(mcpPort), mcpPort, mcpCidr!));
            if (!mcpEnabled)
            {
                output.WriteLine("  NOTE: mcp.enabled is false, so the MCP endpoint stays down until you enable MCP in the");
                output.WriteLine("        Viewer's Settings. The network block you just wrote applies once MCP is enabled.");
            }
        }

        if (webConfigured)
        {
            output.WriteLine("  Web dashboard firewall rule (run ELEVATED; scoped to the port + CIDR — --enable-web also");
            output.WriteLine("  reconciles this rule for you):");
            output.WriteLine("    " + DarlingManagedPostgres.BuildFirewallEnableCommand(
                DarlingWebHostService.WebFirewallRuleName(webPort), webPort, webCidr!));

            /* The one login step a human does differently for Web: a remote browser presents the access token
               once via ?token= and is 302'd back with a session cookie. A 0.0.0.0 bind has no single address
               to print, so fall back to a placeholder. */
            var webHost = webListen == "0.0.0.0" ? "<a-LAN-IP-of-this-machine>" : webListen;
            output.WriteLine("  Remote browser login (after the service restarts):");
            output.WriteLine($"    http://{webHost}:{webPort}/?token=<your-access-token>");
            output.WriteLine("  (the token is exchanged for a session cookie and stripped from the URL; loopback needs no token)");
            if (!webEnabled)
            {
                output.WriteLine("  NOTE: web.enabled is false, so the dashboard stays down until you enable it with --enable-web");
                output.WriteLine("        or the Viewer's Settings. The network block you just wrote applies once the web dashboard");
                output.WriteLine("        is enabled.");
            }
        }
    }

    /// <summary>
    /// Backs up darling.json to a timestamped sibling, then writes the new text. Returns false (with a
    /// message) on any I/O failure.
    ///
    /// <para><b>The backup is hardened, because it is a second copy of the secret.</b> Every edit here
    /// copies a file holding each monitored server's encrypted password and the MCP/web tokens, and
    /// <c>File.Copy</c> does NOT carry the source's DACL — the new file takes the DIRECTORY's inheritable
    /// ACEs instead. Measured, not assumed: copying a file whose DACL is protected with one ACE produces a
    /// backup that is unprotected with three inherited ones. On the documented install location, a folder
    /// created directly under <c>C:\</c>, those inherited ACEs include <c>BUILTIN\Users: Read</c> — so
    /// without this every <c>--rotate-token</c> or <c>--disable</c> would drop a world-readable copy of
    /// every credential beside a correctly hardened <c>darling.json</c>, defeating the ACL that is the
    /// whole protection boundary for LocalMachine-scope DPAPI blobs (#1721).</para>
    ///
    /// <para>Copy-then-harden leaves a sub-millisecond window where the backup exists with inherited
    /// access. That is stated rather than hidden: closing it would mean creating the file with an explicit
    /// security descriptor, which is a bigger change than the exposure warrants for an operator-initiated,
    /// elevated, interactive command — but it is the reason this is a mitigation of a leak rather than a
    /// proof of its absence.</para>
    /// </summary>
    internal static async Task<bool> WriteWithBackupAsync(
        string resolvedPath, string newText, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var backupPath = resolvedPath + ".bak-" + stamp;
            for (var suffix = 2; File.Exists(backupPath); suffix++)
            {
                backupPath = $"{resolvedPath}.bak-{stamp}-{suffix}";
            }

            File.Copy(resolvedPath, backupPath, overwrite: false);
            if (OperatingSystem.IsWindows())
            {
                HardenConfigBackup(backupPath, error);
            }

            /* WriteAllText TRUNCATES an existing file rather than recreating it, so darling.json keeps
               whatever DACL it already had — only the new backup needs hardening. */
            await File.WriteAllTextAsync(resolvedPath, newText, cancellationToken);
            output.WriteLine($"Wrote {resolvedPath}");
            output.WriteLine($"Backup saved: {backupPath}");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Could not write the configuration: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Locks a freshly written config backup to the same principals as <c>darling.json</c> itself.
    ///
    /// <para>Never fatal — the edit that produced the backup has already been decided on and refusing to
    /// finish it over a permissions problem would leave the operator worse off. But it is reported LOUDLY
    /// and names the file, because a silent best-effort ACL failure is exactly how #1721 persisted
    /// unnoticed across months of service starts: the failure was logged once per start and read by nobody
    /// until a deploy check happened to look. An operator who just ran a command is the one person
    /// guaranteed to be watching, so tell them while they are there.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void HardenConfigBackup(string backupPath, TextWriter error)
    {
        try
        {
            DarlingFileSecurity.HardenFile(backupPath, allowInteractiveRead: true);
        }
        catch (Exception ex)
        {
            error.WriteLine($"WARNING: could not restrict permissions on the backup {backupPath} ({ex.Message}).");
            error.WriteLine("         It is a full copy of your encrypted passwords and access tokens. Delete it, or");
            error.WriteLine("         restrict it by hand, before leaving this machine.");
            return;
        }

        if (DarlingFileSecurity.IsReadableByOrdinaryUsers(backupPath))
        {
            error.WriteLine($"WARNING: {backupPath} is still readable by ordinary local users after hardening.");
            error.WriteLine("         It is a full copy of your encrypted passwords and access tokens. Delete it, or");
            error.WriteLine("         move the install out of a world-readable folder.");
        }
    }

    /// <summary>True when the current process is running elevated (Administrators role) — required to control the service.</summary>
    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Writes a prompt and reads a trimmed line. Returns null on EOF (input exhausted); an empty line yields <paramref name="defaultValue"/> (or "").</summary>
    private static string? Prompt(TextReader input, TextWriter output, string label, string? defaultValue = null)
    {
        output.Write(defaultValue is null ? $"{label}: " : $"{label} [{defaultValue}]: ");
        var line = input.ReadLine();
        if (line is null)
        {
            return null;
        }

        line = line.Trim();
        return line.Length == 0 ? defaultValue ?? "" : line;
    }

    /// <summary>Yes/no prompt; EOF or an empty line yields <paramref name="defaultYes"/>. A leading 'y' (any case) is yes.</summary>
    private static bool AskYesNo(TextReader input, TextWriter output, string label, bool defaultYes)
    {
        output.Write($"{label} [{(defaultYes ? "Y/n" : "y/N")}]: ");
        var line = input.ReadLine();
        if (line is null)
        {
            return defaultYes;
        }

        line = line.Trim();
        return line.Length == 0 ? defaultYes : line.StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    /* ================================================================================================
       --enable-mcp / --disable-mcp / --enable-web / --disable-web: headless endpoint bring-up.

       Two gaps these close on a headless box (no WPF Viewer, and the service runs as a virtual service
       account that CANNOT modify Windows Firewall, so its best-effort self-reconcile silently fails):
         (a) ENABLE/DISABLE an endpoint. After the first run mcp.enabled/web.enabled in darling.json are only
             a SEED; the store (config.config_service.mcp_enabled/web_enabled) is authoritative and is normally
             toggled only by the Viewer's Settings. These verbs write the store directly — a TARGETED UPDATE
             whose BEFORE-UPDATE self-bump trigger increments config_version, so the worker hot-reloads within
             one sweep (no restart). We NEVER set config_version ourselves (the trigger owns it), and never
             touch paused or the OTHER endpoint's flag.
         (b) OPEN/CLOSE the endpoint's firewall, but only when its darling.json network block opts into LAN
             exposure. Elevated -> run the SAME scoped, idempotent-by-DisplayName rule the host reconciles;
             not elevated -> print the exact elevated command as a handoff (the store toggle already
             succeeded, so a non-elevated shell is never a failure).

       Managed-mode only (the owner credential + firewall are managed concerns; BYO governs its own
       config_service + exposure). Windows-only: DPAPI credential decrypt + WindowsPrincipal + firewall — the
       Program dispatch is OperatingSystem.IsWindows()-guarded, mirroring --print-viewer-connection.
       ================================================================================================ */

    /// <summary>The CLI's targeted store write that ENABLES the MCP endpoint on the single config_service row
    /// (id=1). Sets only <c>mcp_enabled</c> + the audit columns; the BEFORE-UPDATE self-bump trigger fires
    /// <c>config_version</c> (deliberately NOT set here) so the worker hot-reloads. Pure — Darling.Tests pin the shape.</summary>
    public const string EnableMcpStoreSql =
        "UPDATE config.config_service SET mcp_enabled = TRUE, updated_at = (now() AT TIME ZONE 'UTC'), updated_by = 'cli' WHERE id = 1";

    /// <summary>The CLI's targeted store write that DISABLES the MCP endpoint (twin of <see cref="EnableMcpStoreSql"/>).</summary>
    public const string DisableMcpStoreSql =
        "UPDATE config.config_service SET mcp_enabled = FALSE, updated_at = (now() AT TIME ZONE 'UTC'), updated_by = 'cli' WHERE id = 1";

    /// <summary>The CLI's targeted store write that ENABLES the read-only web dashboard endpoint (twin of <see cref="EnableMcpStoreSql"/>).</summary>
    public const string EnableWebStoreSql =
        "UPDATE config.config_service SET web_enabled = TRUE, updated_at = (now() AT TIME ZONE 'UTC'), updated_by = 'cli' WHERE id = 1";

    /// <summary>The CLI's targeted store write that DISABLES the web dashboard endpoint (twin of <see cref="EnableMcpStoreSql"/>).</summary>
    public const string DisableWebStoreSql =
        "UPDATE config.config_service SET web_enabled = FALSE, updated_at = (now() AT TIME ZONE 'UTC'), updated_by = 'cli' WHERE id = 1";

    /// <summary>Which optional endpoint a toggle verb targets — selects the store column, firewall rule name,
    /// darling.json network block, and seed-key note.</summary>
    private enum EndpointKind
    {
        Mcp,
        Web,
    }

    /// <summary>The firewall step a toggle verb takes, from the pure (exposed, elevated) inputs (pin-tested via
    /// <see cref="ClassifyFirewallPlan"/>).</summary>
    public enum EndpointFirewallPlan
    {
        /// <summary>Loopback-only (no LAN-exposure block) — no firewall change is needed.</summary>
        LoopbackNoAction,

        /// <summary>Exposed + elevated — run the scoped enable/disable command directly.</summary>
        RunElevated,

        /// <summary>Exposed + NOT elevated — print the exact elevated command for the operator to run by hand.</summary>
        Handoff,
    }

    /// <summary>PURE firewall-step decision for a toggle verb (unit-tested): a loopback-only endpoint needs no
    /// rule; an exposed endpoint runs the rule when elevated, otherwise hands the exact command off. Shared by
    /// enable and disable alike (disable just runs/hands-off the removal command instead of the open command).</summary>
    public static EndpointFirewallPlan ClassifyFirewallPlan(bool exposed, bool elevated) =>
        !exposed ? EndpointFirewallPlan.LoopbackNoAction
        : elevated ? EndpointFirewallPlan.RunElevated
        : EndpointFirewallPlan.Handoff;

    /// <summary>Whether an ENABLE toggle's <c>allowFrom</c> can be used as a firewall <c>-RemoteAddress</c> (#1646).</summary>
    public enum EndpointAllowFromVerdict
    {
        /// <summary>Absent/blank — the service would fail-close this endpoint to loopback, so there is nothing to open.</summary>
        Missing,

        /// <summary>Present but not a CIDR — REFUSE. Never build a firewall command from it.</summary>
        Invalid,

        /// <summary>A valid CIDR; the canonical <c>IPNetwork.ToString()</c> form is what reaches the command.</summary>
        Valid,
    }

    /// <summary>
    /// PURE <c>allowFrom</c> gate for a toggle verb (#1646). <c>darling.json</c> is operator-supplied text that
    /// <see cref="DarlingConfig.Load"/> only deserializes — it never calls <see cref="DarlingConfig.Validate"/> —
    /// so this was the ONE <see cref="DarlingManagedPostgres.BuildFirewallEnableCommand"/> caller that reached
    /// the PowerShell <c>-Command</c> string with an unparsed value, where a blank-check was the only gate.
    /// Every other call site passes a canonicalized <c>IPNetwork.ToString()</c>; this makes that universal.
    /// Parsing is the security property, not the formatting: <see cref="IPNetwork.TryParse"/> accepts ONLY a
    /// single <c>address/prefix</c> pair, so no shell metacharacter, statement separator, or second CIDR can
    /// survive it — and <paramref name="canonicalCidr"/> is the PARSER'S output, never the caller's string, so
    /// nothing unvalidated is carried through even on the valid path. That last point is load-bearing rather
    /// than belt-and-braces: <c>TryParse</c> MASKS host bits instead of rejecting them (<c>192.168.1.5/24</c>
    /// parses, as <c>192.168.1.0/24</c>), so "validate, then use the original" would forward a string the
    /// parser had already decided meant something else.
    /// </summary>
    public static EndpointAllowFromVerdict ClassifyAllowFrom(string? allowFrom, out string canonicalCidr)
    {
        canonicalCidr = "";

        if (string.IsNullOrWhiteSpace(allowFrom))
        {
            return EndpointAllowFromVerdict.Missing;
        }

        if (!IPNetwork.TryParse(allowFrom.Trim(), out var cidr))
        {
            return EndpointAllowFromVerdict.Invalid;
        }

        canonicalCidr = cidr.ToString();
        return EndpointAllowFromVerdict.Valid;
    }

    /// <summary>
    /// Enables the embedded MCP endpoint on a headless managed deployment: flips
    /// <c>config.config_service.mcp_enabled</c> TRUE (the live switch — the worker hot-reloads within one sweep
    /// via the self-bump trigger, no restart) and, when mcp.network opts into LAN exposure, opens the scoped
    /// firewall rule if elevated (else prints it as an elevated handoff — the toggle still succeeded). Managed-
    /// mode only; Windows-only (the caller is <c>OperatingSystem.IsWindows()</c>-guarded, mirroring
    /// <see cref="PrintViewerConnectionAsync"/>). Returns 0 on a successful toggle; 1 on a load/mode/credential/unseeded error.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Task<int> EnableMcpAsync(
        string? configPath, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        ToggleEndpointAsync(EndpointKind.Mcp, enable: true, configPath, output, error, cancellationToken);

    /// <summary>Disables the embedded MCP endpoint (twin of <see cref="EnableMcpAsync"/>): flips
    /// <c>mcp_enabled</c> FALSE live and, when exposed, best-effort removes the scoped firewall rule (elevated) or
    /// prints the removal as a handoff. A firewall-removal failure is non-fatal.</summary>
    [SupportedOSPlatform("windows")]
    public static Task<int> DisableMcpAsync(
        string? configPath, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        ToggleEndpointAsync(EndpointKind.Mcp, enable: false, configPath, output, error, cancellationToken);

    /// <summary>Enables the embedded read-only web dashboard endpoint (twin of <see cref="EnableMcpAsync"/> for
    /// <c>web_enabled</c> + the web firewall rule).</summary>
    [SupportedOSPlatform("windows")]
    public static Task<int> EnableWebAsync(
        string? configPath, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        ToggleEndpointAsync(EndpointKind.Web, enable: true, configPath, output, error, cancellationToken);

    /// <summary>Disables the embedded web dashboard endpoint (twin of <see cref="DisableMcpAsync"/> for
    /// <c>web_enabled</c> + the web firewall rule).</summary>
    [SupportedOSPlatform("windows")]
    public static Task<int> DisableWebAsync(
        string? configPath, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        ToggleEndpointAsync(EndpointKind.Web, enable: false, configPath, output, error, cancellationToken);

    /// <summary>The shared body of the four endpoint-toggle verbs: load + managed-mode guard + owner-credential
    /// build (mirroring <see cref="PrintViewerConnectionAsync"/>), a targeted <c>config_service</c> UPDATE (0
    /// rows = an unseeded store), the live-apply note, the firewall step, and the "darling.json enabled is only
    /// the seed" UX note. Never touches config_version (the self-bump trigger owns it) or the OTHER endpoint's flag.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task<int> ToggleEndpointAsync(
        EndpointKind endpoint, bool enable, string? configPath, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        DarlingConfig config;
        try
        {
            config = DarlingConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            error.WriteLine($"Could not load configuration: {ex.Message}");
            return 1;
        }

        var verb = VerbName(endpoint, enable);
        var endpointLabel = endpoint == EndpointKind.Mcp ? "MCP" : "web dashboard";
        var column = endpoint == EndpointKind.Mcp ? "mcp_enabled" : "web_enabled";
        var seedKey = endpoint == EndpointKind.Mcp ? "mcp.enabled" : "web.enabled";

        /* Managed-mode guard (mirrors PrintViewerConnectionAsync): the owner credential + firewall reconcile are
           managed concerns. In BYO the operator's own PostgreSQL holds config_service — toggle it there. */
        var postgres = config.Postgres;
        if (postgres is null)
        {
            error.WriteLine("postgres section is required.");
            return 1;
        }

        if (!postgres.Managed)
        {
            error.WriteLine(
                $"{verb} applies to the managed store only. In bring-your-own mode (postgres.connectionString), the " +
                $"endpoint enable flags live in YOUR PostgreSQL's config.config_service ({column}) — toggle them there.");
            return 1;
        }

        /* The OWNER connection (the service's own superuser credential) — null until the worker's first run has
           written the DPAPI-protected credential (i.e. the service has never initialized the store). */
        var connectionString = DarlingManagedPostgres.TryBuildConnectionStringFromStoredCredential(postgres);
        if (connectionString is null)
        {
            error.WriteLine(
                "The managed store credential does not exist yet — start the PerformanceMonitor Darling service once " +
                "so its first run initializes the store, then re-run this command.");
            return 1;
        }

        /* The TARGETED store write. A DIRECT config_service UPDATE self-bumps config_version via the BEFORE-UPDATE
           trigger, so the worker hot-reloads within one sweep — we never touch config_version ourselves. */
        var sql = EndpointToggleSql(endpoint, enable);
        int rows;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            rows = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Could not update the control-plane store: {ex.Message}");
            return 1;
        }

        if (rows == 0)
        {
            error.WriteLine(
                "The control-plane store is not seeded yet (config.config_service has no id=1 row) — start the " +
                "PerformanceMonitor Darling service once so it seeds the store, then re-run this command.");
            return 1;
        }

        output.WriteLine(
            $"{endpointLabel} endpoint {(enable ? "ENABLED" : "DISABLED")} in the control-plane store " +
            $"(config.config_service.{column} = {(enable ? "true" : "false")}).");
        output.WriteLine(
            "The running service applies this LIVE within one collection sweep (the write self-bumps the reload " +
            "beacon) — no restart needed.");

        await ReconcileEndpointFirewallAsync(endpoint, enable, config, output, error, cancellationToken);

        output.WriteLine();
        output.WriteLine(
            $"NOTE: '{seedKey}' in darling.json is only the FIRST-RUN seed, not the live switch. After the first run " +
            $"the store (config.config_service.{column}) is authoritative — which is exactly what this command changed.");

        return 0;
    }

    /// <summary>The four endpoint-toggle SQL strings, selected by (endpoint, enable) — the routing the public verbs share.</summary>
    private static string EndpointToggleSql(EndpointKind endpoint, bool enable) => (endpoint, enable) switch
    {
        (EndpointKind.Mcp, true) => EnableMcpStoreSql,
        (EndpointKind.Mcp, false) => DisableMcpStoreSql,
        (EndpointKind.Web, true) => EnableWebStoreSql,
        (EndpointKind.Web, false) => DisableWebStoreSql,
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
    };

    /// <summary>The verb spelling for a toggle (for error + handoff text).</summary>
    private static string VerbName(EndpointKind endpoint, bool enable) => (endpoint, enable) switch
    {
        (EndpointKind.Mcp, true) => "--enable-mcp",
        (EndpointKind.Mcp, false) => "--disable-mcp",
        (EndpointKind.Web, true) => "--enable-web",
        (EndpointKind.Web, false) => "--disable-web",
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
    };

    /// <summary>
    /// The firewall half of a toggle (defense-in-depth, never the boundary — pg_hba/token + the in-app CIDR
    /// check are). Only acts when the endpoint's darling.json network block opts into LAN exposure (a non-loopback
    /// listen, via the shared <see cref="DarlingNetwork.IsExposedListenAddress"/>). Uses the SAME scoped,
    /// idempotent-by-DisplayName rule name the host's self-reconcile uses
    /// (<see cref="DarlingMcpHostService.McpFirewallRuleName"/> / <see cref="DarlingWebHostService.WebFirewallRuleName"/>)
    /// and the SAME pure command builders. Elevated -> runs the rule; otherwise prints the exact elevated command
    /// — the store toggle already succeeded, so a non-elevated shell is a handoff, never a failure. A firewall
    /// failure is likewise non-fatal.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task ReconcileEndpointFirewallAsync(
        EndpointKind endpoint, bool enable, DarlingConfig config, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var (port, listen, allowFrom, ruleName) = endpoint == EndpointKind.Mcp
            ? (config.Mcp.Port, config.Mcp.Network?.Listen, config.Mcp.Network?.AllowFrom, DarlingMcpHostService.McpFirewallRuleName(config.Mcp.Port))
            : (config.Web.Port, config.Web.Network?.Listen, config.Web.Network?.AllowFrom, DarlingWebHostService.WebFirewallRuleName(config.Web.Port));

        var exposed = DarlingNetwork.IsExposedListenAddress(listen);
        var plan = ClassifyFirewallPlan(exposed, IsElevated());

        output.WriteLine();
        if (plan == EndpointFirewallPlan.LoopbackNoAction)
        {
            output.WriteLine(enable
                ? "Firewall: this endpoint has no LAN-exposure block, so it binds LOOPBACK ONLY — no firewall change is " +
                  "needed. To expose it on the LAN, run --configure-network (which writes the listen/allowFrom/token block)."
                : "Firewall: this endpoint is loopback-only — there is no scoped firewall rule to remove.");
            return;
        }

        /* #1646: parse allowFrom as a CIDR BEFORE it can reach a PowerShell -Command string, and pass the
           parser's canonical form — the posture every other BuildFirewallEnableCommand caller already had.
           An unparseable value is refused outright: the firewall is NOT touched and nothing is printed for an
           operator to paste into an elevated shell, because the injected text would run either way (this verb
           runs the command itself when elevated, and hands it to a human to run elevated when it is not). */
        var canonicalCidr = "";
        if (enable)
        {
            switch (ClassifyAllowFrom(allowFrom, out canonicalCidr))
            {
                case EndpointAllowFromVerdict.Missing:
                    /* Non-loopback listen but no allowFrom CIDR: the service itself would fail-close this to loopback, so
                       there is nothing to open. Point at the wizard rather than emit a malformed New-NetFirewallRule. */
                    output.WriteLine(
                        $"Firewall: the network block sets listen '{listen}' but no allowFrom CIDR, so the service will bind " +
                        "loopback-only until it is completed. Run --configure-network to finish the block; not opening the firewall.");
                    return;

                case EndpointAllowFromVerdict.Invalid:
                    error.WriteLine(
                        $"Firewall: allowFrom in darling.json is not a valid CIDR, so NO firewall change was made and no " +
                        "command is being printed to run by hand. The endpoint toggle itself already succeeded; the service " +
                        "will bind loopback-only until allowFrom is fixed. Expected an address/prefix with the host bits " +
                        "zeroed, e.g. 192.168.1.0/24 or 2001:db8::/32. Run --configure-network to rewrite the block.");
                    return;
            }
        }

        var command = enable
            ? DarlingManagedPostgres.BuildFirewallEnableCommand(ruleName, port, canonicalCidr)
            : DarlingManagedPostgres.BuildFirewallDisableCommand(ruleName);

        if (plan == EndpointFirewallPlan.RunElevated)
        {
            await RunFirewallCommandAsync(command, ruleName, enable, output, error, cancellationToken);
            return;
        }

        /* Handoff (not elevated) — the store toggle already succeeded; print the exact command to run elevated. */
        output.WriteLine(enable
            ? "Firewall: this shell is not elevated, so the endpoint was enabled but its firewall rule was NOT opened. " +
              "Run this in an ELEVATED PowerShell to open the port (scoped to the port + CIDR):"
            : "Firewall: this shell is not elevated, so the firewall rule was NOT removed. Run this in an ELEVATED PowerShell to close the port:");
        output.WriteLine("  " + command);
    }

    /// <summary>Runs a scoped firewall command via the shared PowerShell runner and reports the outcome. NEVER
    /// throws (except on cancellation) — the store toggle already succeeded, so a firewall failure degrades to a
    /// printed elevated hand-off, never a non-zero exit.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task RunFirewallCommandAsync(
        string command, string ruleName, bool enable, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, psOutput) = await DarlingManagedPostgres.RunPowerShellAsync(command, cancellationToken);
            if (exitCode == 0)
            {
                output.WriteLine($"Firewall rule '{ruleName}' {(enable ? "opened" : "removed")}.");
                return;
            }

            error.WriteLine(
                $"Firewall rule {(enable ? "open" : "removal")} did not confirm (exit {exitCode}: {psOutput}). " +
                "Run this in an elevated PowerShell:");
            error.WriteLine("  " + command);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Firewall rule {(enable ? "open" : "removal")} failed ({ex.Message}). Run this in an elevated PowerShell:");
            error.WriteLine("  " + command);
        }
    }
}
