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
/// Availability Group REPLICA health — one row per replica per AG, from sys.availability_replicas
/// joined to sys.availability_groups and sys.dm_hadr_availability_replica_states (#991). The
/// replica-grain half of the AG surface; <see cref="AgDatabaseReplicaStatesCollector"/> carries the
/// database grain.
///
/// <para>Metric surface follows Hannah Vernon's SqlServerAgMonitor (MIT),
/// https://github.com/HannahVernon/SqlServerAgMonitor.</para>
///
/// <para>A pure point-in-time snapshot: every column is a current state string, so there is no
/// counter and no delta-framework involvement (unlike <see cref="LatchStatsCollector"/>). Zero rows
/// is the NORMAL result on a server with no Availability Groups — the DMV and both catalog views
/// exist on every non-Azure-SQL-DB instance whether or not Always On is enabled, so an AG-less
/// server returns an empty set rather than erroring, and the host records SUCCESS/0 rows.</para>
///
/// <para><c>WHERE COALESCE(ag.is_distributed, 0) = 0</c> keeps distributed-AG container rows out;
/// the member AGs themselves still appear. Drilling into a DAG's remote members
/// (<c>sys.fn_hadr_distributed_ag_replica</c>) needs a connection per member and is out of scope.</para>
///
/// <para>Every column here can read NULL under WSFC quorum loss: MS Learn documents that when the
/// instance cannot reach the failover cluster, sys.availability_replicas returns only locally cached
/// metadata. <c>endpoint_url</c> is explicitly documented NULL in that state. So the reads below are
/// null-tolerant throughout rather than only on the columns whose steady-state value is optional.</para>
/// </summary>
public sealed class AgReplicaStatesCollector : CollectorDefinitionBase<AgReplicaStatesCollector.Row>
{
    public static AgReplicaStatesCollector Instance { get; } = new();

    private AgReplicaStatesCollector()
    {
    }

    public readonly record struct Row(
        string? AgName,
        string? ReplicaServerName,
        string? RoleDesc,
        string? OperationalStateDesc,
        string? ConnectedStateDesc,
        string? RecoveryHealthDesc,
        string? SynchronizationHealthDesc,
        string? AvailabilityModeDesc,
        string? FailoverModeDesc,
        string? EndpointUrl);

    private const string QueryText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    ag_name = ag.name,
    replica_server_name = ar.replica_server_name,
    role_desc = ars.role_desc,
    operational_state_desc = ars.operational_state_desc,
    connected_state_desc = ars.connected_state_desc,
    recovery_health_desc = ars.recovery_health_desc,
    synchronization_health_desc = ars.synchronization_health_desc,
    availability_mode_desc = ar.availability_mode_desc,
    failover_mode_desc = ar.failover_mode_desc,
    endpoint_url = ar.endpoint_url
FROM sys.availability_replicas AS ar
JOIN sys.availability_groups AS ag
  ON ar.group_id = ag.group_id
JOIN sys.dm_hadr_availability_replica_states AS ars
  ON ar.replica_id = ars.replica_id
WHERE COALESCE(ag.is_distributed, 0) = 0
ORDER BY
    ag.name,
    ar.replica_server_name
OPTION(RECOMPILE);";

    public override string Name => "ag_replica_states";

    public override string TargetTable => "ag_replica_states";

    public override string? WatermarkColumn => null;

    /// <summary>
    /// Azure SQL Database has no Availability Group surface (no sys.availability_groups /
    /// sys.availability_replicas), so it is gated off — the same <c>!target.IsAzureSqlDb</c> gate
    /// server_config and trace_flags use. Managed Instance and RDS keep it: both expose the AG
    /// objects, and on an instance without AGs the query is simply a zero-row read.
    /// </summary>
    public override bool AppliesTo(CollectorTargetInfo target) => !target.IsAzureSqlDb;

    public override bool RunsPerDatabase(CollectorTargetInfo target) => false;

    public override CollectorQuery BuildQuery(CollectorContext context) => new(QueryText);

    public override IReadOnlyList<CollectorColumn> PayloadColumns { get; } = new[]
    {
        new CollectorColumn("ag_name", CollectorColumnType.Varchar),
        new CollectorColumn("replica_server_name", CollectorColumnType.Varchar),
        new CollectorColumn("role_desc", CollectorColumnType.Varchar),
        new CollectorColumn("operational_state_desc", CollectorColumnType.Varchar),
        new CollectorColumn("connected_state_desc", CollectorColumnType.Varchar),
        new CollectorColumn("recovery_health_desc", CollectorColumnType.Varchar),
        new CollectorColumn("synchronization_health_desc", CollectorColumnType.Varchar),
        new CollectorColumn("availability_mode_desc", CollectorColumnType.Varchar),
        new CollectorColumn("failover_mode_desc", CollectorColumnType.Varchar),
        new CollectorColumn("endpoint_url", CollectorColumnType.Varchar),
    };

    public override async ValueTask<List<Row>> ReadAsync(DbDataReader reader, CollectorContext context, CancellationToken cancellationToken)
    {
        var rows = new List<Row>();

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new Row(
                AgName: reader.IsDBNull(0) ? null : reader.GetString(0),
                ReplicaServerName: reader.IsDBNull(1) ? null : reader.GetString(1),
                RoleDesc: reader.IsDBNull(2) ? null : reader.GetString(2),
                OperationalStateDesc: reader.IsDBNull(3) ? null : reader.GetString(3),
                ConnectedStateDesc: reader.IsDBNull(4) ? null : reader.GetString(4),
                RecoveryHealthDesc: reader.IsDBNull(5) ? null : reader.GetString(5),
                SynchronizationHealthDesc: reader.IsDBNull(6) ? null : reader.GetString(6),
                AvailabilityModeDesc: reader.IsDBNull(7) ? null : reader.GetString(7),
                FailoverModeDesc: reader.IsDBNull(8) ? null : reader.GetString(8),
                EndpointUrl: reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return rows;
    }

    public override void WritePayload(Row row, ICollectorRowWriter writer, CollectorContext context)
    {
        writer
            .Value(row.AgName)                     /* ag_name VARCHAR */
            .Value(row.ReplicaServerName)          /* replica_server_name VARCHAR */
            .Value(row.RoleDesc)                   /* role_desc VARCHAR */
            .Value(row.OperationalStateDesc)       /* operational_state_desc VARCHAR */
            .Value(row.ConnectedStateDesc)         /* connected_state_desc VARCHAR */
            .Value(row.RecoveryHealthDesc)         /* recovery_health_desc VARCHAR */
            .Value(row.SynchronizationHealthDesc)  /* synchronization_health_desc VARCHAR */
            .Value(row.AvailabilityModeDesc)       /* availability_mode_desc VARCHAR */
            .Value(row.FailoverModeDesc)           /* failover_mode_desc VARCHAR */
            .Value(row.EndpointUrl);               /* endpoint_url VARCHAR */
    }
}
