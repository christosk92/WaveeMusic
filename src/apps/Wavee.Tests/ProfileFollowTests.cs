// ── Wavee.Tests/ProfileFollowTests.cs — following a user profile (profile pages plan, Appendix D §3) ──────────────────
//
//   · the bodies `isFollowingUsers` / `followUsers` / `unfollowUsers` send (the captured variables);
//   · the two answer readers (`ProfileFollowAnswer.IsFollowing`, the lenient `WriteSucceeded`) — pure over bytes;
//   · the optimistic half (`UserFollowWrite`) over a real user table, and the commit that must not undo it;
//   · the library seam's user arms (`User.LibrarySeam`), and the own-profile no-op.
// `Spotify.Library.FollowUser`'s network half is SHELL and is not driven here (it toasts through the engine).

using System.Text;
using System.Text.Json;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class ProfileFollowTests
{
    const string Bob = "spotify:user:bob";
    const string Hash = "c00e0cb6c7766e7230fc256cf4fe07aec63b53d1160a323940fce7b664e95596";

    static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    // ── the answer readers ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{\"data\":{\"users\":[{\"__typename\":\"User\",\"uri\":\"spotify:user:bob\",\"following\":true}]}}", FollowAnswer.Following)]
    [InlineData("{\"data\":{\"users\":[{\"__typename\":\"User\",\"uri\":\"spotify:user:bob\",\"following\":false}]}}", FollowAnswer.NotFollowing)]
    [InlineData("{\"data\":{\"users\":[{\"__typename\":\"NotFound\",\"uri\":\"spotify:user:bob\"}]}}", FollowAnswer.NotFound)]
    public void IsFollowing_reads_following_not_following_and_not_found(string json, FollowAnswer expected)
        => Assert.Equal(expected, Spotify.Api.ProfileFollowAnswer.IsFollowing(Bytes(json), Bob));

    [Fact]
    public void IsFollowing_picks_the_entry_for_the_uri_it_asked_about()
    {
        string json = "{\"data\":{\"users\":["
                    + "{\"__typename\":\"User\",\"uri\":\"spotify:user:alice\",\"following\":true},"
                    + "{\"__typename\":\"User\",\"uri\":\"spotify:user:bob\",\"following\":false}]}}";
        Assert.Equal(FollowAnswer.NotFollowing, Spotify.Api.ProfileFollowAnswer.IsFollowing(Bytes(json), Bob));
        Assert.Equal(FollowAnswer.Following, Spotify.Api.ProfileFollowAnswer.IsFollowing(Bytes(json), "spotify:user:alice"));
    }

    [Fact]
    public void A_single_unmatched_entry_is_still_the_answer()
    {
        // The uri's spelling can differ from ours (percent-escaping): one entry for one ask is unambiguous.
        string one = "{\"data\":{\"users\":[{\"__typename\":\"User\",\"uri\":\"spotify:user:a%40b\",\"following\":true}]}}";
        Assert.Equal(FollowAnswer.Following, Spotify.Api.ProfileFollowAnswer.IsFollowing(Bytes(one), "spotify:user:a@b"));

        // Two unmatched entries are not.
        string two = "{\"data\":{\"users\":["
                   + "{\"uri\":\"spotify:user:x\",\"following\":true},{\"uri\":\"spotify:user:y\",\"following\":true}]}}";
        Assert.Equal(FollowAnswer.Unknown, Spotify.Api.ProfileFollowAnswer.IsFollowing(Bytes(two), Bob));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":{\"users\":[]}}")]
    [InlineData("{\"data\":{\"users\":{}}}")]
    [InlineData("{\"data\":{\"users\":[{\"uri\":\"spotify:user:bob\"}]}}")]                       // no `following`
    [InlineData("{\"data\":{\"users\":[{\"uri\":\"spotify:user:bob\",\"following\":\"yes\"}]}}")]  // not a boolean
    public void Garbage_is_unknown(string json)
        => Assert.Equal(FollowAnswer.Unknown, Spotify.Api.ProfileFollowAnswer.IsFollowing(Bytes(json), Bob));

    [Theory]
    // the captured shape (research §3): data.followUsers.responses[]{__typename, result, username}
    [InlineData("{\"data\":{\"followUsers\":{\"responses\":[{\"__typename\":\"FollowUserResult\",\"result\":\"SUCCESS\",\"username\":\"bob\"}]}}}", true, true)]
    [InlineData("{\"data\":{\"unfollowUsers\":{\"responses\":[{\"__typename\":\"UnfollowUserResult\",\"result\":\"SUCCESS\",\"username\":\"bob\"}]}}}", false, true)]
    [InlineData("{\"data\":{\"followUsers\":{}}}", true, true)]                                     // an op with no responses took
    [InlineData("{\"data\":{\"followUsers\":{\"responses\":[{\"result\":true}]}}}", true, true)]
    // explicit refusals
    [InlineData("{\"errors\":[{\"message\":\"nope\"}]}", true, false)]
    [InlineData("{\"data\":{\"unfollowUsers\":{\"responses\":[]}}}", true, false)]                  // the op we asked for is missing
    [InlineData("{\"data\":{\"followUsers\":{\"responses\":[{\"__typename\":\"FollowUserError\"}]}}}", true, false)]
    [InlineData("{\"data\":{\"followUsers\":{\"responses\":[{\"result\":false}]}}}", true, false)]
    [InlineData("{\"data\":{\"followUsers\":{\"responses\":[{\"result\":\"FAILED\"}]}}}", true, false)]
    [InlineData("{\"data\":{\"followUsers\":{\"responses\":[{\"result\":\"not_allowed\"}]}}}", true, false)]
    [InlineData("{\"data\":{\"followUsers\":{\"responses\":[{\"result\":\"DENIED\"}]}}}", true, false)]
    [InlineData("[]", true, false)]
    [InlineData("not json", true, false)]
    public void WriteSucceeded_accepts_the_captured_shape_and_refuses_errors_false_and_failure_strings(string json, bool follow, bool expected)
        => Assert.Equal(expected, Spotify.Api.ProfileFollowAnswer.WriteSucceeded(Bytes(json), follow));

    [Fact]
    public void An_empty_write_body_is_a_yes()
        => Assert.True(Spotify.Api.ProfileFollowAnswer.WriteSucceeded([], follow: true));

    // ── the bodies ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Follow_bodies_are_the_captured_variables()
    {
        // followUsers / unfollowUsers: bare ids under `usernames` — the same persisted hash, the operation name selects.
        using (var follow = JsonDocument.Parse(Spotify.Api.ProfileQueries.FollowBody("a@b", follow: true)))
        {
            var root = follow.RootElement;
            Assert.Equal("followUsers", root.GetProperty("operationName").GetString());
            Assert.Equal("a@b", root.GetProperty("variables").GetProperty("usernames")[0].GetString());
            Assert.Equal(1, root.GetProperty("variables").GetProperty("usernames").GetArrayLength());
            Assert.Equal(Hash, root.GetProperty("extensions").GetProperty("persistedQuery").GetProperty("sha256Hash").GetString());
            Assert.Equal(1, root.GetProperty("extensions").GetProperty("persistedQuery").GetProperty("version").GetInt32());
        }
        using (var unfollow = JsonDocument.Parse(Spotify.Api.ProfileQueries.FollowBody("a@b", follow: false)))
        {
            Assert.Equal("unfollowUsers", unfollow.RootElement.GetProperty("operationName").GetString());
            Assert.Equal(Hash, unfollow.RootElement.GetProperty("extensions").GetProperty("persistedQuery").GetProperty("sha256Hash").GetString());
        }

        // isFollowingUsers: URIs under `uris` (not ids).
        using var ask = JsonDocument.Parse(Spotify.Api.ProfileQueries.IsFollowingBody(["spotify:user:a%40b", Bob]));
        Assert.Equal("isFollowingUsers", ask.RootElement.GetProperty("operationName").GetString());
        var uris = ask.RootElement.GetProperty("variables").GetProperty("uris");
        Assert.Equal(2, uris.GetArrayLength());
        Assert.Equal("spotify:user:a%40b", uris[0].GetString());
        Assert.Equal(Bob, uris[1].GetString());
        Assert.Equal(Hash, ask.RootElement.GetProperty("extensions").GetProperty("persistedQuery").GetProperty("sha256Hash").GetString());
    }

    [Fact]
    public void The_three_operations_share_one_hash_and_the_desktop_identity()
    {
        var q = Spotify.Api.ProfileQueries.IsFollowingUsers;
        Assert.Equal(Hash, q.Hash);
        Assert.Equal(Hash, Spotify.Api.ProfileQueries.FollowUsers.Hash);
        Assert.Equal(Hash, Spotify.Api.ProfileQueries.UnfollowUsers.Hash);
        Assert.False(q.Web);
        Assert.False(Spotify.Api.ProfileQueries.FollowUsers.Web);
        Assert.False(Spotify.Api.ProfileQueries.UnfollowUsers.Web);
    }

    // ── the optimistic half, over a real user table ─────────────────────────────────────────────────────────────────

    const uint SocialAndFollow = (uint)(UserFields.Social | UserFields.Follow);

    /// <summary>A user row that a profile view answered at Full (Identity + Social + Follow unless <paramref name="knowFollow"/> is off).</summary>
    static (UserTable Table, User User) ProfileRow(string uri, int followers, bool followed, bool knowFollow = true)
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        uint known = (uint)(UserFields.Identity | UserFields.Social) | (knowFollow ? (uint)UserFields.Follow : 0u);
        ref var row = ref s.Users.RowFor(new StagedId(s.Text(uri)), Authority.Full, known);
        row.Name = s.Text("Bob");
        row.Followers = followers;
        row.Flags = followed ? (uint)UserFlags.Followed : 0u;
        s.Users.Settle();
        TestScope.CommitAndPublish(s);
        return (Entities.Current.Users, Entities.User(EntityUri.Parse(uri.AsSpan())));
    }

    /// <summary>A Full profile answer landing now — the in-flight GET the mount's Invalidate sent before the click.</summary>
    static void LandFullAnswer(string uri, int followers, bool followed)
    {
        var s = Staging.Rent();
        ref var row = ref s.Users.RowFor(new StagedId(s.Text(uri)), Authority.Full, SocialAndFollow);
        row.Followers = followers;
        row.Flags = followed ? (uint)UserFlags.Followed : 0u;
        s.Users.Settle();
        TestScope.CommitAndPublish(s);
    }

    [Fact]
    public void Apply_flips_and_counts_and_holds_a_stale_full_answer_off_until_confirm()
    {
        var (t, user) = ProfileRow(Bob, followers: 10, followed: false);
        int slot = user.Slot;
        Assert.False(user.IsFollowedByViewer);
        t.Stale[slot] |= SocialAndFollow;                         // the mount's Invalidate left both groups stale

        var snap = UserFollowWrite.Apply(t, slot, follow: true);

        Assert.True(user.IsFollowedByViewer);
        Assert.Equal(11, user.Followers);
        Assert.Equal((byte)Authority.Local, t.FollowAuthority[slot]);
        Assert.Equal((byte)Authority.Local, t.ExtrasAuthority[slot]);
        Assert.Equal(0u, t.Stale[slot] & SocialAndFollow);        // a stale group is a hole to Accepts: the hold clears it

        // The GET sent BEFORE the click comes back with the pre-click world: the hold refuses it.
        LandFullAnswer(Bob, followers: 10, followed: false);
        Assert.True(user.IsFollowedByViewer);
        Assert.Equal(11, user.Followers);

        // The server agreed: the authorities go back, so the NEXT answer is the truth again.
        UserFollowWrite.Confirm(t, slot, in snap);
        Assert.Equal((byte)Authority.Full, t.FollowAuthority[slot]);
        Assert.Equal((byte)Authority.Full, t.ExtrasAuthority[slot]);
        Assert.True(user.IsFollowedByViewer);
        LandFullAnswer(Bob, followers: 12, followed: false);
        Assert.False(user.IsFollowedByViewer);
        Assert.Equal(12, user.Followers);
    }

    [Fact]
    public void Apply_to_the_state_the_row_already_has_moves_no_count()
    {
        var (t, user) = ProfileRow(Bob, followers: 10, followed: true);
        UserFollowWrite.Apply(t, user.Slot, follow: true);
        Assert.True(user.IsFollowedByViewer);
        Assert.Equal(10, user.Followers);

        var (t2, other) = ProfileRow("spotify:user:carol", followers: 0, followed: false);
        UserFollowWrite.Apply(t2, other.Slot, follow: false);
        Assert.False(other.IsFollowedByViewer);
        Assert.Equal(0, other.Followers);                          // never below zero, never moved for a no-change
    }

    [Fact]
    public void Unfollowing_takes_one_off_the_count_and_never_goes_below_zero()
    {
        var (t, user) = ProfileRow(Bob, followers: 1, followed: true);
        UserFollowWrite.Apply(t, user.Slot, follow: false);
        Assert.False(user.IsFollowedByViewer);
        Assert.Equal(0, user.Followers);

        // A count the server had not caught up on (0 with the viewer already following) clamps instead of going negative.
        var (t2, behind) = ProfileRow("spotify:user:erin", followers: 0, followed: true);
        UserFollowWrite.Apply(t2, behind.Slot, follow: false);
        Assert.Equal(0, behind.Followers);
    }

    [Fact]
    public void Revert_restores_flag_count_authorities_and_the_known_bit()
    {
        var (t, user) = ProfileRow(Bob, followers: 10, followed: false);
        int slot = user.Slot;
        var snap = UserFollowWrite.Apply(t, slot, follow: true);
        UserFollowWrite.Revert(t, slot, in snap);

        Assert.False(user.IsFollowedByViewer);
        Assert.Equal(10, user.Followers);
        Assert.Equal((byte)Authority.Full, t.FollowAuthority[slot]);
        Assert.Equal((byte)Authority.Full, t.ExtrasAuthority[slot]);
        Assert.True(user.Knows(UserFields.Follow));               // it was known before the click, so it still is

        // A follow state nobody had answered: the click made it "known", the refusal puts the hole back.
        var (t2, unknown) = ProfileRow("spotify:user:dave", followers: 5, followed: false, knowFollow: false);
        Assert.False(unknown.Knows(UserFields.Follow));
        var snap2 = UserFollowWrite.Apply(t2, unknown.Slot, follow: true);
        Assert.True(unknown.Knows(UserFields.Follow));
        UserFollowWrite.Revert(t2, unknown.Slot, in snap2);
        Assert.False(unknown.Knows(UserFields.Follow));
        Assert.False(unknown.IsFollowedByViewer);
        Assert.Equal(5, unknown.Followers);
    }

    // ── the library seam ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_library_seam_reads_a_user_follow()
    {
        var (t, user) = ProfileRow(Bob, followers: 3, followed: false);
        Entities.Current.MeSlot = Entities.User(EntityUri.Parse("spotify:user:seam-me".AsSpan())).Slot;

        Assert.False(User.LibrarySeam.IsSaved(Bob));
        UserFollowWrite.Apply(t, user.Slot, follow: true);
        Assert.True(User.LibrarySeam.IsSaved(Bob));
        Assert.False(User.LibrarySeam.IsSaved("spotify:user:never-seen"));    // no row: reads unsaved
    }

    [Fact]
    public void Toggling_your_own_profile_is_a_no_op()
    {
        var (t, me) = ProfileRow("spotify:user:me-tester", followers: 7, followed: false);
        Entities.Current.MeSlot = me.Slot;
        uint version = t.Version[me.Slot];
        uint flags = t.Flags[me.Slot];

        User.LibrarySeam.ToggleSaved("spotify:user:me-tester", null);
        Spotify.Library.FollowUser("spotify:user:me-tester", follow: true);

        Assert.Equal(version, t.Version[me.Slot]);
        Assert.Equal(flags, t.Flags[me.Slot]);
        Assert.Equal(7, me.Followers);
        Assert.False(me.IsFollowedByViewer);
    }
}
