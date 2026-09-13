using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// Phase C's two remaining pure decisions: V3's SIBLING-RUN CLAMP (D11) and the collapsed RAIL's transparency rule
/// (D16).
///
/// <para><b>The clamp.</b> V3's custom order is a LOCAL view overlay, so it cannot move an item between folders — that
/// is a rootlist write, and the rootlist is written only through the resource-drop seam. The landed behaviour was a
/// drag that animated all the way across a folder boundary and then silently did not commit
/// (<c>LibraryV3Sidebar.CommitPaneReorder</c> bailing at the very end). Clamping the REQUESTED slot during the gesture
/// means the gap never opens where the drop cannot land: what the user sees is what commits, and the commit-time bail
/// becomes an invariant instead of the only feedback.</para>
///
/// <para><b>The rail.</b> A 56-DIP strip of covers is a corridor as much as a set of destinations, so a folder crossing
/// it on its way to a folder tile must not be accused of having "Nothing to add".</para>
/// </summary>
public class SidebarDragClampTests
{
    // ── fixture: two top-level playlists with an EXPANDED folder between them ────────────────────────────────────────
    //
    // Built rows (group: true):  0 a │ 1 outer │ 2 mid1 │ 3 mid2 │ 4 b
    // Parents:                   ""    ""        outer    outer     ""
    //
    // The shape is the whole point: a sibling "run" is a SET, not a contiguous span — an expanded folder's children sit
    // between two top-level siblings — so a top-level drag has to travel PAST them while a child drag stays boxed in.

