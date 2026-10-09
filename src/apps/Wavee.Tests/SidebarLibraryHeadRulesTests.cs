using System.Collections.Generic;
using FluentGpu.Signals;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// ── SidebarLibraryHeadRules: Your Library's head (design P.2a) — the pages, the chips, the toolbar, the ring, the Tab order ──
//
// docs/plans/wavee/sidebar-rework-implementation.md §P5.1 and §P5.10. Pure: every fact is a rule over plain values.

public sealed class SidebarLibraryHeadRulesTests
{
    static readonly System.Func<string, string> Glyph = static r => "g:" + r;

    [Fact]
    public void HeadHeight_Is172()
    {
        Assert.Equal(172f, SidebarLibraryHeadRules.HeadHeight);
    }

    [Fact]
    public void Pages_CountUnknown_NoNumber_WhileTheLibraryLoads()
    {
        var pages = new List<SidebarLibraryPage>();
        SidebarLibraryHeadRules.Pages(SidebarLibraryKinds.None, default, Glyph, pages);   // default ⇒ Known false
        Assert.Equal(4, pages.Count);
        Assert.All(pages, p => Assert.Null(p.Count));
    }

    [Fact]
    public void Pages_GenuineZero_IsShown()
    {
        var pages = new List<SidebarLibraryPage>();
        SidebarLibraryHeadRules.Pages(SidebarLibraryKinds.None, new SidebarLibraryCounts(0, 3, 0, 1, Known: true), Glyph, pages);
        Assert.Equal(0, pages[0].Count);       // Albums 0, not "no count"
        Assert.Equal(3, pages[1].Count);
    }

    [Fact]
    public void Pages_AllFourHidden_Empty_TitleIsNotAMenu()
    {
        var pages = new List<SidebarLibraryPage>();
        var all = SidebarLibraryKinds.Albums | SidebarLibraryKinds.Artists | SidebarLibraryKinds.Podcasts | SidebarLibraryKinds.Audiobooks;
        SidebarLibraryHeadRules.Pages(all, new SidebarLibraryCounts(1, 1, 1, 1, Known: true), Glyph, pages);
        Assert.Empty(pages);
        Assert.False(SidebarLibraryHeadRules.DropdownIsMenu(pages.Count));
        Assert.True(SidebarLibraryHeadRules.DropdownIsMenu(1));
    }

    [Fact]
    public void Pages_AllFour_InOrder_WithCounts()
    {
        var pages = new List<SidebarLibraryPage>();
        SidebarLibraryHeadRules.Pages(SidebarLibraryKinds.None, new SidebarLibraryCounts(2, 5, 7, 11, Known: true), Glyph, pages);
        Assert.Equal(new[] { "albums", "artists", "podcasts", "audiobooks" }, pages.ConvertAll(p => p.Route));
        Assert.Equal(new[] { "nav.albums", "nav.artists", "nav.podcasts", "nav.audiobooks" }, pages.ConvertAll(p => p.TitleKey));
        Assert.Equal(new[] { 2, 5, 7, 11 }, pages.ConvertAll(p => p.Count!.Value));
        Assert.Equal("g:artists", pages[1].Glyph);
    }

    [Fact]
    public void Pages_HiddenKindAbsent()
    {
        var pages = new List<SidebarLibraryPage>();
        SidebarLibraryHeadRules.Pages(SidebarLibraryKinds.Podcasts, new SidebarLibraryCounts(2, 5, 7, 11, Known: true), Glyph, pages);
        Assert.Equal(new[] { "albums", "artists", "audiobooks" }, pages.ConvertAll(p => p.Route));
    }

    [Fact]
    public void PageOf_LibraryPages_NullForOthers_NullForHidden()
    {
        Assert.Equal("albums", SidebarLibraryHeadRules.PageOf("albums", SidebarLibraryKinds.None));
        Assert.Equal("podcasts", SidebarLibraryHeadRules.PageOf("podcasts", SidebarLibraryKinds.Albums));
        Assert.Null(SidebarLibraryHeadRules.PageOf("search", SidebarLibraryKinds.None));
        Assert.Null(SidebarLibraryHeadRules.PageOf("albums", SidebarLibraryKinds.Albums));   // hidden: its route works, its page is gone
    }

