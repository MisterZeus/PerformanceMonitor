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

        /// <summary>
        /// Free space per distinct volume on the server, ordered worst (lowest free %) first.
        /// Empty on Azure SQL DB (no volume stats collected). Used by the low-disk alert.
        /// </summary>
        public List<VolumeFreeSpaceInfo> Volumes { get; set; } = new();
        public List<AnomalousJobInfo> AnomalousJobs { get; set; } = new();

        /// <summary>
        /// SQL Agent job runs that failed within the failed-job lookback window. Live
        /// msdb query — empty on Azure SQL DB (no Agent) or when the login lacks msdb /
        /// SQLAgentReaderRole access.
        /// </summary>
        public List<FailedJobInfo> RecentlyFailedJobs { get; set; } = new();
        public bool IsOnline { get; set; } = true;

        /// <summary>
        /// Capture types ("Blocking", "Deadlock") whose XE session is missing —
        /// the collector's latest collection_log status is SESSION_MISSING (#1086).
        /// Empty when both sessions are healthy.
        /// </summary>
        public List<string> MissingCaptureSessions { get; set; } = new();

        /// <summary>
        /// True when data collection appears to have stopped — either the PerformanceMonitor
        /// SQL Agent collector jobs are disabled, or nothing has logged a collection within the
        /// expected window (Agent service stopped, or collectors silently failing). This is the
        /// one signal that survives the collector being off, because the app computes it directly
        /// rather than reading a collected table the dead collector would have filled.
        /// </summary>
        public bool CollectionStopped { get; set; }

        /// <summary>
        /// Human-readable cause for <see cref="CollectionStopped"/>
        /// (e.g. "3 of 6 collector jobs are disabled"). Null when collection is healthy.
        /// </summary>
        public string? CollectionStoppedReason { get; set; }

        /// <summary>
        /// Count of PerformanceMonitor Agent jobs with enabled = 0. 0 when none are disabled,
        /// or when job state couldn't be read (Azure SQL DB / restricted msdb).
        /// </summary>
        public int DisabledCollectorJobs { get; set; }

        /// <summary>
        /// Total PerformanceMonitor Agent jobs found in msdb. 0 on Azure SQL DB or when
        /// job state couldn't be read.
        /// </summary>
        public int TotalCollectorJobs { get; set; }

        /// <summary>
        /// Minutes since the most recent entry in config.collection_log. Null when the log is
        /// empty (never collected) or couldn't be read.
        /// </summary>
        public int? MinutesSinceLastCollection { get; set; }

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
