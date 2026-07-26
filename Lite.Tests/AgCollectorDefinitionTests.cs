/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lite.Tests.Helpers;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the cross-SKU parity contract of the two shared Availability Group collector definitions
/// (#991): verbatim query text, target gate, payload column order, and the ReadAsync mapping —
/// including its null tolerance, which is the part most likely to regress. An intentional change to
/// any of these must consciously update these tests; that is the point.
///
/// <para>Neither collector computes a delta (both DMVs report current state, never a counter), so
/// unlike the wait/latch definition tests there is no delta group/key/gap contract to pin — the
/// absence is itself pinned by <see cref="WritePayload_EmitsPayloadOrder_AndTakesNoDeltas"/>.</para>
/// </summary>
public sealed class AgCollectorDefinitionTests
{
    private static readonly ICollectorSchemaInfo[] AgCollectors =
    {
        AgReplicaStatesCollector.Instance,
        AgDatabaseReplicaStatesCollector.Instance,
    };

    private const string ExpectedReplicaQuery = @"
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

    private const string ExpectedDatabaseQuery = @"
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

    [Fact]
    public void ReplicaQuery_IsTheVerbatimParityContract()
    {
        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        Assert.Equal(ExpectedReplicaQuery, AgReplicaStatesCollector.Instance.BuildQuery(context).Text);
        Assert.Empty(AgReplicaStatesCollector.Instance.BuildQuery(context).Parameters);
        Assert.Null(AgReplicaStatesCollector.Instance.WatermarkColumn);
        Assert.Equal("ag_replica_states", AgReplicaStatesCollector.Instance.Name);
        Assert.Equal("ag_replica_states", AgReplicaStatesCollector.Instance.TargetTable);
        Assert.Equal("collection_time", AgReplicaStatesCollector.Instance.PrefixTimeColumnName);
    }

    [Fact]
    public void DatabaseQuery_IsTheVerbatimParityContract()
    {
        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        Assert.Equal(ExpectedDatabaseQuery, AgDatabaseReplicaStatesCollector.Instance.BuildQuery(context).Text);
        Assert.Empty(AgDatabaseReplicaStatesCollector.Instance.BuildQuery(context).Parameters);
        Assert.Null(AgDatabaseReplicaStatesCollector.Instance.WatermarkColumn);
        Assert.Equal("ag_database_replica_states", AgDatabaseReplicaStatesCollector.Instance.Name);
        Assert.Equal("ag_database_replica_states", AgDatabaseReplicaStatesCollector.Instance.TargetTable);
        Assert.Equal("collection_time", AgDatabaseReplicaStatesCollector.Instance.PrefixTimeColumnName);
    }

