// ── Wavee.Tests/SpotifyDecodeEntryTests.cs — the provider's raw-body folds (gap batch B1b, G-040, G-042) ─────────────
//
// `Spotify/Spotify.Decode.Entry.cs`: the five answers the routing table names and nothing decoded before — popcount
// (Saves), permission/base (Visibility), the extended top-track list (Chart), discography.all (ArtistReleases) and the
// collection-v2 pages (every library relation). Every fact decodes a hand-built body, COMMITS it the way a UI drain
// does, and reads a handle — never a staged row. The bodies are the captured shapes (popcount.proto's own sample bytes,
// the permission capture's exact 12 bytes), built by the generated encoders where a generated type exists.

using System.Text;
using Google.Protobuf;
using Wavee;
using Xunit;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class SpotifyDecodeEntryTests
{
    const string PlaylistUri = "spotify:playlist:0aL7M6qKDacHCtUUch3AhB";
    const string ArtistUri = "spotify:artist:04gDigrS5kc9YWfZHwBETP";
    const string MeUri = "spotify:user:wavee-test";
    const string TrackA = "spotify:track:03YlpHoTSPFiuJKPznChAT";
    const string TrackB = "spotify:track:0GxQ1A5L9xnMOytbP6eKBG";
    const string TrackC = "spotify:track:0NT3Jrqgntj9T3DZNEEMkU";
    const string AlbumA = "spotify:album:08Dvx4JDDAICQr0Vzeu0ve";
    const string AlbumB = "spotify:album:0BCjGDBIymcwf4etd4KBgu";
    const string AlbumC = "spotify:album:0JfWflwFS8yOSELbH7bDbQ";

    static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    static Playlist PlaylistOf(string uri) => Entities.Playlist(EntityUri.Parse(uri.AsSpan()));
    static Artist ArtistOf(string uri) => Entities.Artist(EntityUri.Parse(uri.AsSpan()));
    static int SlotOf(string uri) => EntityUri.Parse(uri.AsSpan()).Kind switch
    {
        EntityKind.Track => Entities.Track(EntityUri.Parse(uri.AsSpan())).Slot,
        EntityKind.Album => Entities.Album(EntityUri.Parse(uri.AsSpan())).Slot,
        _ => Entities.User(EntityUri.Parse(uri.AsSpan())).Slot,
    };

    // ── popcount ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>popcount.proto's own sample: <c>08 9809 10 01 38 cc04</c> — field 1 is 1176 (the zig-zag shadow),
    /// field 7 is 588, and 588 is the count.</summary>
    [Fact]
    public void A_popcount_answer_lands_field_seven_as_the_save_count()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.Popcount([0x08, 0x98, 0x09, 0x10, 0x01, 0x38, 0xcc, 0x04], Utf8(PlaylistUri), s);
        TestScope.CommitAndPublish(s);

        var playlist = PlaylistOf(PlaylistUri);
        Assert.True(playlist.Knows(PlaylistFields.Saves));
        Assert.Equal(588, playlist.Saves);
    }

    /// <summary>The DJ's 128 M is a platform aggregate, not a save count: answered, as nothing to render.</summary>
    [Fact]
    public void An_implausible_save_count_is_answered_as_zero()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.Popcount([0x08, 0x00, 0x10, 0x01, 0x38, 0x90, 0xcf, 0x92, 0x3d, 0x40, 0x01], Utf8(PlaylistUri), s);
        TestScope.CommitAndPublish(s);

        var playlist = PlaylistOf(PlaylistUri);
        Assert.True(playlist.Knows(PlaylistFields.Saves));
        Assert.Equal(0, playlist.Saves);
    }

    // ── permission/base ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The capture's exact body for an own playlist: <c>{ revision = 34c5325001893817, level = 2 VIEWER }</c>.</summary>
    [Fact]
    public void A_viewer_base_permission_is_a_public_playlist_with_its_revision()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.PermissionBase([0x0a, 0x08, 0x34, 0xc5, 0x32, 0x50, 0x01, 0x89, 0x38, 0x17, 0x10, 0x02],
                                      Utf8(PlaylistUri), s);
        TestScope.CommitAndPublish(s);

        var playlist = PlaylistOf(PlaylistUri);
        Assert.True(playlist.Knows(PlaylistFields.Visibility));
        Assert.True(playlist.IsPublic);
    }

    [Fact]
    public void A_blocked_base_permission_is_a_private_playlist()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.PermissionBase([0x10, 0x01], Utf8(PlaylistUri), s);
        TestScope.CommitAndPublish(s);

        var playlist = PlaylistOf(PlaylistUri);
        Assert.True(playlist.Knows(PlaylistFields.Visibility));
        Assert.False(playlist.IsPublic);
    }

    /// <summary>No level is silence, and silence keeps "public unless told" rather than inventing a private flag.</summary>
    [Fact]
    public void A_permission_with_no_level_says_nothing()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.PermissionBase([0x0a, 0x01, 0x00], Utf8(PlaylistUri), s);
        TestScope.CommitAndPublish(s);

        Assert.False(PlaylistOf(PlaylistUri).Knows(PlaylistFields.Visibility));
    }

    // ── the extended top-track list ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_extended_top_tracks_replace_the_popular_run_and_answer_the_chart()
    {
        TestScope.Fresh();
        string json = "{\"tracks\":[{\"uri\":\"" + TrackA + "\"},{\"uri\":\"" + AlbumA + "\"},{\"uri\":\"" + TrackB
                    + "\",\"extra\":{\"uri\":\"ignored\"}}]}";
        var s = Staging.Rent();
        Spotify.Decode.ArtistTopTracks(Utf8(json), Utf8(ArtistUri), s);
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf(ArtistUri);
        Assert.True(artist.Knows(ArtistFields.Chart));
        Assert.Equal(new[] { SlotOf(TrackA), SlotOf(TrackB) }, artist.PopularSlots.ToArray());
    }

    [Fact]
    public void An_empty_top_track_list_is_still_an_answered_chart()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.ArtistTopTracks(Utf8("{\"tracks\":[]}"), Utf8(ArtistUri), s);
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf(ArtistUri);
        Assert.True(artist.Knows(ArtistFields.Chart));
        Assert.Equal(0, artist.PopularSlots.Length);
    }

    // ── discography.all ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A group is ONE row (its first release), the page lands at its offset with the server's group total,
    /// and the artist's name — which this answer does not carry — is left alone.</summary>
    [Fact]
    public void A_discography_page_lands_one_release_per_group_and_leaves_the_artist_alone()
    {
        TestScope.Fresh();
        var named = Staging.Rent();
        ref var row = ref named.Artists.RowFor(EntityUri.Parse(ArtistUri.AsSpan()).Id, Authority.Full, (uint)ArtistFields.Identity);
        row.Name = named.Text("Maroon 5");
        TestScope.CommitAndPublish(named);

        string json = "{\"data\":{\"artistUnion\":{\"discography\":{\"all\":{\"totalCount\":7,\"items\":["
                    + Group(AlbumA, AlbumC) + "," + Group(AlbumB) + "]}}}}}";
        var s = Staging.Rent();
        Spotify.Decode.DiscographyAll(Utf8(json), Utf8(ArtistUri), 0, s);
        TestScope.CommitAndPublish(s);

        var artist = ArtistOf(ArtistUri);
        var releases = Entities.Current.Edges.ArtistReleases;
        Assert.Equal(new[] { SlotOf(AlbumA), SlotOf(AlbumB) }, releases.Targets(artist.Slot).ToArray());
        Assert.Equal("Maroon 5", Entities.Strings.Resolve(Entities.Current.Artists.Name[artist.Slot]));

        static string Group(params string[] albums)
            => "{\"releases\":{\"items\":[" + string.Join(",", albums.Select(a => "{\"uri\":\"" + a + "\",\"name\":\"n\"}")) + "]}}";
    }

    // ── collection-v2 ───────────────────────────────────────────────────────────────────────────────────────────────

    static byte[] Page(string nextToken, params (string Uri, int At, bool Removed)[] items)
    {
        var page = new Col.PageResponse { NextPageToken = nextToken };
        foreach (var (uri, at, removed) in items) page.Items.Add(new Col.CollectionItem { Uri = uri, AddedAt = at, IsRemoved = removed });
        return page.ToByteArray();
    }

    /// <summary>The shared `collection` set: tracks are Liked, albums are SavedAlbums, a tombstone is left out, and
    /// every page of the walk is one whole relation.</summary>
    [Fact]
    public void Every_collection_page_lands_as_one_whole_relation_of_its_own_kind()
    {
        TestScope.Fresh();
        byte[][] pages =
        [
            Page("t1", (TrackA, 10, false), (AlbumA, 11, false), (TrackB, 12, true)),
            Page("", (TrackC, 13, false), (AlbumB, 14, false)),
        ];
        var s = Staging.Rent();
        Spotify.Decode.LibrarySet(pages, LibraryEdgeKind.Liked, Utf8(MeUri), s);
        Spotify.Decode.LibrarySet(pages, LibraryEdgeKind.SavedAlbums, Utf8(MeUri), s);
        TestScope.CommitAndPublish(s);

        int me = SlotOf(MeUri);
        Assert.Equal(new[] { SlotOf(TrackA), SlotOf(TrackC) }, Entities.Current.Edges.Liked.Targets(me).ToArray());
        Assert.Equal(new[] { SlotOf(AlbumA), SlotOf(AlbumB) }, Entities.Current.Edges.SavedAlbums.Targets(me).ToArray());
        Assert.Equal(10, Entities.Current.Edges.Liked.Payload(me)[0].AddedAt);
    }

    [Fact]
    public void An_empty_library_is_an_answered_empty_relation()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.LibrarySet([Page("")], LibraryEdgeKind.FollowedArtists, Utf8(MeUri), s);
        TestScope.CommitAndPublish(s);

        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.FollowedArtists.State(SlotOf(MeUri)));
    }

    /// <summary>Pins (the `ylpin` set, G-062) stage <see cref="Relation.Pins"/> with NO item-kind filter of their own —
    /// they are cross-kind — and it is the commit's <c>Relation.Pins</c> arm that drops a shape the sidebar cannot pin
    /// (a track). The album lands there and NOWHERE ELSE: never in Liked, which is what the old (now-deleted)
    /// `Decode.CollectionPage` got wrong for a Pins call (B2b).</summary>
    [Fact]
    public void A_pins_page_stages_cross_kind_targets_the_commit_arm_filters()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.LibrarySet([Page("", (AlbumA, 1, false), (TrackA, 2, false))], LibraryEdgeKind.Pins, Utf8(MeUri), s);
        TestScope.CommitAndPublish(s);

        int me = SlotOf(MeUri);
        Assert.Equal(new[] { SlotOf(AlbumA) }, Entities.Current.Edges.Pins.Targets(me).ToArray());
        Assert.Equal(0, Entities.Current.Edges.Liked.Targets(me).Length);
    }
}
