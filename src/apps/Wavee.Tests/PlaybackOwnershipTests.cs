// ── Wavee.Tests/PlaybackOwnershipTests.cs — the one authority, and its fence (C5) ─────────────────────────────────
//
// Ported from 0.2.9's `PlaybackOwnershipTests` / `ConnectOwnership*` / `ConnectIncident20260911Tests`, row for row:
// F0-F6 (playback is not ours), P1-P4 (a claim is pending), A1-A3 (the claim settled). The rows are the design's own
// names and they stay, because the incident reports reference them.
//
// WHY THIS FILE EXISTS AT ALL. 0.2.9 had TWO authorities that disagreed — a sticky `_ownsActivePlayback` demoted only
// on an active-id TRANSITION, and a raw cluster `ActiveDeviceId` plus a 5 s wall-clock window — plus several is_active
// writers that asked neither. Every Connect incident of 2026-09-11 was the two of them answering differently. There is
// one fold now, it is pure, and it lives in `Playback.cs`; these facts are what keeps it one.
//
// ORDERING. Only SERVER timestamps are compared with each other. A local claim is judged by the server's answer to it:
// the put-state RESPONSE is a Cluster, and its `server_timestamp_ms` becomes the FENCE.
//
// Device ids are `ulong` hashes here for the same reason they are in the fold: a CORE file may not walk a string per
// dealer push. `Playback.DeviceHash` is the ONE hasher, so a test and the app can never disagree about identity.

using Wavee;
using Xunit;

