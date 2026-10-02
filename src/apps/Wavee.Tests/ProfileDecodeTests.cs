// ── Wavee.Tests/ProfileDecodeTests.cs — user-profile-view/v3 → the user row and the four profile relations ───────────
//
// Gate for `Spotify/Spotify.Decode.Profile.cs` (profile pages plan, D2). The fixtures are BUILT with the generated
// `Wavee.Protocol.UserProfileView` classes (`Spotify/Protos/user_profile_view.proto`, derived from the 2026-10-01
// capture), decoded by the hand-written reader, committed the way one UI drain commits, and read back through the
// handle's profile accessors — nothing here asserts on a staged row.

using System.Text;
using FluentGpu.Foundation;
using Google.Protobuf;
using Wavee;
using Xunit;
using Upv = Wavee.Protocol.UserProfileView;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class ProfileDecodeTests
{
    const string Owner = "spotify:user:u2";
    const string Cover = "ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaaa";
    const string Avatar = "ab6775700000ee85bbbbbbbbbbbbbbbbbbbbbbbb";

    static string PlaylistUri(int i) => "spotify:playlist:" + i.ToString("D22");
    static string ArtistUri(int i) => "spotify:artist:" + i.ToString("D22");

    static User UserOf(string uri) => Entities.User(EntityUri.Parse(uri.AsSpan()));
    static string Text(StringId id) => Entities.Strings.Resolve(id);

    static Upv.PublicPlaylist PlaylistCard(int i, string image = "", int followers = 0, bool following = false)
        => new()
        {
            Uri = PlaylistUri(i), Name = "Playlist " + i, ImageUrl = image, FollowersCount = followers,
            OwnerName = "Someone", OwnerUri = Owner, IsFollowing = following,
        };

    static Upv.RecentlyPlayedArtist ArtistCard(int i, bool ownerFollows = false, int followers = 0, string image = "")
        => new() { Uri = ArtistUri(i), Name = "Artist " + i, ImageUrl = image, FollowersCount = followers, OwnerFollows = ownerFollows };

    static Upv.ProfileEntry UserEntry(int i, bool following = false, uint color = 0, bool named = true)
        => new()
        {
            Uri = "spotify:user:friend" + i, Name = named ? "Friend " + i : "", ImageUrl = "https://i.scdn.co/image/" + Avatar,
            FollowersCount = i, IsFollowingUser = following, Color = (int)color,
        };

    static Upv.ProfileEntry ArtistEntry(int i, bool following = false)
        => new() { Uri = ArtistUri(i), Name = "Artist " + i, ImageUrl = "https://i.scdn.co/image/" + Cover, FollowersCount = 100 + i, IsFollowing = following };

    static User Land(Action<Staging> decode, string uri = Owner)
    {
        var s = Staging.Rent();
        decode(s);
        TestScope.CommitAndPublish(s);
        return UserOf(uri);
    }

    static User View(Upv.UserProfile view, string uri = Owner)
        => Land(s => Spotify.Decode.ProfileView(view.ToByteArray(), Encoding.UTF8.GetBytes(uri), s), uri);

    static User List(Upv.ProfileList list, Relation relation, int cap = Spotify.Decode.ProfileListCap)
        => Land(s => Spotify.Decode.ProfileList(list.ToByteArray(), Encoding.UTF8.GetBytes(Owner), relation, s, cap));

    // ── the header ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_profile_view_lands_the_header_columns_and_flags()
    {
        TestScope.Fresh();
        var u = View(new Upv.UserProfile
        {
            Uri = "spotify:user:not-the-asked-one", Name = "Alex", ImageUrl = "spotify:image:" + Avatar,
            FollowersCount = 118, FollowingCount = 1063, IsFollowing = true, TotalPublicPlaylistsCount = 94,
            Color = 0x1E3264, AllowFollows = true, ShowFollows = true,
        });

        Assert.True(u.Knows(UserFields.Identity | UserFields.Social | UserFields.Follow));
        Assert.Equal("Alex", Text(u.NameId));
        Assert.Equal("https://i.scdn.co/image/" + Avatar, Text(u.ImageId));          // the token becomes its cdn url
        Assert.Equal(0xFF1E3264u, u.Color);
        Assert.Equal(118, u.Followers);
        Assert.Equal(1063, u.Following);
        Assert.Equal(94, u.PublicPlaylists);
        Assert.True(u.IsFollowedByViewer);
        Assert.True(u.ShowFollows);
        Assert.True(u.AllowFollows);
        Assert.False(u.IsCurrentUser);
        Assert.False(u.IsUnavailable);

        // The identity is the uri the caller ASKED with, never field 1.
        Assert.False(UserOf("spotify:user:not-the-asked-one").Knows(UserFields.Identity));

        var me = View(new Upv.UserProfile { Name = "Me", IsCurrentUser = true }, "spotify:user:me2");
        Assert.True(me.IsCurrentUser);
        Assert.False(me.IsFollowedByViewer);
        Assert.False(me.ShowFollows);
    }

    [Fact]
    public void A_view_with_no_image_blanks_the_avatar_at_full_and_a_zero_colour_is_not_stated()
    {
        TestScope.Fresh();
        var withImage = View(new Upv.UserProfile { Name = "Alex", ImageUrl = "https://i.scdn.co/image/" + Avatar, Color = 0x1E3264 });
        Assert.NotEqual("", Text(withImage.ImageId));

        var u = View(new Upv.UserProfile { Name = "Alex" });                         // the user removed the picture
        Assert.Equal("", Text(u.ImageId));
        Assert.Equal(0xFF1E3264u, u.Color);                                          // 0 = not stated: never a blank
    }

    [Fact]
    public void RgbColor_masks_any_sign_and_keeps_zero_as_not_stated()
    {
        Assert.Equal(0xFF1E3264u, Spotify.Decode.RgbColor(0x1E3264));
        Assert.Equal(0u, Spotify.Decode.RgbColor(0));
        Assert.Equal(0u, Spotify.Decode.RgbColor(0xFF000000));                       // alpha alone says nothing
        Assert.Equal(0xFFFFFFFFu, Spotify.Decode.RgbColor(unchecked((ulong)-1L)));   // a negative int32 is sign-extended
    }

    // ── the two riding shelves ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_profile_view_lands_its_playlists_and_artists_as_two_runs_with_payloads()
    {
        TestScope.Fresh();
        string mosaic = $"spotify:mosaic:{Cover}:{Cover}:{Cover}:{Cover}";
        var view = new Upv.UserProfile { Name = "Alex", TotalPublicPlaylistsCount = 94 };
        view.PublicPlaylists.Add(PlaylistCard(1, "https://i.scdn.co/image/" + Cover, followers: 7, following: true));
        view.PublicPlaylists.Add(PlaylistCard(2, "spotify:image:" + Cover));
        view.PublicPlaylists.Add(PlaylistCard(3, mosaic));
        view.RecentlyPlayedArtists.Add(ArtistCard(1, ownerFollows: true, followers: 1000, image: "https://i.scdn.co/image/" + Cover));
        view.RecentlyPlayedArtists.Add(ArtistCard(2, image: "spotify:image:" + Cover));
        var u = View(view);

        var playlists = u.ProfileTargets(ProfileShelf.Playlists);
        var cards = u.ProfileCards(ProfileShelf.Playlists);
        Assert.Equal(3, playlists.Length);
        Assert.Equal(new ProfileCardEdge(EntityKind.Playlist, ProfileCardFlags.ViewerFollows, 7), cards[0]);
        Assert.Equal(new ProfileCardEdge(EntityKind.Playlist, ProfileCardFlags.None, 0), cards[1]);
        for (int i = 0; i < 3; i++) Assert.Equal("Playlist " + (i + 1), Text(new Playlist(playlists[i]).TitleId));   // wire order
        Assert.Equal("https://i.scdn.co/image/" + Cover, Text(new Playlist(playlists[0]).ImageId));
        Assert.Equal("https://i.scdn.co/image/" + Cover, Text(new Playlist(playlists[1]).ImageId));   // the token, normalised
        Assert.Equal(mosaic, Text(new Playlist(playlists[2]).ImageId));                               // a mosaic stays verbatim
        Assert.Equal(u.Slot, new Playlist(playlists[0]).Owner.Slot);
        Assert.Equal(EdgeState.Partial, u.ProfileReadiness(ProfileShelf.Playlists));
        Assert.Equal(94, u.ProfileTotal(ProfileShelf.Playlists));
        Assert.Equal(3, u.ProfileCount(ProfileShelf.Playlists));

        var artists = u.ProfileTargets(ProfileShelf.Artists);
        var artistCards = u.ProfileCards(ProfileShelf.Artists);
        Assert.Equal(2, artists.Length);
        Assert.Equal(new ProfileCardEdge(EntityKind.Artist, ProfileCardFlags.OwnerFollows, 1000), artistCards[0]);
        Assert.Equal(new ProfileCardEdge(EntityKind.Artist, ProfileCardFlags.None, 0), artistCards[1]);
        Assert.Equal("Artist 1", new Artist(artists[0]).Name);
        Assert.Equal("Artist 2", new Artist(artists[1]).Name);
        Assert.Equal("https://i.scdn.co/image/" + Cover, Text(new Artist(artists[1]).ImageId));
        Assert.Equal(EdgeState.Complete, u.ProfileReadiness(ProfileShelf.Artists));
    }

    [Fact]
    public void A_complete_playlist_shelf_states_the_wire_total()
    {
        TestScope.Fresh();
        var view = new Upv.UserProfile { Name = "Alex", TotalPublicPlaylistsCount = 2 };
        view.PublicPlaylists.Add(PlaylistCard(1));
        view.PublicPlaylists.Add(PlaylistCard(2));
        var u = View(view);

        Assert.Equal(EdgeState.Complete, u.ProfileReadiness(ProfileShelf.Playlists));
        Assert.Equal(2, u.ProfileCount(ProfileShelf.Playlists));
    }

    [Fact]
    public void An_absent_recently_played_field_is_a_complete_empty_shelf()
    {
        TestScope.Fresh();
        var u = View(new Upv.UserProfile { Name = "Alex" });          // no playlists, no artists (the owner's privacy switch)

        Assert.Equal(EdgeState.Complete, u.ProfileReadiness(ProfileShelf.Artists));
        Assert.Equal(0, u.ProfileCount(ProfileShelf.Artists));
        Assert.Equal(EdgeState.Complete, u.ProfileReadiness(ProfileShelf.Playlists));
        Assert.Equal(0, u.ProfileCount(ProfileShelf.Playlists));
    }

    [Fact]
    public void Interleaved_repeated_fields_still_land_contiguous_runs()
    {
        TestScope.Fresh();
        // Four single-card encodings concatenated in the order 8, 7, 8, 7: protobuf merges repeated fields in wire
        // order, and a serializer is free to interleave them — the two runs must not slice each other's edges.
        var p1 = new Upv.UserProfile { TotalPublicPlaylistsCount = 2 };
        p1.PublicPlaylists.Add(PlaylistCard(1));
        var a1 = new Upv.UserProfile();
        a1.RecentlyPlayedArtists.Add(ArtistCard(1));
        var p2 = new Upv.UserProfile { Name = "Alex" };
        p2.PublicPlaylists.Add(PlaylistCard(2));
        var a2 = new Upv.UserProfile();
        a2.RecentlyPlayedArtists.Add(ArtistCard(2));
        byte[] body = [.. p1.ToByteArray(), .. a1.ToByteArray(), .. p2.ToByteArray(), .. a2.ToByteArray()];

        var u = Land(s => Spotify.Decode.ProfileView(body, Encoding.UTF8.GetBytes(Owner), s));

        var playlists = u.ProfileTargets(ProfileShelf.Playlists);
        var artists = u.ProfileTargets(ProfileShelf.Artists);
        Assert.Equal(2, playlists.Length);
        Assert.Equal(2, artists.Length);
        Assert.Equal("Playlist 1", Text(new Playlist(playlists[0]).TitleId));
        Assert.Equal("Playlist 2", Text(new Playlist(playlists[1]).TitleId));
        Assert.Equal("Artist 1", new Artist(artists[0]).Name);
        Assert.Equal("Artist 2", new Artist(artists[1]).Name);
        Assert.Equal(EdgeState.Complete, u.ProfileReadiness(ProfileShelf.Playlists));
    }

    // ── the 404 ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_404_profile_is_a_known_negative_not_a_miss()
    {
        TestScope.Fresh();
        var u = Land(s => Spotify.Decode.ProfileUnavailable("spotify:user:gone"u8, s), "spotify:user:gone");

        Assert.True(u.Knows(UserFields.Social | UserFields.Follow));
        Assert.True(u.IsUnavailable);
        Assert.Equal(EdgeState.Complete, u.ProfileReadiness(ProfileShelf.Playlists));
        Assert.Equal(0, u.ProfileCount(ProfileShelf.Playlists));
        Assert.Equal(EdgeState.Complete, u.ProfileReadiness(ProfileShelf.Artists));
        Assert.Equal(0, u.ProfileCount(ProfileShelf.Artists));
        Assert.Equal(ProfileLoad.Unavailable, ProfileLoadRule.Header(u));
    }

    // ── the two lists ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_following_list_keeps_artists_then_users_across_two_tables()
    {
        TestScope.Fresh();
        var list = new Upv.ProfileList();
        list.Entries.Add(ArtistEntry(1, following: true));
        list.Entries.Add(ArtistEntry(2));
        list.Entries.Add(UserEntry(1, following: true, color: 0x1E3264));
        list.Entries.Add(UserEntry(2));
        var u = List(list, Relation.ProfileFollowing);

        var targets = u.ProfileTargets(ProfileShelf.Following);
        var cards = u.ProfileCards(ProfileShelf.Following);
        Assert.Equal(4, targets.Length);
        Assert.Equal(EdgeState.Complete, u.ProfileReadiness(ProfileShelf.Following));
        Assert.Equal(
            new[] { EntityKind.Artist, EntityKind.Artist, EntityKind.User, EntityKind.User },
            new[] { cards[0].Kind, cards[1].Kind, cards[2].Kind, cards[3].Kind });

        Assert.Equal("Artist 1", new Artist(targets[0]).Name);
        Assert.Equal("Friend 1", Text(new User(targets[2]).NameId));
        Assert.Equal(0xFF1E3264u, new User(targets[2]).Color);
        Assert.Equal(0u, new User(targets[3]).Color);
        Assert.True(cards[0].ViewerFollows);              // an artist's f7
        Assert.False(cards[1].ViewerFollows);
        Assert.True(cards[2].ViewerFollows);              // a user's f6
        Assert.False(cards[3].ViewerFollows);
        Assert.Equal(101, cards[0].Followers);
        Assert.Equal(2, cards[3].Followers);
    }

    [Fact]
    public void A_followers_list_admits_users_only()
    {
        TestScope.Fresh();
        var list = new Upv.ProfileList();
        list.Entries.Add(ArtistEntry(1));
        list.Entries.Add(UserEntry(1));
        list.Entries.Add(new Upv.ProfileEntry { Uri = PlaylistUri(1), Name = "Not a person" });
        list.Entries.Add(UserEntry(2));
        var u = List(list, Relation.ProfileFollowers);

        var targets = u.ProfileTargets(ProfileShelf.Followers);
        Assert.Equal(2, targets.Length);
        Assert.Equal("Friend 1", Text(new User(targets[0]).NameId));
        Assert.Equal("Friend 2", Text(new User(targets[1]).NameId));
        Assert.All(u.ProfileCards(ProfileShelf.Followers).ToArray(), c => Assert.Equal(EntityKind.User, c.Kind));
    }

    [Fact]
    public void An_empty_list_answer_is_complete_and_empty()
    {
        TestScope.Fresh();
        var u = Land(s => Spotify.Decode.ProfileList([], Encoding.UTF8.GetBytes(Owner), Relation.ProfileFollowers, s));

        Assert.Equal(EdgeState.Complete, u.ProfileReadiness(ProfileShelf.Followers));
        Assert.Equal(0, u.ProfileCount(ProfileShelf.Followers));
    }

    [Fact]
    public void A_list_past_the_cap_lands_its_head_partial_with_the_true_total()
    {
        TestScope.Fresh();
        var list = new Upv.ProfileList();
        for (int i = 1; i <= 5; i++) list.Entries.Add(UserEntry(i));
        var u = List(list, Relation.ProfileFollowers, cap: 3);

        Assert.Equal(3, u.ProfileCount(ProfileShelf.Followers));
        Assert.Equal(EdgeState.Partial, u.ProfileReadiness(ProfileShelf.Followers));
        Assert.Equal(5, u.ProfileTotal(ProfileShelf.Followers));
        Assert.Equal("Friend 3", Text(new User(u.ProfileTargets(ProfileShelf.Followers)[2]).NameId));

        // At the cap exactly is whole.
        var exact = new Upv.ProfileList();
        for (int i = 1; i <= 3; i++) exact.Entries.Add(UserEntry(i));
        var whole = List(exact, Relation.ProfileFollowers, cap: 3);
        Assert.Equal(EdgeState.Complete, whole.ProfileReadiness(ProfileShelf.Followers));
    }

    [Fact]
    public void A_nameless_list_user_lands_on_the_edge_without_sealing_identity()
    {
        TestScope.Fresh();
        var list = new Upv.ProfileList();
        list.Entries.Add(UserEntry(1, named: false));
        var u = List(list, Relation.ProfileFollowers);

        var targets = u.ProfileTargets(ProfileShelf.Followers);
        Assert.Single(targets.ToArray());
        Assert.False(new User(targets[0]).Knows(UserFields.Identity));      // a later kind-15 answer may still name it
    }

    [Fact]
    public void A_list_for_a_relation_that_is_not_a_profile_list_stages_nothing()
    {
        TestScope.Fresh();
        var list = new Upv.ProfileList();
        list.Entries.Add(UserEntry(1));
        var u = List(list, Relation.PlaylistTracks);

        Assert.Equal(EdgeState.Unknown, u.ProfileReadiness(ProfileShelf.Followers));
        Assert.Equal(EdgeState.Unknown, u.ProfileReadiness(ProfileShelf.Following));
    }

    // ── the allocation gate (P1, P8) ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_warm_profile_decode_allocates_nothing()
    {
        TestScope.Fresh();
        var view = new Upv.UserProfile
        {
            Name = "Alex", ImageUrl = "spotify:image:" + Avatar, FollowersCount = 118, FollowingCount = 1063,
            TotalPublicPlaylistsCount = 94, Color = 0x1E3264, ShowFollows = true, AllowFollows = true, IsFollowing = true,
        };
        for (int i = 1; i <= 10; i++)
        {
            view.PublicPlaylists.Add(PlaylistCard(i, i % 2 == 0 ? "spotify:image:" + Cover : $"spotify:mosaic:{Cover}:{Cover}:{Cover}:{Cover}", i, i % 3 == 0));
            view.RecentlyPlayedArtists.Add(ArtistCard(i, i % 2 == 0, 10 * i, "spotify:image:" + Cover));
        }
        var list = new Upv.ProfileList();
        for (int i = 1; i <= 20; i++) list.Entries.Add(i % 2 == 0 ? UserEntry(i, i % 4 == 0, 0x509BF5) : ArtistEntry(i));
        byte[] viewBytes = view.ToByteArray(), listBytes = list.ToByteArray(), owner = Encoding.UTF8.GetBytes(Owner);

        var s = Staging.Rent();
        for (int i = 0; i < 2; i++)
        {
            Spotify.Decode.ProfileView(viewBytes, owner, s);
            Spotify.Decode.ProfileList(listBytes, owner, Relation.ProfileFollowing, s);
            s.Reset();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Spotify.Decode.ProfileView(viewBytes, owner, s);
        Spotify.Decode.ProfileList(listBytes, owner, Relation.ProfileFollowing, s);
        long after = GC.GetAllocatedBytesForCurrentThread();
        s.Reset();
        Staging.Return(s);

        Assert.Equal(0L, after - before);
    }
}
