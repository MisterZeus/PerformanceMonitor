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
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Service.Hosting;
using PerformanceMonitor.Darling.Service.Mcp;
using PerformanceMonitor.Darling.Storage;

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

    /// <summary>The verb <see cref="ExportViewerConfigAsync"/> handles — write a COMPLETE viewer darling.json +
    /// server.crt + README.txt an operator copies to the viewer machine as-is (#1953).</summary>
    public static bool IsExportViewerConfigVerb(string arg) =>
        string.Equals(arg, "--export-viewer-config", StringComparison.OrdinalIgnoreCase);

    /// <summary>The verb <see cref="ConfigureNetworkAsync"/> handles — the interactive exposure wizard (#1561).</summary>
    public static bool IsConfigureNetworkVerb(string arg) =>
        string.Equals(arg, "--configure-network", StringComparison.OrdinalIgnoreCase);

    /// <summary>The verb <see cref="ConfigureFirewallAsync"/> handles — reconcile every scoped Darling firewall
    /// rule from darling.json, elevated (#1771). This is the ONLY place rules are created; the service itself
    /// only verifies them.</summary>
    public static bool IsConfigureFirewallVerb(string arg) =>
        string.Equals(arg, "--configure-firewall", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>The verb <see cref="BackfillRollupsAsync"/> handles — materialize the query-acceleration rollups
    /// back to raw's oldest row, behind a disk preflight (#1759 Phase 2).</summary>
    public static bool IsBackfillRollupsVerb(string arg) =>
        string.Equals(arg, "--backfill-rollups", StringComparison.OrdinalIgnoreCase);

    /// <summary>The verb <see cref="CollapseLegacySlicesAsync"/> handles — repair the pre-#1907 Query Store
    /// split slices still sitting in stored rows, then re-materialize what they fed (#1912).</summary>
    public static bool IsCollapseLegacySlicesVerb(string arg) =>
        string.Equals(arg, "--collapse-legacy-slices", StringComparison.OrdinalIgnoreCase);

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
        || IsExportViewerConfigVerb(arg)
        || IsConfigureNetworkVerb(arg)
        || IsConfigureFirewallVerb(arg)
        || IsEnableMcpVerb(arg)
        || IsDisableMcpVerb(arg)
        || IsEnableWebVerb(arg)
        || IsDisableWebVerb(arg)
        || IsBackfillRollupsVerb(arg)
        || IsCollapseLegacySlicesVerb(arg);

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
        "  PerformanceMonitor.Darling.Service.exe --export-viewer-config [dir]  Write a ready-to-copy viewer folder (darling.json + server.crt + README.txt)." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --configure-network Interactive LAN-exposure wizard." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --configure-firewall  Create/remove the scoped firewall rules to match darling.json (run elevated)." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --enable-mcp        Enable the MCP endpoint in the store and open its firewall (run elevated)." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --disable-mcp       Disable the MCP endpoint in the store and remove its firewall rule (run elevated)." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --enable-web        Enable the web dashboard in the store and open its firewall (run elevated)." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --disable-web       Disable the web dashboard in the store and remove its firewall rule (run elevated)." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --backfill-rollups  Materialize the retention rollups back over existing history, after a disk preflight." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --collapse-legacy-slices  Repair Query Store rows collected before the split-slice fix, then re-materialize the rollups they fed." + Environment.NewLine +
        "  PerformanceMonitor.Darling.Service.exe --backfill-rollups --dry-run   Show the plan, the disk estimate and the time budget, and change nothing.";

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

        var handoff = await ResolveViewerHandoffAsync(
            config, "--print-viewer-connection", "print", error, cancellationToken);
        if (handoff is null)
        {
            return 1;
        }

        /* The client-side Root Certificate placeholder: the operator saves the PEM below at this path on the
           VIEWER machine (a bare filename resolves against the viewer's working directory; an absolute path
           also works). Kept as a literal so the printed string is paste-ready. */
        const string clientCertificatePath = ViewerClientCertificateFileName;
        var connectionString = BuildViewerConnectionString(
            handoff.Host, handoff.Port, handoff.Role, handoff.Password, clientCertificatePath);

        /* Read the cert BEFORE anything reaches STDOUT: every STDERR line — including the missing-cert NOTE —
           must be emitted ahead of the payload (#1953 item 3). The field report watched the live password scroll
           past and only THEN saw the redirect advice, which is exactly backwards for a warning. */
        var certificate = File.Exists(handoff.CertificatePath)
            ? (await File.ReadAllTextAsync(handoff.CertificatePath, cancellationToken)).Trim()
            : null;

        /* Guidance + the live-secret warning go to STDERR, so redirecting STDOUT to a file or the clipboard
           captures the connection string + cert WITHOUT swallowing the warning (D8). */
        error.WriteLine();
        error.WriteLine(
            $"WARNING: the connection string below contains a LIVE database password (the '{handoff.Role}' role), written " +
            "to STDOUT. Redirect it to an ACL'd file or pipe it to the clipboard; do not leave it in shell " +
            "scrollback, CI logs, or a screenshare.");
        error.WriteLine("  Example (file):      PerformanceMonitor.Darling.Service.exe --print-viewer-connection > viewer-connection.txt");
        error.WriteLine("  Example (clipboard): PerformanceMonitor.Darling.Service.exe --print-viewer-connection | clip");
        error.WriteLine("  Example (no paste):  PerformanceMonitor.Darling.Service.exe --export-viewer-config   (writes the whole viewer folder for you)");
        if (string.Equals(handoff.Role, "admin", StringComparison.Ordinal))
        {
            error.WriteLine(
                "  NOTE: 'admin' is a WRITE credential holding the config-table pivot surface. Prefer the default " +
                "'viewer' (read-only) for a remote seat; if you must use 'admin', NTFS-ACL the laptop file too.");
        }

        if (certificate is not null)
        {
            error.WriteLine(
                $"Save the certificate block below as '{clientCertificatePath}' on the viewer machine (beside its " +
                "darling.json) and point \"Root Certificate\" at it — the store uses SSL Mode=VerifyFull, so the cert must match.");
        }
        else
        {
            error.WriteLine(
                $"NOTE: the server TLS certificate ({handoff.CertificatePath}) does not exist yet — the service generates it " +
                "on its first managed start with postgres.network exposed. Enable postgres.network, restart the " +
                "service, then re-run this command to emit the cert for verify-full.");
        }

        error.WriteLine();

        output.WriteLine(
            "# Paste into the viewer machine's darling.json -> postgres.connectionString (with postgres.managed = false):");
        output.WriteLine(connectionString);
        output.WriteLine();

        /* Emit the server cert PEM so the operator can copy it to the viewer machine. */
        if (certificate is not null)
        {
            output.WriteLine($"# Server TLS certificate ({DarlingManagedPostgres.ServerCertFileName}) — save as '{clientCertificatePath}' on the viewer machine:");
            output.WriteLine(certificate);
        }

        return 0;
    }

    /// <summary>
    /// The name the exported/pasted client-side certificate takes on the VIEWER machine, and therefore the
    /// <c>Root Certificate=</c> value both viewer verbs emit. Deliberately the same file name the store
    /// generates, so an operator who copies the folder never has to re-point anything.
    /// </summary>
    public const string ViewerClientCertificateFileName = "server.crt";

    /// <summary>Everything a remote viewer seat needs, resolved from darling.json + the managed store's
    /// on-disk material: where to dial, as whom, with which password, and the server cert to pin. Shared by
    /// <see cref="PrintViewerConnectionAsync"/> and <see cref="ExportViewerConfigAsync"/> so the two verbs
    /// cannot disagree about any of it.</summary>
    /// <param name="CertificatePath">The store-side <c>server.crt</c>; may not exist yet (each verb decides
    /// whether that is fatal — the print verb still has a useful string, the export does not).</param>
    private sealed record ViewerHandoff(
        string Host, int Port, string Role, string Password, string CertificatePath);

    /// <summary>
    /// Resolves the remote-viewer handoff material (D8), writing every refusal + warning to
    /// <paramref name="error"/> and returning null when the caller must exit 1. Managed-mode only: the DPAPI
    /// credential files and the generated TLS cert it reads exist only there — in BYO the operator's own
    /// PostgreSQL governs exposure + credentials (D-BYO). Windows-only (DPAPI-LocalMachine). The two string
    /// parameters shape message WORDING only — never the logic, so both verbs resolve identically.
    /// </summary>
    /// <param name="verb">The CLI verb to name in refusals, e.g. <c>--export-viewer-config</c>.</param>
    /// <param name="action">What the verb would have produced, e.g. "print" / "export", for the no-remote-connection refusal.</param>
    [SupportedOSPlatform("windows")]
    private static async Task<ViewerHandoff?> ResolveViewerHandoffAsync(
        DarlingConfig config, string verb, string action, TextWriter error, CancellationToken cancellationToken)
    {
        var postgres = config.Postgres;
        if (postgres is null)
        {
            error.WriteLine("postgres section is required.");
            return null;
        }

        if (!postgres.Managed)
        {
            error.WriteLine(
                $"{verb} is for the managed store only. In bring-your-own mode " +
                "(postgres.connectionString), your own PostgreSQL governs network exposure and credentials — " +
                "build the remote viewer's connection string from your own role + TLS setup.");
            return null;
        }

        /* The pg_hba login role the network exposure names — default viewer (read-only, the secure default).
           An explicitly-invalid value is a hard error: the store degrades to loopback for it, so no remote
           connection exists at all. */
        var network = postgres.Network;
        var role = DarlingNetwork.NormalizeNetworkRole(network?.Role);
        if (role is null)
        {
            error.WriteLine(
                $"postgres.network.role '{network?.Role}' is invalid — it must be \"viewer\" (default, read-only) " +
                $"or \"admin\". The store degrades to loopback for an unknown role, so there is no remote connection to {action}.");
            return null;
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
            return null;
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
            return null;
        }

        return new ViewerHandoff(
            host,
            postgres.Port,
            role,
            password,
            Path.Combine(Path.GetDirectoryName(credentialPath)!, DarlingManagedPostgres.ServerCertFileName));
    }

    /// <summary>
    /// #1953 — writes the viewer machine's COMPLETE handoff folder: a ready-to-use <c>darling.json</c> (the
    /// resolved connection string, <c>"managed": false</c> already set, every field commented in place), the
    /// store's <c>server.crt</c> beside it, and a <c>README.txt</c> that documents the fields — including the
    /// valid <c>Root Certificate=</c> values — for an operator who never opens the JSON. The field report that
    /// motivated this had to hand-merge <c>--print-viewer-connection</c>'s output into JSON copied out of the
    /// docs and discover the <c>managed</c> flip by trial; nobody writes that file by hand anymore.
    /// <para>Managed-mode + Windows only (same DPAPI/TLS material as <see cref="PrintViewerConnectionAsync"/>,
    /// resolved by the same helper). The cert is REQUIRED here, unlike the print verb: a folder whose
    /// verify-full connection cannot be completed is exactly the half-finished handoff this verb exists to
    /// kill, so a missing cert fails with the reason instead of exporting something broken.</para>
    /// <para><b>The written darling.json holds a LIVE credential</b> — the verb says so on STDERR, naming the
    /// file, before writing it, then ACLs it to SYSTEM + Administrators + this account + INTERACTIVE and
    /// CONFIRMS that (returning 2, not 0, when the secret is still readable — "exported" must not read as
    /// "protected" to a script). The password value itself is never echoed.</para>
    /// <para>It also refuses destinations rather than clobbering them, all decided BEFORE any config load or
    /// credential decrypt because they are pure path questions whose failure cannot be undone: the service's
    /// OWN config directory (exporting there replaced darling.json with the viewer's, destroying its servers,
    /// encrypted passwords and tokens — reproduced, and one keystroke from a legitimate command); a
    /// darling.json this verb did not write (an operator's file, or one pre-created by a local user who would
    /// keep OWNERSHIP through the harden — a Windows owner keeps WRITE_DAC); and a junction/symlink
    /// destination (creating one needs no privilege, and it redirects the cleartext credential). Re-exporting
    /// over its OWN output is silent — that is the documented step after a rotation.</para>
    /// </summary>
    /// <param name="outputDirectory">Where to write; null = a <c>viewer-config</c> folder beside darling.json.</param>
    /// <returns>0 exported and protected; 1 refused or failed; 2 exported but the secret is NOT protected.</returns>
    [SupportedOSPlatform("windows")]
    public static async Task<int> ExportViewerConfigAsync(
        string? configPath, string? outputDirectory, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var servicePath = DarlingConfig.ResolveConfigPath(configPath);
        var targetDirectory = ResolveViewerExportDirectory(servicePath, outputDirectory);

        /* Destination guards run FIRST — before any config load or credential decrypt — because they are pure
           path questions and because the failure they prevent is unrecoverable. Nothing below has touched
           DPAPI yet, so a bad argument costs nothing. */
        if (File.Exists(targetDirectory)
            || targetDirectory.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            /* This verb's positional argument is the DESTINATION, while every sibling verb's is a config path,
               so an operator with sibling muscle memory types darling.json here. */
            error.WriteLine(
                $"'{targetDirectory}' is a file, not a directory. This verb's argument is the DESTINATION folder " +
                "for the exported viewer files; to point it at a different darling.json, use --config <path> " +
                "(e.g. --export-viewer-config D:\\handoff --config C:\\Darling\\darling.json).");
            return 1;
        }

        var exportedConfigPath = Path.Combine(targetDirectory, ViewerConfigFileName);

        /* The unrecoverable one: exporting INTO the service's own config directory would overwrite the
           service's darling.json with the viewer's, destroying every monitored server, every DPAPI
           encryptedPassword, and the MCP/web tokens — none of which exist anywhere else. One keystroke from
           a legitimate command (the install directory is the obvious place to put a handoff folder). */
        if (string.Equals(
                Path.GetFullPath(exportedConfigPath), Path.GetFullPath(servicePath), StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine(
                $"Refusing to export into {targetDirectory}: that would overwrite the SERVICE's own {ViewerConfigFileName} " +
                $"({servicePath}) with the viewer's, destroying its monitored servers, encrypted passwords and tokens. " +
                "Name a different destination — the default (no argument) is a viewer-config subfolder beside it.");
            return 1;
        }

        /* A destination that is a junction/symlink is refused: creating one needs no privilege on Windows, so
           it is how an unprivileged local user redirects a cleartext credential into a directory they control. */
        if (Directory.Exists(targetDirectory)
            && new DirectoryInfo(targetDirectory).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            error.WriteLine(
                $"Refusing to export into {targetDirectory}: it is a junction or symbolic link, so the real " +
                "destination is somewhere else. Export to a real directory.");
            return 1;
        }

        /* Overwrite only what THIS verb wrote. Re-exporting after a credential or certificate rotation is a
           documented workflow, so an export of ours is replaced silently; anything else — an operator's own
           file, or one an attacker pre-created to keep ownership of (a Windows owner retains WRITE_DAC no
           matter what DACL the harden below applies) — stops the run. */
        if (File.Exists(exportedConfigPath))
        {
            string existing;
            try
            {
                existing = await File.ReadAllTextAsync(exportedConfigPath, cancellationToken);
            }
            catch (Exception ex)
            {
                error.WriteLine($"Could not read the existing {exportedConfigPath} to check whether it is ours: {ex.Message}");
                return 1;
            }

            if (!existing.Contains(ViewerConfigMarker, StringComparison.Ordinal))
            {
                error.WriteLine(
                    $"Refusing to overwrite {exportedConfigPath}: it was not written by --export-viewer-config. " +
                    "Export to an empty directory, or move that file aside first.");
                return 1;
            }
        }

        if (targetDirectory.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error.WriteLine(
                $"WARNING: {targetDirectory} is a network path. The exported {ViewerConfigFileName} holds a live " +
                "password, and the file ACL this verb applies locally does not necessarily hold on a remote share — " +
                "check the share's permissions yourself.");
        }

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

        var handoff = await ResolveViewerHandoffAsync(
            config, "--export-viewer-config", "export", error, cancellationToken);
        if (handoff is null)
        {
            return 1;
        }

        string certificate;
        if (!File.Exists(handoff.CertificatePath))
        {
            error.WriteLine(
                $"Cannot export: the server TLS certificate ({handoff.CertificatePath}) does not exist yet, and the " +
                "exported connection uses SSL Mode=VerifyFull — without the cert the viewer could not connect. The " +
                "service generates it on its first managed start with postgres.network exposed: set postgres.network " +
                "(listen + allowFrom), restart the service, then re-run this command.");
            return 1;
        }

        try
        {
            certificate = (await File.ReadAllTextAsync(handoff.CertificatePath, cancellationToken)).Trim();
        }
        catch (Exception ex)
        {
            error.WriteLine($"Could not read the server TLS certificate ({handoff.CertificatePath}): {ex.Message}");
            return 1;
        }

        var configFile = exportedConfigPath;
        var certificateFile = Path.Combine(targetDirectory, ViewerClientCertificateFileName);
        var readmeFile = Path.Combine(targetDirectory, ViewerReadmeFileName);

        /* The live-secret warning goes out BEFORE the write, naming the file — so an operator who walks away
           mid-run has still been told a credential-bearing file is landing there (#1953 item 3's ordering rule,
           applied to the verb that writes the secret rather than printing it). */
        error.WriteLine();
        error.WriteLine(
            $"WARNING: {configFile} will contain a LIVE database password (the '{handoff.Role}' role) in cleartext, " +
            "because that is what the viewer connects with. Copy the folder over a channel you trust, and keep the " +
            "file ACL'd to the operator account on the viewer machine.");

        /* One timestamp for the whole export: the two files date the same act, and sampling the clock twice
           can straddle a second boundary and print two. */
        var generatedUtc = DateTimeOffset.UtcNow;

        bool secretProtected;
        try
        {
            Directory.CreateDirectory(targetDirectory);

            /* Delete rather than truncate: writing over an existing file keeps its OWNER, and on Windows an
               owner keeps WRITE_DAC — so overwriting a file someone else created would hand them a DACL they
               can undo after the harden below. Recreating makes this process the owner. */
            File.Delete(configFile);
            await File.WriteAllTextAsync(
                configFile,
                BuildViewerConfigJson(
                    BuildViewerConnectionString(
                        handoff.Host, handoff.Port, handoff.Role, handoff.Password, ViewerClientCertificateFileName),
                    generatedUtc),
                cancellationToken);
            secretProtected = TryHardenExportedSecret(configFile, error);

            await File.WriteAllTextAsync(certificateFile, certificate + Environment.NewLine, cancellationToken);
            await File.WriteAllTextAsync(
                readmeFile,
                BuildViewerConfigReadme(handoff.Host, handoff.Port, handoff.Role, generatedUtc),
                cancellationToken);
        }
        catch (Exception ex)
        {
            error.WriteLine($"Could not write the viewer config to {targetDirectory}: {ex.Message}");

            /* A write that died part-way can leave a file holding the start of a live password. Remove it, and
               say so either way — "the export failed" must not leave an unmentioned secret on disk. */
            try
            {
                if (File.Exists(configFile))
                {
                    File.Delete(configFile);
                    error.WriteLine($"Removed the partially-written {configFile} (it would have held part of a live password).");
                }
            }
            catch (Exception cleanupFailure)
            {
                error.WriteLine(
                    $"WARNING: {configFile} was left behind and may hold part of a live password " +
                    $"({cleanupFailure.Message}). Delete it yourself.");
            }

            return 1;
        }

        if (string.Equals(handoff.Role, "admin", StringComparison.Ordinal))
        {
            error.WriteLine(
                "NOTE: 'admin' is a WRITE credential holding the config-table pivot surface. Prefer the default " +
                "'viewer' (read-only) for a remote seat; if you must use 'admin', NTFS-ACL the exported file on the " +
                "viewer machine too.");
        }

        error.WriteLine();
        error.WriteLine($"Copy the whole folder to the viewer machine, then point DARLING_CONFIG at its {ViewerConfigFileName}");
        error.WriteLine($"(or drop the files beside the Viewer executable). {ViewerReadmeFileName} documents every field.");
        error.WriteLine("Re-run this command after the store's certificate is regenerated (a changed bind IP rotates it).");
        error.WriteLine();

        output.WriteLine(targetDirectory);
        output.WriteLine(configFile);
        output.WriteLine(certificateFile);
        output.WriteLine(readmeFile);

        /* The files exist and work, but an unprotected cleartext credential is not a success an automation
           should read as one — exit non-zero so a script stops, with the manifest already printed so the
           operator can act on the exact file. */
        return secretProtected ? 0 : 2;
    }

    /// <summary>The exported viewer folder's file names — the JSON the Viewer resolves by name, the cert its
    /// <c>Root Certificate=</c> points at, and the operator-facing field reference (#1953 item 4).</summary>
    public const string ViewerConfigFileName = "darling.json";
    public const string ViewerReadmeFileName = "README.txt";

    /// <summary>
    /// The first comment line of an exported viewer config, and the way a re-export tells ITS OWN previous
    /// output apart from a file it must not clobber (an operator's real darling.json, or a pre-created one an
    /// attacker wants to keep ownership of). Re-exporting after a rotation is a documented workflow, so our
    /// own file is replaced silently; anything lacking this line stops the run.
    /// </summary>
    public const string ViewerConfigMarker = "PerformanceMonitor Darling - VIEWER configuration.";

    /// <summary>
    /// Where <see cref="ExportViewerConfigAsync"/> writes: the operator's directory when they named one,
    /// else a <c>viewer-config</c> folder beside the service's darling.json — next to the file the operator
    /// already knows, not buried under ProgramData. Pure (no I/O), so it pins directly.
    /// </summary>
    public static string ResolveViewerExportDirectory(string configPath, string? outputDirectory)
    {
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            return Path.GetFullPath(outputDirectory.Trim());
        }

        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
        return Path.Combine(
            string.IsNullOrEmpty(configDirectory) ? AppContext.BaseDirectory : configDirectory,
            "viewer-config");
    }

    /// <summary>
    /// The exported viewer <c>darling.json</c> (#1953 items 1 + 4): the two fields the Viewer actually reads —
    /// <c>managed: false</c> and the verbatim connection string — with every field, and the valid
    /// <c>Root Certificate=</c> values, documented in <c>//</c> comments IN the file. The Viewer parses with
    /// <c>JsonCommentHandling.Skip</c> + <c>AllowTrailingCommas</c> (the same tolerance darling.sample.json
    /// relies on), so the commented file is consumed as-is — <see cref="!:ViewerSettings"/> is pinned against
    /// this text by <c>DarlingExportViewerConfigTests</c>. Hand-composed rather than serialized precisely
    /// because <c>System.Text.Json</c> cannot WRITE comments, and the comments are the point.
    /// Pure — unit-testable.
    /// </summary>
    public static string BuildViewerConfigJson(string connectionString, DateTimeOffset generatedUtc) =>
        "{" + Environment.NewLine +
        "  // " + ViewerConfigMarker + Environment.NewLine +
        $"  // Generated by --export-viewer-config at {generatedUtc.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z. Nothing here needs hand-editing." + Environment.NewLine +
        "  //" + Environment.NewLine +
        "  // THIS FILE CONTAINS A LIVE DATABASE PASSWORD. Keep it ACL'd to the operator account." + Environment.NewLine +
        "  //" + Environment.NewLine +
        "  // Where it goes on the viewer machine (first match wins):" + Environment.NewLine +
        "  //   1. the path in the DARLING_CONFIG environment variable" + Environment.NewLine +
        "  //   2. darling.json beside the Viewer executable" + Environment.NewLine +
        "  //   3. darling.json one folder up (the release zip puts the Viewer in a viewer\\ subfolder)" + Environment.NewLine +
        "  // Keep server.crt in the SAME folder you launch the Viewer from (see Root Certificate below)." + Environment.NewLine +
        "  \"postgres\": {" + Environment.NewLine +
        "    // false = this machine does not RUN a store, it connects to one. The service host's own" + Environment.NewLine +
        "    // darling.json says true; that flag is about who OWNS the PostgreSQL, not who is connecting." + Environment.NewLine +
        "    // A viewer left on true would hunt for a bundled local PostgreSQL that is not there." + Environment.NewLine +
        "    \"managed\": false," + Environment.NewLine +
        Environment.NewLine +
        "    // Consumed VERBATIM by the Viewer (Npgsql keywords):" + Environment.NewLine +
        "    //   Host / Port      the service host's exposed store endpoint (its postgres.network.listen + postgres.port)" + Environment.NewLine +
        "    //   Username         the least-privilege store role: viewer = read-only, admin = read + config writes" + Environment.NewLine +
        "    //   Password         that role's live password (rotate by re-running the export after a rotation)" + Environment.NewLine +
        "    //   Database         always darling" + Environment.NewLine +
        "    //   Search Path      collect,config,public - resolves the bare table names the Viewer queries" + Environment.NewLine +
        "    //   SSL Mode         VerifyFull - encrypted AND the server certificate must match Host. Do not" + Environment.NewLine +
        "    //                    downgrade it to Require: that keeps the encryption but stops verifying the" + Environment.NewLine +
        "    //                    certificate, so Root Certificate below would be ignored entirely." + Environment.NewLine +
        "    //   Root Certificate the server.crt exported beside this file. Valid values:" + Environment.NewLine +
        "    //                      server.crt            a bare name, resolved against the Viewer's" + Environment.NewLine +
        "    //                                            WORKING DIRECTORY - correct when the Viewer is" + Environment.NewLine +
        "    //                                            launched from this folder, which a desktop" + Environment.NewLine +
        "    //                                            shortcut may not do" + Environment.NewLine +
        "    //                      C:\\Darling\\server.crt  an absolute path - always correct, and the fix if a" + Environment.NewLine +
        "    //                                            connection fails on the certificate" + Environment.NewLine +
        "    //                    It must be the PEM the SERVICE generated (exported beside this file);" + Environment.NewLine +
        "    //                    VerifyFull rejects any other certificate, including a re-issued one." + Environment.NewLine +
        $"    \"connectionString\": {JsonSerializer.Serialize(connectionString)}" + Environment.NewLine +
        "  }" + Environment.NewLine +
        "}" + Environment.NewLine;

    /// <summary>
    /// The exported <c>README.txt</c> (#1953 item 4): the same field reference as the JSON's comments, for an
    /// operator who never opens the JSON, plus the one-line "copy this folder" instruction. Takes no password
    /// — the credential lives in exactly one exported file and is never echoed anywhere else. Pure —
    /// unit-testable.
    /// </summary>
    public static string BuildViewerConfigReadme(string host, int port, string role, DateTimeOffset generatedUtc) =>
        "PerformanceMonitor Darling - remote Viewer setup" + Environment.NewLine +
        "================================================" + Environment.NewLine +
        Environment.NewLine +
        $"Generated by --export-viewer-config at {generatedUtc.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z on the service host." + Environment.NewLine +
        Environment.NewLine +
        "TO USE IT" + Environment.NewLine +
        "  Copy this folder to the viewer machine and set DARLING_CONFIG to the darling.json inside it" + Environment.NewLine +
        $"  (or copy the files next to the Viewer executable). Then start the Viewer - nothing to edit." + Environment.NewLine +
        Environment.NewLine +
        "WHAT IS IN HERE" + Environment.NewLine +
        $"  {ViewerConfigFileName}      the Viewer's configuration. CONTAINS A LIVE DATABASE PASSWORD - treat it as a secret," + Environment.NewLine +
        "                    ACL it to your account, and do not commit it or mail it around." + Environment.NewLine +
        $"  {ViewerClientCertificateFileName}        the store's TLS certificate. The connection pins THIS file; the Viewer will" + Environment.NewLine +
        "                    refuse any other certificate." + Environment.NewLine +
        $"  {ViewerReadmeFileName}        this file." + Environment.NewLine +
        Environment.NewLine +
        "THE FIELDS" + Environment.NewLine +
        "  managed = false   This machine does not RUN a store, it connects to one over the network. The" + Environment.NewLine +
        "                    SERVICE host's own darling.json says managed = true, which is about who OWNS" + Environment.NewLine +
        "                    the PostgreSQL, not who connects. A viewer left on true looks for a bundled" + Environment.NewLine +
        "                    local PostgreSQL that does not exist there and fails." + Environment.NewLine +
        "  connectionString  Handed to Npgsql verbatim. This export's values:" + Environment.NewLine +
        Environment.NewLine +
        $"      Host={host} / Port={port}" + Environment.NewLine +
        "          the exposed store endpoint on the service host." + Environment.NewLine +
        $"      Username={role}" + Environment.NewLine +
        "          the least-privilege store role: viewer = read-only, admin = read plus the" + Environment.NewLine +
        "          Viewer's config writes." + Environment.NewLine +
        "      Password=<that role's live password>" + Environment.NewLine +
        "          filled in for you; it is in darling.json and nowhere else, this file included." + Environment.NewLine +
        "      Database=darling" + Environment.NewLine +
        "          always." + Environment.NewLine +
        "      Search Path=collect,config,public" + Environment.NewLine +
        "          resolves the bare table names the Viewer queries." + Environment.NewLine +
        "      SSL Mode=VerifyFull" + Environment.NewLine +
        "          encrypted AND the server certificate must match Host. Do NOT downgrade this to" + Environment.NewLine +
        "          Require: the encryption stays, the verification stops, and Root Certificate is" + Environment.NewLine +
        "          then ignored entirely." + Environment.NewLine +
        $"      Root Certificate={ViewerClientCertificateFileName}" + Environment.NewLine +
        "          the certificate exported beside darling.json. Valid values:" + Environment.NewLine +
        "            server.crt              a bare name, resolved against the Viewer's" + Environment.NewLine +
        "                                    WORKING DIRECTORY. Correct when the Viewer is" + Environment.NewLine +
        "                                    launched from this folder; a desktop shortcut can" + Environment.NewLine +
        "                                    set a different working directory." + Environment.NewLine +
        "            C:\\Darling\\server.crt   an absolute path - always correct. Switch to this if" + Environment.NewLine +
        "                                    a connection fails on the certificate." + Environment.NewLine +
        "          It must be the PEM the SERVICE generated; VerifyFull rejects any other" + Environment.NewLine +
        "          certificate, including a re-issued one." + Environment.NewLine +
        Environment.NewLine +
        "IF IT DOES NOT CONNECT" + Environment.NewLine +
        "  - Certificate errors: make Root Certificate an absolute path to the server.crt in this folder." + Environment.NewLine +
        "  - The store's certificate is regenerated when its bind IP changes. Re-run --export-viewer-config" + Environment.NewLine +
        "    on the service host and replace this folder." + Environment.NewLine +
        $"  - The service host must be reachable on port {port} (its firewall rule is scoped to the allowed CIDR)," + Environment.NewLine +
        "    and its postgres.network block must still name this network." + Environment.NewLine;

    /// <summary>
    /// ACLs the exported darling.json down to SYSTEM + Administrators + this account + INTERACTIVE (the
    /// admin/viewer-credential posture — the Viewer reads it interactively), so it does not sit in a folder
    /// that inherited BUILTIN\Users read, then CONFIRMS the result. Returns whether the file is protected —
    /// "we tried" is not the same claim as "the secret is not readable", which is why every sibling harden
    /// site pairs the call with <see cref="DarlingFileSecurity.IsReadableByOrdinaryUsers"/> and why the
    /// caller's exit code depends on this answer. Never throws: the export's value is the file, so a failure
    /// is reported with the icacls that fixes it rather than throwing the folder away.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool TryHardenExportedSecret(string path, TextWriter error)
    {
        try
        {
            DarlingFileSecurity.HardenFile(path, allowInteractiveRead: true);
        }
        catch (Exception ex)
        {
            error.WriteLine(
                $"WARNING: could not restrict the ACL on {path} ({ex.Message}){DarlingFileSecurity.DescribeOwnerAndExposure(path)}. " +
                "It holds a LIVE password in cleartext. Delete it, or restrict it by hand before leaving this machine: " +
                $"icacls \"{path}\" /inheritance:r /grant \"{DarlingFileSecurity.ServiceAccountDisplayName}\":F");
            return false;
        }

        try
        {
            if (DarlingFileSecurity.IsReadableByOrdinaryUsers(path))
            {
                error.WriteLine(
                    $"WARNING: {path} is STILL readable by ordinary users after hardening" +
                    $"{DarlingFileSecurity.DescribeOwnerAndExposure(path)}. It holds a LIVE password in cleartext. " +
                    "Delete it, or restrict it by hand before leaving this machine: " +
                    $"icacls \"{path}\" /inheritance:r /grant \"{DarlingFileSecurity.ServiceAccountDisplayName}\":F");
                return false;
            }
        }
        catch (Exception ex)
        {
            error.WriteLine(
                $"WARNING: could not confirm the ACL on {path} ({ex.Message}). It holds a LIVE password in " +
                "cleartext — check its permissions before leaving this machine.");
            return false;
        }

        return true;
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

    /// <summary>Prints the handoff reminders: the scoped firewall command(s), the store's --export-viewer-config step, and the web dashboard's browser login hint.</summary>
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
            output.WriteLine("  After the service restarts (which generates the TLS cert), write the remote viewer's");
            output.WriteLine("  whole config folder — darling.json + server.crt + README — with:");
            output.WriteLine("    PerformanceMonitor.Darling.Service.exe --export-viewer-config");
            output.WriteLine("  (or --print-viewer-connection for just the paste-ready string + certificate).");
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
    /// Locks a freshly written config backup to SYSTEM, Administrators and the service account — and,
    /// unlike <c>darling.json</c> itself, NOT to <c>NT AUTHORITY\INTERACTIVE</c> (#1769).
    ///
    /// <para>A backup is a byte-for-byte copy of the config: every monitored server's
    /// <c>encryptedPassword</c>, the MCP bearer token, the web access token. Those are DPAPI
    /// <b>LocalMachine</b> blobs with an entropy constant that ships in an open-source repo, so READ access
    /// IS the secret. The live config grants INTERACTIVE read because things genuinely read it as the
    /// interactive operator — the Viewer (<c>ViewerSettings.TryLoad</c>) and the CLI verbs. <b>Nothing reads
    /// a backup.</b> The only references to the <c>.bak-</c> name in non-test code are the two lines above
    /// that CONSTRUCT it, and restoring one is a hand operation that already requires elevation, because
    /// writing <c>darling.json</c> does — INTERACTIVE only ever had Read here, never Write. So the grant
    /// bought nothing and cost a second copy of every secret, readable by any interactively-logged-on user.</para>
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
            DarlingFileSecurity.HardenFile(backupPath, allowInteractiveRead: false);
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
       account that CANNOT modify Windows Firewall, so the running service only VERIFIES its rules):
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
    /// idempotent-by-DisplayName rule name the host's start-up check looks for
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

    /* ================================================================================================
       --configure-firewall: the ELEVATED owner of every scoped Darling firewall rule (#1771).

       The service runs as an unprivileged virtual account and CANNOT create firewall rules. It used to try
       on every start anyway, failing "Access is denied" each time; on a fresh networked install — the normal
       deployment mode — that meant the rule was never created at all and remote clients were simply blocked.
       Rule management therefore belongs to the elevated context that already exists: install-darling.ps1
       calls this verb, uninstall-darling.ps1 removes the rules, and the running service only VERIFIES
       (DarlingFirewallCheck).

       It reconciles all THREE surfaces (store, MCP, web) in one pass, from darling.json alone: no store
       connection, no credentials, so it is safe to run at install time before the store has ever booted —
       unlike --enable-mcp/--enable-web, which write the control-plane store and therefore need it running.
       ================================================================================================ */

    /// <summary>What <c>--configure-firewall</c> will do to one surface's rule.</summary>
    public enum FirewallRuleAction
    {
        /// <summary>The config exposes this surface on the LAN — create/refresh the scoped allow rule.</summary>
        Open,

        /// <summary>The surface is loopback-only (by config, or fail-closed by its own resolver) — the desired
        /// state is NO rule. Removal is idempotent, so this is also the no-op for a rule that never existed.</summary>
        Remove,
    }

    /// <summary>One surface's desired firewall state. <paramref name="Note"/> is a human explanation printed
    /// alongside, non-null only where the reason is not self-evident (a fail-closed exposure).</summary>
    public readonly record struct FirewallRulePlan(
        string Surface, string RuleName, int Port, FirewallRuleAction Action, string? Cidr, string? Note);

    /// <summary>
    /// PURE desired-state for all three rules, so the whole decision pins without a live firewall.
    /// <para>Every surface is resolved by the SAME resolver the RUNNING service fail-closes on
    /// (<see cref="DarlingManagedPostgres.ResolveNetworkExposure"/>,
    /// <see cref="DarlingMcpHostService.ResolveMcpBind"/>, <see cref="DarlingWebHostService.ResolveWebBind"/>)
    /// rather than by re-reading <c>listen</c>/<c>allowFrom</c> here. That is the load-bearing choice: a config
    /// the service degrades to loopback (an unparseable listen, a mismatched address family, a missing CIDR)
    /// must NOT get an open port, and the service's own start-up check must reach the same verdict this did —
    /// otherwise every start would report a rule this verb had just deliberately created.</para>
    /// <para>BYO mode resolves loopback for MCP/web (their resolvers take <c>managed</c> and refuse exposure
    /// without it) and skips the store entirely, whose exposure the operator's own PostgreSQL governs.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<FirewallRulePlan> PlanFirewallRules(DarlingConfig config)
    {
        var plans = new List<FirewallRulePlan>();
        var managed = config.Postgres?.Managed ?? false;

        if (managed && config.Postgres is not null)
        {
            var (certPath, keyPath) = ResolveStoreCertPaths(config.Postgres);
            var store = DarlingManagedPostgres.ResolveNetworkExposure(config.Postgres.Network, certPath, keyPath);
            plans.Add(new FirewallRulePlan(
                "store",
                DarlingManagedPostgres.StoreFirewallRuleName(config.Postgres.Port),
                config.Postgres.Port,
                store.Exposed ? FirewallRuleAction.Open : FirewallRuleAction.Remove,
                store.Exposed ? store.Cidr : null,
                store.DegradeReason));
        }

        var mcpBind = DarlingMcpHostService.ResolveMcpBind(config.Mcp, managed);
        var mcpExposed = mcpBind.Mode == DarlingMcpHostService.McpBindMode.NetworkAndLoopback;
        plans.Add(new FirewallRulePlan(
            "MCP",
            DarlingMcpHostService.McpFirewallRuleName(config.Mcp.Port),
            config.Mcp.Port,
            mcpExposed ? FirewallRuleAction.Open : FirewallRuleAction.Remove,
            mcpExposed ? CanonicalCidrOrNull(config.Mcp.Network?.AllowFrom) : null,
            mcpExposed ? null : DescribeLoopbackReason(mcpBind.Reason, "mcp")));

        var webBind = DarlingWebHostService.ResolveWebBind(config.Web, managed);
        var webExposed = webBind.Mode == DarlingHostBinding.BindMode.NetworkAndLoopback;
        plans.Add(new FirewallRulePlan(
            "web dashboard",
            DarlingWebHostService.WebFirewallRuleName(config.Web.Port),
            config.Web.Port,
            webExposed ? FirewallRuleAction.Open : FirewallRuleAction.Remove,
            webExposed ? CanonicalCidrOrNull(config.Web.Network?.AllowFrom) : null,
            webExposed ? null : DescribeLoopbackReason((DarlingMcpHostService.McpBindReason)webBind.Reason, "web")));

        return plans;
    }

    /// <summary>The parser's canonical CIDR, or null when it will not parse. The bind resolvers have already
    /// refused exposure in the null case, so an Open plan always carries a real CIDR; this never forwards the
    /// caller's raw string into a PowerShell command (#1646).</summary>
    private static string? CanonicalCidrOrNull(string? allowFrom) =>
        ClassifyAllowFrom(allowFrom, out var canonical) == EndpointAllowFromVerdict.Valid ? canonical : null;

    /// <summary>Why a surface is loopback-only, when the reason is a DEGRADE worth printing. A plain
    /// loopback-by-default config is the normal case and gets no note.</summary>
    private static string? DescribeLoopbackReason(DarlingMcpHostService.McpBindReason reason, string section) =>
        reason switch
        {
            DarlingMcpHostService.McpBindReason.TokenMissing =>
                $"{section}.network is set but its token is missing or unreadable, so the service fail-closes this endpoint to loopback",
            DarlingMcpHostService.McpBindReason.AllowFromInvalid =>
                $"{section}.network.allowFrom is missing or not a valid CIDR, so the service fail-closes this endpoint to loopback",
            DarlingMcpHostService.McpBindReason.ManagedModeRequired =>
                $"{section}.network is set but postgres.managed = false; LAN exposure is managed-mode only and is ignored",
            _ => null,
        };

    /// <summary>
    /// Creates or removes every scoped Darling firewall rule so the live firewall matches darling.json.
    /// Requires elevation (that is the entire point of the verb) and is idempotent — safe to re-run on every
    /// upgrade, which is exactly how install-darling.ps1 uses it. Returns 0 when the firewall ends up matching
    /// the config, 1 when it could not be made to.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<int> ConfigureFirewallAsync(
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

        var plans = PlanFirewallRules(config);
        var toOpen = plans.Count(p => p.Action == FirewallRuleAction.Open);

        output.WriteLine("Reconciling the scoped Windows Firewall rules to match darling.json.");
        output.WriteLine(
            "These rules are managed HERE, elevated. The service account cannot create them by design, so the " +
            "running service only verifies them and reports what it finds.");
        output.WriteLine();

        if (!IsElevated())
        {
            /* Not elevated. When nothing is exposed there is genuinely nothing to do and a hard failure would
               be a lie (and would fail an otherwise fine loopback install); when something IS exposed this is a
               real, actionable failure, so exit non-zero AND print every command to run by hand. */
            if (toOpen == 0)
            {
                output.WriteLine(
                    "Every endpoint is loopback-only, so no firewall rule is needed and none was changed. " +
                    "(This shell is not elevated, but there was nothing to do.)");
                return 0;
            }

            error.WriteLine("This shell is not elevated, so NO firewall rule was changed. Run these in an ELEVATED PowerShell:");
            foreach (var plan in plans.Where(p => p.Action == FirewallRuleAction.Open))
            {
                error.WriteLine("  " + DarlingManagedPostgres.BuildFirewallEnableCommand(plan.RuleName, plan.Port, plan.Cidr!));
            }

            return 1;
        }

        var failures = 0;
        foreach (var plan in plans)
        {
            if (plan.Note is not null)
            {
                output.WriteLine($"{plan.Surface}: {plan.Note}.");
            }

            /* Sweep this surface's rules for EVERY port FIRST, as its own step. The port is part of the rule
               name, so changing a port does not update a rule — it makes a different one and strands the old as
               an inbound allow rule on a port nothing serves. Reconciling by exact name could never reach that.
               Its own step, not concatenated ahead of the open below, because the sweep command ends in exit 0
               and would otherwise terminate the shell before the rule was created. */
            var wildcard = DarlingFirewallCheck.SurfaceRuleWildcard(plan.RuleName);
            if (!await TryRunFirewallStepAsync(
                DarlingManagedPostgres.BuildFirewallSweepCommand(wildcard),
                plan.Action == FirewallRuleAction.Remove
                    ? $"{plan.Surface}: loopback-only — no rule needed (removed any '{wildcard}')."
                    : $"{plan.Surface}: cleared any previous rule matching '{wildcard}'.",
                $"{plan.Surface}: could not remove the rule(s) matching '{wildcard}'",
                output, error, cancellationToken))
            {
                failures++;
            }

            if (plan.Action == FirewallRuleAction.Remove)
            {
                /* Loopback-only: the desired state is no rule at all, on any port — the sweep WAS the work. */
                continue;
            }

            if (!await TryRunFirewallStepAsync(
                DarlingManagedPostgres.BuildFirewallEnableCommand(plan.RuleName, plan.Port, plan.Cidr!),
                $"{plan.Surface}: opened '{plan.RuleName}' (TCP {plan.Port}, inbound, from {plan.Cidr}).",
                $"{plan.Surface}: could not open the rule '{plan.RuleName}'",
                output, error, cancellationToken))
            {
                failures++;
            }
        }

        output.WriteLine();
        if (failures > 0)
        {
            error.WriteLine($"{failures} firewall rule(s) could not be reconciled — see the commands above.");
            return 1;
        }

        output.WriteLine(toOpen == 0
            ? "Done. Every endpoint is loopback-only, so no port was opened."
            : $"Done. {toOpen} endpoint(s) exposed on the LAN; their scoped rules are in place.");
        return 0;
    }

    /// <summary>Runs one reconcile step and reports it. Returns false on failure, after printing the exact
    /// command so an operator can finish by hand. Never throws except on cancellation.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task<bool> TryRunFirewallStepAsync(
        string command, string successLine, string failurePrefix, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, psOutput) = await DarlingManagedPostgres.RunPowerShellAsync(command, cancellationToken);
            if (exitCode == 0)
            {
                output.WriteLine(successLine);
                return true;
            }

            error.WriteLine($"{failurePrefix} (exit {exitCode}: {psOutput}). Run this by hand:");
            error.WriteLine("  " + command);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error.WriteLine($"{failurePrefix} ({ex.Message}). Run this by hand:");
            error.WriteLine("  " + command);
            return false;
        }
    }

    /* ================================================================================================
       --backfill-rollups: the staged, disk-preflighted rollup backfill (#1759 Phase 2).

       Why an operator verb and not a startup step: the #1680 arming gate is ALL-OR-NOTHING, so a store
       with a year of raw has to materialize the WHOLE history before the first purge arms and reclaims
       anything. Peak disk comes BEFORE any relief. Running that automatically at service start, on the
       exact stores worst affected (one already down to ~150 GB free), is a plausible disk-exhaustion
       event -- so it is explicit, preflighted, and refuses with numbers rather than filling the volume.

       This verb's job ENDS AT COVERAGE. It never arms a retention policy: the arming gate already
       self-heals, checking coverage on every service start and releasing the held purges by itself once
       a rollup genuinely covers raw. Arming here would duplicate that decision in a second place, and
       the whole reason nothing has been lost on these stores is that exactly one thing decides it.
       ================================================================================================ */

    /// <summary>
    /// <c>--collapse-legacy-slices</c> (#1912): repair the Query Store rows collected before #1907, then
    /// re-materialize the rollups they fed.
    ///
    /// <para>Before #1907 the collector stored Query Store's FLUSHED and still-IN-MEMORY slice of one interval
    /// as two rows. They are ADDITIVE, so the read-side dedup — which keeps one row per key — reports a
    /// fraction of the interval's work, and the rollups materialized that fraction. #1907 made the choice
    /// deterministic; this makes it correct, for the rows it can still reach.</para>
    ///
    /// <para><b>Bounded on purpose, and the bound is raw retention.</b> Only rows still in the raw tier can be
    /// collapsed, so on a Darling store the repair reaches the last few days — which at upgrade time is the
    /// operator's most-looked-at recent history, and is why running it PROMPTLY after upgrading is what makes
    /// it worth anything. Everything older keeps its understated counts permanently: the daily tiers are kept
    /// indefinitely and cannot be rebuilt from raw that no longer exists. That residue is disclosed in the
    /// release notes rather than papered over, the same shape #1849 used for the inflated-rollup boundary.</para>
    ///
    /// <para><b>The refresh is CLAMPED to the rows actually collapsed, and that is a safety property rather
    /// than an optimization.</b> A refresh whose range lies entirely within DROPPED raw chunks DESTROYS the
    /// materialization there — with force and without, measured on PG 18.4 + TimescaleDB 2.28.1 and pinned by
    /// <c>QueryStoreSliceRepairLiveTests</c>. Aiming at a nominal "pre-fix period" instead of at the collapsed
    /// rows' own span would therefore blank the retained hourly tier and the indefinitely-kept daily below raw's
    /// floor, which is precisely the history #1759/#1793 forbid destroying. Collapsed rows are inside raw's
    /// extent by definition, so deriving the window from them cannot reach under it.</para>
    ///
    /// <para><b>Safe to re-run.</b> The pre-fix signature — two or more rows sharing the whole dedup key AND
    /// <c>collection_time</c> — cannot match a row collected since #1907, which emits at most one row per
    /// interval per cycle. A second run finds nothing and says so.</para>
    ///
    /// <para>Returns 0 when the repair completed or had nothing to do, 1 on a load/mode/credential error.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<int> CollapseLegacySlicesAsync(
        string? configPath, bool dryRun, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

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

        var connectionString = postgres.Managed
            ? DarlingManagedPostgres.TryBuildConnectionStringFromStoredCredential(postgres)
            : postgres.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            error.WriteLine(postgres.Managed
                ? "The managed store credential does not exist yet — start the PerformanceMonitor Darling service once so its first run initializes the store, then re-run this command."
                : "postgres.connectionString is empty, so there is no store to repair.");
            return 1;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error.WriteLine($"Could not connect to the store: {ex.Message}");
            return 1;
        }

        output.WriteLine();
        output.WriteLine("PerformanceMonitor Darling — Query Store slice repair (--collapse-legacy-slices)");
        output.WriteLine();

        QueryStoreSliceRepair.Survey survey;
        try
        {
            survey = await QueryStoreSliceRepair.SurveyAsync(connection, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error.WriteLine($"Could not survey query_store_stats: {ex.Message}");
            return 1;
        }

        if (!survey.HasWork)
        {
            output.WriteLine("  Nothing to repair — no Query Store rows carry the pre-fix split-slice signature.");
            output.WriteLine();
            output.WriteLine("  That is the expected result on a store that has only ever run builds carrying the");
            output.WriteLine("  collection-side fix, and on any store where a previous run already repaired them.");
            return 0;
        }

        output.WriteLine($"  Split intervals found : {survey.SplitGroups:N0}");
        output.WriteLine($"  Rows involved         : {survey.SplitRows:N0}  (collapsing to {survey.SplitGroups:N0}, removing {survey.RowsRemoved:N0})");
        output.WriteLine($"  Collection-time span  : {survey.OldestUtc:yyyy-MM-dd HH:mm} .. {survey.NewestUtc:yyyy-MM-dd HH:mm} UTC");
        output.WriteLine();
        output.WriteLine("  Only rows still held in the raw tier can be repaired. Query Store history older than");
        output.WriteLine("  raw retention keeps its understated counts permanently — see the release notes.");
        output.WriteLine();

        if (dryRun)
        {
            output.WriteLine("  DRY RUN — nothing was changed.");
            return 0;
        }

        /* SLICED PER DAY, not one call over the whole span. CollapseSliceAsync runs each slice in ONE
           transaction, and that transaction takes locks on the raw chunks it touches — which the compression
           policy also wants. Handing it the entire survey span would make one long transaction sitting across
           however much history the store keeps, which is exactly the lock-duration family that has bitten this
           repo before (#1564/#1567). On a default 4-day raw tier this is a handful of slices; on a store with
           a widened retention it is the protection the method's own doc promises.

           The half-open upper bound includes the newest collapsed row — the survey reports that instant
           itself, not a bound past it — hence the final slice's one-second nudge. */
        long removed = 0;
        var sliceStart = survey.OldestUtc!.Value.Date;
        var collapseEnd = survey.NewestUtc!.Value.AddSeconds(1);

        try
        {
            while (sliceStart < collapseEnd)
            {
                var sliceEnd = sliceStart.AddDays(1);
                if (sliceEnd > collapseEnd)
                {
                    sliceEnd = collapseEnd;
                }

                removed += await QueryStoreSliceRepair.CollapseSliceAsync(
                    connection, sliceStart, sliceEnd, cancellationToken);

                sliceStart = sliceEnd;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* Each slice is its own transaction, so earlier slices are already committed and are not lost —
               and the collapse is idempotent, so re-running picks up where this stopped. */
            error.WriteLine($"  The collapse failed after {removed:N0} row(s); the failing slice was rolled back: {ex.Message}");
            error.WriteLine("  Slices already committed are safe. Re-run to continue — the repair is idempotent.");
            return 1;
        }

        output.WriteLine($"  Collapsed. Rows removed: {removed:N0}");
        output.WriteLine();

        /* Re-materialize every rollup fed by query_store_stats, in the dependency order RollupBackfill.Targets
           already carries — a rollup refreshed before its source materializes nothing, reports success, and
           consumes the invalidations that covered the range. The window is the collapsed rows' own span,
           widened to whole buckets so a partially-covered bucket is recomputed rather than left half-old. */
        var affected = RollupBackfill.Targets
            .Where(t => string.Equals(t.RawTable, QueryStoreSliceRepair.Table, StringComparison.Ordinal))
            .ToList();

        /* ONE disclosure for the whole run, so a pre-2.21 store's missing `options` parameter is reported once
           rather than per rollup — and reported at all, which is the point of it being required (#1797). */
        var disclosure = new RefreshDisclosure(message => output.WriteLine($"  [NOTE] {message}"));

        var refreshed = 0;
        foreach (var target in affected)
        {
            var from = Floor(survey.OldestUtc!.Value, target.BucketWidth);
            var to = Floor(survey.NewestUtc!.Value, target.BucketWidth) + target.BucketWidth;

            try
            {
                await RollupBackfill.RepairAsync(connection, target.View, from, to, disclosure, cancellationToken);
                refreshed++;
                output.WriteLine($"  [OK]   {target.View}: re-materialized {from:yyyy-MM-dd HH:mm} .. {to:yyyy-MM-dd HH:mm} UTC");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error.WriteLine($"  [FAIL] {target.View}: {ex.Message}");
            }
        }

        output.WriteLine();
        output.WriteLine(refreshed == affected.Count
            ? $"  DONE — {removed:N0} row(s) collapsed, {refreshed} rollup(s) re-materialized."
            : $"  PARTIAL — {removed:N0} row(s) collapsed, {refreshed} of {affected.Count} rollup(s) re-materialized. The collapse itself is committed; re-run to retry the rollups.");

        return 0;
    }

    /// <summary>Floors an instant to its bucket, so a refresh window covers whole buckets on both edges.</summary>
    private static DateTime Floor(DateTime value, TimeSpan bucket)
        => bucket <= TimeSpan.Zero ? value : new DateTime(value.Ticks - (value.Ticks % bucket.Ticks), value.Kind);

    /// <summary>
    /// Materializes the query-acceleration rollups back over pre-existing history so the held raw retention
    /// policies can arm themselves (#1759 Phase 2). Runs while the service is UP.
    ///
    /// <para><b>Concurrency.</b> <c>refresh_continuous_aggregate</c> takes no lock that blocks writers on the
    /// source hypertable, so collection keeps running throughout; readers are likewise unaffected (Phase 1 has
    /// them on raw for these windows anyway, and a window whose coverage a slice has just filled starts
    /// resolving to the rollup at the next probe). What the refresh DOES contend with is the compression policy
    /// on the same chunks, which is why slices are one chunk wide — a short window cannot sit across a
    /// compression job long enough to deadlock (the #1778 watch). It cannot run inside a transaction
    /// (<c>PreventInTransactionBlock</c>), so each slice is its own statement and partial progress survives an
    /// abort.
    ///
    /// <para><b>Resumable and idempotent.</b> Every pass re-plans from the MEASURED coverage floor, so an
    /// interrupted run continues where it stopped and a completed one converges to a no-op.</para>
    ///
    /// <para>Returns 0 when every rollup reached coverage (or already had it), 1 on a load/mode/credential
    /// error, on a preflight refusal, or when any rollup finished short of raw.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<int> BackfillRollupsAsync(
        string? configPath, bool dryRun, TextWriter output, TextWriter error, CancellationToken cancellationToken)
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

        /* Managed reads the service's own DPAPI-protected owner credential; bring-your-own uses the operator's
           configured string. Both are supported — this is a STORE operation, not a Windows one. */
        var connectionString = postgres.Managed
            ? DarlingManagedPostgres.TryBuildConnectionStringFromStoredCredential(postgres)
            : postgres.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            error.WriteLine(postgres.Managed
                ? "The managed store credential does not exist yet — start the PerformanceMonitor Darling service once so its first run initializes the store, then re-run this command."
                : "postgres.connectionString is empty, so there is no store to back fill.");
            return 1;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error.WriteLine($"Could not connect to the store: {ex.Message}");
            return 1;
        }

        output.WriteLine();
        output.WriteLine("PerformanceMonitor Darling — rollup backfill (--backfill-rollups)");
        output.WriteLine();

        /* Plan every rollup. The list is in DEPENDENCY ORDER (hourlies, then the dailies sourced from them),
           and the run loop below preserves it — a daily refreshed over a range its hourly has not materialized
           reads an empty source, materializes nothing, reports success, and CONSUMES the invalidations that
           covered the range, so a later correct-order pass no-ops over the hole. */
        var plans = new List<(RollupBackfillTarget Target, RollupBackfillPlan Plan)>();
        var refusedPlans = new List<string>();
        var rawBytesCache = new Dictionary<string, long>(StringComparer.Ordinal);
        var rawOldestCache = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        foreach (var target in RollupBackfill.Targets)
        {
            RollupBackfillProbe probe;
            try
            {
                probe = await RollupBackfill.ProbeAsync(connection, target.View, target.Source, target.SourceTimeColumn, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                /* A missing rollup (a partially-built store) or a store without TimescaleDB lands here. Report
                   and carry on: the rollups are independent, and an operator who cannot fix one should not be
                   blocked from backfilling the rest. */
                error.WriteLine($"  [SKIP] {target.View}: cannot be probed ({ex.Message}).");
                continue;
            }

            /* The UNCALIBRATED estimate stays bounded by RAW's size even for a hierarchical daily, whose
               convergence target is now its hourly. Raw is the larger relation, so it is the conservative
               bound, and this branch only runs when the rollup has materialized nothing to measure — where
               erring high is the whole point (#1759). Deliberately not switched to the source with the rest of
               #1798: sizing a CAGG source would need its materialization hypertable, which buys accuracy in
               precisely the case where accuracy is not what is wanted. */
            if (!rawBytesCache.TryGetValue(target.RawTable, out var rawBytes))
            {
                rawBytes = await RollupBackfill.RawBytesAsync(connection, target.RawTable, cancellationToken);
                rawBytesCache[target.RawTable] = rawBytes;
            }

            /* BOUND THE PREFLIGHT AGAINST WHERE THE SOURCE WILL END UP, not where it starts now. A daily's
               source is an hourly that this same run deepens FIRST, so estimating from the pre-backfill floor
               under-counts precisely the region the run then writes — and the printed number is the commitment
               the operator accepted. Same invariant as the rows-per-bucket under-estimate, and it bites on the
               store shape where disk is tightest. A no-op for the hourlies, whose source IS raw. */
            if (!rawOldestCache.TryGetValue(target.RawTable, out var rootRawOldest))
            {
                rootRawOldest = await RollupBackfill.OldestInstantAsync(connection, target.RawTable, "collection_time", cancellationToken);
                rawOldestCache[target.RawTable] = rootRawOldest;
            }

            plans.Add((target, RollupBackfill.Plan(
                target.View, RollupBackfill.EventualSourceFloor(probe.SourceOldestUtc, rootRawOldest), probe.CoverageOldestUtc,
                probe.MaterializedBuckets, probe.MaterializedBytes, rawBytes,
                target.BucketWidth)));
        }

        var work = plans.Where(p => !p.Plan.IsComplete).ToList();
        foreach (var (_, done) in plans.Where(p => p.Plan.IsComplete))
        {
            /* A refusal is NOT a skip. "Nothing to do" is a success an operator can scroll past; a plan the
               store's own data makes nonsense of names a row someone has to go and look at, and printing it as
               an [OK] line would bury it. */
            if (done.Refusal is string refusal)
            {
                error.WriteLine($"  [REFUSED] {done.View}: {refusal}");
                refusedPlans.Add($"{done.View}: {refusal}");
            }
            else
            {
                output.WriteLine($"  [OK]   {done.View}: nothing to do — {done.SkipReason}.");
            }
        }

        if (work.Count == 0)
        {
            output.WriteLine();
            if (refusedPlans.Count > 0)
            {
                /* Never "all covered, you are done" when a rollup was refused: that rollup is NOT covered, its
                   retention policy will stay held, and saying otherwise sends the operator away from a corrupt
                   row they need to fix. */
                error.WriteLine("REFUSED. Nothing was materialized for these rollups, and their retention policies stay held:");
                foreach (var refusal in refusedPlans)
                {
                    error.WriteLine("  - " + refusal);
                }

                return 1;
            }

            output.WriteLine("Every rollup already covers its own source. Any retention policy still held will arm itself on the next service start.");
            return 0;
        }

        var totalEstimate = work.Sum(w => w.Plan.EstimatedBytes);
        var anyUncalibrated = work.Exists(w => !w.Plan.Calibrated);

        output.WriteLine("Plan:");
        foreach (var (_, plan) in work)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {plan.View}: materialize {plan.FromUtc:yyyy-MM-dd} -> {plan.ToUtc:yyyy-MM-dd} ({plan.BucketsToAdd:N0} buckets in {plan.Slices:N0} slices), estimated {plan.EstimatedSize}")
                + (plan.Calibrated ? "" : " (UNCALIBRATED upper bound — this rollup has materialized nothing to measure)"));
        }

        output.WriteLine();
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Total estimated materialization: {RollupBackfillPlan.FormatBytes(totalEstimate)}; expected duration about {DescribeDuration(RollupBackfill.EstimatedDuration(totalEstimate))} at the ~16 MB/s throughput measured on this host class."));

        if (anyUncalibrated)
        {
            output.WriteLine(
                "NOTE: at least one estimate is an UPPER BOUND, not a measurement — that rollup has materialized " +
                "nothing this could be calibrated against. It is deliberately generous: refusing a backfill that " +
                "would have fit is recoverable, filling the volume is not.");
        }

        var (freeBytes, freeError) = await ResolveStoreFreeSpaceAsync(connection, cancellationToken);
        if (freeError is not null)
        {
            error.WriteLine();
            error.WriteLine("REFUSING: " + freeError);
            error.WriteLine(
                "This backfill materializes history BEFORE any purge can reclaim anything, so running it without " +
                "knowing the free space is exactly the disk-exhaustion risk it exists to avoid.");
            return 1;
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Free space on the store volume: {RollupBackfillPlan.FormatBytes(freeBytes)}; required: {RollupBackfillPlan.FormatBytes(RollupBackfill.RequiredBytes(totalEstimate))}."));

        if (!RollupBackfill.HasRoom(totalEstimate, freeBytes))
        {
            error.WriteLine();
            error.WriteLine(FormatDiskRefusal(totalEstimate, freeBytes));
            return 1;
        }

        if (dryRun)
        {
            output.WriteLine();
            output.WriteLine("--dry-run: nothing was materialized. Re-run without --dry-run to proceed.");
            return 0;
        }

        output.WriteLine();
        /* Slices run NEWEST-FIRST (see RollupBackfill.Slices), so coverage extends DOWNWARD toward raw's oldest
           row and the measured floor is a truthful progress cursor rather than a value the first slice pins.
           That is what makes this claim safe to make: an interrupted run reports SHORT, not DONE. */
        output.WriteLine("Backfilling, newest-first, so coverage extends downward toward the oldest raw row.");
        output.WriteLine("Safe to interrupt: every completed slice is committed, an interrupted run reports SHORT rather");
        output.WriteLine("than claiming success, and re-running resumes from the measured floor.");

        /* THE DEGRADE HAS TO REACH THE OPERATOR. RefreshDisclosure carries both the run's options capability
           (latched once, so a pre-2.21 store pays one failed call rather than one per slice) and the sink that
           says so. It is a REQUIRED argument precisely because the first cut made it a trailing optional and
           both call sites silently omitted it — degrade-and-be-silent, while the call-site doc claimed the
           contract was held. Routed to stderr alongside the REFUSED/SHORT lines. */
        var disclosure = new RefreshDisclosure(message => error.WriteLine("  NOTE: " + message));

        var shortfalls = new List<string>();
        foreach (var (target, plannedUpFront) in work)
        {
            output.WriteLine();
            output.WriteLine($"  {plannedUpFront.View}:");

            /* RE-PLAN FROM LIVE MEASUREMENTS, because a hierarchical daily's target MOVES during this run.
               The plans above were all taken before any slice ran — necessary, since the disk preflight has to
               total the whole job before committing to any of it — but a daily converges to its HOURLY, and
               that hourly is backfilled EARLIER IN THIS SAME LOOP. Planning a daily from the pre-backfill
               snapshot aims it at where its source used to start, so it does almost nothing and is then judged
               against where its source now starts: SHORT on every daily, every time. Caught by the verb's own
               end-to-end test going red on exactly that.

               Re-planning here is the same measured-floor idiom that makes resume work, applied one level up:
               the up-front pass sizes the job, this one aims it. */
            RollupBackfillPlan plan;
            try
            {
                var live = await RollupBackfill.ProbeAsync(connection, target.View, target.Source, target.SourceTimeColumn, cancellationToken);
                plan = RollupBackfill.Plan(
                    target.View, live.SourceOldestUtc, live.CoverageOldestUtc,
                    live.MaterializedBuckets, live.MaterializedBytes,
                    rawBytesCache.TryGetValue(target.RawTable, out var cachedRawBytes) ? cachedRawBytes : 0,
                    target.BucketWidth);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error.WriteLine($"    FAILED to re-probe before running: {ex.Message}");
                shortfalls.Add($"{plannedUpFront.View} could not be re-probed before its slices ran");
                continue;
            }

            if (plan.Refusal is string replanRefusal)
            {
                /* A REFUSAL IS NOT A COMPLETION, and this is a GUARD rather than an assertion that the case
                   cannot arise. RollupBackfillPlan.Absurd sets IsComplete as well as Refusal, so testing
                   IsComplete first — as an earlier cut did — printed "already covers" over a refusal and
                   counted it as success.

                   On today's code a refused re-plan is close to unreachable: a corrupt source timestamp makes
                   the HOURLY's up-front plan refuse on the same row, and since a daily is now planned against
                   its source's eventual floor, the daily refuses up front too — so neither reaches this loop.
                   But "close to unreachable" is an argument about the current call graph, not a property, and
                   the failure it protects against is slicing a window the planner already rejected as garbage.
                   The guard costs three lines; the argument would have to be re-derived by every future
                   reader. */
                error.WriteLine($"    [REFUSED] {target.View}: {replanRefusal}");
                shortfalls.Add($"{target.View}: {replanRefusal}");
                continue;
            }

            if (plan.IsComplete)
            {
                /* Its source moved under it and it is already covered — normal for a daily whose hourly
                   turned out to reach no further back than the daily already did. */
                output.WriteLine($"    already covers {target.Source} — nothing to do.");
                continue;
            }

            var completed = 0L;
            var sliceFailed = false;
            foreach (var (from, to) in RollupBackfill.Slices(plan.FromUtc, plan.ToUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var floor = await RollupBackfill.RunSliceAsync(connection, plan.View, from, to, disclosure, cancellationToken);
                    completed++;
                    output.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"    [{completed:N0}/{plan.Slices:N0}] {from:yyyy-MM-dd} -> {to:yyyy-MM-dd}; coverage now starts {floor:yyyy-MM-dd HH:mm}"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error.WriteLine($"    FAILED at {from:yyyy-MM-dd}: {ex.Message}");
                    sliceFailed = true;
                    break;
                }
            }

            /* CONVERGENCE IS MEASURED, NEVER INFERRED. A refresh that stops on its internal batch cap logs
               server-side and returns success to the client, so "the calls did not throw" is no evidence that
               the range was materialized. The only honest check is to re-read the floor from DATA and compare
               it to the raw row it had to reach. */
            var finalFloor = await RollupBackfill.ReadCoverageFloorAsync(connection, plan.View, cancellationToken);
            var after = await RollupBackfill.ProbeAsync(connection, plan.View, target.Source, target.SourceTimeColumn, cancellationToken);
            var reached = finalFloor is not null && after.SourceOldestUtc is not null && finalFloor <= after.SourceOldestUtc;

            /* Short WITHOUT a slice having failed means the refreshes reported success over a range they did
               not materialize — the signature of an earlier pass cut short, whose invalidation records a plain
               refresh will now skip straight over. That is the one case the forced form repairs, and the only
               case it is worth paying for. */
            if (!reached && !sliceFailed && after.SourceOldestUtc is not null)
            {
                output.WriteLine($"    {plan.View} is still short after every slice; escalating to a forced refresh over the range.");
                try
                {
                    finalFloor = await RollupBackfill.RepairAsync(connection, plan.View, plan.FromUtc, plan.ToUtc, disclosure, cancellationToken);
                    reached = finalFloor is not null && finalFloor <= after.SourceOldestUtc;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    /* force arrived in TimescaleDB 2.18 — an older bring-your-own store raises 42883 here.
                       Reported, not fatal: the shortfall is already going to be reported below. */
                    error.WriteLine($"    forced refresh unavailable on this store ({ex.Message}).");
                }
            }

            if (reached)
            {
                output.WriteLine($"    COVERED: {plan.View} now starts at {finalFloor:yyyy-MM-dd HH:mm}, at or before {target.Source}'s oldest row ({after.SourceOldestUtc:yyyy-MM-dd HH:mm}) — which is what the arming gate measures for this tier.");
            }
            else
            {
                shortfalls.Add($"{plan.View} reaches back to {finalFloor:yyyy-MM-dd HH:mm} but its source {target.Source} reaches back to {after.SourceOldestUtc:yyyy-MM-dd HH:mm}");
                error.WriteLine(
                    $"    SHORT: {plan.View} did not reach its source {target.Source}'s oldest row" +
                    (sliceFailed
                        ? " (a slice failed above)."
                        : " even though every slice reported success — a refresh that stops on its internal batch cap is silent to the client. Re-run to continue."));
            }
        }

        output.WriteLine();
        /* A refusal from the planning pass counts as a shortfall here too: those rollups were never attempted,
           so DONE would be a lie about them even when every rollup that DID run reached coverage. */
        shortfalls.AddRange(refusedPlans);

        if (shortfalls.Count > 0)
        {
            error.WriteLine("INCOMPLETE. These rollups do not yet cover their sources, so their retention policies stay held:");
            foreach (var shortfall in shortfalls)
            {
                error.WriteLine("  - " + shortfall);
            }

            error.WriteLine();
            error.WriteLine("Re-run --backfill-rollups; it resumes from the measured floor.");
            return 1;
        }

        output.WriteLine("DONE. Every rollup now covers its own source, which is what each retention policy's arming gate measures.");
        output.WriteLine();
        output.WriteLine("NEXT: restart the PerformanceMonitor Darling service. The arming gate checks coverage at");
        output.WriteLine("startup and releases the held retention policies by itself — there is no arming step here and");
        output.WriteLine("nothing to run by hand. The startup log line reading");
        output.WriteLine("  'N/N retention policies in place, N armed, 0 held paused pending backfill'");
        output.WriteLine("is the confirmation; the first purge then reclaims the raw tables in one pass.");
        output.WriteLine();
        output.WriteLine($"Do not delay the restart. The hourly rollups carry their OWN retention policy ({TimescaleSupport.HourlyRetentionInterval}), already");
        output.WriteLine("armed on these stores, which will trim the coverage this run just built when it next fires");
        output.WriteLine("(roughly daily). Restarting now is what lets the raw policies arm off that coverage first. If");
        output.WriteLine("the trim wins the race nothing is lost — raw is still held — and re-running this verb rebuilds it.");
        return 0;
    }

    /// <summary>
    /// Free space on the volume the store's data directory lives on. Asked of the STORE
    /// (<c>current_setting('data_directory')</c>) rather than derived from config, so it is right in
    /// bring-your-own mode too. Returns an error string rather than a number in the two cases where measuring
    /// would measure the WRONG volume: the login cannot read the setting, or the store is on another host so
    /// the path does not exist here.
    /// </summary>
    private static async Task<(long FreeBytes, string? Error)> ResolveStoreFreeSpaceAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var dataDirectory = await RollupBackfill.DataDirectoryAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return (0, "could not read the store's data_directory, so the free space on the volume that will grow is unknown. The login needs superuser or pg_read_all_settings.");
        }

        if (!Directory.Exists(dataDirectory))
        {
            return (0, $"the store's data directory ({dataDirectory}) does not exist on this machine, so the store is on another host and this machine's free space is not the number that matters. Run --backfill-rollups ON the store host.");
        }

        try
        {
            return (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dataDirectory))!).AvailableFreeSpace, null);
        }
        catch (Exception ex)
        {
            return (0, $"could not read free space for {dataDirectory}: {ex.Message}.");
        }
    }

    /// <summary>
    /// The refusal, with the exact shortfall and the two things an operator can actually do about it. PURE, so
    /// the numbers and the options pin in a unit test — which matters precisely because this message only ever
    /// appears in the situation nobody wants to reproduce by hand.
    /// </summary>
    public static string FormatDiskRefusal(long estimatedBytes, long freeBytes)
    {
        var required = RollupBackfill.RequiredBytes(estimatedBytes);
        var shortfall = required - freeBytes;

        return string.Create(CultureInfo.InvariantCulture, $@"REFUSING: not enough free space to back fill safely.

  Estimated materialization : {RollupBackfillPlan.FormatBytes(estimatedBytes)}
  Required free space       : {RollupBackfillPlan.FormatBytes(required)}  (estimate x {RollupBackfill.SafetyFactor:0.##} headroom + {RollupBackfillPlan.FormatBytes(RollupBackfill.ReserveBytes)} reserve)
  Free space now            : {RollupBackfillPlan.FormatBytes(freeBytes)}
  SHORT BY                  : {RollupBackfillPlan.FormatBytes(shortfall)}

The rollups have to materialize the WHOLE history before the arming gate will release the raw
retention policies, so peak disk comes BEFORE any reclaim. Running this now would fill the volume
and take the store down without ever reaching the point where it frees anything.

Your options:
  1. Grow the volume by at least {RollupBackfillPlan.FormatBytes(shortfall)} and re-run. This is the option that ends
     with the raw purges armed and the space reclaimed permanently.
  2. Accept waiting. Nothing is broken and nothing is being lost: the arming gate is holding the raw
     purges closed precisely because the rollups do not cover this history yet, and reads of those old
     windows are served from raw. Raw keeps growing at full rate until the backfill happens, so this
     buys time rather than solving it.

Re-run with --dry-run at any time to re-check the numbers; the plan is recomputed from the store.");
    }

    /// <summary>A duration an operator can plan around ("about 3 hours"), not a timespan literal.</summary>
    private static string DescribeDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
        {
            return "under a minute";
        }

        if (duration < TimeSpan.FromHours(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{duration.TotalMinutes:0} minutes");
        }

        return duration < TimeSpan.FromDays(1)
            ? string.Create(CultureInfo.InvariantCulture, $"{duration.TotalHours:0.#} hours")
            : string.Create(CultureInfo.InvariantCulture, $"{duration.TotalDays:0.#} days");
    }
}
