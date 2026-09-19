// ── Wavee.Tests/PlaybackBindBeforeSendTests.cs — B1: the claim must be bound before its response can arrive ────────
//
// `Spotify.Connect.Flush` used to post `PutSent(msgId, isActive)` (Playback.cs's `Ownership.PutSent`) only AFTER a
// 2xx came back — so on a REJECTED put `ClaimMsgId` was still 0 and `Ownership.PutFailed` (the reducer's `PutVerdict`
// input) was a guaranteed no-op: a claim sat `Protected` — and audible — for the full 5 s expiry instead of hearing
// the rejection at once. The fix (B1) moves the post to immediately before `Api.Send`, so the claim is bound before
// the request even leaves.
//
// `Spotify.Connect.Flush` itself cannot be unit-tested (`Spotify.Api.Send` opens a real socket, and the file header
// of `SpotifyConnectTests.cs` says why 0.2.9-style JSON captures are not the way around that). What IS purely
// testable — and is the entire mechanism the fix depends on — is `Playback.Ownership`: a response cluster's verdict
// only applies when `ClaimMsgId` already names the put it answers (`Playback.cs`'s fence, C5). These tests pin that
// mechanism down at the pure fold, the same level `PlaybackOwnershipTests.cs` already tests P1-P4 at.

using Wavee;
using Xunit;

using ClusterOrigin = Wavee.Spotify.Decode.ClusterOrigin;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PlaybackBindBeforeSendTests
{
    static readonly ulong Us = Playback.DeviceHash("wavee-device");
    static readonly ulong Phone = Playback.DeviceHash("phone-device");

    static Playback.ClusterFrame Response(ulong active, long serverTs, uint putMsgId)
        => new(ClusterOrigin.PutResponse, putMsgId, active, serverTs);

    static Playback.OwnerState ClaimedNotYetSent(long nowMs = 0)
    {
        var s = Playback.OwnerState.Initial;
        Playback.Ownership.Claim(ref s, Playback.ClaimCause.UserPlay, 1, nowMs, nowMs, acknowledged: true);
        return s;
    }

    [Fact]
    public void Bound_before_the_response_arrives_the_rejection_lands_at_once_P3()
    {
        // Exactly what `Flush` now guarantees: `PutSent` folds before the request leaves, so it is folded — and the
        // claim bound — long before any response could possibly be decoded.
        var s = ClaimedNotYetSent();
        Playback.Ownership.PutSent(ref s, msgId: 9, isActive: true);
        Assert.Equal(9u, s.ClaimMsgId);

        Playback.OwnerFx fx = Playback.Ownership.Fold(ref s, Response(Phone, 5_000, 9), Us);

        // P3: the verdict applied immediately — the claim is rejected, not left waiting for the 5 s expiry.
        Assert.Equal(Playback.Owner.Foreign, s.Kind);
        Assert.Equal(Playback.OwnerFx.StopHost | Playback.OwnerFx.EmitBecameInactive | Playback.OwnerFx.ClaimRejected, fx);
    }

    [Fact]
    public void Unbound_the_same_response_is_not_read_as_a_verdict_at_all()
    {
        // The bug B1 fixes: `PutSent` never folded (the old code posted it only after a 2xx, so a REJECTED put never
        // posted it at all). `ClaimMsgId` is still 0, so `f.PutMsgId >= s.ClaimMsgId` never gets asked as a verdict —
        // the fold treats the response as an ordinary P2 sighting and waits out the full protection window.
        var s = ClaimedNotYetSent();
        Assert.Equal(0u, s.ClaimMsgId);

        Playback.OwnerFx fx = Playback.Ownership.Fold(ref s, Response(Phone, 5_000, 9), Us);

        Assert.Equal(Playback.Owner.Us, s.Kind);                    // still ours — the rejection never applied
        Assert.Equal(Playback.ClaimPhase.Protected, s.Claim);       // still waiting on the 5 s expiry (P2), not P3
        Assert.Equal(Playback.OwnerFx.None, fx);

        // …and it only settles once the window actually expires — seconds later than a bound claim would have.
        Playback.OwnerFx expiry = Playback.Ownership.Tick(ref s, Playback.Ownership.ClaimProtectMs);
        Assert.True(expiry.HasFlag(Playback.OwnerFx.ClaimRejected));
    }

    [Fact]
    public void A_failed_put_is_also_only_actionable_once_bound()
    {
        // The other half of the same bug (Connect.cs's own `PutVerdict(false)` on a non-2xx): `Ownership.PutFailed`
        // reads `ClaimMsgId` too, so it is a no-op unless `PutSent` already bound it — bind-before-send is what makes
        // BOTH the accepted and the rejected round trip observable at once instead of only on a 2xx.
        var boundLate = ClaimedNotYetSent();
        Playback.OwnerFx unbound = Playback.Ownership.PutFailed(ref boundLate, msgId: 9);
        Assert.Equal(Playback.OwnerFx.None, unbound);
        Assert.Equal(Playback.ClaimPhase.Protected, boundLate.Claim);   // PutFailed before PutSent: nothing to fail

        var bound = ClaimedNotYetSent();
        Playback.Ownership.PutSent(ref bound, msgId: 9, isActive: true);
        Playback.Ownership.PutFailed(ref bound, msgId: 9);
        Assert.Equal(Playback.ClaimPhase.Unadopted, bound.Claim);       // PutSent first: the failure is heard at once
    }
}
