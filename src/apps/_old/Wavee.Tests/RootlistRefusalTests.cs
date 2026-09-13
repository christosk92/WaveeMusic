using System.Collections.Generic;
using Wavee;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE REFUSAL TABLE. Every one of these used to be a silent <c>false</c>: <c>RootlistOps.TryBuildMove</c> rejected the
/// cycle and the no-op three layers below the pointer, and the drag showed "Move into B" and then simply did nothing
/// (D8/D2). They are decided here now — WHERE THE CUE IS DRAWN — so each one can carry its own sentence.
///
/// <para><b>The one structural invariant:</b> a refused slot always reports <see cref="SidebarDropKind.None"/>. That is
/// what makes the cue's rule total: a refusal draws neither a line nor a plate, so there is no state in which the
/// surface promises a drop it will not perform.</para>
/// </summary>
public class RootlistRefusalTests
{
    static SidebarRowFacts Row(bool folder = false, bool self = false, bool sorted = false, bool loaded = true) => new(
        IsFolder: folder, FolderExpanded: false, FolderHasChildren: false,
        Depth: 0, NextVisibleDepth: 0, CenterAccepts: folder,
        SourceIsSelf: self, SortedNonCustom: sorted, RootlistLoaded: loaded);

    static SidebarDropSlot At(float t, in SidebarRowFacts f)
        => RootlistSlotResolver.Resolve(2, t, 200f, 44f, in f, SidebarDropSlot.None);

    [Fact]
    public void Self_RefusesTheRowTheDragCameFrom()
    {
        var slot = At(0.1f, Row(self: true));
        Assert.Equal(SidebarDropRefusal.Self, slot.Refusal);
        Assert.Equal(SidebarDropKind.None, slot.Kind);
    }

    [Fact]
    public void IntoItself_IsItsOwnSentence_ForAFoldersCentre()
    {
        // "Can't move a folder into itself" names what the user tried; the generic "can't move here" would leave them
        // guessing which rule they hit.
        Assert.Equal(SidebarDropRefusal.IntoItself, At(0.5f, Row(folder: true, self: true)).Refusal);
        // The same folder's EDGES are the ordinary self refusal — the user is aiming at an ordering, not at the folder.
        Assert.Equal(SidebarDropRefusal.Self, At(0.02f, Row(folder: true, self: true)).Refusal);
    }

    [Fact]
    public void SortedList_RefusesOrderingsOnly_IntoStaysLegal()
    {
        // D10: a non-custom sort cannot SHOW a positional insert, so an ordering is refused with the existing fix
        // ("clear sorting to reorder") — but a deposit into a folder or a playlist needs no position at all.
        Assert.Equal(SidebarDropRefusal.SortedList, At(0.1f, Row(sorted: true)).Refusal);
        Assert.Equal(SidebarDropRefusal.SortedList, At(0.9f, Row(sorted: true)).Refusal);
        var into = At(0.5f, Row(folder: true, sorted: true));
        Assert.Equal(SidebarDropRefusal.None, into.Refusal);
        Assert.Equal(SidebarDropKind.Into, into.Kind);

        var end = RootlistSlotResolver.Resolve(2, 0.5f, 200f, 24f,
            Row(sorted: true) with { IsListEnd = true }, SidebarDropSlot.None);
        Assert.Equal(SidebarDropRefusal.SortedList, end.Refusal);
    }

    [Fact]
    public void NotLoaded_RefusesEverything_BeforeAnyOtherRule()
    {
        // "Not known to be loaded" must never present as "is": a filing written against an empty tree lands at an index
        // that means nothing. It is checked FIRST, so it is the reason the user sees even when another would also apply.
        Assert.Equal(SidebarDropRefusal.NotLoaded, At(0.5f, Row(folder: true, self: true, loaded: false)).Refusal);
        Assert.Equal(SidebarDropRefusal.NotLoaded, At(0.1f, Row(loaded: false)).Refusal);
    }

    [Fact]
    public void Unavailable_IsTheDegenerateGeometryRefusal()
    {
        Assert.Equal(SidebarDropRefusal.Unavailable,
            RootlistSlotResolver.Resolve(-1, 0.5f, 200f, 44f, Row(), SidebarDropSlot.None).Refusal);
    }

    // ── NoOp / cycle / self, decided against THE MARKER STREAM ──────────────────
    //
    // These used to be answered by `RootlistTreeMoves.Check` over a flattened entry list with NO end-group rows — a
    // second copy of the index math whose "Inside" landed at the folder's END and therefore called a perfectly legal
    // filing a no-op (F1). The copy is deleted: every one of them is now `RootlistOps.CheckMove` over the same marker
    // stream the write indexes into, mapped by the ONE `RootlistDropDecision.RefusalFor` table.

    static SidebarDropRefusal Check(string sourceId, string targetId, RootlistDropPlacement placement)
    {
        var tree = SidebarTreeFixture.Tree();
        return RootlistDropDecision.RefusalFor(RootlistDropDecision.Check(
            SidebarTreeFixture.Markers(), SidebarTreeFixture.Ref(tree, sourceId),
            SidebarTreeFixture.Ref(tree, targetId), placement));
    }

    static string A => SidebarTreeFixture.Pl("a");
    static string B => SidebarTreeFixture.Pl("b");
    static string C => SidebarTreeFixture.Pl("c");
    static string D => SidebarTreeFixture.Pl("d");
    static string G => SidebarTreeFixture.Fo("g");

