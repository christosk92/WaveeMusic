using Wavee.Backend;
using Xunit;

namespace Wavee.Tests;

// The Connect ownership fold (Backend/PlaybackOwnership.cs) — one test per row of the design table (F0–F6 not ours,
// P1–P4 claim pending, A1–A3 claim settled) plus claims, put binding, expiry and release. The incidents it replaces the
// old predicates for are replayed end-to-end in ConnectIncident20260911Tests; this pins the rule itself.
public class PlaybackOwnershipTests
{
    const string Us = "us";
    const string Phone = "phone";
    const string Speaker = "speaker";

    static ClusterFrame Push(string active, long ts) => new(ClusterOrigin.Push, 0, active, ts);
    static ClusterFrame Resp(uint msgId, string active, long ts) => new(ClusterOrigin.PutResponse, msgId, active, ts);

    static (OwnerState, OwnerFx) Fold(OwnerState s, ClusterFrame f) => PlaybackOwnership.OnCluster(s, f, Us);

    static OwnerState Foreign(string id, long lastTs = 100)
        => Fold(OwnerState.Initial with { LastServerTs = 0 }, Push(id, lastTs)).Item1;

    // A protected claim whose carrier PUT (msgId 7) is on the wire.
    static OwnerState ProtectedClaim(OwnerState from, long nowMono = 1000)
    {
        var (s, _) = PlaybackOwnership.OnClaim(from, ClaimCause.UserPlay, id: 1, startedAtMs: 5_000, nowMono, acknowledged: true);
        return PlaybackOwnership.OnPutSent(s, msgId: 7, isActive: true);
    }

    static OwnerState Adopted(long fenceTs = 200)
        => Fold(ProtectedClaim(OwnerState.Initial), Resp(7, Us, fenceTs)).Item1;

    // ── F: not ours ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void F0_OlderFrame_IsDropped_AndChangesNothing()
    {
        var s = Foreign(Phone, lastTs: 500);
        var (t, fx) = Fold(s, Push(Speaker, 400));
        Assert.Equal(OwnerFx.DropFrame, fx);
        Assert.Equal(s, t);
    }

    [Fact]
    public void F0_FrameWithoutServerTime_IsNeverDropped()
    {
        var s = Foreign(Phone, lastTs: 500);
        var (t, fx) = Fold(s, Push(Speaker, 0));
        Assert.Equal(OwnerKind.Foreign, t.Kind);
        Assert.Equal(Speaker, t.DeviceId);
        Assert.Equal(OwnerFx.StopHost, fx);
    }

    [Fact]
    public void F1_Nobody_EmptyActive_StaysNobody()
    {
        var (t, fx) = Fold(OwnerState.Initial, Push("", 100));
        Assert.Equal(OwnerKind.Nobody, t.Kind);
        Assert.Equal(NobodyCause.Launch, t.Cause);
        Assert.Equal(OwnerFx.None, fx);
    }

    [Fact]
    public void F2_Nobody_ClusterNamesUsWithoutAClaim_IsStaleSelf_NotOwnership()
    {
        var (t, fx) = Fold(OwnerState.Initial, Push(Us, 100));
        Assert.Equal(OwnerKind.Nobody, t.Kind);
        Assert.Equal(NobodyCause.StaleSelf, t.Cause);
        Assert.Equal(OwnerFx.None, fx);
        Assert.False(PlaybackOwnership.IsActiveOnWire(t));
    }

    [Fact]
    public void F3_Nobody_ForeignActive_BecomesForeign_AndStopsTheHost()
    {
        var (t, fx) = Fold(OwnerState.Initial, Push(Phone, 100));
        Assert.Equal(OwnerKind.Foreign, t.Kind);
        Assert.Equal(Phone, t.DeviceId);
        Assert.Equal(OwnerFx.StopHost, fx);
        Assert.False(PlaybackOwnership.RoutesLocal(t));
    }

