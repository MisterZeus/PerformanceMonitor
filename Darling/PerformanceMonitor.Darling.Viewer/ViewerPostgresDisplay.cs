/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PerformanceMonitor.Darling.Storage;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// Stored PostgreSQL rows, projected into what a grid may show (#2530). Pure and WPF-free, so every rule
/// below is unit-testable without a window — which matters, because the rules ARE the content: each one
/// exists to stop the grid printing a number that would be read as a measurement when it is not one.
///
/// <list type="bullet">
/// <item><b><c>-1</c> is not a value.</b> It is the store's not-applicable sentinel for a duration, a WAL
/// size or a threshold. 0 was rejected for these deliberately — it reads as "started this instant",
/// "retains nothing", "already past its threshold" — so rendering <c>-1</c> raw would be the same mistake
/// one step later.</item>
/// <item><b>Absent is not zero.</b> <c>pg_stat_io</c>'s write side is NULL across the board on Aurora,
/// because backends there do not write data files. A blank cell says that; a <c>0</c> claims a measurement
/// that was never taken.</item>
/// <item><b>NULL recurrence is "cannot tell", not "once".</b> The blocking read returns NULL when the
/// root's own backend id did not resolve, and those are different claims.</item>
/// <item><b>Every timestamp is naive UTC in the store</b> and goes through
/// <see cref="ViewerTimeHelper.ForDisplay"/>, exactly like every other timestamp the viewer renders.</item>
/// <item><b>Blocks are not bytes.</b> <c>temp_blks_written</c> counts blocks, and the block size is a
/// compile-time setting of the server, not a constant. It is labelled in blocks rather than multiplied by
/// an assumed 8kB.</item>
/// </list>
/// </summary>
internal static class PgDisplay
{
    /// <summary>The store's not-applicable sentinel. Named rather than repeated, because reading it as a
    /// number anywhere is the defect this class exists to prevent.</summary>
    internal const long NotApplicable = -1;

    /// <summary>What an unmeasured / not-applicable cell shows. An em dash, not "0" and not blank: blank
    /// reads as "nothing here yet" and 0 reads as a measurement.</summary>
    internal const string NotApplicableText = "—";

