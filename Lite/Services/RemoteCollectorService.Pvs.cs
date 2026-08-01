/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Threading;
using System.Threading.Tasks;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Models;

namespace PerformanceMonitorLite.Services;

public partial class RemoteCollectorService
{
    /// <summary>
    /// Collects ADR persistent version store pressure via the shared <see cref="PvsStatsCollector"/>
    /// definition (the 2019+ version gate, the single 2022-only column, the Azure SQL DB variant and the
    /// one-row-per-database OUTER APPLY shape live there — the cross-SKU parity contract).
    /// </summary>
    private Task<int> CollectPvsStatsAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(PvsStatsCollector.Instance, server, cancellationToken);
}
