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
    /// The first number appearing ANYWHERE in <paramref name="text"/> (sign, digits, culture decimal
    /// separator; anything before or after it is ignored), or <paramref name="fallback"/> when the
    /// text carries no digit at all (state strings like <c>"PRIMARY"</c> or <c>"Online"</c> — the
    /// history column is NOT NULL, so those still need a value).
    ///
    /// <para><b>ANYWHERE, not "leading" — read this before calling it (#1881).</b> This summary said
    /// "leading numeric token" for its whole life, and the call sites were written against that
    /// promise, but the scan below skips non-digits rather than bailing on them. The difference is
    /// invisible for the case it was built for (<c>"87% (Total CPU)"</c>, where the number leads
    /// anyway) and decisive for prose: the first digit in a sentence is whatever happened to be
    /// there, so <c>"PostgreSQL 18"</c> yields 18 and an object name like <c>"Sales2024"</c> yields
    /// 2024. Neither is a measurement of anything the alert is about.</para>
    ///
    /// <para>The scan is deliberately NOT tightened to require a leading number, because at least one
    /// real producer depends on it: <c>Store Disk Pressure</c>'s text is
    /// <c>"The monitor store's disk volume has only 7% free (…)"</c>, whose percent-free IS the right
    /// value and does not lead. Tightening would silently zero it — and a stored 0 there means a FULL
    /// volume, the one reading that must never be invented. The fix for #1881 is therefore on the
    /// producer side instead: a metric whose value is a measurement passes it explicitly as
    /// <c>AlertOutcome.NumericCurrentValue</c>, and a metric whose value is a STATE passes an explicit
    /// 0 and joins <c>AlertMetricClassifier.IsStateOnly</c>. Both leave this scan un-consulted, which
    /// is the point: nothing reaches it whose text a semantically-wrong number can be coined from.</para>
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

    /// <summary>
    /// What an alert-history store writes into its NOT NULL <c>current_value</c> /
    /// <c>threshold_value</c> column: the producer's numeric when it supplied one, else the fallback
    /// parse of the display text.
    ///
    /// <para>This one expression was written out twice — once in Lite's <c>DuckDbAlertHistoryStore</c>
    /// and once in Darling's <c>PgAlertHistoryStore</c>, four call sites between them — which is the
    /// shape that lets the two stores drift apart on the exact question #1881 is about (whether a row
    /// stores a measurement or a coincidence). Both now call this, so a producer's stored value is
    /// decided in ONE place and can be pinned without a live store: the resolve is the seam, and the
    /// bug this whole issue describes lives in it rather than on either side.</para>
    /// </summary>
    public static double ResolveStoredValue(double? numericValue, string? displayText) =>
        numericValue ?? ParseOrDefault(displayText);
}
