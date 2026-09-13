// ── Wavee.Tests/DragTests.cs — the drag vocabulary: the kind map, every drop rule, the chip model ─────────────────────
//
// Wave 4's gate for `Platform/Drag.cs` (owner L), ported from 0.2.9's `WaveeDragRulesTests` (286 lines),
// `WaveeDragChipModelTests` (101) and the payload half of `RootlistRefusalTests`. Owners I (tab spring-load) and J (the
// sidebar's five drop cues) consume these tables, so a fact here is a contract for two other files as much as for this
// one.
//
// Pure: no engine loop, no window. The chip-model facts that need a TRACK boot a fake scope, because a track is a slot
// into the current scope's table and a payload carries handles; everything else touches no table at all.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DragKindMapTests
{
    [Theory]
    [InlineData(EntityKind.Track, DragKind.Track)]
    [InlineData(EntityKind.Episode, DragKind.Episode)]
    [InlineData(EntityKind.Album, DragKind.Album)]
    [InlineData(EntityKind.Artist, DragKind.Artist)]
    [InlineData(EntityKind.Playlist, DragKind.Playlist)]
    [InlineData(EntityKind.Collection, DragKind.Playlist)]   // Liked Songs navigates, pins and resolves like a playlist
    [InlineData(EntityKind.Show, DragKind.Show)]
    [InlineData(EntityKind.User, DragKind.Route)]            // a person: pinnable, never depositable
    [InlineData(EntityKind.Unknown, DragKind.Route)]
    public void Entity_kind_maps_to_one_drag_kind(EntityKind kind, DragKind expected)
        => Assert.Equal(expected, Drag.KindOf(kind));

    [Theory]
    [InlineData("spotify:album:4aawyAB9vmqN3uQ7FjRGTy", DragKind.Album)]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", DragKind.Playlist)]
    [InlineData("spotify:artist:0OdUWJ0sBjDrqHygGUXeCF", DragKind.Artist)]
    [InlineData("spotify:track:6rqhFgbbKwnb9MLmUQDhG6", DragKind.Track)]
    [InlineData("spotify:collection:tracks", DragKind.Playlist)]
    [InlineData("spotify:folder:abc123", DragKind.Folder)]
    [InlineData("", DragKind.Route)]
    [InlineData("not a uri at all", DragKind.Route)]
    public void A_bare_uri_maps_through_the_one_parser(string uri, DragKind expected)
        => Assert.Equal(expected, Drag.KindOfUri(uri));

    [Fact]
    public void A_playable_row_says_whether_it_is_a_song_or_an_episode()
    {
        // An episode rides the same row model as a song but it is not one, and the chip's glyph and the captions read
        // the KIND. Anything unclassifiable (a local file, an unknown uri) stays a Track — a playable with a snapshot.
        Assert.Equal(DragKind.Episode, Drag.PlayableKind("spotify:episode:512ojhOuo1ktJprKbVcKyQ"));
        Assert.Equal(DragKind.Track, Drag.PlayableKind("spotify:track:6rqhFgbbKwnb9MLmUQDhG6"));
        Assert.Equal(DragKind.Track, Drag.PlayableKind("wavee:local:file:QzpcbXVzaWNcYS5mbGFj"));
    }

    [Theory]
    [InlineData(DragKind.Playlist, true)]
    [InlineData(DragKind.Album, true)]
    [InlineData(DragKind.Artist, true)]
    [InlineData(DragKind.Show, true)]
    [InlineData(DragKind.Folder, true)]
    [InlineData(DragKind.Route, true)]
    [InlineData(DragKind.Track, false)]     // playables are never pins …
    [InlineData(DragKind.Episode, false)]   // … refused explicitly, never collapsed to a guessed route pin
    public void Only_containers_and_routes_pin(DragKind kind, bool pinnable)
        => Assert.Equal(pinnable, Drag.Pinnable(kind));
}

[Collection(EntitiesCollection.Name)]
public class DragRefusalTests
{
    [Fact]
    public void A_foreign_copy_onto_an_editable_loaded_playlist_is_accepted()
        => Assert.Equal(DropRefusal.None,
            Drag.Evaluate(editable: true, loading: false, payloadHasTracks: true,
                          sameList: false, naturalOrder: false, filtered: true, rowsKeyed: false));

    [Fact]
    public void A_foreign_copy_is_legal_under_any_sort_or_filter()
    {
        // It inserts by DISPLAY position without having to name existing membership rows, so the last three arms are
        // same-list-move ambiguities only.
        foreach (bool natural in new[] { true, false })
        foreach (bool filtered in new[] { true, false })
        foreach (bool keyed in new[] { true, false })
            Assert.True(Drag.Accepts(true, false, true, sameList: false, natural, filtered, keyed));
    }

