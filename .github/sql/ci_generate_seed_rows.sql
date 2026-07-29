/*
Copyright 2026 Darling Data, LLC
https://www.erikdarling.com/

DEV TOOL - not run by CI. Regenerates the generated section of ci_seed_report_sources.sql from a
database carrying the current install schema: run it there, paste the PRINTed INSERTs between the
"generated" markers, and keep the hand-authored history pairs at the bottom of that file (#1669).
*/

SET NOCOUNT ON;
/* Generate one INSERT per collect/config base table: NOT NULL, non-identity, non-computed columns.
   Values are type-driven; time-ish columns land 5 minutes ago so "today"/"last hour" views see them. */
DECLARE @sql nvarchar(max);
DECLARE t CURSOR LOCAL FAST_FORWARD FOR
    SELECT s.name, tb.name
    FROM sys.tables AS tb
    JOIN sys.schemas AS s ON s.schema_id = tb.schema_id
    WHERE s.name IN (N'collect', N'config')
    AND   tb.is_ms_shipped = 0
    ORDER BY s.name, tb.name;
DECLARE @s sysname, @tb sysname;
OPEN t;
FETCH NEXT FROM t INTO @s, @tb;
WHILE @@FETCH_STATUS = 0
BEGIN
    DECLARE @cols nvarchar(max) = N'', @vals nvarchar(max) = N'';
    SELECT
        @cols += CASE WHEN @cols = N'' THEN N'' ELSE N', ' END + QUOTENAME(c.name),
        @vals += CASE WHEN @vals = N'' THEN N'' ELSE N', ' END +
            CASE
                WHEN c.name = N'severity' THEN N'N''CRITICAL'''
                WHEN tp.name = N'datetimeoffset' THEN N'SYSDATETIMEOFFSET()'
                WHEN c.name IN (N'query_text', N'query_plan_text', N'query_sql_text', N'statement_text', N'plan_xml_compressed') AND tp.name IN (N'varbinary')
                    THEN N'COMPRESS(N''SELECT 1 AS ci_seed;'')'
                WHEN tp.name IN (N'nvarchar', N'varchar', N'sysname', N'nchar', N'char') THEN N'N''ci'''
                WHEN tp.name IN (N'datetime2', N'datetime', N'smalldatetime') THEN N'DATEADD(MINUTE, -5, SYSDATETIME())'
                WHEN tp.name = N'date' THEN N'CONVERT(date, SYSDATETIME())'
                WHEN tp.name = N'time' THEN N'CONVERT(time, SYSDATETIME())'
                WHEN tp.name IN (N'bit') THEN N'1'
                WHEN tp.name IN (N'int', N'bigint', N'smallint', N'tinyint', N'decimal', N'numeric', N'float', N'real', N'money') THEN N'1'
                WHEN tp.name IN (N'varbinary', N'binary') THEN N'0x00'
                WHEN tp.name = N'uniqueidentifier' THEN N'NEWID()'
                WHEN tp.name = N'xml' THEN N'CONVERT(xml, N''<ci/>'')'
                ELSE N'NULL /* unhandled: ' + tp.name + N' */'
            END
    FROM sys.columns AS c
    JOIN sys.types AS tp ON tp.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(QUOTENAME(@s) + N'.' + QUOTENAME(@tb))
    AND   c.is_identity = 0
    AND   c.is_computed = 0
    AND   c.is_nullable = 0;
    IF @cols <> N''
        PRINT N'IF NOT EXISTS (SELECT 1/0 FROM ' + QUOTENAME(@s) + N'.' + QUOTENAME(@tb) + N' WHERE 1 = 1) INSERT INTO ' + QUOTENAME(@s) + N'.' + QUOTENAME(@tb) + N' (' + @cols + N') VALUES (' + @vals + N');';
    ELSE
        PRINT N'/* ' + QUOTENAME(@s) + N'.' + QUOTENAME(@tb) + N': all columns nullable/identity - DEFAULT VALUES */';
    FETCH NEXT FROM t INTO @s, @tb;
END;
CLOSE t; DEALLOCATE t;
