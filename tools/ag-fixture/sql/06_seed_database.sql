/*
Runs on: ag1 (primary)

Creates the seed database, gives it a table for the write-load script to hammer, and
adds it to the AG.

A database cannot join an AG straight out of CREATE DATABASE: it must be in FULL
recovery AND have had a full backup taken after that, otherwise it is still in
pseudo-simple recovery and ADD DATABASE fails. The backup goes to the container's own
data directory - SQL Server on Linux has no /var/opt/mssql/backup by default.

Automatic seeding streams the database to ag2 from here; nothing needs restoring on
the secondary.
*/
USE master;
GO

/*
Explicit small files. The model database's defaults are far larger than this fixture
needs, and automatic seeding reproduces the primary's file layout on ag2, so every
megabyte here is paid for twice. Growth increments are small for the same reason -
the write-load script is meant to grow the log a little, not run away with the disk.

RECOVERY FULL is mandatory, not a default worth trimming: an availability group will
not accept a database in SIMPLE recovery. "Small" here is about file sizes only.
*/
IF DB_ID(N'AgFixtureDb') IS NULL
BEGIN
    CREATE DATABASE AgFixtureDb
    ON PRIMARY
    (
        NAME = N'AgFixtureDb',
        FILENAME = N'/var/opt/mssql/data/AgFixtureDb.mdf',
        SIZE = 64MB,
        FILEGROWTH = 32MB
    )
    LOG ON
    (
        NAME = N'AgFixtureDb_log',
        FILENAME = N'/var/opt/mssql/data/AgFixtureDb_log.ldf',
        SIZE = 64MB,
        FILEGROWTH = 32MB
    );
END;
GO

ALTER DATABASE AgFixtureDb
    SET RECOVERY FULL;
GO

USE AgFixtureDb;
GO

IF OBJECT_ID(N'dbo.write_load', N'U') IS NULL
BEGIN
    CREATE TABLE
        dbo.write_load
    (
        id bigint IDENTITY(1, 1) NOT NULL,
        inserted_at datetime2(7) NOT NULL
            CONSTRAINT df_write_load_inserted_at DEFAULT (SYSDATETIME()),
        filler nvarchar(400) NOT NULL,
        CONSTRAINT pk_write_load
            PRIMARY KEY CLUSTERED (id)
    );
END;
GO

USE master;
GO

/*
Full backup after FULL recovery, then add to the AG.
*/
IF NOT EXISTS
(
    SELECT
        1/0
    FROM sys.databases AS d
    JOIN sys.dm_hadr_database_replica_states AS drs
      ON  drs.database_id = d.database_id
      AND drs.is_local = 1
    WHERE d.name = N'AgFixtureDb'
)
BEGIN
    BACKUP DATABASE AgFixtureDb
        TO DISK = '/var/opt/mssql/data/AgFixtureDb.bak'
        WITH
            INIT,
            FORMAT,
            COMPRESSION,
            NAME = 'AgFixtureDb seed backup';

    ALTER AVAILABILITY GROUP ag_fixture
        ADD DATABASE AgFixtureDb;
END;
GO
