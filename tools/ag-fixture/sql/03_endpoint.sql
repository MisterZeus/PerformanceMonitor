/*
Runs on: BOTH ag1 and ag2

Creates the database mirroring endpoint the AG replicates over.

LISTENER_IP = ALL makes the endpoint listen on 0.0.0.0 inside the container. Without
it the endpoint can bind an address the other container cannot reach, and the AG sits
in NOT_SYNCHRONIZING with a connected_state of DISCONNECTED.
*/
USE master;
GO

IF NOT EXISTS
(
    SELECT
        1/0
    FROM sys.database_mirroring_endpoints AS dme
    WHERE dme.name = N'ag_fixture_endpoint'
)
BEGIN
    CREATE ENDPOINT ag_fixture_endpoint
        AS TCP
        (
            LISTENER_PORT = 5022,
            LISTENER_IP = ALL
        )
        FOR DATABASE_MIRRORING
        (
            ROLE = ALL,
            AUTHENTICATION = CERTIFICATE ag_fixture_certificate,
            ENCRYPTION = REQUIRED ALGORITHM AES
        );
END;
GO

ALTER ENDPOINT ag_fixture_endpoint
    STATE = STARTED;
GO