    [Fact]
    public void F4_Foreign_IsLevelTriggered_EveryFoldStopsTheHost()
    {
        var s = Foreign(Phone, 100);
        var (same, fx1) = Fold(s, Push(Phone, 110));
        var (other, fx2) = Fold(same, Push(Speaker, 120));
        Assert.Equal(OwnerFx.StopHost, fx1);
        Assert.Equal(OwnerFx.StopHost, fx2);
        Assert.Equal(Speaker, other.DeviceId);
    }

    [Fact]
    public void F5_Foreign_ThenEmptyActive_IsNobodyFromForeign_NeverLocalDisplay()
    {
        var (t, fx) = Fold(Foreign(Phone, 100), Push("", 110));
        Assert.Equal(OwnerKind.Nobody, t.Kind);
        Assert.Equal(NobodyCause.FromForeign, t.Cause);
        Assert.Equal(Phone, t.DeviceId);
        Assert.Equal(OwnerFx.None, fx);                                    // never reloads anything
        Assert.False(PlaybackOwnership.ShowsLocalNowPlaying(t, hasLocalSession: true));
        Assert.False(PlaybackOwnership.AllowsLoad(t, LoadOrigin.Restore, paused: true) && false);
    }

    [Fact]
    public void F6_Foreign_ThenClusterNamesUsWithoutAClaim_IsStaleSelf()
    {
        var (t, fx) = Fold(Foreign(Phone, 100), Push(Us, 110));
        Assert.Equal(OwnerKind.Nobody, t.Kind);
        Assert.Equal(NobodyCause.StaleSelf, t.Cause);
        Assert.Equal(OwnerFx.None, fx);
    }

    // ── P: claim pending (Protected) ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P1_ResponseNamingUs_Adopts_AndSetsTheFence()
    {
        var (t, fx) = Fold(ProtectedClaim(OwnerState.Initial), Resp(7, Us, 200));
        Assert.Equal(OwnerKind.Us, t.Kind);
        Assert.Equal(ClaimPhase.Adopted, t.Claim);
        Assert.Equal(200, t.FenceServerTs);
        Assert.Equal(OwnerFx.None, fx);
    }

    [Fact]
    public void P1_AVerdictOvertakenByANewerForeignPush_DefersToThatPush()
    {
        var s = Fold(ProtectedClaim(OwnerState.Initial), Push(Phone, 250)).Item1;   // P2: recorded, not acted on
        var (t, fx) = Fold(s, Resp(7, Us, 200));                                      // the server adopted us at 200, the phone took it at 250
        Assert.Equal(OwnerKind.Foreign, t.Kind);
        Assert.Equal(Phone, t.DeviceId);
        Assert.Equal(OwnerFx.DropFrame | OwnerFx.StopHost | OwnerFx.EmitBecameInactive, fx);   // the stale frame itself is not folded
    }

    [Fact]
    public void P1_AVerdictOvertakenByANewerNobodyPush_SettlesUnadopted()
    {
        var s = Fold(ProtectedClaim(OwnerState.Initial), Push("", 250)).Item1;
        var (t, fx) = Fold(s, Resp(7, Us, 200));
        Assert.Equal(OwnerKind.Us, t.Kind);
        Assert.Equal(ClaimPhase.Unadopted, t.Claim);
        Assert.Equal(OwnerFx.DropFrame, fx);
    }

    [Fact]
    public void P2_ForeignPushDuringProtection_DoesNotRevoke()
    {
        var (t, fx) = Fold(ProtectedClaim(Foreign(Phone, 100)), Push(Phone, 150));
        Assert.Equal(OwnerKind.Us, t.Kind);
        Assert.Equal(ClaimPhase.Protected, t.Claim);
        Assert.Equal(Phone, t.LastSeenActive);
        Assert.Equal(OwnerFx.None, fx);
        Assert.True(PlaybackOwnership.AllowsLoad(t, LoadOrigin.Claim, paused: false));
    }

