// ── Entities/User.Follow.cs — the optimistic half of a user follow ─────────────────────────────────────────────────────
//
// Role: CORE (pure over a UserTable; Spotify.Library.FollowUser feeds it and settles it)
// Owner: D3 (profile pages)
// Spec: docs/plans/wavee/profile-pages-implementation.md Appendix D §3.2, finding 4

namespace Wavee;

/// <summary>What a follow write changed, so its refusal can put it back exactly.</summary>
public readonly record struct FollowSnapshot(uint Flags, int Followers, byte FollowAuthority, byte ExtrasAuthority, uint Known);

/// <summary>THE OPTIMISTIC HALF of a user follow (Spotify.Library.FollowUser). While the write is out, the Follow and Social
/// group authorities are held at <see cref="Authority.Local"/>: a profile GET issued BEFORE the click (the mount's
/// Invalidate) can then not land the pre-click state over the flip (<c>Table.Accepts</c>: Full &lt; Local). UI thread.
/// <para>A group the mount's <c>Invalidate</c> left STALE reads as a hole to <c>Accepts</c> (it gates on
/// <c>Settled</c>, not <c>Known</c>), and a hole takes any authority — so the hold also clears the stale bit of the two
/// groups it protects. The in-flight answer is then refused for those groups (Identity and the riding shelves still
/// land), and <see cref="Confirm"/> leaves them settled: the optimistic value IS the answer until the next mount.</para></summary>
public static class UserFollowWrite
{
    const uint HeldGroups = (uint)UserFields.Follow | (uint)UserFields.Social;

    public static FollowSnapshot Apply(UserTable t, int slot, bool follow)
    {
        var snap = new FollowSnapshot(t.Flags[slot], t.Followers[slot], t.FollowAuthority[slot], t.ExtrasAuthority[slot], t.Known[slot]);
        uint followed = (uint)UserFlags.Followed;
        bool was = (t.Known[slot] & (uint)UserFields.Follow) != 0 && (t.Flags[slot] & followed) != 0;
        t.Flags[slot] = follow ? t.Flags[slot] | followed : t.Flags[slot] & ~followed;
        if (was != follow && (t.Known[slot] & (uint)UserFields.Social) != 0)
            t.Followers[slot] = Math.Max(0, t.Followers[slot] + (follow ? 1 : -1));
        t.FollowAuthority[slot] = (byte)Authority.Local;
        t.ExtrasAuthority[slot] = (byte)Authority.Local;
        t.Stale[slot] &= ~HeldGroups;                  // a stale group is a hole to Accepts: the hold needs it settled
        t.Bump(slot, (uint)UserFields.Follow);        // the click IS the answer until the server says otherwise
        return snap;
    }

    /// <summary>The server agreed: the held authorities go back, so the next real answer may move the row again.</summary>
    public static void Confirm(UserTable t, int slot, in FollowSnapshot snap)
    {
        t.FollowAuthority[slot] = snap.FollowAuthority;
        t.ExtrasAuthority[slot] = snap.ExtrasAuthority;
        t.Bump(slot);
    }

    /// <summary>The server refused: the bit, the count, the authorities and the Follow known-bit as they were.</summary>
    public static void Revert(UserTable t, int slot, in FollowSnapshot snap)
    {
        t.Flags[slot] = (t.Flags[slot] & ~(uint)UserFlags.FollowMask) | (snap.Flags & (uint)UserFlags.FollowMask);
        t.Followers[slot] = snap.Followers;
        t.FollowAuthority[slot] = snap.FollowAuthority;
        t.ExtrasAuthority[slot] = snap.ExtrasAuthority;
        if ((snap.Known & (uint)UserFields.Follow) == 0) t.Known[slot] &= ~(uint)UserFields.Follow;
        t.Bump(slot);
    }
}
