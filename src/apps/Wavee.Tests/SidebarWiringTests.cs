// ── Wavee.Tests/SidebarWiringTests.cs — the sidebar's wiring: seams, rules and the binder facts they unblocked ────────
//
// Gap-fix batch B6 (wavee-0.3-gap-register.md §2.7). Every fact drives the REAL production rule or the REAL binder:
//
//   SidebarCreateDropRulesTests    G-170 — a "+" drop or click whose library seam is missing REFUSES with a sentence,
//                                  never cues a drop that does nothing (PURE).
//   SidebarFolderRenameTests       G-171 — what the folder rename prompt commits (PURE).
//   SidebarCanvasRuleTests         G-184 — the options popover's subject watch and the palette's per-card accept (PURE).
//   SidebarFolderPayloadTests      D10  — a folder payload's wire uri, its group id and its pin id agree (PURE).
//   SidebarRecencyFoldTests        G-172 — the shell logs folded into the sidebar's recency shapes.
//   SidebarLibraryFingerprintTests G-180 — the rebuild gate's content lane moves for a sidebar row, not for any row.
//   SidebarBinderWiringTests       G-172/G-173/G-180 — the binder's gate, its recency wiring and InputVersion; G-176 —
//                                  Sidebar.Shutdown lands the last edit.
//   SidebarShortcutCountTests      W3-A2 — the library-shortcut count badge's pure half: route → relation, state → what
//                                  the badge shows, and the stamp its live component gates its render on (PURE).
//   SidebarPlanDiffTests           W3-A2 — the pane's publish diff: which rows a republish re-skins and whether the rail
//                                  plan moved, with the entry compare BY VALUE (fresh-but-equal mosaic tiles are not a
//                                  change) (PURE).
//
// The classes that touch `Entities`, `Shell.Parse` or the process-wide `Sidebar` service join EntitiesCollection (no
// parallelism). The binder takes its two logs through `ISidebarRecencyLogs`, and the service its store through
// `Sidebar.UseStore`, so nothing here reads the real shell stores or the real profile. No engine loop, no window, no
// network.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// ── G-170: the "+" create destinations ──────────────────────────────────────────────────────────────────────────────

public class SidebarCreateDropRulesTests
{
    static DragPayload Filing(DragKind kind = DragKind.Playlist)
        => new(kind, "pl:spotify:playlist:x", "spotify:playlist:x", "X", RootlistItem: true);

    static DragPayload TrackSet()
        => new(DragKind.Album, "album:spotify:album:a", "spotify:album:a", "A",
               TrackResolver: static _ => Task.FromResult(Array.Empty<Track>()));

    static DragPayload ArtistRow() => new(DragKind.Artist, "artist:spotify:artist:r", "spotify:artist:r", "R");

    static SidebarLibraryWrites Writes(bool folder = true, bool playlist = true) => new()
    {
        NewFolderWith = folder ? static (_, _) => { } : null,
        CreatePlaylistWith = playlist ? static _ => { } : null,
    };

    [Fact]
    public void The_header_plus_files_rootlist_items_into_a_new_folder()
    {
        Assert.Equal(SidebarCreateDrop.NewFolder, SidebarCreateDropRules.Header(Filing(), Writes()));
        Assert.Equal(SidebarCreateDrop.NewFolder, SidebarCreateDropRules.Header(Filing(DragKind.Folder), Writes()));
    }

    [Fact]
    public void The_header_plus_refuses_a_filing_it_cannot_write_instead_of_cueing_it()
    {
        Assert.Equal(SidebarCreateDrop.Refused, SidebarCreateDropRules.Header(Filing(), null));
        Assert.Equal(SidebarCreateDrop.Refused, SidebarCreateDropRules.Header(Filing(), Writes(folder: false)));
    }

    [Fact]
    public void The_header_plus_makes_a_playlist_from_a_track_set_and_refuses_one_without_its_seam()
    {
        Assert.Equal(SidebarCreateDrop.NewPlaylist, SidebarCreateDropRules.Header(TrackSet(), Writes()));
        Assert.Equal(SidebarCreateDrop.Refused, SidebarCreateDropRules.Header(TrackSet(), Writes(playlist: false)));
        Assert.Equal(SidebarCreateDrop.Refused, SidebarCreateDropRules.Header(TrackSet(), null));
    }

