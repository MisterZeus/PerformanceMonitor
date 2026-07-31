/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Windows;

namespace PerformanceMonitor.Ui;

/// <summary>
/// Shared, app-agnostic host window for a graph viewer control (block-chain today, deadlock graph next).
/// Hosts any <see cref="FrameworkElement"/>; if it implements <see cref="IGraphViewer"/>, the window
/// calls <see cref="IGraphViewer.Cleanup"/> on close so the control unsubscribes from theme changes.
/// Shown owned + non-modal so it stays interactive above a modal host and closes with it — same model as
/// the per-app PlanViewerWindow.
/// </summary>
public partial class GraphViewerWindow : Window
{
    private readonly IGraphViewer? _viewer;

    public GraphViewerWindow(FrameworkElement content, string title)
    {
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(title))
            Title = title.Length > 100 ? title[..100] : title;

        _viewer = content as IGraphViewer;
        ContentHost.Children.Add(content);
        Closed += (_, _) => _viewer?.Cleanup();
    }

    /// <summary>
    /// Esc closes the window. Bubbling KeyDown rather than PreviewKeyDown on purpose: a hosted control that
    /// wants Esc for itself marks the event handled and never reaches here, whereas a preview handler would
    /// take Esc away from it. There is no close button to hang <c>IsCancel</c> on — the hosted viewer fills
    /// the window edge to edge. Same treatment as the per-app PlanViewerWindow.
    /// </summary>
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>
    /// Opens a new, owned, non-modal graph window hosting <paramref name="content"/>. A new window per
    /// call lets the user compare graphs side by side; owned so it stays usable above a modal host window.
    /// </summary>
    public static GraphViewerWindow ShowGraph(Window? owner, FrameworkElement content, string title)
    {
        var window = new GraphViewerWindow(content, title) { Owner = owner };
        window.Show();
        return window;
    }
}
