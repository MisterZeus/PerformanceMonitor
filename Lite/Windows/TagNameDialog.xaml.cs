/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Windows;

namespace PerformanceMonitorLite.Windows;

/// <summary>
/// A minimal single-line prompt reused by the tag toolbar actions (New Tag, New Child Tag, Rename).
/// Returns the trimmed name via <see cref="TagName"/> when <see cref="Window.ShowDialog"/> is true; the
/// empty-name guard lives in <see cref="Ok_Click"/> so an empty name can never be committed.
/// </summary>
public partial class TagNameDialog : Window
{
    /// <summary>The entered name, trimmed. Only meaningful when the dialog returned true.</summary>
    public string TagName => NameBox.Text.Trim();

    public TagNameDialog(string title, string prompt, string initial = "")
    {
        InitializeComponent();

        Title = title;
        PromptLabel.Text = prompt;
        NameBox.Text = initial;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            return;
        }

        DialogResult = true;
    }
}
