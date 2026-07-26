/*
Runs on: ag1 (primary)

Tiny insert loop, just enough to put log traffic on the wire so the send/redo queues
and the *_rate columns in sys.dm_hadr_database_replica_states move off zero. Without
traffic those columns sit at 0 and a collector reading them looks broken when it is
not.

Batches are committed individually and CHECKPOINTed periodically so the log actually
ships rather than sitting in one long transaction.

Requires sqlcmd variable: duration_seconds
*/
USE AgFixtureDb;
GO

SET NOCOUNT ON;

/*
Start from a clean table when a previous run left a big one behind. The fixture is
sized to stay out of the way of everything else on the machine, and repeated write
loads would otherwise accumulate indefinitely across sessions. TRUNCATE rather than
DELETE: it is cheap, and the AG ships it like any other operation.
*/
IF EXISTS
(
    SELECT
        1/0
    FROM dbo.write_load AS wl
    HAVING
        COUNT_BIG(*) > 250000
)
BEGIN
    TRUNCATE TABLE dbo.write_load;
END;

DECLARE
    @stop_time datetime2(7) = DATEADD(SECOND, $(duration_seconds), SYSDATETIME()),
    @batches bigint = 0,
    @rows bigint = 0;

WHILE SYSDATETIME() < @stop_time
BEGIN
    /*
    Small batches on purpose. The point is a steady trickle of log to ship, not
    throughput - and the containers only have one CPU each.
    */
    INSERT
        dbo.write_load
    (
        filler
    )
    SELECT TOP (200)
        filler = REPLICATE(N'x', 400)
    FROM sys.all_columns AS ac;

    /* ROWCOUNT_BIG() has to be read before any other statement resets it. */
    SET @rows += ROWCOUNT_BIG();
    SET @batches += 1;

    IF @batches % 20 = 0
    BEGIN
        CHECKPOINT;
    END;

    /*
    Back the log up periodically, overwriting the same file every time. The database
    has to be in FULL recovery for the AG to accept it, which means the log cannot
    truncate on its own - without this a long run grows the log without bound, and
    this fixture is meant to stay small enough to share a machine with everything
    else. WITH INIT keeps the backup file itself from accumulating; nothing here is
    ever restored, so breaking the log chain costs nothing.
    */
    IF @batches % 100 = 0
    BEGIN
        BACKUP LOG AgFixtureDb
            TO DISK = '/var/opt/mssql/data/AgFixtureDb_log.trn'
            WITH
                INIT,
                COMPRESSION,
                NAME = 'AgFixtureDb write-load log trim';
    END;
END;

SELECT
    batches = @batches,
    rows_inserted = @rows,
    total_rows =
    (
        SELECT
            COUNT_BIG(*)
        FROM dbo.write_load AS wl
    );
GO