    [Fact]
    public void A_rootlist_item_that_carries_tracks_is_filed_never_turned_into_a_playlist_of_itself()
    {
        var both = Filing() with { TrackResolver = static _ => Task.FromResult(Array.Empty<Track>()) };
        Assert.Equal(SidebarCreateDrop.NewFolder, SidebarCreateDropRules.Header(both, Writes()));
    }

    [Fact]
    public void Whatever_a_plus_cannot_create_crosses_it_with_no_cue_at_all()
    {
        // Transparent whatever the seam says: a refusal would claim the user aimed at the "+".
        Assert.Equal(SidebarCreateDrop.Transparent, SidebarCreateDropRules.Header(ArtistRow(), Writes()));
        Assert.Equal(SidebarCreateDrop.Transparent, SidebarCreateDropRules.Header(ArtistRow(), null));
    }

    [Fact]
    public void A_folder_plus_only_files_and_lets_a_track_set_cross_to_a_playlist_row()
    {
        Assert.Equal(SidebarCreateDrop.NewFolder, SidebarCreateDropRules.Folder(Filing(), Writes()));
        Assert.Equal(SidebarCreateDrop.Refused, SidebarCreateDropRules.Folder(Filing(), Writes(folder: false)));
        Assert.Equal(SidebarCreateDrop.Transparent, SidebarCreateDropRules.Folder(TrackSet(), Writes()));
    }

    [Fact]
    public void A_write_verb_without_its_seam_refuses_with_a_sentence()
    {
        Assert.Equal(SidebarDropRefusal.None, SidebarCreateDropRules.Verb(seamPresent: true));
        Assert.Equal(SidebarDropRefusal.WritesUnavailable, SidebarCreateDropRules.Verb(seamPresent: false));
        Assert.Equal("drag.libraryUnavailable", SidebarDropRefusalText.LocKey(SidebarDropRefusal.WritesUnavailable));
    }
}

// ── G-171: the folder rename prompt's commit ────────────────────────────────────────────────────────────────────────

public class SidebarFolderRenameTests
{
    [Theory]
    [InlineData(null, "Mixes", null)]
    [InlineData("", "Mixes", null)]
    [InlineData("   ", "Mixes", null)]
    [InlineData("Mixes", "Mixes", null)]              // unchanged: no round trip, no toast
    [InlineData("  Mixes  ", "Mixes", null)]          // unchanged once trimmed
    [InlineData("Road trip", "Mixes", "Road trip")]
    [InlineData("  Road trip ", "Mixes", "Road trip")]
    [InlineData("mixes", "Mixes", "mixes")]           // a case change IS a rename
    public void The_prompt_commits_a_trimmed_real_change_only(string? typed, string current, string? expected)
        => Assert.Equal(expected, SidebarFolderRename.Commit(typed, current));
}

// ── G-184: the canvas leftovers ─────────────────────────────────────────────────────────────────────────────────────

public class SidebarCanvasRuleTests
{
    static SidebarCustomLayout Doc()
        => new(SidebarTemplates.Curated,
        [
            new SidebarSectionSpec("sec_top", SidebarSectionKind.Pinned),
            new SidebarSectionSpec("sec_grp", SidebarSectionKind.CustomGroup,
                Children: [new SidebarSectionSpec("sec_child", SidebarSectionKind.StaticLinks)]),
        ]);

    [Fact]
    public void The_options_popover_keeps_its_subject_while_it_is_selected_and_in_the_document()
        => Assert.False(SidebarEditPlan.OptionsSubjectGone("sec_top", "sec_top", subjectInDocument: true));

    [Fact]
    public void Remove_section_inside_the_popover_clears_the_subject_and_closes_it()
        => Assert.True(SidebarEditPlan.OptionsSubjectGone(null, "sec_top", subjectInDocument: true));

    [Fact]
    public void Another_subject_or_a_section_gone_from_the_document_closes_it_too()
    {
        Assert.True(SidebarEditPlan.OptionsSubjectGone("sec_grp", "sec_top", subjectInDocument: true));
        Assert.True(SidebarEditPlan.OptionsSubjectGone("sec_top", "sec_top", subjectInDocument: false));
    }

