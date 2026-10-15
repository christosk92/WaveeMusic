// ── Wavee.Tests/NavModelTests.cs — the navigation pane's core model (Shell/NavModel*.cs, themes plan §5.1 row C9) ──────
//
// Three groups, each self-contained:
//
//   READINESS RULES     NavReadinessRules / NavReadinessFacts: edge state in, readiness out, the planner's two-state
//                       projection of it. PURE.
//   PROJECTION PARITY   The model over a REAL SidebarProjectionBinder on six fixture libraries (empty, small, folders,
//                       many pins, never loaded, failed load): every typed read the model offers agrees with what the
//                       binder publishes, the literal values are the ones today's binder produces, and a failed ask is
//                       published as Failed on the model while the planner (and so every pane) still sees today's Pending.
//   VERBS               The verbs on the model with the process-wide Sidebar service booted against a temp profile
//                       folder: the account facts, pin / unpin / move through the undo ring, a section toggle, folder
//                       expansion, the create verbs and their refusal, play, and the drag-peek write.
//
// The projection facts stage real rows through `SidebarWiringStage` (SidebarWiringTests.cs) and write real edges, exactly
// like SidebarProjectionTests: no mocked store, no engine loop, no window, no network. The classes that touch `Entities`
// or the `Sidebar` statics join EntitiesCollection (no parallelism) and put back what they change.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FluentGpu.Localization;
using Wavee;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── READINESS RULES ────────────────────────────────────────────────────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
public class NavReadinessRulesTests
{
    [Theory]
    [InlineData(EdgeState.Unknown, NavReadiness.Pending)]
    [InlineData(EdgeState.Partial, NavReadiness.Ready)]
    [InlineData(EdgeState.Complete, NavReadiness.Ready)]
    [InlineData(EdgeState.Failed, NavReadiness.Failed)]
    public void An_edge_state_maps_to_its_readiness(EdgeState state, NavReadiness expected)
        => Assert.Equal(expected, NavReadinessRules.Of(state));

    [Theory]
    [InlineData(NavReadiness.Ready, NavReadiness.Ready, NavReadiness.Ready)]
    [InlineData(NavReadiness.Ready, NavReadiness.Pending, NavReadiness.Pending)]
    [InlineData(NavReadiness.Pending, NavReadiness.Ready, NavReadiness.Pending)]
    [InlineData(NavReadiness.Pending, NavReadiness.Pending, NavReadiness.Pending)]
    [InlineData(NavReadiness.Ready, NavReadiness.Failed, NavReadiness.Failed)]
    [InlineData(NavReadiness.Failed, NavReadiness.Ready, NavReadiness.Failed)]
    [InlineData(NavReadiness.Pending, NavReadiness.Failed, NavReadiness.Failed)]
    [InlineData(NavReadiness.Failed, NavReadiness.Pending, NavReadiness.Failed)]
    [InlineData(NavReadiness.Failed, NavReadiness.Failed, NavReadiness.Failed)]
    public void The_worse_of_two_readinesses_wins(NavReadiness a, NavReadiness b, NavReadiness expected)
        => Assert.Equal(expected, NavReadinessRules.Worst(a, b));

    [Theory]
    [InlineData(NavReadiness.Ready, SidebarSourceState.Ready)]
    [InlineData(NavReadiness.Pending, SidebarSourceState.Pending)]
    [InlineData(NavReadiness.Failed, SidebarSourceState.Error)]   // S10: a failed source plans no skeleton (the head draws it)
    public void The_planner_reads_the_readiness_as_ready_pending_or_error(NavReadiness readiness, SidebarSourceState expected)
        => Assert.Equal(expected, NavReadinessRules.ToPlanner(readiness));

