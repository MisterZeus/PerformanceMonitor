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
    /// Collects automatic plan correction state via the shared <see cref="PlanCorrectionCollector"/>
    /// definition (#1952) — the 2017+ gate, the per-database enumeration, the details-JSON shredding
    /// and the Query Store text resolution all live there, which is the cross-SKU parity contract.
    /// </summary>
    private Task<int> CollectPlanCorrectionAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(PlanCorrectionCollector.Instance, server, cancellationToken);
}
