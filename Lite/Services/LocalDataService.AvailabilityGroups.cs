/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using PerformanceMonitor.Common;

namespace PerformanceMonitorLite.Services;

public partial class LocalDataService
{
    /// <summary>
    /// The server's LATEST <c>ag_replica_states</c> snapshot (#1696) — the rows at that table's own
    /// MAX(collection_time), plus that timestamp so the caller can decide whether the reading is fresh
    /// enough to judge. Darling's twin is <c>ReadLatestAgReplicaStatesAsync</c>; the SQL is the same shape
    /// against DuckDB instead of Postgres.
    ///
    /// <para>Rows whose <c>ag_name</c> or <c>replica_server_name</c> is NULL are DROPPED: those are the
    /// identity of the alert's state key, and under WSFC quorum loss they can read NULL. A row that cannot be
    /// keyed cannot have an edge tracked for it, so keying it under a placeholder would invent transitions.
    /// Zero rows is the NORMAL result on a server with no Availability Groups.</para>
    /// </summary>
    public async Task<(DateTime? CollectionTimeUtc, List<AgReplicaReading> Replicas)> GetLatestAgReplicaStatesAsync(int serverId)
    {
        var replicas = new List<AgReplicaReading>();
        DateTime? newest = null;

        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT ag_name, replica_server_name, role_desc, connected_state_desc, collection_time
FROM ag_replica_states
WHERE server_id = $1
AND   collection_time = (SELECT MAX(collection_time) FROM ag_replica_states WHERE server_id = $1)
ORDER BY ag_name, replica_server_name";
        command.Parameters.Add(new DuckDBParameter { Value = serverId });

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(4))
            {
                var stamped = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc);
                if (newest is null || stamped > newest)
                {
                    newest = stamped;
                }
            }

            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }

            replicas.Add(new AgReplicaReading(
                AgName: reader.GetString(0),
                ReplicaServerName: reader.GetString(1),
                RoleDesc: reader.IsDBNull(2) ? null : reader.GetString(2),
                ConnectedStateDesc: reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return (newest, replicas);
    }

    /// <summary>
    /// The server's LATEST <c>ag_database_replica_states</c> snapshot (#1696), with its OWN
    /// MAX(collection_time). Separate from <see cref="GetLatestAgReplicaStatesAsync"/> — and separately
    /// freshness-gated by the caller — because the two AG collectors carry independent schedule entries: a
    /// disabled or failing database-grain collector must not have the replica grain's healthy timestamp vouch
    /// for its stale rows, which would keep re-firing "AG Sync Fell Behind" off days-old lag readings.
    /// </summary>
    public async Task<(DateTime? CollectionTimeUtc, List<AgDatabaseReading> Databases)> GetLatestAgDatabaseReplicaStatesAsync(int serverId)
    {
        var databases = new List<AgDatabaseReading>();
        DateTime? newest = null;

        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT ag_name, database_name, replica_server_name, secondary_lag_seconds, redo_queue_size,
       is_suspended, suspend_reason_desc, collection_time
FROM ag_database_replica_states
WHERE server_id = $1
AND   collection_time = (SELECT MAX(collection_time) FROM ag_database_replica_states WHERE server_id = $1)
ORDER BY ag_name, database_name, replica_server_name";
        command.Parameters.Add(new DuckDBParameter { Value = serverId });

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(7))
            {
                var stamped = DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc);
                if (newest is null || stamped > newest)
                {
                    newest = stamped;
                }
            }

            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
            {
                continue;
            }

            databases.Add(new AgDatabaseReading(
                AgName: reader.GetString(0),
                DatabaseName: reader.GetString(1),
                ReplicaServerName: reader.GetString(2),
                SecondaryLagSeconds: reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3)),
                RedoQueueSizeKb: reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4)),
                IsSuspended: reader.IsDBNull(5) ? null : reader.GetBoolean(5),
                SuspendReasonDesc: reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return (newest, databases);
    }
}