    [Fact]
    public void P2_ResponseToAPutSentBeforeTheClaim_IsNotTheVerdict()
    {
        var (t, _) = Fold(ProtectedClaim(Foreign(Phone, 100)), Resp(6, Phone, 150));
        Assert.Equal(OwnerKind.Us, t.Kind);
        Assert.Equal(ClaimPhase.Protected, t.Claim);
    }

    [Fact]
    public void P3_TheClaimsOwnResponseNamingAnotherDevice_RejectsTheClaim()
    {
        var (t, fx) = Fold(ProtectedClaim(Foreign(Phone, 100)), Resp(7, Phone, 150));
        Assert.Equal(OwnerKind.Foreign, t.Kind);
        Assert.Equal(Phone, t.DeviceId);
        Assert.Equal(OwnerFx.StopHost | OwnerFx.EmitBecameInactive | OwnerFx.ClaimRejected, fx);
    }

    [Fact]
    public void P3_ALaterPutsResponse_IsAlsoAVerdict()
    {
        var (t, fx) = Fold(ProtectedClaim(Foreign(Phone, 100)), Resp(9, Phone, 150));
        Assert.Equal(OwnerKind.Foreign, t.Kind);
        Assert.True((fx & OwnerFx.ClaimRejected) != 0);
    }

    [Fact]
    public void P4_ResponseNamingNobody_KeepsPlaying_Unadopted()
    {
        var (t, fx) = Fold(ProtectedClaim(OwnerState.Initial), Resp(7, "", 150));
        Assert.Equal(OwnerKind.Us, t.Kind);
        Assert.Equal(ClaimPhase.Unadopted, t.Claim);
        Assert.Equal(OwnerFx.None, fx);
        Assert.True(PlaybackOwnership.IsActiveOnWire(t));
    }

    // ── A: claim settled ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A1_Heartbeat_NamingUs_StaysAdopted_FenceAdvances()
    {
        var (t, fx) = Fold(Adopted(200), Push(Us, 300));
        Assert.Equal(ClaimPhase.Adopted, t.Claim);
        Assert.Equal(300, t.FenceServerTs);
        Assert.Equal(OwnerFx.None, fx);
    }

    [Fact]
    public void A2_ForeignNewerThanTheFence_IsATakeover()
    {
        var (t, fx) = Fold(Adopted(200), Push(Phone, 300));
        Assert.Equal(OwnerKind.Foreign, t.Kind);
        Assert.Equal(OwnerFx.StopHost | OwnerFx.EmitBecameInactive, fx);
    }

    [Fact]
    public void A2_FromUnadopted_ForeignNewerThanTheFence_IsATakeover()
    {
        var un = Fold(ProtectedClaim(OwnerState.Initial), Resp(7, "", 150)).Item1;
        var (t, fx) = Fold(un, Push(Phone, 160));
        Assert.Equal(OwnerKind.Foreign, t.Kind);
        Assert.Equal(OwnerFx.StopHost | OwnerFx.EmitBecameInactive, fx);
    }

    [Fact]
    public void A3_TheTakeoversTransitionalEmptyFrame_ThenThePhone_IsOneTakeover()
    {
        // 15:21:38.428 (reason 2, active "") then 38.943 (active=PHONE) — the old code demoted us on the empty frame
        // and then treated the real takeover as a "stray" stop (no BecameInactive, no generation bump).
        var (mid, fx1) = Fold(Adopted(200), Push("", 300));
        Assert.Equal(OwnerKind.Us, mid.Kind);
        Assert.Equal(ClaimPhase.Unadopted, mid.Claim);
        Assert.Equal(OwnerFx.None, fx1);
        var (t, fx2) = Fold(mid, Push(Phone, 310));
        Assert.Equal(OwnerKind.Foreign, t.Kind);
        Assert.Equal(OwnerFx.StopHost | OwnerFx.EmitBecameInactive, fx2);
    }

