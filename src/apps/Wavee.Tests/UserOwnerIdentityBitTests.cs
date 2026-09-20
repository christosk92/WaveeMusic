// ── Wavee.Tests/UserOwnerIdentityBitTests.cs — the playlist owner whose name never landed (2026-09-20) ─────────────
//
// `Spotify.Decode.Pathfinder.cs`'s `Stage()` sealed `UserFields.Identity` UNCONDITIONALLY for every User node,
// including a nameless `ownerV2`/`addedBy` mention that carries only a uri and an avatar. Once sealed,
// `Entities.Ensure`'s `wanted & ~known` treats the profile as already answered and never asks for it again — a
// playlist header's owner segment ("<owner> · N songs · 85.1K saves · 4 hr 44 min") rendered with the owner
// silently missing forever, the avatar (when the row happened to carry one) the only trace the mention ever
// existed. The fix mirrors the Track arm's identical guard (`TrackImageBitTests`, `Track.cs`'s `CommitTracks`):
// `known` is gated on `n.Name.IsEmpty`, so a nameless mention leaves Identity a HOLE the ordinary fetch machinery
// re-asks, while `row.Image` is still written to the STAGED row unconditionally (the Show arm's shape,
// `ShowFields.Title`/`ShowFields.Image`) so a fuller answer arriving right behind it is never starved of the
// avatar it already read.
//
// `UserFields` has no separate Image bit the way `ShowFields` does: `CommitUsers` (`Entities/User.cs`) applies
// Name and Image TOGETHER, gated on the ONE Identity bit. So withholding Identity on a nameless mention also
// withholds the COMMITTED Image for that one mention — the Track arm's own trade (a row with a withheld bit does
// not get to keep a partial win either): the avatar reappears the moment the real profile answers, same as the
// name does, rather than the row reading a permanently blank name forever. The tests below pin both halves: the
// staged row (the image survives `Stage()` even though Identity does not) and the committed table (nothing lands
// until a real name arrives, and then everything does, together).
//
// `Playlist.NameOf`'s uri fallback (0.2.9's "show the raw id while the profile has not landed") was DELETED rather
// than repaired: a Spotify user's identity is always the TEXT form, but nothing on the path from a bare mention to
// this row guarantees `u.Uri.Text` resolves to anything a caller could carve an id out of — the fallback silently
// answered "" anyway, and a fallback that LOOKS like it does something is worse than an honest blank. Its
// replacement (`Entities.Strings.Resolve(u.NameId)`, "" until a real answer lands) is pinned last.

