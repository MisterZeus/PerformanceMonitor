/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Npgsql;
using PerformanceMonitor.Common;

#pragma warning disable CA1707 // MCP tools use snake_case naming convention

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// The SQL Agent running-jobs MCP tool — get_running_jobs — served over Darling's Postgres store. The tool
/// body mirrors LITE's <c>McpJobTools</c> field-for-field (Lite and the Dashboard share this shape; Lite adds
/// <c>collection_time</c> to the envelope, which the store-faithful Darling read carries). Reads flow through
/// <see cref="DarlingJobReader"/> — a STORED read of the latest snapshot, no live monitored-server hit.
/// </summary>
[McpServerToolType]
public sealed class DarlingMcpJobTools
{
    [McpServerTool(Name = "get_running_jobs"), Description("Gets currently running SQL Agent jobs with duration comparison. Shows each job's current duration vs its historical average and p95, flagging jobs that are running longer than usual.")]
    public static async Task<string> GetRunningJobs(
        NpgsqlDataSource postgres,
        [Description("Server name or display name.")] string? server_name = null)
    {
        var (resolved, error) = await DarlingServerResolver.ResolveOrErrorAsync(postgres, server_name);
        if (error != null) return error;

        try
        {
            var rows = await DarlingJobReader.GetRunningJobsAsync(postgres, resolved.ServerId);
            if (rows.Count == 0)
                return await DarlingEngineCapability.NotCollectedStatusAsync(postgres, resolved.ServerId, resolved.ServerName, "running_jobs")
                    ?? McpHelpers.Status("empty", "No running SQL Agent jobs found (or the running_jobs collector has not run yet).");

            var jobs = rows.Select(r => new
            {
                job_name = r.JobName,
                job_id = r.JobId,
                job_enabled = r.JobEnabled,
                start_time = r.StartTime.ToString("o"),
                current_duration_seconds = r.CurrentDurationSeconds,
                current_duration_formatted = DarlingJobReader.FormatDuration(r.CurrentDurationSeconds),
                avg_duration_seconds = r.AvgDurationSeconds,
                avg_duration_formatted = DarlingJobReader.FormatDuration(r.AvgDurationSeconds),
                p95_duration_seconds = r.P95DurationSeconds,
                p95_duration_formatted = DarlingJobReader.FormatDuration(r.P95DurationSeconds),
                successful_run_count = r.SuccessfulRunCount,
                is_running_long = r.IsRunningLong,
                percent_of_average = r.PercentOfAverage
            });

            return JsonSerializer.Serialize(new
            {
                server = resolved.ServerName,
                collection_time = rows[0].CollectionTime.ToString("o"),
                running_job_count = rows.Count,
                long_running_count = rows.Count(r => r.IsRunningLong),
                jobs
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_running_jobs", ex);
        }
    }
}
