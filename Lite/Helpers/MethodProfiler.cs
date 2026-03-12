/*
 * SQL Server Performance Monitor Lite
 *
 * Method profiler for tracking slow application code
 */

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace PerformanceMonitorLite.Helpers;

/// <summary>
/// Profiles method execution time and logs slow methods.
/// Usage: using var _ = MethodProfiler.StartTiming("context");
/// </summary>
public static class MethodProfiler
{
    private static string s_logDirectory = "";
    private static readonly object _lock = new();
    private static volatile bool _isEnabled = true;
    private static double _thresholdMs = 500;

    public static void Initialize(string logDirectory)
    {
        s_logDirectory = logDirectory;
        try
        {
            if (!Directory.Exists(s_logDirectory))
                Directory.CreateDirectory(s_logDirectory);
            CleanOldLogs();
        }
        catch { }
    }

    public static string GetCurrentLogFile()
        => Path.Combine(s_logDirectory, $"MethodProfile_{DateTime.Now:yyyyMMdd}.log");

    public static string GetLogDirectory() => s_logDirectory;

    public static void SetEnabled(bool enabled) => _isEnabled = enabled;
    public static bool IsEnabled => _isEnabled;

    public static void SetThresholdMs(double thresholdMs) => _thresholdMs = thresholdMs;
    public static double ThresholdMs => _thresholdMs;

    public static MethodTimingContext StartTiming(
        string? context = null,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        return new MethodTimingContext(context, memberName, filePath, lineNumber);
    }

    internal static void LogSlowMethod(
        DateTime startTime,
        DateTime endTime,
        double elapsedMs,
        string? context,
        string memberName,
        string filePath,
        int lineNumber)
    {
        if (!_isEnabled || string.IsNullOrEmpty(s_logDirectory))
            return;

        if (elapsedMs < _thresholdMs)
            return;

        try
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "SLOW METHOD: {0:F0}ms - {1}", elapsedMs, memberName));
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Start Time:   {0:yyyy-MM-dd HH:mm:ss.fff}", startTime));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "End Time:     {0:yyyy-MM-dd HH:mm:ss.fff}", endTime));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Elapsed:      {0:F0}ms", elapsedMs));

                if (!string.IsNullOrEmpty(context))
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Context:      {0}", context));

                var fileName = Path.GetFileName(filePath);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Location:     {0}:{1}", fileName, lineNumber));
                sb.AppendLine();

                File.AppendAllText(GetCurrentLogFile(), sb.ToString());
            }
        }
        catch { }
    }

    private static void CleanOldLogs()
    {
        try
        {
            var files = Directory.GetFiles(s_logDirectory, "MethodProfile_*.log");
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var file in files)
            {
                if (new FileInfo(file).CreationTime < cutoff)
                    File.Delete(file);
            }
        }
        catch { }
    }
}

/// <summary>
/// Disposable context for timing method execution.
/// </summary>
public sealed class MethodTimingContext : IDisposable
{
    private readonly DateTime _startTime;
    private readonly Stopwatch _stopwatch;
    private readonly string? _context;
    private readonly string _memberName;
    private readonly string _filePath;
    private readonly int _lineNumber;
    private bool _disposed;

    internal MethodTimingContext(string? context, string memberName, string filePath, int lineNumber)
    {
        _startTime = DateTime.Now;
        _stopwatch = Stopwatch.StartNew();
        _context = context;
        _memberName = memberName;
        _filePath = filePath;
        _lineNumber = lineNumber;
    }

    public double ElapsedMs => _stopwatch.Elapsed.TotalMilliseconds;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopwatch.Stop();

        MethodProfiler.LogSlowMethod(
            _startTime, DateTime.Now, _stopwatch.Elapsed.TotalMilliseconds,
            _context, _memberName, _filePath, _lineNumber);

        GC.SuppressFinalize(this);
    }
}
