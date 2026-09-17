// ── Wavee.Tests/PathfinderDecodeTests.cs — the GraphQL half of Spotify.Decode (Wave 2, owner E) ──────────────────
//
// Wave 2's gate for `Spotify/Spotify.Decode.Pathfinder.cs`, read against the FOUR CAPTURED ANSWERS the app already
// ships: `assets/spotify/*.json`, copied into `Fixtures/spotify/`. They are the bundled-export offline fixture path
// ch 31 §9.5 names, and they are real wire captures — a playlist with fifteen tracks, a home feed with thirty-one
// bands, an artist overview with a full discography, and a twenty-four-row library.
//
// Every fact reads a HANDLE after the commit, never a staged row, and every count below was measured from the
// fixture itself rather than guessed: 15 items, 31 sections, 10 top tracks, 20 related artists, 24 library rows.
//
// `Export` is the entry point under test as often as the individual folds are, because it is the one `--fake` and
// the live session both call (ch 31 §9.5): a fixture path that diverges from the live path proves nothing.

using System.IO;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PathfinderDecodeTests
{
    static byte[] Fixture(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spotify", name));

    /// <summary>Decode one bundled export and commit it the way a UI drain does.</summary>
    static void Load(string name)
    {
        var s = Staging.Rent();
        Spotify.Decode.Export(Fixture(name), s);
        TestScope.CommitAndPublish(s);
    }

    static Playlist PlaylistOf(string uri) => Entities.Playlist(EntityUri.Parse(uri.AsSpan()));
    static Track TrackOf(string uri) => Entities.Track(EntityUri.Parse(uri.AsSpan()));
    static Artist ArtistOf(string uri) => Entities.Artist(EntityUri.Parse(uri.AsSpan()));
    static User UserOf(string uri) => Entities.User(EntityUri.Parse(uri.AsSpan()));

    // ── playlistV2 (ch 06 §7) ───────────────────────────────────────────────────────────────────────────────────────

    const string PlaylistUri = "spotify:playlist:0dijb70Boi9TIdmiLLq13V";
    const string OwnerUri = "spotify:user:31unjfmo3oefvlz36ef3eb6kj5tq";
    const string FirstTrackUri = "spotify:track:7idegBIikag5rTZP4WZihP";

    [Fact]
    public void A_playlist_export_fills_the_header_the_owner_and_the_capabilities()
    {
        TestScope.Fresh();
        Load("icedamericano.json");

        var playlist = PlaylistOf(PlaylistUri);
        Assert.True(playlist.Knows(PlaylistFields.Identity));
        Assert.Equal("Iced Americano", Entities.Strings.Resolve(playlist.TitleId));
        Assert.Equal(15, playlist.TrackCount);
        Assert.Equal(UserOf(OwnerUri).Slot, playlist.Owner.Slot);
        Assert.True(playlist.Knows(PlaylistFields.Capabilities));
        Assert.True(playlist.Editable);
        Assert.True(playlist.CanView);
        Assert.True(playlist.CanAdministratePermissions);
    }

    [Fact]
    public void A_playlist_export_lands_its_membership_complete_with_the_facts_on_the_edge()
    {
        // D10: the uid, the added-at instant and the adding user are properties of the MEMBERSHIP, not of the track.
        TestScope.Fresh();
        Load("icedamericano.json");

        var playlist = PlaylistOf(PlaylistUri);
        var edges = Entities.Current.Edges.PlaylistTracks;
        Assert.Equal(EdgeState.Complete, edges.State(playlist.Slot));
        Assert.Equal(15, edges.Count(playlist.Slot));

        var first = edges.Payload(playlist.Slot)[0];
        Assert.Equal("2a826aa43895001e", Entities.Strings.Resolve(first.ItemId));
        Assert.Equal(UserOf(OwnerUri).Slot, first.AddedBy);
        Assert.Equal(Spotify.Decode.Seconds(2026, 6, 2), first.AddedAt);
        Assert.Equal(TrackOf(FirstTrackUri).Slot, edges.Targets(playlist.Slot)[0]);
    }

    [Fact]
    public void A_playlist_items_track_carries_its_row_group_and_its_credit_line()
    {
        TestScope.Fresh();
        Load("icedamericano.json");

        var track = TrackOf(FirstTrackUri);
        Assert.True(track.Knows(TrackFields.Identity));
        Assert.Equal("Cold Brew Chapters", track.Title);
        Assert.Equal(234_959, track.DurationMs);
        Assert.Equal("roti.", Entities.Strings.Resolve(track.ArtistLineId));
        Assert.True(track.Knows(TrackFields.PlayCount));
        Assert.Equal(147_606u, track.PlayCount);
        // `playability.playable: true` is a RULING, and a ruled row is what `PlayableOnly` may filter on.
        Assert.True(track.Knows(TrackFields.Availability));
        Assert.True(track.IsPlayable);
        Assert.NotEqual(Table.None, track.AlbumSlot);
    }

    // ── home (ch 10 §7) ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_home_export_lands_every_band_and_hangs_them_off_the_feed_subject()
    {
        TestScope.Fresh();
        Load("home.json");

        var home = Entities.HomeFeed();
        Assert.Equal(EdgeState.Complete, home.SectionState);
        Assert.Equal(31, home.SectionCount);
    }

    [Fact]
    public void A_bands_cursor_is_its_next_offset_and_never_its_total()
    {
        // The measured defect (ch 10 §7): sections disagree with their own `totalCount`, and paging on it never
        // terminates. The first band has 64 total, 10 cards and `nextOffset: 10`.
        TestScope.Fresh();
        Load("home.json");

        var first = Entities.Section("spotify:section:0JQ5DAIiKWzVFULQfUm85Y".AsSpan());
        Assert.Equal(64, first.Total);
        Assert.Equal(10, first.Raw);
        Assert.Equal(9, first.Cards);   // the UnknownType wrapper is counted as unsupported (WP-5.P)
        Assert.Equal(10, first.NextOffset);
        Assert.True(first.HasMore);
    }

    [Fact]
    public void A_band_with_no_cursor_reads_as_having_no_more()
    {
        // `nextOffset: null` is "no more", and it is NOT `nextOffset: 0` — which a complete band can answer.
        TestScope.Fresh();
        Load("home.json");

        var spotlight = Entities.Section("spotify:section:0JQ5DAKjx0PtyWwzEGt6uI".AsSpan());
        Assert.Equal(SectionPaging.NoCursor, spotlight.NextOffset);
        Assert.Equal(1, spotlight.Cards);
        Assert.Equal(SectionKind.HomeSpotlight, spotlight.Kind);
        Assert.Equal("New release from KIMMUSEUM", Entities.Strings.Resolve(spotlight.TitleId));
    }

    [Fact]
    public void A_bands_ledger_accounts_for_every_raw_item()
    {
        // `Raw == Cards + Unsupported + Duplicates` (ch 10 §7). A page that re-asks on the deduped count walks its
        // own cursor backwards, which is why the ledger is written at the decode and not derived at render.
        TestScope.Fresh();
        Load("home.json");

        var home = Entities.HomeFeed();
        foreach (int slot in home.SectionSlots)
        {
            var section = new Section(slot);
            Assert.Equal(section.Raw, section.Cards + section.Unsupported + section.Duplicates);
        }
    }

    [Fact]
    public void A_home_export_stages_the_feed_subject_its_greeting_and_its_chip_strip()
    {
        // The subject row is what `Home.Classify` reads and what the greeting and the facet strip paint from
        // (ch 10 §7). Before it was staged, a decoded feed filled thirty-one bands and left the row above them blank.
        TestScope.Fresh();
        Load("home.json");

        var home = Entities.HomeFeed();
        Assert.True(home.Knows(HomeFields.Greeting));
        Assert.Equal("Good morning", Entities.Strings.Resolve(home.GreetingId));

        Assert.True(home.Knows(HomeFields.Chips));
        // Three top-level chips, and Music and Podcasts each carry one "Following" sub-chip: the strip is FLAT and a
        // sub-chip points at its parent's index, which is how `HomeChip.SubChips` survives being a slab.
        Assert.Equal(5, home.ChipIds.Length);
        Assert.Equal("music-chip", Entities.Strings.Resolve(home.ChipIds[0]));
        Assert.Equal("Music", Entities.Strings.Resolve(home.ChipLabels[0]));
        Assert.Equal(-1, home.ChipParents[0]);
        Assert.Equal("music-following-chip", Entities.Strings.Resolve(home.ChipIds[1]));
        Assert.Equal(0, home.ChipParents[1]);
    }

    [Fact]
    public void A_band_attaches_its_cards_and_not_only_their_count()
    {
        // Ch 10 §7's `Edges.SectionCards`. Before the edge existed a band knew how MANY cards it had and not WHICH,
        // so every shelf painted its count and no content.
        TestScope.Fresh();
        Load("home.json");

        var spotlight = Entities.Section("spotify:section:0JQ5DAKjx0PtyWwzEGt6uI".AsSpan());
        Assert.Equal(1, spotlight.Cards);
        Assert.Equal(EdgeState.Complete, spotlight.CardState);
        Assert.Single(spotlight.CardSlots.ToArray());
        // CROSS-KIND: the payload carries the table the slot indexes, because a band mixes kinds.
        Assert.Equal(EntityKind.Album, spotlight.CardKinds[0].Kind);
        Assert.Equal(Entities.Album(EntityUri.Parse("spotify:album:1I80HwIDdWXtmA3Fqsbqnl".AsSpan())).Slot,
                     spotlight.CardSlots[0]);

        // And the card edge is parallel to the ledger for every band in the feed: a card that was staged is a card
        // that is attached.
        foreach (int slot in Entities.HomeFeed().SectionSlots)
        {
            var section = new Section(slot);
            Assert.Equal(section.CardSlots.Length, section.CardKinds.Length);
            Assert.True(section.CardSlots.Length <= section.Cards);
        }
    }

    [Fact]
    public void A_home_cards_own_row_is_staged_so_the_card_paints_without_a_second_fetch()
    {
        TestScope.Fresh();
        Load("home.json");

        var album = Entities.Album(EntityUri.Parse("spotify:album:1I80HwIDdWXtmA3Fqsbqnl".AsSpan()));
        Assert.True(album.Knows(AlbumFields.Title));
        Assert.NotEqual(0, album.TitleId.Value);
        Assert.NotEqual(0, album.ImageId.Value);
    }

    // ── artistOverview (ch 08 §7) ───────────────────────────────────────────────────────────────────────────────────

    const string ArtistUri = "spotify:artist:04gDigrS5kc9YWfZHwBETP";

    [Fact]
    public void An_artist_overview_fills_the_identity_the_stats_and_the_bio()
    {
        TestScope.Fresh();
        Load("artist-maroon5.json");

        var artist = ArtistOf(ArtistUri);
        Assert.True(artist.Knows(ArtistFields.Identity));
        Assert.Equal("Maroon 5", artist.Name);
        Assert.True(artist.Knows(ArtistFields.Stats));
        Assert.Equal(48_175_721u, artist.Followers);
        Assert.Equal(77_819_363u, artist.MonthlyListeners);
        Assert.Equal(19, artist.WorldRank);
        Assert.True(artist.Knows(ArtistFields.Bio));
        Assert.NotEqual(0, artist.BioId.Value);
        Assert.True(artist.Knows(ArtistFields.Header));
    }

    [Fact]
    public void An_artist_overview_lands_its_popular_releases_and_related_runs()
    {
        TestScope.Fresh();
        Load("artist-maroon5.json");

        var artist = ArtistOf(ArtistUri);
        var edges = Entities.Current.Edges;
        Assert.Equal(10, edges.ArtistPopular.Count(artist.Slot));
        Assert.Equal(20, edges.ArtistRelated.Count(artist.Slot));
        Assert.True(edges.ArtistAlbums.Count(artist.Slot) > 0);

        // Every popular track is a row of its own, with the credit line the shelf paints.
        int first = edges.ArtistPopular.Targets(artist.Slot)[0];
        Assert.True(new Track(first).Knows(TrackFields.Title));
    }

    // ── libraryV3 (ch 15 §7) ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_library_export_lands_the_rootlist_in_the_servers_order()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.Export(Fixture("playlists.json"), s, Spotify.Decode.DefaultMe);
        TestScope.CommitAndPublish(s);

        var me = Entities.User(EntityUri.Parse(System.Text.Encoding.UTF8.GetString(Spotify.Decode.DefaultMe).AsSpan()));
        var rootlist = Entities.Current.Edges.Rootlist;
        Assert.Equal(EdgeState.Complete, rootlist.State(me.Slot));
        // 24 rows on the wire: 23 playlists and ONE folder — which lands as a start marker, and its end marker is built
        // after its last member (G-046), so the stream is 25 edges.
        Assert.Equal(25, rootlist.Count(me.Slot));

        // The first row is Liked Songs — a pseudo playlist, and the one row the library never fetches.
        var liked = PlaylistOf("spotify:collection:tracks");
        Assert.Equal(liked.Slot, rootlist.Targets(me.Slot)[0]);
        Assert.Equal(0, rootlist.Payload(me.Slot)[0].Position);
    }

    [Fact]
    public void A_library_folder_is_a_marker_pair_with_its_group_id_and_never_a_user_row()
    {
        // `spotify:user:<u>:folder:bf9e64dce09a1afc`, depth 0, "New Folder", holding one depth-1 playlist. Before G-046
        // this row was staged as a USER named "New Folder", and its sidebar id was its position (G-047).
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.Export(Fixture("playlists.json"), s, Spotify.Decode.DefaultMe);
        TestScope.CommitAndPublish(s);

        var me = Entities.User(EntityUri.Parse(System.Text.Encoding.UTF8.GetString(Spotify.Decode.DefaultMe).AsSpan()));
        var rows = Entities.Current.Edges.Rootlist.Payload(me.Slot);
        var targets = Entities.Current.Edges.Rootlist.Targets(me.Slot);

        int start = -1;
        for (int i = 0; i < rows.Length; i++) if ((RootlistKind)rows[i].Kind == RootlistKind.FolderStart) { start = i; break; }
        Assert.Equal(20, start);
        Assert.Equal("bf9e64dce09a1afc", Entities.Strings.Resolve(rows[start].FolderId));
        Assert.Equal("New Folder", Entities.Strings.Resolve(rows[start].FolderName));
        Assert.Equal(Table.None, targets[start]);

        Assert.Equal(RootlistKind.Item, (RootlistKind)rows[start + 1].Kind);          // Evening Commute, inside it
        Assert.Equal(1, rows[start + 1].Depth);
        Assert.Equal(RootlistKind.FolderEnd, (RootlistKind)rows[start + 2].Kind);     // closed by the next depth-0 row
        Assert.Equal("bf9e64dce09a1afc", Entities.Strings.Resolve(rows[start + 2].FolderId));
        Assert.Equal(0, rows[start + 2].Depth);

        Assert.False(Entities.Current.Users.TryGetSlot(
            "spotify:user:31unjfmo3oefvlz36ef3eb6kj5tq:folder:bf9e64dce09a1afc".AsSpan(), out _));
    }

    // ── browse (ch 13 §7, G-045) ────────────────────────────────────────────────────────────────────────────────────

    static Browse NodeOf(string uri) => Entities.BrowseNode(uri.AsSpan());

    [Fact]
    public void Browse_all_lands_every_tile_once_in_wire_order_under_the_directory()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.BrowseAll(Fixture("browse-all.json"), s);
        TestScope.CommitAndPublish(s);

        var directory = Entities.BrowseDirectory();
        Assert.True(directory.Knows(BrowseFields.All));
        var tiles = Entities.Current.Edges.BrowseDirectory;
        Assert.Equal(EdgeState.Complete, tiles.State(directory.Slot));
        Assert.Equal(3, tiles.Count(directory.Slot));                                // the titleless tile is dropped

        var music = NodeOf("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy");
        Assert.Equal(music.Slot, tiles.Targets(directory.Slot)[0]);
        Assert.Equal("Music", Entities.Strings.Resolve(music.TitleId));             // under the DOUBLE `data`
        Assert.Equal(0xFF1E3264u, music.Color);
        Assert.Equal("https://i.scdn.co/image/music", Entities.Strings.Resolve(music.ImageId));
        Assert.False(music.IsClientFeature);

        var live = new Browse(tiles.Targets(directory.Slot)[1]);
        Assert.True(live.IsClientFeature);                                          // routes by featureUri…
        Assert.Equal("spotify:concerts", live.Id.Text);                             // …never by its xlink
        Assert.Equal("Live Events", Entities.Strings.Resolve(live.TitleId));

        var pop = NodeOf("spotify:genre:0JQ5DAqbMKFEC4WFtoNRpw");                   // a genre is its page
        Assert.Equal(pop.Slot, tiles.Targets(directory.Slot)[2]);
        Assert.Equal(0u, pop.Color);                                                // a malformed colour is none, not wrong
    }

    [Fact]
    public void Browse_page_lands_its_header_its_bands_their_cards_and_their_tiles()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.BrowsePage(Fixture("browse-page.json"), "spotify:page:0JQ5DAqbMKFSi39LMRT0Cy"u8, 0, s);
        TestScope.CommitAndPublish(s);

        var page = NodeOf("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy");
        Assert.True(page.Knows(BrowseFields.Page));
        Assert.Equal(0xFFDC148Cu, page.Accent);
        Assert.Equal(11, page.TotalSections);
        Assert.Equal(10, page.NextSectionOffset);
        Assert.Equal("Music", Entities.Strings.Resolve(page.TitleId));             // a node nobody listed gets its title

        var bands = Entities.Current.Edges.BrowseSections;
        Assert.Equal(2, bands.Count(page.Slot));
        Assert.Equal(EdgeState.Partial, bands.State(page.Slot));                    // 2 of 11

        // The shelf: its kind was read FIRST even though the wire put `sectionItems` before `data`.
        var shelf = new Section(bands.Targets(page.Slot)[0]);
        Assert.Equal(SectionKind.BrowseShelf, shelf.Kind);
        Assert.Equal("Hip-Hop Workout Music", Entities.Strings.Resolve(shelf.TitleId));
        Assert.Equal(8, shelf.Total);
        Assert.Equal(2, shelf.Raw);
        Assert.Equal(1, shelf.Cards);
        Assert.Equal(1, shelf.Unsupported);                                         // the NotFound item
        Assert.Equal(PlaylistOf("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M").Slot, Assert.Single(shelf.CardSlots.ToArray()));

        // The grid: further CATEGORIES (browse is a tree), not entity cards.
        var grid = new Section(bands.Targets(page.Slot)[1]);
        Assert.Equal(SectionKind.BrowseCategoryGrid, grid.Kind);
        Assert.True(grid.CardSlots.IsEmpty);
        var categories = Entities.Current.Edges.SectionCategories;
        Assert.Equal(2, categories.Count(grid.Slot));
        Assert.Equal(NodeOf("spotify:page:pop").Slot, categories.Targets(grid.Slot)[0]);
        Assert.Equal(NodeOf("spotify:genre:rock").Slot, categories.Targets(grid.Slot)[1]);
        Assert.Equal(0xFF477D95u, NodeOf("spotify:page:pop").Color);
    }

    [Fact]
    public void A_browse_page_body_with_only_its_typename_is_a_real_empty_page()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.BrowsePage("""{"data":{"browse":{"__typename":"BrowseSectionContainer"}}}"""u8, "spotify:page:x"u8, 0, s);
        TestScope.CommitAndPublish(s);

        var page = NodeOf("spotify:page:x");
        Assert.True(page.Knows(BrowseFields.Page));
        Assert.Equal(0, Entities.Current.Edges.BrowseSections.Count(page.Slot));
    }

    [Fact]
    public void A_browse_section_page_lands_at_its_offset_with_the_raw_cursor()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var uri = Spotify.Decode.BrowseSection(Fixture("browse-section.json"), 20, s);
        Assert.False(uri.IsEmpty);
        TestScope.CommitAndPublish(s);

        var band = Entities.Section("spotify:section:weekly".AsSpan());
        Assert.Equal(40, band.NextOffset);                                          // passed through untouched
        Assert.Equal(74, band.Total);
        Assert.Equal(22, band.Raw);                                                 // offset 20 + the 2 items of this page
        var cards = Entities.Current.Edges.SectionCards;
        Assert.Equal(22, cards.Count(band.Slot));                                   // a page at 20 leaves 0-19 to arrive
        Assert.Equal(EdgeState.Partial, cards.State(band.Slot));
        Assert.Equal(PlaylistOf("spotify:playlist:37i9dQZEVXbLRQDuF5jeBp").Slot, cards.Targets(band.Slot)[21]);
    }

    [Fact]
    public void A_home_section_drill_lands_its_band_and_accounts_for_every_raw_item()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var uri = Spotify.Decode.HomeSection(Fixture("home-section.json"), 0, s);
        Assert.False(uri.IsEmpty);
        TestScope.CommitAndPublish(s);

        var band = Entities.Section("spotify:section:0JQ5DAIiKWzVFULQfUm85Y".AsSpan());
        Assert.Equal("Made for you", Entities.Strings.Resolve(band.TitleId));
        Assert.Equal("Picked today", Entities.Strings.Resolve(band.SubtitleId));
        Assert.Equal(SectionKind.HomeGeneric, band.Kind);
        Assert.Equal(64, band.Total);
        Assert.Equal(20, band.NextOffset);
        Assert.Equal(3, band.Raw);
        Assert.Equal(band.Raw, band.Cards + band.Unsupported + band.Duplicates);
        Assert.Equal(2, band.CardSlots.Length);

        // A 200 whose homeSections carried nothing is "this did not work", never an empty band.
        var nothing = Staging.Rent();
        Assert.True(Spotify.Decode.HomeSection("""{"data":{"homeSections":{"sections":[]}}}"""u8, 0, nothing).IsEmpty);
        Assert.True(Spotify.Decode.HomeSection("""{"data":{}}"""u8, 0, nothing).IsEmpty);
        Staging.Return(nothing);
    }

    // ── track hits carry the album cover and a nameless hit stays re-askable (S3/S4) ───────────────────────────────────

    [Fact]
    public void A_pathfinder_track_hit_takes_its_album_cover()
    {
        // S3: `NodeProperty`'s `albumOfTrack` branch staged the ALBUM's `coverArt`, but the track node itself never
        // picked it up — every pathfinder-sourced track (search hits, top tracks, album paging) landed with no art.
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.Export("""
        { "data": { "searchV2": { "tracksV2": { "items": [
          { "item": { "data": {
            "uri": "spotify:track:0TDLuuLlV54CkRRUOahJb4",
            "name": "Titanium",
            "albumOfTrack": { "uri": "spotify:album:1I80HwIDdWXtmA3Fqsbqnl", "coverArt": { "sources": [
              { "url": "https://i.scdn.co/image/cover" }
            ] } }
          } } }
        ] } } } }
        """u8, s, "wavee:search:00:takes-cover"u8);
        TestScope.CommitAndPublish(s);

        var track = TrackOf("spotify:track:0TDLuuLlV54CkRRUOahJb4");
        Assert.True(track.Knows(TrackFields.Identity));
        Assert.Equal("https://i.scdn.co/image/cover", Entities.Strings.Resolve(track.ImageId));
    }

    [Fact]
    public void A_nameless_pathfinder_track_hit_does_not_seal_its_identity()
    {
        // S4: a hit with a uri but no `name` (a region-substituted or deleted catalogue row) must not seal Identity —
        // `Stage`'s Track arm declared it unconditionally, sealing a blank title forever ("2 · 479M plays").
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.Export("""
        { "data": { "searchV2": { "tracksV2": { "items": [
          { "item": { "data": { "uri": "spotify:track:2plbrEY59IikOBgBGLjaoe" } } }
        ] } } } }
        """u8, s, "wavee:search:00:nameless"u8);
        TestScope.CommitAndPublish(s);

        var track = TrackOf("spotify:track:2plbrEY59IikOBgBGLjaoe");
        Assert.True(track.IsValid);                              // a real edge target, not dropped
        Assert.False(track.Knows(TrackFields.Identity));         // and re-askable, not sealed blank
    }

    // ── gander (G-045) ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gander_notifications_become_social_rows_with_their_required_fields_and_their_verdicts()
    {
        var rows = new List<Notification>();
        int n = Spotify.Decode.Notifications(Fixture("gander-notifications.json"), rows);

        Assert.Equal(3, n);
        var follow = rows[0];
        Assert.Equal("ntf-1001", follow.Id);
        Assert.Equal(NotifyCategory.Social, follow.Category);
        Assert.Equal("fakeuser42 started following you", follow.Title);
        Assert.True(follow.IsUnread);
        Assert.Equal(new DateTimeOffset(2026, 7, 9, 16, 2, 24, 11, TimeSpan.Zero).ToUnixTimeMilliseconds(), follow.TimestampMs);
        Assert.Equal(SocialActionType.Navigate, follow.ActionType);
        Assert.Equal("spotify:user:fakeuser42", follow.ActionUri);
        Assert.Equal("https://i.example/img/fakeuser42.jpg", follow.ImageUrl);
        Assert.Equal("fakeuser42", follow.ActName);

        Assert.Equal(SocialActionType.NavigateWebview, rows[1].ActionType);
        Assert.False(rows[1].IsUnread);
        Assert.Equal("Ada", rows[1].ActName);                                       // the FIRST non-blank name
        Assert.Null(rows[2].ActionUri);
        Assert.Equal(SocialActionType.NavigateWebview, rows[2].ActionType);         // no action is a web view (0.2.9)
    }

    [Theory]
    [InlineData("2026-07-09T16:02:24.011Z", 2026, 7, 9, 16, 2, 24, 11)]
    [InlineData("2026-07-09T16:02:24Z", 2026, 7, 9, 16, 2, 24, 0)]
    [InlineData("2026-07-09T18:02:24.5+02:00", 2026, 7, 9, 16, 2, 24, 500)]
    public void An_iso_instant_parses_to_unix_milliseconds(string iso, int y, int mo, int d, int h, int mi, int s, int ms)
        => Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, s, ms, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                        Spotify.Decode.IsoInstantMs(System.Text.Encoding.UTF8.GetBytes(iso)));

    // ── the allocation gate (P1, P8) ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_warm_pathfinder_decode_allocates_nothing()
    {
        // The home fixture is the biggest answer the app receives (806 KB, 31 bands, hundreds of cards). Two warm-up
        // passes grow the arena, the row lists and the edge buffers; the third must cost zero bytes.
        TestScope.Fresh();
        var json = Fixture("home.json");
        var s = Staging.Rent();
        for (int i = 0; i < 2; i++) { Spotify.Decode.Export(json, s); s.Reset(); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Spotify.Decode.Export(json, s);
        long after = GC.GetAllocatedBytesForCurrentThread();
        s.Reset();
        Staging.Return(s);

        Assert.Equal(0L, after - before);
    }
}
