/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// Which of <see cref="ViewerSettings.ResolveConfigPath"/>'s rules produced the path the viewer read.
/// Reported verbatim in the startup diagnostics (#1954): "it read the wrong file" is only diagnosable
/// when the viewer says WHICH rule won, because the four candidates are indistinguishable from the path
/// alone (an operator who set DARLING_CONFIG and also has a darling.json beside the binary cannot
/// otherwise tell which one the viewer is honoring).
/// </summary>
public enum ViewerConfigSource
{
    /// <summary>The first non-option command-line argument — outranks everything.</summary>
    CommandLine,

    /// <summary>The <c>DARLING_CONFIG</c> environment variable.</summary>
    EnvironmentVariable,

    /// <summary>darling.json beside the viewer binary (also the reported path when nothing was found).</summary>
    BesideViewer,

    /// <summary>darling.json one level up — the release zip's viewer\ under the service root.</summary>
    ServiceRoot,
}

/// <summary>
/// Where the viewer's darling.json came from: the rule that won, the path it named (verbatim and
/// fully resolved), and whether that file exists. Produced by
/// <see cref="ViewerSettings.ResolveConfigLocation"/>, which IS the resolver — <see cref="ViewerSettings.ResolveConfigPath"/>
/// delegates to it, so the diagnostics can never describe a different resolution than the one performed.
/// </summary>
public sealed class ViewerConfigLocation
{
    internal ViewerConfigLocation(string path, ViewerConfigSource source, bool exists)
    {
        Path = path;
        Source = source;
        Exists = exists;

        /* The operator-supplied paths (command line / DARLING_CONFIG) can be relative, and a relative path
           is exactly the case where "which file did you read?" is unanswerable — so carry the absolute form
           too. GetFullPath throws on a syntactically invalid path (illegal characters, a bare drive-relative
           form we cannot expand); a diagnostic must never be the thing that crashes startup, so fall back to
           the verbatim path. */
        try
        {
            FullPath = System.IO.Path.GetFullPath(path);
        }
        catch (Exception)
        {
            FullPath = path;
        }
    }

    /// <summary>The path as the winning rule produced it (verbatim for an operator-supplied path).</summary>
    public string Path { get; }

    /// <summary>The absolute form of <see cref="Path"/> (falls back to <see cref="Path"/> if it cannot be expanded).</summary>
    public string FullPath { get; }

    /// <summary>Which resolution rule produced <see cref="Path"/>.</summary>
    public ViewerConfigSource Source { get; }

    /// <summary>Whether the file at <see cref="Path"/> exists.</summary>
    public bool Exists { get; }
}

