/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PerformanceMonitorLite;

/// <summary>
/// Chooses the sidebar row template — a group header vs a server row — so one <see cref="ListBox"/> can
/// render the mixed <see cref="FleetView"/> projection. Set as <c>ServerListView.ItemTemplateSelector</c>;
/// the two templates are supplied from XAML. Ported from the Darling viewer's identical selector.
/// </summary>
internal sealed class FleetRowTemplateSelector : DataTemplateSelector
{
    /// <summary>Template for a <see cref="FleetHeaderRow"/> (tag / Favorites / Untagged).</summary>
    public DataTemplate? HeaderTemplate { get; set; }

    /// <summary>Template for a <see cref="FleetServerRow"/> (the server row).</summary>
    public DataTemplate? ServerTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        FleetHeaderRow => HeaderTemplate,
        FleetServerRow => ServerTemplate,
        _ => base.SelectTemplate(item, container)
    };
}

/// <summary>
/// Turns a <see cref="FleetRow.Depth"/> into a left indent so nested tags and their servers step inward.
/// The step is deliberately small: with a 4-level nesting cap the deepest server indents ~5 steps, which
/// must still fit a narrow sidebar without truncating names.
/// </summary>
[ValueConversion(typeof(int), typeof(Thickness))]
internal sealed class FleetDepthToIndentConverter : IValueConverter
{
    private const double StepPixels = 14;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(value is int depth ? depth * StepPixels : 0, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