    [Fact]
    public void A_palette_chip_lands_above_a_top_level_card_the_shortcuts_head_or_nothing()
    {
        var doc = Doc();
        Assert.True(SidebarEditPlan.CanAddBefore(doc, "sec_top"));
        Assert.True(SidebarEditPlan.CanAddBefore(doc, "sec_grp"));
        Assert.True(SidebarEditPlan.CanAddBefore(doc, SidebarIds.TopBarSection));
        Assert.True(SidebarEditPlan.CanAddBefore(doc, null));
        // Each of these agrees with the command translation it guards.
        var payload = new SidebarSectionDropPayload(SidebarSectionKind.Divider, "Divider");
        Assert.NotNull(SidebarEditPlan.ToAddSection(doc, "sec_grp", payload));
    }

    [Fact]
    public void A_child_card_or_a_vanished_card_refuses_the_chip_instead_of_dropping_it_into_nothing()
    {
        var doc = Doc();
        var payload = new SidebarSectionDropPayload(SidebarSectionKind.Divider, "Divider");
        Assert.False(SidebarEditPlan.CanAddBefore(doc, "sec_child"));
        Assert.Null(SidebarEditPlan.ToAddSection(doc, "sec_child", payload));
        Assert.False(SidebarEditPlan.CanAddBefore(doc, "sec_gone"));
        Assert.Null(SidebarEditPlan.ToAddSection(doc, "sec_gone", payload));
        Assert.False(SidebarEditPlan.CanAddBefore(null, "sec_top"));
    }
}

// ── D10: a folder's three spellings ─────────────────────────────────────────────────────────────────────────────────

[Collection(EntitiesCollection.Name)]   // a non-folder uri reaches EntityUri.Parse, which interns
public class SidebarFolderPayloadTests
{
    [Fact]
    public void A_folder_wire_uri_pins_as_its_folder_pin_id()
    {
        // The folder DRAG payload travels as its wire uri; dropping it on the pin band pins it as "folder:<hex>".
        Assert.Equal("folder:6a1f2c", SidebarPinId.FromUri("spotify:folder:6a1f2c"));
        Assert.Equal("folder:6a1f2c", SidebarPinId.Canonical("spotify:folder:6a1f2c"));
        Assert.Equal("folder:6a1f2c", SidebarPinId.Canonical("folder:6a1f2c"));
        Assert.Equal(SidebarEntryKind.Folder, SidebarPinId.KindOf(SidebarPinId.Canonical("spotify:folder:6a1f2c")));
    }

    [Fact]
    public void The_wire_uri_reduces_to_the_same_group_id_the_marker_stream_and_the_pin_id_carry()
    {
        const string hex = "36405e1711f88d9c";
        Assert.Equal(hex, Drag.FolderKey(EntityUri.FolderPrefix + hex));
        Assert.Equal(hex, SidebarPinId.FolderIdOf(SidebarPinId.ForFolder(hex)));
        Assert.Equal(PinSyncRules.TryPinId(EntityUri.FolderPrefix + hex), SidebarPinId.FromUri(EntityUri.FolderPrefix + hex));
    }

    [Fact]
    public void A_non_hex_folder_uri_is_still_not_pinnable()
        => Assert.Null(SidebarPinId.FromUri("spotify:folder:~7"));

    /// <summary>G-183's dropped ShellRoutesTests fact: a route the pin vocabulary offers is a route the shell resolves —
    /// a pin that parses to NotFound would render as a dead row forever.</summary>
    [Fact]
    public void Every_offered_pinnable_route_is_a_destination_the_shell_resolves()
    {
        TestScope.Fresh();
        foreach (string key in SidebarPinId.PinnableRoutes)
            Assert.NotEqual(Shell.RouteKind.NotFound, Shell.Parse(key).Kind);
        foreach (string key in SidebarPinId.AlsoPinnableRoutes)
            Assert.NotEqual(Shell.RouteKind.NotFound, Shell.Parse(key).Kind);
    }
}

// ── G-172: the shell logs → the sidebar's recency shapes ────────────────────────────────────────────────────────────

[Collection(EntitiesCollection.Name)]
public class SidebarRecencyFoldTests
{
    static Shell.PlayEntry Play(string track, string? context, long atMs)
        => new(EntityUri.Parse(track), context is null ? default : EntityUri.Parse(context), atMs, null);