    [Fact]
    public void NoOp_RefusesBothEdgesOfTheSpanTheItemAlreadyOccupies()
    {
        // "Before the row right after me" and "after the row right before me" are the same place I am already in — the
        // two gestures a reorder produces most often, and the two that used to fail silently.
        Assert.Equal(SidebarDropRefusal.NoOp, Check(B, C, RootlistDropPlacement.Before));
        Assert.Equal(SidebarDropRefusal.NoOp, Check(C, B, RootlistDropPlacement.After));
        // Onto itself, either way round: the stream calls that SameItem, and the table says "this row IS the drag".
        Assert.Equal(SidebarDropRefusal.Self, Check(B, B, RootlistDropPlacement.Before));
        Assert.Equal(SidebarDropRefusal.Self, Check(B, B, RootlistDropPlacement.After));
        // The folder's LAST child, appended back into that same folder.
        Assert.Equal(SidebarDropRefusal.NoOp, Check(SidebarTreeFixture.Fo("k"), G, RootlistDropPlacement.Inside));
    }

    [Fact]
    public void RealMoves_AreNotRefused()
    {
        Assert.Equal(SidebarDropRefusal.None, Check(A, C, RootlistDropPlacement.After));
        Assert.Equal(SidebarDropRefusal.None, Check(B, G, RootlistDropPlacement.After));
        Assert.Equal(SidebarDropRefusal.None, Check(A, G, RootlistDropPlacement.Inside));
        Assert.Equal(SidebarDropRefusal.None, Check(D, A, RootlistDropPlacement.Before));
        // F1 — THE FLYOUT'S "ALREADY THERE". Filing a NON-last child into its own folder is a real move (it becomes
        // the last one). The deleted flattened check mapped Inside to the folder's end index and refused it as a no-op,
        // which is exactly the bug the user hit dropping a playlist onto a folder that already held it.
        Assert.Equal(SidebarDropRefusal.None, Check(B, G, RootlistDropPlacement.Inside));
    }

    [Fact]
    public void Cycle_RefusesAFolderFiledIntoItsOwnSubtree()
    {
        Assert.Equal(SidebarDropRefusal.IntoDescendant, Check(G, B, RootlistDropPlacement.Before));
        Assert.Equal(SidebarDropRefusal.IntoDescendant, Check(G, B, RootlistDropPlacement.After));
        Assert.Equal(SidebarDropRefusal.IntoDescendant,
                     Check(G, SidebarTreeFixture.Fo("k"), RootlistDropPlacement.Inside));
        // Into the dragged folder ITSELF is the identity answer; the RESOLVER turns that into its own sentence
        // ("can't move a folder into itself") from the row facts — see IntoItself above.
        Assert.Equal(SidebarDropRefusal.Self, Check(G, G, RootlistDropPlacement.Inside));
    }

    [Fact]
    public void AnUnknownRowOrAMissingStream_IsRefusedRatherThanArmed()
    {
        // The OPPOSITE of the old flattened rule, deliberately. "The tree may not be showing it, so allow it" was a
        // guess: a destination we cannot find in the marker stream has no index, and arming a slot for it is what let a
        // drop silently do nothing. Unavailable is the honest answer, and it carries a sentence.
        Assert.Equal(SidebarDropRefusal.Unavailable, Check("pl:missing", A, RootlistDropPlacement.After));
        Assert.Equal(SidebarDropRefusal.Unavailable, Check(A, "pl:missing", RootlistDropPlacement.After));
        Assert.Equal(SidebarDropRefusal.Unavailable, RootlistDropDecision.RefusalFor(RootlistDropDecision.Check(
            null, new RootlistItemRef("spotify:playlist:a", false), new RootlistItemRef("spotify:playlist:d", false),
            RootlistDropPlacement.After)));
    }

    [Fact]
    public void TheTableIsTotal_AndMapsEveryCheckToExactlyOneRefusal()
    {
        // Every value of the ONE table, so a new RootlistMoveCheck cannot land silently in the default arm without
        // this failing first.
        Assert.Equal(SidebarDropRefusal.None, RootlistDropDecision.RefusalFor(RootlistMoveCheck.Ok));
        Assert.Equal(SidebarDropRefusal.NoOp, RootlistDropDecision.RefusalFor(RootlistMoveCheck.NoOp));
        Assert.Equal(SidebarDropRefusal.IntoDescendant, RootlistDropDecision.RefusalFor(RootlistMoveCheck.Cycle));
        Assert.Equal(SidebarDropRefusal.Self, RootlistDropDecision.RefusalFor(RootlistMoveCheck.SameItem));
        Assert.Equal(SidebarDropRefusal.Unavailable, RootlistDropDecision.RefusalFor(RootlistMoveCheck.Missing));
        Assert.Equal(SidebarDropRefusal.Unavailable, RootlistDropDecision.RefusalFor(RootlistMoveCheck.Invalid));
    }

    [Fact]
    public void AFolderMovesItsWholeSubtree_MarkersAndAll()
    {
        // What the deleted `TryRange` used to assert over the flattened list, asked of the stream that actually gets
        // written: Chill spans its start marker, b, c, Deep's whole pair and its own end marker.
        var markers = SidebarTreeFixture.Markers();
        Assert.True(RootlistOps.TryBuildMove(markers, new RootlistItemRef("g", IsFolder: true),
                                             new RootlistItemRef("spotify:playlist:d", false),
                                             RootlistDropPlacement.After, out var op, out var reason));
        Assert.Equal(RootlistMoveCheck.Ok, reason);
        Assert.Equal(1, op!.FromIndex);      // the start-group row
        Assert.Equal(7, op!.Length);         // start . b . c . [Deep start . f . Deep end] . end
    }
}