    // ── claims, put binding, expiry, release ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClaimWithoutAnAcknowledger_IsUnadoptedAtOnce_AndAForeignFrameAfterItRevokes()
    {
        var (s, _) = PlaybackOwnership.OnClaim(Foreign(Phone, 100), ClaimCause.InboundTransfer, 1, 5_000, 1000, acknowledged: false);
        Assert.Equal(ClaimPhase.Unadopted, s.Claim);
        Assert.Equal(100, s.FenceServerTs);
        Assert.Equal(OwnerKind.Foreign, Fold(s, Push(Phone, 150)).Item1.Kind);
        Assert.Equal(OwnerKind.Us, Fold(s, Push(Phone, 90)).Item1.Kind);        // older than the fence (and F0-dropped)
    }

    [Fact]
    public void OnPutSent_BindsOnlyTheFirstActivePut()
    {
        var (s, _) = PlaybackOwnership.OnClaim(OwnerState.Initial, ClaimCause.UserPlay, 1, 5_000, 1000, acknowledged: true);
        s = PlaybackOwnership.OnPutSent(s, 3, isActive: false);
        Assert.Equal(0u, s.ClaimMsgId);
        s = PlaybackOwnership.OnPutSent(s, 4, isActive: true);
        s = PlaybackOwnership.OnPutSent(s, 5, isActive: true);
        Assert.Equal(4u, s.ClaimMsgId);
    }

    [Fact]
    public void PutFailed_ForTheClaimsPut_SettlesUnadopted()
    {
        var (t, fx) = PlaybackOwnership.OnPutFailed(ProtectedClaim(OwnerState.Initial), 7);
        Assert.Equal(ClaimPhase.Unadopted, t.Claim);
        Assert.Equal(OwnerFx.None, fx);
    }

    [Fact]
    public void ProtectionExpiry_ForeignSeen_RejectsTheClaim()
    {
        var s = Fold(ProtectedClaim(Foreign(Phone, 100), nowMono: 1000), Push(Phone, 150)).Item1;
        Assert.Equal(OwnerKind.Us, PlaybackOwnership.OnTick(s, 5_999).Item1.Kind);   // not yet
        var (t, fx) = PlaybackOwnership.OnTick(s, 6_000);
        Assert.Equal(OwnerKind.Foreign, t.Kind);
        Assert.Equal(OwnerFx.StopHost | OwnerFx.EmitBecameInactive | OwnerFx.ClaimRejected, fx);
    }

    [Fact]
    public void ProtectionExpiry_NothingSeen_SettlesUnadopted()
    {
        var (t, fx) = PlaybackOwnership.OnTick(ProtectedClaim(OwnerState.Initial, nowMono: 1000), 6_000);
        Assert.Equal(OwnerKind.Us, t.Kind);
        Assert.Equal(ClaimPhase.Unadopted, t.Claim);
        Assert.Equal(OwnerFx.None, fx);
    }

    [Fact]
    public void ReClaimWhileUs_OnlyNewPlaybackRestampsStartedAt()
    {
        var s = Adopted(200);
        var resumed = PlaybackOwnership.OnClaim(s, ClaimCause.UserResume, 2, 9_000, 2000, acknowledged: true).Item1;
        Assert.Equal(5_000, resumed.ClaimStartedAtMs);
        Assert.Equal(ClaimPhase.Adopted, resumed.Claim);
        var played = PlaybackOwnership.OnClaim(s, ClaimCause.UserPlay, 3, 9_000, 2000, acknowledged: true).Item1;
        Assert.Equal(9_000, played.ClaimStartedAtMs);
        Assert.Equal(ClaimPhase.Adopted, played.Claim);
    }

    [Fact]
    public void Release_TransferAway_StopsEmitsAndPublishesInactive()
    {
        var (t, fx) = PlaybackOwnership.OnRelease(Adopted(), ReleaseCause.TransferAway);
        Assert.Equal(OwnerKind.Nobody, t.Kind);
        Assert.Equal(NobodyCause.FromUs, t.Cause);
        Assert.Equal(OwnerFx.StopHost | OwnerFx.EmitBecameInactive | OwnerFx.PublishInactive, fx);
    }