    [Fact]
    public void Write_capability_is_asked_FIRST()
    {
        // Nothing else can rescue a read-only playlist, so its sentence must win over every other.
        Assert.Equal(DropRefusal.NotEditable,
            Drag.Evaluate(editable: false, loading: true, payloadHasTracks: false,
                          sameList: true, naturalOrder: false, filtered: true, rowsKeyed: false));
    }

    [Fact]
    public void Loading_is_asked_before_the_payload()
        => Assert.Equal(DropRefusal.Loading,
            Drag.Evaluate(true, loading: true, payloadHasTracks: false, sameList: false, true, false, true));

    [Fact]
    public void A_payload_with_nothing_to_add_is_refused_with_its_own_reason()
        => Assert.Equal(DropRefusal.NoTracks,
            Drag.Evaluate(true, false, payloadHasTracks: false, sameList: false, true, false, true));

    [Fact]
    public void A_same_list_move_names_sort_before_filter_before_syncing()
    {
        // Sorting and filtering are states the user can ACT on; "still syncing" is a WAIT. Naming the wait first would
        // hide the two refusals that have a remedy.
        Assert.Equal(DropRefusal.Sorted, Drag.Evaluate(true, false, true, true, naturalOrder: false, filtered: true, rowsKeyed: false));
        Assert.Equal(DropRefusal.Filtered, Drag.Evaluate(true, false, true, true, naturalOrder: true, filtered: true, rowsKeyed: false));
        Assert.Equal(DropRefusal.Syncing, Drag.Evaluate(true, false, true, true, naturalOrder: true, filtered: false, rowsKeyed: false));
        Assert.Equal(DropRefusal.None, Drag.Evaluate(true, false, true, true, naturalOrder: true, filtered: false, rowsKeyed: true));
    }

    [Fact]
    public void Accepts_and_Evaluate_can_never_disagree()
    {
        // One table answers BOTH, so a refusal can never be cued with a reason the accept test did not use.
        for (int bits = 0; bits < 128; bits++)
        {
            bool B(int i) => (bits & (1 << i)) != 0;
            var r = Drag.Evaluate(B(0), B(1), B(2), B(3), B(4), B(5), B(6));
            Assert.Equal(r == DropRefusal.None, Drag.Accepts(B(0), B(1), B(2), B(3), B(4), B(5), B(6)));
        }
    }

    [Fact]
    public void An_empty_row_set_is_keyed_and_one_unacked_row_is_not()
    {
        Assert.True(Drag.RowsAreKeyed(ReadOnlySpan<RowRef>.Empty));
        TestScope.Fresh();
        var keyed = new RowRef(Entities.Strings.Intern("item-1"), 0);
        var pending = new RowRef(default, 1);
        Assert.True(Drag.RowsAreKeyed([keyed]));
        Assert.False(Drag.RowsAreKeyed([keyed, pending]));
    }
}

public class DragSurfaceRuleTests
{
    const string P = "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M";
    const string Q = "spotify:playlist:5ABHKGoOzxkaa28ttQV9sE";

    [Fact]
    public void Only_a_real_playlist_is_a_deposit_destination()
    {
        Assert.True(Drag.IsDepositablePlaylistUri(P));
        Assert.False(Drag.IsDepositablePlaylistUri("spotify:collection:tracks"));   // navigates like one, is not one
        Assert.False(Drag.IsDepositablePlaylistUri("spotify:album:4aawyAB9vmqN3uQ7FjRGTy"));
        Assert.False(Drag.IsDepositablePlaylistUri(""));
    }

    [Fact]
    public void A_tab_takes_a_foreign_deposit()
        => Assert.True(Drag.TabAcceptsDeposit(P, targetEditable: true, payloadHasTracks: true,
                                              payloadSourcePlaylistUri: Q, payloadUri: "spotify:track:1"));

    [Fact]
    public void A_row_dragged_onto_its_OWN_playlists_tab_is_refused_outright()
    {
        // A tab can only APPEND, so the same-list MOVE arm cannot engage and the drop would duplicate the user's own
        // rows. Refusing means the tab never lights up for a gesture that has nothing to do.
        Assert.False(Drag.TabAcceptsDeposit(P, true, true, payloadSourcePlaylistUri: P, payloadUri: "spotify:track:1"));
    }

