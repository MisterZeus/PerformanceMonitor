/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;
using System.Threading.Tasks;

namespace PerformanceMonitor.Common;

/// <summary>How one run of an <see cref="AbandonableStep"/> ended.</summary>
public enum AbandonableStepOutcome
{
    /// <summary>The step finished within its deadline.</summary>
    Completed,

    /// <summary>The step threw; the exception rides <see cref="AbandonableStepResult.Exception"/>.</summary>
    Faulted,

    /// <summary>The deadline elapsed first. The step's task is ABANDONED, not cancelled — it may still
    /// be running; the in-flight guard keeps it from being relaunched until it truly ends.</summary>
    Abandoned,

    /// <summary>A previously-abandoned run is still wedged, so this run never started.</summary>
    SkippedStillRunning,

    /// <summary>The caller's token cancelled while waiting.</summary>
    Cancelled,
}

/// <summary>One run's outcome plus the fault when there was one.</summary>
public readonly record struct AbandonableStepResult(AbandonableStepOutcome Outcome, Exception? Exception = null);

/// <summary>
/// A sequential background-loop step that may NOT hold the loop past a deadline (#2148). Born from the
/// field failure this class exists to make impossible: Lite's collection ladder ran its steps strictly
/// in sequence, one step wedged on an Azure elastic pool ~12 minutes after a 3.4.0 upgrade, and ALL
/// collection stopped — permanently, silently, with every step's exception armor intact, because the
/// armor bounded throws and nothing bounded a HANG.
///
/// <para>The discipline is the ladder's own scheduled-analysis idiom, extracted and made reusable:
/// <see cref="Task.WhenAny(Task, Task)"/> against a deadline, and an in-flight guard cleared only when
/// the underlying task TRULY finishes — so an abandoned (possibly wedged) run is never overlapped by a
/// relaunch, and the moment it finally dies the step becomes runnable again on its own. Abandonment is
/// deliberately not cancellation: the wedged task already ignored cooperative signals by definition,
/// and the value here is that the LOOP keeps moving while the guard quarantines the stuck step.</para>
///
/// <para>Outcomes are returned, never thrown (the caller is a loop whose next steps must run; it logs
/// each outcome at its own severity). The step delegate's synchronous throws are treated as
/// <see cref="AbandonableStepOutcome.Faulted"/> like any other fault, with the guard released.</para>
/// </summary>
public sealed class AbandonableStep
{
    private int _inFlight;

    /// <summary>Whether a run is currently holding the guard — an abandoned run still counts until its
    /// task truly ends. Exposed for the caller's logging/diagnostics, racy by nature.</summary>
    public bool IsInFlight => Volatile.Read(ref _inFlight) == 1;

    /// <summary>
    /// Runs <paramref name="step"/> unless a prior run is still wedged, waiting at most
    /// <paramref name="timeout"/> before abandoning it and returning control to the loop.
    /// </summary>
    public async Task<AbandonableStepResult> RunAsync(
        Func<Task> step, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            return new AbandonableStepResult(AbandonableStepOutcome.SkippedStillRunning);
        }

        Task work;
        try
        {
            work = step();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _inFlight, 0);
            return new AbandonableStepResult(AbandonableStepOutcome.Faulted, ex);
        }

        /* The guard clears when the task TRULY ends — completion, fault, or cancellation — never when
           the deadline merely moves the loop on. Faults on the abandoned path are observed here too, so
           an abandoned-then-faulted task cannot surface as UnobservedTaskException. */
        _ = work.ContinueWith(
            static (t, state) =>
            {
                _ = t.Exception; /* observe */
                Interlocked.Exchange(ref ((AbandonableStep)state!)._inFlight, 0);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var finished = await Task.WhenAny(work, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);

        if (finished != work)
        {
            return cancellationToken.IsCancellationRequested
                ? new AbandonableStepResult(AbandonableStepOutcome.Cancelled)
                : new AbandonableStepResult(AbandonableStepOutcome.Abandoned);
        }

        try
        {
            await work.ConfigureAwait(false);
            return new AbandonableStepResult(AbandonableStepOutcome.Completed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AbandonableStepResult(AbandonableStepOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            return new AbandonableStepResult(AbandonableStepOutcome.Faulted, ex);
        }
    }
}
