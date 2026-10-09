// ── Wavee.Tests/SidebarLibraryShaperTests.cs — the list shaper's re-grouping, drill slice, sibling clamp and materialized order (Shell/Sidebar.Library.cs) ──
//
// Pure: no window, no loop, no element.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class SidebarLibraryShaperTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // `Cover: StringId.Empty` — not `Cover: null` as 0.2.9's fixtures wrote it: SidebarLibraryEntry.Cover is a
    // non-nullable `StringId` in 0.3 (Shell/Sidebar.cs), and StringId has no implicit conversion from a null literal.

    static SidebarLibraryEntry Playlist(string slug, string folderId = "", int order = 0, int depth = 0)
        => new(Id: "pl:spotify:playlist:" + slug, Kind: SidebarEntryKind.Playlist,
               Uri: "spotify:playlist:" + slug, Name: slug, Creator: "Owner", Cover: StringId.Empty, MosaicTiles: null,
               ChildCount: 0, AddedAtMs: 0, SortStamp: 1, LastVisitedTicksUtc: 0, SourceOrder: order, Depth: depth,
               Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { FolderId = folderId, FolderName = folderId, FirstArtistName = "" };

    static SidebarLibraryEntry Folder(string id, int order = 0, int depth = 0)
        => new(Id: "folder:" + id, Kind: SidebarEntryKind.Folder, Uri: "", Name: id, Creator: "", Cover: StringId.Empty,
               MosaicTiles: null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
               SourceOrder: order, Depth: depth, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { FolderId = id, FolderName = id, FirstArtistName = "" };

    static SidebarLibraryEntry Album(string slug, int order = 0)
        => new(Id: "album:spotify:album:" + slug, Kind: SidebarEntryKind.Album, Uri: "spotify:album:" + slug,
               Name: slug, Creator: "Artist", Cover: StringId.Empty, MosaicTiles: null, ChildCount: 0, AddedAtMs: 0,
               SortStamp: 2, LastVisitedTicksUtc: 0, SourceOrder: order, Depth: 0, Circular: false,
               Flavor: SidebarPlaylistFlavor.None)
        { FolderId = "", FolderName = "", FirstArtistName = "" };

    /// <summary>The binder's fully flattened tree slice — folders included at every depth, which is the ONLY place a
    /// folder's PARENT is recoverable (a folder row's own FolderId is itself).</summary>
    static SidebarLibraryEntry[] Tree() =>
    [
        Folder("outer", order: 0, depth: 0),
        Folder("inner", order: 1, depth: 1),
        Playlist("deep", folderId: "inner", order: 2, depth: 2),
        Playlist("mid", folderId: "outer", order: 3, depth: 1),
        Playlist("top", order: 4),
    ];

    static string[] NamesOf(SidebarLibraryShaper view)
    {
        var names = new string[view.Count];
        for (int i = 0; i < names.Length; i++) names[i] = view.Rows[i].Name;
        return names;
    }

    static int[] DepthsOf(SidebarLibraryShaper view)
    {
        var depths = new int[view.Count];
        for (int i = 0; i < depths.Length; i++) depths[i] = view.Rows[i].Depth;
        return depths;
    }

    // ── grouping ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRootLevel_ReGroupsChildrenUnderTheirFolder()
    {
        // The published order is what a FLAT sort produces: the nested playlists sort above the folders that contain
        // them.
        var published = new[]
        {
            Playlist("deep", folderId: "inner"),
            Playlist("mid", folderId: "outer"),
            Folder("outer"),
            Folder("inner"),
            Playlist("top"),
        };

        var view = new SidebarLibraryShaper();
        view.Build(published, 0, Tree(), 1, null, group: true);

        Assert.Equal(new[] { "outer", "mid", "inner", "deep", "top" }, NamesOf(view));
        Assert.Equal(new[] { 0, 1, 1, 2, 0 }, DepthsOf(view));
    }

    [Fact]
    public void Grouping_PreservesTheSiblingOrderTheProjectionPublished()
    {
        // Siblings keep their published (sorted) order — the re-grouping only moves children UNDER their parent, it
        // never re-sorts a level.
        var published = new[] { Folder("outer"), Playlist("b", folderId: "outer"), Playlist("a", folderId: "outer") };
        var view = new SidebarLibraryShaper();
        view.Build(published, 0, Tree(), 1, null, group: true);
        Assert.Equal(new[] { "outer", "b", "a" }, NamesOf(view));
    }

    [Fact]
    public void ARowWhoseFolderIsNotVisible_IsPromotedToTopLevel()
    {
        // Nothing is ever hidden because its container happens to be elsewhere (pinned into the band, dropped by the
        // lens, or a cold tree) — that would silently lose playlists.
        var published = new[] { Playlist("orphan", folderId: "outer"), Playlist("top") };
        var view = new SidebarLibraryShaper();
        view.Build(published, 0, Tree(), 1, null, group: true);
        Assert.Equal(new[] { "orphan", "top" }, NamesOf(view));
        Assert.Equal(new[] { 0, 0 }, DepthsOf(view));
    }

    [Fact]
    public void TheLeadingPinBand_IsSkipped_WhenItIsRenderedAsItsOwnSection()
    {
        var published = new[] { Playlist("pinned"), Folder("outer"), Playlist("mid", folderId: "outer") };
        var view = new SidebarLibraryShaper();
        view.Build(published, 1, Tree(), 1, null, group: true);
        Assert.Equal(new[] { "outer", "mid" }, NamesOf(view));
    }

    [Fact]
    public void FlatMode_PassesTheSliceThrough_AtDepthZero()
    {
        // A search has already flattened the projection and a grid cannot express disclosure: both want the
        // published order verbatim, with no indent inherited from the tree the entries came out of.
        var published = new[] { Playlist("deep", folderId: "inner", depth: 2), Album("one"), Playlist("top") };
        var view = new SidebarLibraryShaper();
        view.Build(published, 0, Tree(), 1, null, group: false);
        Assert.Equal(new[] { "deep", "one", "top" }, NamesOf(view));
        Assert.Equal(new[] { 0, 0, 0 }, DepthsOf(view));
    }

    // ── the drill level ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADrillLevel_IsExactlyOneFoldersDirectChildren_AtDepthZero()
    {
        var published = new[]
        {
            Folder("outer"), Folder("inner"), Playlist("mid", folderId: "outer"),
            Playlist("deep", folderId: "inner"), Playlist("top"),
        };

        var view = new SidebarLibraryShaper();
        view.Build(published, 0, Tree(), 1, "outer", group: true);

        // "inner" is a child folder of "outer" and stays a row (it can be drilled into again); "deep" belongs to
        // inner and does NOT leak into this level.
        Assert.Equal(new[] { "inner", "mid" }, NamesOf(view));
        Assert.Equal(new[] { 0, 0 }, DepthsOf(view));
        Assert.False(view.DrillTargetMissing);
    }

    [Fact]
    public void ADrillLevel_IgnoresTheSkip_BecauseThereIsNoPinBandInside()
    {
        // A pinned playlist that lives inside the folder must still appear inside it.
        var published = new[] { Playlist("mid", folderId: "outer"), Folder("outer"), Playlist("top") };
        var view = new SidebarLibraryShaper();
        view.Build(published, 1, Tree(), 1, "outer", group: true);
        Assert.Equal(new[] { "mid" }, NamesOf(view));
    }

    [Fact]
    public void AnEmptyFolder_IsALegitimateLevel_NotAMissingTarget()
    {
        // Popping out of an empty folder would make an empty folder impossible to open.
        var published = new[] { Folder("outer"), Playlist("top") };
        var view = new SidebarLibraryShaper();
        view.Build(published, 0, Tree(), 1, "outer", group: true);
        Assert.Equal(0, view.Count);
        Assert.False(view.DrillTargetMissing);
    }

    [Fact]
    public void ADrilledFolderThatVanished_IsReportedMissing()
    {
        var published = new[] { Playlist("top") };
        var view = new SidebarLibraryShaper();
        view.Build(published, 0, Tree(), 1, "outer", group: true);
        Assert.True(view.DrillTargetMissing);
        Assert.Equal(0, view.Count);
    }

    [Fact]
    public void AColdProjection_IsNotAMissingDrillTarget()
    {
        var view = new SidebarLibraryShaper();
        view.Build(Array.Empty<SidebarLibraryEntry>(), 0, Tree(), 1, "outer", group: true);
        Assert.False(view.DrillTargetMissing);
        Assert.Equal(0, view.Count);
    }

    // ── the two facts the custom-order commit rests on ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SourceOrder_IsRewrittenToThePosition_SoAReSortIsANoOp()
    {
        // The planner's EntityList path re-sorts, and its CustomOrder comparator (with no rank map) is SourceOrder
        // ascending — so stamping the position here is what makes the grid views reproduce this exact order.
        var published = new[] { Playlist("a", order: 40), Playlist("b", order: 10), Playlist("c", order: 25) };
        var view = new SidebarLibraryShaper();
        view.Build(published, 0, Tree(), 1, null, group: false);
        Assert.Equal(new[] { 0, 1, 2 }, new[] { view.Rows[0].SourceOrder, view.Rows[1].SourceOrder, view.Rows[2].SourceOrder });
    }

    [Fact]
    public void SameParent_ClampsADragAcrossAFolderBoundary()
    {
        var published = new[] { Folder("outer"), Playlist("mid", folderId: "outer"), Playlist("top") };
        var view = new SidebarLibraryShaper();
        view.Build(published, 0, Tree(), 1, null, group: true);

        // 0 = the folder row (top level), 1 = its child, 2 = a top-level playlist.
        Assert.Equal("", view.ParentOf(0));
        Assert.Equal("outer", view.ParentOf(1));
        Assert.True(view.SameParent(0, 2));
        Assert.False(view.SameParent(1, 2));
    }

    [Fact]
    public void MaterializeOrder_WritesTheWholeVisibleOrder_WithTheRowMoved()
    {
        var published = new[] { Playlist("a"), Playlist("b"), Playlist("c") };
        var view = new SidebarLibraryShaper();
        view.Build(published, 0, Tree(), 1, null, group: false);

        var into = new List<string>();
        view.MaterializeOrder(into, 0, 2);
        Assert.Equal(new[] { view.KeyAt(1), view.KeyAt(2), view.KeyAt(0) }, into);

        view.MaterializeOrder(into, 2, 0);
        Assert.Equal(new[] { view.KeyAt(2), view.KeyAt(0), view.KeyAt(1) }, into);

        view.MaterializeOrder(into, 1, 1);
        Assert.Equal(new[] { view.KeyAt(0), view.KeyAt(1), view.KeyAt(2) }, into);
    }

    [Fact]
    public void MaterializeOrder_SkipsRowsThatArePartOfNoPlaylistOrder()
    {
        // An authored route row (Liked Songs, a pinned route) has no place in a playlist order.
        var published = new[] { SidebarLibraryEntry.ForRoute("liked", "Liked Songs"), Playlist("a"), Playlist("b") };
        var view = new SidebarLibraryShaper();
        view.Build(published, 0, Tree(), 1, null, group: false);

        var into = new List<string>();
        view.MaterializeOrder(into, 1, 2);
        Assert.Equal(new[] { view.KeyAt(2), view.KeyAt(1) }, into);
    }

    [Fact]
    public void ARebuild_ReusesItsBuffers_AndNeverLeaksTheOldOrder()
    {
        var view = new SidebarLibraryShaper();
        view.Build(new[] { Folder("outer"), Playlist("mid", folderId: "outer") }, 0, Tree(), 1, null, group: true);
        Assert.Equal(2, view.Count);

        view.Build(new[] { Playlist("top") }, 0, Tree(), 1, null, group: true);
        Assert.Equal(new[] { "top" }, NamesOf(view));

        view.Build(Array.Empty<SidebarLibraryEntry>(), 0, Tree(), 1, null, group: true);
        Assert.Equal(0, view.Count);
    }
}