    [Fact]
    public void A_playlist_dropped_onto_its_own_tab_is_refused()
        => Assert.False(Drag.TabAcceptsDeposit(P, true, true, payloadSourcePlaylistUri: null, payloadUri: P));

    [Fact]
    public void A_read_only_or_empty_handed_tab_deposit_is_refused()
    {
        Assert.False(Drag.TabAcceptsDeposit(P, targetEditable: false, true, Q, "spotify:track:1"));
        Assert.False(Drag.TabAcceptsDeposit(P, true, payloadHasTracks: false, Q, "spotify:artist:1"));
    }

    [Fact]
    public void A_queue_row_offers_its_tracks_to_nobody()
    {
        // A queue row travels WITH its track (the chip reads it) — which is how a reorder attempt once ended as
        // "Added to {playlist}". It is a reorder, and every destination reads this one predicate.
        Assert.True(Drag.Depositable(hasTracks: true, fromQueue: false));
        Assert.False(Drag.Depositable(hasTracks: true, fromQueue: true));
        Assert.False(Drag.Depositable(hasTracks: false, fromQueue: false));
    }

    [Fact]
    public void A_folder_crossing_the_collapsed_rail_is_passing_through_not_refused()
    {
        // "Nothing to add" was an accusation aimed at a drag that was only on its way somewhere else.
        Assert.True(Drag.RailTileTransparent(payloadIsRootlistItem: true, payloadCanCopyTracks: false));
        Assert.False(Drag.RailTileTransparent(payloadIsRootlistItem: true, payloadCanCopyTracks: true));
        Assert.False(Drag.RailTileTransparent(payloadIsRootlistItem: false, payloadCanCopyTracks: false));
    }

    [Fact]
    public void Spring_load_waits_long_enough_that_travelling_across_never_opens()
        => Assert.Equal(500f, Drag.SpringLoadMs);

    [Fact]
    public void The_insertion_preview_cap_is_the_frameworks_own()
    {
        // A local 3 would drift the cards off the gap the view already sized from the framework's number.
        Assert.Equal(SortableMath.DefaultPreviewCap, Drag.PreviewCap);
    }
}

[Collection(EntitiesCollection.Name)]
public class DragPayloadTests
{
    const string P = "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M";

    [Fact]
    public void A_payload_with_a_resolver_can_copy_and_a_queue_row_cannot()
    {
        var withResolver = new DragPayload(DragKind.Album, "a", "spotify:album:1", "Album",
            TrackResolver: static _ => System.Threading.Tasks.Task.FromResult(Array.Empty<Track>()));
        Assert.True(withResolver.CanCopyTracks);

        TestScope.Fresh();
        var slot = Entities.Current.Tracks.Alloc(Entities.Strings.Intern("spotify:track:q1"));
        var queueRow = new DragPayload(DragKind.Track, "t", "spotify:track:q1", "Song",
            Tracks: [new Track(slot)], SourceQueueItemId: 42UL);
        Assert.True(queueRow.FromQueue);
        Assert.False(queueRow.CanCopyTracks);
    }

    [Fact]
    public void A_bare_artist_payload_has_nothing_to_deposit()
        => Assert.False(new DragPayload(DragKind.Artist, "x", "spotify:artist:1", "Artist").CanCopyTracks);

    [Fact]
    public void The_rootlist_count_is_the_one_count()
    {
        Assert.Equal(0, new DragPayload(DragKind.Album, "a", "u", "n").RootlistCount);
        Assert.Equal(1, new DragPayload(DragKind.Playlist, "p", P, "n", RootlistItem: true).RootlistCount);
        var many = new DragPayload(DragKind.Playlist, "p", P, "n", RootlistItem: true,
            RootlistItems: [new RootRef(P, false), new RootRef("c1", true), new RootRef("spotify:playlist:x", false)]);
        Assert.Equal(3, many.RootlistCount);
    }

    [Fact]
    public void A_single_rootlist_drag_moves_as_a_list_of_one()
    {
        // No separate single-item path in the decision, the cue or the commit.
        var single = new DragPayload(DragKind.Playlist, "pl:" + P, P, "n", RootlistItem: true);
        var refs = single.RootRefs();
        Assert.Single(refs);
        Assert.Equal(new RootRef(P, false), refs[0]);
    }

