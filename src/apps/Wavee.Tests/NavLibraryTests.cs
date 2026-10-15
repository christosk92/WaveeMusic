// ── Wavee.Tests/NavLibraryTests.cs — Your Library as model state and verbs (Shell/NavModel.Library.cs, themes plan §5.1 row C10) ──
//
// One mount's `NavLibrary` session, opened from a NavModel over a REAL SidebarProjectionBinder: the drill stack (ids AND names,
// the expand-on-enter rule, the version bump), the folder-activation rule (grid → list and open, narrow → drill, wide → toggle),
// the chip commit (a chip change leaves the drill; "clear filter" does not), the Esc rule and the blur rule of the search box,
// the sort and view verbs, the reorder rule, the shaped input, and the empty-state facts the head draws from.
//
// The Library layout is made active for the fact (only it shapes the published list by the chip and the search) and put back;
// the process-wide `Sidebar` statics a fact touches are restored in Dispose. No engine loop, no window, no network.

using System;
using System.Collections.Generic;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class NavLibraryTests : IDisposable
{
    sealed class NavLogs : ISidebarRecencyLogs
    {
        public int HistoryVersion => 0;
        public IReadOnlyList<Shell.HistoryEntry> History { get; } = new List<Shell.HistoryEntry>();
        public int PlayLogVersion => 0;
        public IReadOnlyList<Shell.PlayEntry> Plays { get; } = new List<Shell.PlayEntry>();
        public IReadOnlyDictionary<string, long> LastPlayed { get; } = new Dictionary<string, long>();
    }

    static readonly string[] Folders = ["aa", "bb", "cc"];

    readonly SidebarLayoutId _layout;
    readonly User _me;

    public NavLibraryTests()
    {
        TestScope.Fresh();
        _me = SidebarWiringStage.Me();
        _layout = Sidebar.Layout.Peek();
        Sidebar.SwitchLayout(SidebarLayoutId.Library);          // only Library shapes the published list by the chip and the search
        Sidebar.SetLibraryFilter(SidebarLibraryFilter.None);
        Sidebar.LibrarySearch.SetIfChanged("");
        Sidebar.LibrarySearchOpen.SetIfChanged(false);
        Sidebar.Pins.LoadFrom(null);
    }

    public void Dispose()
    {
        Sidebar.SetLibraryFilter(SidebarLibraryFilter.None);
        Sidebar.LibrarySearch.SetIfChanged("");
        Sidebar.LibrarySearchOpen.SetIfChanged(false);
        foreach (string folder in Folders) Sidebar.SetFolderExpanded(folder, false);
        Sidebar.FlushPendingCommit();
        Sidebar.ResetLayoutRecorded(SidebarLayoutId.Library, null);     // the overlay AND the view (the theme's state, which outlives the overlay)
        Sidebar.SwitchLayout(_layout);
        Sidebar.Pins.LoadFrom(null);
    }

    NavModel NewModel() => new(new SidebarProjectionBinder(new NavLogs(), peek: null));

    void AnswerEmpty()
    {
        _me.ReplaceRootlist(Array.Empty<int>(), Array.Empty<RootlistEdge>());
        _me.Replace(LibraryEdgeKind.SavedAlbums, Array.Empty<int>(), Array.Empty<LibraryEdge>(), EdgeState.Complete, 0);
        _me.Replace(LibraryEdgeKind.FollowedArtists, Array.Empty<int>(), Array.Empty<LibraryEdge>(), EdgeState.Complete, 0);
        _me.Replace(LibraryEdgeKind.SavedShows, Array.Empty<int>(), Array.Empty<LibraryEdge>(), EdgeState.Complete, 0);
    }

    // ── the drill stack ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Drilling_pushes_a_named_level_expands_the_folder_and_bumps_the_version()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);
        Assert.False(lib.DrillActive);
        Assert.Equal("", lib.CurrentFolderId);
        Assert.Equal(Loc.Get(Strings.Sidebar.V3.Title), lib.CurrentFolderName);   // the root level is the library itself
        int version = lib.DrillVersion.Peek();

        lib.Drill("aa", "Mixes");
        lib.Drill("bb", "Road trips");

        Assert.True(lib.DrillActive);
        Assert.Equal("bb", lib.CurrentFolderId);
        Assert.Equal("Road trips", lib.CurrentFolderName);
        Assert.Equal("Mixes", lib.ParentName);
        Assert.Equal(version + 2, lib.DrillVersion.Peek());
        // The projection omits a collapsed folder's children, so entering one opens it.
        Assert.True(Sidebar.IsFolderExpanded("aa"));
        Assert.True(Sidebar.IsFolderExpanded("bb"));
    }

    [Fact]
    public void Back_pops_one_level_and_the_first_level_returns_to_the_library()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);
        lib.Drill("aa", "Mixes");
        lib.Drill("bb", "Road trips");

        lib.Back();
        Assert.Equal("aa", lib.CurrentFolderId);
        Assert.Equal("Mixes", lib.CurrentFolderName);
        Assert.Equal(Loc.Get(Strings.Sidebar.V3.Title), lib.ParentName);

        lib.Back();
        Assert.False(lib.DrillActive);

        int version = lib.DrillVersion.Peek();
        lib.Back();                                        // nothing to pop: no bump
        Assert.Equal(version, lib.DrillVersion.Peek());
    }

    [Fact]
    public void A_nameless_or_idless_drill_is_labelled_sanely_or_refused()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);
        int version = lib.DrillVersion.Peek();

        lib.Drill("", "x");
        lib.Drill(null, "x");
        Assert.False(lib.DrillActive);
        Assert.Equal(version, lib.DrillVersion.Peek());

        lib.Drill("cc", null);
        Assert.Equal(Loc.Get(Strings.Sidebar.V3.Kind.Folder), lib.CurrentFolderName);
    }

    [Fact]
    public void Reset_leaves_every_level_at_once_and_bumps_only_when_it_left_something()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);
        lib.Drill("aa", "A");
        lib.Drill("bb", "B");

        lib.ResetDrill();
        Assert.False(lib.DrillActive);

        int version = lib.DrillVersion.Peek();
        lib.ResetDrill();
        Assert.Equal(version, lib.DrillVersion.Peek());
    }

    // ── narrow and wide, and what a folder row does ────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_a_wide_list_discloses_folders_in_place()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);
        Assert.True(lib.DisclosesFoldersInline());

        lib.SetNarrow(true);
        Assert.False(lib.DisclosesFoldersInline());

        lib.SetNarrow(false);
        Sidebar.SetLibraryView(SidebarLibraryView.Grid);
        Assert.False(lib.DisclosesFoldersInline());        // a grid cannot disclose
    }

    [Fact]
    public void Widening_the_pane_leaves_a_drilled_folder()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);
        lib.SetNarrow(true);
        lib.Drill("aa", "A");
        Assert.True(lib.DrillActive);

        lib.SetNarrow(false);
        Assert.False(lib.DrillActive);
    }

    [Fact]
    public void A_folder_row_toggles_in_a_wide_list_drills_in_a_narrow_pane_and_opens_the_list_from_a_grid()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);

        // wide list: the inline toggle
        lib.ActivateFolder("aa", "A");
        Assert.True(Sidebar.IsFolderExpanded("aa"));
        lib.ActivateFolder("aa", "A");
        Assert.False(Sidebar.IsFolderExpanded("aa"));
        Assert.False(lib.DrillActive);

        // narrow: drill
        lib.SetNarrow(true);
        lib.ActivateFolder("bb", "B");
        Assert.True(lib.DrillActive);
        Assert.Equal("bb", lib.CurrentFolderId);
        lib.SetNarrow(false);

        // grid: switch to the list and open it, no drill
        Sidebar.SetLibraryView(SidebarLibraryView.Grid);
        lib.ActivateFolder("cc", "C");
        Assert.Equal(SidebarLibraryView.List, Sidebar.Doc.Library.View);
        Assert.True(Sidebar.IsFolderExpanded("cc"));
        Assert.False(lib.DrillActive);
    }

    [Fact]
    public void An_empty_folder_id_activates_nothing()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);
        lib.ActivateFolder("", "x");
        Assert.False(lib.DrillActive);
        Assert.False(Sidebar.IsFolderExpanded(""));
    }

    // ── chips, sort, view ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_chip_commit_toggles_the_filter_and_leaves_any_drilled_folder()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);
        lib.Drill("aa", "A");

        lib.ToggleChip(SidebarLibraryFilter.Albums);
        Assert.Equal(SidebarLibraryFilter.Albums, Sidebar.LibraryFilter.Peek());
        Assert.False(lib.DrillActive);

        lib.ToggleChip(SidebarLibraryFilter.Albums);        // a second click on the active chip clears it
        Assert.Equal(SidebarLibraryFilter.None, Sidebar.LibraryFilter.Peek());

        lib.SetFilter(SidebarLibraryFilter.Playlists);
        Assert.Equal(SidebarLibraryFilter.Playlists, Sidebar.LibraryFilter.Peek());
    }

    [Fact]
    public void Clearing_the_filter_from_the_empty_state_keeps_the_drilled_folder()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);
        Sidebar.SetLibraryFilter(SidebarLibraryFilter.Playlists);
        lib.Drill("aa", "A");

        lib.ClearFilter();

        Assert.Equal(SidebarLibraryFilter.None, Sidebar.LibraryFilter.Peek());
        Assert.True(lib.DrillActive);
    }

    [Fact]
    public void Sort_and_view_go_through_the_one_layout_op()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);

        lib.SetSort(SidebarLibrarySort.Alphabetical, true);
        Assert.Equal(SidebarLibrarySort.Alphabetical, Sidebar.Doc.Library.Sort);
        Assert.True(Sidebar.Doc.Library.Descending);

        lib.SetView(SidebarLibraryView.Grid);
        Assert.Equal(SidebarLibraryView.Grid, Sidebar.Doc.Library.View);
    }

    [Fact]
    public void Only_Playlists_in_custom_order_allows_a_positional_reorder()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);
        Assert.True(lib.TreeSortedNonCustom());               // Recents, every kind: refuses positional drops

        lib.ShowCustomOrder();                               // the refusal toast's action
        Assert.Equal(SidebarLibraryFilter.Playlists, Sidebar.LibraryFilter.Peek());
        Assert.Equal(SidebarLibrarySort.CustomOrder, Sidebar.Doc.Library.Sort);
        Assert.False(lib.TreeSortedNonCustom());

        Sidebar.LibrarySearch.SetIfChanged("mix");           // a query flattens the tree
        Assert.True(lib.TreeSortedNonCustom());
        Sidebar.LibrarySearch.SetIfChanged("");
        Assert.False(lib.TreeSortedNonCustom());

        lib.Drill("aa", "A");                                // a folder level is one folder's children
        Assert.True(lib.TreeSortedNonCustom());
    }

    // ── find: open, Esc, blur, close ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Find_opens_the_box_holding_the_query()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);
        lib.Find("daft");
        Assert.True(Sidebar.LibrarySearchOpen.Peek());
        Assert.Equal("daft", Sidebar.LibrarySearch.Peek());
    }

    [Fact]
    public void Esc_clears_a_typed_query_first_and_closes_an_empty_box_after_the_latch()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);
        lib.Find("daft");
        int latched = 0;

        var first = lib.Escape(() => latched++);
        Assert.Equal(SidebarLibraryHeadRules.SearchEscape.Clear, first);
        Assert.Equal("", Sidebar.LibrarySearch.Peek());
        Assert.True(Sidebar.LibrarySearchOpen.Peek());       // the box stays open for the next query
        Assert.Equal(0, latched);

        var second = lib.Escape(() => latched++);
        Assert.Equal(SidebarLibraryHeadRules.SearchEscape.Close, second);
        Assert.False(Sidebar.LibrarySearchOpen.Peek());
        Assert.Equal(1, latched);                            // the view's "magnifier takes focus" latch ran once, before the close
    }

    [Fact]
    public void Losing_focus_closes_an_empty_box_and_keeps_one_with_text()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);
        lib.Find("daft");
        lib.SearchBlurred();
        Assert.True(Sidebar.LibrarySearchOpen.Peek());
        Assert.Equal("daft", Sidebar.LibrarySearch.Peek());

        lib.ClearSearch();
        lib.SearchBlurred();
        Assert.False(Sidebar.LibrarySearchOpen.Peek());
    }

    [Fact]
    public void Closing_the_box_clears_the_text_too()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);
        lib.Find("daft");
        lib.CloseSearch();
        Assert.False(Sidebar.LibrarySearchOpen.Peek());
        Assert.Equal("", Sidebar.LibrarySearch.Peek());
    }

    // ── the shaped input and the state ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_view_state_folds_the_chip_the_search_the_drill_and_the_columns()
    {
        var lib = NewModel().OpenLibrary(inDrawer: false);
        lib.Columns = new Signal<int>(3);

        var plain = lib.ReadState();
        Assert.Equal(SidebarLibraryFilter.None, plain.Filter);
        Assert.Equal(3, plain.GridColumns);
        Assert.False(plain.Searching);
        Assert.False(plain.Drilled);
        Assert.False(plain.HasPins);

        Sidebar.SetLibraryFilter(SidebarLibraryFilter.Playlists);
        Sidebar.LibrarySearch.SetIfChanged("mix");
        lib.Drill("aa", "A");
        var busy = lib.ReadState();
        Assert.Equal(SidebarLibraryFilter.Playlists, busy.Filter);
        Assert.True(busy.Searching);
        Assert.Equal("aa", busy.DrillFolderId);
        Assert.NotEqual(plain.GetHashCode(), lib.ReadModeEpoch());   // the mode epoch re-plans the pane on any of them

        var options = lib.ShapeOptions(new SidebarPlanOptions());
        Assert.Equal(SidebarLibraryFilter.Playlists, options.Filter);
        Assert.Equal(3, options.GridColumns);
        Assert.True(options.Drilled);
        Assert.False(options.FoldersInline);                         // a drilled level has nothing to disclose
    }

    [Fact]
    public void The_shaped_input_carries_the_list_in_tree_order_and_is_rebuilt_only_when_something_moved()
    {
        var one = SidebarWiringStage.StagePlaylist("spotify:playlist:one", "One");
        var two = SidebarWiringStage.StagePlaylist("spotify:playlist:two", "Two");
        SidebarWiringStage.SetRootlist(_me, one, two);
        _me.Replace(LibraryEdgeKind.SavedAlbums, Array.Empty<int>(), Array.Empty<LibraryEdge>(), EdgeState.Complete, 0);
        _me.Replace(LibraryEdgeKind.FollowedArtists, Array.Empty<int>(), Array.Empty<LibraryEdge>(), EdgeState.Complete, 0);
        _me.Replace(LibraryEdgeKind.SavedShows, Array.Empty<int>(), Array.Empty<LibraryEdge>(), EdgeState.Complete, 0);
        var model = NewModel();
        var lib = model.OpenLibrary(inDrawer: false);
        model.Start();

        var shaped = lib.ShapeInput(model.PlannerInput());
        Assert.Equal(2, shaped.Library!.Count);
        Assert.True(shaped.LibraryIsTree);                           // every kind, a list view, no search, no drill: folders group

        var again = lib.ShapeInput(model.PlannerInput());
        Assert.Same(shaped.Library, again.Library);                  // the same epoch: no re-bucket
    }

    // ── the empty-state facts ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_library_nothing_has_answered_shows_no_empty_state()
    {
        var model = NewModel();
        var lib = model.OpenLibrary(inDrawer: false);
        model.Start();

        var facts = lib.ReadFacts();

        Assert.Equal(LoadState.Pending, facts.Load);
        Assert.True(facts.AnyPending);
        Assert.Equal(SidebarLibraryEmpty.None, facts.Empty);          // skeletons, never an empty state
    }

    [Fact]
    public void An_answered_empty_library_says_so_and_a_chip_or_a_query_says_why_nothing_is_shown()
    {
        AnswerEmpty();
        Sidebar.Dispatch(new SetShowLiked(false));                   // Liked Songs is a row of its own: hide it so the scroller is empty
        var model = NewModel();
        var lib = model.OpenLibrary(inDrawer: false);
        model.Start();

        var facts = lib.ReadFacts();
        Assert.Equal(SidebarLibraryFilter.None, facts.Filter);
        Assert.Equal(LoadState.Ready, facts.Load);
        Assert.False(facts.AnyPending);
        Assert.Equal(0, facts.Rows);
        Assert.Null(facts.ShapedSearch);
        Assert.Equal(SidebarLibraryEmpty.Library, facts.Empty);
        Assert.False(lib.FailedEmpty);

        Sidebar.SetLibraryFilter(SidebarLibraryFilter.Playlists);
        Assert.Equal(SidebarLibraryEmpty.Filter, lib.ReadFacts().Empty);

        Sidebar.SetLibraryFilter(SidebarLibraryFilter.None);
        Sidebar.LibrarySearch.SetIfChanged("zzz");
        lib.Resync();                                                // the binder shapes by the query the view settled on
        var searching = lib.ReadFacts();
        Assert.Equal("zzz", searching.ShapedSearch);
        Assert.Equal(SidebarLibraryEmpty.Search, searching.Empty);
    }

    [Fact]
    public void The_liked_row_keeps_a_library_with_nothing_else_from_reading_as_empty()
    {
        AnswerEmpty();
        var model = NewModel();
        var lib = model.OpenLibrary(inDrawer: false);
        model.Start();

        var facts = lib.ReadFacts();

        Assert.Equal(1, facts.Rows);                                 // Liked Songs, shown by default
        Assert.Equal(SidebarLibraryEmpty.None, facts.Empty);
    }

    // ── the width ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_presented_width_is_the_drawers_overlay_width_or_the_docked_panes_own()
    {
        Assert.Equal(SidebarPaneModeRules.OverlayWidth(Sidebar.Width.Peek(), 500f), NavLibrary.PresentedWidth(true, 500f));
        Assert.Equal(Sidebar.PresentedWidth.Peek(), NavLibrary.PresentedWidth(false, 500f));
    }

    [Fact]
    public void The_search_debounce_is_the_binder_pumps_ninety_milliseconds()
        => Assert.Equal(90f, NavLibrary.SearchDebounceMs);
}
