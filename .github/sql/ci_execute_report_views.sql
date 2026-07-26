/*
Copyright 2026 Darling Data, LLC
https://www.erikdarling.com/

CI: execute every report.* view against the seeded rows (#1669).

Existence checks (OBJECT_ID) let two view bugs ship: #1635's bit -> nvarchar conversion (Msg 245)
and #1666's binary(8) boxed raw into sql_variant. Both compile fine and only fail (or corrupt)
when rows flow through the projection. This sweep runs AFTER ci_seed_report_sources.sql so rows
exist, and materializes each view with SELECT * INTO - a plain COUNT(*) would let the optimizer
prune the projected columns and skip exactly the per-row conversions this exists to catch.

Enumerates sys.views dynamically: a new report view is covered the day it ships, and views that
only exist post-collection (the dynamically-built report.query_snapshots pair) are simply absent
on a fresh install rather than hardcoded failures. Views whose WHERE excludes every seed row still
execute their plan shape; they just prove less - the seeds aim rows at the windows the views read.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

USE PerformanceMonitor;
GO

DECLARE
    @view_name sysname,
    @sql nvarchar(max),
    @failed integer = 0,
    @executed integer = 0,
    @failures nvarchar(max) = N'';

DECLARE view_sweep CURSOR LOCAL FAST_FORWARD FOR
    SELECT
        v.name
    FROM sys.views AS v
    WHERE SCHEMA_NAME(v.schema_id) = N'report'
    ORDER BY v.name;

OPEN view_sweep;
FETCH NEXT FROM view_sweep INTO @view_name;

WHILE @@FETCH_STATUS = 0
BEGIN
    /* SELECT * INTO forces every projected column to be evaluated for every row -
       the temp table is scoped to the EXEC batch and vanishes with it. */
    SET @sql = N'SELECT * INTO #ci_sink FROM report.' + QUOTENAME(@view_name) + N';';

    BEGIN TRY
        EXECUTE sys.sp_executesql @sql;
        SET @executed += 1;
    END TRY
    BEGIN CATCH
        SET @failed += 1;
        SET @failures +=
            NCHAR(10) + N'  report.' + @view_name +
            N' -> Msg ' + CAST(ERROR_NUMBER() AS nvarchar(10)) +
            N': ' + ERROR_MESSAGE();
        PRINT N'FAIL: report.' + @view_name + N' -> Msg ' + CAST(ERROR_NUMBER() AS nvarchar(10)) + N': ' + ERROR_MESSAGE();
    END CATCH;

    FETCH NEXT FROM view_sweep INTO @view_name;
END;

CLOSE view_sweep;
DEALLOCATE view_sweep;

PRINT N'Report view execution sweep: ' + CAST(@executed AS nvarchar(10)) + N' executed clean, ' + CAST(@failed AS nvarchar(10)) + N' failed.';

IF @failed > 0
BEGIN
    DECLARE @error_message nvarchar(2048) = N'Report view execution sweep failed for ' + CAST(@failed AS nvarchar(10)) + N' view(s):' + LEFT(@failures, 1900);
    THROW 50069, @error_message, 1;
END;
GO
