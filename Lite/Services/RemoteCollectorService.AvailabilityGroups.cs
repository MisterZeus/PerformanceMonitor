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
    /// Collects Availability Group replica health via the shared
    /// <see cref="AgReplicaStatesCollector"/> definition (query text, target gate, and payload order
    /// live there — the cross-SKU parity contract). Zero rows on a server with no AGs is a normal
    /// SUCCESS, not an error.
    /// </summary>
    private Task<int> CollectAgReplicaStatesAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(AgReplicaStatesCollector.Instance, server, cancellationToken);

    /// <summary>
    /// Collects Availability Group per-database replica health (send/redo queues, rates, secondary
    /// lag) via the shared <see cref="AgDatabaseReplicaStatesCollector"/> definition (query text,
    /// target gate, and payload order live there — the cross-SKU parity contract). Zero rows on a
    /// server with no AGs is a normal SUCCESS, not an error.
    /// </summary>
    private Task<int> CollectAgDatabaseReplicaStatesAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(AgDatabaseReplicaStatesCollector.Instance, server, cancellationToken);
}
