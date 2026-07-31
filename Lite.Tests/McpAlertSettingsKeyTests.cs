/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PerformanceMonitorLite.Mcp;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the key names Lite's <c>get_alert_settings</c> emits, because three of them were RENAMED to match
/// Darling (#1840 review) and a rename with nothing holding it is a rename that comes back. Darling's side
/// has had <c>DarlingMcpAlertToolsTests</c> asserting its shape all along; this is Lite's missing half.
///
/// <para>The old spellings are asserted ABSENT as well as the new ones present. Presence alone would pass
/// just as happily if someone re-added the old key beside the new one, which is the likelier accident than
/// deleting the new one — and for an MCP client, two keys meaning the same thing is its own bug.</para>
///
/// <para>Runtime rather than source-parsing: the payload is an anonymous type serialized by
/// <c>JsonSerializer</c> with a naming policy in <c>McpHelpers.JsonOptions</c>, so the C# identifier is not
/// automatically the wire key. Only serializing it actually proves what a client receives.</para>
/// </summary>
public sealed class McpAlertSettingsKeyTests
{
    private static JsonElement Settings()
    {
        /* Static, parameterless, and reads App's static settings - no WPF Application instance needed, and
           the SMTP password probe is internally guarded, so an absent credential store returns null rather
           than faulting the call. */
        var json = McpAlertTools.GetAlertSettings().GetAwaiter().GetResult();
        return JsonDocument.Parse(json).RootElement;
    }

    private static IReadOnlyList<string> KeysOf(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).ToList();

    [Fact]
    public void GetAlertSettings_TopLevelMasterSwitch_IsAlertsEnabled()
    {
        var keys = KeysOf(Settings());

        /* alerts_enabled is Darling's name for the master switch. The old notifications_enabled did not
           merely differ - Darling uses that name for the ANALYSIS section's own toggle, so the same key
           meant two things depending on which app answered. */
        Assert.Contains("alerts_enabled", keys);
        Assert.DoesNotContain("notifications_enabled", keys);
    }

    [Fact]
    public void GetAlertSettings_BlockingKeys_MatchDarlingsSpelling()
    {
        var keys = KeysOf(Settings().GetProperty("blocking"));

        Assert.Contains("count_threshold", keys);
        Assert.Contains("wait_threshold_seconds", keys);

        /* threshold_seconds was the #1839 bug (a COUNT under a seconds name); threshold_count was the
           short-lived fix that #1840's review replaced with Darling's existing spelling. */
        Assert.DoesNotContain("threshold_count", keys);
        Assert.DoesNotContain("threshold_seconds", keys);
    }

    [Fact]
    public void GetAlertSettings_DeadlockThreshold_IsCountThreshold()
    {
        var keys = KeysOf(Settings().GetProperty("deadlocks"));

        Assert.Contains("count_threshold", keys);
        Assert.DoesNotContain("threshold", keys);
    }

    [Fact]
    public void GetAlertSettings_ShapeSurvives_SoAClientCanStillFindTheRest()
    {
        var root = Settings();

        /* The keys NOT renamed, pinned so a future edit to the payload cannot quietly drop one while the
           three assertions above stay green. */
        Assert.Contains("notify_connection_changes", KeysOf(root));
        Assert.Contains("threshold_percent", KeysOf(root.GetProperty("cpu")));
        Assert.Contains("enabled", KeysOf(root.GetProperty("cpu")));
        Assert.Contains("password_configured", KeysOf(root.GetProperty("smtp")));
    }
}
