/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Reflection;
using Installer.Core;
using Installer.Core.Models;

namespace PerformanceMonitorInstaller
{
    class Program
    {
        /*
        The unknown sentinel PARSES, so interpolating it printed "Existing installation detected: v0.0.0"
        -- a guess stated as a fact, and one the same run then contradicts a few lines later with "this
        server's recorded version could not be read". It is not a version; do not print it as one.
        */
        static string DescribeInstalledVersion(string? version) =>
            RepairOutcome.IsVersionUnknown(version)
                ? "Existing installation detected, but its recorded version could not be read."
                : $"Existing installation detected: v{version}";

        static async Task<int> Main(string[] args)
        {
            /*
            GetName().Version is never null for a loaded assembly, so the old "?? Unknown" could not fire:
            a build with no AssemblyVersion reports 0.0.0.0, which PARSES -- InstallGuard waves it through,
            and a fresh install writes it into installation_history as the version of record. It then reads
            back as version 0.0.0, which is UnknownVersionSentinel, so every later run takes that server's
            real version as unreadable and replays every migration. A version-less build must land on
            something UNPARSEABLE so InstallGuard blocks it (UnreadableBuildVersion). Same hole, same fix,
            as the Dashboard's GetAppVersion -- the two surfaces write to the same ledger.
            */
            var asmVersion = Assembly.GetExecutingAssembly().GetName().Version;
            var version =
                asmVersion != null && (asmVersion.Major | asmVersion.Minor | asmVersion.Build) != 0
                    ? asmVersion.ToString()
                    : "Unknown";

            var infoVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version;

            Console.WriteLine("================================================================================");
            Console.WriteLine($"Performance Monitor Installation Utility v{infoVersion}");
            Console.WriteLine("Copyright © 2026 Darling Data, LLC");
            Console.WriteLine("Licensed under the MIT License");
            Console.WriteLine("https://github.com/erikdarlingdata/PerformanceMonitor");
            Console.WriteLine("================================================================================");

            await CheckForInstallerUpdateAsync(version);


            /*
            Determine if running in automated mode (command-line arguments provided)
            Usage: PerformanceMonitorInstaller.exe [server] [username] [password] [options]
            If server is provided alone, uses Windows Authentication
            If server, username, and password are provided, uses SQL Authentication

            Options:
              --reinstall       Drop existing database and perform clean install
              --repair          Reinstall schema objects without running upgrade scripts. Use when an
                                upgrade fails on a missing or damaged object; non-destructive, and the
                                pending upgrade still needs to run afterwards
              --encrypt=X       Connection encryption: mandatory (default), optional, strict
              --trust-cert      Trust server certificate without validation (default: require valid cert)
              --data-path DIR   Server-side directory for the data (.mdf) file (first install only)
              --log-path DIR    Server-side directory for the log (.ldf) file (first install only)
            */
            if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase)
                              || a.Equals("-h", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  PerformanceMonitorInstaller.exe                                   Interactive mode");
                Console.WriteLine("  PerformanceMonitorInstaller.exe <server> [options]                 Windows Auth");
                Console.WriteLine("  PerformanceMonitorInstaller.exe <server> <username> <password>     SQL Auth");
                Console.WriteLine("  PerformanceMonitorInstaller.exe <server> <username>                SQL Auth (password via env var)");
                Console.WriteLine("  PerformanceMonitorInstaller.exe <server> --entra <email>           Entra ID (MFA)");
                Console.WriteLine("  PerformanceMonitorInstaller.exe <server> --managed-identity        Entra managed identity");
                Console.WriteLine("  PerformanceMonitorInstaller.exe <server> --service-principal <id>  Entra service principal");
                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine("  -h, --help           Show this help message");
                Console.WriteLine("  --reinstall          Drop existing database and perform clean install");
                Console.WriteLine("  --repair             Reinstall schema objects, skipping upgrade scripts (non-destructive)");
                Console.WriteLine("  --uninstall          Remove database, Agent jobs, and XE sessions");
                Console.WriteLine("  --reset-schedule     Reset collection schedule to recommended defaults");
                Console.WriteLine("  --troubleshoot       Run installation diagnostics (99_installer_troubleshooting.sql)");
                Console.WriteLine("  --encrypt=<level>    Connection encryption: mandatory (default), optional, strict");
                Console.WriteLine("  --trust-cert         Trust server certificate without validation");
                Console.WriteLine("  --entra <email>      Use Microsoft Entra ID interactive authentication (MFA)");
                Console.WriteLine("  --managed-identity[=<clientId>]  Entra managed identity: system-assigned, or user-assigned via clientId");
                Console.WriteLine("  --service-principal <clientId>   Entra service principal (secret via PM_AZURE_CLIENT_SECRET)");
                Console.WriteLine("  --data-path <dir>    Server-side directory for the data (.mdf) file (first install only)");
                Console.WriteLine("  --log-path <dir>     Server-side directory for the log (.ldf) file (first install only)");
                Console.WriteLine();
                Console.WriteLine("Environment Variables:");
                Console.WriteLine("  PM_SQL_PASSWORD         SQL Auth password (avoids passing on command line)");
                Console.WriteLine("  PM_AZURE_CLIENT_SECRET  Service principal client secret (for --service-principal)");
                Console.WriteLine();
                Console.WriteLine("Exit Codes:");
                Console.WriteLine("  0  Success");
                Console.WriteLine("  1  Invalid arguments");
                Console.WriteLine("  2  Connection failed");
                Console.WriteLine("  3  Critical file failed");
                Console.WriteLine("  4  Partial installation (non-critical failures)");
                Console.WriteLine("  5  Version check failed");
                Console.WriteLine("  6  SQL files not found");
                Console.WriteLine("  7  Uninstall failed");
                Console.WriteLine("  8  Upgrade failed");
                Console.WriteLine("  9  Clean install failed");
                Console.WriteLine("  10 Diagnostics found errors or failed to run");
                return 0;
            }

