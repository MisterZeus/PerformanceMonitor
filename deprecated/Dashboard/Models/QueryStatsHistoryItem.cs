/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

namespace PerformanceMonitorDashboard.Models
{
    public class QueryStatsHistoryItem
    {
        public long CollectionId { get; set; }
        public DateTime CollectionTime { get; set; }
        public string ObjectType { get; set; } = string.Empty;
        public DateTime CreationTime { get; set; }
        public DateTime LastExecutionTime { get; set; }

        // Execution count
        public long ExecutionCount { get; set; }
        public long? ExecutionCountDelta { get; set; }
        public long IntervalExecutions { get; set; }

        // Per-interval rows/spills, computed at load time by the same CreationTime-lifetime
        // walk that produces IntervalExecutions (no SQL-side delta exists for these two).
        public long IntervalRows { get; set; }
        public long IntervalSpills { get; set; }

        // Worker time (CPU) - microseconds in database
        public long TotalWorkerTime { get; set; }
        public long MinWorkerTime { get; set; }
        public long MaxWorkerTime { get; set; }
        public long? TotalWorkerTimeDelta { get; set; }

        // Elapsed time - microseconds in database
        public long TotalElapsedTime { get; set; }
        public long MinElapsedTime { get; set; }
        public long MaxElapsedTime { get; set; }
        public long? TotalElapsedTimeDelta { get; set; }

        // Logical reads
        public long TotalLogicalReads { get; set; }
        public long? TotalLogicalReadsDelta { get; set; }

        // Physical reads
        public long TotalPhysicalReads { get; set; }
        public long MinPhysicalReads { get; set; }
        public long MaxPhysicalReads { get; set; }
        public long? TotalPhysicalReadsDelta { get; set; }

        // Logical writes
        public long TotalLogicalWrites { get; set; }
        public long? TotalLogicalWritesDelta { get; set; }

        // CLR time
        public long TotalClrTime { get; set; }

        // Rows
        public long TotalRows { get; set; }
        public long MinRows { get; set; }
        public long MaxRows { get; set; }

        // Parallelism
        public short MinDop { get; set; }
        public short MaxDop { get; set; }

        // Memory grants (KB)
        public long MinGrantKb { get; set; }
        public long MaxGrantKb { get; set; }
        public long MinUsedGrantKb { get; set; }
        public long MaxUsedGrantKb { get; set; }
        public long MinIdealGrantKb { get; set; }
        public long MaxIdealGrantKb { get; set; }

        // Thread usage
        public int MinReservedThreads { get; set; }
        public int MaxReservedThreads { get; set; }
        public int MinUsedThreads { get; set; }
        public int MaxUsedThreads { get; set; }

        // Spills
        public long TotalSpills { get; set; }
        public long MinSpills { get; set; }
        public long MaxSpills { get; set; }

        // Query identifiers (hex strings from binary columns)
        public string? SqlHandle { get; set; }
        public string? PlanHandle { get; set; }
        public string? QueryHash { get; set; }
        public string? QueryPlanHash { get; set; }

        // Sample interval
        public int? SampleIntervalSeconds { get; set; }

        // Query plan
        public string? QueryPlanXml { get; set; }

        // Display helpers - convert microseconds to milliseconds
        public double TotalWorkerTimeMs => TotalWorkerTime / 1000.0;
        public double MinWorkerTimeMs => MinWorkerTime / 1000.0;
        public double MaxWorkerTimeMs => MaxWorkerTime / 1000.0;
        public double? TotalWorkerTimeDeltaMs => TotalWorkerTimeDelta / 1000.0;

        /* Averages are PER-INTERVAL (delta / delta executions), matching Lite. Lifetime-cumulative
           averages (total / execution_count since plan creation) flatten out over time and hide
           recent regressions; the Total* columns still expose the raw cumulative counters. */
        public double? AvgWorkerTimeMs => ExecutionCountDelta > 0 && TotalWorkerTimeDelta.HasValue
            ? TotalWorkerTimeDelta.Value / (double)ExecutionCountDelta!.Value / 1000.0 : null;

        public double TotalElapsedTimeMs => TotalElapsedTime / 1000.0;
        public double MinElapsedTimeMs => MinElapsedTime / 1000.0;
        public double MaxElapsedTimeMs => MaxElapsedTime / 1000.0;
        public double? TotalElapsedTimeDeltaMs => TotalElapsedTimeDelta / 1000.0;
        public double? AvgElapsedTimeMs => ExecutionCountDelta > 0 && TotalElapsedTimeDelta.HasValue
            ? TotalElapsedTimeDelta.Value / (double)ExecutionCountDelta!.Value / 1000.0 : null;

        public double? AvgLogicalReads => ExecutionCountDelta > 0 && TotalLogicalReadsDelta.HasValue
            ? TotalLogicalReadsDelta.Value / (double)ExecutionCountDelta!.Value : null;
        public double? AvgPhysicalReads => ExecutionCountDelta > 0 && TotalPhysicalReadsDelta.HasValue
            ? TotalPhysicalReadsDelta.Value / (double)ExecutionCountDelta!.Value : null;
        public double? AvgLogicalWrites => ExecutionCountDelta > 0 && TotalLogicalWritesDelta.HasValue
            ? TotalLogicalWritesDelta.Value / (double)ExecutionCountDelta!.Value : null;
        public double? AvgRows => IntervalExecutions > 0 ? IntervalRows / (double)IntervalExecutions : null;

        // Memory in MB
        public double MinGrantMb => MinGrantKb / 1024.0;
        public double MaxGrantMb => MaxGrantKb / 1024.0;
        public double MinUsedGrantMb => MinUsedGrantKb / 1024.0;
        public double MaxUsedGrantMb => MaxUsedGrantKb / 1024.0;
        public double MinIdealGrantMb => MinIdealGrantKb / 1024.0;
        public double MaxIdealGrantMb => MaxIdealGrantKb / 1024.0;
    }
}