    internal static string Bytes(long value)
    {
        if (value <= NotApplicable)
        {
            return NotApplicableText;
        }

        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double scaled = value;
        var unit = 0;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:N0} B"
            : string.Create(CultureInfo.CurrentCulture, $"{scaled:N1} {units[unit]}");
    }

    /// <summary>A signed byte delta, for the "is this growing" columns. Zero is stated rather than blanked:
    /// "flat" is an answer, and an empty cell would read as "not measured".</summary>
    internal static string ByteDelta(long from, long to)
    {
        if (from <= NotApplicable || to <= NotApplicable)
        {
            return NotApplicableText;
        }

        var delta = to - from;
        return delta == 0 ? "flat" : (delta > 0 ? "+" : "-") + Bytes(Math.Abs(delta));
    }

    /// <summary>A block count, LABELLED as blocks. PostgreSQL's block size is a compile-time setting of
    /// the server, so converting to bytes here would put a fabricated figure next to measured ones.</summary>
    internal static string Blocks(long value) =>
        value <= NotApplicable
            ? NotApplicableText
            : string.Create(CultureInfo.CurrentCulture, $"{value:N0} blocks");

    internal static string Count(long value) =>
        value <= NotApplicable ? NotApplicableText : value.ToString("N0", CultureInfo.CurrentCulture);

    internal static string CountDelta(long from, long to)
    {
        var delta = to - from;
        return delta == 0 ? "flat" : (delta > 0 ? "+" : "") + delta.ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>A duration in milliseconds, or the not-applicable dash for the <c>-1</c> sentinel.</summary>
    internal static string Milliseconds(long ms)
    {
        if (ms <= NotApplicable)
        {
            return NotApplicableText;
        }

        return ms < 1000
            ? $"{ms:N0} ms"
            : TimeSpan.FromMilliseconds(ms).ToString(ms < 3_600_000 ? @"mm\:ss" : @"d\.hh\:mm\:ss", CultureInfo.CurrentCulture);
    }

    internal static string Timestamp(DateTime? utc) =>
        utc is { } value
            ? ViewerTimeHelper.ForDisplay(value).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : string.Empty;

    /// <summary>A percentage of a total, or the dash when the total is zero — which is "nothing happened",
    /// not "0%".</summary>
    internal static string Percent(long part, long total) =>
        total <= 0
            ? NotApplicableText
            : string.Create(CultureInfo.CurrentCulture, $"{part * 100.0 / total:N1}%");

    // ── Vacuum ───────────────────────────────────────────────────────────────────────────────────

    internal sealed class XminRow
    {
        public string Source { get; init; } = "";
        public string IsWinnerText { get; init; } = "";
        public string XminAge { get; init; } = "";
        public string PeakXminAge { get; init; } = "";
        public string WinnerShare { get; init; } = "";
        public string MeasuredAt { get; init; } = "";
        public string Holder { get; init; } = "";
        public string Detail { get; init; } = "";
    }

    internal static XminRow Xmin(DarlingPgXminReader.PgXminRow row) => new()
    {
        Source = row.Source,
        IsWinnerText = row.IsWinner ? "yes" : "",
        XminAge = Count(row.XminAge),
        PeakXminAge = Count(row.PeakXminAge),
        /* The share, not the raw count. A slot that won 58 of 60 samples is a standing problem someone
           needs to own; a session that won twice is a query that ran long and finished. Reporting only the
           current holder makes those two look the same. */
        WinnerShare = row.Samples <= 0
            ? NotApplicableText
            : string.Create(CultureInfo.CurrentCulture, $"{row.SamplesAsWinner:N0} of {row.Samples:N0} samples"),
        MeasuredAt = Timestamp(row.MeasuredAt),
        Holder = row.Holder ?? "",
        Detail = row.Detail ?? "",
    };

    internal sealed class AutovacuumRow
    {
        public string DatabaseName { get; init; } = "";
        public string SchemaName { get; init; } = "";
        public string TableName { get; init; } = "";
        public string AutovacuumState { get; init; } = "";
        public bool AutovacuumDisabled { get; init; }
        public string DeadTuples { get; init; } = "";
        public string VacuumThreshold { get; init; } = "";
        public string ThresholdRatio { get; init; } = "";
        public string DeadTupleTrend { get; init; } = "";
        public string InsertsSinceVacuum { get; init; } = "";
        public string InsertVacuumThreshold { get; init; } = "";
        public string LastVacuum { get; init; } = "";
        public string LastAutovacuum { get; init; } = "";
        public string LastAnalyze { get; init; } = "";
        public string LastAutoanalyze { get; init; } = "";
        public string AutovacuumCount { get; init; } = "";
        public string LiveTuples { get; init; } = "";
        public string DeadTuplePct { get; init; } = "";
        public string ModsSinceAnalyze { get; init; } = "";
        public string AnalyzeThreshold { get; init; } = "";
        public string TotalSize { get; init; } = "";
        public string MeasuredAt { get; init; } = "";
    }

    internal static AutovacuumRow Autovacuum(DarlingPgAutovacuumReader.PgAutovacuumRow row) => new()
    {
        DatabaseName = row.DatabaseName ?? "",
        SchemaName = row.SchemaName ?? "",
        TableName = row.TableName ?? "",
        AutovacuumState = row.AutovacuumDisabled ? "DISABLED" : "enabled",
        AutovacuumDisabled = row.AutovacuumDisabled,
        DeadTuples = Count(row.DeadTuples),
        VacuumThreshold = Count(row.VacuumThreshold),
        /* Ratio to the table's OWN threshold, which is what makes a small hot table and a huge cold one
           comparable at all. A never-analyzed table can have threshold 0, where a ratio is undefined
           rather than infinite. */
        ThresholdRatio = row.VacuumThreshold <= 0
            ? NotApplicableText
            : string.Create(CultureInfo.CurrentCulture, $"{row.DeadTuples / (double)row.VacuumThreshold:N1}x"),
        /* Climbing dead tuples mean autovacuum is losing; a flat figure well past the threshold usually
           means it is blocked or switched off. Those need different fixes, so the trend is a column rather
           than something to work out from two visits. */
        DeadTupleTrend = CountDelta(row.FirstDeadTuples, row.DeadTuples),
        InsertsSinceVacuum = Count(row.InsertsSinceVacuum),
        /* -1 here is not "no threshold": it is a major with no autovacuum_vacuum_insert_threshold at all. */
        InsertVacuumThreshold = row.InsertVacuumThreshold <= NotApplicable
            ? "n/a on this version"
            : Count(row.InsertVacuumThreshold),
        /* Manual and automatic runs are separate columns because they answer different questions: a table
           whose only vacuums are manual is one somebody is nursing, and that is a finding about the
           configuration rather than about the workload. */
        LastVacuum = Timestamp(row.LastVacuum),
        LastAutovacuum = Timestamp(row.LastAutovacuum),
        LastAnalyze = Timestamp(row.LastAnalyze),
        LastAutoanalyze = Timestamp(row.LastAutoanalyze),
        /* Zero is the finding, not a missing value: a table autovacuum has NEVER processed is the classic
           wraparound route, because relfrozenxid never advances. */
        AutovacuumCount = Count(row.AutovacuumCount),
        LiveTuples = Count(row.LiveTuples),
        /* The dead-tuple SHARE, which is what bloat actually is. The raw count beside a threshold says
           whether vacuum is due; the share says how much of the table is already waste. */
        DeadTuplePct = Percent(row.DeadTuples, row.LiveTuples + row.DeadTuples),
        /* The ANALYZE half of the same story, and it is genuinely separate: a table can be vacuumed on
           schedule and still have statistics old enough to give the planner the wrong row counts. */
        ModsSinceAnalyze = Count(row.ModsSinceAnalyze),
        AnalyzeThreshold = Count(row.AnalyzeThreshold),
        TotalSize = Bytes(row.TotalBytes),
        MeasuredAt = Timestamp(row.MeasuredAt),
    };

    internal sealed class WraparoundRow
    {
        public string DatabaseName { get; init; } = "";
        public string FrozenXidAge { get; init; } = "";
        public double PctTowardEmergencyVacuum { get; init; }
        public double PctTowardWraparound { get; init; }
        public string XidsRemaining { get; init; } = "";
        public string MinMultiXidAge { get; init; } = "";
        public double PctTowardMultixactEmergency { get; init; }
        public double PctTowardMultixactWraparound { get; init; }
        public string MultiXidsRemaining { get; init; } = "";
        public string FreezeMaxAge { get; init; } = "";
        public string MultixactFreezeMaxAge { get; init; } = "";
        public string WindowPeakFrozenXidAge { get; init; } = "";
        public string WindowPeakMinMultiXidAge { get; init; } = "";
        public string ConnectionsAllowed { get; init; } = "";
        public string MeasuredAt { get; init; } = "";
    }

    internal static WraparoundRow Wraparound(DarlingPgWraparoundReader.PgWraparoundRow row) => new()
    {
        DatabaseName = row.DatabaseName,
        FrozenXidAge = Count(row.FrozenXidAge),
        PctTowardEmergencyVacuum = Math.Round(row.PctTowardEmergencyVacuum, 1),
        PctTowardWraparound = Math.Round(row.PctTowardWraparound, 1),
        XidsRemaining = Count(row.XidsRemaining),
        MinMultiXidAge = Count(row.MinMultiXidAge),
        PctTowardMultixactEmergency = Math.Round(row.PctTowardMultixactEmergency, 1),
        /* The multixact side has its OWN shutdown ceiling, reached independently of the XID one - a
           workload heavy on row-level share locks or subtransactions gets there first, and nothing on the
           XID columns would say so. */
        PctTowardMultixactWraparound = Math.Round(row.PctTowardMultixactWraparound, 1),
        MultiXidsRemaining = Count(row.MultiXidsRemaining),
        /* The percentages beside these are graded against THIS cluster's settings, not a constant, so the
           settings themselves are a column: two databases at "80% to emergency" are different distances from
           trouble when their freeze_max_age differs, and nothing else on the row would say so. */
        FreezeMaxAge = Count(row.AutovacuumFreezeMaxAge),
        MultixactFreezeMaxAge = Count(row.AutovacuumMultixactFreezeMaxAge),
        WindowPeakFrozenXidAge = Count(row.WindowPeakFrozenXidAge),
        WindowPeakMinMultiXidAge = Count(row.WindowPeakMinMultiXidAge),
        /* datallowconn=false is why a database can sit at 99% of wraparound and never be vacuumed by
           anything that connects to it. It is the finding, not a footnote. */
        ConnectionsAllowed = row.AllowsConnections ? "allowed" : "NOT ALLOWED",
        MeasuredAt = Timestamp(row.MeasuredAt),
    };

    // ── Waits, I/O, replication ──────────────────────────────────────────────────────────────────

    internal sealed class WaitRow
    {
        public string WaitType { get; init; } = "";
        public string WaitEvent { get; init; } = "";
        public string TotalWaits { get; init; } = "";
        public double TotalWaitTimeMs { get; init; }
        public double AvgWaitTimeMs { get; init; }
    }

    internal static WaitRow Wait(DarlingPgWaitReader.PgWaitRow row) => new()
    {
        WaitType = row.WaitType,
        WaitEvent = row.WaitEvent,
        TotalWaits = Count(row.TotalWaits),
        TotalWaitTimeMs = Math.Round(row.TotalWaitTimeMs, 1),
        AvgWaitTimeMs = Math.Round(row.AvgWaitTimeMs, 3),
    };

    internal sealed class IoRow
    {
        public string BackendType { get; init; } = "";
        public string ObjectType { get; init; } = "";
        public string Context { get; init; } = "";

        /// <summary>What this context MEANS, from the shared reader — the one copy the MCP surface also
        /// prints. Shown as the Context cell's tooltip rather than a column: it is a paragraph, and it is
        /// the dimension with no SQL Server counterpart, so an operator meeting `bulkread` for the first
        /// time needs it and one who knows it does not want it eating the grid.</summary>
        public string ContextMeaning { get; init; } = "";

        public string Reads { get; init; } = "";
        public double ReadTimeMs { get; init; }

        /// <summary>Per-read latency — the figure that separates "a lot of I/O" from "slow I/O", which have
        /// completely different remedies.</summary>
        public string AvgReadMs { get; init; } = "";

        public string Hits { get; init; } = "";

        /// <summary>A hit ratio scoped to THIS combination, the only scope where it means anything: a
        /// server-wide ratio averages bulkread's deliberate misses together with normal-context ones and
        /// understates both.</summary>
        public string HitPct { get; init; } = "";

        /// <summary>This combination's share of the window's total read TIME — how the grid's order was
        /// decided, made legible. A row at 4% of the reads and 60% of the read time is the finding.</summary>
        public string PctOfTotalReadTime { get; init; } = "";

        public string Extends { get; init; } = "";
        public string ExtendTimeMs { get; init; } = "";
        public string Evictions { get; init; } = "";
        public string Reuses { get; init; } = "";
        public string Writes { get; init; } = "";
        public string WriteTimeMs { get; init; } = "";
        public string OpSize { get; init; } = "";
        public string StatsReset { get; init; } = "";
    }

    /// <summary>
    /// Projects the whole I/O result at once, because two of its columns are SHARES of the window and a
    /// per-row projection cannot compute a denominator it never sees. Ordering the grid by read time and
    /// then not saying what share that is leaves the reader to divide by hand.
    /// </summary>
    internal static List<IoRow> IoRows(IReadOnlyList<DarlingPgIoReader.PgIoRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var totalReadTime = rows.Sum(r => r.ReadTimeMs);
        return rows.Select(r => Io(r, totalReadTime)).ToList();
    }

    internal static IoRow Io(DarlingPgIoReader.PgIoRow row, double totalReadTimeMs = 0) => new()
    {
        BackendType = row.BackendType ?? "",
        ObjectType = row.ObjectType ?? "",
        Context = row.Context ?? "",
        ContextMeaning = DarlingPgIoReader.ContextMeaning(row.Context),
        Reads = Count(row.Reads),
        ReadTimeMs = Math.Round(row.ReadTimeMs, 1),
        AvgReadMs = row.Reads > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{row.ReadTimeMs / row.Reads:N3}")
            : NotApplicableText,
        Hits = Count(row.Hits),
        HitPct = Percent(row.Hits, row.Hits + row.Reads),
        PctOfTotalReadTime = totalReadTimeMs > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{row.ReadTimeMs / totalReadTimeMs * 100:N1}%")
            : NotApplicableText,
        Extends = Count(row.Extends),
        ExtendTimeMs = string.Create(CultureInfo.CurrentCulture, $"{Math.Round(row.ExtendTimeMs, 1):N1}"),
        Evictions = Count(row.Evictions),
        Reuses = Count(row.Reuses),
        /* "not tracked", not 0. On Aurora the whole pg_stat_io write side is NULL because backends there
           do not write data files, and the read carries WriteCountersTracked precisely so this cell can
           tell that apart from a server that wrote nothing. */
        Writes = row.WriteCountersTracked ? Count(row.Writes) : "not tracked",
        WriteTimeMs = row.WriteCountersTracked
            ? Math.Round(row.WriteTimeMs, 1).ToString("N1", CultureInfo.CurrentCulture)
            : "not tracked",
        OpSize = Bytes(row.OpBytes),
        StatsReset = Timestamp(row.StatsReset),
    };

    internal sealed class SlotRow
    {
        public string SlotName { get; init; } = "";
        public string SlotType { get; init; } = "";
        public string ActiveText { get; init; } = "";
        public string WalStatus { get; init; } = "";
        public string RetainedWal { get; init; } = "";
        public string RetainedWalTrend { get; init; } = "";
        public string SafeWalSize { get; init; } = "";
        public string XminAge { get; init; } = "";
        public string CatalogXminAge { get; init; } = "";
        public string InactiveSince { get; init; } = "";
        public string DatabaseName { get; init; } = "";
        public string Plugin { get; init; } = "";
        public string InvalidationReason { get; init; } = "";

        /// <summary>The stored <c>conflicting</c> flag: a logical slot whose needed rows were vacuumed away
        /// by a recovery conflict. It is a DIFFERENT failure from invalidation-by-WAL-size and needs a
        /// different response (the subscriber must be re-created either way, but the cause is
        /// <c>hot_standby_feedback</c> rather than <c>max_slot_wal_keep_size</c>), so it is its own column
        /// rather than folded into the invalidation reason.</summary>
        public string Conflicting { get; init; } = "";

        public string MeasuredAt { get; init; } = "";
        public bool IsInvalidated { get; init; }
        public bool IsInactive { get; init; }
    }

    internal static SlotRow Slot(DarlingPgSlotReader.PgSlotRow row) => new()
    {
        SlotName = row.SlotName,
        SlotType = row.SlotType ?? "",
        ActiveText = row.IsActive ? "active" : "INACTIVE",
        WalStatus = row.WalStatus ?? "",
        RetainedWal = Bytes(row.RetainedWalBytes),
        RetainedWalTrend = ByteDelta(row.FirstRetainedWalBytes, row.RetainedWalBytes),
        SafeWalSize = Bytes(row.SafeWalSizeBytes),
        XminAge = Count(row.XminAge),
        CatalogXminAge = Count(row.CatalogXminAge),
        InactiveSince = Timestamp(row.InactiveSince),
        DatabaseName = row.DatabaseName ?? "",
        Plugin = row.Plugin ?? "",
        InvalidationReason = row.InvalidationReason ?? "",
        Conflicting = row.Conflicting ? "CONFLICTING" : "",
        MeasuredAt = Timestamp(row.MeasuredAt),
        /* An invalidated slot has already lost its WAL: the replica behind it needs rebuilding, and that
           is a different day from an inactive slot that is merely accumulating. A CONFLICTING slot is in
           the same category — its subscriber cannot continue — so it shares the highlight even though the
           cause and the fix are different. */
        IsInvalidated = !string.IsNullOrEmpty(row.InvalidationReason)
                        || row.Conflicting
                        || string.Equals(row.WalStatus, "lost", StringComparison.OrdinalIgnoreCase),
        IsInactive = !row.IsActive,
    };

    // ── Activity ─────────────────────────────────────────────────────────────────────────────────

    internal sealed class ChainRow
    {
        public string CapturedAt { get; init; } = "";
        public int RootPid { get; init; }

        /// <summary>The collector's synthetic backend id, which is what "seen as root" is counted on — pids
        /// are reused, so two captures agreeing on a pid are not necessarily the same backend. <c>0</c> is
        /// the vanished-blocker sentinel and shows as unknown rather than as an id.</summary>
        public string RootBackendId { get; init; } = "";

        public string Databases { get; init; } = "";
        public int TotalVictims { get; init; }
        public int DirectVictims { get; init; }
        public string Depth { get; init; } = "";
        public string WorstVictimWait { get; init; } = "";
        public string RootState { get; init; } = "";
        public string RootXactDuration { get; init; } = "";
        public string RootUsername { get; init; } = "";
        public string RootApplicationName { get; init; } = "";
        public string SamplesAsRoot { get; init; } = "";
        public string RootQuery { get; init; } = "";
        public string WorstVictimQuery { get; init; } = "";
        public string Caveats { get; init; } = "";
    }

    internal static ChainRow Chain(DarlingPgBlockingReader.PgBlockingChainRow row)
    {
        var caveats = new List<string>();
        if (row.ChainMayBeTruncated)
        {
            caveats.Add("chain hit the 32-level walk cap");
        }
        if (row.QueryTextMayBeTruncated)
        {
            caveats.Add("query text truncated at track_activity_query_size");
        }

        return new ChainRow
        {
            CapturedAt = Timestamp(row.CapturedAt),
            RootPid = row.RootPid,
            RootBackendId = row.RootBackendId == 0
                ? "unknown"
                : row.RootBackendId.ToString("N0", CultureInfo.CurrentCulture),
            Databases = string.Join(", ", row.Databases.Where(d => !string.IsNullOrEmpty(d))),
            TotalVictims = row.TotalVictims,
            DirectVictims = row.DirectVictims,
            Depth = row.MaxDepth.ToString(CultureInfo.CurrentCulture) + (row.ChainMayBeTruncated ? " (capped)" : ""),
            WorstVictimWait = Milliseconds(row.WorstVictimWaitMs),
            /* idle in transaction is the state worth naming on its own: the root is not running anything,
               so nothing will finish and release the lock without intervention. */
            RootState = row.RootIsIdleInTransaction ? "idle in transaction" : row.RootState ?? "",
            RootXactDuration = Milliseconds(row.RootXactDurationMs),
            RootUsername = row.RootUsername ?? "",
            RootApplicationName = row.RootApplicationName ?? "",
            /* NULL is "cannot tell" — the blocker's own row had already left pg_stat_activity, so it landed
               on the collector's synthetic backend id and counting it would conflate unrelated incidents. */
            SamplesAsRoot = row.SamplesAsRoot is { } samples
                ? string.Create(CultureInfo.CurrentCulture, $"{samples:N0} captures")
                : "unknown",
            RootQuery = row.RootQuery ?? "",
            WorstVictimQuery = row.WorstVictimQuery ?? "",
            Caveats = string.Join("; ", caveats),
        };
    }

    internal sealed class CycleRow
    {
        public string CapturedAt { get; init; } = "";
        public int ParticipantCount { get; init; }
        public string Pids { get; init; } = "";
        public string DatabaseName { get; init; } = "";
        public string ApplicationName { get; init; } = "";
        public int BlockedBehindCount { get; init; }
        public string BlockedBehindPids { get; init; } = "";
    }

    internal static CycleRow Cycle(DarlingPgBlockingReader.PgBlockingCycleRow row) => new()
    {
        CapturedAt = Timestamp(row.CapturedAt),
        ParticipantCount = row.ParticipantCount,
        Pids = string.Join(", ", row.Pids),
        DatabaseName = row.DatabaseName ?? "",
        ApplicationName = row.ApplicationName ?? "",
        BlockedBehindCount = row.BlockedBehindCount,
        BlockedBehindPids = string.Join(", ", row.BlockedBehindPids),
    };

    internal sealed class StatementRow
    {
        /// <summary>A string, not a number. PostgreSQL <c>queryid</c> is an <c>int8</c> that routinely
        /// exceeds 2^53, and rendering it through anything that rounds is the one field whose entire
        /// purpose is joining back to <c>pg_stat_statements</c> (#2548).</summary>
        public string QueryId { get; init; } = "";

        /// <summary>The database OID, which is half the grain of this read — one queryid appears once per
        /// database it ran in, and two rows with the same statement text are not a duplicate.</summary>
        public string DatabaseId { get; init; } = "";

        public string Calls { get; init; } = "";
        public string TotalExecTimeMs { get; init; } = "";
        public string AvgExecTimeMs { get; init; } = "";
        public double MaxExecTimeMs { get; init; }
        public string RowsReturned { get; init; } = "";

        /// <summary>Buffer-cache hit share for this statement, over BOTH cache tiers Aurora reports — the
        /// shared buffers and the Optimized Read cache — against both miss sources. Reported as one ratio
        /// rather than four block counters, which are the same fact in a form nobody reads correctly.</summary>
        public string CacheHitPct { get; init; } = "";

        public string TempRead { get; init; } = "";
        public string TempWritten { get; init; } = "";
        public string PeakMemory { get; init; } = "";
        public string WalWritten { get; init; } = "";
        public string QueryText { get; init; } = "";
    }

    internal static StatementRow Statement(DarlingPgStatementReader.PgStatementRow row) => new()
    {
        QueryId = row.QueryId.ToString(CultureInfo.InvariantCulture),
        DatabaseId = row.DatabaseId.ToString("N0", CultureInfo.CurrentCulture),
        Calls = Count(row.Calls),
        TotalExecTimeMs = Count(row.TotalExecTimeMs),
        AvgExecTimeMs = row.Calls <= 0
            ? NotApplicableText
            : string.Create(CultureInfo.CurrentCulture, $"{row.TotalExecTimeMs / (double)row.Calls:N1}"),
        MaxExecTimeMs = Math.Round(row.MaxExecTimeMs, 1),
        RowsReturned = Count(row.RowsReturned),
        CacheHitPct = Percent(
            row.SharedBlocksHit + row.OrcacheBlocksHit,
            row.SharedBlocksHit + row.OrcacheBlocksHit + row.SharedBlocksRead + row.StorageBlocksRead),
        /* BLOCKS, and labelled as such — both of them. The block size is a compile-time setting of the
           server, so multiplying by an assumed 8kB would put a fabricated byte count next to real ones.
           Temp READ sits beside temp written because a spill that is written and never read back is a
           different (and cheaper) event than one the query then re-reads. */
        TempRead = Blocks(row.TempBlocksRead),
        TempWritten = Blocks(row.TempBlocksWritten),
        PeakMemory = Bytes(row.MaxPeakMemBytes),
        WalWritten = Bytes(row.WalBytes),
        /* Null is "no text captured for this queryid yet" — text refreshes hourly, and a major-version
           upgrade re-keys every queryid — which is a different statement from an empty query. */
        QueryText = row.QueryText ?? "(statement text not captured yet)",
    };

    internal sealed class DatabaseRow
    {
        public string DatabaseName { get; init; } = "";
        public string TempFiles { get; init; } = "";
        public string TempBytes { get; init; } = "";
        public string Deadlocks { get; init; } = "";
        public string CacheHitPct { get; init; } = "";
        public string XactCommit { get; init; } = "";
        public string XactRollback { get; init; } = "";
        public string RollbackPct { get; init; } = "";
        public int SampleCount { get; init; }

        /// <summary>The stored <c>stats_reset</c> timestamp. Shown beside the caveat that counts resets in
        /// the window, because "reset once" and "reset at 14:02" send you to different places.</summary>
        public string StatsReset { get; init; } = "";

        public string Caveats { get; init; } = "";
    }

    internal static DatabaseRow Database(DarlingPgDatabaseReader.PgDatabaseRow row)
    {
        var caveats = new List<string>();
        /* Every total beside it is a LOWER BOUND when the counters were reset inside the window, so this
           has to sit on the row rather than in a footnote nobody reads. */
        if (row.StatsResetCount > 0)
        {
            caveats.Add($"counters reset {row.StatsResetCount}x in window");
        }
        if (row.CounterRewindCount > 0)
        {
            caveats.Add($"counters rewound {row.CounterRewindCount}x");
        }

        return new DatabaseRow
        {
            DatabaseName = row.DatabaseName ?? "",
            TempFiles = Count(row.TempFiles),
            TempBytes = Bytes(row.TempBytes),
            Deadlocks = Count(row.Deadlocks),
            CacheHitPct = Percent(row.BlksHit, row.BlksHit + row.BlksRead),
            XactCommit = Count(row.XactCommit),
            XactRollback = Count(row.XactRollback),
            RollbackPct = Percent(row.XactRollback, row.XactCommit + row.XactRollback),
            SampleCount = row.SampleCount,
            StatsReset = Timestamp(row.StatsReset),
            Caveats = string.Join("; ", caveats),
        };
    }
}
