/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Service.Mcp;

/* #1581: classify the FIRST argument before anything else. Only a no-arg invocation (StartHost — how the
   Windows Service Control Manager starts the exe) or a RECOGNIZED verb reaches the host; --version/--help
   print and exit 0; an UNKNOWN option prints usage to stderr and exits NON-ZERO without starting the host.
   The incident: `Service.exe --version` used to fall through into a real service startup and spawn a second
   instance that fought the first over the bundled PostgreSQL and ports — the outage. The recognized-verb
   dispatch below is unchanged; it only runs for the RunKnownVerb / StartHost fall-through. */
switch (DarlingCliCommands.ClassifyStartupArgs(args))
{
    case StartupAction.PrintVersion:
        Console.WriteLine(DarlingCliCommands.ProductVersion());
        return 0;

    case StartupAction.PrintHelp:
        Console.WriteLine(DarlingCliCommands.UsageText());
        return 0;

    case StartupAction.UnknownOption:
        Console.Error.WriteLine($"Unknown option: {args[0]}");
        Console.Error.WriteLine();
        Console.Error.WriteLine(DarlingCliCommands.UsageText());
        return 1;

    case StartupAction.RunKnownVerb:
    case StartupAction.StartHost:
    default:
        /* Fall through to the recognized-verb dispatch (RunKnownVerb) or the host build (StartHost). */
        break;
}

/* CLI verb: encrypt a SQL-auth password for darling.json. Reads from stdin (not an argument,
   so the plaintext never lands in shell history) and prints the DPAPI-LocalMachine blob. */
if (args.Length > 0 && DarlingCliCommands.IsEncryptPasswordVerb(args[0]))
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("--encrypt-password requires Windows (DPAPI).");
        return 1;
    }

    Console.Error.Write("Password: ");
    var plaintext = Console.ReadLine();
    if (string.IsNullOrEmpty(plaintext))
    {
        Console.Error.WriteLine("No password read from stdin.");
        return 1;
    }

    Console.WriteLine(DarlingSecrets.Protect(plaintext));
    Console.Error.WriteLine("Paste the line above into the server's \"encryptedPassword\" in darling.json.");
    return 0;
}

/* CLI verb: validate darling.json and probe every configured server (reachability + permission pre-flight),
   reusing the same DarlingServerConnector probe the test_connect command runs. Optional second arg = an
   explicit config path (else the usual DARLING_CONFIG / next-to-binary resolution). Exit 0 iff all pass. */
if (args.Length > 0 && DarlingCliCommands.IsValidateConfigVerb(args[0]))
{
    var configPath = args.Length > 1 ? args[1] : null;
    return await DarlingCliCommands.ValidateConfigAsync(configPath, Console.Out, Console.Error, CancellationToken.None);
}

/* CLI verb: print a paste-ready remote-viewer connection string + the server TLS cert for the opt-in store
   network endpoint (darling-network-endpoints D8). It DPAPI-decrypts the network role's credential, so it is
   Windows-only (same guard shape as --encrypt-password). Optional second arg = an explicit config path. */
if (args.Length > 0 && DarlingCliCommands.IsPrintViewerConnectionVerb(args[0]))
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("--print-viewer-connection requires Windows (DPAPI).");
        return 1;
    }

    var configPath = args.Length > 1 ? args[1] : null;
    return await DarlingCliCommands.PrintViewerConnectionAsync(configPath, Console.Out, Console.Error, CancellationToken.None);
}

/* CLI verb: the interactive --configure-network wizard (#1561; web surface #1617) — guides the operator
   through the opt-in store / MCP / web-dashboard LAN exposure, validating every input by delegation to the
   SAME resolvers the running service fail-closes on, then splicing a comment-preserving edit into
   darling.json behind a timestamped backup. It generates + DPAPI-protects the MCP bearer / web access
   tokens, so it is Windows-only (same guard shape as the two verbs above). Optional second arg = an
   explicit config path. Console.In is the scripted-input testability lever. */
if (args.Length > 0 && DarlingCliCommands.IsConfigureNetworkVerb(args[0]))
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("--configure-network requires Windows (DPAPI + service control).");
        return 1;
    }

    var configPath = args.Length > 1 ? args[1] : null;
    return await DarlingCliCommands.ConfigureNetworkAsync(
        configPath, Console.In, Console.Out, Console.Error, CancellationToken.None);
}

/* CLI verbs: enable/disable the embedded MCP + web-dashboard endpoints on a HEADLESS managed deployment. Each
   flips the live switch in config.config_service (mcp_enabled/web_enabled — the store is authoritative after the
   first run; darling.json's enabled is only the seed) via a targeted UPDATE whose self-bump trigger makes the
   worker hot-reload within one sweep, and — when the endpoint opts into LAN exposure — opens/removes the matching
   scoped firewall rule if elevated (else prints it as an elevated handoff). They decrypt the managed owner
   credential + touch the firewall, so they are Windows-only (same guard shape as --print-viewer-connection).
   Optional second arg = an explicit config path. The IsKnownVerb allow-list mirrors this dispatch (they cannot drift). */
if (args.Length > 0 && DarlingCliCommands.IsEnableMcpVerb(args[0]))
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("--enable-mcp requires Windows (DPAPI + firewall).");
        return 1;
    }

    var configPath = args.Length > 1 ? args[1] : null;
    return await DarlingCliCommands.EnableMcpAsync(configPath, Console.Out, Console.Error, CancellationToken.None);
}

