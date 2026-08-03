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
    /// Collects per-database state (<c>sys.databases.state_desc</c>) as a time series via the shared
    /// <see cref="DatabaseStateCollector"/> definition — the cheap periodic feed the baseline-deviation
    /// database-state alert compares against (the load-time <c>database_config</c> snapshot never
    /// reflects a database going OFFLINE / SUSPECT after a server is added).
    /// </summary>
    private Task<int> CollectDatabaseStatesAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(DatabaseStateCollector.Instance, server, cancellationToken);
}
