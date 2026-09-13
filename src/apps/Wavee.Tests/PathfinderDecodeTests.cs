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
        Assert.Equal(10, first.Cards);
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
        Assert.Equal(24, rootlist.Count(me.Slot));

        // The first row is Liked Songs — a pseudo playlist, and the one row the library never fetches.
        var liked = PlaylistOf("spotify:collection:tracks");
        Assert.Equal(liked.Slot, rootlist.Targets(me.Slot)[0]);
        Assert.Equal(0, rootlist.Payload(me.Slot)[0].Position);
    }

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
