/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Collectors;

/// <summary>
/// What a definition may need to know about the monitored server to build its query.
/// Grown deliberately as the sweep demands (engine edition today; version gates arrive with
/// the collectors that need them) — every added field is parity-critical target logic.
/// </summary>
public sealed class CollectorTargetInfo
{
    /// <summary>
    /// Which database engine this target actually is. Defaults to
    /// <see cref="CollectorTargetEngine.SqlServer"/>, so every target the probes classify today —
    /// and every bare <c>new CollectorTargetInfo()</c> in a test — keeps its present behaviour.
    /// <para>A definition is only dispatched when its <see cref="ICollectorSchemaInfo.TargetEngine"/>
    /// matches this; see <see cref="CollectorCatalog.AppliesTo(ICollectorSchemaInfo, CollectorTargetInfo)"/>.
    /// The SQL Server hosting flags below (<see cref="IsAzureSqlDb"/> and friends) are meaningful
    /// only when this is <see cref="CollectorTargetEngine.SqlServer"/>.</para>
    /// </summary>
    public CollectorTargetEngine Engine { get; init; } = CollectorTargetEngine.SqlServer;

    /// <summary>True when the target is Azure SQL Database (engine edition 5).</summary>
    public bool IsAzureSqlDb { get; init; }

    /// <summary>True when the target is Azure SQL Managed Instance (engine edition 8).</summary>
    public bool IsAzureManagedInstance { get; init; }

    /// <summary>
    /// True when the target is an Amazon RDS for SQL Server instance (detected via
    /// <c>DB_ID('rdsadmin') IS NOT NULL</c>). RDS does not expose the underlying OS, so DMVs that
    /// read OS/service state — notably <c>sys.dm_server_services</c> (used by agent_status) — and the
    /// restricted msdb surface running_jobs needs (<c>msdb.dbo.syssessions</c>) are unavailable there.
    /// Definitions gate those collectors off via <see cref="AppliesTo"/> so both hosts skip them.
    /// </summary>
    public bool IsAwsRds { get; init; }

    /// <summary>
    /// SQL Server major version (13 = 2016 … 17 = 2025); 0 when unknown. Definitions gate
    /// version-specific columns on this (database_config treats 0 as "assume newest" to match
    /// the original collector).
    /// </summary>
    public int SqlMajorVersion { get; init; }

    /// <summary>
    /// True when the monitored login can read msdb (<c>HAS_DBACCESS('msdb') = 1</c>). The SQL-Agent
    /// collectors — running_jobs, job_history, agent_status — read <c>msdb.dbo.sysjobs</c>,
    /// <c>sysjobhistory</c>, <c>sysjobschedules</c>, etc., so each gates off via <see cref="AppliesTo"/>
    /// when this is false; a login without msdb access would otherwise fail every cycle (error 229/916)
    /// and pollute collection-health. Both hosts probe this (Lite's ServerManager and Darling's
    /// DarlingServerConnector, verbatim <c>HAS_DBACCESS(N'msdb')</c>) and wire it in here.
    /// <para>Defaults to <c>true</c> so a target the probe never classified (the SqlMajorVersion == 0 /
    /// unknown path, and every bare <c>new CollectorTargetInfo()</c>) still attempts the Agent
    /// collectors — matching the probe's own NULL-means-assume-access default, so "unknown" never
    /// silently gates collection off.</para>
    /// </summary>
    public bool HasMsdbAccess { get; init; } = true;
}
