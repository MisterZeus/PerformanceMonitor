/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using Microsoft.Data.SqlClient;
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// Builds the SqlClient connection string for a monitored server, mirroring Lite's
/// ServerConnection.BuildConnectionString shape (MARS on for the collection loop, 15-second
/// connect budget, Encrypt fail-closed to Mandatory for unknown modes) so the two SKUs present
/// the same connection posture to monitored servers — only the ApplicationName differs.
/// </summary>
public static class MonitoredServerConnection
{
    public static string BuildConnectionString(MonitoredServer server, string? resolvedPassword = null)
    {
        if (server is null)
        {
            throw new ArgumentNullException(nameof(server));
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server.Host,
            InitialCatalog = string.IsNullOrWhiteSpace(server.Database) ? "master" : server.Database,
            ApplicationName = "PerformanceMonitorDarling",
            ConnectTimeout = 15,
            CommandTimeout = 60,
            TrustServerCertificate = server.TrustServerCertificate,
            MultipleActiveResultSets = true,
            ApplicationIntent = server.ReadOnlyIntent ? ApplicationIntent.ReadOnly : ApplicationIntent.ReadWrite,
            MultiSubnetFailover = server.MultiSubnetFailover,
            /* #2164: fewer, larger TDS packets for the plan-XML/query-text payloads the heavy
               collectors read. See CollectorTdsTuning for the measurement that motivated it. */
            PacketSize = CollectorTdsTuning.MonitoredServerPacketSize,
        };

        /* Encrypt fail-closed: unknown/blank modes get Mandatory, matching Lite. */
        builder.Encrypt = server.EncryptMode?.Trim().ToUpperInvariant() switch
        {
            "STRICT" => SqlConnectionEncryptOption.Strict,
            "OPTIONAL" => SqlConnectionEncryptOption.Optional,
            _ => SqlConnectionEncryptOption.Mandatory,
        };

        if (server.UsesSqlAuth)
        {
            builder.UserID = server.Username;
            builder.Password = resolvedPassword
                ?? throw new InvalidOperationException($"Server '{server.DisplayName}' uses sql auth but no password was resolved.");
        }
        else
        {
            builder.IntegratedSecurity = true;
        }

        return builder.ConnectionString;
    }
}
