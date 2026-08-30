using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

// R3.0.3 — LIBRARY V3 AS A SYNTHESIZED DOCUMENT.
//
// V3's content used to be its own pane container (a private index map + list + row + rail), so what its filter/sort/view
// state MEANT lived in engine-bound code no test could reach. It is now two pure halves — a document (what to render) and an
// order (in which sequence) — and both are pinned here, because the mapping IS the behaviour: get `KindFor` wrong and
// folders lose their indent; get `QueryFor` wrong and the 56-DIP rail sorts differently from the pane it collapsed from.
public sealed class LibraryV3DocumentTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static LibraryV3DocState State(
        SidebarV3Filter filter = SidebarV3Filter.All,
        SidebarV3View view = SidebarV3View.List,
        SidebarV3Sort sort = SidebarV3Sort.Recents,
        bool descending = false,
        SidebarV3Qualifier qualifier = SidebarV3Qualifier.Any,
        bool qualifiersAvailable = false,
        bool searching = false,
        string? drill = null,
        bool hasPins = false,
        bool likedPinned = false,
        int columns = 2)
        => new((int)filter, (int)qualifier, (int)sort, descending, (int)view, columns, searching, drill,
               hasPins, likedPinned, qualifiersAvailable);

    static SidebarSectionSpec? Find(SidebarCustomLayout doc, string id) => doc.Find(id);

    static SidebarSectionSpec Library(SidebarCustomLayout doc)
    {
        var s = doc.Find(LibraryV3Document.LibraryId);
        Assert.NotNull(s);
        return s!;
    }

    static string[] IdsOf(SidebarCustomLayout doc)
    {
        var ids = new string[doc.Sections.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = doc.Sections[i].Id;
        return ids;
    }

    // ── section kind per lens ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlaylistLenses_InAListView_RenderAsATree()
    {
        // PlaylistTree is the ONLY planner path that stamps a row's nesting depth and emits the given order verbatim, so
        // the two lenses that can contain folders must use it (§3.2.7).
        foreach (var filter in new[] { SidebarV3Filter.All, SidebarV3Filter.Playlists })
            foreach (var view in new[] { SidebarV3View.CompactList, SidebarV3View.List })
            {
                var state = State(filter, view);
                Assert.True(LibraryV3Document.FoldersApply(in state));
                Assert.Equal(SidebarSectionKind.PlaylistTree, Library(LibraryV3Document.Build(in state)).Kind);
            }
    }

    [Fact]
    public void FlatLenses_RenderAsAnEntityList()
    {
        foreach (var filter in new[] { SidebarV3Filter.Albums, SidebarV3Filter.Artists, SidebarV3Filter.Podcasts })
        {
            var state = State(filter);
            Assert.False(LibraryV3Document.FoldersApply(in state));
            Assert.Equal(SidebarSectionKind.EntityList, Library(LibraryV3Document.Build(in state)).Kind);
        }
    }

    [Fact]
    public void GridViews_AlwaysRenderAsAnEntityList_BecauseATreeCannotPresentAGrid()
    {
        foreach (var view in new[] { SidebarV3View.CompactGrid, SidebarV3View.Grid })
        {
            var section = Library(LibraryV3Document.Build(State(SidebarV3Filter.Playlists, view)));
            Assert.Equal(SidebarSectionKind.EntityList, section.Kind);
            Assert.Equal(SidebarPresentation.Grid, section.Opts.Presentation);
        }
    }

    [Fact]
    public void Searching_FlattensToAnEntityList_AndDissolvesThePinBand()
    {
        // A search flattens the tree (Foundation obligation 3) and its results are ONE relevance list: the matching pins
        // still lead the projection, but they are not a separate band — which is what makes "no rows" mean "no results".
        var state = State(SidebarV3Filter.Playlists, searching: true, hasPins: true);
        var doc = LibraryV3Document.Build(in state);
        Assert.Equal(SidebarSectionKind.EntityList, Library(doc).Kind);
        Assert.Null(Find(doc, LibraryV3Document.PinsId));
        Assert.Null(Find(doc, LibraryV3Document.LikedId));
    }

    [Fact]
    public void ADrillLevel_IsOneFlatFolderLevel_WithNoPinBandAndNoShortcut()
    {
        var state = State(SidebarV3Filter.Playlists, drill: "f1", hasPins: true);
        var doc = LibraryV3Document.Build(in state);
        Assert.True(state.Drilled);
        Assert.False(LibraryV3Document.FoldersApply(in state));      // the level is already flat
        Assert.Equal(SidebarSectionKind.EntityList, Library(doc).Kind);
        Assert.Null(Find(doc, LibraryV3Document.PinsId));
        Assert.Null(Find(doc, LibraryV3Document.LikedId));
    }

    // ── the view code → presentation / density / columns ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarV3View.CompactList, SidebarPresentation.List, SidebarDensity.Compact, false)]
    [InlineData(SidebarV3View.List, SidebarPresentation.List, SidebarDensity.Cozy, true)]
    [InlineData(SidebarV3View.CompactGrid, SidebarPresentation.Grid, SidebarDensity.Cozy, false)]
    [InlineData(SidebarV3View.Grid, SidebarPresentation.Grid, SidebarDensity.Cozy, true)]
    public void TheViewCode_DrivesPresentationDensityAndSubtitles(SidebarV3View view,
        SidebarPresentation presentation, SidebarDensity density, bool subtitles)
    {
        // Through the ONE shared height ladder these are 32 (Compact) and 44 (Cozy + subtitle) — the two row heights the
        // landed V3 list hard-coded per view.
        var opts = Library(LibraryV3Document.Build(State(view: view))).Opts;
        Assert.Equal(presentation, opts.Presentation);
        Assert.Equal(density, opts.Density);
        Assert.Equal(subtitles, opts.Subtitles);
        Assert.True(opts.Artwork);
        Assert.False(opts.CountBadges);
        Assert.Equal(0, opts.MaxItems);
    }

    [Theory]
    [InlineData(0, 2)]     // a pane too narrow for two columns still gets two: the pane's strip wraps
    [InlineData(1, 2)]
    [InlineData(3, 3)]
    [InlineData(9, 4)]     // the reducer's [2,4] range binds a DERIVED count exactly as it binds a persisted one
    public void TheDerivedColumnCount_IsClampedToTheDocumentRange(int derived, int expected)
    {
        var opts = Library(LibraryV3Document.Build(State(view: SidebarV3View.Grid, columns: derived))).Opts;
        Assert.Equal(expected, opts.GridColumns);
        Assert.Equal(expected, LibraryV3Document.ClampColumns(derived));
    }

    [Fact]
    public void ThePinBand_MirrorsTheContentLadder()
    {
        // One ladder for both bands: a pin row and a library row of the same view must be the same height, in the same
        // presentation, or the pane reads as two lists.
        var doc = LibraryV3Document.Build(State(view: SidebarV3View.Grid, hasPins: true, columns: 3));
        var pins = Find(doc, LibraryV3Document.PinsId);
        Assert.NotNull(pins);
        Assert.Equal(SidebarSectionKind.Pinned, pins!.Kind);
        var library = Library(doc).Opts;
        Assert.Equal(library.Presentation, pins.Opts.Presentation);
        Assert.Equal(library.Density, pins.Opts.Density);
        Assert.Equal(library.Subtitles, pins.Opts.Subtitles);
        Assert.Equal(library.GridColumns, pins.Opts.GridColumns);
    }

    // ── the pin band + the Liked Songs shortcut ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePinBand_ExistsOnlyWhenAPinSurvivedTheLens()
    {
        Assert.Null(Find(LibraryV3Document.Build(State(hasPins: false)), LibraryV3Document.PinsId));
        Assert.NotNull(Find(LibraryV3Document.Build(State(hasPins: true)), LibraryV3Document.PinsId));
    }

    [Fact]
    public void ThePinBand_LeadsTheDocument_AndTheShortcutFollowsIt()
    {
        // §3.0's order: pins → Liked Songs → the library.
        var doc = LibraryV3Document.Build(State(hasPins: true));
        Assert.Equal(new[] { LibraryV3Document.PinsId, LibraryV3Document.LikedId, LibraryV3Document.LibraryId },
            IdsOf(doc));
    }

    [Theory]
    [InlineData(SidebarV3Filter.All, true)]
    [InlineData(SidebarV3Filter.Playlists, true)]
    [InlineData(SidebarV3Filter.Albums, false)]
    [InlineData(SidebarV3Filter.Artists, false)]
    [InlineData(SidebarV3Filter.Podcasts, false)]
    public void TheLikedShortcut_IsScopedToTheLensesWhereItIsTruthful(SidebarV3Filter filter, bool present)
    {
        var doc = LibraryV3Document.Build(State(filter));
        Assert.Equal(present, Find(doc, LibraryV3Document.LikedId) is not null);
    }

    [Fact]
    public void TheLikedShortcut_IsAbsentWhenItIsItselfPinned()
    {
        // It is then rendered as pin #n — never twice.
        Assert.Null(Find(LibraryV3Document.Build(State(hasPins: true, likedPinned: true)), LibraryV3Document.LikedId));
    }

    [Fact]
    public void TheLikedShortcut_IsAGlyphRouteRow_AtAContentRowHeight()
    {
        var liked = Find(LibraryV3Document.Build(State()), LibraryV3Document.LikedId);
        Assert.NotNull(liked);
        Assert.Equal(SidebarSectionKind.StaticLinks, liked!.Kind);
        Assert.Equal(SidebarPresentation.List, liked.Opts.Presentation);
        Assert.False(liked.Opts.Artwork);
        Assert.False(liked.Opts.Subtitles);
        // Comfortable + no subtitle = 44 = the Cozy+subtitle content row; Compact = 32 = the compact content row.
        Assert.Equal(SidebarDensity.Comfortable, liked.Opts.Density);
        Assert.Equal(SidebarDensity.Compact,
            Find(LibraryV3Document.Build(State(view: SidebarV3View.CompactList)), LibraryV3Document.LikedId)!
                .Opts.Density);

        var item = Assert.Single(liked.ItemList);
        Assert.Equal(SidebarItemTarget.Route, item.Target);
        Assert.Equal(LibraryV3Document.LikedRouteKey, item.Key);
        Assert.True(SidebarIconNames.IsAllowed(item.IconOverride));
    }

    // ── the query mirror ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarV3Filter.All, SidebarEntityKinds.All)]
    [InlineData(SidebarV3Filter.Playlists, SidebarEntityKinds.Playlists)]
    [InlineData(SidebarV3Filter.Albums, SidebarEntityKinds.Albums)]
    [InlineData(SidebarV3Filter.Artists, SidebarEntityKinds.Artists)]
    [InlineData(SidebarV3Filter.Podcasts, SidebarEntityKinds.Shows)]
    public void TheQuery_MirrorsTheLensKinds(SidebarV3Filter filter, SidebarEntityKinds kinds)
    {
        var query = Library(LibraryV3Document.Build(State(filter, searching: true))).Query;
        Assert.NotNull(query);
        Assert.Equal(kinds, query!.Kinds);
        Assert.Equal(kinds, LibraryV3Document.KindsFor((int)filter));
    }

    [Theory]
    [InlineData(SidebarV3Sort.Recents, SidebarSortMode.Recents)]
    [InlineData(SidebarV3Sort.RecentlyAdded, SidebarSortMode.RecentlyAdded)]
    [InlineData(SidebarV3Sort.Alphabetical, SidebarSortMode.Alphabetical)]
    [InlineData(SidebarV3Sort.Creator, SidebarSortMode.Creator)]
    [InlineData(SidebarV3Sort.Custom, SidebarSortMode.CustomOrder)]
    public void TheQuery_MirrorsTheSortMode(SidebarV3Sort sort, SidebarSortMode mode)
    {
        var query = Library(LibraryV3Document.Build(
            State(SidebarV3Filter.Playlists, sort: sort, searching: true))).Query;
        Assert.Equal(mode, query!.Sort);
    }

    [Fact]
    public void CustomOrder_OutsideThePlaylistsLens_FallsBackToAlphabetical()
    {
        // Locked decision 10 / F.7.10: the fallback is FOR DISPLAY — exactly what SidebarSort.Effective does for the
        // projection — and the persisted preference is never rewritten here.
        var query = Library(LibraryV3Document.Build(State(SidebarV3Filter.Albums, sort: SidebarV3Sort.Custom))).Query;
        Assert.Equal(SidebarSortMode.Alphabetical, query!.Sort);
        Assert.Equal(SidebarSortMode.CustomOrder,
            LibraryV3Document.SortFor((int)SidebarV3Sort.Custom, (int)SidebarV3Filter.Playlists));
    }

    [Theory]
    // V3's flag means "REVERSE the sort's natural direction"; the query's means "descending" literally, and the planner's
    // comparator undoes that mapping again for the two recency modes. Only those two therefore invert.
    [InlineData(SidebarV3Sort.Recents, false, true)]
    [InlineData(SidebarV3Sort.Recents, true, false)]
    [InlineData(SidebarV3Sort.RecentlyAdded, false, true)]
    [InlineData(SidebarV3Sort.Alphabetical, false, false)]
    [InlineData(SidebarV3Sort.Alphabetical, true, true)]
    [InlineData(SidebarV3Sort.Creator, true, true)]
    public void TheQuery_ReconcilesTheDirectionVocabulary(SidebarV3Sort sort, bool v3Descending, bool queryDescending)
    {
        var query = Library(LibraryV3Document.Build(
            State(SidebarV3Filter.Playlists, sort: sort, descending: v3Descending, searching: true))).Query;
        Assert.Equal(queryDescending, query!.Descending);
    }

    [Fact]
    public void TheQualifier_IsTheEFFECTIVEOne_NeverThePersistedOne()
    {
        // Two coercions the projection already applied, mirrored so the query can never filter MORE than the rows it
        // describes: a qualifier the data cannot evidence, and a qualifier outside the Playlists lens.
        Assert.Equal(SidebarPlaylistQualifier.Any,
            Library(LibraryV3Document.Build(State(SidebarV3Filter.Playlists,
                qualifier: SidebarV3Qualifier.ByYou, qualifiersAvailable: false, searching: true))).Query!.Qualifier);

        Assert.Equal(SidebarPlaylistQualifier.Any,
            Library(LibraryV3Document.Build(State(SidebarV3Filter.Albums,
                qualifier: SidebarV3Qualifier.ByYou, qualifiersAvailable: true))).Query!.Qualifier);

        Assert.Equal(SidebarPlaylistQualifier.BySpotify,
            Library(LibraryV3Document.Build(State(SidebarV3Filter.Playlists,
                qualifier: SidebarV3Qualifier.BySpotify, qualifiersAvailable: true, searching: true))).Query!.Qualifier);
    }

    [Fact]
    public void TreeDocument_PreservesTheShapedOrderWithANullQuery()
    {
        var tree = Library(LibraryV3Document.Build(State(SidebarV3Filter.Playlists, SidebarV3View.List)));
        Assert.Equal(SidebarSectionKind.PlaylistTree, tree.Kind);
        Assert.Null(tree.Query);

        var flat = Library(LibraryV3Document.Build(
            State(SidebarV3Filter.Playlists, SidebarV3View.List, searching: true)));
        Assert.Equal(SidebarSectionKind.EntityList, flat.Kind);
        Assert.NotNull(flat.Query);
    }

    // ── document-level invariants ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SectionIds_AreStableAcrossEveryState()
    {
        // The pane keys its reorder bands, its scroll identity and its section lookup off these ids, and this document is
        // rebuilt on every keystroke — a minted id per rebuild would reset all three continuously.
        string[] states =
        [
            .. IdsOf(LibraryV3Document.Build(State(hasPins: true))),
            .. IdsOf(LibraryV3Document.Build(State(SidebarV3Filter.Playlists, SidebarV3View.Grid, hasPins: true))),
            .. IdsOf(LibraryV3Document.Build(State(SidebarV3Filter.Albums, searching: true, hasPins: true))),
        ];
        foreach (string id in states)
            Assert.Contains(id, new[] { LibraryV3Document.PinsId, LibraryV3Document.LikedId, LibraryV3Document.LibraryId });

        Assert.Equal(IdsOf(LibraryV3Document.Build(State(hasPins: true))),
                     IdsOf(LibraryV3Document.Build(State(hasPins: true))));
    }

    [Fact]
    public void NoSectionCarriesATitle_SoThePaneEmitsNoHeaderRows()
    {
        // §3.2.7: V3 has no section headers — structure is the chrome's job. A title (or a title loc key) would make the
        // planner emit a SectionHeader row and hand it the quick-layout menu V3 keeps in its overflow.
        var doc = LibraryV3Document.Build(State(hasPins: true));
        foreach (var s in doc.Sections)
        {
            Assert.Null(s.Title);
            Assert.Null(s.TitleLocKey);
            Assert.False(s.Collapsed);
            Assert.False(s.Hidden);
        }
    }

    [Fact]
    public void TheLibrarySection_SuppressesTheSharedEmptyBody()
    {
        // §3.2.10's three empty states are ACTIONABLE and name the query, so V3's chrome owns them; the shared renderer's
        // quiet one-line hint would be a second, weaker message under them.
        Assert.Equal(SidebarEmptyBehavior.HideBody, Library(LibraryV3Document.Build(State())).Opts.EmptyBehavior);
    }

    [Fact]
    public void EverySection_ShowsInTheRail_SoACollapsedPaneHonoursTheLens()
    {
        // §3.2.13: the rail's tiles are the document's (pins first, then the current filtered library), which is what makes
        // collapsing the pane never silently widen the visible set.
        var doc = LibraryV3Document.Build(State(hasPins: true));
        foreach (var s in doc.Sections) Assert.True(s.Opts.ShowInRail);
    }

    [Fact]
    public void TheDocument_IsNotACuratedTemplate()
    {
        string id = LibraryV3Document.Build(State()).TemplateId;
        Assert.Equal(LibraryV3Document.TemplateId, id);
        foreach (string template in SidebarTemplates.All) Assert.NotEqual(template, id);
    }
}

