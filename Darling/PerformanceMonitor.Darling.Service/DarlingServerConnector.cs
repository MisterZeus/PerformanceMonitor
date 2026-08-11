/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Npgsql;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// Per-server runtime state the collection loop carries: the resolved connection string, the
/// probed target facts (engine edition, major version — the same detection Lite's ServerManager
/// runs), and the shared-identity server id.
/// </summary>
public sealed class ServerRuntime
{
    public required MonitoredServer Config { get; init; }

    public required string ConnectionString { get; init; }

    public required CollectorTargetInfo Target { get; init; }

    /// <summary>host[:database][:RO] — the shared identity rule, hashed to <see cref="ServerId"/>.</summary>
    public required string StorageName { get; init; }

    public required int ServerId { get; init; }

    public bool HasMsdbAccess { get; init; }

    public bool IsAwsRds { get; init; }

    /// <summary>
    /// The raw SERVERPROPERTY('EngineEdition') value from the detection probe — 1 Personal,
    /// 2 Standard, 3 Enterprise, 4 Express, 5 Azure SQL DB, 8 Managed Instance, etc. — carried
    /// whole so the servers registry records the real edition, not just the 5/8 classification
    /// booleans on <see cref="Target"/>.
    /// </summary>
    public int EngineEdition { get; init; }
}

/// <summary>
/// Opens the first connection to a monitored server and probes the target facts the collector
/// definitions branch on. The detection query is verbatim from Lite's ServerManager connectivity
/// check, so both SKUs classify a server identically.
/// </summary>
public static class DarlingServerConnector
{
    /* The scalar detection query - verbatim (modulo whitespace) from Lite's ServerManager
       connectivity check. Deliberately NO FROM sys.dm_os_sys_info: that DMV requires VIEW DATABASE
       STATE, which an Azure SQL DB monitoring login often lacks, so edition detection must not
       depend on it (#1535). sqlserver_start_time - the one column that needs the DMV - is not read
       here (the service never surfaces a start time), so unlike Lite/Dashboard no best-effort
       start-time read is needed. Columns: 0 sql_version, 1 major_version, 2 utc_offset,
       3 engine_edition, 4 is_aws_rds, 5 has_msdb_access. */
    public const string DetectionQueryText = @"
SELECT
    @@VERSION AS sql_version,
    CONVERT(integer, SERVERPROPERTY('ProductMajorVersion')) AS major_version,
    DATEDIFF(MINUTE, GETUTCDATE(), GETDATE()) AS utc_offset_minutes,
    CONVERT(integer, SERVERPROPERTY('EngineEdition')) AS engine_edition,
    CASE WHEN DB_ID('rdsadmin') IS NOT NULL THEN 1 ELSE 0 END AS is_aws_rds,
    HAS_DBACCESS(N'msdb') AS has_msdb_access";

    public static string ResolveConnectionString(MonitoredServer config, ILogger? logger = null)
    {
        string? password = null;
        if (config.UsesSqlAuth)
        {
            bool usedPlaintext;
            if (OperatingSystem.IsWindows())
            {
                password = DarlingSecrets.ResolvePassword(config, out usedPlaintext);
            }
            else
            {
                /* Non-Windows: DPAPI (DarlingSecrets) is unavailable, so only the password slot applies —
                   inlined here to keep the DPAPI call provably Windows-only for the platform analyzer.
                   The slot takes the same env:/file: references as everywhere else (#1804), which is the
                   supported non-Windows shape; a literal still works and still warns below. */
                if (!string.IsNullOrWhiteSpace(config.EncryptedPassword))
                {
                    throw new PlatformNotSupportedException(
                        "encryptedPassword requires Windows (DPAPI); use password with an env:/file: reference on other platforms.");
                }

                if (string.IsNullOrWhiteSpace(config.Password))
                {
                    throw new InvalidOperationException(
                        $"Server '{config.DisplayName}' uses sql auth but has neither encryptedPassword nor password.");
                }

                usedPlaintext = !DarlingSecretSource.IsReference(config.Password);
                password = DarlingSecretSource.Resolve(config.Password, $"servers['{config.DisplayName}'].password");
            }

            if (usedPlaintext)
            {
                logger?.LogWarning(
                    "Server '{Server}' uses a plaintext password in darling.json — run --encrypt-password and switch to encryptedPassword, or reference it via env:/file:.",
                    config.DisplayName);
            }
        }

        return MonitoredServerConnection.BuildConnectionString(config, password);
    }

