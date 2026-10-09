// ── Wavee.Tests/SidebarLayoutInfoTests.cs — Library's filter rail, search host and tree view (the chrome helpers) ─────
//
// Renamed from SidebarDesignTests.cs (P3-WP22). The design vocabulary, the chooser gate, Classic's locked document and the
// nav band are gone with the design model; what survives P3 is the Library V3 chrome: LibraryV3ChipStrip (the filter
// rail's order), LibraryV3SearchRules (the Escape ladder, blur-close, open-width arithmetic) and LibraryV3View (the tree
// re-grouping and the drill slice), all in Shell/Sidebar.Modes.cs. P5 deletes this file with the chrome.
//
// Pure: no window, no loop, no element.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── REGION 5 — LIBRARY V3's FILTER RAIL: the idle / filtered / fused chip strip ─────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// The Library V3 filter rail's ORDER, driven directly: what idle/filtered/fused look like, what a tap on each
// position writes back, and how roving focus survives a relayout. These are the rules the eye reads — the ✕ pops in
// only once something is active, the qualifier only ever rides Playlists.
public sealed class LibraryV3ChipStripTests
{
    const int All = (int)SidebarV3Filter.All;
    const int Playlists = (int)SidebarV3Filter.Playlists;
    const int Podcasts = (int)SidebarV3Filter.Podcasts;
    const int Albums = (int)SidebarV3Filter.Albums;
    const int Artists = (int)SidebarV3Filter.Artists;
    const int Any = (int)SidebarV3Qualifier.Any;
    const int ByYou = (int)SidebarV3Qualifier.ByYou;
    const int BySpotify = (int)SidebarV3Qualifier.BySpotify;
    const int Mixed = (int)SidebarV3Qualifier.Mixed;

    // ── idle ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Idle_NoFilter_IsFourUnselectedFacets_NoClear()
    {
        var slots = LibraryV3ChipStrip.Slots(All, Any, qualifiersAvailable: true);

        Assert.Equal(4, slots.Count);
        Assert.All(slots, s => Assert.Equal(V3ChipKind.Facet, s.Kind));
        Assert.All(slots, s => Assert.False(s.Selected));
        Assert.DoesNotContain(slots, s => s.Kind == V3ChipKind.Clear);
        Assert.Equal(new[] { Playlists, Podcasts, Albums, Artists },
            new[] { slots[0].Code, slots[1].Code, slots[2].Code, slots[3].Code });

        // Tapping an idle facet SELECTS it (Any qualifier — a fresh filter never carries over a stale sub-filter).
        Assert.Equal(Playlists, slots[0].SelectFilter);
        Assert.Equal(Any, slots[0].SelectQualifier);
        Assert.Equal("v3f" + Playlists, slots[0].Key);
    }

    // ── filtered, no qualifier available/relevant ────────────────────────────────────────────────────────────────

    [Fact]
    public void Filtered_NonPlaylists_IsClearPlusTheOneSelectedFacet_NoOptions()
    {
        foreach (var f in new[] { Podcasts, Albums, Artists })
        {
            var slots = LibraryV3ChipStrip.Slots(f, Any, qualifiersAvailable: true);

            Assert.Equal(2, slots.Count);
            Assert.Equal(V3ChipKind.Clear, slots[0].Kind);
            Assert.Equal(V3ChipKind.Facet, slots[1].Kind);
            Assert.True(slots[1].Selected);
            Assert.Equal(f, slots[1].Code);
            Assert.DoesNotContain(slots, s => s.Kind == V3ChipKind.Option);
        }
    }

    [Fact]
    public void Filtered_Playlists_QualifiersUnavailable_NoOptionsSpill()
    {
        var slots = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: false);

