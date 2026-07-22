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
    public enum HeatmapMetric
    {
        Duration,
        Cpu,
        LogicalReads,
        LogicalWrites,
        ExecutionCount
    }

    public class HeatmapCell
    {
        public DateTime TimeBucket { get; set; }
        public int BucketIndex { get; set; }
        public long Count { get; set; }
        public string TopQueryHash { get; set; } = "";
        public string TopQueryText { get; set; } = "";
    }

    public class HeatmapResult
    {
        public double[,] Intensities { get; set; } = new double[0, 0];
        public DateTime[] TimeBuckets { get; set; } = Array.Empty<DateTime>();
        public string[] BucketLabels { get; set; } = Array.Empty<string>();
        public HeatmapCell[,] CellDetails { get; set; } = new HeatmapCell[0, 0];
    }
}
