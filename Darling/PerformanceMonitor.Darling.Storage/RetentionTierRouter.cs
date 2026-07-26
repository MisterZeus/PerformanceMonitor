/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

namespace PerformanceMonitor.Darling.Storage;

/// <summary>Which physical tier answers a window: the raw hypertable, the hourly CAGG, or the daily CAGG.</summary>
public enum RetentionTier
{
    /// <summary>The raw hypertable — full per-sweep rows, including query text and plan XML.</summary>
    Raw,

    /// <summary>The hourly continuous aggregate — pre-summed per hour, no per-row text.</summary>
    Hourly,

    /// <summary>The daily continuous aggregate — pre-summed per day, kept indefinitely, no per-row text.</summary>
    Daily,
}

/// <summary>
/// Picks the retention tier that can actually answer a window, by the AGE of the window's oldest point.
///
/// <para>This is the single source of truth for that decision. It exists in Storage because BOTH readers need it
/// and neither can see the other: the composer lives in the service (<c>ComposeSourceRouter</c>, which delegates
/// its age decision here) and the viewer's built-in tabs live in the viewer, which does not reference the service.
/// Before #1661 only the composer routed, so the built-in tabs read the raw table exclusively and silently
/// returned ~4 days for any longer window once <see cref="TimescaleSupport.EnsureRetentionPoliciesAsync"/> began
/// dropping chunks.</para>
///
/// <para>Age is measured from a caller-supplied <c>nowUtc</c> rather than the window's end, because retention drops
/// chunks by actual wall-clock now: a purely historical window ("30 to 25 days ago") has to reach a tier that still
/// retains it or it comes back empty. The thresholds sit deliberately inside the matching retention horizon so a
/// route never lands on an about-to-drop chunk.</para>
///
/// <para><b>A rollup cannot serve per-row text.</b> The CAGGs group by identity columns and sum deltas — they carry
/// no <c>query_text</c> and no <c>query_plan</c>. A reader that projects either is limited to the raw horizon no
/// matter how wide the requested window is, which is what <see cref="RawTextHorizon"/> is for: clamp the window and
/// tell the user, rather than silently returning a short slice of what they asked for.</para>
/// </summary>
public static class RetentionTierRouter
{
    /// <summary>
    /// Raw answers windows whose oldest point is within this age — a day inside the 4-day
    /// <see cref="TimescaleSupport.RawRetentionInterval"/>, so raw never routes to an about-to-drop chunk.
    /// </summary>
    public static readonly TimeSpan RawMaxAge = TimeSpan.FromDays(3);

    /// <summary>
    /// The hourly CAGG answers up to this age — a day inside the 21-day
    /// <see cref="TimescaleSupport.HourlyRetentionInterval"/>; older windows fall to the daily CAGG, which has no
    /// retention policy and is kept indefinitely.
    /// </summary>
    public static readonly TimeSpan HourlyMaxAge = TimeSpan.FromDays(20);

    /// <summary>
    /// How far back per-row text (<c>query_text</c>, <c>query_plan</c>) actually exists. Identical to
    /// <see cref="RawMaxAge"/> — text lives only in raw — but named separately because it means something
    /// different to a caller: not "which relation do I read" but "how much of the user's requested window can I
    /// honestly answer at all".
    /// </summary>
    public static TimeSpan RawTextHorizon => RawMaxAge;

    /// <summary>
    /// The tier that can answer a window whose oldest point is <paramref name="windowStartUtc"/>, as of
    /// <paramref name="nowUtc"/>. A window starting in the future (or now) is Raw.
    /// </summary>
    public static RetentionTier Resolve(DateTime nowUtc, DateTime windowStartUtc)
    {
        var age = nowUtc - windowStartUtc;

        if (age <= RawMaxAge)
        {
            return RetentionTier.Raw;
        }

        return age <= HourlyMaxAge ? RetentionTier.Hourly : RetentionTier.Daily;
    }

    /// <summary>
    /// The earliest instant a reader that projects per-row text can honestly cover, as of
    /// <paramref name="nowUtc"/>. Callers clamp their window start to this and surface the clamp.
    /// </summary>
    public static DateTime OldestTextInstant(DateTime nowUtc) => nowUtc - RawTextHorizon;

    /// <summary>
    /// Clamps <paramref name="windowStartUtc"/> to the text horizon. <c>clamped</c> is true when the caller asked
    /// for more history than per-row text exists for, which is the signal to tell the user rather than quietly
    /// narrowing what they requested.
    /// </summary>
    public static (DateTime EffectiveStartUtc, bool Clamped) ClampToTextHorizon(DateTime nowUtc, DateTime windowStartUtc)
    {
        var oldest = OldestTextInstant(nowUtc);
        return windowStartUtc < oldest ? (oldest, true) : (windowStartUtc, false);
    }
}
