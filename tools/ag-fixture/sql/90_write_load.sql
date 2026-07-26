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

DECLARE
    @stop_time datetime2(7) = DATEADD(SECOND, $(duration_seconds), SYSDATETIME()),
    @batches bigint = 0,
    @rows bigint = 0;

WHILE SYSDATETIME() < @stop_time
BEGIN
    INSERT
        dbo.write_load
    (
        filler
    )
    SELECT TOP (500)
        filler = REPLICATE(N'x', 400)
    FROM sys.all_columns AS ac;

    /* ROWCOUNT_BIG() has to be read before any other statement resets it. */
    SET @rows += ROWCOUNT_BIG();
    SET @batches += 1;

    IF @batches % 20 = 0
    BEGIN
        CHECKPOINT;
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