    [Fact]
    public void Played_contexts_are_newest_first_context_first_and_distinct()
    {
        TestScope.Fresh();
        var log = new List<Shell.PlayEntry>   // the play log's order: NEWEST LAST
        {
            Play("spotify:track:t1", "spotify:album:a", 1_000),
            Play("spotify:track:t2", "spotify:album:a", 2_000),
            Play("spotify:track:t3", null, 3_000),
            Play("spotify:track:t4", "spotify:collection:tracks", 4_000),
            Play("spotify:track:t5", "spotify:playlist:p", 5_000),
        };
        var into = new List<SidebarPlayedContext>();

        Assert.Equal(4, SidebarRecencyFold.PlayedContexts(log, 40, into, new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal(new SidebarPlayedContext("spotify:playlist:p", SidebarEntryKind.Playlist, 5_000), into[0]);
        Assert.Equal(SidebarEntryKind.AppRoute, into[1].Kind);                  // Liked Songs is the "liked" ROUTE
        Assert.Equal(new SidebarPlayedContext("spotify:track:t3", SidebarEntryKind.Track, 3_000), into[2]);
        Assert.Equal(new SidebarPlayedContext("spotify:album:a", SidebarEntryKind.Album, 2_000), into[3]);   // newest play of it
    }

    [Fact]
    public void Played_contexts_stop_at_the_cap_and_a_reused_seen_set_starts_clean()
    {
        TestScope.Fresh();
        var log = new List<Shell.PlayEntry>();
        for (int i = 0; i < 10; i++) log.Add(Play("spotify:track:x" + i, "spotify:album:a" + i, i + 1));
        var into = new List<SidebarPlayedContext>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { "spotify:album:a9" };   // stale from a previous pass

        Assert.Equal(3, SidebarRecencyFold.PlayedContexts(log, 3, into, seen));
        Assert.Equal("spotify:album:a9", into[0].Uri);
    }

    [Fact]
    public void Visits_are_route_keys_oldest_first_in_utc_ticks_and_skip_a_not_found_route()
    {
        TestScope.Fresh();
        var at = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);
        var log = new List<Shell.HistoryEntry>
        {
            new(Shell.Parse("home"), at),
            new(Shell.Parse("nonsense"), at.AddMinutes(1)),
            new(Shell.For(EntityUri.Parse("spotify:album:5")), at.AddMinutes(2)),
        };
        var into = new List<SidebarVisit>();

        SidebarRecencyFold.Visits(log, into);

        Assert.Equal(2, into.Count);
        Assert.Equal(new SidebarVisit("home", at.Ticks), into[0]);
        // The route key IS the entry/pin id, so the recency join stays an identity lookup.
        Assert.Equal(new SidebarVisit(SidebarPinId.AlbumPrefix + "spotify:album:5", at.AddMinutes(2).Ticks), into[1]);
    }
}

// ── staging plumbing shared by the two entity-backed classes below ─────────────────────────────────────────────────

static class SidebarWiringStage
{
    public static User Me()
    {
        var me = Entities.User(EntityUri.Parse("spotify:user:me"));
        Entities.Current.MeSlot = me.Slot;
        return me;
    }

    public static void StageUser(string uri, string name)
    {
        var s = Staging.Rent();
        ref var row = ref s.Users.Add();
        row.Id = s.Text(uri);
        row.Name = s.Text(name);
        row.Known = (uint)UserFields.Identity;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);
    }

    public static Playlist StagePlaylist(string uri, string title, string? ownerUri = null)
    {
        var s = Staging.Rent();
        ref var row = ref s.Playlists.Add();
        row.Id = s.Text(uri);
        row.Title = s.Text(title);
        if (ownerUri is not null) row.OwnerUri = s.Text(ownerUri);
        row.Caps = (byte)PlaylistCaps.CanView;
        row.Known = (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities);
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);
        return Entities.Playlist(EntityUri.Parse(uri));
    }

    /// <summary>A rootlist of top-level playlists. Each edge carries an AddedAt, so no first-seen stamp is minted and the
    /// binder never reaches the document commit.</summary>
    public static void SetRootlist(User me, params Playlist[] playlists)
    {
        var targets = new int[playlists.Length];
        var payload = new RootlistEdge[playlists.Length];
        for (int i = 0; i < playlists.Length; i++)
        {
            targets[i] = playlists[i].Slot;
            payload[i] = default(RootlistEdge) with { Kind = (byte)RootlistKind.Item, AddedAt = 100 + i };
        }
        me.ReplaceRootlist(targets, payload);
    }
}

// ── G-180: the gate's content lane ──────────────────────────────────────────────────────────────────────────────────

