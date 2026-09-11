using System;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The recs section's ONE fetch decision. Pinned here because the bug it replaces — a 3-state machine with no exit from
// "loading" — was invisible in every log and permanent for the component's life.
public class RecsRefetchPolicyTests
{
    [Theory]
    [InlineData(RecsState.Idle)] [InlineData(RecsState.Loading)] [InlineData(RecsState.Loaded)] [InlineData(RecsState.Failed)]
    public void NotArmed_NeverFetches_EvenWhenForced(RecsState s)
    {
        Assert.Equal(RecsAction.None, RecsRefetchPolicy.Decide(s, armed: false, fingerprintCurrent: false, force: true));
        Assert.Equal(RecsAction.None, RecsRefetchPolicy.Decide(s, armed: false, fingerprintCurrent: true, force: false));
    }

    [Fact] public void Armed_Idle_Current_Fetches()
        => Assert.Equal(RecsAction.Fetch, RecsRefetchPolicy.Decide(RecsState.Idle, true, fingerprintCurrent: true, force: false));

    [Theory] [InlineData(RecsState.Loaded)] [InlineData(RecsState.Failed)]
    public void Armed_Current_NotForced_LoadedOrFailed_DoesNothing(RecsState s)
        => Assert.Equal(RecsAction.None, RecsRefetchPolicy.Decide(s, true, fingerprintCurrent: true, force: false));

    [Fact] public void Armed_Loading_Current_Waits()
        => Assert.Equal(RecsAction.None, RecsRefetchPolicy.Decide(RecsState.Loading, true, fingerprintCurrent: true, force: false));

    [Theory] [InlineData(RecsState.Idle)] [InlineData(RecsState.Loaded)] [InlineData(RecsState.Failed)]
    public void FingerprintChanged_NotLoading_Fetches(RecsState s)
        => Assert.Equal(RecsAction.Fetch, RecsRefetchPolicy.Decide(s, true, fingerprintCurrent: false, force: false));

    [Fact] public void FingerprintChanged_WhileLoading_Supersedes()
        => Assert.Equal(RecsAction.Supersede, RecsRefetchPolicy.Decide(RecsState.Loading, true, fingerprintCurrent: false, force: false));

    [Fact] public void Force_WhileLoading_Supersedes_TheUsersEscapeHatch()
        => Assert.Equal(RecsAction.Supersede, RecsRefetchPolicy.Decide(RecsState.Loading, true, fingerprintCurrent: true, force: true));

    [Theory] [InlineData(RecsState.Idle)] [InlineData(RecsState.Loaded)] [InlineData(RecsState.Failed)]
    public void Force_NotLoading_Fetches(RecsState s)
        => Assert.Equal(RecsAction.Fetch, RecsRefetchPolicy.Decide(s, true, fingerprintCurrent: true, force: true));

    // Fingerprint: same rows in a different order → same value (a reorder is not a membership change); one row more,
    // fewer, or swapped → different; the context uri participates.
    [Fact]
    public void Fingerprint_IsOrderInsensitive_AndMembershipSensitive()
    {
        var a = T("a"); var b = T("b"); var c = T("c");
        long ab = RecsRefetchPolicy.Fingerprint("spotify:playlist:p", new[] { a, b });
        Assert.Equal(ab, RecsRefetchPolicy.Fingerprint("spotify:playlist:p", new[] { b, a }));
        Assert.NotEqual(ab, RecsRefetchPolicy.Fingerprint("spotify:playlist:p", new[] { a, b, c }));
        Assert.NotEqual(ab, RecsRefetchPolicy.Fingerprint("spotify:playlist:p", new[] { a, c }));
        Assert.NotEqual(ab, RecsRefetchPolicy.Fingerprint("spotify:playlist:q", new[] { a, b }));
    }

    static Track T(string id) => new(id, "spotify:track:" + id, id, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null);
}
