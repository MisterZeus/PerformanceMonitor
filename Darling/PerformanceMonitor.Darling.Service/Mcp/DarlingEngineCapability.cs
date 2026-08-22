/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// The store half of the #2511 engine-capability answer: reads this server's probed engine edition and, when
/// the collector serving a read cannot run on that engine, returns the <c>not_collected</c> envelope both
/// SKUs emit. The DECISION and the WORDS both come from
/// <see cref="CollectorEngineCapability"/> — nothing about which collectors are gated, and nothing about how
/// the gap is described, lives in this file or in its Lite twin (<c>McpEngineCapability</c>).
///
/// <para><b>Why <c>not_collected</c> rather than <c>unavailable</c>.</b> The miss vocabulary already has the
/// right word: <c>not_collected</c> is "the input names something this server does not collect", which is
/// exactly true and final here. <c>unavailable</c> means "supported here, just not retrievable now", and it
/// sends an operator hunting for a collector to restart — which is the defect #2511 was filed about, not the
/// fix for it.</para>
///
/// <para><b>Called only on the miss path.</b> Every call site checks capability after its read came back
/// empty, never before it. That costs nothing in the common case, and — more importantly — a server whose
/// registry row says one engine while its collected rows say another (a re-registration, a restored
/// database) still gets its DATA rather than a confident explanation of why it cannot have any.</para>
/// </summary>
internal static class DarlingEngineCapability
{
    /// <summary>
    /// The probed <c>SERVERPROPERTY('EngineEdition')</c> the registration upsert stamps on every connect
    /// (<see cref="DarlingObservability"/>). Exposed as a const so Darling.Tests can pin the dialect without a
    /// live store. $1 server_id.
    /// </summary>
    public const string EngineEditionSql = @"
SELECT sql_engine_edition
FROM servers
WHERE server_id = $1";

    /// <summary>
    /// The <c>not_collected</c> envelope when <paramref name="collectorName"/> cannot run on this server's
    /// engine, or <c>null</c> when it can — in which case the caller falls through to its own
    /// <c>empty</c>/<c>unavailable</c> miss, unchanged.
    ///
    /// <para>A registry read that FAILS answers null, deliberately. This runs on a path that has already
    /// found no data; turning a capability probe into a read error would replace one honest miss with a
    /// worse one.</para>
    /// </summary>
    public static async Task<string?> NotCollectedStatusAsync(
        NpgsqlDataSource postgres,
        int serverId,
        string serverName,
        string collectorName,
        CancellationToken cancellationToken = default)
    {
        int engineEdition;
        try
        {
            engineEdition = await ReadEngineEditionAsync(postgres, serverId, cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }

        var message = CollectorEngineCapability.NotCollectedMessage(serverName, engineEdition, collectorName);
        return message is null ? null : McpHelpers.Status("not_collected", message);
    }

    /// <summary>
    /// The server's probed engine edition, or <see cref="CollectorEngineCapability.UnknownEngineEdition"/>
    /// when the registry has no row or a NULL (a server that has never completed a connect, and a PostgreSQL
    /// target, both land here). Unknown makes no capability claim.
    /// </summary>
    private static async Task<int> ReadEngineEditionAsync(
        NpgsqlDataSource postgres,
        int serverId,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(EngineEditionSql);
        DarlingMcpReadParameters.AddInt(command, serverId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is int edition ? edition : CollectorEngineCapability.UnknownEngineEdition;
    }
}