[Collection(EntitiesCollection.Name)]
public class SidebarLibraryFingerprintTests
{
    [Fact]
    public void A_hydration_elsewhere_in_the_app_does_not_move_the_fingerprint()
    {
        TestScope.Fresh();
        var inLibrary = SidebarWiringStage.StagePlaylist("spotify:playlist:mine", "Mine");
        SidebarWiringStage.StagePlaylist("spotify:playlist:elsewhere", "Elsewhere");
        var me = SidebarWiringStage.Me();
        SidebarWiringStage.SetRootlist(me, inLibrary);

        long before = SidebarLibraryFingerprint.Of(in me, default);
        SidebarWiringStage.StagePlaylist("spotify:playlist:elsewhere", "Elsewhere, renamed on its own page");

        Assert.Equal(before, SidebarLibraryFingerprint.Of(in me, default));
    }

    [Fact]
    public void A_sidebar_row_its_owner_or_an_unlisted_pin_row_moves_it()
    {
        TestScope.Fresh();
        SidebarWiringStage.StageUser("spotify:user:zoe", "Zoe");
        var inLibrary = SidebarWiringStage.StagePlaylist("spotify:playlist:mine", "Mine", ownerUri: "spotify:user:zoe");
        var pinned = SidebarWiringStage.StagePlaylist("spotify:playlist:editorial", "Editorial");
        var me = SidebarWiringStage.Me();
        SidebarWiringStage.SetRootlist(me, inLibrary);
        EntityRef[] pins = [new(EntityKind.Playlist, pinned.Slot)];

        long start = SidebarLibraryFingerprint.Of(in me, pins);

        SidebarWiringStage.StagePlaylist("spotify:playlist:mine", "Mine, renamed", ownerUri: "spotify:user:zoe");
        long renamed = SidebarLibraryFingerprint.Of(in me, pins);
        Assert.NotEqual(start, renamed);

        SidebarWiringStage.StageUser("spotify:user:zoe", "Zoë");
        long owner = SidebarLibraryFingerprint.Of(in me, pins);
        Assert.NotEqual(renamed, owner);

        SidebarWiringStage.StagePlaylist("spotify:playlist:editorial", "Editorial, hydrated");
        Assert.NotEqual(owner, SidebarLibraryFingerprint.Of(in me, pins));
    }
}

// ── G-172 / G-173 / G-180 / G-176: the binder and the service ───────────────────────────────────────────────────────