    [Fact]
    public void DropdownPill_OnlyOnAPage_AndNotWhenARowHasIt()
    {
        Assert.True(SidebarLibraryHeadRules.DropdownCarriesPill("albums", SidebarLibraryKinds.None, rowCarriesPill: false));
        Assert.False(SidebarLibraryHeadRules.DropdownCarriesPill("albums", SidebarLibraryKinds.None, rowCarriesPill: true));
        Assert.False(SidebarLibraryHeadRules.DropdownCarriesPill("pl:a", SidebarLibraryKinds.None, rowCarriesPill: false));
    }

    [Fact]
    public void Chips_PlaylistsFirst_HiddenKindsAbsent()
    {
        var chips = new List<SidebarLibraryFilter>();
        SidebarLibraryHeadRules.Chips(SidebarLibraryKinds.None, chips);
        Assert.Equal(new[]
        {
            SidebarLibraryFilter.Playlists, SidebarLibraryFilter.Albums, SidebarLibraryFilter.Artists,
            SidebarLibraryFilter.Podcasts, SidebarLibraryFilter.Audiobooks,
        }, chips);

        SidebarLibraryHeadRules.Chips(SidebarLibraryKinds.Albums | SidebarLibraryKinds.Podcasts, chips);
        Assert.Equal(new[] { SidebarLibraryFilter.Playlists, SidebarLibraryFilter.Artists, SidebarLibraryFilter.Audiobooks }, chips);
    }

    [Fact]
    public void Toggle_SameClears_OtherReplaces()
    {
        Assert.Equal(SidebarLibraryFilter.None, SidebarLibraryHeadRules.Toggle(SidebarLibraryFilter.Albums, SidebarLibraryFilter.Albums));
        Assert.Equal(SidebarLibraryFilter.Artists, SidebarLibraryHeadRules.Toggle(SidebarLibraryFilter.Albums, SidebarLibraryFilter.Artists));
    }

    [Fact]
    public void Escape_ClearThenClose()
    {
        Assert.Equal(SidebarLibraryHeadRules.SearchEscape.Clear, SidebarLibraryHeadRules.OnEscape("blue"));
        Assert.Equal(SidebarLibraryHeadRules.SearchEscape.Close, SidebarLibraryHeadRules.OnEscape(""));
    }

    [Fact]
    public void Blur_ClosesOnlyEmpty()
    {
        Assert.True(SidebarLibraryHeadRules.ClosesOnBlur(""));
        Assert.False(SidebarLibraryHeadRules.ClosesOnBlur("blue"));
    }

    [Fact]
    public void CustomOrder_OnlyUnderPlaylists_EffectiveFallsBackToRecents()
    {
        Assert.True(SidebarLibraryHeadRules.CustomOrderOffered(SidebarLibraryFilter.Playlists));
        Assert.False(SidebarLibraryHeadRules.CustomOrderOffered(SidebarLibraryFilter.None));
        Assert.False(SidebarLibraryHeadRules.CustomOrderOffered(SidebarLibraryFilter.Albums));

        Assert.Equal(SidebarLibrarySort.CustomOrder, SidebarLibraryHeadRules.Effective(SidebarLibrarySort.CustomOrder, SidebarLibraryFilter.Playlists));
        Assert.Equal(SidebarLibrarySort.Recents, SidebarLibraryHeadRules.Effective(SidebarLibrarySort.CustomOrder, SidebarLibraryFilter.Albums));
        Assert.Equal(SidebarLibrarySort.Alphabetical, SidebarLibraryHeadRules.Effective(SidebarLibrarySort.Alphabetical, SidebarLibraryFilter.Albums));
    }

    [Fact]
    public void GridColumns_Clamped()
    {
        Assert.Equal(2, SidebarLibraryHeadRules.GridColumns(240f));
        Assert.Equal(2, SidebarLibraryHeadRules.GridColumns(300f));
        Assert.Equal(3, SidebarLibraryHeadRules.GridColumns(420f));
        Assert.Equal(4, SidebarLibraryHeadRules.GridColumns(560f));
        Assert.Equal(4, SidebarLibraryHeadRules.GridColumns(900f));
        Assert.Equal(2, SidebarLibraryHeadRules.GridColumns(float.NaN));
    }