        Assert.Equal(2, slots.Count);
        Assert.Equal(V3ChipKind.Clear, slots[0].Kind);
        Assert.Equal(V3ChipKind.Facet, slots[1].Kind);
        Assert.True(slots[1].Selected);
        Assert.DoesNotContain(slots, s => s.Kind == V3ChipKind.Option);
    }

    // ── filtered, Playlists with qualifiers evidenced (spilled) ──────────────────────────────────────────────────

    [Fact]
    public void Filtered_Playlists_QualifiersAvailable_SpillsTheThreeOptionsAfterTheFacet()
    {
        var slots = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: true);

        Assert.Equal(5, slots.Count);
        Assert.Equal(V3ChipKind.Clear, slots[0].Kind);

        Assert.Equal(V3ChipKind.Facet, slots[1].Kind);
        Assert.True(slots[1].Selected);
        Assert.Equal(Playlists, slots[1].Code);
        Assert.Equal("v3f" + Playlists, slots[1].Key);

        Assert.Equal(V3ChipKind.Option, slots[2].Kind);
        Assert.Equal(ByYou, slots[2].Code);
        Assert.Equal("v3q" + ByYou, slots[2].Key);
        Assert.False(slots[2].Selected);
        Assert.Equal(Playlists, slots[2].SelectFilter);
        Assert.Equal(ByYou, slots[2].SelectQualifier);

        Assert.Equal(BySpotify, slots[3].Code);
        Assert.Equal(Mixed, slots[4].Code);
    }

    // ── fused ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fused_PlaylistsWithQualifierPicked_IsClearPlusOneFusedSlot_NoOptions()
    {
        var slots = LibraryV3ChipStrip.Slots(Playlists, ByYou, qualifiersAvailable: true);

        Assert.Equal(2, slots.Count);
        Assert.Equal(V3ChipKind.Clear, slots[0].Kind);

        var fused = slots[1];
        Assert.Equal(V3ChipKind.Fused, fused.Kind);
        Assert.True(fused.Selected);
        Assert.Equal(Playlists, fused.Code);
        Assert.DoesNotContain(slots, s => s.Kind == V3ChipKind.Option);

        // Tapping the fused pill steps back ONE level — drops the qualifier, keeps the facet — not all the way to All.
        Assert.Equal(Playlists, fused.SelectFilter);
        Assert.Equal(Any, fused.SelectQualifier);
    }

    [Fact]
    public void Fused_And_LooseFacet_ShareTheSameKey_ForTheSameCode()
    {
        // The morph mechanism: the reconciler must see ONE node across the loose ⇄ fused transition.
        var loose = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: true)[1];
        var fused = LibraryV3ChipStrip.Slots(Playlists, ByYou, qualifiersAvailable: true)[1];

        Assert.Equal(V3ChipKind.Facet, loose.Kind);
        Assert.Equal(V3ChipKind.Fused, fused.Kind);
        Assert.Equal(loose.Key, fused.Key);
        Assert.Equal("v3f" + Playlists, loose.Key);
    }

    [Fact]
    public void QualifierPicked_ButNotPlaylists_NeverFuses()
    {
        // A stale/unevidenced qualifier under a non-Playlists filter must not fuse — Slots is a pure function of its
        // three inputs and never assumes the caller already normalized qualifier against filter.
        var slots = LibraryV3ChipStrip.Slots(Albums, ByYou, qualifiersAvailable: true);

        Assert.Equal(2, slots.Count);
        Assert.Equal(V3ChipKind.Facet, slots[1].Kind);
        Assert.DoesNotContain(slots, s => s.Kind == V3ChipKind.Fused);
    }

    // ── what a tap writes ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClearSlot_WritesAllAndAny()
    {
        var clear = LibraryV3ChipStrip.Slots(Playlists, ByYou, qualifiersAvailable: true)[0];
        Assert.Equal(V3ChipKind.Clear, clear.Kind);
        Assert.Equal(All, clear.SelectFilter);
        Assert.Equal(Any, clear.SelectQualifier);
    }

    [Fact]
    public void SelectedFacetSlot_TapClears_TheChosenPillIsItsOwnToggle()
    {
        var facet = LibraryV3ChipStrip.Slots(Podcasts, Any, qualifiersAvailable: false)[1];
        Assert.True(facet.Selected);
        Assert.Equal(All, facet.SelectFilter);
        Assert.Equal(Any, facet.SelectQualifier);
    }

    [Fact]
    public void OptionSlot_WritesItsOwnFilterAndQualifier()
    {
        var option = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: true)[3]; // BySpotify
        Assert.Equal(V3ChipKind.Option, option.Kind);
        Assert.Equal(BySpotify, option.Code);
        Assert.Equal(Playlists, option.SelectFilter);
        Assert.Equal(BySpotify, option.SelectQualifier);
    }

    // ── FocusIndex ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FocusIndex_FindsTheMatchingKey()
    {
        var slots = LibraryV3ChipStrip.Slots(All, Any, qualifiersAvailable: false);
        Assert.Equal(2, LibraryV3ChipStrip.FocusIndex(slots, "v3f" + Albums));
    }

    [Fact]
    public void FocusIndex_FallsBackToZero_WhenTheKeyIsGone()
    {
        // The focused chip (an option, say) vanished from the rail on this render — focus lands on the leading slot
        // instead of throwing or leaving the rail with no roving position at all.
        var slots = LibraryV3ChipStrip.Slots(Albums, Any, qualifiersAvailable: false);
        Assert.Equal(0, LibraryV3ChipStrip.FocusIndex(slots, "v3q" + ByYou));
        Assert.Equal(0, LibraryV3ChipStrip.FocusIndex(slots, null));
    }

    [Fact]
    public void FocusIndex_SurvivesTheLooseToFusedRelayout_BecauseTheKeyIsShared()
    {
        var loose = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: true);
        int focusedAt = LibraryV3ChipStrip.FocusIndex(loose, "v3f" + Playlists);
        Assert.Equal(1, focusedAt);

        var fused = LibraryV3ChipStrip.Slots(Playlists, ByYou, qualifiersAvailable: true);
        Assert.Equal(1, LibraryV3ChipStrip.FocusIndex(fused, "v3f" + Playlists));
    }

    // ── Route (issue #85, H4 approach 3) ─────────────────────────────────────────────────────────────────────────────
    // Exactly Albums/Artists/Podcasts have an actual library page; Playlists has no "all playlists" destination, so
    // its chip — loose OR fused — never carries one, same as Clear and every Option slot.

    [Theory]
    [InlineData(Albums, "albums")]
    [InlineData(Artists, "artists")]
    [InlineData(Podcasts, "podcasts")]
    [InlineData(Playlists, null)]
    public void RouteFor_IsPopulatedForExactlyTheThreeKindsWithAPage(int filter, string? expected)
        => Assert.Equal(expected, LibraryV3ChipStrip.RouteFor(filter));

    [Fact]
    public void IdleFacets_CarryTheirRoute_ExceptPlaylists()
    {
        var slots = LibraryV3ChipStrip.Slots(All, Any, qualifiersAvailable: true);
        foreach (var s in slots)
            Assert.Equal(LibraryV3ChipStrip.RouteFor(s.Code), s.Route);
        Assert.Null(slots[0].Route);   // Playlists is always index 0 in the idle order
    }

    [Fact]
    public void SelectedFacetSlot_StillCarriesItsRoute()
    {
        var facet = LibraryV3ChipStrip.Slots(Podcasts, Any, qualifiersAvailable: false)[1];
        Assert.Equal("podcasts", facet.Route);
    }

    [Fact]
    public void ClearAndOptionSlots_NeverCarryARoute()
    {
        var slots = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: true);
        Assert.Equal(V3ChipKind.Clear, slots[0].Kind);
        Assert.Null(slots[0].Route);
        foreach (var s in slots)
            if (s.Kind == V3ChipKind.Option) Assert.Null(s.Route);
    }

    [Fact]
    public void FusedPlaylistsSlot_HasNoRoute_PlaylistsNeverHasOne()
    {
        var fused = LibraryV3ChipStrip.Slots(Playlists, ByYou, qualifiersAvailable: true)[1];
        Assert.Equal(V3ChipKind.Fused, fused.Kind);
        Assert.Null(fused.Route);
    }

    [Fact]
    public void ATap_OnlyEverWritesTheFilterOrQualifier_RouteIsASeparateSecondaryField()
    {
        // The contract H4 decided: Route is data a RENDERER may act on with a secondary gesture (Library V3 chose a
        // double-click) — it must never change what SelectFilter/SelectQualifier themselves write for a plain tap.
        var albums = LibraryV3ChipStrip.Slots(All, Any, qualifiersAvailable: false)[2];
        Assert.Equal(Albums, albums.Code);
        Assert.Equal("albums", albums.Route);
        Assert.Equal(Albums, albums.SelectFilter);   // a tap still only selects the Albums filter
        Assert.Equal(Any, albums.SelectQualifier);
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── REGION 6 — LIBRARY V3's SEARCH HOST: the Escape ladder, blur-close, open-width arithmetic ───────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// The search host's morph logic is decided by LibraryV3SearchRules (System-only), never by the component that
// renders it, so the Escape ladder / blur-close / open-width arithmetic are pinned here without an EditableText, a
// signal or a frame.
public sealed class LibraryV3SearchRulesTests
{
    // ── Escape ladder ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Escape_WithText_Clears()
        => Assert.Equal(LibraryV3SearchRules.EscapeAction.Clear, LibraryV3SearchRules.OnEscape("blue"));

    [Fact]
    public void Escape_WhenEmpty_Closes()
        => Assert.Equal(LibraryV3SearchRules.EscapeAction.Close, LibraryV3SearchRules.OnEscape(""));

    // ── blur ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Blur_WhenEmpty_Closes() => Assert.True(LibraryV3SearchRules.ClosesOnBlur(""));

    [Fact]
    public void Blur_WithQuery_StaysOpen() => Assert.False(LibraryV3SearchRules.ClosesOnBlur("blue"));

    // ── open width ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OpenWidth_IsThePaneMinusPaddingMinusThePillAndGap()
    {
        // 320 pane, 43 DIP of toolbar padding -> 320 - 43 - 28 - 4 = 245.
        float width = LibraryV3SearchRules.OpenWidth(320f, 43f);
        Assert.Equal(245f, width);
    }

    [Fact]
    public void OpenWidth_NeverGoesBelowClosedWidth()
    {
        // A pane too narrow to fit the pill+gap must not yield a negative or shrinking host.
        float width = LibraryV3SearchRules.OpenWidth(40f, 43f);
        Assert.Equal(LibraryV3SearchRules.ClosedWidth, width);
    }

    [Fact]
    public void Resolve_WidePane_IsInlineAndExpanded_WithALabelledPill()
    {
        var wide = LibraryV3SearchRules.Resolve(LibraryV3SearchRules.InlineWidth, openedByUser: false, hasText: false);
        Assert.True(wide.Inline);
        Assert.True(wide.Expanded);
        Assert.False(wide.SortIconOnly);
    }

    [Fact]
    public void Resolve_NarrowPane_IsAButtonUntilOpenedOrTyped()
    {
        float narrow = LibraryV3SearchRules.InlineWidth - 1f;
        var closed = LibraryV3SearchRules.Resolve(narrow, openedByUser: false, hasText: false);
        Assert.False(closed.Inline);
        Assert.False(closed.Expanded);
        Assert.False(closed.SortIconOnly);            // 299 ≥ 280: the pill keeps its label while the field is a button

        var opened = LibraryV3SearchRules.Resolve(narrow, openedByUser: true, hasText: false);
        Assert.True(opened.Expanded);
        Assert.True(opened.SortIconOnly);             // the field owns the row

        // A query typed while wide survives a drag past the threshold: text alone keeps the field expanded.
        var typed = LibraryV3SearchRules.Resolve(narrow, openedByUser: false, hasText: true);
        Assert.True(typed.Expanded);
    }

    [Fact]
    public void Resolve_VeryNarrowPane_DropsThePillLabelEvenWhenClosed()
    {
        var tiny = LibraryV3SearchRules.Resolve(240f, openedByUser: false, hasText: false);
        Assert.False(tiny.Expanded);
        Assert.True(tiny.SortIconOnly);
    }

    [Fact]
    public void OpenWidth_AtTheFloorBoundary_IsExact()
    {
        // paneWidth - padH - pill - gap == ClosedWidth exactly: the floor must not clip a legitimate value.
        float width = LibraryV3SearchRules.OpenWidth(
            LibraryV3SearchRules.ClosedWidth + 43f + LibraryV3SearchRules.SortIconOnlyWidth + LibraryV3SearchRules.Gap,
            43f);
        Assert.Equal(LibraryV3SearchRules.ClosedWidth, width);
    }
}

// The V3 content ORDER: the one thing the retired LibraryV3Index was genuinely for. The published projection is
// sorted FLAT, so a nested playlist can land above the folder that contains it; the re-grouping puts folders among
// their siblings and each folder's children ordered within it. These tests pin that re-grouping, the drill slice,
// and the two facts the custom-order commit depends on (the sibling clamp and the materialized order).
public sealed class LibraryV3ViewTests
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

        var view = new LibraryV3View();
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
        var view = new LibraryV3View();
        view.Build(published, 0, Tree(), 1, null, group: true);
        Assert.Equal(new[] { "outer", "b", "a" }, NamesOf(view));
    }

    [Fact]
    public void ARowWhoseFolderIsNotVisible_IsPromotedToTopLevel()
    {
        // Nothing is ever hidden because its container happens to be elsewhere (pinned into the band, dropped by the
        // lens, or a cold tree) — that would silently lose playlists.
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
        // A search has already flattened the projection and a grid cannot express disclosure: both want the
        // published order verbatim, with no indent inherited from the tree the entries came out of.
        var published = new[] { Playlist("deep", folderId: "inner", depth: 2), Album("one"), Playlist("top") };
        var view = new LibraryV3View();
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

        var view = new LibraryV3View();
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