    [Fact]
    public void Queries_KeepTheDistributedAgFilter()
    {
        /* The DAG container rows must stay out of both tables — member AGs still appear. Pinned
           separately from the verbatim text so the intent survives a reformat of the query. */
        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        Assert.Contains("COALESCE(ag.is_distributed, 0) = 0", AgReplicaStatesCollector.Instance.BuildQuery(context).Text, StringComparison.Ordinal);
        Assert.Contains("COALESCE(ag.is_distributed, 0) = 0", AgDatabaseReplicaStatesCollector.Instance.BuildQuery(context).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AppliesTo_EverywhereExceptAzureSqlDb()
    {
        /* On-prem, Managed Instance and RDS all expose the AG surface; Azure SQL DB does not. An
           AG-less on-prem server is NOT gated off — it collects and returns zero rows. */
        foreach (var collector in AgCollectors)
        {
            Assert.False(collector.AppliesTo(new CollectorTargetInfo { IsAzureSqlDb = true }));
            Assert.True(collector.AppliesTo(new CollectorTargetInfo { IsAzureManagedInstance = true }));
            Assert.True(collector.AppliesTo(new CollectorTargetInfo { IsAwsRds = true }));
            Assert.True(collector.AppliesTo(new CollectorTargetInfo()));

            /* msdb access is irrelevant here — these read server-scoped HADR objects, not msdb. */
            Assert.True(collector.AppliesTo(new CollectorTargetInfo { HasMsdbAccess = false }));
        }
    }

    [Fact]
    public void NeitherCollector_RunsPerDatabase()
    {
        /* Server-scope single sweep: the database grain comes from the DMV's own rows, NOT from the
           host enumerating databases and connecting to each. */
        Assert.False(AgReplicaStatesCollector.Instance.RunsPerDatabase(new CollectorTargetInfo()));
        Assert.False(AgReplicaStatesCollector.Instance.RunsPerDatabase(new CollectorTargetInfo { IsAzureManagedInstance = true }));
        Assert.False(AgDatabaseReplicaStatesCollector.Instance.RunsPerDatabase(new CollectorTargetInfo()));
        Assert.False(AgDatabaseReplicaStatesCollector.Instance.RunsPerDatabase(new CollectorTargetInfo { IsAzureManagedInstance = true }));
    }

    [Fact]
    public void ReplicaPayloadColumns_AreInAppendOrder()
    {
        var names = AgReplicaStatesCollector.Instance.PayloadColumns.Select(c => c.Name).ToArray();

        Assert.Equal(
            new[]
            {
                "ag_name",
                "replica_server_name",
                "role_desc",
                "operational_state_desc",
                "connected_state_desc",
                "recovery_health_desc",
                "synchronization_health_desc",
                "availability_mode_desc",
                "failover_mode_desc",
                "endpoint_url",
            },
            names);

        Assert.All(AgReplicaStatesCollector.Instance.PayloadColumns,
            c => Assert.Equal(CollectorColumnType.Varchar, c.Type));
    }

    [Fact]
    public void DatabasePayloadColumns_AreInAppendOrder_WithTheDeclaredTypes()
    {
        var columns = AgDatabaseReplicaStatesCollector.Instance.PayloadColumns;

        Assert.Equal(
            new[]
            {
                "ag_name",
                "database_name",
                "replica_server_name",
                "is_local",
                "synchronization_state_desc",
                "last_hardened_lsn",
                "last_commit_lsn",
                "log_send_queue_size",
                "redo_queue_size",
                "log_send_rate",
                "redo_rate",
                "is_suspended",
                "suspend_reason_desc",
                "availability_mode_desc",
                "secondary_lag_seconds",
            },
            columns.Select(c => c.Name).ToArray());

        /* The LSNs are numeric(25,0) in the DMV — far wider than BIGINT — so they are converted
           server-side and stored as text. Storing them numerically would silently overflow. */
        Assert.Equal(CollectorColumnType.Varchar, columns.Single(c => c.Name == "last_hardened_lsn").Type);
        Assert.Equal(CollectorColumnType.Varchar, columns.Single(c => c.Name == "last_commit_lsn").Type);

        Assert.Equal(CollectorColumnType.Boolean, columns.Single(c => c.Name == "is_local").Type);
        Assert.Equal(CollectorColumnType.Boolean, columns.Single(c => c.Name == "is_suspended").Type);

        foreach (var numeric in new[] { "log_send_queue_size", "redo_queue_size", "log_send_rate", "redo_rate", "secondary_lag_seconds" })
        {
            Assert.Equal(CollectorColumnType.BigInt, columns.Single(c => c.Name == numeric).Type);
        }
    }

    [Fact]
    public async Task ReplicaReadAsync_MapsColumns()
    {
        using var reader = new FakeCollectorDataReader(
            new object[] { "AG1", "NODE1", "PRIMARY", "ONLINE", "CONNECTED", "ONLINE", "HEALTHY", "SYNCHRONOUS_COMMIT", "AUTOMATIC", "TCP://NODE1.corp:5022" },
            new object[] { "AG1", "NODE2", "SECONDARY", "ONLINE", "CONNECTED", "ONLINE", "HEALTHY", "SYNCHRONOUS_COMMIT", "AUTOMATIC", "TCP://NODE2.corp:5022" });

        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        var rows = await AgReplicaStatesCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal(
            new AgReplicaStatesCollector.Row("AG1", "NODE1", "PRIMARY", "ONLINE", "CONNECTED", "ONLINE", "HEALTHY", "SYNCHRONOUS_COMMIT", "AUTOMATIC", "TCP://NODE1.corp:5022"),
            rows[0]);
        Assert.Equal("NODE2", rows[1].ReplicaServerName);
        Assert.Equal("SECONDARY", rows[1].RoleDesc);
    }

    [Fact]
    public async Task ReplicaReadAsync_ToleratesNulls()
    {
        /* endpoint_url is documented NULL when the instance cannot reach the WSFC cluster, and in that
           state sys.availability_replicas serves only locally cached metadata — so every column can be
           null. A quorum-loss read must produce a row, not an InvalidCastException. */
        using var reader = new FakeCollectorDataReader(
            new object[] { "AG1", "NODE1", "PRIMARY", "ONLINE", "CONNECTED", "ONLINE", "HEALTHY", "SYNCHRONOUS_COMMIT", "AUTOMATIC", DBNull.Value },
            new object[] { DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value });

        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        var rows = await AgReplicaStatesCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].EndpointUrl);
        Assert.Equal("AG1", rows[0].AgName);
        Assert.Equal(default(AgReplicaStatesCollector.Row), rows[1]);
    }

