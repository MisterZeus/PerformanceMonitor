/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// Availability Group DATABASE health — one row per database per replica, from
/// sys.dm_hadr_database_replica_states joined to sys.availability_replicas,
/// sys.availability_groups and sys.databases (#991). The database-grain half of the AG surface;
/// <see cref="AgReplicaStatesCollector"/> carries the replica grain.
///
/// <para>Metric surface follows Hannah Vernon's SqlServerAgMonitor (MIT),
/// https://github.com/HannahVernon/SqlServerAgMonitor.</para>
///
/// <para>The queue sizes and rates are INSTANTANEOUS GAUGES, not cumulative counters — the DMV
/// recomputes each from the current backlog, so no delta-framework involvement (this is the trap
/// that would otherwise make them look like <see cref="WaitStatsCollector"/>'s counters). Zero rows
/// is the NORMAL result on a server with no Availability Groups.</para>
///
/// <para>last_hardened_lsn / last_commit_lsn are <c>numeric(25, 0)</c> — far wider than BIGINT — so
/// they are converted to strings server-side and stored as text. Only last_commit_lsn is a real LSN;
/// last_hardened_lsn is a log-block id padded with zeroes, so neither is safe to do arithmetic on.
/// Deriving a byte distance between them is ANALYSIS, deliberately left to a reader.</para>
///
/// <para>secondary_lag_seconds is 2016+ and the repo floor IS 2016, so it is referenced directly
/// with no version branch. MS Learn documents it reading <c>0</c> — not NULL — while data movement
/// is SUSPENDED, so a suspended replica presents as zero lag: any later lag analysis has to read
/// is_suspended alongside it or it will under-report the worst case. Both columns are collected here
/// so that reading is possible.</para>
///
/// <para>synchronization_state_desc's values are space-separated (<c>NOT SYNCHRONIZING</c>), unlike
/// the replica-grain synchronization_health_desc's underscores (<c>NOT_HEALTHY</c>) — stored
/// verbatim as the DMV reports them.</para>
///
/// <para>PERMISSIONS: this query joins the same two AG catalog views, which require VIEW ANY
/// DEFINITION and hide rows rather than erroring without it — see
/// <see cref="AgReplicaStatesCollector"/> for the full trap and the fingerprint that identifies it.</para>
/// </summary>
public sealed class AgDatabaseReplicaStatesCollector : CollectorDefinitionBase<AgDatabaseReplicaStatesCollector.Row>
{
    public static AgDatabaseReplicaStatesCollector Instance { get; } = new();

    private AgDatabaseReplicaStatesCollector()
    {
    }

    public readonly record struct Row(
        string? AgName,
        string? DatabaseName,
        string? ReplicaServerName,
        bool? IsLocal,
        string? SynchronizationStateDesc,
        string? LastHardenedLsn,
        string? LastCommitLsn,
        long? LogSendQueueSize,
        long? RedoQueueSize,
        long? LogSendRate,
        long? RedoRate,
        bool? IsSuspended,
        string? SuspendReasonDesc,
        string? AvailabilityModeDesc,
        long? SecondaryLagSeconds);

    private const string QueryText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    ag_name = ag.name,
    database_name = d.name,
    replica_server_name = ar.replica_server_name,
    is_local = hdrs.is_local,
    synchronization_state_desc = hdrs.synchronization_state_desc,
    last_hardened_lsn = CONVERT(nvarchar(40), hdrs.last_hardened_lsn),
    last_commit_lsn = CONVERT(nvarchar(40), hdrs.last_commit_lsn),
    log_send_queue_size = hdrs.log_send_queue_size,
    redo_queue_size = hdrs.redo_queue_size,
    log_send_rate = hdrs.log_send_rate,
    redo_rate = hdrs.redo_rate,
    is_suspended = hdrs.is_suspended,
    suspend_reason_desc = hdrs.suspend_reason_desc,
    availability_mode_desc = ar.availability_mode_desc,
    secondary_lag_seconds = hdrs.secondary_lag_seconds
FROM sys.dm_hadr_database_replica_states AS hdrs
JOIN sys.availability_replicas AS ar
  ON  hdrs.replica_id = ar.replica_id
  AND hdrs.group_id = ar.group_id
JOIN sys.availability_groups AS ag
  ON hdrs.group_id = ag.group_id
JOIN sys.databases AS d
  ON hdrs.database_id = d.database_id
WHERE COALESCE(ag.is_distributed, 0) = 0
ORDER BY
    ag.name,
    ar.replica_server_name,
    d.name
OPTION(RECOMPILE);";

    public override string Name => "ag_database_replica_states";

    public override string TargetTable => "ag_database_replica_states";

    public override string? WatermarkColumn => null;

