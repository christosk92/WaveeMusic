// ── Wavee.Tests/SidebarUndoRingTests.cs — the 50-entry inverse ring, its toasts' guards and one-action-one-entry ────────
//
// docs/plans/wavee/sidebar-rework-implementation.md §P4.3 and §P4.10 (design C.5, Q4, Q6, Q17). Pure: the ring never applies
// an inverse itself, so these facts are about what it records, what it hands back and which toast may act.

using System.Collections.Generic;
using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class SidebarUndoRingTests
{
    static SidebarPin P(string id) => new(id, SidebarEntryKind.AppRoute, "", id, 0);

    static SidebarUndoEntry Leaf(string label)
        => new(0, SidebarUndoKind.Density, SidebarLayoutId.Classic, label, DensityBefore: SidebarDensity.Default, DensityAfter: SidebarDensity.Compact);

    static SidebarUndoEntry PinLeaf(SidebarPinChange change, string id, int at)
        => new(0, SidebarUndoKind.Pin, SidebarLayoutId.Library, id, PinChange: change, Pin: P(id), PinFrom: at);

    [Fact]
    public void PushUndoRedo_RoundTrip()
    {
        var ring = new SidebarUndoRing();
        var x = ring.Push(Leaf("x"));
        var y = ring.Push(Leaf("y"));
        Assert.Equal(2, ring.Count);
        Assert.Same(y, ring.Peek);

        Assert.True(ring.TryUndo(out var undone));
        Assert.Equal(y.Id, undone.Id);
        Assert.True(ring.CanRedo);
        Assert.True(ring.TryRedo(out var redone));
        Assert.Equal(y.Id, redone.Id);
        Assert.Same(y, ring.Peek);
        Assert.True(ring.TryUndo(out var first));
        Assert.Equal(y.Id, first.Id);
        Assert.Same(x, ring.Peek);
        Assert.True(ring.CanRedo);
    }

    [Fact]
    public void Cap50_DropsOldest()
    {
        var ring = new SidebarUndoRing();
        for (int i = 0; i <= SidebarUndoRing.Capacity; i++) ring.Push(Leaf("e" + i));   // 51 pushes
        Assert.Equal(SidebarUndoRing.Capacity, ring.Count);
        string? last = null;
        while (ring.TryUndo(out var entry)) last = entry.Label;
        Assert.Equal("e1", last);                                 // e0, the oldest, was dropped
    }

    [Fact]
    public void Push_ClearsRedo()
    {
        var ring = new SidebarUndoRing();
        ring.Push(Leaf("a"));
        var b = ring.Push(Leaf("b"));
        Assert.True(ring.TryUndo(out _));
        Assert.True(ring.IsRedoTop(b.Id));
        ring.Push(Leaf("c"));
        Assert.False(ring.CanRedo);
        Assert.False(ring.IsRedoTop(b.Id));
    }

    [Fact]
    public void IsTop_OnlyNewest()
    {
        var ring = new SidebarUndoRing();
        var a = ring.Push(Leaf("a"));
        var b = ring.Push(Leaf("b"));
        Assert.False(ring.IsTop(a.Id));
        Assert.True(ring.IsTop(b.Id));
        Assert.False(ring.IsTop(0));                              // no entry carries id 0
    }

    [Fact]
    public void Clear_EmptiesBoth()
    {
        var ring = new SidebarUndoRing();
        ring.Push(Leaf("a"));
        ring.Push(Leaf("b"));
        Assert.True(ring.TryUndo(out _));
        ring.Clear();
        Assert.Equal(0, ring.Count);
        Assert.False(ring.CanUndo);
        Assert.False(ring.CanRedo);
    }

    [Fact]
    public void CollapseNotRecorded()
    {
        Assert.False(SidebarUndoRing.Records(new SetSectionCollapsed(SidebarLayoutId.Classic, "collections", true)));
        Assert.True(SidebarUndoRing.Records(new SetSectionShown(SidebarLayoutId.Classic, "collections", false)));
    }

    [Fact]
    public void KeyAllowed_Matrix()
    {
        Assert.True(SidebarUndoRing.KeyAllowed(editing: true, sidebarToastOpen: false, textEditorFocused: false));
        Assert.True(SidebarUndoRing.KeyAllowed(editing: false, sidebarToastOpen: true, textEditorFocused: false));
        Assert.False(SidebarUndoRing.KeyAllowed(editing: false, sidebarToastOpen: false, textEditorFocused: false));
        Assert.False(SidebarUndoRing.KeyAllowed(editing: true, sidebarToastOpen: false, textEditorFocused: true));
        Assert.False(SidebarUndoRing.KeyAllowed(editing: false, sidebarToastOpen: true, textEditorFocused: true));
    }

    [Fact]
    public void RingToasts_ActOnlyOnTheirEntry_UndidOffersRedo_RedidOffersUndo()
    {
        var ring = new SidebarUndoRing();
        var a = ring.Push(PinLeaf(SidebarPinChange.Unpinned, "a", 0));
        var b = ring.Push(PinLeaf(SidebarPinChange.Unpinned, "b", 1));
        Assert.True(ring.TryUndo(out _));                      // "Undid: b · Redo"
        Assert.True(ring.IsRedoTop(b.Id));
        Assert.False(ring.IsRedoTop(a.Id));
        Assert.True(ring.TryUndo(out _));                      // "Undid: a · Redo" — b's toast is now stale
        Assert.False(ring.IsRedoTop(b.Id));
        Assert.True(ring.TryRedo(out var redone));             // "Redid: a · Undo"
        Assert.Equal(a.Id, redone.Id);
        Assert.True(ring.IsTop(a.Id));
        ring.Push(PinLeaf(SidebarPinChange.Pinned, "c", 0));   // a new edit clears redo
        Assert.False(ring.IsRedoTop(b.Id));
    }

    [Fact]
    public void PinEntries_CarryWhatTheInverseNeeds()
    {
        var ring = new SidebarUndoRing();
        var entry = ring.Push(PinLeaf(SidebarPinChange.Unpinned, "a", 2));
        Assert.Equal(SidebarPinChange.Unpinned, entry.PinChange);
        Assert.Equal("a", entry.Pin!.Id);
        Assert.Equal(2, entry.PinFrom);
    }

    [Fact]
    public void PinWhilePinnedHidden_IsOneEntry_TheToastsUndoHitsIt()
    {
        var ring = new SidebarUndoRing();
        var pin = PinLeaf(SidebarPinChange.Pinned, "search", 0);
        var show = new SidebarUndoEntry(0, SidebarUndoKind.Layout, SidebarLayoutId.Library, "show",
            Before: SidebarLayoutState.Default.Of(SidebarLayoutId.Library), After: SidebarLayoutState.Default.Of(SidebarLayoutId.Library));
        var batch = ring.Push(SidebarUndoEntry.Batch(SidebarLayoutId.Library, "Pin Search", [pin, show]));
        Assert.Equal(1, ring.Count);
        Assert.True(ring.IsTop(batch.Id));                     // the "Pinned is shown again · Undo" toast's check
        var undoLeaves = new List<SidebarUndoEntry>();
        SidebarUndoEntry.Flatten(batch, undo: true, undoLeaves);
        Assert.Equal(new[] { SidebarUndoKind.Layout, SidebarUndoKind.Pin }, undoLeaves.Select(e => e.Kind));   // re-hide, then unpin
        var redoLeaves = new List<SidebarUndoEntry>();
        SidebarUndoEntry.Flatten(batch, undo: false, redoLeaves);
        Assert.Equal(new[] { SidebarUndoKind.Pin, SidebarUndoKind.Layout }, redoLeaves.Select(e => e.Kind));
    }

    [Fact]
    public void ResetEverything_IsOneEntry_UndoRevertsAllThree()
    {
        var ring = new SidebarUndoRing();
        var classic = SidebarLayoutState.Default.Of(SidebarLayoutId.Classic);
        var library = SidebarLayoutState.Default.Of(SidebarLayoutId.Library);
        var batch = ring.Push(SidebarUndoEntry.Batch(SidebarLayoutId.Classic, "Reset everything",
        [
            new SidebarUndoEntry(0, SidebarUndoKind.Layout, SidebarLayoutId.Classic, "", Before: classic, After: classic),
            new SidebarUndoEntry(0, SidebarUndoKind.Layout, SidebarLayoutId.Library, "", Before: library, After: library),
            new SidebarUndoEntry(0, SidebarUndoKind.Density, SidebarLayoutId.Classic, "", DensityBefore: SidebarDensity.Compact),
        ]));
        Assert.True(ring.TryUndo(out var undone));
        Assert.Same(batch, undone);
        var leaves = new List<SidebarUndoEntry>();
        SidebarUndoEntry.Flatten(undone, undo: true, leaves);
        Assert.Equal(3, leaves.Count);
        Assert.Equal(SidebarUndoKind.Density, leaves[0].Kind);   // the last applied change is reverted first
        Assert.False(ring.CanUndo);
    }

    [Fact]
    public void UnpinAllShortcuts_IsOneEntry_UndoReinsertsEveryPinInPlace()
    {
        // Pins [Search, x, Radio]; the batch unpinned from the last index down: Radio@2, then Search@0.
        var batch = SidebarUndoEntry.Batch(SidebarLayoutId.Library, "Unpin all shortcuts",
            [PinLeaf(SidebarPinChange.Unpinned, "radio", 2), PinLeaf(SidebarPinChange.Unpinned, "search", 0)]);
        var store = new List<string> { "x" };                   // what is left after the batch ran
        var leaves = new List<SidebarUndoEntry>();
        SidebarUndoEntry.Flatten(batch, undo: true, leaves);
        foreach (var leaf in leaves) store.Insert(leaf.PinFrom, leaf.Pin!.Id);   // the service's Pins.Insert
        Assert.Equal(new[] { "search", "x", "radio" }, store);
    }
}
