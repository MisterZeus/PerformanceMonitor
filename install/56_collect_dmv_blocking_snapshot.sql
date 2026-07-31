/*
Copyright 2026 Darling Data, LLC
https://www.erikdarling.com/

DMV blocking snapshot collector

Captures a point-in-time snapshot of CURRENT blocking (blocker -> blocked edges) directly from
sys.dm_os_waiting_tasks + sys.dm_exec_*. This is the always-on fallback for the blocked-process-report
Extended Event, which only fires when 'blocked process threshold (s)' > 0 -- a value that is unset by
default and cannot be set via sp_configure on some platforms (e.g. AWS RDS, where it requires a parameter
group). Deadlock graphs need no such threshold, so deadlocks already self-heal; blocking did not -- this
closes that gap.

Stores one row per waiting (blocked) task whose blocker is another session, enriched with both sides'
SQL text and identity, so the existing (monitor_loop, spid, ecid) blocking-chain reconstruction consumes
it unchanged. monitor_loop is synthesized NEGATIVE (one value per collection cycle) so a DMV episode can
never collide with a real, non-negative blocked-process-report monitorLoop when both sources feed one
reconstruction.

Platform notes:
  - DMV-only, so supported everywhere (no XE session, no threshold dependency).
  - On Azure SQL DB sys.dm_os_waiting_tasks is database-scoped; on box/MI/RDS it is server-wide.
*/

SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
SET IMPLICIT_TRANSACTIONS OFF;
SET STATISTICS TIME, IO OFF;
GO

USE PerformanceMonitor;
GO

IF OBJECT_ID(N'collect.dmv_blocking_snapshot_collector', N'P') IS NULL
BEGIN
    EXECUTE(N'CREATE PROCEDURE collect.dmv_blocking_snapshot_collector AS RETURN 138;');
END;
GO

ALTER PROCEDURE
    collect.dmv_blocking_snapshot_collector
