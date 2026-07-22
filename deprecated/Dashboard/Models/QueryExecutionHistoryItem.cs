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
    public class QueryExecutionHistoryItem
    {
        public long CollectionId { get; set; }
        public DateTime CollectionTime { get; set; }
        public long PlanId { get; set; }
        public long CountExecutions { get; set; }

        // Execution type (Regular / Aborted / Exception) - group key
        public string? ExecutionTypeDesc { get; set; }

        // First/last execution time in the interval (server local time)
        public DateTime? ServerFirstExecutionTime { get; set; }
        public DateTime? ServerLastExecutionTime { get; set; }

        // Module (proc/function) the query came from
        public string? ModuleName { get; set; }

        // Duration metrics (microseconds stored, convert to ms for display)
        public long AvgDuration { get; set; }
        public long MinDuration { get; set; }
        public long MaxDuration { get; set; }

        // CPU time metrics (microseconds stored)
        public long AvgCpuTime { get; set; }
        public long MinCpuTime { get; set; }
        public long MaxCpuTime { get; set; }

        // CLR time metrics (microseconds stored)
        public long AvgClrTime { get; set; }
        public long MinClrTime { get; set; }
        public long MaxClrTime { get; set; }

        // Logical IO Reads
        public long AvgLogicalReads { get; set; }
        public long MinLogicalReads { get; set; }
        public long MaxLogicalReads { get; set; }

        // Logical IO Writes
        public long AvgLogicalWrites { get; set; }
        public long MinLogicalWrites { get; set; }
        public long MaxLogicalWrites { get; set; }

        // Physical IO Reads
        public long AvgPhysicalReads { get; set; }
        public long MinPhysicalReads { get; set; }
        public long MaxPhysicalReads { get; set; }

        // Number of physical IO reads - nullable (NULL on SQL 2016)
        public long? AvgNumPhysicalReads { get; set; }
        public long? MinNumPhysicalReads { get; set; }
        public long? MaxNumPhysicalReads { get; set; }

        // DOP (degree of parallelism)
        public long MinDop { get; set; }
        public long MaxDop { get; set; }

        // Memory (8KB pages)
        public long AvgMemoryPages { get; set; }
        public long MinMemoryPages { get; set; }
        public long MaxMemoryPages { get; set; }

        // Row count
        public long AvgRowcount { get; set; }
        public long MinRowcount { get; set; }
        public long MaxRowcount { get; set; }

        // Tempdb space (8KB pages) - nullable for SQL 2016
        public long? AvgTempdbSpaceUsed { get; set; }
        public long? MinTempdbSpaceUsed { get; set; }
        public long? MaxTempdbSpaceUsed { get; set; }

        // Log bytes used - nullable for SQL 2016
        public long? AvgLogBytesUsed { get; set; }
        public long? MinLogBytesUsed { get; set; }
        public long? MaxLogBytesUsed { get; set; }

        // Query identifiers (hex strings from binary columns)
        public string? QueryHash { get; set; }
        public string? QueryPlanHash { get; set; }

        // Plan info
        public string? PlanType { get; set; }
        public bool IsForcedPlan { get; set; }
        public long? ForceFailureCount { get; set; }
        public string? LastForceFailureReasonDesc { get; set; }
        public string? PlanForcingType { get; set; }
        public short? CompatibilityLevel { get; set; }
        public string? QueryPlanXml { get; set; }

        // Display helpers - convert microseconds to milliseconds
        public double AvgDurationMs => AvgDuration / 1000.0;
        public double MinDurationMs => MinDuration / 1000.0;
        public double MaxDurationMs => MaxDuration / 1000.0;
        // Per-interval workload weight (Query Store stats are already per-interval averages)
        public double TotalDurationMs => CountExecutions * AvgDurationMs;
        public double TotalCpuMs => CountExecutions * AvgCpuTimeMs;
        public double AvgCpuTimeMs => AvgCpuTime / 1000.0;
        public double MinCpuTimeMs => MinCpuTime / 1000.0;
        public double MaxCpuTimeMs => MaxCpuTime / 1000.0;
        public double AvgClrTimeMs => AvgClrTime / 1000.0;
        public double MinClrTimeMs => MinClrTime / 1000.0;
        public double MaxClrTimeMs => MaxClrTime / 1000.0;

        // Memory in MB (8KB pages * 8 / 1024)
        public double AvgMemoryMb => AvgMemoryPages * 8.0 / 1024.0;
        public double MinMemoryMb => MinMemoryPages * 8.0 / 1024.0;
        public double MaxMemoryMb => MaxMemoryPages * 8.0 / 1024.0;

        // Tempdb in MB
        public double? AvgTempdbMb => AvgTempdbSpaceUsed.HasValue ? AvgTempdbSpaceUsed.Value * 8.0 / 1024.0 : null;
        public double? MinTempdbMb => MinTempdbSpaceUsed.HasValue ? MinTempdbSpaceUsed.Value * 8.0 / 1024.0 : null;
        public double? MaxTempdbMb => MaxTempdbSpaceUsed.HasValue ? MaxTempdbSpaceUsed.Value * 8.0 / 1024.0 : null;

        // Log bytes in MB (1 MB = 1,048,576 bytes)
        public double? AvgLogMb => AvgLogBytesUsed.HasValue ? AvgLogBytesUsed.Value / 1048576.0 : null;
        public double? MinLogMb => MinLogBytesUsed.HasValue ? MinLogBytesUsed.Value / 1048576.0 : null;
        public double? MaxLogMb => MaxLogBytesUsed.HasValue ? MaxLogBytesUsed.Value / 1048576.0 : null;
    }
}