using ClusterOrigin = Wavee.Spotify.Decode.ClusterOrigin;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PlaybackOwnershipTests
{
    static readonly ulong Us = Playback.DeviceHash("wavee-device");
    static readonly ulong Phone = Playback.DeviceHash("phone-device");
    static readonly ulong Tablet = Playback.DeviceHash("tablet-device");

    static Playback.ClusterFrame Push(ulong active, long serverTs) => new(ClusterOrigin.Push, 0, active, serverTs);

    static Playback.ClusterFrame Response(ulong active, long serverTs, uint putMsgId)
        => new(ClusterOrigin.PutResponse, putMsgId, active, serverTs);

    /// <summary>Nobody owns playback: the honest launch state.</summary>
    static Playback.OwnerState Fresh() => Playback.OwnerState.Initial;

    /// <summary>A claim waiting for the server's verdict, with its is_active put already bound to
    /// <paramref name="msgId"/> — the state almost every P- and A- row starts from.</summary>
    static Playback.OwnerState Claimed(uint msgId = 1, long nowMs = 0)
    {
        var s = Fresh();
        Playback.Ownership.Claim(ref s, Playback.ClaimCause.UserPlay, 1, nowMs, nowMs, acknowledged: true);
        Playback.Ownership.PutSent(ref s, msgId, isActive: true);
        return s;
    }

    // ── THE fence rule ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cluster_older_than_fence_cannot_revoke_ownership()
    {
        // The rule the 2026-09-11 incidents cost a day each: a push the server built BEFORE it adopted our claim knows
        // nothing of that claim, so it cannot take playback away — however loudly it names another device.
        var s = Claimed();
        Playback.Ownership.Fold(ref s, Response(Us, 5_000, 1), Us);      // adopted; the fence is 5 000
        Assert.Equal(Playback.Owner.Us, s.Kind);
        Assert.Equal(Playback.ClaimPhase.Adopted, s.Claim);
        Assert.Equal(5_000L, s.Fence);

        Playback.OwnerFx fx = Playback.Ownership.Fold(ref s, Push(Phone, 4_000), Us);

        Assert.Equal(Playback.OwnerFx.DropFrame, fx);
        Assert.Equal(Playback.Owner.Us, s.Kind);
        Assert.Equal(5_000L, s.Fence);

        // …and one NEWER than the fence is a genuine takeover (A2).
        fx = Playback.Ownership.Fold(ref s, Push(Phone, 6_000), Us);
        Assert.Equal(Playback.Owner.Foreign, s.Kind);
        Assert.True(fx.HasFlag(Playback.OwnerFx.StopHost));
        Assert.True(fx.HasFlag(Playback.OwnerFx.EmitBecameInactive));
    }

    [Fact]
    public void F0_a_frame_without_a_server_time_is_never_dropped()
    {
        // Nothing to order it by, so it cannot be stale. Dropping it would lose the only cluster a service that omits
        // the field ever sends.
        var s = Fresh();
        Playback.Ownership.Fold(ref s, Push(Phone, 9_000), Us);
        Assert.Equal(Playback.Owner.Foreign, s.Kind);

        Playback.OwnerFx fx = Playback.Ownership.Fold(ref s, Push(Tablet, 0), Us);

        Assert.False(fx.HasFlag(Playback.OwnerFx.DropFrame));
        Assert.Equal(Tablet, s.Device);
    }

    // ── F1-F6: playback is not ours ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void F1_nobody_and_an_empty_active_stays_nobody()
    {
        var s = Fresh();
        Assert.Equal(Playback.OwnerFx.None, Playback.Ownership.Fold(ref s, Push(0, 1_000), Us));
        Assert.Equal(Playback.Owner.Nobody, s.Kind);
    }

    [Fact]
    public void F2_a_cluster_naming_us_without_a_claim_is_stale_self_and_not_ownership()
    {
        // The launch slot steal: a cluster left over from a previous session names this device, and 0.2.9 read that as
        // "we own playback" — then reloaded a track nobody asked for.
        var s = Fresh();
        Assert.Equal(Playback.OwnerFx.None, Playback.Ownership.Fold(ref s, Push(Us, 1_000), Us));
        Assert.Equal(Playback.Owner.Nobody, s.Kind);
        Assert.Equal(Playback.NobodyCause.StaleSelf, s.Cause);
        Assert.False(Playback.Ownership.IsActiveOnWire(in s));
    }

    [Fact]
    public void F3_nobody_and_a_foreign_active_becomes_foreign_and_stops_the_host()
    {
        var s = Fresh();
        Playback.OwnerFx fx = Playback.Ownership.Fold(ref s, Push(Phone, 1_000), Us);
        Assert.Equal(Playback.OwnerFx.StopHost, fx);
        Assert.Equal(Playback.Owner.Foreign, s.Kind);
        Assert.Equal(Phone, s.Device);
    }

    [Fact]
    public void F4_foreign_is_level_triggered_so_every_fold_stops_the_host()
    {
        // Deliberately level- and not edge-triggered: a host that started LATE still gets stopped.
        var s = Fresh();
        Playback.Ownership.Fold(ref s, Push(Phone, 1_000), Us);
        Assert.Equal(Playback.OwnerFx.StopHost, Playback.Ownership.Fold(ref s, Push(Phone, 2_000), Us));
        Assert.Equal(Playback.OwnerFx.StopHost, Playback.Ownership.Fold(ref s, Push(Phone, 3_000), Us));
    }

    [Fact]
    public void F5_foreign_then_an_empty_active_is_nobody_from_foreign_and_keeps_the_departed_snapshot()
    {
        var s = Fresh();
        Playback.Ownership.Fold(ref s, Push(Phone, 1_000), Us);
        Playback.Ownership.Fold(ref s, Push(0, 2_000), Us);

        Assert.Equal(Playback.Owner.Nobody, s.Kind);
        Assert.Equal(Playback.NobodyCause.FromForeign, s.Cause);
        Assert.Equal(Phone, s.Device);                                    // who left, so the bar can keep their row
        Assert.False(Playback.Ownership.ShowsLocalNowPlaying(in s, hasLocalSession: true));
    }

    [Fact]
    public void F6_foreign_then_a_cluster_naming_us_without_a_claim_is_stale_self()
    {
        var s = Fresh();
        Playback.Ownership.Fold(ref s, Push(Phone, 1_000), Us);
        Playback.Ownership.Fold(ref s, Push(Us, 2_000), Us);

        Assert.Equal(Playback.Owner.Nobody, s.Kind);
        Assert.Equal(Playback.NobodyCause.StaleSelf, s.Cause);
    }

    // ── P1-P4: a claim is pending ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void P1_the_response_naming_us_adopts_and_sets_the_fence()
    {
        var s = Claimed();
        Playback.OwnerFx fx = Playback.Ownership.Fold(ref s, Response(Us, 7_000, 1), Us);

        Assert.Equal(Playback.OwnerFx.None, fx);
        Assert.Equal(Playback.ClaimPhase.Adopted, s.Claim);
        Assert.Equal(7_000L, s.Fence);
        Assert.True(Playback.Ownership.IsActiveOnWire(in s));
    }

    [Fact]
    public void P1_a_verdict_overtaken_by_a_newer_foreign_push_defers_to_that_push()
    {
        // The push arrived DURING protection and its server time is past the fence the verdict just set: it was a real
        // takeover racing our claim. Honour it now rather than waiting for its next heartbeat.
        var s = Claimed();
        Playback.Ownership.Fold(ref s, Push(Phone, 9_000), Us);           // P2 records it
        Assert.Equal(Playback.Owner.Us, s.Kind);

        Playback.OwnerFx fx = Playback.Ownership.Fold(ref s, Response(Us, 8_000, 1), Us);

        Assert.Equal(Playback.Owner.Foreign, s.Kind);
        Assert.Equal(Phone, s.Device);
        Assert.True(fx.HasFlag(Playback.OwnerFx.StopHost));
        Assert.True(fx.HasFlag(Playback.OwnerFx.EmitBecameInactive));
    }

    [Fact]
    public void P2_a_foreign_push_during_protection_does_not_revoke()
    {
        var s = Claimed();
        Playback.OwnerFx fx = Playback.Ownership.Fold(ref s, Push(Phone, 9_000), Us);

        Assert.Equal(Playback.OwnerFx.None, fx);
        Assert.Equal(Playback.Owner.Us, s.Kind);
        Assert.Equal(Playback.ClaimPhase.Protected, s.Claim);
        Assert.Equal(Phone, s.LastSeenActive);                            // remembered, decided later
    }

    [Fact]
    public void P2_the_response_to_a_put_sent_before_the_claim_is_not_the_verdict()
    {
        // The message id is older than the one the claim bound to, so this response answers a question we asked in a
        // previous world.
        var s = Claimed(msgId: 5);
        Playback.OwnerFx fx = Playback.Ownership.Fold(ref s, Response(Phone, 9_000, 4), Us);

        Assert.Equal(Playback.OwnerFx.None, fx);
        Assert.Equal(Playback.Owner.Us, s.Kind);
        Assert.Equal(Playback.ClaimPhase.Protected, s.Claim);
    }

    [Fact]
    public void P3_the_claims_own_response_naming_another_device_rejects_the_claim()
    {
        var s = Claimed();
        Playback.OwnerFx fx = Playback.Ownership.Fold(ref s, Response(Phone, 9_000, 1), Us);

        Assert.Equal(Playback.Owner.Foreign, s.Kind);
        Assert.Equal(Phone, s.Device);
        Assert.True(fx.HasFlag(Playback.OwnerFx.StopHost));
        Assert.True(fx.HasFlag(Playback.OwnerFx.EmitBecameInactive));
        Assert.True(fx.HasFlag(Playback.OwnerFx.ClaimRejected));
    }

    [Fact]
    public void P3_a_later_puts_response_is_also_a_verdict()
    {
        var s = Claimed(msgId: 3);
        Playback.OwnerFx fx = Playback.Ownership.Fold(ref s, Response(Phone, 9_000, 4), Us);

        Assert.Equal(Playback.Owner.Foreign, s.Kind);
        Assert.True(fx.HasFlag(Playback.OwnerFx.ClaimRejected));
    }

    [Fact]
    public void P4_a_response_naming_nobody_keeps_playing_unadopted()
    {
        // A masked module / local-file context the server will not adopt. We play; we are just not on the cluster.
        var s = Claimed();
        Playback.OwnerFx fx = Playback.Ownership.Fold(ref s, Response(0, 9_000, 1), Us);

        Assert.Equal(Playback.OwnerFx.None, fx);
        Assert.Equal(Playback.Owner.Us, s.Kind);
        Assert.Equal(Playback.ClaimPhase.Unadopted, s.Claim);
        Assert.Equal(9_000L, s.Fence);
        Assert.True(Playback.Ownership.IsActiveOnWire(in s));
    }

    // ── A1-A3: the claim settled ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A1_a_heartbeat_naming_us_stays_adopted_and_the_fence_advances()
    {
        var s = Claimed();
        Playback.Ownership.Fold(ref s, Response(Us, 5_000, 1), Us);
        Playback.Ownership.Fold(ref s, Push(Us, 6_000), Us);

        Assert.Equal(Playback.ClaimPhase.Adopted, s.Claim);
        Assert.Equal(6_000L, s.Fence);
    }

    [Fact]
    public void A2_from_unadopted_a_foreign_frame_newer_than_the_fence_is_a_takeover()
    {
        var s = Claimed();
        Playback.Ownership.Fold(ref s, Response(0, 5_000, 1), Us);        // Unadopted, fence 5 000
        Playback.OwnerFx fx = Playback.Ownership.Fold(ref s, Push(Phone, 6_000), Us);

        Assert.Equal(Playback.Owner.Foreign, s.Kind);
        Assert.True(fx.HasFlag(Playback.OwnerFx.StopHost));
    }

    [Fact]
    public void A3_the_takeovers_transitional_empty_frame_then_the_phone_is_ONE_takeover()
    {
        // A real takeover often arrives as two frames: an empty active id, then the new owner. The empty one must not
        // count as "we lost it" and then "we lost it again" — that double-stop is the flip loop of incident 1.
        var s = Claimed();
        Playback.Ownership.Fold(ref s, Response(Us, 5_000, 1), Us);

        Playback.OwnerFx first = Playback.Ownership.Fold(ref s, Push(0, 6_000), Us);
        Assert.Equal(Playback.OwnerFx.None, first);
        Assert.Equal(Playback.Owner.Us, s.Kind);
        Assert.Equal(Playback.ClaimPhase.Unadopted, s.Claim);

        Playback.OwnerFx second = Playback.Ownership.Fold(ref s, Push(Phone, 7_000), Us);
        Assert.Equal(Playback.Owner.Foreign, s.Kind);
        Assert.True(second.HasFlag(Playback.OwnerFx.StopHost));
    }

    // ── claims, puts, expiry, release ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_claim_without_an_acknowledger_is_unadopted_at_once()
    {
        // No publisher (a unit test, --fake): there is no verdict to wait for, so protecting would be waiting for
        // nothing — and a foreign frame after it revokes immediately.
        var s = Fresh();
        Playback.Ownership.Claim(ref s, Playback.ClaimCause.UserPlay, 1, 0, 0, acknowledged: false);
        Assert.Equal(Playback.ClaimPhase.Unadopted, s.Claim);

        Playback.OwnerFx fx = Playback.Ownership.Fold(ref s, Push(Phone, 1_000), Us);
        Assert.Equal(Playback.Owner.Foreign, s.Kind);
        Assert.True(fx.HasFlag(Playback.OwnerFx.StopHost));
    }

    [Fact]
    public void PutSent_binds_only_the_first_active_put()
    {
        var s = Fresh();
        Playback.Ownership.Claim(ref s, Playback.ClaimCause.UserPlay, 1, 0, 0, acknowledged: true);
        Playback.Ownership.PutSent(ref s, 4, isActive: false);            // an inactive put is not a claim
        Assert.Equal(0u, s.ClaimMsgId);

        Playback.Ownership.PutSent(ref s, 5, isActive: true);
        Assert.Equal(5u, s.ClaimMsgId);

        Playback.Ownership.PutSent(ref s, 6, isActive: true);             // the FIRST one is the claim's
        Assert.Equal(5u, s.ClaimMsgId);
    }

    [Fact]
    public void A_put_that_failed_settles_the_claim_unadopted_rather_than_leaving_it_protected()
    {
        var s = Claimed(msgId: 7);
        Playback.Ownership.PutFailed(ref s, 6);                           // not the claim's put
        Assert.Equal(Playback.ClaimPhase.Protected, s.Claim);

        Playback.Ownership.PutFailed(ref s, 7);
        Assert.Equal(Playback.ClaimPhase.Unadopted, s.Claim);
        Assert.Equal(Playback.Owner.Us, s.Kind);                          // we keep playing
    }

    [Fact]
    public void Protection_expiry_with_a_foreign_device_seen_rejects_the_claim()
    {
        var s = Claimed(nowMs: 1_000);
        Playback.Ownership.Fold(ref s, Push(Phone, 9_000), Us);           // P2
        Assert.Equal(Playback.OwnerFx.None, Playback.Ownership.Tick(ref s, 1_000 + Playback.Ownership.ClaimProtectMs - 1));

        Playback.OwnerFx fx = Playback.Ownership.Tick(ref s, 1_000 + Playback.Ownership.ClaimProtectMs);

        Assert.Equal(Playback.Owner.Foreign, s.Kind);
        Assert.True(fx.HasFlag(Playback.OwnerFx.ClaimRejected));
    }

    [Fact]
    public void Protection_expiry_with_nothing_seen_settles_unadopted()
    {
        var s = Claimed(nowMs: 1_000);
        Playback.OwnerFx fx = Playback.Ownership.Tick(ref s, 1_000 + Playback.Ownership.ClaimProtectMs);

        Assert.Equal(Playback.OwnerFx.None, fx);
        Assert.Equal(Playback.Owner.Us, s.Kind);
        Assert.Equal(Playback.ClaimPhase.Unadopted, s.Claim);
    }

    [Fact]
    public void Reclaiming_while_already_ours_only_restamps_started_at_for_a_new_playback()
    {
        // The stamp the server's newest-starter rule compares across devices. A resume or a skip must not move it.
        var s = Claimed();
        Playback.Ownership.Fold(ref s, Response(Us, 5_000, 1), Us);

        Playback.Ownership.Claim(ref s, Playback.ClaimCause.UserResume, 2, 999, 0, acknowledged: true);
        Assert.Equal(0L, s.ClaimStartedAtMs);
        Assert.Equal(Playback.ClaimPhase.Adopted, s.Claim);               // the phase is kept

        Playback.Ownership.Claim(ref s, Playback.ClaimCause.UserPlay, 3, 999, 0, acknowledged: true);
        Assert.Equal(999L, s.ClaimStartedAtMs);
    }

    [Fact]
    public void Releasing_for_a_transfer_stops_emits_and_publishes_inactive()
    {
        var s = Claimed();
        Playback.OwnerFx fx = Playback.Ownership.Release(ref s, Playback.ReleaseCause.TransferAway);

        Assert.Equal(Playback.Owner.Nobody, s.Kind);
        Assert.Equal(Playback.NobodyCause.FromUs, s.Cause);
        Assert.True(fx.HasFlag(Playback.OwnerFx.StopHost));
        Assert.True(fx.HasFlag(Playback.OwnerFx.EmitBecameInactive));
        Assert.True(fx.HasFlag(Playback.OwnerFx.PublishInactive));
    }

    [Fact]
    public void Releasing_at_the_end_of_a_context_only_publishes_inactive()
    {
        var s = Claimed();
        Playback.OwnerFx fx = Playback.Ownership.Release(ref s, Playback.ReleaseCause.EndOfContext);

        Assert.Equal(Playback.OwnerFx.PublishInactive, fx);
        Assert.Equal(Playback.Owner.Nobody, s.Kind);
    }

    [Fact]
    public void Releasing_when_playback_is_not_ours_is_a_no_op()
    {
        var s = Fresh();
        Playback.Ownership.Fold(ref s, Push(Phone, 1_000), Us);
        Playback.OwnerFx fx = Playback.Ownership.Release(ref s, Playback.ReleaseCause.TransferAway);

        Assert.Equal(Playback.OwnerFx.None, fx);
        Assert.Equal(Playback.Owner.Foreign, s.Kind);
    }

    // ── the three questions the rest of the app asks the fold ────────────────────────────────────────────────────────

    [Fact]
    public void IsActiveOnWire_is_true_for_us_and_nobody_else()
    {
        var s = Claimed();
        Assert.True(Playback.Ownership.IsActiveOnWire(in s));

        Playback.Ownership.Fold(ref s, Response(Phone, 9_000, 1), Us);
        Assert.False(Playback.Ownership.IsActiveOnWire(in s));

        Playback.Ownership.Fold(ref s, Push(0, 10_000), Us);
        Assert.False(Playback.Ownership.IsActiveOnWire(in s));
    }

    [Fact]
    public void RoutesLocal_is_false_only_while_another_device_owns_playback()
    {
        var s = Fresh();
        Assert.True(Playback.Ownership.RoutesLocal(in s));

        Playback.Ownership.Fold(ref s, Push(Phone, 1_000), Us);
        Assert.False(Playback.Ownership.RoutesLocal(in s));

        Playback.Ownership.Fold(ref s, Push(0, 2_000), Us);
        Assert.True(Playback.Ownership.RoutesLocal(in s));
    }

    [Fact]
    public void AllowsLoad_lets_a_paused_restore_seed_the_deck_without_claiming()
    {
        var nobody = Fresh();
        Assert.True(Playback.Ownership.AllowsLoad(in nobody, Playback.LoadOrigin.Restore, paused: true));
        Assert.False(Playback.Ownership.AllowsLoad(in nobody, Playback.LoadOrigin.Restore, paused: false));
        Assert.False(Playback.Ownership.AllowsLoad(in nobody, Playback.LoadOrigin.Advance, paused: true));

        var us = Claimed();
        Assert.True(Playback.Ownership.AllowsLoad(in us, Playback.LoadOrigin.Advance, paused: false));

        var foreign = Fresh();
        Playback.Ownership.Fold(ref foreign, Push(Phone, 1_000), Us);
        Assert.False(Playback.Ownership.AllowsLoad(in foreign, Playback.LoadOrigin.Restore, paused: true));
    }

    [Fact]
    public void ShowsLocalNowPlaying_keeps_a_departed_devices_row_until_the_user_acts()
    {
        var us = Claimed();
        Assert.True(Playback.Ownership.ShowsLocalNowPlaying(in us, hasLocalSession: false));

        var fromForeign = Fresh();
        Playback.Ownership.Fold(ref fromForeign, Push(Phone, 1_000), Us);
        Playback.Ownership.Fold(ref fromForeign, Push(0, 2_000), Us);
        Assert.False(Playback.Ownership.ShowsLocalNowPlaying(in fromForeign, hasLocalSession: true));

        var fromUs = Claimed();
        Playback.Ownership.Release(ref fromUs, Playback.ReleaseCause.EndOfContext);
        Assert.True(Playback.Ownership.ShowsLocalNowPlaying(in fromUs, hasLocalSession: true));
        Assert.False(Playback.Ownership.ShowsLocalNowPlaying(in fromUs, hasLocalSession: false));
    }

    // ── the hasher ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_device_hasher_agrees_with_itself_across_both_overloads_and_reserves_zero()
    {
        Assert.Equal(Playback.DeviceHash("phone-device"),
                     Playback.DeviceHash(System.Text.Encoding.UTF8.GetBytes("phone-device")));
        Assert.NotEqual(Playback.DeviceHash("phone-device"), Playback.DeviceHash("phone-devicf"));
        Assert.Equal(0UL, Playback.DeviceHash(""));                        // "no device" — the fold's empty-id test
        Assert.Equal(0UL, Playback.DeviceHash(default(ReadOnlySpan<byte>)));
        Assert.NotEqual(0UL, Playback.DeviceHash("x"));
    }
}

