/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using PerformanceMonitorLite.Services;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// #1652 gap 2: <see cref="AppLogger"/> rotated daily but never PRUNED, so the logs directory grew for the
/// life of the install — a parity gap inside Lite itself, since both sibling loggers (QueryLogger,
/// MethodProfiler) already swept at 7 days. These pin the sweep's window, its file-name scope (it must not
/// touch the siblings' files sharing the directory), and its never-throw contract.
/// <para>They exercise the directory-taking <see cref="AppLogger.CleanOldLogs"/> overload rather than going
/// through <c>Initialize</c> on purpose: AppLogger is a static singleton with a running flush timer, and
/// initializing it from a test would repoint the whole process's logging at a temp directory that the test
/// then deletes.</para>
/// </summary>
public sealed class AppLoggerRetentionTests : IDisposable
{
    private readonly string _logDir =
        Path.Combine(Path.GetTempPath(), "lite-applog-tests", Guid.NewGuid().ToString("N"));

    public AppLoggerRetentionTests() => Directory.CreateDirectory(_logDir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_logDir))
            {
                Directory.Delete(_logDir, recursive: true);
            }
        }
        catch
        {
            /* Best-effort test cleanup. */
        }
    }

    [Fact]
    public void CleanOldLogs_DeletesFilesPastTheWindow_AndKeepsRecentOnes()
    {
        var expired = WriteLog("lite_20240101.log", ageDays: 30);
        var justOutside = WriteLog("lite_20240102.log", ageDays: 8);
        var justInside = WriteLog("lite_20240103.log", ageDays: 6);
        var today = WriteLog($"lite_{DateTime.Now:yyyyMMdd}.log", ageDays: 0);

        AppLogger.CleanOldLogs(_logDir);

        Assert.False(File.Exists(expired), "a 30-day-old log survived the sweep");
        Assert.False(File.Exists(justOutside), "an 8-day-old log survived the 7-day window");
        Assert.True(File.Exists(justInside), "a 6-day-old log was deleted inside the 7-day window");
        Assert.True(File.Exists(today), "today's live log file was deleted");
    }

    /// <summary>
    /// The sweep is scoped to <c>lite_*.log</c>. QueryLogger and MethodProfiler write their own files into
    /// the SAME logs directory and own their own retention, so a wildcard here would have one logger
    /// deleting another's files (and, worse, deleting whatever else a user parked in the folder).
    /// </summary>
    [Fact]
    public void CleanOldLogs_OnlyTouchesItsOwnFiles()
    {
        var ownExpired = WriteLog("lite_20240101.log", ageDays: 30);
        var siblingQuery = WriteLog("SlowQueries_20240101.log", ageDays: 30);
        var siblingProfile = WriteLog("MethodProfile_20240101.log", ageDays: 30);
        var unrelated = WriteLog("notes.txt", ageDays: 30);

        AppLogger.CleanOldLogs(_logDir);

        Assert.False(File.Exists(ownExpired));
        Assert.True(File.Exists(siblingQuery), "the sweep deleted QueryLogger's file");
        Assert.True(File.Exists(siblingProfile), "the sweep deleted MethodProfiler's file");
        Assert.True(File.Exists(unrelated), "the sweep deleted an unrelated file");
    }

    /// <summary>
    /// Seven days, matching both sibling loggers — the window a bug report needs (the user reproduces, then
    /// sends the logs) without keeping a year of them.
    /// </summary>
    [Fact]
    public void RetentionWindow_MatchesTheSiblingLoggers() =>
        Assert.Equal(7, AppLogger.RetentionDays);

    /// <summary>
    /// Retention runs during startup, so it must degrade rather than throw: a directory that does not exist
    /// (or that the user cannot enumerate) must not be the reason the app fails to start.
    /// </summary>
    [Fact]
    public void CleanOldLogs_MissingDirectory_DoesNotThrow() =>
        AppLogger.CleanOldLogs(Path.Combine(_logDir, "does-not-exist"));

    /// <summary>
    /// Writes a log file and backdates it. The sweep compares <see cref="FileInfo.CreationTime"/> — the same
    /// predicate both sibling loggers use — so that is what the fixture has to move.
    /// </summary>
    private string WriteLog(string name, int ageDays)
    {
        var path = Path.Combine(_logDir, name);
        File.WriteAllText(path, "line");

        if (ageDays > 0)
        {
            var stamp = DateTime.Now.AddDays(-ageDays);
            File.SetCreationTime(path, stamp);
            File.SetLastWriteTime(path, stamp);
        }

        return path;
    }
}
