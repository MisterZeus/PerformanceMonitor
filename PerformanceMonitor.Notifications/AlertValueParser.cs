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
/// </summary>
public static class AlertValueParser
{
    /// <summary>
    /// The leading numeric token of <paramref name="text"/> (sign, digits, culture decimal
    /// separator; a trailing <c>%</c> or label is ignored), or <paramref name="fallback"/> when the
    /// text carries no number at all (state strings like <c>"PRIMARY"</c> or <c>"Online"</c> — the
    /// history column is NOT NULL, so those still need a value).
    /// </summary>
    public static double ParseOrDefault(string? text, double fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var culture = CultureInfo.CurrentCulture;
        var decimalSeparator = culture.NumberFormat.NumberDecimalSeparator;

        /* Scan to the first digit (skipping a directly-attached sign), then take digits and at most
           one decimal separator. Char-scan rather than regex: the separator is culture-dependent and
           the inputs are short. */
        var span = text.AsSpan();
        for (int start = 0; start < span.Length; start++)
        {
            bool signed = (span[start] == '-' || span[start] == '+')
                && start + 1 < span.Length && char.IsAsciiDigit(span[start + 1]);
            if (!signed && !char.IsAsciiDigit(span[start]))
            {
                continue;
            }

            int end = signed ? start + 1 : start;
            bool seenSeparator = false;
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

                break;
            }

            return double.TryParse(span[start..end], NumberStyles.Float, culture, out var value)
                ? value
                : fallback;
        }

        return fallback;
    }
}