(
    @debug bit = 0 /*Print debugging information*/
)
WITH RECOMPILE
AS
BEGIN
    SET NOCOUNT ON;
    SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

    DECLARE
        @rows_collected bigint = 0,
        @start_time datetime2(7) = SYSDATETIME(),
        @monitor_loop integer,
        @error_message nvarchar(4000);

    BEGIN TRY
        BEGIN TRANSACTION;

        /*
        Ensure target table exists
        */
        IF OBJECT_ID(N'collect.dmv_blocking_snapshots', N'U') IS NULL
        BEGIN
            INSERT INTO
                config.collection_log
            (
                collection_time,
                collector_name,
                collection_status,
                rows_collected,
                duration_ms,
                error_message
            )
            VALUES
            (
                @start_time,
                N'dmv_blocking_snapshot_collector',
                N'TABLE_MISSING',
                0,
                0,
                N'Table collect.dmv_blocking_snapshots does not exist, calling ensure procedure'
            );

            EXECUTE config.ensure_collection_table
                @table_name = N'dmv_blocking_snapshots',
                @debug = @debug;

            IF OBJECT_ID(N'collect.dmv_blocking_snapshots', N'U') IS NULL
            BEGIN
                ROLLBACK TRANSACTION;
                RAISERROR(N'Table collect.dmv_blocking_snapshots still missing after ensure procedure', 16, 1);
                RETURN;
            END;
        END;

        /*
        One synthetic monitor_loop per cycle. NEGATIVE so it can never collide with a real
        (non-negative) blocked-process-report monitorLoop when both sources feed one reconstruction.
        Stable across all rows of this snapshot (single @start_time); distinct between snapshots
        (collection cadence is >= 1 minute, so the per-second value is unique per cycle).
        */
        SET @monitor_loop = -1 * DATEDIFF(SECOND, CONVERT(datetime2(7), N'2020-01-01'), @start_time);

        /*
        Capture current blocking edges. One row per waiting (blocked) task whose blocker is another
        session. Both sides are enriched: the blocker's identity/SQL is captured natively (no XML
        re-parse), including the sleeping-blocker case via dm_exec_connections.most_recent_sql_handle.
        */
        INSERT INTO
            collect.dmv_blocking_snapshots
        (
            monitor_loop,
            event_time,
            database_name,
            spid,
            ecid,
            last_transaction_started,
            blocking_spid,
            blocking_ecid,
            blocking_last_tran_started,
            wait_time_ms,
            lock_mode,
            blocking_status,
            contentious_object,
            blocked_sql_text,
            blocking_sql_text,
            login_name,
            host_name,
            client_app,
            blocking_login_name,
            blocking_host_name,
            blocking_client_app
        )
        SELECT
            monitor_loop = @monitor_loop,
            event_time = @start_time,
            database_name = DB_NAME(der_b.database_id),
            spid = wt.session_id,
            ecid = wt.exec_context_id,
            last_transaction_started = tat_b.transaction_begin_time,
            blocking_spid = wt.blocking_session_id,
            blocking_ecid = ISNULL(wt.blocking_exec_context_id, 0),
            blocking_last_tran_started = tat_k.transaction_begin_time,
            wait_time_ms = wt.wait_duration_ms,
            /* LCK_M_X -> X, PAGELATCH_EX -> EX, etc. RESOURCE_SEMAPHORE[_QUERY_COMPILE] has no mode prefix to
               strip, so it stays whole and doubles as the contention-type tag (why lock_mode is nvarchar(64)). */
            lock_mode =
                REPLACE(REPLACE(REPLACE(wt.wait_type, N'LCK_M_', N''), N'PAGEIOLATCH_', N''), N'PAGELATCH_', N''),
            /* Blocker status from its SESSION (a sleeping/idle blocker has no request row). */
            blocking_status = ISNULL(der_k.status, ses_k.status),
            /* Resolve "OBJECT: dbid:objectid:indexid" only (cheap, cross-db via OBJECT_NAME). KEY/PAGE/RID
               are not resolved -- resolving a hobt cross-database needs dynamic SQL, deliberately skipped
               (this sweep runs on a much tighter cadence than the blocked-process reader). Since #1893 they
               are NAMED instead: the report collector's exact sentinel shape, carrying the database the
               lock RESOURCE lives in, so one contended object fingerprints once across both collectors. */
            contentious_object =
                CASE
                    WHEN objparse.object_id IS NOT NULL
                    THEN ISNULL(QUOTENAME(OBJECT_SCHEMA_NAME(objparse.object_id, objparse.database_id)) + N'.' + QUOTENAME(OBJECT_NAME(objparse.object_id, objparse.database_id)), der_b.wait_resource)
                    WHEN resparse.resource_database_id IS NOT NULL
                    THEN N'Unresolved: ' +
                         CASE
                             WHEN resparse.lock_type IS NULL
                             THEN N''
                             ELSE LOWER(resparse.lock_type) + N' lock, '
                         END +
                         N'database: ' + COALESCE(DB_NAME(resparse.resource_database_id), DB_NAME(der_b.database_id), N'unknown')
                    ELSE der_b.wait_resource
                END,
            blocked_sql_text = dest_b.text,
            blocking_sql_text = dest_k.text,
            login_name = ses_b.login_name,
            host_name = ses_b.host_name,
            client_app = ses_b.program_name,
            blocking_login_name = ses_k.login_name,
            blocking_host_name = ses_k.host_name,
            blocking_client_app = ses_k.program_name
        FROM sys.dm_os_waiting_tasks AS wt
        JOIN sys.dm_exec_sessions AS ses_b
          ON ses_b.session_id = wt.session_id
        LEFT JOIN sys.dm_exec_requests AS der_b
          ON der_b.session_id = wt.session_id
        LEFT JOIN sys.dm_exec_sessions AS ses_k
          ON ses_k.session_id = wt.blocking_session_id
        LEFT JOIN sys.dm_exec_requests AS der_k
          ON der_k.session_id = wt.blocking_session_id
        LEFT JOIN sys.dm_exec_connections AS con_k
          ON con_k.session_id = wt.blocking_session_id
        OUTER APPLY sys.dm_exec_sql_text(der_b.sql_handle) AS dest_b
        OUTER APPLY sys.dm_exec_sql_text(COALESCE(der_k.sql_handle, con_k.most_recent_sql_handle)) AS dest_k
        OUTER APPLY
        (
            SELECT TOP (1)
                tat.transaction_begin_time
            FROM sys.dm_tran_session_transactions AS stx
            JOIN sys.dm_tran_active_transactions AS tat
              ON tat.transaction_id = stx.transaction_id
            WHERE stx.session_id = wt.session_id
            ORDER BY
                tat.transaction_begin_time ASC
        ) AS tat_b
        OUTER APPLY
        (
            SELECT TOP (1)
                tat.transaction_begin_time
            FROM sys.dm_tran_session_transactions AS stx
            JOIN sys.dm_tran_active_transactions AS tat
              ON tat.transaction_id = stx.transaction_id
            WHERE stx.session_id = wt.blocking_session_id
            ORDER BY
                tat.transaction_begin_time ASC
        ) AS tat_k
        OUTER APPLY
        (
            SELECT
                database_id = TRY_CONVERT(integer, PARSENAME(REPLACE(SUBSTRING(der_b.wait_resource, 9, 200), N':', N'.'), 3)),
                object_id = TRY_CONVERT(integer, PARSENAME(REPLACE(SUBSTRING(der_b.wait_resource, 9, 200), N':', N'.'), 2))
            WHERE der_b.wait_resource LIKE N'OBJECT: %'
        ) AS objparse
        /* #1893: the resource's database id, and the lock type to name it with. Kept in lockstep with
           PerformanceMonitor.Collectors/DmvBlockingSnapshotCollector.cs.
           The id comes from sys.dm_os_waiting_tasks.resource_description rather than from wait_resource,
           because MS Learn documents EVERY lock resource type's description as carrying a dbid= token --
           keylock, pagelock, ridlock, objectlock, databaselock, filelock, extentlock, applicationlock,
           metadatalock, hobtlock and allocunitlock -- so one parse covers every lock shape instead of a
           per-type positional split of wait_resource. Verified live on SQL 2022: a cross-database KEY lock
           reports 'keylock hobtid=72057594045726720 dbid=11 id=lock... mode=X associatedObjectId=...'.
           The lock TYPE still comes from wait_resource, using the report collector's classifier verbatim.
           Restricted to LCK_% waits with a wait_resource: latch and RESOURCE_SEMAPHORE rows have no
           'TYPE: ' resource shape and no dbid= to read, and they keep exactly the value they had. */
        OUTER APPLY
        (
            SELECT
                resource_database_id =
                    TRY_CONVERT
                    (
                        integer,
                        NULLIF(LEFT(d.tail, PATINDEX(N'%[^0-9]%', d.tail + N'.') - 1), N'')
                    ),
                lock_type =
                    CASE
                        WHEN der_b.wait_resource LIKE N'%KEY: %'    THEN N'KEY'
                        WHEN der_b.wait_resource LIKE N'%OBJECT: %' THEN N'OBJECT'
                        WHEN der_b.wait_resource LIKE N'%RID: %'    THEN N'RID'
                        WHEN der_b.wait_resource LIKE N'%PAGE: %'   THEN N'PAGE'
                        ELSE LEFT(UPPER(LEFT(der_b.wait_resource, CHARINDEX(N':', der_b.wait_resource + N':') - 1)), 32)
                    END
            FROM
            (
                SELECT
                    tail = SUBSTRING(wt.resource_description, CHARINDEX(N'dbid=', wt.resource_description) + 5, 10)
            ) AS d
            WHERE wt.wait_type LIKE N'LCK[_]%'
            AND   der_b.wait_resource IS NOT NULL
            AND   der_b.wait_resource <> N''
            AND   CHARINDEX(N'dbid=', wt.resource_description) > 0
        ) AS resparse
        WHERE wt.blocking_session_id IS NOT NULL
        AND   wt.blocking_session_id <> 0
        AND   wt.blocking_session_id <> wt.session_id /*ignore intra-query (CXPACKET) self-waits*/
        AND   wt.session_id > 50
        AND   (
                  wt.wait_type LIKE N'LCK[_]%'
                  OR wt.wait_type LIKE N'PAGELATCH[_]%'
                  OR wt.wait_type LIKE N'PAGEIOLATCH[_]%'
                  OR wt.wait_type LIKE N'RESOURCE_SEMAPHORE%'
              )
        /* Layered minimum-wait floor: cut grid/chain noise by contention class. Locks must persist to matter
           (2s); page latches churn faster, so a lower floor (PAGELATCH 0.5s, PAGEIOLATCH 1s); memory-grant /
           compile-gate waits (RESOURCE_SEMAPHORE and RESOURCE_SEMAPHORE_QUERY_COMPILE) run long, so 5s. */
        AND   wt.wait_duration_ms >=
              CASE
                  WHEN wt.wait_type LIKE N'LCK[_]%'             THEN 2000
                  WHEN wt.wait_type LIKE N'PAGELATCH[_]%'       THEN 500
                  WHEN wt.wait_type LIKE N'PAGEIOLATCH[_]%'     THEN 1000
                  WHEN wt.wait_type LIKE N'RESOURCE_SEMAPHORE%' THEN 5000
                  ELSE 1000
              END
        AND   ISNULL(der_b.database_id, 0) <> DB_ID(N'PerformanceMonitor')
        OPTION(RECOMPILE);

        SET @rows_collected = ROWCOUNT_BIG();

        IF @debug = 1
        BEGIN
            RAISERROR(N'Collected %I64d blocking edges (monitor_loop %d)', 0, 1, @rows_collected, @monitor_loop) WITH NOWAIT;
        END;

        INSERT INTO
            config.collection_log
        (
            collector_name,
            collection_status,
            rows_collected,
            duration_ms
        )
        VALUES
        (
            N'dmv_blocking_snapshot_collector',
            N'SUCCESS',
            @rows_collected,
            DATEDIFF(MILLISECOND, @start_time, SYSDATETIME())
        );

        COMMIT TRANSACTION;

    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        SET @error_message = ERROR_MESSAGE();

        INSERT INTO
            config.collection_log
        (
            collector_name,
            collection_status,
            duration_ms,
            error_message
        )
        VALUES
        (
            N'dmv_blocking_snapshot_collector',
            N'ERROR',
            DATEDIFF(MILLISECOND, @start_time, SYSDATETIME()),
            @error_message
        );

        RAISERROR(N'Error in DMV blocking snapshot collector: %s', 16, 1, @error_message);
    END CATCH;
END;
GO

PRINT 'DMV blocking snapshot collector created successfully';
PRINT 'Captures point-in-time blocking from DMVs (always-on fallback for the blocked-process-report XE)';
PRINT 'Use: EXECUTE collect.dmv_blocking_snapshot_collector @debug = 1;';
GO
