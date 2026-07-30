/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// Bounds how far back an incremental collector will catch up after an outage (#1556). The field
/// incident was query_store: a service stopped for days came back, read its stored per-database
/// watermark, and issued a cutoff pointing DAYS into the past — the source retains ~30 days, so the
/// one cycle tried to pull the entire backlog at once and drove the 0→13GB commit-limit blowout.
///
/// <para>
/// <see cref="ClampCatchup"/> floors a stale watermark to <c>now - 24h</c>: a routine restart or a
/// brief outage never clamps (its watermark is minutes old), a multi-day outage survives with a
/// deliberate, logged, BOUNDED hole (the source still retains the older data; the viewer's windows
/// are 24h anyway). This is deliberately NOT applied to every timestamp watermark — for a ring-buffer
/// or rolling-trace source the clamp is a no-op at best and, on a quiet <c>default_trace</c> whose
/// 100MB ring can span days, a WRONG truncation of legitimate catch-up. It is therefore scoped to
/// exactly ONE collector: query_store's per-database cutoff (the only unbounded-persisted source among
/// the collectors). It lives here as a pure function purely so that placement is unit-testable in
/// isolation.
/// </para>
///
/// <para>
/// Two call sites, one collector. The hosts apply it in the enumeration loop's per-database watermark
/// refresh, where they also emit the operator-visible WARNING; and <c>QueryStoreCollector</c> applies it
/// again inside its own cutoff computation, which is what makes the bound hold on the Azure SQL DB
/// per-database path (#1836) — that host branch is shared with the XE ring-buffer collectors and so
/// deliberately does not clamp. Applying it twice is a no-op by construction: clamping an
/// already-clamped value returns it unchanged.
/// </para>
/// </summary>
public static class WatermarkPolicy
{
    /// <summary>
    /// The maximum catch-up horizon. 24h is chosen so routine outages never clamp while a multi-day
    /// outage survives with a single logged, bounded hole. Exposed so a test pins the boundary.
    /// </summary>
    public static readonly TimeSpan MaxCatchup = TimeSpan.FromHours(24);

    /// <summary>
    /// Floors a &gt;24h-stale timestamp watermark to <c>now - 24h</c>; a null watermark (nothing
    /// collected yet — the definition's documented first-run window applies) stays null, and a
    /// watermark within the horizon is returned unchanged. Compare the result to the input to tell
    /// whether a clamp fired (the runner logs a WARNING when it does).
    /// </summary>
    /// <param name="watermark">The stored watermark (newest already-collected timestamp), or null.</param>
    /// <param name="now">The current collection time (the cutoff is measured back from here).</param>
    public static DateTime? ClampCatchup(DateTime? watermark, DateTime now)
    {
        if (watermark is null)
        {
            return null;
        }

        var floor = now - MaxCatchup;
        return watermark.Value < floor ? floor : watermark;
    }
}
