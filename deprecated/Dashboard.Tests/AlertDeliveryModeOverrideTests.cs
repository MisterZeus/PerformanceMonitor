/*
 * Performance Monitor Dashboard
 * Copyright (c) 2026 Darling Data, LLC
 * Licensed under the MIT License - see LICENSE file for details
 */

using System.Text.Json;
using PerformanceMonitor.Notifications;
using PerformanceMonitorDashboard.Models;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// #1236 per-server alert delivery override persists on <see cref="ServerConnection"/> (servers.json,
/// same WriteIndented options ServerManager uses). A pre-#1236 file with no field inherits the global.
/// The shared precedence rule (<see cref="AlertDeliveryModeResolver"/>) is covered in Lite.Tests.
/// </summary>
public class AlertDeliveryModeOverrideTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    [Fact]
    public void Override_RoundTripsThroughJson()
    {
        var server = new ServerConnection { ServerName = "S1", AlertDeliveryModeOverride = AlertNotificationMode.PerEvent };
        var back = JsonSerializer.Deserialize<ServerConnection>(JsonSerializer.Serialize(server, s_jsonOptions), s_jsonOptions);
        Assert.Equal(AlertNotificationMode.PerEvent, back!.AlertDeliveryModeOverride);
    }

    [Fact]
    public void NullOverride_RoundTripsAsNull()
    {
        var server = new ServerConnection { ServerName = "S1", AlertDeliveryModeOverride = null };
        var back = JsonSerializer.Deserialize<ServerConnection>(JsonSerializer.Serialize(server, s_jsonOptions), s_jsonOptions);
        Assert.Null(back!.AlertDeliveryModeOverride);
    }

    [Fact]
    public void LegacyServersJson_WithoutField_InheritsGlobal()
    {
        // A servers.json written before #1236 has no AlertDeliveryModeOverride property -> null -> inherit.
        var back = JsonSerializer.Deserialize<ServerConnection>("{\"ServerName\":\"S1\",\"DisplayName\":\"S1\"}", s_jsonOptions);
        Assert.Null(back!.AlertDeliveryModeOverride);
    }
}