    [Fact]
    public void Release_EndOfContext_OnlyPublishesInactive()
    {
        var (t, fx) = PlaybackOwnership.OnRelease(Adopted(), ReleaseCause.EndOfContext);
        Assert.Equal(OwnerKind.Nobody, t.Kind);
        Assert.Equal(OwnerFx.PublishInactive, fx);
    }

    [Fact]
    public void Release_WhenNotUs_IsANoOp()
    {
        var s = Foreign(Phone);
        Assert.Equal((s, OwnerFx.None), PlaybackOwnership.OnRelease(s, ReleaseCause.Logout));
    }

    [Fact]
    public void IsActiveOnWire_OnlyUs()
    {
        Assert.False(PlaybackOwnership.IsActiveOnWire(OwnerState.Initial));
        Assert.False(PlaybackOwnership.IsActiveOnWire(Foreign(Phone)));
        Assert.True(PlaybackOwnership.IsActiveOnWire(Adopted()));
        Assert.True(PlaybackOwnership.IsActiveOnWire(ProtectedClaim(OwnerState.Initial)));
    }

    [Fact]
    public void AllowsLoad_ForeignNever_NobodyOnlyAPausedRestore_UsAlways()
    {
        Assert.False(PlaybackOwnership.AllowsLoad(Foreign(Phone), LoadOrigin.Claim, paused: false));
        Assert.False(PlaybackOwnership.AllowsLoad(Foreign(Phone), LoadOrigin.Restore, paused: true));
        Assert.True(PlaybackOwnership.AllowsLoad(OwnerState.Initial, LoadOrigin.Restore, paused: true));
        Assert.False(PlaybackOwnership.AllowsLoad(OwnerState.Initial, LoadOrigin.Restore, paused: false));
        Assert.False(PlaybackOwnership.AllowsLoad(OwnerState.Initial, LoadOrigin.Advance, paused: false));
        Assert.True(PlaybackOwnership.AllowsLoad(Adopted(), LoadOrigin.Advance, paused: false));
    }

    [Fact]
    public void ShowsLocalNowPlaying_FollowsTheOwner()
    {
        Assert.True(PlaybackOwnership.ShowsLocalNowPlaying(Adopted(), hasLocalSession: false));
        Assert.False(PlaybackOwnership.ShowsLocalNowPlaying(Foreign(Phone), hasLocalSession: true));
        Assert.True(PlaybackOwnership.ShowsLocalNowPlaying(OwnerState.Initial, hasLocalSession: true));
        Assert.False(PlaybackOwnership.ShowsLocalNowPlaying(OwnerState.Initial, hasLocalSession: false));
    }

    [Fact]
    public void ConnectOwnership_RaisesOnRealTransitionsOnly()
    {
        long mono = 0;
        var own = new ConnectOwnership(Us, () => mono, () => 5_000);
        var seen = new System.Collections.Generic.List<OwnerTransition>();
        own.Changed += seen.Add;

        own.OnCluster(Push(Phone, 100));           // Nobody → Foreign
        own.OnCluster(Push(Phone, 110));           // level-triggered StopHost: raised, state unchanged
        own.OnCluster(Push(Phone, 105));           // stale: dropped silently
        Assert.Equal(2, seen.Count);
        Assert.Equal(OwnerKind.Foreign, seen[0].To.Kind);
        Assert.Equal(OwnerFx.StopHost, seen[1].Fx);

        own.AttachAcknowledger();
        own.Claim(ClaimCause.InboundTransfer);
        Assert.Equal(ClaimPhase.Protected, own.Current.Claim);
        own.OnPutSent(11, isActive: true);
        own.OnCluster(new ClusterFrame(ClusterOrigin.PutResponse, 11, Us, 200));
        Assert.Equal(ClaimPhase.Adopted, own.Current.Claim);
        own.OnCluster(Push(Us, 210));              // heartbeat: silent
        Assert.Equal(5, seen.Count);               // Foreign, StopHost, claim, put-sent, adopted
    }
}
