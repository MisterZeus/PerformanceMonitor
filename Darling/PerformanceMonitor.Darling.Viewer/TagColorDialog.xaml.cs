/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The tag colour picker (#2008 stage 2a): a grid of the fixed <see cref="TagColorPalette"/> swatches plus a
/// "No colour" option. A modal chooser rather than a full colour wheel, because the palette is deliberately
/// fixed (theme-safe, stable-by-id). On OK, <see cref="SelectedColour"/> is the chosen <c>#RRGGBB</c> or null
/// (No colour); a cancel leaves <see cref="Window.DialogResult"/> unset so the caller writes nothing.
/// </summary>
public partial class TagColorDialog : Window
{
    /// <summary>The chosen colour when the dialog returns true: a <c>#RRGGBB</c> string, or null for "no colour".</summary>
    public string? SelectedColour { get; private set; }

    public TagColorDialog(string? current)
    {
        InitializeComponent();

        foreach (var hex in TagColorPalette.Swatches)
        {
            var isCurrent = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 56,
                Height = 32,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(3),
                Background = TagColorBrushes.Fill(hex),
                BorderBrush = isCurrent ? TagColorBrushes.PillText : TagColorBrushes.SwatchBorder,
                BorderThickness = new Thickness(isCurrent ? 2 : 1),
                Cursor = Cursors.Hand,
                ToolTip = hex,
                Tag = hex,
            };
            swatch.MouseLeftButtonUp += Swatch_Click;
            SwatchPanel.Children.Add(swatch);
        }
    }

    private void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string hex })
        {
            SelectedColour = hex;
            DialogResult = true;
        }
    }

    private void Default_Click(object sender, RoutedEventArgs e)
    {
        SelectedColour = null;
        DialogResult = true;
    }
}