[Collection(EntitiesCollection.Name)]
public sealed class SidebarBinderWiringTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-sidebar-wiring-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    sealed class FakeLogs : ISidebarRecencyLogs
    {
        public int HistoryVersion { get; set; }
        public readonly List<Shell.HistoryEntry> HistoryList = new();
        public IReadOnlyList<Shell.HistoryEntry> History => HistoryList;
        public int PlayLogVersion { get; set; }
        public readonly List<Shell.PlayEntry> PlayList = new();
        public IReadOnlyList<Shell.PlayEntry> Plays => PlayList;
        public readonly Dictionary<string, long> LastPlayedMap = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, long> LastPlayed => LastPlayedMap;
    }

    [Fact]
    public void The_gate_rebuilds_for_a_sidebar_row_and_not_for_a_row_elsewhere()
    {
        TestScope.Fresh();
        var mine = SidebarWiringStage.StagePlaylist("spotify:playlist:mine", "Mine");
        SidebarWiringStage.StagePlaylist("spotify:playlist:elsewhere", "Elsewhere");
        var me = SidebarWiringStage.Me();
        SidebarWiringStage.SetRootlist(me, mine);

        var binder = new SidebarProjectionBinder(new FakeLogs());
        binder.Start();
        Assert.Equal(1, binder.Revision);
        Assert.False(binder.Sync());                                            // nothing moved: one fold, no pass

        SidebarWiringStage.StagePlaylist("spotify:playlist:elsewhere", "Elsewhere, hydrated by its own page");
        Assert.False(binder.Sync());                                            // G-180: not a sidebar row

        SidebarWiringStage.StagePlaylist("spotify:playlist:mine", "Mine, renamed");
        Assert.True(binder.Sync());
        Assert.Equal("Mine, renamed", binder.CurrentInput.PlaylistTree![0].Name);
        Assert.False(binder.Sync());                                            // and settled again
    }

    [Fact]
    public void The_shell_logs_reach_the_planner_input_and_move_InputVersion()
    {
        TestScope.Fresh();
        var mine = SidebarWiringStage.StagePlaylist("spotify:playlist:mine", "Mine");
        var me = SidebarWiringStage.Me();
        SidebarWiringStage.SetRootlist(me, mine);

        var logs = new FakeLogs();
        var binder = new SidebarProjectionBinder(logs);
        binder.Start();
        int input = binder.InputVersion.Peek();
        Assert.Empty(binder.CurrentInput.Visited!);

        // A navigation: "Jump back in" gains the page.
        logs.HistoryList.Add(new Shell.HistoryEntry(Shell.Parse("home"), new DateTime(2026, 9, 13, 9, 0, 0, DateTimeKind.Utc)));
        logs.HistoryVersion++;
        Assert.True(binder.Sync());
        Assert.Equal("home", Assert.Single(binder.CurrentInput.Visited!).Id);
        Assert.Single(((ISidebarProjectionSnapshot)binder).Visits);
        Assert.True(binder.InputVersion.Peek() > input);
        input = binder.InputVersion.Peek();

        // A play: "Recently played" gains the context, and the Recents sort's stamp lands on the library row.
        logs.PlayList.Add(new Shell.PlayEntry(EntityUri.Parse("spotify:track:t"), EntityUri.Parse("spotify:playlist:mine"), 999, null));
        logs.LastPlayedMap["spotify:playlist:mine"] = 999;
        logs.PlayLogVersion++;
        Assert.True(binder.Sync());
        var played = Assert.Single(binder.CurrentInput.Played!);
        Assert.Equal(SidebarPinId.PlaylistPrefix + "spotify:playlist:mine", played.Id);
        Assert.Equal("Mine", played.Name);                                      // joined to the projection
        Assert.Equal(999L, binder.CurrentInput.PlaylistTree![0].LastPlayedMs);
        Assert.True(binder.InputVersion.Peek() > input);

        Assert.False(binder.Sync());
    }

    [Fact]
    public void A_scope_switch_re_points_the_binder_and_the_new_rootlist_lands()
    {
        TestScope.Fresh();
        var mine = SidebarWiringStage.StagePlaylist("spotify:playlist:mine", "Mine");
        var me = SidebarWiringStage.Me();
        SidebarWiringStage.SetRootlist(me, mine);

        var binder = new SidebarProjectionBinder(new FakeLogs());
        binder.Start();
        int rev = binder.Revision;

        // The welcome effect's own move (G-179): a fresh scope, its rootlist not landed yet. `Sync()` reads
        // `Entities.Current` live, so THIS half is not the defect — `PumpBinder`'s missing `ScopeEpoch` read is an
        // engine signal-effect subscription bug the live checklist covers, not something this binder-level fact
        // can reproduce.
        Entities.Switch(CatalogScope.Fake(market: "GB"));
        Assert.True(binder.Sync());
        Assert.Equal(SidebarSourceState.Pending, ((ISidebarProjectionSnapshot)binder).TreeState);
        Assert.Empty(binder.CurrentInput.PlaylistTree!);

        var theirs = SidebarWiringStage.StagePlaylist("spotify:playlist:theirs", "Theirs");
        var them = SidebarWiringStage.Me();
        SidebarWiringStage.SetRootlist(them, theirs);

        Assert.True(binder.Sync());
        Assert.True(binder.Revision > rev);
        Assert.Equal(SidebarSourceState.Ready, ((ISidebarProjectionSnapshot)binder).TreeState);
        Assert.Equal("Theirs", binder.CurrentInput.PlaylistTree![0].Name);
        Assert.False(binder.Sync());
    }

    [Fact]
    public void Shutdown_writes_the_last_edit_before_it_returns()
    {
        string path = Path.Combine(_dir, "WaveeMusic", "sidebar-layout.json");
        Sidebar.UseStore(new SidebarLayoutStore(path));
        Sidebar.Boot();
        try
        {
            Assert.True(Sidebar.Pin(new SidebarPin("pl:spotify:playlist:keep", SidebarEntryKind.Playlist,
                "spotify:playlist:keep", "Keep", AddedAtMs: 1)));

            // The pin armed the 300 ms debounce; Shutdown fires it and waits for the pool write.
            Assert.True(Sidebar.Shutdown(10_000));

            var doc = new SidebarLayoutStore(path).Load().Doc;
            Assert.NotNull(doc);
            Assert.Contains(doc!.Pins!, p => p.Id == "pl:spotify:playlist:keep");
        }
        finally
        {
            // Hand the service an empty store and re-Boot: a first-run load starts the pin list empty, so no later fact
            // inherits this one's pin — and nothing is committed to do it.
            Sidebar.UseStore(new SidebarLayoutStore(Path.Combine(_dir, "reset", "sidebar-layout.json")));
            Sidebar.Boot();
        }
    }
}

