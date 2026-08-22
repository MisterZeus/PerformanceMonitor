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
/// The index / object diagnostic-depth MCP tools — get_table_index_sizes, get_index_usage,
/// get_object_locking, get_database_sizes — served over Darling's Postgres store. Each tool body mirrors
/// LITE's <c>McpObjectStatsTools</c> / <c>McpServerInfoTools</c> field-for-field (byte-identical to the
/// Dashboard for the three object-stats tools), keeping the tool names + parameter contracts. Reads flow
/// through <see cref="DarlingObjectStatsReader"/> — STORED reads of the latest daily snapshot, no live
/// monitored-server hit.
/// </summary>
[McpServerToolType]
public sealed class DarlingMcpObjectStatsTools
{
    /// <summary>Lite's default result caps (the tools take no top parameter).</summary>
    private const int TableSizesTop = 100;
    private const int IndexUsageTop = 200;
    private const int ObjectLockingTop = 200;

    [McpServerTool(Name = "get_table_index_sizes"), Description("Gets the largest tables with per-table size, growth (7d/30d/daily rate), and row counts from the latest daily snapshot. Indexes are rolled up per table. Use to find storage hot-spots and fast-growing tables for capacity planning.")]
    public static async Task<string> GetTableIndexSizes(
        NpgsqlDataSource postgres,
        [Description("Server name or display name.")] string? server_name = null)
    {
        var (resolved, error) = await DarlingServerResolver.ResolveOrErrorAsync(postgres, server_name);
        if (error != null) return error;

        try
        {
            var now = DateTime.UtcNow;
            var rows = await DarlingObjectStatsReader.GetObjectSizeGrowthAsync(
                postgres, resolved.ServerId, now.AddDays(-7), now.AddDays(-30), TableSizesTop);
            if (rows.Count == 0)
                return await DarlingEngineCapability.NotCollectedStatusAsync(postgres, resolved.ServerId, resolved.ServerName, "index_object_stats")
                    ?? McpHelpers.Status("unavailable", "No object size data available. Index/object stats are collected daily.");

            var result = rows.Select(r => new
            {
                database_name = r.DatabaseName,
                schema_name = r.SchemaName,
                table_name = r.TableName,
                reserved_mb = r.CurrentReservedMb,
                used_mb = r.CurrentUsedMb,
                total_rows = r.TotalRows,
                index_count = r.IndexCount,
                growth_7d_mb = r.Growth7dMb,
                growth_30d_mb = r.Growth30dMb,
                daily_growth_rate_mb = r.DailyGrowthRateMb,
                growth_pct_30d = r.GrowthPct30d
            });

            return JsonSerializer.Serialize(new
            {
                server = resolved.ServerName,
                tables = result
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_table_index_sizes", ex);
        }
    }

    [McpServerTool(Name = "get_index_usage"), Description("Gets per-index usage (seeks, scans, lookups, updates) from the latest daily snapshot, classifying each index as Unused, Write-only, or Active. Unused and write-only indexes are listed first - these are drop candidates. Counters are cumulative since the last instance restart.")]
    public static async Task<string> GetIndexUsage(
        NpgsqlDataSource postgres,
        [Description("Server name or display name.")] string? server_name = null)
    {
        var (resolved, error) = await DarlingServerResolver.ResolveOrErrorAsync(postgres, server_name);
        if (error != null) return error;

        try
        {
            var rows = await DarlingObjectStatsReader.GetIndexUsageAsync(postgres, resolved.ServerId, IndexUsageTop);
            if (rows.Count == 0)
                return await DarlingEngineCapability.NotCollectedStatusAsync(postgres, resolved.ServerId, resolved.ServerName, "index_object_stats")
                    ?? McpHelpers.Status("unavailable", "No index usage data available. Index/object stats are collected daily.");

            var result = rows.Select(r => new
            {
                database_name = r.DatabaseName,
                schema_name = r.SchemaName,
                table_name = r.TableName,
                index_name = r.IndexName,
                index_type = r.IndexTypeDesc,
                classification = r.Classification,
                reserved_mb = r.ReservedMb,
                total_rows = r.TotalRows,
                user_seeks = r.UserSeeks,
                user_scans = r.UserScans,
                user_lookups = r.UserLookups,
                total_reads = r.TotalReads,
                user_updates = r.UserUpdates,
                last_user_access = r.LastUserAccess?.ToString("o")
            });

            return JsonSerializer.Serialize(new
            {
                server = resolved.ServerName,
                indexes = result
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_index_usage", ex);
        }
    }

    [McpServerTool(Name = "get_object_locking"), Description("Gets per-index locking and latch contention (row/page lock waits in ms, lock escalations, page-latch and page-IO-latch waits) from the latest daily snapshot, top contended objects first. Use to find tables/indexes driving blocking and contention. Counters are cumulative since the last instance restart.")]
    public static async Task<string> GetObjectLocking(
        NpgsqlDataSource postgres,
        [Description("Server name or display name.")] string? server_name = null)
    {
        var (resolved, error) = await DarlingServerResolver.ResolveOrErrorAsync(postgres, server_name);
        if (error != null) return error;

        try
        {
            var rows = await DarlingObjectStatsReader.GetIndexLockingAsync(postgres, resolved.ServerId, ObjectLockingTop);
            if (rows.Count == 0)
                return await DarlingEngineCapability.NotCollectedStatusAsync(postgres, resolved.ServerId, resolved.ServerName, "index_object_stats")
                    ?? McpHelpers.Status("unavailable", "No locking/contention data recorded. Index/object stats are collected daily.");

            var result = rows.Select(r => new
            {
                database_name = r.DatabaseName,
                schema_name = r.SchemaName,
                table_name = r.TableName,
                index_name = r.IndexName,
                index_type = r.IndexTypeDesc,
                reserved_mb = r.ReservedMb,
                total_rows = r.TotalRows,
                row_lock_wait_count = r.RowLockWaitCount,
                row_lock_wait_ms = r.RowLockWaitInMs,
                page_lock_wait_count = r.PageLockWaitCount,
                page_lock_wait_ms = r.PageLockWaitInMs,
                lock_escalations = r.IndexLockPromotionCount,
                page_latch_wait_ms = r.PageLatchWaitInMs,
                page_io_latch_wait_ms = r.PageIoLatchWaitInMs
            });

            return JsonSerializer.Serialize(new
            {
                server = resolved.ServerName,
                objects = result
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_object_locking", ex);
        }
    }

    [McpServerTool(Name = "get_database_sizes"), Description("Gets database file sizes, space usage, and volume free space. Shows each database file with total size, used space, auto-growth settings, and the underlying volume's capacity. Use for capacity planning and identifying space pressure.")]
    public static async Task<string> GetDatabaseSizes(
        NpgsqlDataSource postgres,
        [Description("Server name or display name.")] string? server_name = null)
    {
        var (resolved, error) = await DarlingServerResolver.ResolveOrErrorAsync(postgres, server_name);
        if (error != null) return error;

        try
        {
            var rows = await DarlingObjectStatsReader.GetLatestDatabaseSizesAsync(postgres, resolved.ServerId);
            if (rows.Count == 0)
                return await DarlingEngineCapability.NotCollectedStatusAsync(postgres, resolved.ServerId, resolved.ServerName, "database_size_stats")
                    ?? McpHelpers.Status("unavailable", "No database size data available. The size collector may not have run yet.");

            return JsonSerializer.Serialize(new
            {
                server = resolved.ServerName,
                collection_time = rows[0].CollectionTime.ToString("o"),
                file_count = rows.Count,
                databases = rows
                    .GroupBy(r => r.DatabaseName)
                    .Select(g => new
                    {
                        database_name = g.Key,
                        total_size_mb = g.Sum(r => r.TotalSizeMb),
                        used_size_mb = g.Sum(r => r.UsedSizeMb ?? 0),
                        files = g.Select(r => new
                        {
                            file_name = r.FileName,
                            file_type = r.FileTypeDesc,
                            total_size_mb = r.TotalSizeMb,
                            used_size_mb = r.UsedSizeMb,
                            auto_growth_mb = r.AutoGrowthMb,
                            max_size_mb = r.MaxSizeMb,
                            volume_mount_point = r.VolumeMountPoint,
                            volume_total_mb = r.VolumeTotalMb,
                            volume_free_mb = r.VolumeFreeMb
                        })
                    })
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_database_sizes", ex);
        }
    }
}
