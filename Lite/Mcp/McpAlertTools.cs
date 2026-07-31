using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PerformanceMonitor.Notifications;
using PerformanceMonitorLite.Services;
using PerformanceMonitor.Common;

namespace PerformanceMonitorLite.Mcp;

[McpServerToolType]
public sealed class McpAlertTools
{
    [McpServerTool(Name = "get_alert_history"), Description("Gets recent alert history from the alert log. Shows what alerts fired, when, and whether email was sent successfully.")]
    public static async Task<string> GetAlertHistory(
        LocalDataService dataService,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description("Maximum rows. Default 50.")] int limit = 50)
    {
        try
        {
            var hoursError = McpHelpers.ValidateHoursBack(hours_back);
            if (hoursError != null) return hoursError;

            var limitError = McpHelpers.ValidateTop(limit);
            if (limitError != null) return limitError;

            var rows = await dataService.GetAlertHistoryAsync(hours_back, limit);

            if (rows.Count == 0)
            {
                return McpHelpers.Status("empty", "No alerts found in the specified time range.");
            }

            var alerts = rows.Select(r => new
            {
                alert_time = r.AlertTime.ToString("o"),
                server_id = r.ServerId,
                server_name = r.ServerName,
                metric_name = r.MetricName,
                current_value = r.CurrentValue,
                threshold_value = r.ThresholdValue,
                alert_sent = r.AlertSent,
                notification_type = r.NotificationType,
                send_error = r.SendError,
                muted = r.Muted,
                detail_text = r.DetailText
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                hours_back,
                total_alerts = alerts.Count,
                alerts
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_alert_history", ex);
        }
    }

    [McpServerTool(Name = "get_alert_settings"), Description("Gets the current alert and SMTP email configuration settings.")]
    public static Task<string> GetAlertSettings()
    {
        try
        {
            var settings = new
            {
                /* alerts_enabled, not notifications_enabled: Darling reports the master switch under this
                   name, and it spells notifications_enabled something else entirely (the analysis
                   sub-object's own toggle), so the old Lite name was not merely different — it collided. */
                alerts_enabled = App.AlertsEnabled,
                notify_connection_changes = App.NotifyConnectionChanges,
                cpu = new
                {
                    enabled = App.AlertCpuEnabled,
                    threshold_percent = App.AlertCpuThreshold
                },
                blocking = new
                {
                    enabled = App.AlertBlockingEnabled,
                    /* Renamed from threshold_seconds (#1839): this gate has always been a COUNT of
                       blocked-process events — the seconds name was copied from the Dashboard, whose
                       blocking threshold really is seconds. Leaving it would now collide with the real
                       seconds threshold below. The spelling is Darling's count_threshold, not a new
                       threshold_count: Darling's get_alert_settings/update_alert_settings pair already
                       used it for this exact field, and one MCP schema across both apps is the point. */
                    count_threshold = App.AlertBlockingThreshold,
                    wait_threshold_seconds = App.AlertBlockingWaitSecondsThreshold
                },
                deadlocks = new
                {
                    enabled = App.AlertDeadlockEnabled,
                    /* Same alignment: Darling reports this as count_threshold too. */
                    count_threshold = App.AlertDeadlockThreshold
                },
                smtp = new
                {
                    enabled = App.SmtpEnabled,
                    server = App.SmtpServer,
                    port = App.SmtpPort,
                    use_ssl = App.SmtpUseSsl,
                    username = App.SmtpUsername,
                    from_address = App.SmtpFromAddress,
                    recipients = App.SmtpRecipients,
                    password_configured = !string.IsNullOrEmpty(App.GetSmtpPassword())
                }
            };

            return Task.FromResult(JsonSerializer.Serialize(settings, McpHelpers.JsonOptions));
        }
        catch (Exception ex)
        {
            return Task.FromResult(McpHelpers.FormatError("get_alert_settings", ex));
        }
    }

    [McpServerTool(Name = "get_mute_rules"), Description("Gets the configured alert mute rules. Mute rules suppress specific recurring alerts while still logging them.")]
    public static Task<string> GetMuteRules(
        MuteRuleService muteRuleService,
        [Description("Include only enabled rules. Default true.")] bool enabled_only = true)
    {
        try
        {
            var rules = muteRuleService.GetRules();
            if (enabled_only)
                rules = rules.Where(r => r.Enabled && (r.ExpiresAtUtc == null || r.ExpiresAtUtc > DateTime.UtcNow)).ToList();

            var result = new
            {
                mute_rules = rules.Select(r => new
                {
                    id = r.Id,
                    enabled = r.Enabled,
                    created_at_utc = r.CreatedAtUtc.ToString("o"),
                    expires_at_utc = r.ExpiresAtUtc?.ToString("o"),
                    reason = r.Reason,
                    server_name = r.ServerName,
                    metric_name = r.MetricName,
                    database_pattern = r.DatabasePattern,
                    query_text_pattern = r.QueryTextPattern,
                    wait_type_pattern = r.WaitTypePattern,
                    job_name_pattern = r.JobNamePattern,
                    summary = r.Summary
                }).ToArray(),
                total_count = rules.Count
            };

            return Task.FromResult(JsonSerializer.Serialize(result, McpHelpers.JsonOptions));
        }
        catch (Exception ex)
        {
            return Task.FromResult(McpHelpers.FormatError("get_mute_rules", ex));
        }
    }
}
