/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Linq;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins <see cref="FleetView"/>, the seam that separates "the whole fleet" from "what the sidebar is
/// currently showing".
///
/// Before it, <c>ServerList.ItemsSource</c> WAS the fleet, and ten call sites read it back to answer
/// "what servers exist?" — the alert-badge painter, the Overview builder, the deep-link resolver, the
/// card click handlers, the schedule editor. That is correct only while the bound list is complete. Once
/// the sidebar shows a filtered subset (or gains tag rows), every one of those consumers silently answers
/// the wrong question: badges stop painting for hidden servers, a card click resolves to nothing, the
/// schedule editor receives a truncated fleet. Nothing throws — it just quietly misbehaves, which is why
/// the rules live here, WPF-free and asserted, rather than in a window.
/// </summary>
public sealed class FleetViewTests
{
    private static DarlingServer Server(int id, string name) =>
        new(id, name, name, isEnabled: true, sqlMajorVersion: 16);

    private static FleetView FleetOf(params DarlingServer[] servers)
    {
        var fleet = new FleetView();
        fleet.SetAll(servers);
        return fleet;
    }

    [Fact]
    public void SetAll_PublishesTheWholeFleetAndProjectsIt()
    {
        var fleet = FleetOf(Server(1, "SQL01"), Server(2, "SQL02"));

        Assert.Equal(new[] { 1, 2 }, fleet.All.Select(s => s.ServerId));
        Assert.Equal(2, fleet.TotalCount);

        /* Step 1 is deliberately behaviour-neutral: no filters exist yet, so the projection is identity
           and nothing may report itself as filtered. */
        Assert.Equal(new[] { 1, 2 }, fleet.Visible.Select(s => s.ServerId));
        Assert.Equal(2, fleet.VisibleCount);
        Assert.False(fleet.IsFiltered);
    }

    [Fact]
    public void SetAll_RaisesProjectionChanged_SoTheWindowRebindsOnce()
    {
        var fleet = new FleetView();
        var raised = 0;
        fleet.ProjectionChanged += (_, _) => raised++;

        fleet.SetAll(new[] { Server(1, "SQL01") });

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Find_ResolvesAgainstTheWholeFleet_NotTheProjection()
    {
        var fleet = FleetOf(Server(1, "SQL01"), Server(2, "SQL02"));

        /* The load-bearing rule: a deep link, an Overview card click, or an acknowledge must reach a
           server even when the sidebar is not showing it. Asserted through the public surface every
           consumer uses, so a future filter cannot quietly narrow it. */
        Assert.Equal("SQL02", fleet.Find(2)!.ServerName);
        Assert.Null(fleet.Find(999));
    }

    [Fact]
    public void ResolveSelection_KeepsThePreviousServer_WhenItSurvives()
    {
        var fleet = FleetOf(Server(1, "SQL01"), Server(2, "SQL02"), Server(3, "SQL03"));

        /* Resolved by id, not index, so a re-sort (favourites) or a rename does not move the selection. */
        Assert.Equal(3, fleet.ResolveSelection(previousServerId: 3)!.ServerId);
    }

    [Fact]
    public void ResolveSelection_FallsBackToTheFirstVisibleServer()
    {
        var fleet = FleetOf(Server(7, "SQL07"), Server(8, "SQL08"));

        /* A server that no longer exists (removed) falls back to the first VISIBLE server — never to
           index 0 of the bound list, which becomes a tag header once tag rows share it. */
        Assert.Equal(7, fleet.ResolveSelection(previousServerId: 999)!.ServerId);
        Assert.Equal(7, fleet.ResolveSelection(previousServerId: null)!.ServerId);
    }

    [Fact]
    public void ResolveSelection_IsNullWhenThereIsNothingToSelect()
    {
        var fleet = FleetOf();

        Assert.Null(fleet.ResolveSelection(previousServerId: null));
        Assert.Null(fleet.ResolveSelection(previousServerId: 1));
    }

    [Fact]
    public void SetAll_ReplacesRatherThanAccumulates()
    {
        var fleet = FleetOf(Server(1, "SQL01"), Server(2, "SQL02"));

        fleet.SetAll(new[] { Server(3, "SQL03") });

        /* A reload after a remove must not leave the deleted servers resolvable — otherwise a stale id
           keeps opening tabs for a server that is gone. */
        Assert.Equal(new[] { 3 }, fleet.All.Select(s => s.ServerId));
        Assert.Null(fleet.Find(1));
    }
}
