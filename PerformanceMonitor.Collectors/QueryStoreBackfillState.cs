/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// The STORED contract of the Query Store backfill worker (#2022/#2058) — the collector_state
/// identity and the hole-range codec, shared by both SKUs' workers and both hosts' clamp-site
/// recording so rows written by one build always decode in the next (and a Lite store opened
/// after a Darling-informed fix, or vice versa, can never disagree on what a hole row means).
/// Lives beside <see cref="WatermarkPolicy"/> for the same reason it does: this is watermark-shaped
/// state that must mean the same thing everywhere.
///
/// <para>The worker itself stays per-SKU (each host owns its tick, its connections, and its
/// HORIZON — Darling's is derived from its raw retention tier, Lite's from its resolved
/// query_store retention; deliberately not shared, per the different staging decisions on
/// #2022/#2058). What is shared is only what is PERSISTED.</para>
/// </summary>
public static class QueryStoreBackfillState
{
    /// <summary>The collector_state owner name for the worker's rows — distinct from the
    /// query_store definition on purpose, so the definition keeps declaring NO state keys and the
    /// state-contract pins stay honest.</summary>
    public const string StateCollectorName = "query_store_backfill";

    /// <summary>State key prefix marking a database's first-contact tail as drained (value: when).</summary>
    public const string DoneKeyPrefix = "done:";

    /// <summary>State key prefix for a recorded clamp hole (value: <see cref="EncodeHole"/>).</summary>
    public const string HoleKeyPrefix = "hole:";

    /// <summary>Encodes a hole range as <c>from|to</c> in round-trip format — deliberately not
    /// JSON, so the state row stays greppable and the codec dependency-free.</summary>
    public static string EncodeHole(DateTime fromUtc, DateTime toUtc)
        => fromUtc.ToString("o", CultureInfo.InvariantCulture) + "|" + toUtc.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Decodes <see cref="EncodeHole"/>; false on any malformed value, which the scan
    /// treats as "no hole recorded" — the conservative direction (the tail logic still runs).</summary>
    public static bool TryDecodeHole(string encoded, out DateTime fromUtc, out DateTime toUtc)
    {
        fromUtc = default;
        toUtc = default;

        var split = encoded.Split('|');
        if (split.Length != 2)
        {
            return false;
        }

        return DateTime.TryParseExact(split[0], "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out fromUtc)
            && DateTime.TryParseExact(split[1], "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out toUtc)
            && fromUtc < toUtc;
    }

    /// <summary>Merges a newly-clamped hole into whatever is already recorded — a repeat outage
    /// WIDENS the range rather than overwriting it, so the earlier hole cannot be lost.</summary>
    public static (DateTime FromUtc, DateTime ToUtc) MergeHole(string? existingEncoded, DateTime fromUtc, DateTime toUtc)
    {
        if (existingEncoded is not null && TryDecodeHole(existingEncoded, out var f, out var t))
        {
            return (fromUtc < f ? fromUtc : f, toUtc > t ? toUtc : t);
        }

        return (fromUtc, toUtc);
    }
}
