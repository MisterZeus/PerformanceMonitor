/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace PerformanceMonitor.Darling.Tests;

/// <summary>
/// Pins the #2008 fix: the Viewer's tag editors gate on the read-only probe like every other write
/// surface. The tag menus shipped without the gate, so a least-privilege viewer-role seat showed
/// working-looking editors whose writes then died as 42501 behind a status-bar line — and the role
/// model is DELIBERATE (the viewer role's only write is config.custom_views; tags are shared fleet
/// configuration and stay admin-seat-edited), so the fix is the missing gate, not a broader grant.
/// Source pins, the BaselineSupplyTests worker-pin idiom: the three tag entry points must each
/// consult the gate, and the gate must read <c>IsReadOnly</c> — a refactor that reroutes any of
/// them around the probe reintroduces the silent-failure seat.
/// </summary>
public sealed class ViewerTagReadOnlyGateTests
{
    [Fact]
    public void EveryTagEntryPoint_ConsultsTheReadOnlyGate()
    {
        var source = ReadTagSource();

        Assert.Contains("private bool CanEditTags => _dataService?.IsReadOnly == false;", source, StringComparison.Ordinal);

        AssertGatedWithin(source, "private void TagHeader_ContextMenuOpening");
        AssertGatedWithin(source, "private void ServerRow_ContextMenuOpening");
        AssertGatedWithin(source, "private void ManageTags_Click");
    }

    [Fact]
    public void DisabledTagEditors_CarryTheReason_NotJustAGreyOut()
    {
        var source = ReadTagSource();

        Assert.Contains("ReadOnlyTagToolTip", source, StringComparison.Ordinal);
        /* WPF suppresses tooltips on disabled elements unless explicitly told otherwise — without
           this the reason exists but never shows, which is #2008's silence back again. */
        Assert.Contains("ToolTipService.SetShowOnDisabled", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ManageTagsWindow_CarriesItsOwnGate_ForEntryPointsTheMenusMiss()
    {
        /* Review catch on #2011: the window is all writes and trusted its call sites. The
           defense-in-depth gate must live in the window itself, generic over the whole content
           tree, so a future ungated entry point lands on inert controls with the reason. */
        var source = ReadSource(Path.Combine(
            "Darling", "PerformanceMonitor.Darling.Viewer", "ManageTagsWindow.xaml.cs"));

        Assert.Contains("dataService.IsReadOnly", source, StringComparison.Ordinal);
        Assert.Contains("read-only seat", source, StringComparison.Ordinal);
    }

    /// <summary>The member starting at <paramref name="memberSignature"/> must reference the gate
    /// before the next member begins (crude but stable: members here are short).</summary>
    private static void AssertGatedWithin(string source, string memberSignature)
    {
        var start = source.IndexOf(memberSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing member: {memberSignature}");

        var next = source.IndexOf("\n    private ", start + memberSignature.Length, StringComparison.Ordinal);
        var body = next > start ? source[start..next] : source[start..];
        Assert.True(body.Contains("CanEditTags", StringComparison.Ordinal),
            $"{memberSignature} must consult CanEditTags — every tag entry point gates on the read-only probe (#2008)");
    }

    private static string ReadTagSource([CallerFilePath] string thisFile = "")
        => ReadSource(Path.Combine("Darling", "PerformanceMonitor.Darling.Viewer", "MainWindow.Tags.cs"), thisFile);

    private static string ReadSource(string relative, [CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, relative)))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.False(dir is null, "could not locate the repo root from the test source path");
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