    [Fact]
    public void Nothing_asked_is_pending_both_ways_and_a_failed_source_marks_the_pair()
    {
        Assert.Equal(new NavReadinessFacts(NavReadiness.Pending, NavReadiness.Pending), NavReadinessFacts.Pending);
        Assert.Equal(NavReadinessFacts.Pending, default(NavReadinessFacts));
        Assert.Equal(NavReadiness.Pending, NavReadinessRules.Worst(NavReadiness.Pending, NavReadiness.Pending));
        Assert.False(NavReadinessFacts.Pending.AnyFailed);

        var half = new NavReadinessFacts(NavReadiness.Ready, NavReadiness.Failed);
        Assert.True(half.AnyFailed);
        Assert.Equal(NavReadiness.Failed, NavReadinessRules.Worst(half.Tree, half.Library));
        Assert.Equal(NavReadiness.Ready, NavReadinessRules.Worst(NavReadiness.Ready, NavReadiness.Ready));
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── PROJECTION PARITY ──────────────────────────────────────────────────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[Collection(EntitiesCollection.Name)]
public sealed class NavModelProjectionTests : IDisposable
{
    sealed class NavLogs : ISidebarRecencyLogs
    {
        public int HistoryVersion => 0;
        public IReadOnlyList<Shell.HistoryEntry> History { get; } = new List<Shell.HistoryEntry>();
        public int PlayLogVersion => 0;
        public IReadOnlyList<Shell.PlayEntry> Plays { get; } = new List<Shell.PlayEntry>();
        public IReadOnlyDictionary<string, long> LastPlayed { get; } = new Dictionary<string, long>();
    }

    public NavModelProjectionTests() => Sidebar.Pins.LoadFrom(null);   // a pin another fact left behind is not this fixture's

    public void Dispose() => Sidebar.Pins.LoadFrom(null);

    static NavModel NewModel(out SidebarProjectionBinder binder)
    {
        binder = new SidebarProjectionBinder(new NavLogs(), peek: null);
        return new NavModel(binder);
    }

    static string[] Names(IReadOnlyList<SidebarLibraryEntry>? list)
    {
        if (list is null) return Array.Empty<string>();
        var names = new string[list.Count];
        for (int i = 0; i < names.Length; i++) names[i] = list[i].Name;
        return names;
    }

    static string PlaylistId(string uri) => SidebarPinId.PlaylistPrefix + uri;

    // Rootlist row builders: the same shapes SidebarProjectionTests stages. An AddedAt on every item, so no first-seen
    // stamp is minted and the binder never reaches the document commit.
    static (int Target, RootlistEdge Edge) Item(Playlist p, byte depth) =>
        (p.Slot, default(RootlistEdge) with { Depth = depth, Kind = (byte)RootlistKind.Item, AddedAt = 100 });

    static (int Target, RootlistEdge Edge) FolderStart(ushort position, byte depth, string name, string hex) =>
        (Table.None, default(RootlistEdge) with
        {
            Position = position, Depth = depth, Kind = (byte)RootlistKind.FolderStart,
            FolderName = Entities.Strings.Intern(name), FolderId = Entities.Strings.Intern(hex),
        });

    static (int Target, RootlistEdge Edge) FolderEnd(byte depth) =>
        (Table.None, default(RootlistEdge) with { Depth = depth, Kind = (byte)RootlistKind.FolderEnd });

    static void SetRootlist(User me, params (int Target, RootlistEdge Edge)[] rows)
    {
        var targets = new int[rows.Length];
        var payload = new RootlistEdge[rows.Length];
        for (int i = 0; i < rows.Length; i++) { targets[i] = rows[i].Target; payload[i] = rows[i].Edge; }
        me.ReplaceRootlist(targets, payload);
    }

    static void AnswerEmpty(User me)
    {
        me.ReplaceRootlist(Array.Empty<int>(), Array.Empty<RootlistEdge>());
        me.Replace(LibraryEdgeKind.SavedAlbums, Array.Empty<int>(), Array.Empty<LibraryEdge>(), EdgeState.Complete, 0);
        me.Replace(LibraryEdgeKind.FollowedArtists, Array.Empty<int>(), Array.Empty<LibraryEdge>(), EdgeState.Complete, 0);
        me.Replace(LibraryEdgeKind.SavedShows, Array.Empty<int>(), Array.Empty<LibraryEdge>(), EdgeState.Complete, 0);
    }

    /// <summary>Every typed read the model offers is the binder's published value, whatever the library holds.</summary>
    static void AssertWiredToTheBinder(NavModel model, SidebarProjectionBinder binder)
    {
        Assert.Equal(binder.Revision, model.Revision);
        Assert.Equal(binder.Counts, model.Counts);
        Assert.Same(binder.Entries, model.LibraryItems);
        Assert.Same(binder.InputVersion, model.InputVersion);
        Assert.Same(binder.Readiness, model.Readiness);
        Assert.Same(binder.CurrentInput.PlaylistTree, model.Rootlist);
        Assert.Same(binder.CurrentInput.Pins, model.PinRows);
        Assert.Same(binder.CurrentInput.Played, model.Feeds.Played);
        Assert.Same(binder.CurrentInput.NewReleases, model.Feeds.NewReleases);
        Assert.Equal(binder.CurrentInput, model.Projection);
        Assert.Same(binder.Entries.Current, model.LibraryItems.Current);
    }

    // ── fixture 1: an empty library, fully answered ────────────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_library_is_ready_and_known_to_be_empty()
    {
        TestScope.Fresh();
        var me = SidebarWiringStage.Me();
        AnswerEmpty(me);
        var model = NewModel(out var binder);
        model.Start();

        AssertWiredToTheBinder(model, binder);
        Assert.Equal(new NavReadinessFacts(NavReadiness.Ready, NavReadiness.Ready), model.Readiness.Peek());
        Assert.Empty(model.Rootlist!);
        Assert.False(model.RootlistLoaded);              // "loaded" needs something in the tree
        Assert.Empty(model.PinRows!);
        Assert.Empty(model.Feeds.Played!);
        Assert.Empty(model.Feeds.NewReleases!);
        Assert.Null(model.RootlistMarkers);

        Assert.True(model.Counts.Known);
        Assert.Equal(0, model.Counts.Albums);    // a known zero, not "unknown"
        Assert.Equal(0, model.Counts.Audiobooks);
        Assert.Equal(SidebarSourceState.Ready, model.Projection.TreeState);
        Assert.Equal(SidebarSourceState.Ready, model.Projection.LibraryState);
    }

    // ── fixture 2: a small library ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_small_library_publishes_its_rootlist_counts_and_destinations()
    {
        TestScope.Fresh();
        var one = SidebarWiringStage.StagePlaylist("spotify:playlist:one", "One");
        var two = SidebarWiringStage.StagePlaylist("spotify:playlist:two", "Two");
        int a1 = SidebarWiringStage.StageAlbum("spotify:album:s1", "Album One");
        int a2 = SidebarWiringStage.StageAlbum("spotify:album:s2", "Album Two");
        int artist = SidebarWiringStage.StageArtist("spotify:artist:s1", "Artist");
        int podcast = SidebarWiringStage.StageShow("spotify:show:s1", "Podcast", audiobook: false);
        int book = SidebarWiringStage.StageShow("spotify:show:s2", "Audiobook", audiobook: true);
        var me = SidebarWiringStage.Me();
        SidebarWiringStage.SetRootlist(me, one, two);
        me.Replace(LibraryEdgeKind.SavedAlbums, new[] { a1, a2 }, SidebarWiringStage.Saved(2));
        me.Replace(LibraryEdgeKind.FollowedArtists, new[] { artist }, SidebarWiringStage.Saved(1));
        me.Replace(LibraryEdgeKind.SavedShows, new[] { podcast, book }, SidebarWiringStage.Saved(2));

        var model = NewModel(out var binder);
        model.Start();

        AssertWiredToTheBinder(model, binder);
        Assert.True(model.Revision > 0);
        Assert.Equal(new[] { "One", "Two" }, Names(model.Rootlist));
        Assert.True(model.RootlistLoaded);
        Assert.Equal(new NavReadinessFacts(NavReadiness.Ready, NavReadiness.Ready), model.Readiness.Peek());
        Assert.Equal(new SidebarLibraryCounts(Albums: 2, Artists: 1, Podcasts: 1, Audiobooks: 1, Known: true), model.Counts);

        // The planner input is the binder's, with Liked Songs' title, and honest once the binder has built.
        var planner = model.PlannerInput();
        Assert.Equal(SidebarSourceState.Ready, planner.TreeState);
        Assert.Equal(SidebarSourceState.Ready, planner.LibraryState);
        Assert.Same(model.Rootlist, planner.PlaylistTree);
    }

    // ── fixture 3: folders ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Folders_publish_their_tree_and_one_cached_marker_stream_per_revision()
    {
        TestScope.Fresh();
        var top = SidebarWiringStage.StagePlaylist("spotify:playlist:top", "Top");
        var in1 = SidebarWiringStage.StagePlaylist("spotify:playlist:in1", "Inner one");
        var deep = SidebarWiringStage.StagePlaylist("spotify:playlist:deep", "Deep");
        var me = SidebarWiringStage.Me();
        SetRootlist(me,
            Item(top, depth: 0),
            FolderStart(position: 1, depth: 0, name: "Outer", hex: "0000000000000001"),
            Item(in1, depth: 1),
            FolderStart(position: 2, depth: 1, name: "Inner", hex: "0000000000000002"),
            Item(deep, depth: 2),
            FolderEnd(depth: 1),
            FolderEnd(depth: 0));

        var model = NewModel(out var binder);
        model.Start();

        AssertWiredToTheBinder(model, binder);
        Assert.Equal(new[] { "Top", "Outer", "Inner one", "Inner", "Deep" }, Names(model.Rootlist));
        var outer = model.Rootlist![1];
        Assert.True(outer.IsFolder);
        Assert.Equal("0000000000000001", outer.FolderId);
        Assert.Equal(2, outer.ChildCount);               // "Inner one" and the "Inner" sub-folder
        Assert.True(model.RootlistLoaded);

        var markers = model.RootlistMarkers;
        Assert.NotNull(markers);
        Assert.NotEmpty(markers!);
        Assert.Same(markers, model.RootlistMarkers);      // cached per projection revision
    }

    // ── fixture 4: many pins ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Many_pins_resolve_in_pin_order_and_name_their_owning_section()
    {
        TestScope.Fresh();
        var playlists = new Playlist[12];
        for (int i = 0; i < playlists.Length; i++)
            playlists[i] = SidebarWiringStage.StagePlaylist("spotify:playlist:p" + i, "P" + i);
        var me = SidebarWiringStage.Me();
        SidebarWiringStage.SetRootlist(me, playlists);

        // Ten pins, newest first (p9 … p0): the pin store's order IS the render order.
        var pins = new List<SidebarPin>();
        for (int i = 9; i >= 0; i--)
            pins.Add(new SidebarPin(PlaylistId("spotify:playlist:p" + i), SidebarEntryKind.Playlist, "spotify:playlist:p" + i, "P" + i, 1));
        Sidebar.Pins.LoadFrom(pins);

        var model = NewModel(out var binder);
        model.Start();

        AssertWiredToTheBinder(model, binder);
        Assert.Equal(10, model.Pins.Count);
        var pinRows = model.PinRows!;
        Assert.Equal(10, pinRows.Count);
        for (int k = 0; k < 10; k++)
        {
            Assert.Equal(PlaylistId("spotify:playlist:p" + (9 - k)), pinRows[k].Id);
            Assert.True(pinRows[k].IsPinned);
            Assert.True(model.IsPinned(pinRows[k].Id));
        }
        Assert.False(model.IsPinned(PlaylistId("spotify:playlist:p11")));
        Assert.Equal(12, model.Rootlist!.Count);

        // The owning section of a route (the planner's answer since T5a, X26): Pinned for a pin, Playlists for the rest of the tree,
        // Collections for a page.
        var doc = SidebarLayoutRules.Resolve(SidebarLayoutState.Default, SidebarLayoutId.Classic, SidebarDensity.Default);
        var input = model.Projection;
        Assert.Equal("pinned", SidebarRowPlanner.OwningSection(doc, in input, PlaylistId("spotify:playlist:p3"), Sidebar.IsPinned));
        Assert.Equal("playlists", SidebarRowPlanner.OwningSection(doc, in input, PlaylistId("spotify:playlist:p11"), Sidebar.IsPinned));
        Assert.Equal("collections", SidebarRowPlanner.OwningSection(doc, in input, "albums", Sidebar.IsPinned));
        Assert.Null(SidebarRowPlanner.OwningSection(doc, in input, "", Sidebar.IsPinned));
        Assert.Null(SidebarRowPlanner.OwningSection(doc, in input, "no-such-route", Sidebar.IsPinned));
    }

    // ── fixture 5: never loaded (the account has nothing and nothing has answered) ────────────────────────────────

    [Fact]
    public void A_library_nothing_has_answered_is_pending_with_unknown_counts_and_a_skeleton_planner_input()
    {
        TestScope.Fresh();
        SidebarWiringStage.Me();   // a user with no relation answered yet
        var model = NewModel(out var binder);

        // Before the pump starts the binder: pending, and the planner input is honest skeleton.
        Assert.Equal(NavReadinessFacts.Pending, model.Readiness.Peek());
        var skeleton = model.PlannerInput();
        Assert.Equal(SidebarSourceState.Pending, skeleton.TreeState);
        Assert.Equal(SidebarSourceState.Pending, skeleton.LibraryState);
        Assert.Equal(Loc.Get("nav.likedSongs"), skeleton.LikedTitle);

        model.Start();
        AssertWiredToTheBinder(model, binder);
        Assert.Equal(NavReadinessFacts.Pending, model.Readiness.Peek());
        Assert.False(model.Counts.Known);
        Assert.False(model.RootlistLoaded);
        Assert.Equal(SidebarSourceState.Pending, model.Projection.TreeState);
    }

    // ── fixture 6: a failed load ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_failed_load_is_published_as_failed_and_the_planner_plans_no_skeleton()
    {
        TestScope.Fresh();
        var me = SidebarWiringStage.Me();
        var model = NewModel(out _);
        model.Start();
        Assert.Equal(NavReadinessFacts.Pending, model.Readiness.Peek());
        Assert.False(model.Sync());                       // nothing moved: one fold, no pass
        int inputVersion = model.InputVersion.Peek();

        var edges = Entities.Current.Edges;
        edges.Rootlist.MarkFailed(me.Slot, 0, 503);
        edges.SavedAlbums.MarkFailed(me.Slot, 0, 503);
        edges.FollowedArtists.MarkFailed(me.Slot, 0, EdgeTableBase.Transport);
        edges.SavedShows.MarkFailed(me.Slot, 0, 500);

        Assert.True(model.Sync());                        // a failure is a trigger
        Assert.Equal(new NavReadinessFacts(NavReadiness.Failed, NavReadiness.Failed), model.Readiness.Peek());
        Assert.True(model.Readiness.Peek().AnyFailed);

        // S10: the planner reads Error (no skeleton), the plan re-publishes, and Your Library's list carries Failed — a
        // failed kind is not a pending one — which its head draws as the Error vacancy with Retry.
        Assert.Equal(SidebarSourceState.Error, model.Projection.TreeState);
        Assert.Equal(SidebarSourceState.Error, model.Projection.LibraryState);
        Assert.NotEqual(inputVersion, model.InputVersion.Peek());
        Assert.Equal(FluentGpu.Signals.LoadState.Failed, model.LibraryItems.State);
        Assert.False(model.LibraryItems.AnyContributingKindPending);
        Assert.False(model.Counts.Known);

        // An answer recovers it.
        AnswerEmpty(me);
        Assert.True(model.Sync());
        Assert.Equal(new NavReadinessFacts(NavReadiness.Ready, NavReadiness.Ready), model.Readiness.Peek());
        Assert.Equal(SidebarSourceState.Ready, model.Projection.TreeState);
    }

    [Fact]
    public void One_failed_source_marks_only_its_half_of_the_pair()
    {
        TestScope.Fresh();
        var me = SidebarWiringStage.Me();
        var model = NewModel(out _);
        me.ReplaceRootlist(Array.Empty<int>(), Array.Empty<RootlistEdge>());   // the tree answered (empty)
        Entities.Current.Edges.SavedAlbums.MarkFailed(me.Slot, 0, 503);        // the library did not
        model.Start();

        Assert.Equal(new NavReadinessFacts(NavReadiness.Ready, NavReadiness.Failed), model.Readiness.Peek());
        Assert.Equal(NavReadiness.Failed, NavReadinessRules.Worst(model.Readiness.Peek().Tree, model.Readiness.Peek().Library));
        Assert.Equal(SidebarSourceState.Ready, model.Projection.TreeState);
        Assert.Equal(SidebarSourceState.Error, model.Projection.LibraryState);
    }

    // ── the selected route ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_selected_route_is_the_shells_current_route_key()
    {
        TestScope.Fresh();
        var model = NewModel(out _);
        Assert.Equal(Shell.NameOf(Shell.Current.Peek()), model.SelectedPeek);
        Assert.Equal(model.SelectedPeek, model.Selected);
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── VERBS ──────────────────────────────────────────────────────────────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
[Collection(EntitiesCollection.Name)]
public sealed class NavModelVerbTests : IDisposable
{
    const string PlaylistA = "spotify:playlist:37i9dQZF1DX4sWSpwq3LiO";
    const string PlaylistB = "spotify:playlist:1TSZDcvlPtAnekTaItI3qO";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-navmodel-tests", Guid.NewGuid().ToString("n"));
    readonly Action<SidebarPin, bool>? _bridge;
    readonly NavModel _model = new(new SidebarProjectionBinder(logs: null, peek: null));

    public NavModelVerbTests()
    {
        TestScope.Fresh();
        Sidebar.UseProfileDir(Path.Combine(_dir, "WaveeMusic"));
        Sidebar.Boot();                                   // the layout, the device file and the fake scope's (signed-out) account
        _bridge = Sidebar.Pins.OnLocalPinChanged;
        Sidebar.Pins.OnLocalPinChanged = null;            // a pin bridge another fact installed must not write a server from here
        Sidebar.Pins.LoadFrom(null);
    }

    public void Dispose()
    {
        Sidebar.Dispatch(new ResetLayout(SidebarLayoutId.Classic));
        Sidebar.EnsureAccount(CatalogScope.Fake());       // back to signed out: the account file lands, its pins unload
        Sidebar.Pins.OnLocalPinChanged = _bridge;
        Sidebar.Shutdown();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    static string[] Ids(SidebarPinStore pins)
    {
        var ids = new string[pins.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = pins[i].Id;
        return ids;
    }

    static string IdOf(string uri) => SidebarPinId.PlaylistPrefix + uri;

    static void SignIn() => Sidebar.EnsureAccount(new CatalogScope("spotify", "navmodel", "en-US", "US", 0, true));

    // ── the stored facts the model reads ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_model_reads_the_services_stored_facts_not_copies_of_them()
    {
        Assert.Same(Sidebar.Editing, _model.Editing);
        Assert.Same(Sidebar.DragPeek, _model.DragPeek);
        Assert.Same(Sidebar.PersistenceHealth, _model.PersistenceHealth);
        Assert.Same(Sidebar.Pins, _model.Pins);
        Assert.Same(Sidebar.Doc, _model.Layout);
        Assert.Same(Sidebar.Doc, _model.ReadLayout());
        Assert.Same(Sidebar.ExpandedFolders, _model.ExpandedFolders);
    }

    [Fact]
    public void The_fake_scope_is_signed_out_and_a_pin_has_nowhere_to_go()
    {
        Assert.True(_model.Account.SignedOut);
        Assert.Equal("", _model.Account.Key);

        Assert.False(_model.Pin(IdOf(PlaylistA), SidebarEntryKind.Playlist, PlaylistA, "A"));
        Assert.Empty(_model.Pins);
    }

    [Fact]
    public void A_signed_in_account_keeps_its_key()
    {
        SignIn();
        Assert.False(_model.Account.SignedOut);
        Assert.Equal("spotify:navmodel", _model.Account.Key);
    }

    // ── pin, unpin, move: through the undo ring ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pin_move_and_unpin_run_through_the_undo_ring()
    {
        SignIn();
        string a = IdOf(PlaylistA), b = IdOf(PlaylistB);

        Assert.True(_model.Pin(a, SidebarEntryKind.Playlist, PlaylistA, "A"));
        Assert.True(_model.Pin(b, SidebarEntryKind.Playlist, PlaylistB, "B"));
        Assert.False(_model.Pin(a, SidebarEntryKind.Playlist, PlaylistA, "A"));   // already pinned: a silent no-op
        Assert.Equal(new[] { a, b }, Ids(_model.Pins));

        _model.MovePin(0, 1);
        Assert.Equal(new[] { b, a }, Ids(_model.Pins));

        Assert.True(_model.Unpin(b, "B"));
        Assert.False(_model.IsPinned(b));
        Assert.True(_model.IsPinned(a));
        Assert.False(_model.Unpin(b, "B"));                                        // nothing left to unpin

        Sidebar.Undo();                                                            // the ring restores it at its former index
        Assert.Equal(new[] { b, a }, Ids(_model.Pins));
    }

    [Fact]
    public void A_fixed_route_is_never_pinned()
    {
        SignIn();
        Assert.False(_model.Pin("liked", SidebarEntryKind.AppRoute, "", "Liked Songs"));
        Assert.False(_model.Pin("", SidebarEntryKind.Playlist, "", ""));
        Assert.Empty(_model.Pins);
    }

    [Fact]
    public void A_pins_name_is_the_stored_one_else_the_routes_title()
    {
        SignIn();
        Assert.True(_model.Pin(IdOf(PlaylistA), SidebarEntryKind.Playlist, PlaylistA, "Morning Mix"));
        Assert.Equal("Morning Mix", _model.PinName(IdOf(PlaylistA)));
        // Not pinned: the route's own title, the same one the shell shows for it.
        Assert.Equal(Shell.Dest(Shell.Parse("search")).Title, _model.PinName("search"));
    }

    // ── a section toggle ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_section_toggle_writes_the_collapsed_bit_of_its_layout()
    {
        Assert.False(Sidebar.State.Of(SidebarLayoutId.Classic).Find("playlists")!.Collapsed);

        _model.ToggleSection(SidebarLayoutId.Classic, "playlists", true);
        Assert.True(Sidebar.State.Of(SidebarLayoutId.Classic).Find("playlists")!.Collapsed);

        _model.ToggleSection(SidebarLayoutId.Classic, "playlists", false);
        Assert.False(Sidebar.State.Of(SidebarLayoutId.Classic).Find("playlists")!.Collapsed);
    }

    [Fact]
    public void Opening_new_releases_marks_it_seen_and_nothing_else_does()
    {
        long before = Sidebar.NewReleasesSeenMs;
        Thread.Sleep(5);                                  // the watermark is a unix ms: the next stamp must differ

        _model.ToggleSection(SidebarLayoutId.Classic, "newReleases", true);    // closing it
        _model.ToggleSection(SidebarLayoutId.Classic, "playlists", false);     // another section opening
        Assert.Equal(before, Sidebar.NewReleasesSeenMs);

        _model.ToggleSection(SidebarLayoutId.Classic, "newReleases", false);   // opening it
        Assert.True(Sidebar.NewReleasesSeenMs > before);
    }

    // ── folders ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Toggling_and_activating_a_folder_flip_the_shared_expansion()
    {
        const string folder = "6a1f2c";
        Assert.False(_model.IsFolderExpanded(folder));

        _model.ToggleFolder(folder);
        Assert.True(_model.IsFolderExpanded(folder));
        Assert.Contains(folder, _model.ExpandedFolders);

        _model.ActivateFolder(folder, "Mixes");           // the model's rule is the inline one
        Assert.False(_model.IsFolderExpanded(folder));

        _model.SetFolderExpanded(folder, true);
        Assert.True(_model.IsFolderExpanded(folder));
        _model.ToggleFolder(folder);
        Sidebar.FlushPendingCommit();
        Assert.False(_model.IsFolderExpanded(folder));
    }

    // ── create ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_create_verbs_call_the_library_seam_and_refuse_without_one()
    {
        var saved = Sidebar.LibraryWrites;
        try
        {
            // No seam: never a half-made playlist and never an exception (the refusal is a log line and a toast).
            Sidebar.LibraryWrites = null;
            _model.CreatePlaylist(null, true);
            _model.CreatePlaylist("abc", true);
            _model.CreateFolder(null);

            string? playlistFolder = "unset";
            bool navigate = false;
            string? folderParent = "unset";
            int folderItems = -1;
            Sidebar.LibraryWrites = new SidebarLibraryWrites
            {
                CreatePlaylist = (folderId, go) => { playlistFolder = folderId; navigate = go; },
                NewFolderWith = (parent, items) => { folderParent = parent; folderItems = items.Count; },
            };

            _model.CreatePlaylist(null, true);
            Assert.Null(playlistFolder);
            Assert.True(navigate);

            _model.CreatePlaylist("abc", false);
            Assert.Equal("abc", playlistFolder);
            Assert.False(navigate);

            _model.CreateFolder("abc");
            Assert.Equal("abc", folderParent);
            Assert.Equal(0, folderItems);                 // the plain "New folder" verb: no items to file
        }
        finally { Sidebar.LibraryWrites = saved; }
    }

    // ── play ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Play_goes_through_the_action_service_else_the_shells_play_seam()
    {
        const string album = "spotify:album:6dVIqQ8qmQ5GBnJ9shOYGE";
        var services = Actions.Services;
        var previousPlay = services.Play;
        var previousSeam = Shell.OnPlayContext;
        try
        {
            string? viaService = null, viaSeam = null;
            Shell.OnPlayContext = uri => viaSeam = uri.Text;

            services.Play = uri => viaService = uri.Text;
            _model.Play(album);
            Assert.Equal(album, viaService);
            Assert.Null(viaSeam);

            services.Play = null;
            _model.Play(album);
            Assert.Equal(album, viaSeam);

            viaSeam = null;
            _model.Play("");                              // nothing to play
            Assert.Null(viaSeam);
        }
        finally
        {
            services.Play = previousPlay;
            Shell.OnPlayContext = previousSeam;
        }
    }

    // ── the drag peek ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_drag_peek_write_lands_on_the_signal_the_shell_clips_the_column_with()
    {
        Assert.False(Sidebar.DragPeek.Peek());
        _model.SetDragPeek(true);
        Assert.True(Sidebar.DragPeek.Peek());
        Assert.True(_model.DragPeek.Peek());
        _model.SetDragPeek(true);                         // idempotent
        _model.SetDragPeek(false);
        Assert.False(Sidebar.DragPeek.Peek());
    }
}