    [Fact]
    public void Ring_HomeDropdownList()
    {
        // Down from Home lands on the dropdown; Down from the dropdown goes to the list only when the list has rows.
        Assert.Equal(SidebarLibraryHeadRules.HeadStop.Dropdown, SidebarLibraryHeadRules.Next(SidebarLibraryHeadRules.HeadStop.Home, 1, 0));
        Assert.Equal(SidebarLibraryHeadRules.HeadStop.Dropdown, SidebarLibraryHeadRules.Next(SidebarLibraryHeadRules.HeadStop.Dropdown, 1, 0));
        Assert.Equal(SidebarLibraryHeadRules.HeadStop.List, SidebarLibraryHeadRules.Next(SidebarLibraryHeadRules.HeadStop.Dropdown, 1, 3));
        Assert.Equal(SidebarLibraryHeadRules.HeadStop.Dropdown, SidebarLibraryHeadRules.Next(SidebarLibraryHeadRules.HeadStop.List, -1, 3));
        Assert.Equal(SidebarLibraryHeadRules.HeadStop.Home, SidebarLibraryHeadRules.Next(SidebarLibraryHeadRules.HeadStop.Home, -1, 3));
    }

    [Fact]
    public void TabOrder_MatchesScreen()
    {
        var order = new List<SidebarLibraryHeadRules.HeadTabStop>();
        SidebarLibraryHeadRules.TabOrder(SidebarLibraryHeadRules.ToolbarShape.Full, searching: false, order);
        Assert.Equal(new[]
        {
            SidebarLibraryHeadRules.HeadTabStop.Home, SidebarLibraryHeadRules.HeadTabStop.Dropdown,
            SidebarLibraryHeadRules.HeadTabStop.Chips, SidebarLibraryHeadRules.HeadTabStop.Search,
            SidebarLibraryHeadRules.HeadTabStop.Sort, SidebarLibraryHeadRules.HeadTabStop.ListView,
            SidebarLibraryHeadRules.HeadTabStop.GridView, SidebarLibraryHeadRules.HeadTabStop.Create,
            SidebarLibraryHeadRules.HeadTabStop.More, SidebarLibraryHeadRules.HeadTabStop.List,
        }, order);
        // Folded (< 240): the view toggles leave; ⋯ stays the last toolbar stop.
        SidebarLibraryHeadRules.TabOrder(SidebarLibraryHeadRules.ToolbarShape.Folded, false, order);
        Assert.DoesNotContain(SidebarLibraryHeadRules.HeadTabStop.ListView, order);
        Assert.Equal(SidebarLibraryHeadRules.HeadTabStop.More, order[^2]);
        // Searching: the box replaces the search button and the right-hand group; sort stays after it.
        SidebarLibraryHeadRules.TabOrder(SidebarLibraryHeadRules.ToolbarShape.Full, searching: true, order);
        Assert.Equal(new[]
        {
            SidebarLibraryHeadRules.HeadTabStop.Home, SidebarLibraryHeadRules.HeadTabStop.Dropdown,
            SidebarLibraryHeadRules.HeadTabStop.Chips, SidebarLibraryHeadRules.HeadTabStop.SearchBox,
            SidebarLibraryHeadRules.HeadTabStop.Sort, SidebarLibraryHeadRules.HeadTabStop.List,
        }, order);
    }

    [Fact]
    public void Toolbar_FitsAt180_IconsOnlyBelow240()
    {
        string[] labels = ["Recents", "Recently added", "Alphabetical", "Creator", "Custom order"];
        foreach (float w in new[] { 180f, 200f, 239f, 240f, 260f, 300f, 320f, 400f, 460f })
            foreach (string label in labels)
            {
                var shape = SidebarLibraryHeadRules.ShapeOf(w, label);
                Assert.True(SidebarLibraryHeadRules.ToolbarWidth(shape, label) <= SidebarLibraryHeadRules.ToolbarLane(w),
                            $"{shape} overflows at {w} with '{label}'");
            }
        Assert.Equal(SidebarLibraryHeadRules.ToolbarShape.Folded, SidebarLibraryHeadRules.ShapeOf(180f, "Recents"));
        Assert.Equal(SidebarLibraryHeadRules.ToolbarShape.Folded, SidebarLibraryHeadRules.ShapeOf(239f, "Recents"));
        Assert.Equal(SidebarLibraryHeadRules.ToolbarShape.IconSort, SidebarLibraryHeadRules.ShapeOf(320f, "Recently added"));
        Assert.Equal(SidebarLibraryHeadRules.ToolbarShape.Full, SidebarLibraryHeadRules.ShapeOf(460f, "Recents"));
        Assert.Equal(SidebarLibraryHeadRules.ToolbarShape.Full, SidebarLibraryHeadRules.ShapeOf(320f, "Recents"));
        Assert.Equal(SidebarLibraryHeadRules.ToolbarShape.Folded, SidebarLibraryHeadRules.ShapeOf(float.NaN, "Recents"));
        Assert.Equal(164f, SidebarLibraryHeadRules.ToolbarLane(180f));
    }