    /* The PostgreSQL detection query. Deliberately built only from surfaces a pg_monitor-grade login
       can read on Amazon Aurora, verified against live 16.11 and 17.7 clusters:

         current_setting('server_version_num') -> 160011 / 170007, so the major is a division rather
           than string parsing (version() text formatting has changed across releases).
         pg_is_in_recovery()                   -> reader vs writer. On Aurora every reader endpoint is
           its own instance with its own statistics, so this is identity, not a routing hint.
         aurora_version()                      -> present only on Aurora. Wrapped: on stock PostgreSQL
           the function does not exist, and a missing function must read as "not Aurora" rather than
           failing the whole probe.

       No timezone offset column: unlike SQL Server's DATEDIFF-on-GETDATE idiom, Postgres timestamps
       here are read as-is and the store's convention is naive UTC either way. */
    public const string PostgresDetectionQueryText = @"
SELECT
    version() AS server_version_text,
    current_setting('server_version_num')::int / 10000 AS major_version,
    pg_is_in_recovery() AS is_in_recovery,
    (SELECT count(*) FROM pg_proc WHERE proname = 'aurora_version') > 0 AS has_aurora_marker,
    current_setting('server_version_num')::int AS server_version_num";

    /// <summary>Connects, probes, and returns the runtime state for one configured server.</summary>
    public static async Task<ServerRuntime> ConnectAsync(MonitoredServer config, ILogger? logger, CancellationToken cancellationToken)
    {
        if (config.IsPostgres)
        {
            return await ConnectPostgresAsync(config, logger, cancellationToken);
        }

        var connectionString = ResolveConnectionString(config, logger);
        var storageName = config.StorageName;

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = new SqlCommand(DetectionQueryText, connection) { CommandTimeout = 30 };
        using var reader = await command.ExecuteReaderAsync(cancellationToken);

        int majorVersion = 0, engineEdition = 0;
        bool isAwsRds = false, hasMsdbAccess = true;
        if (await reader.ReadAsync(cancellationToken))
        {
            // Column indices per DetectionQueryText: 1 major_version, 3 engine_edition,
            // 4 is_aws_rds, 5 has_msdb_access (sqlserver_start_time was dropped in #1535).
            majorVersion = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            engineEdition = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            isAwsRds = !reader.IsDBNull(4) && reader.GetInt32(4) == 1;
            hasMsdbAccess = reader.IsDBNull(5) || reader.GetInt32(5) == 1;
        }

        return new ServerRuntime
        {
            Config = config,
            ConnectionString = connectionString,
            Target = new CollectorTargetInfo
            {
                IsAzureSqlDb = engineEdition == 5,
                IsAzureManagedInstance = engineEdition == 8,
                IsAwsRds = isAwsRds,
                SqlMajorVersion = majorVersion,
                /* Already probed above via HAS_DBACCESS(N'msdb'); wiring it into the gate is the fix —
                   before this it rode only on ServerRuntime and never reached the collectors' AppliesTo,
                   so Darling attempted running_jobs/job_history/agent_status every cycle on a no-msdb login. */
                HasMsdbAccess = hasMsdbAccess,
            },
            StorageName = storageName,
            ServerId = ServerIdHelper.GetDeterministicHashCode(storageName),
            HasMsdbAccess = hasMsdbAccess,
            IsAwsRds = isAwsRds,
            EngineEdition = engineEdition,
        };
    }