// ── W3-A2: the library-shortcut count badge's pure half ─────────────────────────────────────────────────────────────

public class SidebarShortcutCountTests
{
    [Fact]
    public void The_four_counted_shortcuts_map_to_their_library_relation()
    {
        Assert.Equal(LibraryEdgeKind.SavedAlbums, Sidebar.ShortcutCount.KindOf("albums"));
        Assert.Equal(LibraryEdgeKind.FollowedArtists, Sidebar.ShortcutCount.KindOf("artists"));
        Assert.Equal(LibraryEdgeKind.Liked, Sidebar.ShortcutCount.KindOf("liked"));
        Assert.Equal(LibraryEdgeKind.SavedShows, Sidebar.ShortcutCount.KindOf("podcasts"));
    }

    [Fact]
    public void A_shortcut_without_a_library_relation_carries_no_count()
    {
        Assert.Null(Sidebar.ShortcutCount.KindOf("local"));
        Assert.Null(Sidebar.ShortcutCount.KindOf("home"));
        Assert.Null(Sidebar.ShortcutCount.KindOf(""));
    }

    [Fact]
    public void An_unknown_relation_is_the_pending_plate_whatever_its_total_says()
    {
        Assert.Null(Sidebar.ShortcutCount.Shown(EdgeState.Unknown, 0));
        Assert.Null(Sidebar.ShortcutCount.Shown(EdgeState.Unknown, 42));
    }

    [Fact]
    public void A_paging_or_complete_relation_shows_its_server_total()
    {
        Assert.Equal(42, Sidebar.ShortcutCount.Shown(EdgeState.Partial, 42));
        Assert.Equal(0, Sidebar.ShortcutCount.Shown(EdgeState.Complete, 0));
        Assert.Equal(7, Sidebar.ShortcutCount.Shown(new Sidebar.ShortcutCount.Stamp(1, EdgeState.Complete, 7)));
    }

    [Fact]
    public void The_stamp_is_equal_exactly_when_what_the_badge_paints_is_equal()
    {
        var a = new Sidebar.ShortcutCount.Stamp(3, EdgeState.Partial, 12);
        // A relation publication that moved nothing the badge paints: the memo resolves EQUAL, the badge does not render.
        Assert.Equal(a, new Sidebar.ShortcutCount.Stamp(3, EdgeState.Partial, 12));
        Assert.NotEqual(a, new Sidebar.ShortcutCount.Stamp(3, EdgeState.Partial, 13));   // a like / a save landed
        Assert.NotEqual(a, new Sidebar.ShortcutCount.Stamp(3, EdgeState.Complete, 12));  // the last page landed
        Assert.NotEqual(a, new Sidebar.ShortcutCount.Stamp(4, EdgeState.Partial, 12));   // a scope switch re-points it
    }

    [Fact]
    public void Each_counted_kind_has_its_own_stable_reconciler_key()
    {
        var kinds = new[]
        {
            LibraryEdgeKind.SavedAlbums, LibraryEdgeKind.FollowedArtists, LibraryEdgeKind.Liked, LibraryEdgeKind.SavedShows,
        };
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in kinds)
        {
            string key = Sidebar.ShortcutCount.KeyOf(kind);
            Assert.True(keys.Add(key), $"{kind} shares a key with another kind");
            Assert.Same(key, Sidebar.ShortcutCount.KeyOf(kind));   // a literal, never a per-render allocation
        }
    }
}

// ── W3-A2: the pane's publish diff ──────────────────────────────────────────────────────────────────────────────────

public class SidebarPlanDiffTests
{
    static SidebarLibraryEntry Entry(string id, string name, IReadOnlyList<StringId>? tiles = null, int count = 0)
        => new(id, SidebarEntryKind.Playlist, "spotify:playlist:" + id, name, "Me",
               default, tiles, ChildCount: count, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
               SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.ByYou);

    static SidebarRow Row(SidebarRowKind kind, int entryIndex, string key)
        => new(kind, "sec", 0, entryIndex, 0, key);

    static readonly SidebarRow[] TwoRows =
    [
        Row(SidebarRowKind.SectionHeader, -1, "sec"),
        Row(SidebarRowKind.EntityRow, 0, "pl:a"),
    ];

