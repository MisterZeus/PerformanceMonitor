/*
Runs on: ag1 (primary)

Creates the clusterless AG. CLUSTER_TYPE = NONE is what makes this work without
WSFC or Pacemaker; it also forces FAILOVER_MODE = MANUAL on every replica.

SYNCHRONOUS_COMMIT is deliberate: it is what drives synchronization_state_desc to
SYNCHRONIZED and synchronization_health_desc to HEALTHY, which is the state the
collector queries are validated against. REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT
is left at its default of 0 so stopping ag2 does not freeze writes on ag1.

Replica names must match @@SERVERNAME on each container, which is why compose pins
the hostnames. Endpoint URLs use the compose service names - they resolve on the
ag-fixture-net network, and would not resolve from the host.
*/
USE master;
GO

IF NOT EXISTS
(
    SELECT
        1/0
    FROM sys.availability_groups AS ag
    WHERE ag.name = N'ag_fixture'
)
BEGIN
    CREATE AVAILABILITY GROUP ag_fixture
        WITH
        (
            CLUSTER_TYPE = NONE
        )
        FOR REPLICA ON
            N'ag1'
            WITH
            (
                ENDPOINT_URL = N'tcp://ag1:5022',
                AVAILABILITY_MODE = SYNCHRONOUS_COMMIT,
                FAILOVER_MODE = MANUAL,
                SEEDING_MODE = AUTOMATIC,
                BACKUP_PRIORITY = 50,
                SECONDARY_ROLE
                (
                    ALLOW_CONNECTIONS = ALL
                )
            ),
            N'ag2'
            WITH
            (
                ENDPOINT_URL = N'tcp://ag2:5022',
                AVAILABILITY_MODE = SYNCHRONOUS_COMMIT,
                FAILOVER_MODE = MANUAL,
                SEEDING_MODE = AUTOMATIC,
                BACKUP_PRIORITY = 50,
                SECONDARY_ROLE
                (
                    ALLOW_CONNECTIONS = ALL
                )
            );
END;
GO
