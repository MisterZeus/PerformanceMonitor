/*
Runs on: ag2 (secondary)

Joins ag2 to the AG and grants the AG the right to create databases on this replica.

The AG is absent from sys.availability_groups on this replica until the JOIN succeeds
- a clusterless AG has no cluster metadata store to read it from - so NOT EXISTS is
the idempotency guard, and it is what makes a re-run of setup.ps1 a no-op.

The GRANT is not optional with SEEDING_MODE = AUTOMATIC: without it direct seeding
cannot materialize the database here, and the database sits forever in a
synchronization_state_desc of NOT SYNCHRONIZING on the secondary.
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
    ALTER AVAILABILITY GROUP ag_fixture
        JOIN WITH
        (
            CLUSTER_TYPE = NONE
        );
END;
GO

ALTER AVAILABILITY GROUP ag_fixture
    GRANT CREATE ANY DATABASE;
GO