    [Fact]
    public async Task DatabaseReadAsync_MapsColumns()
    {
        using var reader = new FakeCollectorDataReader(
            new object[]
            {
                "AG1", "Orders", "NODE2", false, "SYNCHRONIZING",
                "37000000045600001", "37000000045600002",
                4096L, 2048L, 512L, 256L,
                false, DBNull.Value, "SYNCHRONOUS_COMMIT", 12L,
            });

        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        var rows = await AgDatabaseReplicaStatesCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("AG1", row.AgName);
        Assert.Equal("Orders", row.DatabaseName);
        Assert.Equal("NODE2", row.ReplicaServerName);
        Assert.False(row.IsLocal);
        Assert.Equal("SYNCHRONIZING", row.SynchronizationStateDesc);
        Assert.Equal("37000000045600001", row.LastHardenedLsn);
        Assert.Equal("37000000045600002", row.LastCommitLsn);
        Assert.Equal(4096L, row.LogSendQueueSize);
        Assert.Equal(2048L, row.RedoQueueSize);
        Assert.Equal(512L, row.LogSendRate);
        Assert.Equal(256L, row.RedoRate);
        Assert.False(row.IsSuspended);
        Assert.Null(row.SuspendReasonDesc);
        Assert.Equal("SYNCHRONOUS_COMMIT", row.AvailabilityModeDesc);
        Assert.Equal(12L, row.SecondaryLagSeconds);
    }

    [Fact]
    public async Task DatabaseReadAsync_ToleratesNulls()
    {
        /* Every payload column of sys.dm_hadr_database_replica_states is documented nullable —
           last_hardened_lsn is explicitly NULL on an async-commit primary, and the queues/rates/lag
           are null on a replica that is not reporting. */
        using var reader = new FakeCollectorDataReader(
            new object[]
            {
                "AG1", "Orders", "NODE1", true, "SYNCHRONIZED",
                DBNull.Value, DBNull.Value,
                DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value,
                DBNull.Value, DBNull.Value, "ASYNCHRONOUS_COMMIT", DBNull.Value,
            });

        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        var rows = await AgDatabaseReplicaStatesCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.True(row.IsLocal);
        Assert.Null(row.LastHardenedLsn);
        Assert.Null(row.LastCommitLsn);
        Assert.Null(row.LogSendQueueSize);
        Assert.Null(row.RedoQueueSize);
        Assert.Null(row.LogSendRate);
        Assert.Null(row.RedoRate);
        Assert.Null(row.IsSuspended);
        Assert.Null(row.SuspendReasonDesc);
        Assert.Null(row.SecondaryLagSeconds);
    }