            bool automatedMode = args.Length > 0;
            bool reinstallMode = args.Any(a => a.Equals("--reinstall", StringComparison.OrdinalIgnoreCase));
            bool repairMode = args.Any(a => a.Equals("--repair", StringComparison.OrdinalIgnoreCase));
            bool uninstallMode = args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));

            /*
            --repair means "restore the objects, destroy nothing". Pairing it with a mode that drops the
            database is a contradiction, and the destructive mode would otherwise win silently: --uninstall
            is dispatched further down BEFORE any repair logic, and in automated mode it skips its own
            confirmation. So both destructive companions are rejected here, not just --reinstall -- the
            --uninstall gap was the parity hole in only guarding one of them.
            */
            if (repairMode && reinstallMode)
            {
                WriteError("--repair and --reinstall are mutually exclusive: --reinstall drops the database, leaving nothing to repair.");
                return (int)InstallationResultCode.InvalidArguments;
            }
            if (repairMode && uninstallMode)
            {
                WriteError("--repair and --uninstall are mutually exclusive: --uninstall drops the database, leaving nothing to repair.");
                return (int)InstallationResultCode.InvalidArguments;
            }
            bool resetSchedule = args.Any(a => a.Equals("--reset-schedule", StringComparison.OrdinalIgnoreCase));
            bool troubleshootMode = args.Any(a => a.Equals("--troubleshoot", StringComparison.OrdinalIgnoreCase));
            bool trustCert = args.Any(a => a.Equals("--trust-cert", StringComparison.OrdinalIgnoreCase));
            bool entraMode = args.Any(a => a.Equals("--entra", StringComparison.OrdinalIgnoreCase));

            /*Parse --entra email (the argument following --entra)*/
            string? entraEmail = null;
            if (entraMode)
            {
                int entraIndex = Array.FindIndex(args, a => a.Equals("--entra", StringComparison.OrdinalIgnoreCase));
                if (entraIndex >= 0 && entraIndex + 1 < args.Length && !args[entraIndex + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    entraEmail = args[entraIndex + 1];
                }
            }

            /*#1325: non-interactive Entra auth for Managed Instance / AAD-enabled targets.
              --managed-identity          -> system-assigned managed identity (no value)
              --managed-identity=<id> / --managed-identity <id> -> user-assigned MI (client id)
              --service-principal <id>    -> service principal; client secret via PM_AZURE_CLIENT_SECRET.
              The service layer (InstallationService.BuildConnectionString) already maps these to
              SqlAuthenticationMethod.ActiveDirectoryManagedIdentity / ActiveDirectoryServicePrincipal;
              this only wires the CLI surface to it.*/
            bool managedIdentityMode = args.Any(a => a.Equals("--managed-identity", StringComparison.OrdinalIgnoreCase)
                || a.StartsWith("--managed-identity=", StringComparison.OrdinalIgnoreCase));
            bool servicePrincipalMode = args.Any(a => a.Equals("--service-principal", StringComparison.OrdinalIgnoreCase)
                || a.StartsWith("--service-principal=", StringComparison.OrdinalIgnoreCase));
            string? managedIdentityClientId = GetOptionValue(args, "--managed-identity");
            string? servicePrincipalClientId = GetOptionValue(args, "--service-principal");

            /*Parse encryption option (default: Mandatory)
              Supports both --encrypt=optional and --encrypt optional */
            string encryptionLevel = "Mandatory";
            var encryptEqualsArg = args.FirstOrDefault(a => a.StartsWith("--encrypt=", StringComparison.OrdinalIgnoreCase));
            if (encryptEqualsArg != null)
            {
                string encryptValue = encryptEqualsArg.Substring("--encrypt=".Length).ToLowerInvariant();
                encryptionLevel = encryptValue switch
                {
                    "optional" => "Optional",
                    "strict" => "Strict",
                    _ => "Mandatory"
                };
            }
            else
            {
                int encryptIndex = Array.FindIndex(args, a => a.Equals("--encrypt", StringComparison.OrdinalIgnoreCase));
                if (encryptIndex >= 0 && encryptIndex + 1 < args.Length && !args[encryptIndex + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    encryptionLevel = args[encryptIndex + 1].ToLowerInvariant() switch
                    {
                        "optional" => "Optional",
                        "strict" => "Strict",
                        _ => "Mandatory"
                    };
                }
            }

            /*Parse optional custom database file locations (#768).
              Supports both --data-path=<dir> and --data-path <dir> (and --log-path).
              These are server-side directories where SQL Server places the
              PerformanceMonitor data/log files on first creation.*/
            string? dataPathArg = GetOptionValue(args, "--data-path");
            string? logPathArg = GetOptionValue(args, "--log-path");

            string? dataPath = null;
            string? logPath = null;

            if (dataPathArg != null)
            {
                if (!PathValidation.TryValidateDirectory(dataPathArg, out dataPath, out string dataPathError))
                {
                    Console.WriteLine($"Error: invalid --data-path: {dataPathError}");
                    return (int)InstallationResultCode.InvalidArguments;
                }
            }

            if (logPathArg != null)
            {
                if (!PathValidation.TryValidateDirectory(logPathArg, out logPath, out string logPathError))
                {
                    Console.WriteLine($"Error: invalid --log-path: {logPathError}");
                    return (int)InstallationResultCode.InvalidArguments;
                }
            }

            if (dataPath != null)
            {
                Console.WriteLine($"Custom data file directory: {dataPath} (used only when the database is first created)");
            }
            if (logPath != null)
            {
                Console.WriteLine($"Custom log file directory:  {logPath} (used only when the database is first created)");
            }

            /*Filter out all --flags and their trailing values to get positional arguments
              (server, username, password). Flags like --entra <email>, --encrypt <level>,
              --data-path <dir>, --log-path <dir>, --service-principal <clientId>, and the
              space form of --managed-identity <clientId> have a following value that must
              also be removed. (The --flag=value forms are single tokens handled by the
              continue below.)*/
            var filteredArgsList = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    /*Skip flags that take a trailing value (space-separated form)*/
                    if ((args[i].Equals("--entra", StringComparison.OrdinalIgnoreCase)
                        || args[i].Equals("--encrypt", StringComparison.OrdinalIgnoreCase)
                        || args[i].Equals("--data-path", StringComparison.OrdinalIgnoreCase)
                        || args[i].Equals("--log-path", StringComparison.OrdinalIgnoreCase)
                        || args[i].Equals("--service-principal", StringComparison.OrdinalIgnoreCase)
                        || args[i].Equals("--managed-identity", StringComparison.OrdinalIgnoreCase))
                        && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        i++; /*skip the value too*/
                    }
                    continue;
                }
                filteredArgsList.Add(args[i]);
            }

            var filteredArgs = filteredArgsList.ToArray();
            string? serverName;
            string? username = null;
            string? password = null;
            bool useWindowsAuth;
            bool useEntraAuth = false;
            /*#1325: "ServicePrincipal" / "ManagedIdentity" for the non-interactive Entra modes; null
              leaves the Windows/SQL/Entra-interactive paths unchanged. Matched as string literals by
              BuildConnectionString (Installer.Core has no reference to PerformanceMonitor.Common).*/
            string? authenticationType = null;

            if (automatedMode)
            {
                /*
                Automated mode with command-line arguments
                */
                serverName = filteredArgs.Length > 0 ? filteredArgs[0] : null;

                if (entraMode)
                {
                    /*Microsoft Entra ID interactive authentication*/
                    useWindowsAuth = false;
                    useEntraAuth = true;
                    username = entraEmail;

                    if (string.IsNullOrWhiteSpace(username))
                    {
                        Console.WriteLine("Error: Email address is required for Entra ID authentication.");
                        Console.WriteLine("Usage: PerformanceMonitorInstaller.exe <server> --entra <email>");
                        return (int)InstallationResultCode.InvalidArguments;
                    }

                    Console.WriteLine($"Server: {serverName}");
                    Console.WriteLine($"Authentication: Microsoft Entra ID ({username})");
                    Console.WriteLine("A browser window will open for interactive authentication...");
                }
                else if (servicePrincipalMode)
                {
                    /*Microsoft Entra service principal (client id + secret, non-interactive)*/
                    useWindowsAuth = false;
                    authenticationType = "ServicePrincipal";
                    username = servicePrincipalClientId;   /*application (client) id*/
                    password = Environment.GetEnvironmentVariable("PM_AZURE_CLIENT_SECRET");

                    if (string.IsNullOrWhiteSpace(username))
                    {
                        Console.WriteLine("Error: Client (application) ID is required for service principal authentication.");
                        Console.WriteLine("Usage: PerformanceMonitorInstaller.exe <server> --service-principal <clientId>");
                        Console.WriteLine("       (client secret via the PM_AZURE_CLIENT_SECRET environment variable)");
                        return (int)InstallationResultCode.InvalidArguments;
                    }

                    if (string.IsNullOrWhiteSpace(password))
                    {
                        Console.WriteLine("Error: Client secret is required for service principal authentication.");
                        Console.WriteLine("Set the PM_AZURE_CLIENT_SECRET environment variable to the application's client secret.");
                        return (int)InstallationResultCode.InvalidArguments;
                    }

                    Console.WriteLine($"Server: {serverName}");
                    Console.WriteLine($"Authentication: Microsoft Entra service principal ({username})");
                }
                else if (managedIdentityMode)
                {
                    /*Microsoft Entra managed identity (no secret). Blank client id = system-assigned;
                      a client id (--managed-identity=<id> or --managed-identity <id>) = user-assigned.*/
                    useWindowsAuth = false;
                    authenticationType = "ManagedIdentity";

                    Console.WriteLine($"Server: {serverName}");
                    Console.WriteLine(string.IsNullOrWhiteSpace(managedIdentityClientId)
                        ? "Authentication: Microsoft Entra managed identity (system-assigned)"
                        : $"Authentication: Microsoft Entra managed identity (user-assigned: {managedIdentityClientId})");
                }
                else if (filteredArgs.Length >= 2)
                {
                    /*SQL Authentication - password from env var or command-line*/
                    useWindowsAuth = false;
                    username = filteredArgs[1];

                    string? envPassword = Environment.GetEnvironmentVariable("PM_SQL_PASSWORD");
                    if (filteredArgs.Length >= 3)
                    {
                        password = filteredArgs[2];
                        if (envPassword == null)
                        {
                            Console.WriteLine("Note: Password provided via command-line is visible in process listings.");
                            Console.WriteLine("      Consider using PM_SQL_PASSWORD environment variable instead.");
                            Console.WriteLine();
                        }
                    }
                    else if (envPassword != null)
                    {
                        password = envPassword;
                    }
                    else
                    {
                        Console.WriteLine("Error: Password is required for SQL Server Authentication.");
                        Console.WriteLine("Provide password as third argument or set PM_SQL_PASSWORD environment variable.");

                        /*
                        The common trap: a password that begins with "--" (e.g. "--h7!x") is indistinguishable
                        from a flag, so the positional filter above dropped it and we landed here reporting
                        "no password" for a password that WAS supplied. Only say so when a "--"-token that is
                        not a recognized flag actually appeared -- otherwise this is just a forgotten password.
                        */
                        if (HasUnrecognizedDoubleDashArg(args))
                        {
                            Console.WriteLine();
                            Console.WriteLine("Note: an argument beginning with \"--\" was treated as a flag and ignored. A password");
                            Console.WriteLine("      cannot be passed on the command line if it starts with \"--\"; set PM_SQL_PASSWORD.");
                        }

                        return (int)InstallationResultCode.InvalidArguments;
                    }

                    Console.WriteLine($"Server: {serverName}");
                    Console.WriteLine($"Authentication: SQL Server ({username})");
                }
                else if (filteredArgs.Length == 1)
                {
                    /*Windows Authentication*/
                    useWindowsAuth = true;
                    Console.WriteLine($"Server: {serverName}");
                    Console.WriteLine($"Authentication: Windows");
                }
                else
                {
                    Console.WriteLine("Error: Invalid arguments.");
                    Console.WriteLine("Usage:");
                    Console.WriteLine("  Windows Auth:   PerformanceMonitorInstaller.exe <server> [options]");
                    Console.WriteLine("  SQL Auth:       PerformanceMonitorInstaller.exe <server> <username> <password> [options]");
                    Console.WriteLine("  SQL Auth:       PerformanceMonitorInstaller.exe <server> <username> [options]");
                    Console.WriteLine("                  (with PM_SQL_PASSWORD environment variable set)");
                    Console.WriteLine("  Entra ID:       PerformanceMonitorInstaller.exe <server> --entra <email>");
                    Console.WriteLine("  Managed ID:     PerformanceMonitorInstaller.exe <server> --managed-identity[=<clientId>]");
                    Console.WriteLine("  Service Prin.:  PerformanceMonitorInstaller.exe <server> --service-principal <clientId>");
                    Console.WriteLine("                  (with PM_AZURE_CLIENT_SECRET environment variable set)");
                    Console.WriteLine();
                    Console.WriteLine("Options:");
                    Console.WriteLine("  --reinstall          Drop existing database and perform clean install");
                    Console.WriteLine("  --reset-schedule     Reset collection schedule to recommended defaults");
                    Console.WriteLine("  --encrypt=<level>    Connection encryption: mandatory (default), optional, strict");
                    Console.WriteLine("  --trust-cert         Trust server certificate without validation (default: require valid cert)");
                    Console.WriteLine("  --data-path <dir>    Server-side directory for the data (.mdf) file (first install only)");
                    Console.WriteLine("  --log-path <dir>     Server-side directory for the log (.ldf) file (first install only)");
                    return (int)InstallationResultCode.InvalidArguments;
                }
            }
            else
            {
                /*
                Interactive mode - prompt for connection information
                */
                Console.Write("SQL Server instance (e.g., localhost, SQL2022): ");
                serverName = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(serverName))
                {
                    Console.WriteLine("Error: Server name is required.");
                    WaitForExit();
                    return (int)InstallationResultCode.InvalidArguments;
                }

                Console.Write("Trust server certificate? (Y/N, default N): ");
                string? trustResponse = Console.ReadLine()?.Trim();
                trustCert = trustResponse?.Equals("Y", StringComparison.OrdinalIgnoreCase) ?? false;

                Console.WriteLine("Encryption level:");
                Console.WriteLine("  [M] Mandatory (default)");
                Console.WriteLine("  [O] Optional");
                Console.WriteLine("  [S] Strict");
                Console.Write("Choice (M/O/S, default M): ");
                string? encryptResponse = Console.ReadLine()?.Trim();
                encryptionLevel = encryptResponse?.ToUpperInvariant() switch
                {
                    "O" => "Optional",
                    "S" => "Strict",
                    _ => "Mandatory"
                };

                Console.WriteLine("Authentication type:");
                Console.WriteLine("  [W] Windows Authentication (default)");
                Console.WriteLine("  [S] SQL Server Authentication");
                Console.WriteLine("  [E] Microsoft Entra ID (interactive MFA)");
                Console.Write("Choice (W/S/E, default W): ");
                string? authResponse = Console.ReadLine()?.Trim();

                if (string.IsNullOrWhiteSpace(authResponse) || authResponse.Equals("W", StringComparison.OrdinalIgnoreCase))
                {
                    useWindowsAuth = true;
                }
                else if (authResponse.Equals("E", StringComparison.OrdinalIgnoreCase))
                {
                    useWindowsAuth = false;
                    useEntraAuth = true;

                    Console.Write("Email address (UPN): ");
                    username = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(username))
                    {
                        Console.WriteLine("Error: Email address is required for Entra ID authentication.");
                        WaitForExit();
                        return (int)InstallationResultCode.InvalidArguments;
                    }

                    Console.WriteLine("A browser window will open for interactive authentication...");
                }
                else
                {
                    useWindowsAuth = false;

                    Console.Write("SQL Server login: ");
                    username = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(username))
                    {
                        Console.WriteLine("Error: Login is required for SQL Server Authentication.");
                        WaitForExit();
                        return (int)InstallationResultCode.InvalidArguments;
                    }

                    Console.Write("Password: ");
                    password = ReadPassword();
                    Console.WriteLine();

                    if (string.IsNullOrWhiteSpace(password))
                    {
                        Console.WriteLine("Error: Password is required for SQL Server Authentication.");
                        WaitForExit();
                        return (int)InstallationResultCode.InvalidArguments;
                    }
                }
            }

            /*
            Build connection string using Installer.Core
            */
            string connectionString = InstallationService.BuildConnectionString(
                serverName!,
                useWindowsAuth,
                username,
                password,
                encryptionLevel,
                trustCert,
                useEntraAuth,
                authenticationType: authenticationType,
                managedIdentityClientId: managedIdentityClientId);

            /*
            Test connection and get SQL Server version
            */
            string sqlServerVersion = "";
            string sqlServerEdition = "";

            Console.WriteLine();
            Console.WriteLine("Testing connection...");

            var serverInfo = await InstallationService.TestConnectionAsync(connectionString).ConfigureAwait(false);

            if (!serverInfo.IsConnected)
            {
                WriteError($"Connection failed: {serverInfo.ErrorMessage}");
                if (!automatedMode)
                {
                    WaitForExit();
                }
                return (int)InstallationResultCode.ConnectionFailed;
            }

            WriteSuccess("Connection successful!");
            sqlServerVersion = serverInfo.SqlServerVersion;
            sqlServerEdition = serverInfo.SqlServerEdition;

            /*Check minimum SQL Server version -- 2016+ required for on-prem (Standard/Enterprise).
              Azure MI (EngineEdition 8) is always current, skip the check.*/
            if (serverInfo.ProductMajorVersion > 0 && !serverInfo.IsSupportedVersion)
            {
                Console.WriteLine();
                Console.WriteLine($"ERROR: {serverInfo.ProductMajorVersionName} is not supported.");
                Console.WriteLine("Performance Monitor requires SQL Server 2016 (13.x) or later.");
                if (!automatedMode)
                {
                    WaitForExit();
                }
                return (int)InstallationResultCode.VersionCheckFailed;
            }

            /*
            Handle --uninstall mode (no SQL files needed)
            */
            if (uninstallMode)
            {
                return await PerformUninstallAsync(connectionString, automatedMode);
            }

            /*
            Find SQL files using ScriptProvider.FromDirectory()
            Search current directory and up to 5 parent directories
            Prefer install/ subfolder if it exists (new structure)
            */
            ScriptProvider? scriptProvider = null;
            string? sqlDirectory = null;
            string? monitorRootDirectory = null;
            string currentDirectory = Directory.GetCurrentDirectory();
            DirectoryInfo? searchDir = new DirectoryInfo(currentDirectory);

            for (int i = 0; i < 6 && searchDir != null; i++)
            {
                /*Check for install/ subfolder first (new structure)*/
                string installFolder = Path.Combine(searchDir.FullName, "install");
                if (Directory.Exists(installFolder))
                {
                    var installFiles = Directory.GetFiles(installFolder, "*.sql")
                        .Where(f => Patterns.SqlFilePattern().IsMatch(Path.GetFileName(f)))
                        .ToList();

                    if (installFiles.Count > 0)
                    {
                        sqlDirectory = installFolder;
                        monitorRootDirectory = searchDir.FullName;
                        break;
                    }
                }

                /*Fall back to old structure (SQL files in root)*/
                var files = Directory.GetFiles(searchDir.FullName, "*.sql")
                    .Where(f => Patterns.SqlFilePattern().IsMatch(Path.GetFileName(f)))
                    .ToList();

                if (files.Count > 0)
                {
                    sqlDirectory = searchDir.FullName;
                    monitorRootDirectory = searchDir.FullName;
                    break;
                }

                searchDir = searchDir.Parent;
            }

            if (sqlDirectory == null || monitorRootDirectory == null)
            {
                Console.WriteLine($"Error: No SQL installation files found.");
                Console.WriteLine($"Searched in: {currentDirectory}");
                Console.WriteLine("Expected files in install/ folder or root directory:");
                Console.WriteLine("  install/01_install_database.sql, install/02_create_tables.sql, etc.");
                Console.WriteLine();
                Console.WriteLine("Make sure the installer is in the Monitor directory or a subdirectory.");
                if (!automatedMode)
                {
                    WaitForExit();
                }
                return (int)InstallationResultCode.SqlFilesNotFound;
            }

            scriptProvider = ScriptProvider.FromDirectory(monitorRootDirectory);

            if (troubleshootMode)
            {
                return await PerformTroubleshootAsync(connectionString, scriptProvider, automatedMode);
            }

            var sqlFiles = scriptProvider.GetInstallFiles();

            Console.WriteLine();
            Console.WriteLine($"Found {sqlFiles.Count} SQL files in: {sqlDirectory}");
            if (monitorRootDirectory != sqlDirectory)
            {
                Console.WriteLine($"Using new folder structure (install/ subfolder)");
            }

            /*
            Create progress reporter that routes to console helpers
            */
            var progress = new Progress<InstallationProgress>(p =>
            {
                switch (p.Status)
                {
                    case "Success":
                        WriteSuccess(p.Message);
                        break;
                    case "Error":
                        WriteError(p.Message);
                        break;
                    case "Warning":
                        WriteWarning(p.Message);
                        break;
                    case "Debug":
                        /*Suppress debug messages in CLI output*/
                        break;
                    default:
                        Console.WriteLine(p.Message);
                        break;
                }
            });

            /*
            Main installation loop - allows retry on failure
            */
            int upgradeSuccessCount = 0;
            int upgradeFailureCount = 0;
            int installSuccessCount = 0;
            int installFailureCount = 0;
            int totalSuccessCount = 0;
            int totalFailureCount = 0;
            var installationErrors = new List<(string FileName, string ErrorMessage)>();
            bool installationSuccessful = false;
            bool retry;
            DateTime installationStartTime = DateTime.Now;
            /* Declared out here so the history write below can tell what version the database is still at. */
            string? currentVersion = null;
            do
            {
                retry = false;
                upgradeSuccessCount = 0;
                upgradeFailureCount = 0;
                installSuccessCount = 0;
                installFailureCount = 0;
                installationErrors.Clear();
                installationSuccessful = false;
                installationStartTime = DateTime.Now;
                /* Reset with its siblings so a clean-install iteration can never reuse the prior one's version. */
                currentVersion = null;

                /*
                Ask about clean install (automated mode preserves database unless --reinstall flag is used)
                */
                bool dropExisting;
                if (automatedMode)
                {
                    dropExisting = reinstallMode;
                    Console.WriteLine();
                    if (reinstallMode)
                    {
                        Console.WriteLine("Automated mode: Performing clean reinstall (dropping existing database)...");
                    }
                    else
                    {
                        Console.WriteLine("Automated mode: Performing upgrade (preserving existing database)...");
                    }
                }
                else
                {
                    Console.WriteLine();
                    Console.Write("Drop existing PerformanceMonitor database if it exists? (Y/N, default N): ");
                    string? cleanInstall = Console.ReadLine();
                    dropExisting = cleanInstall?.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase) ?? false;
                }

                /*
                Validate this binary's OWN version BEFORE the clean-install branch, not inside the upgrade
                one. It is what a FRESH install writes to installation_history.installer_version, so
                letting an unparseable value through poisons a brand-new server's ledger at birth -- after
                which both this installer and the Dashboard refuse to touch it ever again. --reinstall IS
                a fresh install, so scoping this to the upgrade path missed the exact case it names.
                */
                if (ScriptProvider.TryParseVersionCore(version) == null)
                {
                    Console.WriteLine();
                    WriteError($"This installer reports its own version as '{version}', which is not a valid version.");
                    Console.WriteLine("Aborting: an install would record that value as the server's version.");
                    Console.WriteLine("This is a build problem, not a problem with the server.");
                    if (!automatedMode)
                    {
                        WaitForExit();
                    }
                    return (int)InstallationResultCode.VersionCheckFailed;
                }

                if (dropExisting)
            {
                Console.WriteLine();
                Console.WriteLine("Performing clean install...");
                try
                {
                    await InstallationService.CleanInstallAsync(connectionString).ConfigureAwait(false);
                    WriteSuccess("Clean install completed (jobs and database removed)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("================================================================================");
                    WriteError($"Clean install failed: {ex.Message}");
                    Console.WriteLine("The database was NOT dropped/reset, so continuing would install over an");
                    Console.WriteLine("inconsistent database with neither a clean drop nor upgrade scripts. Aborting.");
                    Console.WriteLine("Fix the error above and re-run the installer.");
                    Console.WriteLine("================================================================================");

                    string errorLogPath = WriteErrorLog(ex, serverName!, infoVersion);
                    Console.WriteLine($"Error log written to: {errorLogPath}");

                    if (!automatedMode)
                    {
                        WaitForExit();
                    }
                    return (int)InstallationResultCode.CleanInstallFailed;
                }
            }
            else
            {
                /*
                Upgrade mode - check for existing installation and apply upgrades
                */
                try
                {
                    currentVersion = await InstallationService.GetInstalledVersionAsync(connectionString, throwOnError: true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("================================================================================");
                    Console.WriteLine("ERROR: Failed to check for existing installation");
                    Console.WriteLine("================================================================================");
                    Console.WriteLine(ex.Message);
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine($"Details: {ex.InnerException.Message}");
                    }
                    Console.WriteLine();
                    Console.WriteLine("This may indicate a permissions issue or database corruption.");
                    Console.WriteLine("Please review the error log and report this issue if it persists.");
                    Console.WriteLine();

                    /*Write error log for bug reporting*/
                    string errorLogPath = WriteErrorLog(ex, serverName!, infoVersion);
                    Console.WriteLine($"Error log written to: {errorLogPath}");

                    if (!automatedMode)
                    {
                        WaitForExit();
                    }
                    return (int)InstallationResultCode.VersionCheckFailed;
                }

                /*
                Same decision the Dashboard makes -- shared via InstallGuard so the two cannot drift, and
                pinned by tests that actually run in CI. --repair skips ExecuteAllUpgradesAsync, which is
                the only thing that would otherwise parse the recorded version before writing it straight
                back, and the newer-than-us case produces zero hops AND zero failures, so nothing
                downstream catches it. Repair is no exception -- it runs the same install scripts.
                */
                switch (InstallGuard.Check(currentVersion, version))
                {
                    case InstallBlock.UnreadableBuildVersion:
                        /* Also checked above, before the clean-install branch. Handled here so the switch
                           is exhaustive and cannot silently fall through if that check ever moves. */
                        Console.WriteLine();
                        WriteError($"This installer reports its own version as '{version}', which is not a valid version.");
                        Console.WriteLine("Aborting: an install would record that value as the server's version.");
                        if (!automatedMode)
                        {
                            WaitForExit();
                        }
                        return (int)InstallationResultCode.VersionCheckFailed;

                    case InstallBlock.UnreadableInstalledVersion:
                        Console.WriteLine();
                        WriteError($"The version recorded on this server ('{currentVersion}') is not a valid version.");
                        Console.WriteLine("Aborting: without a comparable version we cannot tell which migrations still need to run.");
                        if (!automatedMode)
                        {
                            WaitForExit();
                        }
                        return (int)InstallationResultCode.VersionCheckFailed;

                    case InstallBlock.InstalledIsNewerThanBuild:
                        Console.WriteLine();
                        WriteError($"Installed version v{currentVersion} is newer than this installer (v{version}).");
                        Console.WriteLine("Aborting: running an older installer over a newer database would revert its objects to");
                        Console.WriteLine("the older definitions and record it at the lower version. Use a matching or newer installer.");
                        if (!automatedMode)
                        {
                            WaitForExit();
                        }
                        return (int)InstallationResultCode.VersionCheckFailed;

                    case InstallBlock.None:
                        break;

                    default:
                        /*
                        Never default to "safe to install". A new InstallBlock member silently becoming an
                        allow is the one failure mode this whole guard exists to prevent.
                        */
                        Console.WriteLine();
                        WriteError($"Unhandled install-guard result. Aborting rather than assuming it is safe.");
                        if (!automatedMode)
                        {
                            WaitForExit();
                        }
                        return (int)InstallationResultCode.VersionCheckFailed;
                }

                /*
                Refuse to repair what is not there. Falling through would run a FULL fresh install and
                stamp the target version -- a whole new database and Agent jobs on a mistyped server, and,
                if the database exists but its history table does not, a target-version stamp that strands
                every migration in between. An operator who typed --repair asked for the opposite.
                */
                if (repairMode && currentVersion == null)
                {
                    Console.WriteLine();
                    WriteError("--repair found no existing PerformanceMonitor installation to repair on this server.");
                    Console.WriteLine("Repair reinstalls the objects of an existing installation; it will not create one.");
                    Console.WriteLine("Re-run without --repair to install, or check the server name.");
                    if (!automatedMode)
                    {
                        WaitForExit();
                    }
                    return (int)InstallationResultCode.VersionCheckFailed;
                }

                if (currentVersion != null && repairMode)
                {
                    /*
                    Repair reinstalls the schema objects (install scripts are idempotent) without
                    running migrations, so a hop that failed on a missing or damaged object can be
                    recovered without dropping the database. The pending upgrade runs afterwards.
                    */
                    Console.WriteLine();
                    Console.WriteLine(DescribeInstalledVersion(currentVersion));
                    Console.WriteLine("Repair mode: skipping upgrade scripts. Objects will be reinstalled at their");
                    Console.WriteLine("current definitions; the pending upgrade still needs to run afterwards.");
                }
                else if (currentVersion != null)
                {
                    Console.WriteLine();
                    Console.WriteLine(DescribeInstalledVersion(currentVersion));
                    Console.WriteLine("Checking for applicable upgrades...");

                    var (upgSuccessCount, upgFailureCount, upgradeCount) =
                        await InstallationService.ExecuteAllUpgradesAsync(
                            scriptProvider,
                            connectionString,
                            currentVersion,
                            version,
                            progress).ConfigureAwait(false);

                    upgradeSuccessCount = upgSuccessCount;
                    upgradeFailureCount = upgFailureCount;

                    if (upgradeCount > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"Upgrades complete: {upgradeSuccessCount} succeeded, {upgradeFailureCount} failed");
                    }
                    else if (upgradeFailureCount == 0)
                    {
                        Console.WriteLine("No pending upgrades found.");
                    }

                    /*
                    Abort if any upgrade failed -- proceeding would reinstall over a partially-upgraded
                    database. Checked outside the upgradeCount block because discovery itself can fail
                    before any hop runs (an unreadable installed version reports a failure with an
                    upgrade count of zero), and that must not read as "no pending upgrades".
                    */
                    if (upgradeFailureCount > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("================================================================================");
                        WriteError("Installation aborted: upgrade scripts must succeed before installation can proceed.");
                        Console.WriteLine("Fix the errors above and re-run the installer.");
                        Console.WriteLine("================================================================================");
                        if (!automatedMode)
                        {
                            WaitForExit();
                        }
                        return (int)InstallationResultCode.UpgradesFailed;
                    }
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("No existing installation detected, proceeding with fresh install...");
                }
            }

            /*
            Execute SQL files in order
            */
            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine("Starting installation...");
            Console.WriteLine("================================================================================");
            Console.WriteLine();

            /*
            Execute installation using Installer.Core
            Use DependencyInstaller for community dependencies before validation
            */
            string communityDir = Path.Combine(monitorRootDirectory, "community");
            using var dependencyInstaller = new DependencyInstaller(communityDir);

            var installResult = await InstallationService.ExecuteInstallationAsync(
                connectionString,
                scriptProvider,
                cleanInstall: false, /* Clean install was already handled above if requested */
                resetSchedule: resetSchedule,
                progress: new Progress<InstallationProgress>(p =>
                {
                    switch (p.Status)
                    {
                        case "Success":
                            if (p.Message.EndsWith(" - Success", StringComparison.Ordinal))
                            {
                                /*The "Executing..." was already printed by the Info message*/
                                WriteSuccess("Success");
                            }
                            else
                            {
                                WriteSuccess(p.Message);
                            }
                            break;
                        case "Error":
                            if (p.Message.Contains(" - FAILED:", StringComparison.Ordinal))
                            {
                                WriteError("FAILED");
                                string errorMsg = p.Message.Substring(p.Message.IndexOf(" - FAILED: ", StringComparison.Ordinal) + 11);
                                Console.WriteLine($"  Error: {errorMsg}");
                            }
                            else if (p.Message == "Critical installation file failed. Aborting installation.")
                            {
                                Console.WriteLine();
                                Console.WriteLine(p.Message);
                            }
                            else
                            {
                                WriteError(p.Message);
                            }
                            break;
                        case "Warning":
                            WriteWarning(p.Message);
                            break;
                        case "Info":
                            if (p.Message.StartsWith("Executing ", StringComparison.Ordinal) && p.Message.EndsWith("...", StringComparison.Ordinal))
                            {
                                /*Replicate "Executing <file>... " format (no newline yet)*/
                                Console.Write(p.Message + " ");
                            }
                            else if (p.Message == "Resetting schedule to recommended defaults...")
                            {
                                Console.Write("(resetting schedule) ");
                            }
                            else if (p.Message != "Starting installation...")
                            {
                                Console.WriteLine(p.Message);
                            }
                            break;
                        case "Debug":
                            /*Suppress debug messages in CLI output*/
                            break;
                        default:
                            Console.WriteLine(p.Message);
                            break;
                    }
                }),
                preValidationAction: async () =>
                {
                    Console.WriteLine();
                    Console.WriteLine("================================================================================");
                    Console.WriteLine("Installing community dependencies...");
                    Console.WriteLine("================================================================================");
                    Console.WriteLine();

                    try
                    {
                        await dependencyInstaller.InstallDependenciesAsync(
                            connectionString,
                            new Progress<InstallationProgress>(dp =>
                            {
                                switch (dp.Status)
                                {
                                    case "Success":
                                        WriteSuccess(dp.Message);
                                        break;
                                    case "Error":
                                        WriteError(dp.Message);
                                        break;
                                    case "Warning":
                                        WriteWarning(dp.Message);
                                        break;
                                    case "Debug":
                                        break;
                                    default:
                                        Console.WriteLine(dp.Message);
                                        break;
                                }
                            })).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Dependency installation encountered errors: {ex.Message}");
                        Console.WriteLine("Continuing with installation...");
                    }
                },
                cancellationToken: default,
                dataPath: dataPath,
                logPath: logPath).ConfigureAwait(false);

            installSuccessCount = installResult.FilesSucceeded;
            installFailureCount = installResult.FilesFailed;
            installationErrors.AddRange(installResult.Errors);

            /*Check for critical file failure*/
            if (installResult.FilesFailed > 0 && installResult.Errors.Any(e => Patterns.IsCriticalFile(e.FileName)))
            {
                if (!automatedMode)
                {
                    WaitForExit();
                }
                return (int)InstallationResultCode.CriticalScriptFailed;
            }

            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine("File Execution Summary");
            Console.WriteLine("================================================================================");
            if (upgradeSuccessCount > 0 || upgradeFailureCount > 0)
            {
                Console.WriteLine($"Upgrades:     {upgradeSuccessCount} succeeded, {upgradeFailureCount} failed");
            }
            Console.WriteLine($"Installation: {installSuccessCount} succeeded, {installFailureCount} failed");
            Console.WriteLine();

            /*
            Run initial collection and retry failed views
            This validates the installation and creates dynamically-generated tables
            */
            if (installFailureCount <= 1 && automatedMode) /* Allow 1 failure for query_snapshots view */
            {
                Console.WriteLine();
                Console.WriteLine("================================================================================");
                Console.WriteLine("Running initial collection to validate installation...");
                Console.WriteLine("================================================================================");
                Console.WriteLine();

                try
                {
                    Console.Write("Executing master collector... ");
                    var (collectorsSucceeded, collectorsFailed) = await InstallationService.RunValidationAsync(
                        connectionString,
                        new Progress<InstallationProgress>(vp =>
                        {
                            /*Suppress most messages; the method writes detailed results*/
                            if (vp.Status == "Error" && !vp.Message.StartsWith("  ", StringComparison.Ordinal))
                            {
                                WriteError(vp.Message);
                            }
                        })).ConfigureAwait(false);

                    WriteSuccess("Success");
                    Console.WriteLine();
                    Console.Write("Verifying data collection... ");
                    Console.WriteLine($"✓ {collectorsSucceeded} collectors ran successfully");

                    if (collectorsFailed > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"⚠ {collectorsFailed} collector(s) encountered errors");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"✗ Failed");
                    Console.WriteLine($"Error: {ex.Message}");
                    Console.WriteLine();
                    Console.WriteLine("Installation completed but initial collection failed.");
                    Console.WriteLine("Check PerformanceMonitor.config.collection_log for details.");
                }
            }

            /*
            Installation summary
            Calculate totals and determine success
            Treat query_snapshots view failure as a warning, not an error
            */
            totalSuccessCount = upgradeSuccessCount + installSuccessCount;
            totalFailureCount = upgradeFailureCount + installFailureCount;

            /*
            A repair on a database with pending migrations is EXPECTED to fail some install files, and
            that is not a failed repair. The install scripts compile against the CURRENT schema -- e.g.
            install/23_process_blocked_process_xml.sql reads collect.blocking_BlockedProcessReport.monitor_loop,
            a column the 3.0.0-to-3.1.0 migration adds -- and ALTER PROCEDURE binds columns at compile
            time, so those procedures cannot compile until the upgrade runs (Msg 207). A failed
            CREATE OR ALTER leaves the old body intact, so nothing is damaged, and the upgrade's own
            install pass recompiles them.

            Reporting that as PartialInstallation (exit 4) is actively dangerous: a script gating on
            %ERRORLEVEL% treats a good repair as a failure, and the operator reading "repair failed"
            reaches for --reinstall, which DROPS the database -- the exact destructive outcome this
            feature exists to avoid. A critical file (01_/02_/03_) failing is different: that aborts the
            whole pass, so the repair reinstalled nothing and really did fail.
            */
            bool repairRan = repairMode && currentVersion != null;

            /*
            Shared with the Dashboard so the two cannot drift -- see RepairOutcome. A critical file
            failing already returned CriticalScriptFailed above, so it cannot reach here; pass false.
            */
            bool repairFailuresExcused = RepairOutcome.FailuresAreExpected(
                repairRan,
                currentVersion,
                version,
                criticalFileFailed: false);

            installationSuccessful = totalFailureCount == 0 || repairFailuresExcused;

            /*
            Log installation history to database
            */
            /*
            A repair reinstalls objects without running migrations, so it must NOT record the target
            version -- that would strand every pending hop, which is exactly what the upgrade abort
            above exists to prevent.

            It writes NO history row at all. A repair changes no version, and installation_history is
            the version ledger -- echoing back a version we merely READ is how a guess becomes a fact.
            Concretely: GetInstalledVersionAsync returns the unknown sentinel as a #538 fallback when the database
            exists but has no SUCCESS row, meaning "unknown, try every upgrade". Persisting that as a
            SUCCESS row would turn the guess into truth. Writing nothing leaves the previous row as the
            version of record, so the pending upgrade is still offered afterwards.
            */
            if (repairMode && currentVersion != null)
            {
                Console.WriteLine();
                Console.WriteLine("Repair does not change the recorded version, so no installation history row was written.");
            }
            else
            {
                try
                {
                    await InstallationService.LogInstallationHistoryAsync(
                        connectionString,
                        version,
                        infoVersion,
                        installationStartTime,
                        totalSuccessCount,
                        totalFailureCount,
                        installationSuccessful
                    ).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not log installation history: {ex.Message}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine("Installation Summary");
            Console.WriteLine("================================================================================");

            if (repairFailuresExcused && totalFailureCount > 0)
            {
                /*
                A repair with a pending upgrade exits 0 -- but it did NOT install cleanly, and saying
                "Installation completed successfully! / All collector stored procedures" over N procedures
                that demonstrably failed to compile is a lie the very next paragraph contradicts. The exit
                code is the contract for scripts; the banner is for the human, and the human needs the truth.

                Gated on there BEING failures: repairFailuresExcused only means "if it failed, that is
                expected", so a clean repair is still a clean install and gets the ordinary banner.
                */
                WriteWarning($"Repair completed with {totalFailureCount} expected error(s).");
                Console.WriteLine();
                Console.WriteLine("Those errors are expected: the install scripts compile against the CURRENT");
                Console.WriteLine("schema, and this server's pending upgrade has not run yet, so a few procedures");
                Console.WriteLine("cannot compile until it does. A failed CREATE OR ALTER leaves the previous");
                Console.WriteLine("definition intact -- nothing was damaged, and the upgrade recompiles them.");
            }
            else if (installationSuccessful)
            {
                WriteSuccess("Installation completed successfully!");
                Console.WriteLine();
                Console.WriteLine("WHAT WAS INSTALLED:");
                Console.WriteLine("✓ PerformanceMonitor database and all collection tables");
                Console.WriteLine("✓ All collector stored procedures");
                Console.WriteLine("✓ Community dependencies (sp_WhoIsActive, DarlingData, First Responder Kit)");
                Console.WriteLine("✓ SQL Agent Job: PerformanceMonitor - Collection (runs every 1 minute)");
                Console.WriteLine("✓ SQL Agent Job: PerformanceMonitor - Data Retention (runs daily at 2:00 AM)");
                Console.WriteLine("✓ Initial collection completed successfully");

                Console.WriteLine();
                Console.WriteLine("NEXT STEPS:");
                Console.WriteLine("1. Ensure SQL Server Agent service is running");
                Console.WriteLine("2. Verify installation: SELECT * FROM PerformanceMonitor.report.collection_health;");
                Console.WriteLine("3. Monitor job history in SQL Server Agent");
                Console.WriteLine();
                Console.WriteLine("See README.md for detailed information.");
            }
            else
            {
                WriteWarning($"Installation completed with {totalFailureCount} error(s).");
                Console.WriteLine("Review errors above and check PerformanceMonitor.config.collection_log for details.");
            }

            /*
            The repair handoff. Without it the operator is told "N error(s), review errors above" with no
            next step and reaches for --reinstall, which drops the database.
            */
            if (repairRan)
            {
                /*
                Keyed on HasPendingUpgrade, NOT on FailuresAreExpected. They answer different questions,
                and they disagree for the unknown sentinel: an unreadable version cannot excuse a file
                failure, but it absolutely can have every hop pending. Reusing the one flag printed
                "already at the current version, so there is no upgrade to apply" two lines under
                "still at v1.0.0" -- for a server with eleven migrations waiting.
                */
                bool versionUnknown = RepairOutcome.IsVersionUnknown(currentVersion);
                bool pendingUpgrade = RepairOutcome.HasPendingUpgrade(currentVersion, version);

                Console.WriteLine();
                Console.WriteLine("================================================================================");

                if (versionUnknown)
                {
                    Console.WriteLine("Repair complete. No version was recorded.");
                    Console.WriteLine("This server's recorded version could not be read, so it is unknown which migrations");
                    Console.WriteLine("have run.");
                    if (totalFailureCount > 0)
                    {
                        Console.WriteLine($"{totalFailureCount} object(s) failed. Some may simply be waiting on a migration.");
                    }
                    Console.WriteLine();
                    Console.WriteLine("Next: re-run WITHOUT --repair to attempt every upgrade, then re-check.");
                }
                else if (pendingUpgrade)
                {
                    Console.WriteLine($"Repair complete. This server is still at v{currentVersion} and no version was recorded.");
                    if (totalFailureCount > 0)
                    {
                        Console.WriteLine($"{totalFailureCount} object(s) could not be compiled because the pending upgrade has not");
                        Console.WriteLine("run yet. This is expected -- they reference columns the upgrade adds, and the");
                        Console.WriteLine("upgrade will recompile them.");
                    }
                    Console.WriteLine();
                    Console.WriteLine("Next: re-run WITHOUT --repair to apply the pending upgrade.");
                }
                else
                {
                    Console.WriteLine($"Repair complete. This server is at v{currentVersion} and no version was recorded.");
                    Console.WriteLine("It was already at the current version, so there is no upgrade to apply.");
                }

                Console.WriteLine("================================================================================");
            }

            /*
            Ask if user wants to retry or exit (skip in automated mode)
            */
            if (totalFailureCount > 0 && !automatedMode)
            {
                retry = PromptRetryOrExit();
            }

            } while (retry);

            /*
            Generate installation summary report file
            */
            try
            {
                var summaryResult = new InstallationResult
                {
                    Success = installationSuccessful,
                    FilesSucceeded = totalSuccessCount,
                    FilesFailed = totalFailureCount,
                    StartTime = installationStartTime,
                    EndTime = DateTime.Now
                };
                foreach (var (fileName, errorMessage) in installationErrors)
                {
                    summaryResult.Errors.Add((fileName, errorMessage));
                }

                string reportPath = InstallationService.GenerateSummaryReport(
                    serverName!,
                    sqlServerVersion,
                    sqlServerEdition,
                    infoVersion,
                    summaryResult,
                    outputDirectory: Directory.GetCurrentDirectory());

                Console.WriteLine();
                Console.WriteLine($"Installation report saved to: {reportPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"Warning: Could not generate summary report: {ex.Message}");
            }

            /*
            Exit message for successful completion or user chose not to retry
            */
            if (!automatedMode)
            {
                Console.WriteLine();
                Console.Write("Press any key to exit...");
                Console.ReadKey(true);
                Console.WriteLine();
            }

            return installationSuccessful
                ? (int)InstallationResultCode.Success
                : (int)InstallationResultCode.PartialInstallation;
        }

        /*
        Ask user if they want to retry or exit
        Returns true to retry, false to exit
        */
        private static bool PromptRetryOrExit()
        {
            Console.WriteLine();
            Console.Write("Y to retry installation, N to exit: ");
            string? response = Console.ReadLine();
            return response?.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase) ?? false;
        }

        /// <summary>
        /// Performs a complete uninstall: stops traces, removes jobs, XE sessions, and database.
        /// </summary>
        private static async Task<int> PerformUninstallAsync(string connectionString, bool automatedMode)
        {
            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine("UNINSTALL MODE");
            Console.WriteLine("================================================================================");
            Console.WriteLine();

            if (!automatedMode)
            {
                Console.WriteLine("This will remove:");
                Console.WriteLine("  - SQL Agent jobs (Collection, Data Retention, Hung Job Monitor)");
                Console.WriteLine("  - Extended Events sessions (BlockedProcess, Deadlock)");
                Console.WriteLine("  - Server-side traces");
                Console.WriteLine("  - PerformanceMonitor database and ALL collected data");
                Console.WriteLine();
                Console.Write("Are you sure you want to continue? (Y/N, default N): ");
                string? confirm = Console.ReadLine();
                if (!confirm?.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase) ?? true)
                {
                    Console.WriteLine("Uninstall cancelled.");
                    WaitForExit();
                    return (int)InstallationResultCode.Success;
                }
            }

            Console.WriteLine();
            Console.WriteLine("Uninstalling Performance Monitor...");

            try
            {
                await InstallationService.ExecuteUninstallAsync(
                    connectionString,
                    new Progress<InstallationProgress>(p =>
                    {
                        switch (p.Status)
                        {
                            case "Success":
                                WriteSuccess(p.Message);
                                break;
                            case "Error":
                                WriteError(p.Message);
                                break;
                            case "Warning":
                                WriteWarning(p.Message);
                                break;
                            case "Info":
                                Console.WriteLine(p.Message);
                                break;
                            case "Debug":
                                break;
                            default:
                                Console.WriteLine(p.Message);
                                break;
                        }
                    })).ConfigureAwait(false);

                Console.WriteLine();
                WriteSuccess("Uninstall completed successfully");
                Console.WriteLine();
                Console.WriteLine("Note: blocked process threshold (s) was NOT reset.");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"Uninstall failed: {ex.Message}");
                if (!automatedMode)
                {
                    WaitForExit();
                }
                return (int)InstallationResultCode.UninstallFailed;
            }

            if (!automatedMode)
            {
                WaitForExit();
            }
            return (int)InstallationResultCode.Success;
        }

        /// <summary>
        /// Runs installation diagnostics (99_installer_troubleshooting.sql) against the server and
        /// prints the [OK]/[WARN]/[ERROR] results. Exit code 0 when no errors are found, otherwise 10.
        /// </summary>
        private static async Task<int> PerformTroubleshootAsync(string connectionString, ScriptProvider scriptProvider, bool automatedMode)
        {
            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine("TROUBLESHOOT MODE");
            Console.WriteLine("================================================================================");
            Console.WriteLine();
            Console.WriteLine("Running installation diagnostics (99_installer_troubleshooting.sql)...");
            Console.WriteLine();

            bool noErrors;
            try
            {
                noErrors = await InstallationService.RunTroubleshootingAsync(
                    connectionString,
                    scriptProvider,
                    new Progress<InstallationProgress>(p =>
                    {
                        switch (p.Status)
                        {
                            case "Success":
                                WriteSuccess(p.Message);
                                break;
                            case "Error":
                                WriteError(p.Message);
                                break;
                            case "Warning":
                                WriteWarning(p.Message);
                                break;
                            case "Info":
                                Console.WriteLine(p.Message);
                                break;
                            case "Debug":
                                break;
                            default:
                                Console.WriteLine(p.Message);
                                break;
                        }
                    })).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"Diagnostics failed: {ex.Message}");
                if (!automatedMode)
                {
                    WaitForExit();
                }
                return (int)InstallationResultCode.DiagnosticsFailed;
            }

            Console.WriteLine();
            if (noErrors)
            {
                WriteSuccess("Diagnostics completed: no errors found.");
            }
            else
            {
                WriteWarning("Diagnostics completed: issues were reported above ([WARN]/[ERROR]).");
            }

            if (!automatedMode)
            {
                WaitForExit();
            }
            return noErrors ? (int)InstallationResultCode.Success : (int)InstallationResultCode.DiagnosticsFailed;
        }

        /*
        Write error log file for bug reporting
        Returns the path to the log file
        */
        private static string WriteErrorLog(Exception ex, string serverName, string installerVersion)
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string sanitizedServer = SanitizeFilename(serverName);
            string fileName = $"PerformanceMonitor_Error_{sanitizedServer}_{timestamp}.log";
            string logPath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

            var sb = new System.Text.StringBuilder();

            sb.AppendLine("================================================================================");
            sb.AppendLine("Performance Monitor Installer - Error Log");
            sb.AppendLine("================================================================================");
            sb.AppendLine();
            sb.AppendLine($"Timestamp:         {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Installer Version: {installerVersion}");
            sb.AppendLine($"Server:            {serverName}");
            sb.AppendLine($"Machine:           {Environment.MachineName}");
            sb.AppendLine($"User:              {Environment.UserName}");
            sb.AppendLine($"OS:                {Environment.OSVersion}");
            sb.AppendLine($".NET Version:      {Environment.Version}");
            sb.AppendLine();
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine("ERROR DETAILS");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"Type:    {ex.GetType().FullName}");
            sb.AppendLine($"Message: {ex.Message}");
            sb.AppendLine();

            if (ex.InnerException != null)
            {
                sb.AppendLine("Inner Exception:");
                sb.AppendLine($"  Type:    {ex.InnerException.GetType().FullName}");
                sb.AppendLine($"  Message: {ex.InnerException.Message}");
                sb.AppendLine();
            }

            sb.AppendLine("Stack Trace:");
            sb.AppendLine(ex.StackTrace ?? "(not available)");
            sb.AppendLine();

            if (ex.InnerException?.StackTrace != null)
            {
                sb.AppendLine("Inner Exception Stack Trace:");
                sb.AppendLine(ex.InnerException.StackTrace);
                sb.AppendLine();
            }

            sb.AppendLine("================================================================================");
            sb.AppendLine("Please include this file when reporting issues at:");
            sb.AppendLine("https://github.com/erikdarlingdata/PerformanceMonitor/issues");
            sb.AppendLine("================================================================================");

            File.WriteAllText(logPath, sb.ToString());

            return logPath;
        }

        /*
        Read an option value supporting both "--opt=value" and "--opt value" forms.
        Returns null when the option is absent or has no value. A value that
        itself starts with "--" (i.e., the next flag) is treated as absent.
        */
        private static string? GetOptionValue(string[] args, string optionName)
        {
            string prefix = optionName + "=";
            var equalsForm = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (equalsForm != null)
            {
                return equalsForm.Substring(prefix.Length);
            }

            int index = Array.FindIndex(args, a => a.Equals(optionName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return args[index + 1];
            }

            return null;
        }

        /*
        Sanitize a string for use in a filename
        Replaces invalid characters with underscores
        */
        private static string SanitizeFilename(string input)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Concat(input.Select(c => invalid.Contains(c) ? '_' : c));
        }

        /*
        The flags the positional-argument filter knows to skip. Kept only to DIAGNOSE the dropped-password
        trap: a "--"-token that is not one of these was almost certainly a value the filter silently ate.
        If a new flag is added and not listed here, the only cost is a spurious hint in an error path -- so
        this is a diagnostic aid, not a parsing authority (the actual flag handling matches each flag by
        name inline in Main).
        */
        private static readonly string[] RecognizedFlags =
        {
            "--data-path", "--encrypt", "--entra", "--help", "--log-path", "--managed-identity",
            "--reinstall", "--repair", "--reset-schedule", "--service-principal", "--troubleshoot",
            "--trust-cert", "--uninstall"
        };

        private static bool HasUnrecognizedDoubleDashArg(string[] args)
        {
            foreach (string arg in args)
            {
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                /* Compare the flag name only, so the --flag=value form is matched too. */
                string flagName = arg.Split('=')[0];
                if (!RecognizedFlags.Contains(flagName, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /*
        Wait for user input before exiting (prevents window from closing)
        Used for fatal errors where retry doesn't make sense
        */
        private static void WaitForExit()
        {
            Console.WriteLine();
            Console.Write("Press any key to exit...");
            Console.ReadKey(true);
            Console.WriteLine();
        }

        /*
        Read password from console, displaying asterisks
        */
        private static string ReadPassword()
        {
            string password = string.Empty;
            ConsoleKeyInfo key;

            do
            {
                key = Console.ReadKey(true);

                if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                {
                    password = password.Substring(0, password.Length - 1);
                    Console.Write("\b \b");
                }
                else if (key.Key != ConsoleKey.Enter && !char.IsControl(key.KeyChar))
                {
                    password += key.KeyChar;
                    Console.Write("*");
                }
            } while (key.Key != ConsoleKey.Enter);

            return password;
        }

        private static void WriteSuccess(string message)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("√ ");
            Console.ResetColor();
            Console.WriteLine(message);
        }

        private static void WriteError(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("✗ ");
            Console.ResetColor();
            Console.WriteLine(message);
        }

        private static void WriteWarning(string message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("! ");
            Console.ResetColor();
            Console.WriteLine(message);
        }

        private static async Task CheckForInstallerUpdateAsync(string currentVersion)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                client.DefaultRequestHeaders.Add("User-Agent", "PerformanceMonitor");
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

                var response = await client.GetAsync(
                    "https://api.github.com/repos/erikdarlingdata/PerformanceMonitor/releases/latest")
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode) return;

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                var versionString = tagName.TrimStart('v', 'V');

                if (!Version.TryParse(versionString, out var latest)) return;
                if (!Version.TryParse(currentVersion, out var current)) return;

                if (latest > current)
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
                    Console.WriteLine($"║  A newer version ({tagName}) is available!                          ");
                    Console.WriteLine("║  https://github.com/erikdarlingdata/PerformanceMonitor/releases     ");
                    Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
                    Console.ResetColor();
                    Console.WriteLine();
                }
            }
            catch
            {
                /* Best effort — don't block installation if GitHub is unreachable */
            }
        }
    }
}
