/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;

namespace PerformanceMonitorDashboard.Models
{
    /// <summary>
    /// Lightweight result from alert-only health queries.
    /// Contains only the metrics needed for alert evaluation (CPU, blocking, deadlocks, poison waits).
    /// Used by MainWindow's independent alert timer to avoid running all 9 NOC queries.
    /// </summary>
    public class AlertHealthResult
    {
        public int? CpuPercent { get; set; }
        public int? OtherCpuPercent { get; set; }
        public long TotalBlocked { get; set; }
        public decimal LongestBlockedSeconds { get; set; }
        public long DeadlockCount { get; set; }

        /// <summary>
        /// Deadlock count for the alert window filtered by excluded databases.
        /// Sourced from collect.blocking_deadlock_stats when excluded databases are configured.
        /// When set, EvaluateAlertConditionsAsync uses this instead of the raw delta
        /// from the server-wide performance counter, matching how blocking alerts filter.
        /// Null when no databases are excluded (fall back to raw delta).
        /// </summary>
        public long? FilteredDeadlockCount { get; set; }
        public List<PoisonWaitDelta> PoisonWaits { get; set; } = new();
        public List<LongRunningQueryInfo> LongRunningQueries { get; set; } = new();
        public TempDbSpaceInfo? TempDbSpace { get; set; }
        public List<AnomalousJobInfo> AnomalousJobs { get; set; } = new();
        public bool IsOnline { get; set; } = true;

        /// <summary>
        /// Total CPU = SQL + Other.
        /// </summary>
        public int? TotalCpuPercent
        {
            get
            {
                if (!CpuPercent.HasValue && !OtherCpuPercent.HasValue) return null;
                return (CpuPercent ?? 0) + (OtherCpuPercent ?? 0);
            }
        }
    }
}