/// <summary>
/// The viewer's read of darling.json — the SAME file the Darling service uses, but the viewer
/// only needs the postgres section (it talks to the central store, never to monitored
/// SQL Servers). This intentionally duplicates a sliver of the service's DarlingConfig — the
/// resolution order and the postgres section — rather than referencing the service project;
/// a shared config library is not warranted for one section yet.
/// Resolution order matches the service — explicit path → DARLING_CONFIG environment
/// variable → darling.json next to the binary — plus one viewer-only fallback: the parent
/// directory, because the release zip puts the viewer in a viewer\ subfolder under the
/// service root where the operator's darling.json lives. Comments and trailing commas are
/// allowed, property names are case-insensitive.
/// Managed mode (<c>postgres.managed = true</c>, the shipped default): the SERVICE bootstraps
/// and owns the bundled Postgres; the viewer only derives the same connection string —
/// 127.0.0.1 + port + the darling user with the password the service generated on first run,
/// unprotected from the DPAPI-LocalMachine credential file beside the data directory. The
/// derivation constants (user/database name, credential path convention, DPAPI entropy) are
/// the service's <c>DarlingManagedPostgres</c>/<c>DarlingSecrets</c> values, duplicated under
/// the same sliver rule and pinned against the service by a Darling.Tests round-trip test.
/// </summary>
public sealed class ViewerSettings
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /* The service's DarlingSecrets entropy — must match byte-for-byte or the viewer cannot
       read the managed credential (pinned by ViewerSettingsTests). */
    private static readonly byte[] s_dpapiEntropy = Encoding.UTF8.GetBytes("PerformanceMonitor.Darling.v1");

    /* The service's DarlingManagedPostgres least-privilege role + credential constants, duplicated
       under the same sliver rule as the entropy above (the viewer does not reference the Service
       project) and pinned against it by ViewerSettingsTests. The Viewer drops from the superuser to
       one of these roles: admin (the default — reads both schemas + writes the config tables the mute
       / dismiss / analysis-mute surfaces need) or viewer (read-only). SearchPath resolves the bare
       table names to the V8 collect/config schemas on every connection. */
    private const string AdminRoleName = "admin";
    private const string ViewerRoleName = "viewer";
    private const string AdminCredentialFileName = "pg-admin-credential.dpapi";
    private const string ViewerCredentialFileName = "pg-viewer-credential.dpapi";
    private const string ManagedSearchPath = "collect,config,public";

    public string ConnectionString { get; }

    /// <summary>
    /// True when the connection string was DERIVED by the viewer from <c>postgres.managed = true</c> rather
    /// than read out of the file. Reported in the startup diagnostics (#1954), because it decides whether
    /// <c>postgres.connectionString</c> in the file is even being consulted — an operator editing that
    /// property on a managed install is editing something nothing reads.
    /// </summary>
    public bool Managed { get; }

    private ViewerSettings(string connectionString, bool managed)
    {
        ConnectionString = connectionString;
        Managed = managed;
    }

    /// <param name="explicitPath">A path handed on the command line; wins outright.</param>
    /// <param name="baseDirectory">The viewer binary's directory; null means AppContext.BaseDirectory (tests pass a temp directory).</param>
    public static string ResolveConfigPath(string? explicitPath = null, string? baseDirectory = null) =>
        ResolveConfigLocation(explicitPath, baseDirectory).Path;

    /// <summary>
    /// The resolver itself: the same explicit path → <c>DARLING_CONFIG</c> → beside-the-viewer →
    /// service-root order, but reporting WHICH rule won and whether the file exists, for the startup
    /// diagnostics (#1954). <see cref="ResolveConfigPath"/> is a projection of this, so the diagnostics and
    /// the load can never disagree about which file was chosen.
    /// </summary>
    /// <param name="explicitPath">A path handed on the command line; wins outright.</param>
    /// <param name="baseDirectory">The viewer binary's directory; null means AppContext.BaseDirectory (tests pass a temp directory).</param>
    public static ViewerConfigLocation ResolveConfigLocation(string? explicitPath = null, string? baseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return new ViewerConfigLocation(explicitPath, ViewerConfigSource.CommandLine, File.Exists(explicitPath));
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("DARLING_CONFIG");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return new ViewerConfigLocation(
                fromEnvironment, ViewerConfigSource.EnvironmentVariable, File.Exists(fromEnvironment));
        }

        baseDirectory ??= AppContext.BaseDirectory;
        var besideBinary = Path.Combine(baseDirectory, "darling.json");
        if (File.Exists(besideBinary))
        {
            return new ViewerConfigLocation(besideBinary, ViewerConfigSource.BesideViewer, true);
        }

        /* The release zip ships the viewer in a viewer\ subfolder under the service root, and
           the operator's darling.json lives beside the SERVICE exe — so when there is nothing
           beside the viewer, probe one level up. Miss both and we still return the
           beside-binary path, so the not-found hint names the viewer's own directory. */
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)));
        if (!string.IsNullOrEmpty(parent))
        {
            var besideService = Path.Combine(parent, "darling.json");
            if (File.Exists(besideService))
            {
                return new ViewerConfigLocation(besideService, ViewerConfigSource.ServiceRoot, true);
            }
        }

        return new ViewerConfigLocation(besideBinary, ViewerConfigSource.BesideViewer, false);
    }

    /// <summary>
    /// Loads the resolved config file, or returns null when it does not exist (the window shows
    /// the copy-the-sample hint instead of crashing). Malformed content still throws — the
    /// caller turns that into a readable message too.
    /// </summary>
    public static ViewerSettings? TryLoad(string? explicitPath = null)
    {
        var path = ResolveConfigPath(explicitPath);
        if (!File.Exists(path))
        {
            return null;
        }

        return Parse(File.ReadAllText(path));
    }

    public static ViewerSettings Parse(string json)
    {
        var config = JsonSerializer.Deserialize<ConfigDto>(json, s_jsonOptions);

        if (config?.Postgres?.Managed == true)
        {
            return new ViewerSettings(DeriveManagedConnectionString(config.Postgres), managed: true);
        }

        var connectionString = config?.Postgres?.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidDataException("darling.json has no postgres.connectionString (and postgres.managed is not true).");
        }

        return new ViewerSettings(connectionString, managed: false);
    }

    /// <summary>
    /// The managed-mode derivation, mirroring the service: data directory = configured or
    /// %ProgramData%\PerformanceMonitorDarling\pg, and connection = 127.0.0.1 + port + the
    /// least-privilege role <c>postgres.connectAs</c> selects (admin by default, or viewer),
    /// authenticated with that role's DPAPI credential file beside the data directory and carrying
    /// the collect/config search path. The Viewer never connects as the superuser <c>darling</c> —
    /// the service provisions and owns that. A missing credential means the service has not completed
    /// its first run (which provisions the roles + writes their credentials) yet — a readable message,
    /// because the main window shows parse failures to the user.
    /// </summary>
    private static string DeriveManagedConnectionString(PostgresDto postgres)
    {
        var dataDirectory = string.IsNullOrWhiteSpace(postgres.DataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PerformanceMonitorDarling", "pg")
            : Path.GetFullPath(postgres.DataDirectory);

        /* connectAs picks the role + its credential; unknown values fall back to admin (the service's
           DarlingConfig default), so a typo degrades to the writable role, not to a crash. */
        var wantsViewer = string.Equals(postgres.ConnectAs?.Trim(), ViewerRoleName, StringComparison.OrdinalIgnoreCase);
        var role = wantsViewer ? ViewerRoleName : AdminRoleName;
        var credentialFileName = wantsViewer ? ViewerCredentialFileName : AdminCredentialFileName;

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dataDirectory));
        var credentialPath = string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, credentialFileName);
        if (credentialPath is null || !File.Exists(credentialPath))
        {
            throw new InvalidDataException(
                "darling.json uses the managed Postgres (postgres.managed = true), but the credential file for the " +
                $"'{role}' role ({credentialPath ?? "beside " + dataDirectory}) does not exist yet. Start the " +
                "PerformanceMonitor Darling service once — its first run initializes the bundled Postgres and " +
                "provisions the least-privilege roles and their credentials.");
        }

        var protectedBytes = Convert.FromBase64String(File.ReadAllText(credentialPath).Trim());
        var password = Encoding.UTF8.GetString(
            ProtectedData.Unprotect(protectedBytes, s_dpapiEntropy, DataProtectionScope.LocalMachine));

        var builder = new NpgsqlConnectionStringBuilder
        {
            /* 127.0.0.1, not "localhost", mirroring the service's DarlingManagedPostgres.BuildConnectionString
               (the parity half of the same pair): a literal IPv4 loopback avoids a localhost->::1 resolution
               that would miss the managed cluster's IPv4-only listen_addresses (it binds 127.0.0.1, not ::1;
               initdb's pg_hba ships a ::1 rule but there is no ::1 listener). */
            Host = "127.0.0.1",
            Port = postgres.Port,
            Username = role,
            Password = password,
            Database = "darling",
            SearchPath = ManagedSearchPath,
            /* #1566: bound the viewer seat's backend count (the service's pools were capped at 24 in
               #1559, but this string was built independently and rode Npgsql's default of 100). Every
               pooled connection is a live postgres.exe on Windows; a read-only UI seat polling on 30/60s
               timers needs a handful, not a hundred. */
            MaxPoolSize = 10,
        };
        return builder.ConnectionString;
    }

    private sealed class ConfigDto
    {
        [JsonPropertyName("postgres")]
        public PostgresDto? Postgres { get; set; }
    }

    private sealed class PostgresDto
    {
        [JsonPropertyName("connectionString")]
        public string? ConnectionString { get; set; }

        [JsonPropertyName("managed")]
        public bool Managed { get; set; }

        [JsonPropertyName("port")]
        public int Port { get; set; } = 5641;

        [JsonPropertyName("dataDirectory")]
        public string? DataDirectory { get; set; }

        /// <summary>Which least-privilege role to connect as: "admin" (default) or "viewer" (read-only).</summary>
        [JsonPropertyName("connectAs")]
        public string? ConnectAs { get; set; }
    }
}