using System.Text;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class UserOwnerIdentityBitTests
{
    static Spotify.Decode.Node OwnerNode(Staging s, string uri, string name = "", string image = "") => new()
    {
        Uri = new StagedId(s.Text(uri)),
        Name = s.Text(name),
        Image = s.Text(image),
    };

    static User UserOf(string uri) => Entities.User(EntityUri.Parse(uri.AsSpan()));
    static string Resolved(StringId id) => Entities.Strings.Resolve(id);
    static string Utf8Of(Staging s, TextRef t) => Encoding.UTF8.GetString(s.Utf8(t));

    // ── Stage() itself: the staged row, before any commit ───────────────────────────────────────────────────────────

    [Fact]
    public void A_nameless_mention_with_an_avatar_leaves_Identity_unsealed_but_stages_the_image()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var node = OwnerNode(s, "spotify:user:nameless1", image: "https://i.scdn.co/image/avatar1");

        var id = Spotify.Decode.Stage(s, in node, Authority.Thin);

        Assert.False(id.IsEmpty);                                     // the row still lands — a real edge target
        var row = s.Users[s.Users.Count - 1];
        Assert.Equal(0u, row.Known & (uint)UserFields.Identity);       // the bug: this used to read Identity, sealed
        Assert.False(row.Image.IsEmpty);                               // the avatar is still written to the STAGED row
        Assert.Equal("https://i.scdn.co/image/avatar1", Utf8Of(s, row.Image));
        Staging.Return(s);
    }

    [Fact]
    public void A_mention_with_a_name_seals_Identity_as_before()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var node = OwnerNode(s, "spotify:user:named1", name: "Christos", image: "https://i.scdn.co/image/avatar2");

        var id = Spotify.Decode.Stage(s, in node, Authority.Thin);

        Assert.False(id.IsEmpty);
        var row = s.Users[s.Users.Count - 1];
        Assert.Equal((uint)UserFields.Identity, row.Known & (uint)UserFields.Identity);
        Assert.Equal("Christos", Utf8Of(s, row.Name));
        Assert.Equal("https://i.scdn.co/image/avatar2", Utf8Of(s, row.Image));
        Staging.Return(s);
    }

    // ── the end-to-end shape: the commit gate, and the re-ask it now allows ─────────────────────────────────────────

    [Fact]
    public void A_nameless_mention_commits_no_Identity_and_stays_askable()
    {
        // CommitUsers applies Name and Image TOGETHER — the one Identity bit `UserFields` has, no Show-style split —
        // so withholding the bit here also withholds the avatar at the TABLE for this one mention. The row is not
        // lost: it resolved a real slot, and `Entities.Ensure(user, UserFields.Identity)` now sees a genuine hole
        // instead of a permanently "known" blank.
        TestScope.Fresh();
        var s = Staging.Rent();
        var node = OwnerNode(s, "spotify:user:nameless2", image: "https://i.scdn.co/image/avatar3");
        Spotify.Decode.Stage(s, in node, Authority.Thin);
        TestScope.CommitAndPublish(s);

        var user = UserOf("spotify:user:nameless2");
        Assert.True(user.IsValid);                       // the identity itself still resolved a row
        Assert.False(user.Knows(UserFields.Identity));    // … but nothing about it is "known" yet — re-askable
        Assert.True(user.ImageId.IsEmpty);
        Assert.Equal("", Playlist.NameOf(user));
    }

    [Fact]
    public void A_later_full_answer_fills_the_name_and_the_image_together()
    {
        // The shape that matters end to end: a playlist page mentions the owner thinly (avatar, no name) before the
        // profile fetch lands; the fetch then answers with both, and — because Identity was never falsely sealed —
        // `CommitUsers` is free to apply them together instead of finding the group already "known".
        TestScope.Fresh();
        var mention = Staging.Rent();
        var thin = OwnerNode(mention, "spotify:user:nameless3", image: "https://i.scdn.co/image/avatar4");
        Spotify.Decode.Stage(mention, in thin, Authority.Thin);
        TestScope.CommitAndPublish(mention);
        Assert.False(UserOf("spotify:user:nameless3").Knows(UserFields.Identity));

        var answer = Staging.Rent();
        var full = OwnerNode(answer, "spotify:user:nameless3", name: "Christos", image: "https://i.scdn.co/image/avatar4");
        Spotify.Decode.Stage(answer, in full, Authority.Full);
        TestScope.CommitAndPublish(answer);

        var user = UserOf("spotify:user:nameless3");
        Assert.True(user.Knows(UserFields.Identity));
        Assert.Equal("Christos", Resolved(user.NameId));
        Assert.Equal("https://i.scdn.co/image/avatar4", Resolved(user.ImageId));
        Assert.Equal("Christos", Playlist.NameOf(user));
    }

    // ── `Playlist.NameOf`'s fallback decision (deleted, not repaired) ───────────────────────────────────────────────

    [Fact]
    public void NameOf_answers_the_resolved_name_when_one_is_known()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Users.RowFor(new StagedId(s.Text("spotify:user:named2")), Authority.Full, (uint)UserFields.Identity);
        row.Name = s.Text("Ada");
        TestScope.CommitAndPublish(s);

        Assert.Equal("Ada", Playlist.NameOf(UserOf("spotify:user:named2")));
    }

    [Fact]
    public void NameOf_answers_blank_rather_than_a_uri_derived_guess_while_unresolved()
    {
        TestScope.Fresh();
        // A row that exists (something else made it an edge target) but has never had its Identity answered — the
        // exact shape the old `EntityUri.IdOf(u.Uri.Text.AsSpan())` fallback tried, and failed, to paper over.
        int slot = Entities.Current.Users.Slot(EntityId.Parse("spotify:user:unresolved1".AsSpan()));
        Assert.Equal("", Playlist.NameOf(new User(slot)));
    }

    [Fact]
    public void NameOf_answers_blank_for_an_invalid_user()
    {
        TestScope.Fresh();
        Assert.Equal("", Playlist.NameOf(default(User)));
    }
}