// ── the same rules, reached the way the app reaches them: through Step ──────────────────────────────────────────────
//
// These need a live queue (`Step` reads `Entities.Current`), so they join the entities collection. They are the
// 2026-09-11 INCIDENTS, each one a sequence of inputs that used to end in the wrong place.

[Collection(EntitiesCollection.Name)]
public class PlaybackOwnershipThroughStepTests
{
    static readonly ulong Us = Playback.DeviceHash("wavee-device");
    static readonly ulong Phone = Playback.DeviceHash("phone-device");

    static Playback.ClusterFrame Push(ulong active, long serverTs) => new(ClusterOrigin.Push, 0, active, serverTs);

    static Playback.ClusterFrame Response(ulong active, long serverTs, uint putMsgId)
        => new(ClusterOrigin.PutResponse, putMsgId, active, serverTs);

    static EntityRef Track(int n)
    {
        var id = EntityId.ForGid(EntityKind.Track, (UInt128)(ulong)(0x7000 + n));
        return new EntityRef(EntityKind.Track, Entities.Current.Tracks.Slot(id));
    }

    static Playback.State Session()
    {
        TestScope.Fresh();
        EntityRef[] refs = [Track(0), Track(1), Track(2)];
        QueueEdge[] rows =
        [
            new(1, (byte)QueueProvider.Context, (byte)QueueBucket.NowPlaying),
            new(2, (byte)QueueProvider.Context, (byte)QueueBucket.NextUp),
            new(3, (byte)QueueProvider.Context, (byte)QueueBucket.NextUp),
        ];
        Queue.Replace(refs, rows);

        var s = Playback.State.Initial;
        s.Us = Us;
        s.Current = refs[0];
        s.CurrentId = refs[0].Id;
        s.Cursor = Queue.CursorOf(0);
        s.Phase = Playback.Phase.Playing;
        s.DurationMs = 180_000;
        return s;
    }