    /// <summary>
    /// Gated off for Azure SQL Database only, matching <see cref="AgReplicaStatesCollector"/> —
    /// see that definition for the reasoning.
    /// </summary>
    public override bool AppliesTo(CollectorTargetInfo target) => !target.IsAzureSqlDb;

    public override bool RunsPerDatabase(CollectorTargetInfo target) => false;

    public override CollectorQuery BuildQuery(CollectorContext context) => new(QueryText);

    public override IReadOnlyList<CollectorColumn> PayloadColumns { get; } = new[]
    {
        new CollectorColumn("ag_name", CollectorColumnType.Varchar),
        new CollectorColumn("database_name", CollectorColumnType.Varchar),
        new CollectorColumn("replica_server_name", CollectorColumnType.Varchar),
        new CollectorColumn("is_local", CollectorColumnType.Boolean),
        new CollectorColumn("synchronization_state_desc", CollectorColumnType.Varchar),
        new CollectorColumn("last_hardened_lsn", CollectorColumnType.Varchar),
        new CollectorColumn("last_commit_lsn", CollectorColumnType.Varchar),
        new CollectorColumn("log_send_queue_size", CollectorColumnType.BigInt),
        new CollectorColumn("redo_queue_size", CollectorColumnType.BigInt),
        new CollectorColumn("log_send_rate", CollectorColumnType.BigInt),
        new CollectorColumn("redo_rate", CollectorColumnType.BigInt),
        new CollectorColumn("is_suspended", CollectorColumnType.Boolean),
        new CollectorColumn("suspend_reason_desc", CollectorColumnType.Varchar),
        new CollectorColumn("availability_mode_desc", CollectorColumnType.Varchar),
        new CollectorColumn("secondary_lag_seconds", CollectorColumnType.BigInt),
    };

    public override async ValueTask<List<Row>> ReadAsync(DbDataReader reader, CollectorContext context, CancellationToken cancellationToken)
    {
        var rows = new List<Row>();

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new Row(
                AgName: reader.IsDBNull(0) ? null : reader.GetString(0),
                DatabaseName: reader.IsDBNull(1) ? null : reader.GetString(1),
                ReplicaServerName: reader.IsDBNull(2) ? null : reader.GetString(2),
                IsLocal: reader.IsDBNull(3) ? null : reader.GetBoolean(3),
                SynchronizationStateDesc: reader.IsDBNull(4) ? null : reader.GetString(4),
                LastHardenedLsn: reader.IsDBNull(5) ? null : reader.GetString(5),
                LastCommitLsn: reader.IsDBNull(6) ? null : reader.GetString(6),
                LogSendQueueSize: reader.IsDBNull(7) ? null : reader.GetInt64(7),
                RedoQueueSize: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                LogSendRate: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                RedoRate: reader.IsDBNull(10) ? null : reader.GetInt64(10),
                IsSuspended: reader.IsDBNull(11) ? null : reader.GetBoolean(11),
                SuspendReasonDesc: reader.IsDBNull(12) ? null : reader.GetString(12),
                AvailabilityModeDesc: reader.IsDBNull(13) ? null : reader.GetString(13),
                SecondaryLagSeconds: reader.IsDBNull(14) ? null : reader.GetInt64(14)));
        }

        return rows;
    }

    public override void WritePayload(Row row, ICollectorRowWriter writer, CollectorContext context)
    {
        writer
            .Value(row.AgName)                    /* ag_name VARCHAR */
            .Value(row.DatabaseName)              /* database_name VARCHAR */
            .Value(row.ReplicaServerName)         /* replica_server_name VARCHAR */
            .Value(row.IsLocal)                   /* is_local BOOLEAN */
            .Value(row.SynchronizationStateDesc)  /* synchronization_state_desc VARCHAR */
            .Value(row.LastHardenedLsn)           /* last_hardened_lsn VARCHAR */
            .Value(row.LastCommitLsn)             /* last_commit_lsn VARCHAR */
            .Value(row.LogSendQueueSize)          /* log_send_queue_size BIGINT (KB) */
            .Value(row.RedoQueueSize)             /* redo_queue_size BIGINT (KB) */
            .Value(row.LogSendRate)               /* log_send_rate BIGINT (KB/s) */
            .Value(row.RedoRate)                  /* redo_rate BIGINT (KB/s) */
            .Value(row.IsSuspended)               /* is_suspended BOOLEAN */
            .Value(row.SuspendReasonDesc)         /* suspend_reason_desc VARCHAR */
            .Value(row.AvailabilityModeDesc)      /* availability_mode_desc VARCHAR */
            .Value(row.SecondaryLagSeconds);      /* secondary_lag_seconds BIGINT (s) */
    }
}
