using System;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

/// <summary>The pure decision behind the <c>hm://identity/user-profile-changed</c> dealer route: whether a push is
/// ours, and what the profile chip should read once it is folded in. Kept engine-free and side-effect-free so the
/// rules below can be unit-tested against the REAL captured wire payloads rather than reasoned about.
///
/// <para><b>Why a fold rather than a straight assignment.</b> Spotify does not push one settled profile — a capture of a
/// single rename produced five messages in 56 seconds: <c>"Christos"</c>, <c>"Christos"</c>, <c>"Chrr"</c>,
/// <c>"Chrr"</c> + images, <c>"Chris"</c> + images. The first three carry a name and NO image at all, because the new
/// picture had not finished processing server-side yet. Assigning each push wholesale would therefore blank the avatar
/// for the length of the rename and put it back afterwards — a visible flicker caused entirely by us. So an image-less
/// push updates the NAME and leaves the picture standing.</para>
///
/// <para><b>The cost of that rule</b> is that a profile picture REMOVAL cannot be told apart from "this push simply has
/// no image in it", so a removal is not reflected until the next login re-fetches. The wire does appear to carry a
/// has-image flag (field 10 shows up only alongside images), but <see cref="UserProfilePayloadDecoder"/> does not
/// surface it and inventing a meaning for an unnamed field is a worse trade than a stale picture after a removal.</para></summary>
public static class UserProfilePush
{
    /// <summary>The dealer topic. A MESSAGE push, not a request — nothing is acked.</summary>
    public const string Topic = "hm://identity/user-profile-changed";

    /// <summary>How long a burst is collapsed for. Sized from the capture: the three rename pushes landed 62 ms,
    /// 9.7 s and 45.6 s apart, so this catches the keystroke pair without delaying a settled change (the coalescer
    /// runs the FIRST push immediately and only defers what follows inside the window).</summary>
    public const int CoalesceWindowMs = 750;

    /// <summary>Is this push about <paramref name="account"/>? The dealer connection is per-session, but the topic is
    /// not scoped to the signed-in user, so the id in the payload is what authorises applying it — never the fact that
    /// it arrived. Both sides go through <see cref="UserProfileIds.Normalize"/> so a bare id and a
    /// <c>spotify:user:</c> uri compare equal and case never decides.</summary>
    public static bool IsForAccount(UserProfilePayload? push, string? account)
    {
        if (push is null) return false;
        if (UserProfileIds.Normalize(account) is not { } mine) return false;
        if (UserProfileIds.Normalize(push.Username) is not { } theirs) return false;
        return string.Equals(mine, theirs, StringComparison.Ordinal);
    }

    /// <summary>Fold a push onto what the chip currently shows. <paramref name="account"/> is the bare account id, used
    /// as the name only when the push carries none (the same fallback the login-time fetch uses, so the two paths
    /// cannot disagree); <paramref name="currentAvatarUrl"/> is what survives an image-less push.</summary>
    public static (string Name, string? AvatarUrl) Fold(UserProfilePayload push, string account, string? currentAvatarUrl)
    {
        ArgumentNullException.ThrowIfNull(push);
        string name = string.IsNullOrWhiteSpace(push.Name) ? account : push.Name!.Trim();
        string? avatar = string.IsNullOrWhiteSpace(push.ImageUrl) ? currentAvatarUrl : push.ImageUrl;
        return (name, avatar);
    }
}