    [Fact]
    public void A_folder_moves_by_its_group_id()
    {
        // Either spelling reduces to the one group id, so the sidebar's two addresses cannot name two folders.
        var byUri = new DragPayload(DragKind.Folder, "spotify:folder:a42", "", "Chill", RootlistItem: true);
        var byId = new DragPayload(DragKind.Folder, "a42", "", "Chill", RootlistItem: true);
        Assert.Single(byUri.RootRefs());
        Assert.True(byUri.RootRefs()[0].IsFolder);
        Assert.Equal("a42", byUri.RootRefs()[0].Key);
        Assert.Equal(byUri.RootRefs()[0], byId.RootRefs()[0]);
    }

    [Fact]
    public void IsSource_asks_the_row_both_ways()
    {
        // The sidebar addresses a row by its projection id AND by the bare uri the seam moves it as; they are different
        // strings, and asking only one of them is how "into myself" went unexplained.
        var byUri = new DragPayload(DragKind.Playlist, "pl:" + P, P, "n", RootlistItem: true);
        Assert.True(Drag.IsSource(byUri, entryId: "", uri: P));
        Assert.True(Drag.IsSource(byUri, entryId: "pl:" + P, uri: ""));
        Assert.False(Drag.IsSource(byUri, "pl:other", "spotify:playlist:other"));

        var multi = new DragPayload(DragKind.Playlist, "x", P, "n", RootlistItem: true,
            RootlistItems: [new RootRef("b7", IsFolder: true), new RootRef(P, IsFolder: false)]);
        Assert.True(Drag.IsSource(multi, entryId: "spotify:folder:b7", uri: ""));
        Assert.True(Drag.IsSource(multi, entryId: "", uri: P));
        Assert.False(Drag.IsSource(multi, "spotify:folder:b8", "spotify:playlist:nope"));
        Assert.False(Drag.IsSource(null, "a", "b"));
    }

    [Fact]
    public void An_unwrap_accepts_a_direct_payload_or_a_reorder_wrapped_one()
    {
        var p = new DragPayload(DragKind.Album, "a", "spotify:album:1", "A");
        Assert.Same(p, Drag.Unwrap(p));
        Assert.Null(Drag.Unwrap("something else"));
        Assert.Null(Drag.Unwrap(null));
    }
}

[Collection(EntitiesCollection.Name)]
public class DragChipModelTests
{
    [Fact]
    public void An_entity_names_itself_and_has_no_second_line()
    {
        var m = DragChipModel.For("Discovery", "https://i.scdn.co/image/x", ReadOnlySpan<Track>.Empty);
        Assert.Equal("Discovery", m.Title);
        Assert.Null(m.Subtitle);
        Assert.Equal("https://i.scdn.co/image/x", m.ArtUrl);
        Assert.Equal(1, m.Count);
    }

    [Fact]
    public void Empty_strings_are_absent_not_blank()
    {
        var m = DragChipModel.For("", "", ReadOnlySpan<Track>.Empty);
        Assert.Null(m.Title);
        Assert.Null(m.ArtUrl);
    }

    [Fact]
    public void A_rootlist_multi_select_counts_through_the_badge()
    {
        // "I am carrying five things" is one idea and gets the same badge a song selection gets.
        Assert.Equal(5, DragChipModel.For("5 items", null, ReadOnlySpan<Track>.Empty, rootlistCount: 5).Count);
        Assert.Equal(1, DragChipModel.For("one", null, ReadOnlySpan<Track>.Empty, rootlistCount: 0).Count);
    }

    [Fact]
    public void A_track_selection_names_its_FIRST_track_and_counts_the_whole_selection()
    {
        // The corner badge says "and N−1 more", so a multi-select chip stays a real track card instead of degrading
        // into a bare "3 songs" label (the payload's own name).
        TestScope.Fresh();
        var t = Entities.Current.Tracks;
        int a = t.Alloc(Entities.Strings.Intern("spotify:track:chipA"));
        int b = t.Alloc(Entities.Strings.Intern("spotify:track:chipB"));
        t.Title[a] = Entities.Strings.Intern("Weird Fishes");
        t.ArtistLine[a] = Entities.Strings.Intern("Radiohead");
        t.Title[b] = Entities.Strings.Intern("Genesis");

        var m = DragChipModel.For("2 songs", null, [new Track(a), new Track(b)]);
        Assert.Equal("Weird Fishes", m.Title);
        Assert.Equal("Radiohead", m.Subtitle);
        Assert.Equal(2, m.Count);
    }

    [Fact]
    public void A_nameless_first_track_falls_back_to_the_payload_name()
    {
        TestScope.Fresh();
        int a = Entities.Current.Tracks.Alloc(Entities.Strings.Intern("spotify:track:nameless"));
        var m = DragChipModel.For("1 song", null, [new Track(a)]);
        Assert.Equal("1 song", m.Title);
    }
}
