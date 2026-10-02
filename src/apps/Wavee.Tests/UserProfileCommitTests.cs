// ── Wavee.Tests/UserProfileCommitTests.cs — the profile pages' entity layer: groups, masks, avatar, shelves (plan D1) ──
//
// A profile is one user row plus four shelves. The facts below are column reads over a real scope, committed through the
// same `Staging` + `Entities.Commit` path a decoder takes: the two answer groups (Social, Follow) merge their flag bits
// under their OWN masks; an avatar only ever gets sharper, and only a Full answer can say "no avatar"; a stated colour is
// never blanked by a colourless answer; the cross-kind shelves resolve each target in its own table and drop the kinds a
// shelf does not admit; the current-user rule is the account row OR the view's flag; and an Invalidate keeps a known
// profile rendering while it asks again.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class UserProfileCommitTests : IDisposable
{
    const string Owner = "spotify:user:profile-commit";
    const string Tail = "0123456789abcdef01234567";                       // the 24-char artwork identity of an image id

    public UserProfileCommitTests()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
    }

    public void Dispose()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
    }

    static User UserOf(string uri) => Entities.User(EntityUri.Parse(uri.AsSpan()));
    static string Resolved(User u) => Entities.Strings.Resolve(u.ImageId);

    /// <summary>A 40-char user avatar url: 8-char prefix + 8-char SIZE marker + the 24-char artwork identity.</summary>
    static string Avatar(string size, string tail = Tail) => "https://i.scdn.co/image/ab677570" + size + tail;
    const string Px300 = "0000ee85", Px64 = "00003b82";

    static string ArtistUri(int i) => "spotify:artist:" + i.ToString("D22");
    static string PlaylistUri(int i) => "spotify:playlist:" + i.ToString("D22");
    static string AlbumUri(int i) => "spotify:album:" + i.ToString("D22");

    /// <summary>One user answer through the real commit path: the groups it speaks for, at one authority.</summary>
    static User CommitUser(string uri, Authority authority, UserFields known, string name = "", string image = "",
                           uint color = 0, UserFlags flags = UserFlags.None, int followers = 0, int following = 0,
                           int playlists = 0)
    {
        var s = Staging.Rent();
        ref var row = ref s.Users.RowFor(new StagedId(s.Text(uri)), authority, (uint)known);
        row.Name = s.Text(name);
        row.Image = s.Text(image);
        row.Color = color;
        row.Flags = (uint)flags;
        row.Followers = followers;
        row.Following = following;
        row.PublicPlaylists = playlists;
        TestScope.CommitAndPublish(s);
        return UserOf(uri);
    }

    static void StageShelf(Staging s, Relation relation, string owner, params (string Uri, ProfileCardFlags Flags, int Followers)[] cards)
    {
        var run = s.Run(relation);
        foreach (var card in cards)
        {
            ref var e = ref run.Add(new StagedId(s.Text(card.Uri)));
            e.At = card.Followers;
            e.B0 = (byte)card.Flags;
        }
        run.End(new StagedId(s.Text(owner)));
    }

    [Fact]
    public void Social_and_follow_flags_merge_under_their_own_masks()
    {
        TestScope.Fresh();

        // A Social answer that also asserts the follow bit: the bit is not Social's to set, so it is ignored.
        var u = CommitUser(Owner, Authority.Full, UserFields.Social,
            flags: UserFlags.ShowFollows | UserFlags.AllowFollows | UserFlags.Followed,
            followers: 118, following: 1063, playlists: 94);
        Assert.True(u.ShowFollows);
        Assert.True(u.AllowFollows);
        Assert.False(u.IsFollowedByViewer);
        Assert.Equal(UserFlags.ShowFollows | UserFlags.AllowFollows, u.Flags);
        Assert.Equal(118, u.Followers);
        Assert.Equal(94, u.PublicPlaylists);

        // The Follow answer sets only its own bit and leaves the social bits and counts where they were.
        CommitUser(Owner, Authority.Full, UserFields.Follow, flags: UserFlags.Followed);
        Assert.True(u.IsFollowedByViewer);
        Assert.True(u.ShowFollows);
        Assert.Equal(UserFlags.ShowFollows | UserFlags.AllowFollows | UserFlags.Followed, u.Flags);
        Assert.Equal(118, u.Followers);

        // A later Social answer turns one social bit off; the viewer's follow survives it.
        CommitUser(Owner, Authority.Full, UserFields.Social, flags: UserFlags.AllowFollows, followers: 119, following: 1063, playlists: 94);
        Assert.False(u.ShowFollows);
        Assert.True(u.AllowFollows);
        Assert.True(u.IsFollowedByViewer);
        Assert.Equal(119, u.Followers);

        // An unfollow clears the follow bit alone.
        CommitUser(Owner, Authority.Full, UserFields.Follow, flags: UserFlags.None);
        Assert.False(u.IsFollowedByViewer);
        Assert.True(u.AllowFollows);
        Assert.Equal(UserFlags.AllowFollows, u.Flags);
    }

    [Fact]
    public void An_empty_avatar_blanks_at_full_and_never_at_thin()
    {
        TestScope.Fresh();
        string avatar = Avatar(Px300);

        // Thin first, so the Thin re-answer below is ADMITTED by the authority ladder and only the avatar rule decides.
        var u = CommitUser(Owner, Authority.Thin, UserFields.Identity, name: "Ada", image: avatar);
        Assert.Equal(avatar, Resolved(u));
        Assert.Equal(avatar, u.Image);

        // A Thin mention (an owner chip, a list row) that carried no avatar says nothing about it.
        CommitUser(Owner, Authority.Thin, UserFields.Identity, name: "Ada");
        Assert.Equal(avatar, Resolved(u));
        Assert.Equal("Ada", u.Name);

        // A Full answer (kind 15 / the profile view) saying "no image" is the one that clears it.
        CommitUser(Owner, Authority.Full, UserFields.Identity, name: "Ada");
        Assert.True(u.ImageId.IsEmpty);
        Assert.Null(u.Image);
        Assert.Equal("Ada", u.Name);
    }

    [Fact]
    public void A_zero_colour_never_blanks_a_stated_one()
    {
        TestScope.Fresh();

        var u = CommitUser(Owner, Authority.Full, UserFields.Identity, name: "Ada", color: 0xFF1E3264u);
        Assert.Equal(0xFF1E3264u, u.Color);

        CommitUser(Owner, Authority.Full, UserFields.Identity, name: "Ada");             // colourless: says nothing
        Assert.Equal(0xFF1E3264u, u.Color);

        CommitUser(Owner, Authority.Full, UserFields.Identity, name: "Ada", color: 0xFFE8115Bu);
        Assert.Equal(0xFFE8115Bu, u.Color);                                               // a new stated colour replaces

        // The accessor is gated on Identity: a row that only ever answered Social paints no colour, whatever a recycled
        // slot's column still holds.
        var social = CommitUser("spotify:user:social-only", Authority.Full, UserFields.Social, followers: 1);
        Entities.Current.Users.Color[social.Slot] = 0xFF509BF5u;
        Assert.Equal(0u, social.Color);
    }

    [Fact]
    public void A_thinner_rendition_of_the_same_avatar_never_replaces_the_sharper()
    {
        TestScope.Fresh();
        string sharp = Avatar(Px300), soft = Avatar(Px64);

        var u = CommitUser(Owner, Authority.Full, UserFields.Identity, name: "Ada", image: sharp);
        CommitUser(Owner, Authority.Full, UserFields.Identity, name: "Ada", image: soft);        // 64 after 300 at Full
        Assert.Equal(sharp, Resolved(u));

        // DIFFERENT art replaces, whatever its size.
        string other = Avatar(Px64, tail: "fedcba9876543210fedcba98");
        CommitUser(Owner, Authority.Full, UserFields.Identity, name: "Ada", image: other);
        Assert.Equal(other, Resolved(u));

        // And the first paint is never a downgrade: a 64 that arrived first is upgraded by the 300.
        var v = CommitUser("spotify:user:soft-first", Authority.Thin, UserFields.Identity, name: "Bea", image: soft);
        Assert.Equal(soft, Resolved(v));
        CommitUser("spotify:user:soft-first", Authority.Full, UserFields.Identity, name: "Bea", image: sharp);
        Assert.Equal(sharp, Resolved(v));
    }

    [Fact]
    public void Profile_shelves_commit_cross_kind_and_drop_what_a_shelf_does_not_admit()
    {
        TestScope.Fresh();
        const string friend = "spotify:user:shelf-friend";
        string artistA = ArtistUri(1), artistB = ArtistUri(2), playlist = PlaylistUri(1), album = AlbumUri(1);

        var s = Staging.Rent();
        // Following is CROSS-KIND (artists then users, wire order); a playlist is not someone you follow.
        StageShelf(s, Relation.ProfileFollowing, Owner,
            (artistA, ProfileCardFlags.ViewerFollows, 25_000),
            (friend, ProfileCardFlags.ViewerFollows, 3),
            (playlist, ProfileCardFlags.None, 9),
            (artistB, ProfileCardFlags.None, 7));
        // Followers admit users only; Artists admit artists only; Playlists admit playlists only.
        StageShelf(s, Relation.ProfileFollowers, Owner, (friend, ProfileCardFlags.None, 3), (artistA, ProfileCardFlags.None, 1));
        StageShelf(s, Relation.ProfileArtists, Owner, (artistA, ProfileCardFlags.OwnerFollows, 10_000), (friend, ProfileCardFlags.None, 1));
        StageShelf(s, Relation.ProfilePlaylists, Owner, (playlist, ProfileCardFlags.None, 5), (album, ProfileCardFlags.None, 1));
        TestScope.CommitAndPublish(s);

        var owner = UserOf(Owner);
        int artistASlot = Entities.Artist(EntityUri.Parse(artistA.AsSpan())).Slot;
        int artistBSlot = Entities.Artist(EntityUri.Parse(artistB.AsSpan())).Slot;
        int friendSlot = UserOf(friend).Slot;

        var following = owner.ProfileCards(ProfileShelf.Following);
        var followingTargets = owner.ProfileTargets(ProfileShelf.Following);
        Assert.Equal(3, following.Length);
        Assert.Equal(3, followingTargets.Length);
        Assert.Equal(new[] { EntityKind.Artist, EntityKind.User, EntityKind.Artist }, following.ToArray().Select(c => c.Kind).ToArray());
        Assert.Equal(new[] { artistASlot, friendSlot, artistBSlot }, followingTargets.ToArray());
        Assert.Equal(new EntityRef(EntityKind.User, friendSlot), following[1].Ref(followingTargets[1]));
        Assert.True(following[0].ViewerFollows);
        Assert.False(following[0].OwnerFollows);
        Assert.False(following[2].ViewerFollows);
        Assert.Equal(new[] { 25_000, 3, 7 }, following.ToArray().Select(c => c.Followers).ToArray());
        Assert.Equal(EdgeState.Complete, owner.ProfileReadiness(ProfileShelf.Following));

        var followers = owner.ProfileCards(ProfileShelf.Followers).ToArray();
        Assert.Equal(EntityKind.User, Assert.Single(followers).Kind);
        Assert.Equal(friendSlot, Assert.Single(owner.ProfileTargets(ProfileShelf.Followers).ToArray()));

        var artists = owner.ProfileCards(ProfileShelf.Artists).ToArray();
        var onlyArtist = Assert.Single(artists);
        Assert.Equal(EntityKind.Artist, onlyArtist.Kind);
        Assert.True(onlyArtist.OwnerFollows);
        Assert.False(onlyArtist.ViewerFollows);
        Assert.Equal(10_000, onlyArtist.Followers);
        Assert.Equal(artistASlot, Assert.Single(owner.ProfileTargets(ProfileShelf.Artists).ToArray()));

        var playlists = owner.ProfileCards(ProfileShelf.Playlists).ToArray();
        var onlyPlaylist = Assert.Single(playlists);
        Assert.Equal(EntityKind.Playlist, onlyPlaylist.Kind);
        Assert.Equal(5, onlyPlaylist.Followers);
        Assert.Equal(Entities.Playlist(EntityUri.Parse(playlist.AsSpan())).Slot, Assert.Single(owner.ProfileTargets(ProfileShelf.Playlists).ToArray()));
    }

    [Fact]
    public void The_shelves_admit_exactly_the_kinds_the_wire_puts_on_them()
    {
        // The decoder's and the commit's one filter (pure).
        Assert.True(ProfileCardEdge.Admits(Relation.ProfilePlaylists, EntityKind.Playlist));
        Assert.False(ProfileCardEdge.Admits(Relation.ProfilePlaylists, EntityKind.Album));
        Assert.True(ProfileCardEdge.Admits(Relation.ProfileArtists, EntityKind.Artist));
        Assert.False(ProfileCardEdge.Admits(Relation.ProfileArtists, EntityKind.User));
        Assert.True(ProfileCardEdge.Admits(Relation.ProfileFollowers, EntityKind.User));
        Assert.False(ProfileCardEdge.Admits(Relation.ProfileFollowers, EntityKind.Artist));
        Assert.True(ProfileCardEdge.Admits(Relation.ProfileFollowing, EntityKind.Artist));
        Assert.True(ProfileCardEdge.Admits(Relation.ProfileFollowing, EntityKind.User));
        Assert.False(ProfileCardEdge.Admits(Relation.ProfileFollowing, EntityKind.Playlist));
        Assert.False(ProfileCardEdge.Admits(Relation.Liked, EntityKind.Track));        // not a profile relation at all
    }

    [Fact]
    public void IsCurrentUser_is_the_account_row_or_the_view_flag()
    {
        Entities.Boot(new CatalogScope("offline", "me-account", "en-US", "US", 0, true));
        Entities.Now = 1_000_000;

        // The account's own row: the current user before any answer says so.
        Assert.True(User.Me.IsCurrentUser);

        // Another row is not, until the profile view flags it (the view knows who is asking).
        var other = CommitUser(Owner, Authority.Full, UserFields.Social, flags: UserFlags.ShowFollows);
        Assert.False(other.IsCurrentUser);
        CommitUser(Owner, Authority.Full, UserFields.Social, flags: UserFlags.ShowFollows | UserFlags.CurrentUser);
        Assert.True(other.IsCurrentUser);

        // The flag is Social's: a Follow-only answer cannot set it.
        var third = CommitUser("spotify:user:third", Authority.Full, UserFields.Follow, flags: UserFlags.CurrentUser);
        Assert.False(third.IsCurrentUser);
    }

    [Fact]
    public void RenditionRank_orders_avatar_and_artist_renditions_by_pixels()
    {
        static int Rank(string prefix16) => Detail.CoverLatch.RenditionRank((prefix16 + Tail).AsSpan());

        // Albums 640/300/64, artist portraits 640/320/160, user avatars 300/64.
        Assert.Equal(640, Rank("ab67616d0000b273"));
        Assert.Equal(300, Rank("ab67616d00001e02"));
        Assert.Equal(64, Rank("ab67616d00004851"));
        Assert.Equal(640, Rank("ab6761610000e5eb"));
        Assert.Equal(320, Rank("ab67616100005174"));
        Assert.Equal(160, Rank("ab6761610000f178"));
        Assert.Equal(300, Rank("ab6775700000ee85"));
        Assert.Equal(64, Rank("ab67757000003b82"));

        Assert.True(Rank("ab6761610000e5eb") > Rank("ab67616100005174"));
        Assert.True(Rank("ab67616100005174") > Rank("ab6761610000f178"));
        Assert.True(Rank("ab6775700000ee85") > Rank("ab67757000003b82"));

        // Unknown prefixes and non-40-char ids rank 0: only the ORDER matters, and an unknown never beats a known one.
        Assert.Equal(0, Rank("ab67706c00006c11"));
        Assert.Equal(0, Detail.CoverLatch.RenditionRank("ab6775700000ee85".AsSpan()));
        Assert.Equal(0, Detail.CoverLatch.RenditionRank(default));
    }

    [Fact]
    public void Invalidate_on_a_user_keeps_it_rendering_stale_and_re_asks()
    {
        Entities.Boot(new CatalogScope("spotify", "christos", "en-US", "US", 0, true));
        Entities.Now = 0;
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);

        var u = CommitUser(Owner, Authority.Full, UserFields.Social | UserFields.Follow,
            flags: UserFlags.ShowFollows | UserFlags.Followed, followers: 118, following: 1063, playlists: 94);
        Assert.True(u.Knows(UserFields.Social | UserFields.Follow));
        Assert.False(u.IsStale(UserFields.Social));

        Entities.Invalidate(u, UserFields.Social | UserFields.Follow);
        Fetch.Drain();

        Assert.True(u.Knows(UserFields.Social | UserFields.Follow));       // still rendering: never a skeleton
        Assert.Equal(118, u.Followers);
        Assert.True(u.IsFollowedByViewer);
        Assert.True(u.IsStale(UserFields.Social));
        Assert.True(u.IsAsked(UserFields.Social));                          // re-asked by the plan that followed
        Assert.Single(provider.Seen);
    }
}
