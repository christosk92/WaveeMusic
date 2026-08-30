using System;
using System.Collections.Generic;
using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE PLAYLIST TREE'S MULTI-SELECTION, driven directly. <see cref="SidebarTreeSelection"/> is engine-free and
/// source-included, so WinUI's Extended semantics are pinned here as RULES rather than through a mounted pane.
///
/// <para>The rules being pinned are <c>SelectionModel.OnInteractedAction</c>'s Extended arm
/// (<c>ExtendedSelector.cpp:18-53</c>), ported by KEY: Shift replaces the selection with the anchor range, Ctrl
/// toggles, a plain interaction clears-and-selects only when the row was not already selected, and every single-item
/// operation moves the anchor. What is deliberately NOT ported is the INDEX addressing — the sidebar's tree re-flows
/// constantly (a collapse, a search, a projection, a customizer edit), so an index selected one frame names a
/// different playlist the next.</para>
/// </summary>
public class SidebarTreeSelectionTests
{
    static readonly string[] Order = ["a", "b", "c", "d", "e"];

    static SidebarTreeSelection New() => new();

    static string[] Sel(SidebarTreeSelection s) => s.Ordered(Order).ToArray();

    // ── the Extended trio ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APlainInteraction_ReplacesTheSelection_AndIsANoOpOnARowAlreadyAlone()
    {
        var s = New();
        Assert.True(s.Interact("b", ctrl: false, shift: false, Order));
        Assert.Equal(["b"], Sel(s));
        Assert.Equal("b", s.Anchor);

        // Already the whole selection: nothing CHANGES, which is what lets a plain press on a selected row start a drag
        // instead of collapsing the selection under the pointer.
        Assert.False(s.Interact("b", ctrl: false, shift: false, Order));
        Assert.Equal(["b"], Sel(s));

        Assert.True(s.Interact("d", ctrl: false, shift: false, Order));
        Assert.Equal(["d"], Sel(s));
        Assert.Equal("d", s.Anchor);
    }

    [Fact]
    public void CtrlToggles_AndMovesTheAnchorEitherWay()
    {
        var s = New();
        s.Interact("b", false, false, Order);
        Assert.True(s.Interact("d", ctrl: true, shift: false, Order));
        Assert.Equal(["b", "d"], Sel(s));
        Assert.Equal("d", s.Anchor);

        Assert.True(s.Interact("b", ctrl: true, shift: false, Order));   // toggles OFF
        Assert.Equal(["d"], Sel(s));
        Assert.Equal("b", s.Anchor);                                     // Deselect moves it too (SelectionModel does)
    }

    [Fact]
    public void ShiftSelectsTheAnchorRange_InEitherDirection_AndLeavesTheAnchorPut()
    {
        var s = New();
        s.Interact("b", false, false, Order);
        Assert.True(s.Interact("d", ctrl: false, shift: true, Order));
        Assert.Equal(["b", "c", "d"], Sel(s));
        Assert.Equal("b", s.Anchor);

        // A SECOND Shift re-ranges from the SAME anchor rather than walking, and it REPLACES (never accumulates).
        Assert.True(s.Interact("a", ctrl: false, shift: true, Order));
        Assert.Equal(["a", "b"], Sel(s));
        Assert.Equal("b", s.Anchor);
    }

    [Fact]
    public void ShiftWithNoResolvableAnchor_SelectsJustThatRow()
    {
        var s = New();
        // No anchor at all.
        Assert.True(s.Interact("c", ctrl: false, shift: true, Order));
        Assert.Equal(["c"], Sel(s));

        // …and an anchor that has left the tree: refusing here would read as a dead modifier.
        var t = New();
        t.Interact("e", false, false, Order);
        Assert.True(t.Interact("b", ctrl: false, shift: true, ["a", "b", "c"]));
        Assert.Equal(["b"], t.Ordered(["a", "b", "c"]).ToArray());
    }

    [Fact]
    public void ARangeThatChangesNothing_ReportsNoChange()
    {
        var s = New();
        s.Interact("b", false, false, Order);
        Assert.True(s.Interact("d", false, true, Order));
        Assert.False(s.Interact("d", false, true, Order));   // same range, same set
    }

    // ── the check lane ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheLaneIsVisibleAtTwoSelected_OrWheneverCheckModeIsOn()
    {
        var s = New();
        Assert.False(s.CheckLaneVisible);

        s.Toggle("a");
        Assert.False(s.CheckLaneVisible);        // one row is still "the row I clicked"
        s.Toggle("b");
        Assert.True(s.CheckLaneVisible);         // two is a set and needs a visible handle

        var t = New();
        Assert.True(t.SetCheckMode(true));
        Assert.True(t.CheckLaneVisible);         // explicit mode survives an EMPTY selection…
        Assert.Equal(0, t.Count);
        t.Toggle("a");
        Assert.True(t.CheckLaneVisible);         // …which is what lets the first click pick the first row
    }

    [Fact]
    public void ClearLeavesCheckMode_AndEmptiesTheAnchor()
    {
        var s = New();
        s.SetCheckMode(true);
        s.Interact("b", false, false, Order);
        Assert.True(s.Clear());
        Assert.Equal(0, s.Count);
        Assert.Null(s.Anchor);
        Assert.False(s.CheckMode);
        Assert.False(s.CheckLaneVisible);
        Assert.False(s.Clear());                 // idempotent — a second Escape bumps nothing
    }

    // ── prune ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PruneDropsRowsTheTreeNoLongerShows_AndTheAnchorWithThem()
    {
        var s = New();
        s.Interact("b", false, false, Order);
        s.Interact("d", true, false, Order);     // {b, d}, anchor d

        // "c" collapsed away under a folder, and so did "d".
        Assert.True(s.Prune(["a", "b", "e"]));
        Assert.Equal(["b"], s.Ordered(["a", "b", "e"]).ToArray());
        Assert.Null(s.Anchor);

        Assert.False(s.Prune(["a", "b", "e"]));  // nothing left to drop
    }

    [Fact]
    public void PruneAgainstAnEmptyOrderKeepsTheSelection()
    {
        // A transient frame (a pending projection, a section that planned nothing) is not evidence that a row is gone —
        // emptying the selection there would make a library refresh silently cancel a multi-select.
        var s = New();
        s.Interact("b", false, false, Order);
        Assert.False(s.Prune([]));
        Assert.Equal(1, s.Count);
        Assert.False(s.Prune(null));
        Assert.Equal(1, s.Count);
    }

    // ── ordering ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OrderedIsTreeOrder_NotClickOrder()
    {
        var s = New();
        s.Toggle("d");
        s.Toggle("a");
        s.Toggle("c");
        Assert.Equal(["a", "c", "d"], Sel(s));
        // …and an id the visible order does not hold simply drops out, for the same reason Prune drops it.
        Assert.Equal(["a", "c"], s.Ordered(["a", "b", "c"]).ToArray());
        Assert.Empty(s.Ordered([]));
    }

    [Fact]
    public void IdsIsTheSetTheNormalizerTakes()
    {
        var s = New();
        s.Toggle("a");
        s.Toggle("c");
        Assert.True(s.Ids.Contains("a"));
        Assert.False(s.Ids.Contains("b"));
        Assert.Equal(2, s.Ids.Count);
        Assert.True(s.Contains("c"));
        Assert.False(s.Contains(""));
        Assert.False(s.Contains(null));
    }

    [Fact]
    public void AnEmptyIdIsNeverASelection()
    {
        var s = New();
        Assert.False(s.Interact("", false, false, Order));
        Assert.False(s.Toggle(""));
        Assert.Equal(0, s.Count);
    }
}
