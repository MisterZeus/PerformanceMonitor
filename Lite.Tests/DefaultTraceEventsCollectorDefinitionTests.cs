/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lite.Tests.Helpers;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the cross-SKU parity contract of the shared default_trace_events collector definition (the built-in
/// Default Trace read via sys.fn_trace_gettable): the server-side rollover-file read with the base-path
/// normalization, the CURATED event set and its four must-handles — the config-change slice DROPPED
/// (Darling's config-snapshot diff owns it), the auto-grow/shrink stall gate, the tempdb / _WA_ DDL
/// exclusions — the event_time watermark with its all-history FIRST-run cutoff (NOT the XE collectors'
/// 10-minute fallback), the collect-everywhere-but-Azure-SQL-DB AppliesTo, the per-server excluded-database
/// splice, the 21-column payload, and the null-tolerant ReadAsync/WritePayload. An intentional change to any
/// of these must consciously update this test.
/// </summary>
public sealed class DefaultTraceEventsCollectorDefinitionTests
{
    private static readonly RecordingCollectorDeltaCalculator s_deltas = new();

    private static CollectorContext MakeContext(
        bool isAzureSqlDb = false,
        bool isAzureManagedInstance = false,
        DateTime? watermark = null,
        DateTime? collectionTime = null,
        string[]? excludedDatabases = null,
        bool hasCollectedBefore = false,
        bool collectSchemaChanges = true)
        => new()
        {
            ServerId = 42,
            ServerName = "test-server",
            CollectionTime = collectionTime ?? new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc),
            Deltas = s_deltas,
            Target = new CollectorTargetInfo { IsAzureSqlDb = isAzureSqlDb, IsAzureManagedInstance = isAzureManagedInstance },
            Watermark = watermark,
            HasCollectedBefore = hasCollectedBefore,
            ExcludedDatabases = excludedDatabases ?? Array.Empty<string>(),
            CollectSchemaChangeEvents = collectSchemaChanges,
        };

    [Fact]
    public void Identity_Pinned()
    {
        Assert.Equal("default_trace_events", DefaultTraceEventsCollector.Instance.Name);
        Assert.Equal("default_trace_events", DefaultTraceEventsCollector.Instance.TargetTable);
        Assert.Equal("default_trace_event_id", DefaultTraceEventsCollector.Instance.PrefixIdColumnName);
        Assert.Equal("event_time", DefaultTraceEventsCollector.Instance.WatermarkColumn);
    }

    [Fact]
    public void BuildQuery_ReadsDefaultTraceViaFnTraceGettable_ServerSide()
    {
        var text = DefaultTraceEventsCollector.Instance.BuildQuery(MakeContext()).Text;

        Assert.Contains("sys.traces", text, StringComparison.Ordinal);
        Assert.Contains("sys.fn_trace_gettable", text, StringComparison.Ordinal);
        Assert.Contains("sys.trace_events", text, StringComparison.Ordinal);
        /* Base-path normalization so ALL rollover files are read (strip _NN, re-append the extension). */
        Assert.Contains("REVERSE(t.path)", text, StringComparison.Ordinal);
        Assert.Contains("t.max_files", text, StringComparison.Ordinal);
        Assert.Contains("t.is_default = 1", text, StringComparison.Ordinal);
        Assert.Contains("t.status = 1", text, StringComparison.Ordinal);
        Assert.Contains("ft.StartTime > @cutoff_time", text, StringComparison.Ordinal);
        Assert.Contains("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED", text, StringComparison.Ordinal);
        Assert.Contains("OPTION(RECOMPILE)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildQuery_RolloverPathStrip_FallsBackToBasePath_WhenNoUnderscore()
    {
        /* SQL Server on Linux names the initial trace file without a rollover suffix (log.trc, not
           log_1.trc). An unconditional strip finds no underscore, so LEFT takes the full path and
           the re-appended extension doubles it (log.trc.trc), and fn_trace_gettable errors on the
           nonexistent file. The CASE must fall back to the base t.path when CHARINDEX finds no underscore. */
        var text = DefaultTraceEventsCollector.Instance.BuildQuery(MakeContext()).Text;

        Assert.Contains("WHEN CHARINDEX(N'_', REVERSE(t.path)) > 0", text, StringComparison.Ordinal);
        Assert.Contains("ELSE t.path", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildQuery_CapturesTheCuratedEventSet()
    {
        var text = DefaultTraceEventsCollector.Instance.BuildQuery(MakeContext()).Text;

        Assert.Contains("Server Memory Change", text, StringComparison.Ordinal);
        Assert.Contains("Data File Auto Grow", text, StringComparison.Ordinal);
        Assert.Contains("Log File Auto Grow", text, StringComparison.Ordinal);
        Assert.Contains("Data File Auto Shrink", text, StringComparison.Ordinal);
        Assert.Contains("Log File Auto Shrink", text, StringComparison.Ordinal);
        Assert.Contains("N'ErrorLog'", text, StringComparison.Ordinal);
        Assert.Contains("N'Object:Created'", text, StringComparison.Ordinal);
        Assert.Contains("N'Object:Altered'", text, StringComparison.Ordinal);
        Assert.Contains("N'Object:Deleted'", text, StringComparison.Ordinal);
        Assert.Contains("N'Audit Change Audit Event'", text, StringComparison.Ordinal);
        Assert.Contains("N'Audit DBCC Event'", text, StringComparison.Ordinal);
        Assert.Contains("N'Audit Server Alter Trace Event'", text, StringComparison.Ordinal);

        /* The auto-grow/shrink stall gate (over one second) and the noise exclusions. */
        Assert.Contains("ISNULL(ft.Duration, 0) > 1000000", text, StringComparison.Ordinal);
        Assert.Contains("ISNULL(ft.DatabaseID, 0) NOT IN (1, 3, 4)", text, StringComparison.Ordinal); /* master/model/msdb */
        Assert.Contains("< 32761", text, StringComparison.Ordinal);                                   /* AG system DBs */
        Assert.Contains("ISNULL(ft.DatabaseID, 0) <> 2", text, StringComparison.Ordinal);             /* tempdb DDL */
        Assert.Contains("NOT LIKE N'[_]WA[_]%'", text, StringComparison.Ordinal);                     /* auto-stats DDL */
    }

    [Fact]
    public void BuildQuery_DropsTheConfigChangeSlice_DarlingSnapshotDiffOwnsIt()
    {
        /* Must-handle: the config-change events (Server / Database Configuration Change, Alter Database) the
           Dashboard collector included are DROPPED here so Darling's server/database/trace-flag change
           tracking (the config-snapshot diff) is not double-counted. */
        var text = DefaultTraceEventsCollector.Instance.BuildQuery(MakeContext()).Text;

        Assert.DoesNotContain("Configuration Change", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Alter Database", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildQuery_Watermark_AndFirstRunGuard_TrueFirstRunVsArchivalEmpty()
    {
        /* Steady state: the watermark is used verbatim. */
        var watermark = new DateTime(2026, 7, 9, 11, 55, 0, DateTimeKind.Utc);
        var withWatermark = DefaultTraceEventsCollector.Instance.BuildQuery(MakeContext(watermark: watermark));
        var cutoff = withWatermark.Parameters.Single(p => p.Name == "@cutoff_time");
        Assert.Equal(watermark, cutoff.Value);
        Assert.Equal(CollectorParameterType.DateTime2, cutoff.Type);

        var collectionTime = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

        /* TRUE first run (no watermark, never succeeded): collect EVERYTHING still on disk — a far-past
           sentinel, NOT the XE collectors' collectionTime.AddMinutes(-10). */
        var firstRun = DefaultTraceEventsCollector.Instance.BuildQuery(
            MakeContext(collectionTime: collectionTime, hasCollectedBefore: false));
        var firstCutoff = (DateTime)firstRun.Parameters.Single(p => p.Name == "@cutoff_time").Value!;
        Assert.Equal(new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc), firstCutoff);
        Assert.NotEqual(collectionTime.AddMinutes(-10), firstCutoff);

        /* Archival-emptied hot store (no watermark, but HAS succeeded before): a BOUNDED recent window
           (24 h), NEVER all-history — so we don't re-scan .trc events already in the parquet archive, which
           v_default_trace_events (hot UNION parquet, no dedup) would double-count. The Dashboard's guard. */
        var archivalEmpty = DefaultTraceEventsCollector.Instance.BuildQuery(
            MakeContext(collectionTime: collectionTime, hasCollectedBefore: true));
        var boundedCutoff = (DateTime)archivalEmpty.Parameters.Single(p => p.Name == "@cutoff_time").Value!;
        Assert.Equal(collectionTime.AddHours(-24), boundedCutoff);
        Assert.NotEqual(new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc), boundedCutoff);
    }

    [Fact]
    public void BuildQuery_ExcludedDatabases_IsNullSafe_KeepsServerLevelEvents()
    {
        var noExclusion = DefaultTraceEventsCollector.Instance.BuildQuery(MakeContext());
        Assert.DoesNotContain("@excl_db_0", noExclusion.Text, StringComparison.Ordinal);
        Assert.Equal(2, noExclusion.Parameters.Count); /* @cutoff_time + @include_object_ddl */

        var withExclusion = DefaultTraceEventsCollector.Instance.BuildQuery(
            MakeContext(excludedDatabases: new[] { "ReportingDB", "ScratchDB" }));

        /* NULL-safe: a server-level event with a NULL DatabaseName (Server Memory Change, server-scoped
           audit/ErrorLog) is KEPT — a bare `NOT IN` would evaluate NULL to UNKNOWN and silently drop the
           whole category whenever any database is excluded. */
        Assert.Contains(
            "AND (ft.DatabaseName IS NULL OR ft.DatabaseName NOT IN (@excl_db_0, @excl_db_1))",
            withExclusion.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("AND ft.DatabaseName NOT IN", withExclusion.Text, StringComparison.Ordinal); /* never the bare form */
        Assert.Equal(4, withExclusion.Parameters.Count); /* @cutoff_time + @include_object_ddl + two excluded names */
        Assert.Contains(withExclusion.Parameters, p => p.Name == "@excl_db_0" && (string?)p.Value == "ReportingDB");
        Assert.Contains(withExclusion.Parameters, p => p.Name == "@excl_db_1" && (string?)p.Value == "ScratchDB");
    }

    [Fact]
    public void BuildQuery_ObjectDdlGate_OnByDefault_SuppressedWhenFlagOff()
    {
        /* Default (CollectSchemaChangeEvents = true — Lite's unchanged behavior): the Object DDL slice is
           gated ON via a bound @include_object_ddl = 1, so Object:Created/Altered/Deleted events are collected. */
        var on = DefaultTraceEventsCollector.Instance.BuildQuery(MakeContext());
        Assert.Contains("@include_object_ddl = 1", on.Text, StringComparison.Ordinal);
        var onParameter = on.Parameters.Single(p => p.Name == "@include_object_ddl");
        Assert.Equal(1, onParameter.Value);
        Assert.Equal(CollectorParameterType.Int32, onParameter.Type);

        /* Flag off (Darling's darling.json collectSchemaChangeEvents = false on a noisy/benchmark box, the
           shared collector's equivalent of the Dashboard proc's @include_object_events): the SAME gate binds
           0, dropping the schema-DDL category. It is a bound parameter, not a text fork — so the generated SQL
           is byte-identical either way and the other curated categories are untouched. */
        var off = DefaultTraceEventsCollector.Instance.BuildQuery(MakeContext(collectSchemaChanges: false));
        Assert.Equal(0, off.Parameters.Single(p => p.Name == "@include_object_ddl").Value);
        Assert.Equal(on.Text, off.Text);
    }

    [Fact]
    public void AppliesTo_CollectsEverywhereExceptAzureSqlDb()
    {
        /* No default trace on Azure SQL DB; Managed Instance and on-prem / RDS have it. */
        Assert.False(DefaultTraceEventsCollector.Instance.AppliesTo(new CollectorTargetInfo { IsAzureSqlDb = true }));
        Assert.True(DefaultTraceEventsCollector.Instance.AppliesTo(new CollectorTargetInfo { IsAzureManagedInstance = true }));
        Assert.True(DefaultTraceEventsCollector.Instance.AppliesTo(new CollectorTargetInfo()));
    }

    [Fact]
    public void PayloadColumns_TwentyOneColumns_InAppendOrder()
    {
        var columns = DefaultTraceEventsCollector.Instance.PayloadColumns;

        Assert.Equal(new[]
        {
            "event_time", "event_name", "event_class", "spid", "database_name", "database_id",
            "login_name", "host_name", "application_name", "object_name", "filename",
            "integer_data", "integer_data_2", "text_data", "session_login_name",
            "error_number", "severity", "state", "event_sequence", "duration_us", "end_time",
        }, columns.Select(c => c.Name).ToArray());

        Assert.Equal(CollectorColumnType.Timestamp, columns.Single(c => c.Name == "event_time").Type);
        Assert.Equal(CollectorColumnType.Varchar, columns.Single(c => c.Name == "event_name").Type);
        Assert.Equal(CollectorColumnType.Integer, columns.Single(c => c.Name == "event_class").Type);
        Assert.Equal(CollectorColumnType.BigInt, columns.Single(c => c.Name == "integer_data").Type);
        Assert.Equal(CollectorColumnType.BigInt, columns.Single(c => c.Name == "duration_us").Type);
        Assert.Equal(CollectorColumnType.Timestamp, columns.Single(c => c.Name == "end_time").Type);
    }

    [Fact]
    public async Task ReadAsync_WritePayload_PinsTwentyOneColumnOrder_AndToleratesNulls()
    {
        var context = MakeContext();
        var eventTime = new DateTime(2026, 7, 9, 11, 58, 0, DateTimeKind.Utc);
        var endTime = new DateTime(2026, 7, 9, 11, 58, 1, DateTimeKind.Utc);

        var fullRow = new object[]
        {
            eventTime, "Data File Auto Grow", 92, 55, "SalesDB", 7, "app_login", "APPHOST",
            "SqlClient", (object)DBNull.Value, "S:\\data\\SalesDB.mdf", 128L, 0L, (object)DBNull.Value,
            "app_login", (object)DBNull.Value, (object)DBNull.Value, (object)DBNull.Value, 900001L, 1500000L, endTime,
        };
        var nullRow = Enumerable.Repeat((object)DBNull.Value, 21).ToArray();

        using var reader = new FakeCollectorDataReader(fullRow, nullRow);
        var rows = await DefaultTraceEventsCollector.Instance.ReadAsync(reader, context, CancellationToken.None);
        Assert.Equal(2, rows.Count);

        var writer = new RecordingCollectorRowWriter();
        DefaultTraceEventsCollector.Instance.WritePayload(rows[0], writer, context);

        Assert.Equal(21, writer.Values.Count);
        Assert.Equal(eventTime, writer.Values[0]);
        Assert.Equal("Data File Auto Grow", writer.Values[1]);
        Assert.Equal(92, writer.Values[2]);
        Assert.Equal(55, writer.Values[3]);
        Assert.Equal("SalesDB", writer.Values[4]);
        Assert.Equal(7, writer.Values[5]);
        Assert.Null(writer.Values[9]);         /* object_name NULL */
        Assert.Equal(128L, writer.Values[11]); /* integer_data (pages grown) */
        Assert.Equal(1500000L, writer.Values[19]); /* duration_us (a stall > 1 s) */
        Assert.Equal(endTime, writer.Values[20]);

        /* An all-NULL row still writes 21 values; capture is a point-in-time read with no delta interaction. */
        var nullWriter = new RecordingCollectorRowWriter();
        DefaultTraceEventsCollector.Instance.WritePayload(rows[1], nullWriter, context);
        Assert.Equal(21, nullWriter.Values.Count);
        Assert.All(nullWriter.Values, Assert.Null);
        Assert.Empty(s_deltas.Calls);
    }
}
