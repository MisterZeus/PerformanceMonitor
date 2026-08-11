/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using Microsoft.Data.SqlClient;
using PerformanceMonitor.Common;
using PerformanceMonitor.Darling.Service;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the TDS packet size both apps apply to monitored-server connections (#2164). It exists because
/// the setting is invisible at runtime — a connection with the wrong packet size behaves correctly and
/// merely runs slowly, so nothing but a pin would notice it being dropped in a builder refactor.
/// </summary>
public sealed class PacketSizeTuningTests
{
    [Fact]
    public void DarlingBuildsMonitoredConnectionsWithTheTunedPacketSize()
    {
        var cs = MonitoredServerConnection.BuildConnectionString(new MonitoredServer
        {
            Name = "s",
            Host = "example.database.windows.net",
            Auth = "integrated",
        });

        var parsed = new SqlConnectionStringBuilder(cs);
        Assert.Equal(CollectorTdsTuning.MonitoredServerPacketSize, parsed.PacketSize);
    }

    [Fact]
    public void TunedPacketSizeIsWithinWhatTheDriverAndProtocolAccept()
    {
        /* The driver accepts 512-32768 and SQL Server negotiates down to the smaller of the two ends, so a
           value in range can never fail a connection — it can only fail to help. A value OUTSIDE the range
           throws at connect time, which on a monitoring fleet means every server going dark at once, so
           the bound matters more than the tuning does. */
        Assert.InRange(CollectorTdsTuning.MonitoredServerPacketSize, 512, 32768);

        /* Not the 8 KB default — if someone "cleans up" the constant back to the default, this fails
           rather than silently reverting the measurement this was built on. */
        Assert.NotEqual(8000, CollectorTdsTuning.MonitoredServerPacketSize);
        Assert.NotEqual(8192, CollectorTdsTuning.MonitoredServerPacketSize);
    }
}