    static SidebarLibraryEntry Playlist(string slug, string folderId = "", int order = 0, int depth = 0)
        => new(Id: "pl:spotify:playlist:" + slug, Kind: SidebarEntryKind.Playlist,
               Uri: "spotify:playlist:" + slug, Name: slug, Creator: "Owner", Cover: null, MosaicTiles: null,
               ChildCount: 0, AddedAtMs: 0, SortStamp: 1, LastVisitedTicksUtc: 0, SourceOrder: order, Depth: depth,
               Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { FolderId = folderId, FolderName = folderId, FirstArtistName = "" };

    static SidebarLibraryEntry Folder(string id, int order = 0, int depth = 0)
        => new(Id: "folder:" + id, Kind: SidebarEntryKind.Folder, Uri: "", Name: id, Creator: "", Cover: null,
               MosaicTiles: null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
               SourceOrder: order, Depth: depth, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { FolderId = id, FolderName = id, FirstArtistName = "" };

    /// <summary>The binder's flattened tree slice — the only place a FOLDER's parent is recoverable.</summary>
    static SidebarLibraryEntry[] Tree() =>
    [
        Folder("outer", order: 0, depth: 0),
        Playlist("mid1", folderId: "outer", order: 1, depth: 1),
        Playlist("mid2", folderId: "outer", order: 2, depth: 1),
    ];

    static LibraryV3View View()
    {
        var published = new[]
        {
            Playlist("a"), Folder("outer"), Playlist("mid1", folderId: "outer"),
            Playlist("mid2", folderId: "outer"), Playlist("b"),
        };
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: true);
        return view;
    }

    [Fact]
    public void TheFixtureIsTheShapeTheseRulesAreAbout()
    {
        var view = View();
        Assert.Equal(5, view.Count);
        Assert.Equal(new[] { "a", "outer", "mid1", "mid2", "b" },
                     new[] { view.Rows[0].Name, view.Rows[1].Name, view.Rows[2].Name, view.Rows[3].Name, view.Rows[4].Name });
        Assert.Equal(new[] { "", "", "outer", "outer", "" },
                     new[] { view.ParentOf(0), view.ParentOf(1), view.ParentOf(2), view.ParentOf(3), view.ParentOf(4) });
    }

    // ── within the run: untouched ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASlotInsideTheSourcesOwnRun_PassesThrough()
    {
        var view = View();
        // Top level → top level (the folder ROW is a top-level sibling, so landing on it is a legal reorder).
        Assert.Equal(1, view.ClampToSiblingRun(from: 0, to: 1));
        Assert.Equal(4, view.ClampToSiblingRun(from: 0, to: 4));
        // Child → its own sibling.
        Assert.Equal(3, view.ClampToSiblingRun(from: 2, to: 3));
        Assert.Equal(2, view.ClampToSiblingRun(from: 3, to: 2));
        // A no-move is a no-move.
        Assert.Equal(0, view.ClampToSiblingRun(from: 0, to: 0));
    }

    // ── across a boundary: snapped to the run's edge ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ATopLevelDragOverAFoldersChildren_SnapsToTheNearestTopLevelSlot()
    {
        var view = View();
        // Slot 2/3 are INSIDE the folder. The gap must not open there — it snaps to whichever top-level slot is nearer,
        // which reads as the dragged row travelling past the expanded folder rather than into it.
        Assert.Equal(1, view.ClampToSiblingRun(from: 0, to: 2));   // 1 is one away, 4 is two
        Assert.Equal(4, view.ClampToSiblingRun(from: 0, to: 3));   // 4 is one away, 1 is two
    }

    [Fact]
    public void AChildDrag_NeverLeavesItsFolder()
    {
        var view = View();
        // There is no legal slot outside the run at all, so every request collapses onto the run's edges.
        Assert.Equal(2, view.ClampToSiblingRun(from: 2, to: 0));
        Assert.Equal(2, view.ClampToSiblingRun(from: 2, to: 1));
        Assert.Equal(3, view.ClampToSiblingRun(from: 2, to: 4));
        Assert.Equal(3, view.ClampToSiblingRun(from: 3, to: 4));
    }

    [Fact]
    public void EveryClampedSlot_IsOneTheCommitWouldAccept()
    {
        // THE INVARIANT the commit-time `SameParent` bail is now the belt to: for every (from, to) pair in the view,
        // the clamped destination is a same-parent slot. If this ever fails, the gesture can again animate to a slot
        // that silently does not commit.
        var view = View();
        for (int from = 0; from < view.Count; from++)
            for (int to = 0; to < view.Count; to++)
                Assert.True(view.SameParent(from, view.ClampToSiblingRun(from, to)),
                            $"clamp({from}, {to}) escaped the sibling run");
    }

    [Fact]
    public void AnOutOfRangeRequest_IsClampedIntoTheList()
    {
        var view = View();
        // Degenerate geometry (a dead viewport, a stale band count) must never produce a slot outside the view.
        Assert.Equal(0, view.ClampToSiblingRun(from: 0, to: -5));
        Assert.Equal(4, view.ClampToSiblingRun(from: 0, to: 99));
        Assert.Equal(3, view.ClampToSiblingRun(from: 2, to: 99));
    }

    [Fact]
    public void AFlatView_ClampsNothing()
    {
        // No folders, no boundaries: a search result or a grid lens must behave exactly as it did before the clamp.
        var view = new LibraryV3View();
        view.Build([Playlist("a"), Playlist("b"), Playlist("c")], 0, Tree(), 1, null, group: false);
        for (int to = 0; to < view.Count; to++) Assert.Equal(to, view.ClampToSiblingRun(from: 0, to: to));
    }

    // ── the gap the clamped destination draws ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheClampedGap_MovesOnlyTheRowsBetweenSourceAndDestination()
    {
        const float h = 40f;
        // Downward: the rows the lifted one passes close up behind it.
        Assert.Equal(0f, SidebarReorderClamp.Offset(slot: 0, from: 0, to: 3, h));   // the lifted row itself
        Assert.Equal(-h, SidebarReorderClamp.Offset(slot: 1, from: 0, to: 3, h));
        Assert.Equal(-h, SidebarReorderClamp.Offset(slot: 3, from: 0, to: 3, h));
        Assert.Equal(0f, SidebarReorderClamp.Offset(slot: 4, from: 0, to: 3, h));   // past the destination
        // Upward: they part to make room.
        Assert.Equal(h, SidebarReorderClamp.Offset(slot: 1, from: 3, to: 1, h));
        Assert.Equal(h, SidebarReorderClamp.Offset(slot: 2, from: 3, to: 1, h));
        Assert.Equal(0f, SidebarReorderClamp.Offset(slot: 0, from: 3, to: 1, h));
        // A no-move draws no gap.
        Assert.Equal(0f, SidebarReorderClamp.Offset(slot: 2, from: 2, to: 2, h));
    }

    // ── the rail (D16) ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFolderCrossingTheRail_IsTransparent_NotRefused()
    {
        // A folder carries no tracks: it has nothing to add to a playlist tile and cannot be filed into one, and it is
        // on its way to a folder tile or to the peeked pane. "Nothing to add" was an accusation aimed at a pass-through.
        Assert.True(SidebarRailDropRules.TileTransparent(payloadIsRootlistItem: true, payloadCanCopyTracks: false));
    }

    [Fact]
    public void EveryOtherPayloadKeepsItsAnswer()
    {
        // A playlist being re-filed still carries tracks, so a rail playlist tile is a real deposit destination for it.
        Assert.False(SidebarRailDropRules.TileTransparent(payloadIsRootlistItem: true, payloadCanCopyTracks: true));
        // A track set aimed at a playlist tile: accepted, or refused with a reason — never silent.
        Assert.False(SidebarRailDropRules.TileTransparent(payloadIsRootlistItem: false, payloadCanCopyTracks: true));
        // An artist/route payload over a rail tile is handled by the row spec's own transparency arm, not this one.
        Assert.False(SidebarRailDropRules.TileTransparent(payloadIsRootlistItem: false, payloadCanCopyTracks: false));
    }
}
