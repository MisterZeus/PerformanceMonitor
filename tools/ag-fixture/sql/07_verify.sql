/*
Runs on: ag1 (primary)

Post-setup sanity check. Prints the replica grain and the database grain so a failed
setup is obvious without digging through the error log.

Expected: two replica rows, one PRIMARY_NORMAL and one SECONDARY_NORMAL, both
connected_state_desc CONNECTED and synchronization_health_desc HEALTHY; two database
rows, both SYNCHRONIZED.
*/
USE master;
GO

SELECT
    grain = 'replica',
    ag_name = ag.name,
    replica_server_name = ar.replica_server_name,
    role = ars.role_desc,
    connected_state = ars.connected_state_desc,
    synchronization_health = ars.synchronization_health_desc,
    operational_state = ars.operational_state_desc,
    availability_mode = ar.availability_mode_desc,
    is_local = ars.is_local
FROM sys.availability_groups AS ag
JOIN sys.availability_replicas AS ar
  ON ar.group_id = ag.group_id
JOIN sys.dm_hadr_availability_replica_states AS ars
  ON  ars.group_id = ar.group_id
  AND ars.replica_id = ar.replica_id
ORDER BY
    ars.role_desc,
    ar.replica_server_name;
GO

/*
sys.dm_hadr_database_replica_states carries database_id, not a name, so the name comes
from sys.databases - the same join the collector queries make.
*/
SELECT
    grain = 'database',
    ag_name = ag.name,
    replica_server_name = ar.replica_server_name,
    database_name = d.name,
    role = ars.role_desc,
    synchronization_state = drs.synchronization_state_desc,
    is_suspended = drs.is_suspended,
    suspend_reason = drs.suspend_reason_desc,
    log_send_queue_kb = drs.log_send_queue_size,
    redo_queue_kb = drs.redo_queue_size
FROM sys.dm_hadr_database_replica_states AS drs
JOIN sys.availability_groups AS ag
  ON ag.group_id = drs.group_id
JOIN sys.availability_replicas AS ar
  ON  ar.group_id = drs.group_id
  AND ar.replica_id = drs.replica_id
JOIN sys.dm_hadr_availability_replica_states AS ars
  ON  ars.group_id = drs.group_id
  AND ars.replica_id = drs.replica_id
JOIN sys.databases AS d
  ON d.database_id = drs.database_id
ORDER BY
    ars.role_desc,
    d.name;
GO