// The V3 content ORDER: the one thing the retired LibraryV3Index was genuinely for. The published projection is sorted
// FLAT, so a nested playlist can land above the folder that contains it; §3.2.7 wants folders ordered among their siblings
// and each folder's children ordered within it. These tests pin that re-grouping, the drill slice, and the two facts the
// custom-order commit depends on (the sibling clamp and the materialized order).
public sealed class LibraryV3ViewTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────

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

    static SidebarLibraryEntry Album(string slug, int order = 0)
        => new(Id: "album:spotify:album:" + slug, Kind: SidebarEntryKind.Album, Uri: "spotify:album:" + slug,
               Name: slug, Creator: "Artist", Cover: null, MosaicTiles: null, ChildCount: 0, AddedAtMs: 0,
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

    static string[] NamesOf(LibraryV3View view)
    {
        var names = new string[view.Count];
        for (int i = 0; i < names.Length; i++) names[i] = view.Rows[i].Name;
        return names;
    }

    static int[] DepthsOf(LibraryV3View view)
    {
        var depths = new int[view.Count];
        for (int i = 0; i < depths.Length; i++) depths[i] = view.Rows[i].Depth;
        return depths;
    }

    // ── grouping ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRootLevel_ReGroupsChildrenUnderTheirFolder()
    {
        // The published order is what a FLAT sort produces: the nested playlists sort above the folders that contain them.
        var published = new[]
        {
            Playlist("deep", folderId: "inner"),
            Playlist("mid", folderId: "outer"),
            Folder("outer"),
            Folder("inner"),
            Playlist("top"),
        };

        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: true);

        Assert.Equal(new[] { "outer", "mid", "inner", "deep", "top" }, NamesOf(view));
        Assert.Equal(new[] { 0, 1, 1, 2, 0 }, DepthsOf(view));
    }

    [Fact]
    public void Grouping_PreservesTheSiblingOrderTheProjectionPublished()
    {
        // Siblings keep their published (sorted) order — the re-grouping only moves children UNDER their parent, it never
        // re-sorts a level.
        var published = new[] { Folder("outer"), Playlist("b", folderId: "outer"), Playlist("a", folderId: "outer") };
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: true);
        Assert.Equal(new[] { "outer", "b", "a" }, NamesOf(view));
    }

    [Fact]
    public void ARowWhoseFolderIsNotVisible_IsPromotedToTopLevel()
    {
        // Nothing is ever hidden because its container happens to be elsewhere (pinned into the band, dropped by the lens,
        // or a cold tree) — that would silently lose playlists.
        var published = new[] { Playlist("orphan", folderId: "outer"), Playlist("top") };
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: true);
        Assert.Equal(new[] { "orphan", "top" }, NamesOf(view));
        Assert.Equal(new[] { 0, 0 }, DepthsOf(view));
    }

    [Fact]
    public void TheLeadingPinBand_IsSkipped_WhenItIsRenderedAsItsOwnSection()
    {
        var published = new[] { Playlist("pinned"), Folder("outer"), Playlist("mid", folderId: "outer") };
        var view = new LibraryV3View();
        view.Build(published, 1, Tree(), 1, null, group: true);
        Assert.Equal(new[] { "outer", "mid" }, NamesOf(view));
    }

    [Fact]
    public void FlatMode_PassesTheSliceThrough_AtDepthZero()
    {
        // A search has already flattened the projection and a grid cannot express disclosure: both want the published order
        // verbatim, with no indent inherited from the tree the entries came out of.
        var published = new[] { Playlist("deep", folderId: "inner", depth: 2), Album("one"), Playlist("top") };
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: false);
        Assert.Equal(new[] { "deep", "one", "top" }, NamesOf(view));
        Assert.Equal(new[] { 0, 0, 0 }, DepthsOf(view));
    }

    // ── the drill level (Revision 2) ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADrillLevel_IsExactlyOneFoldersDirectChildren_AtDepthZero()
    {
        var published = new[]
        {
            Folder("outer"), Folder("inner"), Playlist("mid", folderId: "outer"),
            Playlist("deep", folderId: "inner"), Playlist("top"),
        };

        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, "outer", group: true);

        // "inner" is a child folder of "outer" and stays a row (it can be drilled into again); "deep" belongs to inner and
        // does NOT leak into this level.
        Assert.Equal(new[] { "inner", "mid" }, NamesOf(view));
        Assert.Equal(new[] { 0, 0 }, DepthsOf(view));
        Assert.False(view.DrillTargetMissing);
    }

    [Fact]
    public void ADrillLevel_IgnoresTheSkip_BecauseThereIsNoPinBandInside()
    {
        // A pinned playlist that lives inside the folder must still appear inside it.
        var published = new[] { Playlist("mid", folderId: "outer"), Folder("outer"), Playlist("top") };
        var view = new LibraryV3View();
        view.Build(published, 1, Tree(), 1, "outer", group: true);
        Assert.Equal(new[] { "mid" }, NamesOf(view));
    }

    [Fact]
    public void AnEmptyFolder_IsALegitimateLevel_NotAMissingTarget()
    {
        // Popping out of an empty folder would make an empty folder impossible to open.
        var published = new[] { Folder("outer"), Playlist("top") };
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, "outer", group: true);
        Assert.Equal(0, view.Count);
        Assert.False(view.DrillTargetMissing);
    }

    [Fact]
    public void ADrilledFolderThatVanished_IsReportedMissing()
    {
        var published = new[] { Playlist("top") };
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, "outer", group: true);
        Assert.True(view.DrillTargetMissing);
        Assert.Equal(0, view.Count);
    }

    [Fact]
    public void AColdProjection_IsNotAMissingDrillTarget()
    {
        var view = new LibraryV3View();
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
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: false);
        Assert.Equal(new[] { 0, 1, 2 }, new[] { view.Rows[0].SourceOrder, view.Rows[1].SourceOrder, view.Rows[2].SourceOrder });
    }

    [Fact]
    public void SameParent_ClampsADragAcrossAFolderBoundary()
    {
        var published = new[] { Folder("outer"), Playlist("mid", folderId: "outer"), Playlist("top") };
        var view = new LibraryV3View();
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
        var view = new LibraryV3View();
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
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: false);

        var into = new List<string>();
        view.MaterializeOrder(into, 1, 2);
        Assert.Equal(new[] { view.KeyAt(2), view.KeyAt(1) }, into);
    }

    [Fact]
    public void ARebuild_ReusesItsBuffers_AndNeverLeaksTheOldOrder()
    {
        var view = new LibraryV3View();
        view.Build(new[] { Folder("outer"), Playlist("mid", folderId: "outer") }, 0, Tree(), 1, null, group: true);
        Assert.Equal(2, view.Count);

        view.Build(new[] { Playlist("top") }, 0, Tree(), 1, null, group: true);
        Assert.Equal(new[] { "top" }, NamesOf(view));

        view.Build(Array.Empty<SidebarLibraryEntry>(), 0, Tree(), 1, null, group: true);
        Assert.Equal(0, view.Count);
    }
}
