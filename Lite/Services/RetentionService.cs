/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// Cleans up old Parquet archive files beyond the retention period.
/// </summary>
public class RetentionService
{
    private readonly string _archivePath;
    private readonly ILogger<RetentionService>? _logger;

    public RetentionService(string archivePath, ILogger<RetentionService>? logger = null)
    {
        _archivePath = archivePath;
        _logger = logger;
    }

    /// <summary>
    /// Deletes Parquet files older than the specified retention period.
    /// Supports naming formats:
    ///   - Monthly compacted: "202602_wait_stats.parquet" (yyyyMM prefix)
    ///   - Timestamped: "20260221_1328_wait_stats.parquet" (yyyyMMdd prefix)
    ///   - Consolidated daily: "20260221_wait_stats.parquet" (yyyyMMdd prefix)
    ///   - Legacy monthly: "2026-02_wait_stats.parquet" (yyyy-MM prefix)
    /// </summary>
    public void CleanupOldArchives(int retentionMonths = 3)
    {
        if (!Directory.Exists(_archivePath))
        {
            return;
        }

        var cutoffDate = DateTime.UtcNow.AddMonths(-retentionMonths);

        foreach (var file in Directory.GetFiles(_archivePath, "*.parquet"))
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                DateTime? fileDate = null;

                /* Monthly compacted format: "202602_wait_stats" -> "202602" */
                if (fileName.Length >= 6 &&
                    DateTime.TryParseExact(
                        fileName[..6],
                        "yyyyMM",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var monthDate) &&
                    fileName.Length > 6 && fileName[6] == '_')
                {
                    fileDate = monthDate;
                }
                /* Timestamped or daily format: "20260221..." -> "20260221" */
                else if (fileName.Length >= 8 &&
                    DateTime.TryParseExact(
                        fileName[..8],
                        "yyyyMMdd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var dayDate))
                {
                    fileDate = dayDate;
                }
                /* Legacy monthly format: "2026-02_wait_stats" -> "2026-02" */
                else if (fileName.Length >= 7 &&
                    DateTime.TryParseExact(
                        fileName[..7],
                        "yyyy-MM",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var legacyMonth))
                {
                    fileDate = legacyMonth;
                }

                if (fileDate.HasValue && fileDate.Value < cutoffDate)
                {
                    File.Delete(file);
                    _logger?.LogInformation("Deleted expired archive: {File}", file);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to evaluate/delete archive file: {File}", file);
            }
        }
    }
}
