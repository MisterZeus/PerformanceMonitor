/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using PerformanceMonitor.Ui;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// Pins <see cref="TagColorPalette"/> — the shared, pure tag-colour palette and its deterministic tag → colour
/// assignment (#2008 stage 2a). Mirror of Darling.Tests' copy (the ServerOverviewSort/Filter precedent). The
/// palette is used by the viewer now and will be shared with Lite when tags reach it.
/// </summary>
public sealed class TagColorPaletteTests
{
    [Fact]
    public void Swatches_AreAllValidHex_AndDistinct()
    {
        Assert.NotEmpty(TagColorPalette.Swatches);
        Assert.All(TagColorPalette.Swatches, hex => Assert.True(TagColorPalette.IsValidStoredColour(hex), $"{hex} is not #RRGGBB"));
        Assert.Equal(
            TagColorPalette.Swatches.Count,
            TagColorPalette.Swatches.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ForTagId_IsDeterministic_InPalette_AndRotatesById()
    {
        var n = TagColorPalette.Swatches.Count;

        Assert.Equal(TagColorPalette.ForTagId(7), TagColorPalette.ForTagId(7));   // stable for an id
        Assert.Contains(TagColorPalette.ForTagId(3), TagColorPalette.Swatches);   // always a palette value
        Assert.Equal(TagColorPalette.ForTagId(2), TagColorPalette.ForTagId(2 + n)); // wraps by palette length
        Assert.NotEqual(TagColorPalette.ForTagId(2), TagColorPalette.ForTagId(3)); // adjacent ids differ
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("#B5484A", true)]
    [InlineData("#b5484a", true)]
    [InlineData("B5484A", false)]    // no leading hash
    [InlineData("#B5484", false)]    // too short
    [InlineData("#B5484AA", false)]  // too long
    [InlineData("#GGGGGG", false)]   // non-hex digits
    public void IsValidStoredColour_AcceptsHexOrNull_RejectsGarbage(string? colour, bool expected)
    {
        Assert.Equal(expected, TagColorPalette.IsValidStoredColour(colour));
    }
}
