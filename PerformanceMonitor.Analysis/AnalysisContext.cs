using System;
using System.Threading;

namespace PerformanceMonitor.Analysis;

/// <summary>
/// Context for an analysis run — what server, what time range.
/// </summary>
public class AnalysisContext
{
    public int ServerId { get; set; }
    public string ServerName { get; set; } = string.Empty;
    public DateTime TimeRangeStart { get; set; }
    public DateTime TimeRangeEnd { get; set; }

    /// <summary>
    /// The token the pass's store reads observe. Default <see cref="CancellationToken.None"/> — a
    /// caller that does not plumb one (the fact-inspection paths) keeps the prior behavior exactly,
    /// because every abandon classification requires this token to be SIGNALLED. Carried on the context
    /// rather than on thirty method signatures because the context already reaches every pipeline stage.
    ///
    /// <para>Originally this WAS the host's stopping token (#2299). Since #2430 it is the pass's
    /// EFFECTIVE token, which the Darling worker links from the stopping token and arms with the
    /// per-pass budget — so it now fires on an ordinary timeout against a perfectly healthy service,
    /// not only at shutdown. That is why <see cref="ShutdownToken"/> exists: something has to still know
    /// which of the two happened, and this token can no longer answer it.</para>
    /// </summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// The host's stopping token, and ONLY that (#2430). Default <see cref="CancellationToken.None"/>,
    /// which reads as "this pass has no shutdown to distinguish" — correct for the on-demand callers,
    /// whose cancellation is never a service stop.
    ///
    /// <para>Kept separate from <see cref="CancellationToken"/> because a classifier that asks the
    /// armed token "are we stopping?" gets a yes on every timeout, and would log an ordinary overrun on
    /// a running service as "abandoned at shutdown" at Information — a wrong answer that reads as a
    /// calm one, which is the worst kind.</para>
    /// </summary>
    public CancellationToken ShutdownToken { get; set; }

    /// <summary>
    /// The monitored SERVER's UTC offset (SYSDATETIME − SYSUTCDATETIME), captured once at
    /// analysis start. <see cref="TimeRangeStart"/>/<see cref="TimeRangeEnd"/> are in the
    /// server's LOCAL clock so every windowed read matches the collectors (which stamp rows
    /// with SYSDATETIME, server-local); this offset converts that window back to UTC for
    /// persistence/display. <see cref="TimeSpan.Zero"/> when the clock probe was unavailable
    /// (the window is then host-UTC — the prior behavior).
    /// </summary>
    public TimeSpan ServerUtcOffset { get; set; }

    /// <summary>
    /// Duration of the examined period in milliseconds.
    /// </summary>
    public double PeriodDurationMs => (TimeRangeEnd - TimeRangeStart).TotalMilliseconds;
}