    [Fact]
    public async Task ReadAsync_OnAnAgLessServer_YieldsZeroRowsNotAnError()
    {
        /* The normal case on the overwhelming majority of monitored servers: both catalog views and
           the DMV exist whether or not Always On is configured, so the sweep is an empty result set,
           and the host logs SUCCESS with 0 rows rather than an error. */
        var context = CollectorTestContext.Make(new RecordingCollectorDeltaCalculator());

        using var replicaReader = new FakeCollectorDataReader();
        Assert.Empty(await AgReplicaStatesCollector.Instance.ReadAsync(replicaReader, context, CancellationToken.None));

        using var databaseReader = new FakeCollectorDataReader();
        Assert.Empty(await AgDatabaseReplicaStatesCollector.Instance.ReadAsync(databaseReader, context, CancellationToken.None));
    }

    [Fact]
    public void WritePayload_EmitsPayloadOrder_AndTakesNoDeltas()
    {
        var deltas = new RecordingCollectorDeltaCalculator();
        var context = CollectorTestContext.Make(deltas);

        var replicaWriter = new RecordingCollectorRowWriter();
        AgReplicaStatesCollector.Instance.WritePayload(
            new AgReplicaStatesCollector.Row("AG1", "NODE1", "PRIMARY", "ONLINE", "CONNECTED", "ONLINE", "HEALTHY", "SYNCHRONOUS_COMMIT", "AUTOMATIC", null),
            replicaWriter,
            context);

        Assert.Equal(
            new object?[] { "AG1", "NODE1", "PRIMARY", "ONLINE", "CONNECTED", "ONLINE", "HEALTHY", "SYNCHRONOUS_COMMIT", "AUTOMATIC", null },
            replicaWriter.Values);

        var databaseWriter = new RecordingCollectorRowWriter();
        AgDatabaseReplicaStatesCollector.Instance.WritePayload(
            new AgDatabaseReplicaStatesCollector.Row("AG1", "Orders", "NODE2", false, "SYNCHRONIZING", "1", "2", 4096L, 2048L, 512L, 256L, true, "SUSPEND_FROM_USER", "SYNCHRONOUS_COMMIT", 0L),
            databaseWriter,
            context);

        Assert.Equal(
            new object?[] { "AG1", "Orders", "NODE2", false, "SYNCHRONIZING", "1", "2", 4096L, 2048L, 512L, 256L, true, "SUSPEND_FROM_USER", "SYNCHRONOUS_COMMIT", 0L },
            databaseWriter.Values);

        /* The queues, rates and lag are instantaneous gauges, NOT counters: neither collector may
           touch the delta calculator. A delta here would silently produce nonsense. */
        Assert.Empty(deltas.Calls);
    }

    [Fact]
    public void BothCollectors_AreRegisteredInTheCatalog_WithDefaultSchedules()
    {
        foreach (var name in new[] { "ag_replica_states", "ag_database_replica_states" })
        {
            Assert.Contains(CollectorCatalog.All, c => c.Name == name);
            Assert.True(CollectorScheduleDefaults.All.TryGetValue(name, out var entry));
            Assert.Equal(1, entry!.FrequencyMinutes);
            Assert.Equal(30, entry.RetentionDays);

            /* Default ON — unlike long_query_completions, an AG read costs nothing on a server
               without AGs, so there is no reason to make it opt-in. */
            Assert.True(entry.DefaultEnabled);
        }
    }

    [Fact]
    public void CatalogAppliesTo_ByName_AgreesWithTheDefinitions()
    {
        /* Lite consults the gate BY NAME for its pre-dispatch SKIPPED log; a typo there would
           silently return "not gated". Pin that the by-name lookup resolves to the real override. */
        var azure = new CollectorTargetInfo { IsAzureSqlDb = true };

        Assert.False(CollectorCatalog.AppliesTo("ag_replica_states", azure));
        Assert.False(CollectorCatalog.AppliesTo("ag_database_replica_states", azure));
        Assert.True(CollectorCatalog.AppliesTo("ag_replica_states", new CollectorTargetInfo()));
        Assert.True(CollectorCatalog.AppliesTo("ag_database_replica_states", new CollectorTargetInfo()));
    }
}
