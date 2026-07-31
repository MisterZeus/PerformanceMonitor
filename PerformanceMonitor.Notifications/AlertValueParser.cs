/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;

namespace PerformanceMonitor.Notifications;

/// <summary>
/// Extracts the numeric half of an alert's display text for the history stores' fallback path
/// (#1830). The stores' original fallback was <c>double.TryParse(text.TrimEnd('%'))</c>, which fails
/// on any DECORATED value — <c>"87% (Total CPU)"</c> ends with <c>)</c> so the trim is a no-op, the
/// parse fails, and the <c>: 0</c> arm silently stored 0 for every High CPU alert ever recorded, in
/// Lite and Darling alike. Producers should pass real numerics on the <c>AlertOutcome</c>; this
/// parser is the belt so a future text-only alert cannot re-coin a silent 0 when its text still
/// carries a number.
///
/// <para>Parses with <see cref="CultureInfo.CurrentCulture"/> deliberately: the producers format
/// their display text with CurrentCulture too, so a de-DE <c>"92,5%"</c> must parse as 92.5 there.
/// Hardening this to InvariantCulture would break exactly the locales the fallback exists for.</para>
///
/// <para>Grouped numbers are understood, because a truncated-but-plausible number is the same silent
/// wrong value #1830 was about, only harder to spot. No producer formats with <c>"N0"</c> TODAY, but
/// the adjacent alert code does (<c>AlertContextBuilders</c> fills its detail fields that way), so
/// "nobody groups" is a habit and not a guarantee. Left unhandled, an en-US <c>"1,234"</c> would
/// store 1 — and de-DE is worse, since there the group separator IS <c>.</c>, so an ordinary
/// <c>"1.234"</c> would too.</para>
/// </summary>
public static class AlertValueParser
{
    /// <summary>
    /// The leading numeric token of <paramref name="text"/> (sign, digits, culture group separators,
    /// culture decimal separator; a trailing <c>%</c> or label is ignored), or
    /// <paramref name="fallback"/> when the text carries no number at all (state strings like
    /// <c>"PRIMARY"</c> or <c>"Online"</c> — the history column is NOT NULL, so those still need a
    /// value).
    /// </summary>
    public static double ParseOrDefault(string? text, double fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var culture = CultureInfo.CurrentCulture;
        var decimalSeparator = culture.NumberFormat.NumberDecimalSeparator;
        var groupSeparator = culture.NumberFormat.NumberGroupSeparator;

        /* Scan to the first digit (skipping a directly-attached sign), then take digits, whole group
           separators, and at most one decimal separator. Char-scan rather than regex: the separators
           are culture-dependent and the inputs are short. */
        var span = text.AsSpan();
        for (int start = 0; start < span.Length; start++)
        {
            bool signed = (span[start] == '-' || span[start] == '+')
                && start + 1 < span.Length && char.IsAsciiDigit(span[start + 1]);
            if (!signed && !char.IsAsciiDigit(span[start]))
            {
                continue;
            }

            int digitsStart = signed ? start + 1 : start;
            int end = digitsStart;
            bool seenSeparator = false;
            bool seenGroup = false;
            while (end < span.Length)
            {
                if (char.IsAsciiDigit(span[end]))
                {
                    end++;
                    continue;
                }

                if (!seenSeparator
                    && span.Slice(end).StartsWith(decimalSeparator, StringComparison.Ordinal)
                    && end + decimalSeparator.Length < span.Length
                    && char.IsAsciiDigit(span[end + decimalSeparator.Length]))
                {
                    seenSeparator = true;
                    end += decimalSeparator.Length;
                    continue;
                }

                /* The LEADING run has to be a valid first group too (1-3 digits), or "1234,567" would fuse
                   into 1234567 — a new way to join two adjacent numbers, which is the exact thing the
                   strictness below exists to prevent, and worse than the 1234 it returned before grouping
                   was handled at all. Only the first separator needs the check: every later run is exactly
                   three digits because IsGroup already required it. The bound is on GROUPS only — a plain
                   "1234.56" has no group separator and is untouched. */
                if (!seenSeparator
                    && (seenGroup || end - digitsStart is >= 1 and <= GroupSize)
                    && IsGroup(span, end, groupSeparator))
                {
                    seenGroup = true;
                    end += groupSeparator.Length + GroupSize;
                    continue;
                }

                break;
            }

            return double.TryParse(span[start..end], NumberStyles.Float | NumberStyles.AllowThousands, culture, out var value)
                ? value
                : fallback;
        }

        return fallback;
    }

    /// <summary>Digits per group. Every culture this ships to groups by three.</summary>
    private const int GroupSize = 3;

    /// <summary>
    /// Whether a WELL-FORMED group boundary starts at <paramref name="at"/>: the culture's group
    /// separator followed by exactly <see cref="GroupSize"/> digits and then a non-digit.
    ///
    /// <para>The strictness is the entire point. .NET's own <c>AllowThousands</c> does not validate
    /// group placement — <c>double.Parse("1,2,3", en-US)</c> happily returns 123 — so handing the
    /// token to <c>TryParse</c> and trusting it would turn any comma-joined list into a fused number.
    /// Requiring a full group means <c>"1,234"</c> reads as 1234 while <c>"1,5"</c>, <c>"55,66"</c>
    /// and <c>"1,2,3"</c> stop at the first run exactly as they did before, and a culture whose group
    /// separator is a narrow no-break space cannot be tricked by an ordinary space.</para>
    ///
    /// <para>Cultures that group by something other than three (hi-IN's 3-then-2) fall out here and
    /// keep the leading run, which is what they got before this existed — no regression, just no
    /// improvement.</para>
    /// </summary>
    private static bool IsGroup(ReadOnlySpan<char> span, int at, string groupSeparator)
    {
        if (groupSeparator.Length == 0 || !span.Slice(at).StartsWith(groupSeparator, StringComparison.Ordinal))
        {
            return false;
        }

        int digits = at + groupSeparator.Length;
        if (digits + GroupSize > span.Length)
        {
            return false;
        }

        for (int i = digits; i < digits + GroupSize; i++)
        {
            if (!char.IsAsciiDigit(span[i]))
            {
                return false;
            }
        }

        return digits + GroupSize == span.Length || !char.IsAsciiDigit(span[digits + GroupSize]);
    }
}
