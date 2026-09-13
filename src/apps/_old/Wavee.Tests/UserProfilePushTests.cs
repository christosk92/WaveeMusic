using System;
using Wavee.Backend.Hydration;
using Xunit;

namespace Wavee.Tests;

/// <summary>The <c>hm://identity/user-profile-changed</c> fold, exercised against the REAL wire payloads captured from
/// a live rename (dealer archive, 2026-09-10). One account changed its display name and its picture; Spotify pushed
/// five messages in 56 seconds, three of them carrying a name with no image because the new picture had not finished
/// processing yet. These are those bytes verbatim, base64 exactly as the dealer frame delivered them — the point is
/// that the rules are pinned to what the service actually sends, not to a hand-written protobuf that agrees with our
/// assumptions.</summary>
public class UserProfilePushTests
{
    // The signed-in account in the capture.
    const string Account = "31unjfmo3oefvlz36ef3eb6kj5tq";

    // The 300x300 image; the payload also carries a 64x64 and the decoder deliberately keeps the largest.
    const string Image300 = "https://i.scdn.co/image/ab6775700000ee857eaebd008310226621360d2e";

    // Name "Christos", NO images (the first push, before the new picture existed server-side).
    const string NameOnly =
        "Ch4KHDMxdW5qZm1vM29lZnZsejM2ZWYzZWI2a2o1dHESCgoIQ2hyaXN0b3MiACoAMgA6AEIASgIIAVIAWgUIyLfSBWIAkgEAmgEAwgEMCgptbHhHdlJGTEFn";

    // Name "Chrr" (mid-keystroke) + both images.
    const string MidRenameWithImages =
        "Ch4KHDMxdW5qZm1vM29lZnZsejM2ZWYzZWI2a2o1dHESBgoEQ2hychpGCEAQQBpAaHR0cHM6Ly9pLnNjZG4uY28vaW1hZ2UvYWI2Nzc1NzAwMDAwM2I4MjdlYWViZDAwODMxMDIyNjYyMTM2MGQyZRpICKwCEKwCGkBodHRwczovL2kuc2Nkbi5jby9pbWFnZS9hYjY3NzU3MDAwMDBlZTg1N2VhZWJkMDA4MzEwMjI2NjIxMzYwZDJlIgAqADIAOgBCAEoCCAFSAggBWgUIyLfSBWIAkgEAmgEAwgEMCgptbHhHdlJGTEFn";

    // Name "Chris" (settled) + both images.
    const string SettledWithImages =
        "Ch4KHDMxdW5qZm1vM29lZnZsejM2ZWYzZWI2a2o1dHESBwoFQ2hyaXMaRghAEEAaQGh0dHBzOi8vaS5zY2RuLmNvL2ltYWdlL2FiNjc3NTcwMDAwMDNiODI3ZWFlYmQwMDgzMTAyMjY2MjEzNjBkMmUaSAisAhCsAhpAaHR0cHM6Ly9pLnNjZG4uY28vaW1hZ2UvYWI2Nzc1NzAwMDAwZWU4NTdlYWViZDAwODMxMDIyNjYyMTM2MGQyZSIAKgAyADoAQgBKAggBUgIIAVoECIzNZ2IAkgEAmgEAwgEMCgptbHhHdlJGTEFn";

    static UserProfilePayload Decode(string base64)
    {
        var parsed = UserProfilePayloadDecoder.Decode(Convert.FromBase64String(base64));
        Assert.NotNull(parsed);
        return parsed!;
    }

    [Fact]
    public void Decodes_the_captured_push_into_name_and_the_largest_image()
    {
        var p = Decode(SettledWithImages);
        Assert.Equal(Account, p.Username);
        Assert.Equal("Chris", p.Name);
        Assert.Equal(Image300, p.ImageUrl);
    }

    [Fact]
    public void A_push_carrying_no_image_still_carries_the_name()
    {
        var p = Decode(NameOnly);
        Assert.Equal("Christos", p.Name);
        Assert.True(string.IsNullOrEmpty(p.ImageUrl));
    }

    [Fact]
    public void Only_this_account_is_applied()
    {
        var p = Decode(SettledWithImages);
        Assert.True(UserProfilePush.IsForAccount(p, Account));
        Assert.True(UserProfilePush.IsForAccount(p, "spotify:user:" + Account));   // uri and bare id agree
        Assert.False(UserProfilePush.IsForAccount(p, "someone-else"));
        Assert.False(UserProfilePush.IsForAccount(null, Account));
        Assert.False(UserProfilePush.IsForAccount(p, null));
    }

    [Fact]
    public void An_image_less_push_updates_the_name_and_keeps_the_current_picture()
    {
        // This is the whole reason Fold exists: three of the five captured pushes look like this, and assigning them
        // wholesale would blank the avatar for the length of the rename.
        var (name, avatar) = UserProfilePush.Fold(Decode(NameOnly), Account, currentAvatarUrl: Image300);
        Assert.Equal("Christos", name);
        Assert.Equal(Image300, avatar);
    }

    [Fact]
    public void A_push_with_an_image_replaces_the_current_picture()
    {
        var (name, avatar) = UserProfilePush.Fold(Decode(MidRenameWithImages), Account, currentAvatarUrl: null);
        Assert.Equal("Chrr", name);
        Assert.Equal(Image300, avatar);
    }

    [Fact]
    public void The_settled_push_wins_when_the_whole_burst_is_folded_in_order()
    {
        // The capture's real order. Whatever coalescing does to the middle, folding every one of them must still land
        // on the last name and the picture — no push may leave the chip worse off than the one before it.
        string? avatar = null;
        string name = Account;
        foreach (var b64 in new[] { NameOnly, NameOnly, MidRenameWithImages, SettledWithImages })
            (name, avatar) = UserProfilePush.Fold(Decode(b64), Account, avatar);

        Assert.Equal("Chris", name);
        Assert.Equal(Image300, avatar);
    }

    [Fact]
    public void An_empty_name_falls_back_to_the_account_id()
    {
        var (name, _) = UserProfilePush.Fold(new UserProfilePayload(Account, "   ", null), Account, null);
        Assert.Equal(Account, name);
    }
}