    [Fact]
    public void The_put_state_verdict_flips_ownership()
    {
        // The ROUND TRIP, end to end through the reducer: we claim, the put binds, and the server answers by keeping
        // the phone. That answer — and nothing else — is what takes playback away from us.
        var s = Session();
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Play(s.Current, s.CurrentId, default, s.Cursor, nowMs: 1_000), ref fx);
        Assert.Equal(Playback.Owner.Us, s.Owner);
        Assert.Equal(Playback.ClaimPhase.Protected, s.Own.Claim);

        Playback.Step(ref s, Playback.Input.PutSent(11, isActive: true), ref fx);
        Assert.Equal(11u, s.Own.ClaimMsgId);
        fx.Clear();

        var verdict = Response(Phone, 9_000, 11);
        Playback.Step(ref s, Playback.Input.Cluster(in verdict, default, nowMs: 2_000), ref fx);

        Assert.Equal(Playback.Owner.Foreign, s.Owner);
        Assert.Equal(Phone, s.Own.Device);
        Assert.Equal(Playback.Phase.Idle, s.Phase);
        Assert.True(fx.Stop);
        Assert.Equal(Playback.StopReason.LostOwnership, fx.StopWhy);
        Assert.Equal(s.Epoch, s.LoadEpoch);                               // the load in flight is superseded
    }

    [Fact]
    public void A_lost_put_response_still_ends_the_wait()
    {
        var s = Session();
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Play(s.Current, s.CurrentId, default, s.Cursor, nowMs: 1_000), ref fx);
        Playback.Step(ref s, Playback.Input.PutSent(3, isActive: true), ref fx);

        Playback.Step(ref s, Playback.Input.PutVerdict(3, accepted: false), ref fx);

        Assert.Equal(Playback.Owner.Us, s.Owner);
        Assert.Equal(Playback.ClaimPhase.Unadopted, s.Own.Claim);          // keeps playing, off the cluster
    }

    [Fact]
    public void Incident1_a_flip_loop_of_empty_active_frames_never_reloads_and_never_seeks()
    {
        // 2026-09-11 incident 1: a phone flapping in and out produced empty-active frames, each of which the two
        // authorities read differently — and every disagreement cost a reload and a seek.
        var s = Session();
        var fx = new Playback.Effects();
        var takeover = Push(Phone, 1_000);
        Playback.Step(ref s, Playback.Input.Cluster(in takeover, default, nowMs: 0), ref fx);
        Assert.Equal(Playback.Owner.Foreign, s.Owner);
        fx.Clear();

        for (int i = 0; i < 6; i++)
        {
            var empty = Push(0, 2_000 + i);
            Playback.Step(ref s, Playback.Input.Cluster(in empty, default, nowMs: i), ref fx);
            var again = Push(Phone, 2_100 + i);
            Playback.Step(ref s, Playback.Input.Cluster(in again, default, nowMs: i), ref fx);
        }

        Assert.False(fx.Load);
        Assert.False(fx.Seek);
        Assert.Equal(Playback.Owner.Foreign, s.Owner);
    }

    [Fact]
    public void Incident2_a_launch_cluster_naming_us_never_claims_and_never_reloads()
    {
        // The launch slot steal: a leftover cluster names this device, and 0.2.9 read it as ownership.
        var s = Session();
        s.Phase = Playback.Phase.Paused;
        var fx = new Playback.Effects();
        var stale = Push(Us, 1_000);

        Playback.Step(ref s, Playback.Input.Cluster(in stale, default, nowMs: 0), ref fx);

        Assert.Equal(Playback.Owner.Nobody, s.Owner);
        Assert.Equal(Playback.NobodyCause.StaleSelf, s.Own.Cause);
        Assert.False(fx.Load);
        Assert.False(fx.Stop);
        Assert.Equal(Playback.Phase.Paused, s.Phase);                      // the restored deck is left alone

        // …and a LEGITIMATE claim afterwards is honoured normally.
        fx.Clear();
        Playback.Step(ref s, Playback.Input.Play(s.Current, s.CurrentId, default, s.Cursor, nowMs: 2_000), ref fx);
        Assert.Equal(Playback.Owner.Us, s.Owner);
        Assert.True(fx.Load);
    }

    [Fact]
    public void Incident3_a_rejected_claim_stops_the_host_here_instead_of_playing_under_the_wrong_label()
    {
        // "transfer-to-self playing here while the bar said Playing on iPhone" — the two authorities disagreeing about
        // whose claim the server honoured.
        var s = Session();
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Play(s.Current, s.CurrentId, default, s.Cursor, nowMs: 1_000), ref fx);
        Playback.Step(ref s, Playback.Input.PutSent(1, isActive: true), ref fx);
        fx.Clear();

        var rejection = Response(Phone, 5_000, 1);
        Playback.Step(ref s, Playback.Input.Cluster(in rejection, default, nowMs: 2_000), ref fx);

        Assert.Equal(Playback.Owner.Foreign, s.Owner);
        Assert.True(fx.Stop);
        Assert.NotEqual(Playback.Phase.Playing, s.Phase);
    }

    [Fact]
    public void Incident4_a_foreign_takeover_swallows_the_hosts_later_ticks()
    {
        // "the PLAY glyph over audible playback": the local host kept reporting after the takeover, and those reports
        // were folded. The load epoch bump is what swallows them now.
        var s = Session();
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Play(s.Current, s.CurrentId, default, s.Cursor, nowMs: 1_000), ref fx);
        Playback.Step(ref s, Playback.Input.PutSent(1, isActive: true), ref fx);
        var adopted = Response(Us, 5_000, 1);
        Playback.Step(ref s, Playback.Input.Cluster(in adopted, default, nowMs: 1_050), ref fx);
        Assert.Equal(Playback.ClaimPhase.Adopted, s.Own.Claim);

        uint loadEpoch = s.LoadEpoch;
        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Started, loadEpoch, 1_100, 0), ref fx);
        Assert.Equal(Playback.Phase.Playing, s.Phase);
        fx.Clear();

        var takeover = Push(Phone, 9_000);
        Playback.Step(ref s, Playback.Input.Cluster(in takeover, default, nowMs: 1_200), ref fx);
        Assert.Equal(Playback.Owner.Foreign, s.Owner);
        Assert.NotEqual(loadEpoch, s.LoadEpoch);

        // The old host's next position tick arrives, stamped with the dead epoch. It must change nothing.
        int posBefore = s.PosMs;
        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Position, loadEpoch, 1_300, 44_000), ref fx);
        Assert.Equal(posBefore, s.PosMs);
    }

    [Fact]
    public void Incident5_while_playback_is_not_ours_the_announce_never_claims_is_active()
    {
        var s = Session();
        var fx = new Playback.Effects();
        var takeover = Push(Phone, 1_000);
        Playback.Step(ref s, Playback.Input.Cluster(in takeover, default, nowMs: 0), ref fx);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Release(Playback.ReleaseCause.EndOfContext), ref fx);
        Assert.False(fx.PublishState);                                     // we were not the owner: nothing to release

        // And a snapshot captured while a phone owns playback carries an EMPTY player half plus OUR volume.
        var identity = new Playback.DeviceIdentity("wavee-device", "Wavee", "cid", "Win32", "1.2.3", "3.2.6");
        var snapshot = Playback.Snapshot.Of(in s, in identity, Playback.PublishReason.PlayerStateChanged, 1,
            unixMs: 1_700_000_000_000, frameNowMs: 0);
        Assert.False(snapshot.IsActive);
        Assert.False(snapshot.HasTrack);
        Assert.Equal(Playback.Input.WireVolume(s.Volume), snapshot.Volume);
    }

    [Fact]
    public void Losing_ownership_clears_the_stream_format_badge()
    {
        // Ch 21 G8: we cannot know a phone's format, and 0.2.9 left ours on screen.
        var s = Session();
        s.StreamFormat = Entities.Strings.Intern("OGG 320");
        var fx = new Playback.Effects();
        var takeover = Push(Phone, 1_000);

        Playback.Step(ref s, Playback.Input.Cluster(in takeover, default, nowMs: 0), ref fx);

        Assert.True(s.StreamFormat.IsEmpty);
    }
}
