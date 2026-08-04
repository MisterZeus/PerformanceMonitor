/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Windows.Media;
using PerformanceMonitor.Ui;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// Turns a stored tag colour (<c>#RRGGBB</c>, or null for "no colour") into a frozen WPF brush, with a neutral
/// fallback for an unset or malformed value. The Lite twin of the Darling viewer's <c>TagColorBrushes</c> —
/// same fixed neutral/label values and frozen-brush caching, so a tag pill looks identical across the two apps;
/// the palette values themselves live in the shared <see cref="TagColorPalette"/>.
/// </summary>
public static class TagColorBrushes
{
    /// <summary>Fill for a tag with no colour — a dark neutral chip (theme-independent, like the severity dots).</summary>
    public static readonly SolidColorBrush Neutral = Frozen("#2A2D35");

    /// <summary>The constant light label colour on every pill, chosen to read on the whole palette.</summary>
    public static readonly SolidColorBrush PillText = Frozen("#E4E6EB");

    /// <summary>Border for the neutral/unselected swatch in the colour picker.</summary>
    public static readonly SolidColorBrush SwatchBorder = Frozen("#555A63");

    private static readonly Dictionary<string, SolidColorBrush> s_cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The pill fill for a stored colour: the colour itself when it is a valid <c>#RRGGBB</c>, else
    /// <see cref="Neutral"/>. Cached and frozen, so repeated card renders reuse one brush per colour.</summary>
    public static SolidColorBrush Fill(string? colour)
    {
        if (!TagColorPalette.IsValidStoredColour(colour) || string.IsNullOrEmpty(colour))
        {
            return Neutral;
        }

        if (!s_cache.TryGetValue(colour, out var brush))
        {
            brush = Frozen(colour);
            s_cache[colour] = brush;
        }

        return brush;
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