if (args.Length > 0 && DarlingCliCommands.IsDisableMcpVerb(args[0]))
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("--disable-mcp requires Windows (DPAPI + firewall).");
        return 1;
    }

    var configPath = args.Length > 1 ? args[1] : null;
    return await DarlingCliCommands.DisableMcpAsync(configPath, Console.Out, Console.Error, CancellationToken.None);
}

if (args.Length > 0 && DarlingCliCommands.IsEnableWebVerb(args[0]))
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("--enable-web requires Windows (DPAPI + firewall).");
        return 1;
    }

    var configPath = args.Length > 1 ? args[1] : null;
    return await DarlingCliCommands.EnableWebAsync(configPath, Console.Out, Console.Error, CancellationToken.None);
}

if (args.Length > 0 && DarlingCliCommands.IsDisableWebVerb(args[0]))
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("--disable-web requires Windows (DPAPI + firewall).");
        return 1;
    }

    var configPath = args.Length > 1 ? args[1] : null;
    return await DarlingCliCommands.DisableWebAsync(configPath, Console.Out, Console.Error, CancellationToken.None);
}

var builder = Host.CreateApplicationBuilder(args);

/* Windows-service lifetime is a no-op when run from a console, so the same exe
   serves interactive debugging and `sc create` installation (plan HP7/Phase 8). */
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PerformanceMonitor Darling";
});

/* The rolling file log under %ProgramData%\PerformanceMonitorDarling\logs — the service's PRIMARY
   diagnostic surface (see DarlingFileLoggerProvider remarks). Registered unconditionally: console
   runs get the same file, which is exactly what an operator collecting a bug report wants. */
builder.Logging.AddProvider(new DarlingFileLoggerProvider());

if (OperatingSystem.IsWindows())
{
    /* Pin the Event Log source to the SERVICE NAME so operators find events where the docs say to
       look ("PerformanceMonitor Darling"), not under the assembly name AddWindowsService defaults
       to. The source itself can only be REGISTERED by an elevated principal: the recommended
       NT SERVICE virtual account cannot, so the attempt below is best-effort (it succeeds on an
       elevated console run or an install script that pre-created it; see the README install step).
       When the source does not exist, the Event Log provider silently drops events — which is why
       the file log above, not this, is the primary surface. */
    builder.Logging.AddEventLog(settings => settings.SourceName = "PerformanceMonitor Darling");
    try
    {
        if (!System.Diagnostics.EventLog.SourceExists("PerformanceMonitor Darling"))
        {
            System.Diagnostics.EventLog.CreateEventSource("PerformanceMonitor Darling", "Application");
        }
    }
    catch
    {
        /* SourceExists itself throws for a non-elevated caller when the source is missing (it
           probes the Security log). Degrade: the file log carries the diagnostics regardless. */
    }
}

/* #1560/#1562: the live MCP + web enable/port seams — the worker publishes the control-plane values on
   every reload; each host's supervisor observes and starts/stops/rebinds without a service restart. */
builder.Services.AddSingleton<McpRuntimeState>();
builder.Services.AddSingleton<WebRuntimeState>();

builder.Services.AddHostedService<DarlingWorker>();

/* AN4: the analysis MCP tools over Streamable HTTP — registered always, self-gating on
   darling.json's mcp.enabled (default OFF), so Program.cs stays config-free like the worker. */
builder.Services.AddHostedService<DarlingMcpHostService>();

/* #1562: the read-only web dashboard on its own port — registered always, self-gating on
   darling.json's web.enabled (default OFF), same config-free posture as the worker and MCP host. */
builder.Services.AddHostedService<DarlingWebHostService>();

var host = builder.Build();

/* #1581 single-instance guard: acquire a system-wide named mutex BEFORE running the host (Build() only
   constructs the DI graph; Run() is what starts the worker and touches Postgres). If another instance
   already holds it, log and exit non-zero WITHOUT starting — a second instance would fight the first over
   the bundled PostgreSQL shared-memory segment and the MCP/web ports (the ~7.5h outage). Windows-only: the
   Global\ namespace and the shared-memory fight are Windows concerns, and the managed-Postgres bootstrap
   already fail-closes on non-Windows — matching how the codebase gates Windows behavior. The guard is
   acquired and released on THIS (main) thread, around the blocking host.Run(), honoring the mutex's thread
   affinity. */
SingleInstanceGuard? instanceGuard = null;
if (OperatingSystem.IsWindows())
{
    var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SingleInstance");
    instanceGuard = SingleInstanceGuard.TryAcquire(SingleInstanceGuard.ServiceMutexName);
    if (!instanceGuard.Owns)
    {
        startupLogger.LogCritical(
            "Another PerformanceMonitor Darling service instance already holds the single-instance lock ({Mutex}) — exiting without starting a second instance (a second instance would fight the first over the bundled PostgreSQL and the MCP/web ports).",
            SingleInstanceGuard.ServiceMutexName);
        instanceGuard.Dispose();
        /* Run() disposes the host on the normal path; we are skipping Run(), so dispose it ourselves. */
        host.Dispose();
        return 4;
    }

    if (instanceGuard.WasAbandoned)
    {
        startupLogger.LogWarning(
            "Acquired the single-instance lock ({Mutex}) left abandoned by a previous instance that exited without a clean shutdown — continuing startup.",
            SingleInstanceGuard.ServiceMutexName);
    }
}

try
{
    host.Run();
    return 0;
}
finally
{
    instanceGuard?.Dispose();
}