    [Fact]
    public void An_identical_republish_changes_no_row_and_leaves_the_rail_alone()
    {
        var entries = new[] { Entry("a", "A") };
        Assert.False(Sidebar.PlanDiff.RowChanged(TwoRows, entries, TwoRows, entries, 0));
        Assert.False(Sidebar.PlanDiff.RowChanged(TwoRows, entries, TwoRows, entries, 1));
        Assert.False(Sidebar.PlanDiff.Changed(TwoRows, entries, TwoRows, entries));
    }

    [Fact]
    public void Fresh_but_equal_mosaic_tiles_are_not_a_change()
    {
        // The projection materialises a folder's / a cover-less playlist's tile list fresh on every rebuild. The record's
        // compiler equality compares that member by REFERENCE and would re-skin the row (and its chevron / pill) on
        // every re-plan; the pane's diff compares it by VALUE.
        var before = new[] { Entry("a", "A", new List<StringId> { new(1), new(2), new(3), new(4) }) };
        var after = new[] { Entry("a", "A", new List<StringId> { new(1), new(2), new(3), new(4) }) };
        Assert.False(before[0].Equals(after[0]));   // the trap this rule exists for
        Assert.False(Sidebar.PlanDiff.RowChanged(TwoRows, before, TwoRows, after, 1));
        Assert.False(Sidebar.PlanDiff.Changed(TwoRows, before, TwoRows, after));
    }

    [Fact]
    public void A_different_tile_set_behind_the_same_row_is_a_change()
    {
        var before = new[] { Entry("a", "A", new List<StringId> { new(1), new(2) }) };
        var after = new[] { Entry("a", "A", new List<StringId> { new(1), new(9) }) };
        Assert.True(Sidebar.PlanDiff.RowChanged(TwoRows, before, TwoRows, after, 1));
    }

    [Fact]
    public void A_hydrated_entry_behind_an_unchanged_row_record_re_skins_that_row_only()
    {
        var before = new[] { Entry("a", "") };
        var after = new[] { Entry("a", "A", count: 12) };
        Assert.False(Sidebar.PlanDiff.RowChanged(TwoRows, before, TwoRows, after, 0));   // the header carries no entry
        Assert.True(Sidebar.PlanDiff.RowChanged(TwoRows, before, TwoRows, after, 1));
        Assert.True(Sidebar.PlanDiff.Changed(TwoRows, before, TwoRows, after));
    }

    [Fact]
    public void A_moved_row_record_is_a_change_even_over_the_same_entries()
    {
        var entries = new[] { Entry("a", "A"), Entry("b", "B") };
        var after = new[] { TwoRows[0], Row(SidebarRowKind.EntityRow, 1, "pl:b") };
        Assert.True(Sidebar.PlanDiff.RowChanged(TwoRows, entries, after, entries, 1));
    }

    [Fact]
    public void A_row_new_at_its_slot_and_a_count_change_are_both_changes()
    {
        var entries = new[] { Entry("a", "A"), Entry("b", "B") };
        var grown = new[] { TwoRows[0], TwoRows[1], Row(SidebarRowKind.EntityRow, 1, "pl:b") };
        Assert.True(Sidebar.PlanDiff.RowChanged(TwoRows, entries, grown, entries, 2));
        Assert.True(Sidebar.PlanDiff.Changed(TwoRows, entries, grown, entries));
        Assert.True(Sidebar.PlanDiff.Changed(grown, entries, TwoRows, entries));   // shrinking moves it too
    }

    [Fact]
    public void An_entry_index_valid_in_one_plan_and_not_the_other_is_a_change()
    {
        var some = new[] { Entry("a", "A") };
        var none = Array.Empty<SidebarLibraryEntry>();
        Assert.True(Sidebar.PlanDiff.RowChanged(TwoRows, none, TwoRows, some, 1));
        Assert.True(Sidebar.PlanDiff.RowChanged(TwoRows, some, TwoRows, none, 1));
        // Out of range on BOTH sides is a placeholder that stayed a placeholder.
        Assert.False(Sidebar.PlanDiff.RowChanged(TwoRows, none, TwoRows, none, 1));
    }

    [Fact]
    public void An_index_past_the_new_plan_is_never_a_change()
        => Assert.False(Sidebar.PlanDiff.RowChanged(TwoRows, Array.Empty<SidebarLibraryEntry>(), TwoRows,
                                                    Array.Empty<SidebarLibraryEntry>(), 5));
}