    /// <summary>
    /// The PostgreSQL connect-and-probe. Same contract as the SQL Server path — open, probe, return a
    /// <see cref="ServerRuntime"/> whose <see cref="CollectorTargetInfo"/> is what the collectors' gate
    /// reads — with the SQL Server-only facts left at their defaults.
    /// <para><c>HasMsdbAccess</c> stays <c>true</c> and the Azure flags stay <c>false</c> because they are
    /// meaningless here; no Postgres definition consults them, and the engine check in
    /// <see cref="CollectorCatalog.AppliesTo(ICollectorSchemaInfo, CollectorTargetInfo)"/> keeps every
    /// T-SQL definition away from this target regardless of their values.</para>
    /// </summary>
    private static async Task<ServerRuntime> ConnectPostgresAsync(
        MonitoredServer config, ILogger? logger, CancellationToken cancellationToken)
    {
        var connectionString = ResolveConnectionString(config, logger);
        var storageName = config.StorageName;

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = new NpgsqlCommand(PostgresDetectionQueryText, connection) { CommandTimeout = 30 };
        using var reader = await command.ExecuteReaderAsync(cancellationToken);

        int majorVersion = 0, versionNum = 0;
        bool isInRecovery = false, isAurora = false;
        string versionText = "";
        if (await reader.ReadAsync(cancellationToken))
        {
            versionText = reader.IsDBNull(0) ? "" : reader.GetString(0);
            majorVersion = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            isInRecovery = !reader.IsDBNull(2) && reader.GetBoolean(2);
            isAurora = !reader.IsDBNull(3) && reader.GetBoolean(3);
            versionNum = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
        }

        logger?.LogInformation(
            "Connected to PostgreSQL target '{Server}': major {Major} (server_version_num {Num}), {Role}, Aurora: {Aurora} — {VersionText}",
            config.DisplayName, majorVersion, versionNum, isInRecovery ? "reader (in recovery)" : "writer", isAurora,
            versionText);

        /* A Postgres target reached through the SQL Server path would have failed on the detection
           query, so an engine mismatch is loud. The reverse — a SQL Server host configured as
           "postgres" — fails at connect, which is equally loud. */
        return new ServerRuntime
        {
            Config = config,
            ConnectionString = connectionString,
            Target = new CollectorTargetInfo
            {
                Engine = CollectorTargetEngine.PostgreSql,
                PostgresMajorVersion = majorVersion,
                PostgresVersionNum = versionNum,
                IsAurora = isAurora,
                IsInRecovery = isInRecovery,
            },
            StorageName = storageName,
            ServerId = ServerIdHelper.GetDeterministicHashCode(storageName),
        };
    }

    /// <summary>
    /// Non-throwing connect-and-probe: runs <see cref="ConnectAsync"/> and packages the outcome as a
    /// <see cref="ConnectionProbeResult"/> — success carries the probed version/edition/engine facts, a
    /// failure carries the error message (never plaintext credentials). Shared by the <c>test_connect</c>
    /// command (the Stage-3 Add-dialog validates a server BEFORE saving; the SERVICE holds the network
    /// path + credentials) and the <c>--test-connection</c>/<c>--validate-config</c> CLI verb, so both
    /// classify a server identically. <see cref="OperationCanceledException"/> propagates (shutdown).
    /// </summary>
    public static async Task<ConnectionProbeResult> ProbeAsync(MonitoredServer config, ILogger? logger, CancellationToken cancellationToken)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        try
        {
            var runtime = await ConnectAsync(config, logger, cancellationToken);
            return new ConnectionProbeResult(
                Success: true,
                MajorVersion: runtime.Target.SqlMajorVersion,
                EngineEdition: runtime.EngineEdition,
                EngineEditionDescription: DescribeEngineEdition(runtime.EngineEdition),
                IsAzureSqlDb: runtime.Target.IsAzureSqlDb,
                IsAzureManagedInstance: runtime.Target.IsAzureManagedInstance,
                IsAwsRds: runtime.IsAwsRds,
                HasMsdbAccess: runtime.HasMsdbAccess,
                Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ConnectionProbeResult(
                Success: false,
                MajorVersion: 0,
                EngineEdition: 0,
                EngineEditionDescription: null,
                IsAzureSqlDb: false,
                IsAzureManagedInstance: false,
                IsAwsRds: false,
                HasMsdbAccess: false,
                Error: ex.Message);
        }
    }

    /// <summary>Human-readable SERVERPROPERTY('EngineEdition') description for the probe result.</summary>
    public static string DescribeEngineEdition(int engineEdition) => engineEdition switch
    {
        1 => "Personal/Desktop",
        2 => "Standard",
        3 => "Enterprise",
        4 => "Express",
        5 => "Azure SQL Database",
        6 => "Azure Synapse Analytics",
        8 => "Azure SQL Managed Instance",
        9 => "Azure SQL Edge",
        11 => "Azure Synapse serverless SQL pool",
        _ => $"Unknown ({engineEdition})",
    };
}

/// <summary>
/// The outcome of a connect-and-probe attempt (<see cref="DarlingServerConnector.ProbeAsync"/>): the
/// success flag plus the probed target facts, or the error message on failure. Deliberately carries NO
/// credentials so it is safe to serialize into <c>config_command.result_json</c> and print from the CLI.
/// </summary>
public sealed record ConnectionProbeResult(
    bool Success,
    int MajorVersion,
    int EngineEdition,
    string? EngineEditionDescription,
    bool IsAzureSqlDb,
    bool IsAzureManagedInstance,
    bool IsAwsRds,
    bool HasMsdbAccess,
    string? Error);