    static readonly SidebarLibraryOptions LikedShown = SidebarLibraryOptions.Default;                 // ShowLiked true
    static readonly SidebarLibraryOptions LikedHidden = SidebarLibraryOptions.Default with { ShowLiked = false };

    [Fact]
    public void Empty_AlbumsChipWithNoAlbums_IsFilter_NeverLibrary()
    {
        // The Albums chip hides Liked and route pins; no albums ⇒ the ONE message is "No albums · Clear filter".
        int rows = SidebarLibraryEmptyRules.ScrollerRows([SidebarLibraryEntry.ForRoute("search", "Search")], pinnedShown: true,
            LikedShown, SidebarLibraryFilter.Albums, search: null, likedTitle: "Liked Songs", listCount: 0);
        Assert.Equal(0, rows);
        Assert.Equal(SidebarLibraryEmpty.Filter,
            SidebarLibraryEmptyRules.Of(rows, LoadState.Ready, anyPending: false, SidebarLibraryFilter.Albums, searching: false));
    }

    [Fact]
    public void Empty_EmptyLibraryWithLiked_IsNone()
    {
        // An empty library still shows Liked Songs under no chip: no "Your library is empty" above it.
        int rows = SidebarLibraryEmptyRules.ScrollerRows(null, pinnedShown: true, LikedShown, SidebarLibraryFilter.None,
            null, "Liked Songs", 0);
        Assert.Equal(1, rows);
        Assert.Equal(SidebarLibraryEmpty.None,
            SidebarLibraryEmptyRules.Of(rows, LoadState.Ready, false, SidebarLibraryFilter.None, false));
        int none = SidebarLibraryEmptyRules.ScrollerRows(null, true, LikedHidden, SidebarLibraryFilter.None, null, "Liked Songs", 0);
        Assert.Equal(SidebarLibraryEmpty.Library,
            SidebarLibraryEmptyRules.Of(none, LoadState.Ready, false, SidebarLibraryFilter.None, false));
    }

    [Fact]
    public void Empty_Priority_PendingFailedSearchFilter()
    {
        Assert.Equal(SidebarLibraryEmpty.None, SidebarLibraryEmptyRules.Of(0, LoadState.Pending, false, SidebarLibraryFilter.None, true));
        Assert.Equal(SidebarLibraryEmpty.None, SidebarLibraryEmptyRules.Of(0, LoadState.Ready, true, SidebarLibraryFilter.None, true));
        Assert.Equal(SidebarLibraryEmpty.Failed, SidebarLibraryEmptyRules.Of(0, LoadState.Failed, false, SidebarLibraryFilter.Albums, true));
        Assert.Equal(SidebarLibraryEmpty.Search, SidebarLibraryEmptyRules.Of(0, LoadState.Ready, false, SidebarLibraryFilter.Albums, true));
        Assert.Equal(SidebarLibraryEmpty.None, SidebarLibraryEmptyRules.Of(3, LoadState.Failed, false, SidebarLibraryFilter.None, false));
    }

    [Fact]
    public void Empty_DrilledEmptyFolder_IsLibrary_PinsAndLikedNotCounted()
    {
        // Drilled into an empty folder: the pins and Liked Songs are not on this level (PlanLibrary under Drilled plans
        // neither), so the head says the level is empty instead of counting rows that are not drawn.
        var pins = new[] { SidebarLibraryEntry.ForRoute("search", "Search") };
        int rows = SidebarLibraryEmptyRules.ScrollerRows(pins, pinnedShown: true, LikedShown, SidebarLibraryFilter.None,
            search: null, likedTitle: "Liked Songs", listCount: 0, drilled: true);
        Assert.Equal(0, rows);
        Assert.Equal(SidebarLibraryEmpty.Library,
            SidebarLibraryEmptyRules.Of(rows, LoadState.Ready, false, SidebarLibraryFilter.None, false));
        // The same inputs, not drilled: the route pin and Liked Songs are rows.
        Assert.Equal(2, SidebarLibraryEmptyRules.ScrollerRows(pins, true, LikedShown, SidebarLibraryFilter.None, null,
            "Liked Songs", 0));
    }
}
