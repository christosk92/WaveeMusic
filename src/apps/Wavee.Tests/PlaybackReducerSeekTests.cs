// ── Wavee.Tests/PlaybackReducerSeekTests.cs — the reducer's seek, position and prepare arms (playback smoothness, #167) ──
//
// Plan docs/plans/wavee/playback-smoothness-implementation.md §4.9 (reducer) and §5 (tests): POST THESE INPUTS, ASSERT
// STATE AND EFFECTS — the same shape as `PlaybackStepTests`, and for the same reason: `Playback.Step` is synchronous, owns
// no clock (every input carries its own frame stamp) and touches no player, so a seek that "snaps the thumb back" is a unit
// test, not an on-box hunt. The audit evidence behind each fact is `playback-audit-and-scrubbing.md` §1:
//
//   U-1  A local seek was posted under the TRANSPORT epoch (`fx.SeekEpoch = s.Epoch`); `DoAudio` drops every report whose
//        epoch is not `LoadEpoch`, so every user seek's `Seeked` confirmation was discarded and the painted position ran
//        ahead of the audio until the next 1 s sample stepped it back. Fix: `EmitSeek` stamps `LoadEpoch` and bumps a
//        SEEK GENERATION the pump stamps on its `Position`/`Seeked` reports; an older generation is dropped (V-PA2).
//   U-3  A seek while another device owns playback only forwarded `seek_to`; the thumb snapped back to the mirror's
//        extrapolated old position until the cluster echoed. Fix: the drop point is painted at once and HELD for
//        `RemoteSeekHoldMs` against reports more than `RemoteSeekToleranceMs` away, released by tolerance, expiry or a
//        track change — not by a bare newer ack, which may acknowledge another command (V-PA10).
//   U-5  `Position(now)` extrapolated through a buffering stall and `Buffered` re-anchored the clock against the stale
//        `PosMs`: the playhead jumped back by the stall's length. Fix: frozen at the `Buffering` edge and held while it lasts.
//   V-PA33  A seek moves the playhead out from under the join the pump scheduled: `PrepareLost` re-arms the prepared row.
//
// The queue is the live one (`Entities/Queue.cs`), so every fact boots a fake scope and joins the entities collection.

using Wavee;
using Xunit;

using RemoteCmd = Wavee.Spotify.Decode.RemoteCmd;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PlaybackReducerSeekTests
{
    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly ulong Phone = Playback.DeviceHash("phone");
    static readonly EntityId RemoteTrack = EntityId.ForGid(EntityKind.Track, (UInt128)0xBEEFUL);
    static readonly EntityId OtherRemoteTrack = EntityId.ForGid(EntityKind.Track, (UInt128)0xF00DUL);

    /// <summary>The foreign owner's row length, and where its drag lands in the remote-seek facts. The target sits far from
    /// the pre-seek report so "the cluster still disagrees" is unambiguous.</summary>
    const int RemoteDurationMs = 200_000, RemoteTargetMs = 120_000;
    /// <summary>The frame-clock stamps of the remote-seek arrangement: the owner's first report, and the user's drop.</summary>
    const long FirstClusterAt = 1_000, SeekAt = 5_000;
    /// <summary>Where the owner still says it is when it has not heard the seek yet.</summary>
    const long PreSeekPositionMs = 10_500;

    static QueueEdge Row(QueueBucket bucket, ulong id, QueueProvider provider = QueueProvider.Context)
        => new(id, (byte)provider, (byte)bucket);

    static EntityRef Track(int n)
    {
        var id = EntityId.ForGid(EntityKind.Track, (UInt128)(ulong)(0x6000 + n));
        return new EntityRef(EntityKind.Track, Entities.Current.Tracks.Slot(id));
    }

    /// <summary>Row 0 on the deck, playing and OURS, with <paramref name="upNext"/> rows of context after it. The load epoch is
    /// the state's own, so an input stamped <c>s.LoadEpoch</c> is a current one.</summary>
    static Playback.State Playing(int upNext = 3)
    {
        TestScope.Fresh();
        var refs = new EntityRef[upNext + 1];
        var rows = new QueueEdge[upNext + 1];
        refs[0] = Track(0);
        rows[0] = Row(QueueBucket.NowPlaying, 1);
        for (int i = 1; i <= upNext; i++)
        {
            refs[i] = Track(i);
            rows[i] = Row(QueueBucket.NextUp, (ulong)(i + 1));
        }
        Queue.Replace(refs, rows);

        var s = Playback.State.Initial;
        s.Us = Playback.DeviceHash("wavee-device");
        s.Current = refs[0];
        s.CurrentId = refs[0].Id;
        s.Cursor = Queue.CursorOf(0);
        s.Phase = Playback.Phase.Playing;
        s.DurationMs = 180_000;
        s.LoadEpoch = s.Epoch;
        Playback.Ownership.Claim(ref s.Own, Playback.ClaimCause.UserPlay, 1, 0, 0, acknowledged: false);
        return s;
    }

    // ── U-1 / V-PA2: EmitSeek — one emitter, LOAD epoch, a generation ───────────────────────────────────────────────

    [Fact]
    public void A_local_seek_is_posted_under_the_load_epoch_and_bumps_the_generation()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        uint loadEpoch = s.LoadEpoch;
        Assert.Equal(0u, s.SeekGen);

        Playback.Step(ref s, Playback.Input.Seek(60_000, nowMs: 2_000), ref fx);

        Assert.True(fx.Seek);
        Assert.Equal(60_000, fx.SeekMs);
        Assert.Equal(loadEpoch, fx.SeekEpoch);                           // U-1: the epoch DoAudio compares against…
        Assert.Equal(s.LoadEpoch, fx.SeekEpoch);
        Assert.NotEqual(s.Epoch, fx.SeekEpoch);                          // …NOT the transport epoch the seek itself just bumped
        Assert.Equal(1u, s.SeekGen);
        Assert.Equal(s.SeekGen, fx.SeekGen);                             // the pump stamps THIS on its reports
        Assert.Equal(60_000, s.PosMs);                                   // the drop point is painted at once, on the frame clock
        Assert.Equal(2_000L, s.PosQpc);
        Assert.True(fx.PublishState);
        Assert.True(fx.SmtcTimeline);
    }

    [Fact]
    public void Two_seeks_in_one_drain_leave_one_slot_carrying_the_last_target_and_the_last_generation()
    {
        var s = Playing();
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Seek(30_000, nowMs: 2_000), ref fx);
        Playback.Step(ref s, Playback.Input.Seek(90_000, nowMs: 2_010), ref fx);

        Assert.Equal(2u, s.SeekGen);
        Assert.Equal(2u, fx.SeekGen);                                    // last write wins (C3): the pump runs the newest target only
        Assert.Equal(90_000, fx.SeekMs);
        Assert.Equal(s.LoadEpoch, fx.SeekEpoch);
    }

    [Fact]
    public void Restart_goes_through_the_same_emitter_when_previous_is_pressed_past_the_restart_window()
    {
        var s = Playing();
        s.PosMs = Playback.RestartWindowMs + 1;
        s.PosQpc = 0;
        s.Phase = Playback.Phase.Paused;                                 // no extrapolation, so PosMs is the position
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Prev(nowMs: 4_000), ref fx);

        Assert.False(fx.Load);                                           // it restarts THIS row…
        Assert.True(fx.Seek);                                            // …through a seek to 0 that is a first-class seek:
        Assert.Equal(0, fx.SeekMs);
        Assert.Equal(s.LoadEpoch, fx.SeekEpoch);                         // posted under the LOAD epoch (it used to be s.Epoch)
        Assert.Equal(1u, s.SeekGen);                                     // and bumps the generation
        Assert.Equal(1u, fx.SeekGen);
        Assert.Equal(0, s.PosMs);
        Assert.Equal(4_000L, s.PosQpc);
    }

    [Fact]
    public void Repeat_one_restarts_through_the_same_emitter_too()
    {
        var s = Playing();
        s.Repeat = RepeatMode.Track;
        s.PosMs = 60_000;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Next(nowMs: 9_000), ref fx);

        Assert.True(fx.Seek);
        Assert.Equal(0, fx.SeekMs);
        Assert.Equal(s.LoadEpoch, fx.SeekEpoch);
        Assert.Equal(1u, s.SeekGen);
        Assert.Equal(s.SeekGen, fx.SeekGen);
    }

    [Fact]
    public void A_parked_or_refused_seek_emits_nothing_and_bumps_no_generation()
    {
        // Nothing live to seek: the parked deck only moves where its eventual load will start — no seek effect, so no
        // generation (a bump nobody will ever stamp a report with would drop the NEXT real report).
        var parked = Playing();
        parked.Parked = true;
        var fx = new Playback.Effects();
        Playback.Step(ref parked, Playback.Input.Seek(42_000, nowMs: 2_000), ref fx);
        Assert.False(fx.Seek);
        Assert.Equal(0u, parked.SeekGen);
        Assert.Equal(42_000, parked.PosMs);

        var refused = Playing();
        refused.NoSeek = true;
        fx.Clear();
        Playback.Step(ref refused, Playback.Input.Seek(42_000, nowMs: 2_000), ref fx);
        Assert.False(fx.Seek);
        Assert.Equal(0u, refused.SeekGen);
    }

    [Fact]
    public void Every_load_carries_the_seek_generation_so_the_pump_is_seeded_from_the_reducers_counter()
    {
        var s = Playing(upNext: 4);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Seek(30_000, nowMs: 1_000), ref fx);
        Playback.Step(ref s, Playback.Input.Seek(40_000, nowMs: 1_100), ref fx);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Next(nowMs: 1_200), ref fx);

        Assert.True(fx.Load);
        Assert.Equal(s.SeekGen, fx.SeekGen);                             // V-PA2: Audio.Load(…, seekGen: s_fx.SeekGen)
    }

    // ── U-1: the confirmation lands; generations and epochs drop what is stale ──────────────────────────────────────

    [Fact]
    public void The_seeked_confirmation_of_a_local_seek_lands()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Seek(60_000, nowMs: 2_000), ref fx);
        uint epoch = fx.SeekEpoch, gen = fx.SeekGen;

        // The pump reports where it ACTUALLY landed (the page-exact position), stamped with what the seek effect carried.
        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Seeked, epoch, nowMs: 2_120, arg: 60_043, gen: gen), ref fx);

        Assert.Equal(60_043, s.PosMs);                                   // before U-1 this was dropped and PosMs stayed at 60_000
        Assert.Equal(2_120L, s.PosQpc);                                  // re-anchored on the frame the audio landed
        Assert.Equal(gen, s.LastSeekLandedGen);                          // what the seek rail's drop-point hold waits for (V-PA12)
    }

    [Fact]
    public void A_seeked_or_position_report_from_an_older_generation_is_dropped_and_the_current_one_lands()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Seek(30_000, nowMs: 1_000), ref fx);
        uint first = fx.SeekGen;
        Playback.Step(ref s, Playback.Input.Seek(90_000, nowMs: 1_050), ref fx);
        uint second = fx.SeekGen;
        Assert.NotEqual(first, second);
        long qpcAtDrop = s.PosQpc;

        // The FIRST seek's confirmation arrives after the second was issued: it would drag the thumb back over the drop point.
        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Seeked, s.LoadEpoch, nowMs: 1_100, arg: 30_020, gen: first), ref fx);
        Assert.Equal(90_000, s.PosMs);
        Assert.Equal(qpcAtDrop, s.PosQpc);
        Assert.Equal(0u, s.LastSeekLandedGen);

        // …and so does a periodic sample the pump took before the seek reached it.
        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Position, s.LoadEpoch, nowMs: 1_110, arg: 12_345, gen: first), ref fx);
        Assert.Equal(90_000, s.PosMs);
        Assert.Equal(qpcAtDrop, s.PosQpc);
        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Position, s.LoadEpoch, nowMs: 1_120, arg: 12_345), ref fx);   // gen 0
        Assert.Equal(90_000, s.PosMs);

        // a Started stamped with the superseded generation (the pump restarting the device before the swap) is a
        // pre-drop position too
        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Started, s.LoadEpoch, nowMs: 1_150, arg: 30_100, gen: first), ref fx);
        Assert.Equal(90_000, s.PosMs);

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Seeked, s.LoadEpoch, nowMs: 1_200, arg: 90_031, gen: second), ref fx);
        Assert.Equal(90_031, s.PosMs);
        Assert.Equal(second, s.LastSeekLandedGen);

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Position, s.LoadEpoch, nowMs: 2_200, arg: 91_050, gen: second), ref fx);
        Assert.Equal(91_050, s.PosMs);                                   // a current-generation sample is the ordinary 1 Hz path
        Assert.Equal(2_200L, s.PosQpc);
    }

    [Fact]
    public void A_report_of_the_right_generation_under_a_superseded_load_epoch_is_still_dropped()
    {
        // C4 stands: the generation is a SECOND guard, not a replacement for the load epoch.
        var s = Playing();
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Seek(60_000, nowMs: 2_000), ref fx);
        uint staleEpoch = s.LoadEpoch;
        uint gen = s.SeekGen;
        Playback.Step(ref s, Playback.Input.Next(nowMs: 2_100), ref fx);          // a new load supersedes the old one
        Assert.NotEqual(staleEpoch, s.LoadEpoch);
        int posAfterNext = s.PosMs;

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Seeked, staleEpoch, nowMs: 2_200, arg: 60_043, gen: gen), ref fx);
        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Position, staleEpoch, nowMs: 2_210, arg: 61_000, gen: gen), ref fx);

        Assert.Equal(posAfterNext, s.PosMs);
        Assert.Equal(0u, s.LastSeekLandedGen);
    }

    [Fact]
    public void An_episodes_ending_soon_still_fires_from_a_current_generation_position_and_not_from_a_stale_one()
    {
        // DoAudio's Position arm also runs the podcast endgame trigger. The generation guard sits in front of the WHOLE arm, so a
        // stale sample must neither move the playhead nor open the endgame — and a current one must keep opening it.
        var s = Playing();
        s.CurrentId = EntityId.ForGid(EntityKind.Episode, (UInt128)0xE15UL);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Seek(100_000, nowMs: 1_000), ref fx);
        uint gen = s.SeekGen;
        int nearTheEnd = s.DurationMs - 10_000;                           // inside the 30 s window

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Position, s.LoadEpoch, nowMs: 1_500, arg: nearTheEnd, gen: gen - 1u), ref fx);
        Assert.False(s.EndingSoon);
        Assert.Equal(100_000, s.PosMs);

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Position, s.LoadEpoch, nowMs: 2_000, arg: nearTheEnd, gen: gen), ref fx);
        Assert.True(s.EndingSoon);
        Assert.Equal(nearTheEnd, s.PosMs);
    }

    // ── U-3 / V-PA10: a seek while another device owns playback ─────────────────────────────────────────────────────

    /// <summary>One cluster from the phone: it plays <paramref name="track"/> at <paramref name="positionMs"/> as of this very
    /// moment (its own timestamp and the server's are the same, so the fold's projection adds nothing).</summary>
    static void PhonePlays(ref Playback.State s, ref Playback.Effects fx, long atMs, long positionMs, ulong ack = 0, EntityId? track = null)
    {
        var frame = new Playback.ClusterFrame(Spotify.Decode.ClusterOrigin.Push, 0, Phone, atMs, AckId: ack);
        var remote = new Playback.RemoteState(true, track ?? RemoteTrack, true, false, false,
            positionMs, atMs, RemoteDurationMs, false, RepeatMode.Off, -1, NoPrev: false, NoNext: false, NoSeek: false);
        Playback.Step(ref s, Playback.Input.Cluster(in frame, in remote, nowMs: atMs), ref fx);
    }

    /// <summary>The phone owns playback at 10 s; the user drops the thumb at <see cref="RemoteTargetMs"/> at
    /// <see cref="SeekAt"/>. <paramref name="standingAck"/> is the ack the first cluster carried.</summary>
    static Playback.State DroppedOnThePhone(out Playback.Effects fx, ulong standingAck = 0)
    {
        var s = Playing();
        fx = new Playback.Effects();
        PhonePlays(ref s, ref fx, FirstClusterAt, 10_000, standingAck);
        Assert.Equal(Playback.Owner.Foreign, s.Owner);
        fx.Clear();
        Playback.Step(ref s, Playback.Input.Seek(RemoteTargetMs, nowMs: SeekAt), ref fx);
        return s;
    }

    [Fact]
    public void A_remote_seek_forwards_one_seek_to_and_paints_the_drop_point_at_once()
    {
        var s = DroppedOnThePhone(out var fx);

        Assert.True(fx.SendRemote);                                      // one command to the owner…
        Assert.Equal(RemoteCmd.SeekTo, fx.RemoteCmd);
        Assert.Equal((long)RemoteTargetMs, fx.RemoteArg);
        Assert.Equal(Phone, fx.RemoteDevice);
        Assert.False(fx.Seek);                                           // …and nothing for OUR pump
        Assert.Equal(0u, s.SeekGen);                                     // no local generation: there is no local seek to confirm

        Assert.Equal(RemoteTargetMs, s.PosMs);                           // U-3: optimistic, on the frame clock
        Assert.Equal(SeekAt, s.PosQpc);
        Assert.Equal(SeekAt, s.RemoteSeekAtMs);
        Assert.Equal((long)RemoteTargetMs, s.RemoteSeekTargetMs);
        Assert.Equal(RemoteTargetMs, s.Position(SeekAt));
    }

    [Fact]
    public void A_remote_seek_is_clamped_like_a_local_one_and_the_clamped_target_is_what_is_held()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        PhonePlays(ref s, ref fx, FirstClusterAt, 10_000);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Seek(999_999, nowMs: SeekAt), ref fx);

        int expected = RemoteDurationMs - Playback.SeekTarget.TailGuardMs;
        Assert.Equal((long)expected, fx.RemoteArg);
        Assert.Equal(expected, s.PosMs);
        Assert.Equal((long)expected, s.RemoteSeekTargetMs);
    }

    [Fact]
    public void A_cluster_that_still_reports_the_pre_seek_position_is_held_off_while_the_drop_is_fresh()
    {
        var s = DroppedOnThePhone(out var fx);
        fx.Clear();

        PhonePlays(ref s, ref fx, SeekAt + 500, PreSeekPositionMs);        // the owner has not heard the seek yet

        Assert.Equal(RemoteTargetMs, s.PosMs);                           // the thumb does not snap back
        Assert.Equal(SeekAt, s.PosQpc);                                  // and the extrapolation clock was not re-anchored on the stale report
        Assert.Equal(SeekAt, s.RemoteSeekAtMs);
        Assert.Equal(Playback.Phase.Playing, s.Phase);                   // everything else about the cluster is still mirrored
        Assert.Equal(RemoteDurationMs, s.DurationMs);
    }

    [Fact]
    public void The_hold_lasts_exactly_the_hold_window_and_then_the_cluster_is_adopted()
    {
        var s = DroppedOnThePhone(out var fx);
        fx.Clear();
        long lastHeld = SeekAt + Playback.RemoteSeekHoldMs - 1;
        PhonePlays(ref s, ref fx, lastHeld, PreSeekPositionMs);
        Assert.Equal(RemoteTargetMs, s.PosMs);                           // one millisecond inside the window: still held
        Assert.Equal(SeekAt, s.RemoteSeekAtMs);

        long expires = SeekAt + Playback.RemoteSeekHoldMs;
        PhonePlays(ref s, ref fx, expires, PreSeekPositionMs);

        Assert.Equal((int)PreSeekPositionMs, s.PosMs);                   // a seek the owner never honoured: the owner is right
        Assert.Equal(expires, s.PosQpc);
        Assert.Equal(0L, s.RemoteSeekAtMs);                              // cleared: the next cluster is adopted as before
    }

    [Fact]
    public void A_cluster_within_the_tolerance_of_the_target_ends_the_hold_and_is_adopted()
    {
        var s = DroppedOnThePhone(out var fx);
        fx.Clear();
        long atTolerance = RemoteTargetMs - Playback.RemoteSeekToleranceMs;   // exactly 1.5 s short: the owner has caught up
        PhonePlays(ref s, ref fx, SeekAt + 500, atTolerance);

        Assert.Equal((int)atTolerance, s.PosMs);
        Assert.Equal(SeekAt + 500, s.PosQpc);
        Assert.Equal(0L, s.RemoteSeekAtMs);
    }

    [Fact]
    public void One_millisecond_beyond_the_tolerance_is_still_held()
    {
        var s = DroppedOnThePhone(out var fx);
        fx.Clear();

        PhonePlays(ref s, ref fx, SeekAt + 500, RemoteTargetMs - Playback.RemoteSeekToleranceMs - 1);

        Assert.Equal(RemoteTargetMs, s.PosMs);
        Assert.Equal(SeekAt, s.RemoteSeekAtMs);
    }

    [Fact]
    public void A_newer_cluster_ack_alone_does_not_end_the_hold_because_it_may_ack_another_command()
    {
        var s = DroppedOnThePhone(out var fx);
        fx.Clear();
        Assert.Equal(0UL, s.ClusterAck);

        // an ack for (say) a pause, arriving with the owner still on the pre-seek position: no snap-back
        PhonePlays(ref s, ref fx, SeekAt + 500, PreSeekPositionMs, ack: 0xACC0UL);

        Assert.Equal(0xACC0UL, s.ClusterAck);                            // recorded…
        Assert.Equal(RemoteTargetMs, s.PosMs);                           // …but the drop point holds
        Assert.Equal(SeekAt, s.RemoteSeekAtMs);

        // the owner reaching the target is what ends it
        PhonePlays(ref s, ref fx, SeekAt + 800, RemoteTargetMs + 200, ack: 0xACC1UL);
        Assert.Equal(0L, s.RemoteSeekAtMs);
        Assert.Equal(RemoteTargetMs + 200, s.PosMs);
    }

    [Fact]
    public void An_ack_that_is_not_newer_than_the_standing_one_does_not_end_the_hold()
    {
        var s = DroppedOnThePhone(out var fx, standingAck: 0xACC0UL);
        fx.Clear();

        PhonePlays(ref s, ref fx, SeekAt + 500, PreSeekPositionMs, ack: 0xACC0UL);   // the same ack: nothing of ours was applied

        Assert.Equal(RemoteTargetMs, s.PosMs);
        Assert.Equal(SeekAt, s.RemoteSeekAtMs);
    }

    [Fact]
    public void A_cluster_naming_another_track_drops_the_hold_for_the_old_one()
    {
        var s = DroppedOnThePhone(out var fx);
        fx.Clear();

        PhonePlays(ref s, ref fx, SeekAt + 500, 3_000, track: OtherRemoteTrack);

        Assert.Equal(OtherRemoteTrack, s.CurrentId);
        Assert.Equal(3_000, s.PosMs);                                    // a seek held for the OLD row says nothing about this one
        Assert.Equal(0L, s.RemoteSeekAtMs);
        Assert.True(fx.Fetch);
    }

    // ── U-5: the position does not run through a buffering stall ────────────────────────────────────────────────────

    [Fact]
    public void Position_is_held_while_buffering_and_resumes_from_the_edge_without_a_jump()
    {
        var s = Playing();
        s.PosMs = 5_000;
        s.PosQpc = 1_000;
        var fx = new Playback.Effects();
        Assert.Equal(7_000, s.Position(3_000));                           // the baseline: it extrapolates while flowing

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Buffering, s.LoadEpoch, nowMs: 3_000), ref fx);

        Assert.True(s.Buffering);
        Assert.Equal(Playback.Phase.Playing, s.Phase);                   // buffering is a bit beside playing, never a phase
        Assert.Equal(7_000, s.PosMs);                                    // frozen ON the edge: the extrapolation up to it is kept
        Assert.Equal(3_000L, s.PosQpc);
        Assert.Equal(7_000, s.Position(3_000));
        Assert.Equal(7_000, s.Position(60_000));                         // the stall's length is never extrapolated…

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Buffered, s.LoadEpoch, nowMs: 10_000), ref fx);

        Assert.False(s.Buffering);
        Assert.Equal(7_000, s.Position(10_000));                         // …so nothing snaps back when it ends
        Assert.Equal(8_000, s.Position(11_000));                         // and the clock runs on from the edge
    }

    [Fact]
    public void A_buffering_deck_reports_its_stored_position_whatever_the_clock_says()
    {
        var s = Playing();
        s.PosMs = 42_000;
        s.PosQpc = 0;
        s.Buffering = true;
        Assert.Equal(42_000, s.Position(0));
        Assert.Equal(42_000, s.Position(1_000_000));
        s.Buffering = false;
        Assert.Equal(s.DurationMs, s.Position(1_000_000));               // flowing again: extrapolated and clamped into the duration
    }

    // ── V-PA33: the pump dropped the join a seek moved the playhead out from under ─────────────────────────────────────

    [Fact]
    public void Prepare_lost_for_the_current_load_re_arms_the_prepared_row()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.EndingSoon, s.LoadEpoch, 170_000), ref fx);
        Assert.True(fx.PrepareNext);                                     // the endgame opened: the next row is prepared…
        Assert.True(s.NextArmed);
        EntityId next = s.NextId;
        fx.Clear();

        Playback.Step(ref s, Playback.Input.PrepareLost(s.LoadEpoch), ref fx);

        Assert.True(fx.PrepareNext);                                     // …and the pump's "I dropped it" prepares it AGAIN
        Assert.Equal(next, fx.NextId);
        Assert.True(s.NextArmed);
    }

    [Fact]
    public void Prepare_lost_for_a_superseded_load_is_dropped()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.EndingSoon, s.LoadEpoch, 170_000), ref fx);
        Assert.True(s.NextArmed);
        EntityId next = s.NextId;
        fx.Clear();

        Playback.Step(ref s, Playback.Input.PrepareLost(s.LoadEpoch + 1u), ref fx);

        Assert.False(fx.PrepareNext);
        Assert.True(s.NextArmed);                                        // the arm of the CURRENT load is untouched
        Assert.Equal(next, s.NextId);
    }

    [Fact]
    public void Prepare_lost_before_the_endgame_prepares_nothing()
    {
        // The row is only WARMED until the endgame opens (a prefetch, no ring): there is no prepared join to lose.
        var s = Playing();
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.PrepareLost(s.LoadEpoch), ref fx);

        Assert.False(fx.PrepareNext);
        Assert.False(s.NextArmed);
    }

    // ── D4 / V-PA5 / V-PA41: the scrub arms (WP 3d) ─────────────────────────────────────────────────────────────────
    //
    // The gesture is a value (`ScrubModel`) inside the state; the reducer turns its edges into effect slots. An AUDIBLE scrub (a
    // local, playing, non-video deck) holds the main voice and plays grains, so its release IS the seek: `fx.ScrubEnd` is set INSTEAD of
    // `fx.Seek`. Everything else scrubs visually and commits one ordinary seek.

    const long ScrubT0 = 1_000;

    static void Step(ref Playback.State s, ref Playback.Effects fx, Playback.Input i) => Playback.Step(ref s, i, ref fx);

    [Fact]
    public void An_audible_press_marks_the_gesture_and_asks_the_pump_for_grains_without_touching_the_generation()
    {
        var s = Playing();
        var fx = new Playback.Effects();

        Step(ref s, ref fx, Playback.Input.ScrubBegin(60_000, ScrubT0));

        Assert.True(fx.ScrubBegin);
        Assert.Equal(60_000, fx.ScrubMs);
        Assert.True(s.Scrub.Audible);
        Assert.Equal(ScrubModel.State.Pressed, s.Scrub.Current);
        Assert.Equal(60_000, s.ScrubPosMs);
        Assert.Equal(s.LoadEpoch, s.ScrubEpoch);                         // the gesture belongs to THIS load
        Assert.True(fx.Any);
        Assert.False(fx.Seek);                                           // a preview never reaches the position…
        Assert.Equal(0u, s.SeekGen);                                     // …nor the generation: only the release mints one
        Assert.False(fx.PublishState);
    }

    [Fact]
    public void Audibility_is_assigned_on_every_press_so_a_stale_flag_never_leaks_into_the_next_gesture()
    {
        var s = Playing();
        s.Phase = Playback.Phase.Paused;                                 // the device is stopped: nothing to hear
        s.Scrub.Audible = true;                                          // what an earlier gesture left behind (`Up` leaves it alone)
        var fx = new Playback.Effects();

        Step(ref s, ref fx, Playback.Input.ScrubBegin(60_000, ScrubT0));

        Assert.False(s.Scrub.Audible);
        Assert.False(fx.ScrubBegin);
    }

    [Theory]
    [InlineData(0)]    // paused
    [InlineData(1)]    // video
    [InlineData(2)]    // a deck that is not seekable
    [InlineData(3)]    // an errored row
    public void A_press_the_pump_cannot_play_is_visual_only(int why)
    {
        var s = Playing();
        switch (why)
        {
            case 0: s.Phase = Playback.Phase.Paused; break;
            case 1: s.Kind = Playback.PlayableKind.Video; break;
            case 2: s.NoSeek = true; break;
            default: s.Error = Playback.Fault.Network; break;
        }
        var fx = new Playback.Effects();

        Step(ref s, ref fx, Playback.Input.ScrubBegin(60_000, ScrubT0));

        Assert.False(fx.ScrubBegin);
        Assert.False(s.Scrub.Audible);
        Assert.Equal(ScrubModel.State.Pressed, s.Scrub.Current);         // the gesture itself still runs: the labels follow the pointer
        Assert.Equal(60_000, s.ScrubPosMs);
    }

    [Fact]
    public void Moves_are_coalesced_for_the_pump_while_the_labels_follow_every_one()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.ScrubBegin(60_000, ScrubT0));
        fx.Clear();

        Step(ref s, ref fx, Playback.Input.ScrubMove(61_000, ScrubT0 + 10));   // 10 ms after the press: inside the coalescing window
        Assert.False(fx.ScrubMove);
        Assert.False(fx.Any);
        Assert.Equal(61_000, s.ScrubPosMs);                              // …but the readout is the pointer's, not the pump's
        Assert.Equal(ScrubModel.State.Scrubbing, s.Scrub.Current);

        Step(ref s, ref fx, Playback.Input.ScrubMove(62_000, ScrubT0 + 60));
        Assert.True(fx.ScrubMove);
        Assert.Equal(62_000, fx.ScrubMs);
        Assert.Equal(35.0, fx.ScrubVelocity, 6);                         // smoothed audio-ms per wall-ms, handed over unchanged (V-PA9)
        Assert.Equal(62_000, s.ScrubPosMs);
        Assert.Equal(0u, s.SeekGen);
    }

    [Fact]
    public void A_visual_only_audio_scrub_sends_the_pump_no_moves()
    {
        var s = Playing();
        s.Phase = Playback.Phase.Paused;
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.ScrubBegin(60_000, ScrubT0));
        fx.Clear();

        Step(ref s, ref fx, Playback.Input.ScrubMove(90_000, ScrubT0 + 200));

        Assert.False(fx.ScrubMove);
        Assert.Equal(90_000, s.ScrubPosMs);
    }

    [Fact]
    public void The_release_of_an_audible_scrub_is_ScrubEnd_instead_of_Seek_and_mints_the_generation()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.ScrubBegin(60_000, ScrubT0));
        Step(ref s, ref fx, Playback.Input.ScrubMove(100_000, ScrubT0 + 80));
        fx.Clear();

        Step(ref s, ref fx, Playback.Input.ScrubEnd(120_000, ScrubT0 + 120));

        Assert.True(fx.ScrubEnd);
        Assert.False(fx.Seek);                                           // V-PA5: the pump's release IS the seek — exactly one of the two
        Assert.Equal(120_000, fx.SeekMs);
        Assert.Equal(s.LoadEpoch, fx.SeekEpoch);                         // the LOAD epoch, like every local seek (U-1)
        Assert.Equal(1u, s.SeekGen);
        Assert.Equal(1u, fx.SeekGen);
        Assert.Equal(120_000, s.PosMs);                                  // the drop point is painted at once, on the frame clock
        Assert.Equal(ScrubT0 + 120, s.PosQpc);
        Assert.True(fx.PublishState);
        Assert.True(fx.SmtcTimeline);
        Assert.Equal(ScrubModel.State.Idle, s.Scrub.Current);
        Assert.False(s.Scrub.Audible);

        fx.Clear();
        Step(ref s, ref fx, Playback.Input.ScrubEnd(130_000, ScrubT0 + 130));   // a second release of the same gesture is nothing
        Assert.False(fx.Any);
        Assert.Equal(1u, s.SeekGen);
    }

    [Fact]
    public void The_release_lands_where_the_reducers_own_seek_clamp_puts_it()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.ScrubBegin(60_000, ScrubT0));
        fx.Clear();

        Step(ref s, ref fx, Playback.Input.ScrubEnd(999_999, ScrubT0 + 100));

        Assert.True(fx.ScrubEnd);
        Assert.Equal(s.DurationMs - Playback.SeekTarget.TailGuardMs, fx.SeekMs);   // the tail guard, as `DoSeek`
        Assert.Equal(fx.SeekMs, s.PosMs);
    }

    [Fact]
    public void A_visual_scrub_commits_one_ordinary_seek_on_release()
    {
        var s = Playing();
        s.Phase = Playback.Phase.Paused;
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.ScrubBegin(60_000, ScrubT0));
        Step(ref s, ref fx, Playback.Input.ScrubMove(90_000, ScrubT0 + 100));
        fx.Clear();

        Step(ref s, ref fx, Playback.Input.ScrubEnd(90_000, ScrubT0 + 150));

        Assert.True(fx.Seek);                                            // paused: one plain seek (V-PA41)…
        Assert.False(fx.ScrubEnd);                                       // …no voice was held, so there is no release to run
        Assert.False(fx.ScrubCancel);
        Assert.Equal(90_000, fx.SeekMs);
        Assert.Equal(1u, s.SeekGen);
        Assert.Equal(s.SeekGen, fx.SeekGen);
    }

    [Fact]
    public void A_release_the_pump_will_not_run_lets_go_of_the_held_voice_and_does_not_leave_a_seek_behind()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.ScrubBegin(60_000, ScrubT0));
        Assert.True(s.Scrub.Audible);
        s.NoSeek = true;                                                 // the cluster forbade seeking while the pointer was down
        fx.Clear();

        Step(ref s, ref fx, Playback.Input.ScrubEnd(90_000, ScrubT0 + 100));

        Assert.True(fx.ScrubCancel);                                     // the held main voice resumes where it was…
        Assert.False(fx.ScrubEnd);
        Assert.False(fx.Seek);                                           // …and `DoSeek` refused, so nothing moves
        Assert.Equal(0u, s.SeekGen);
    }

    [Fact]
    public void A_foreign_owner_gets_the_visual_scrub_and_exactly_one_forwarded_seek_to()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        PhonePlays(ref s, ref fx, FirstClusterAt, 10_000);
        Assert.Equal(Playback.Owner.Foreign, s.Owner);
        fx.Clear();

        Step(ref s, ref fx, Playback.Input.ScrubBegin(100_000, SeekAt));
        Step(ref s, ref fx, Playback.Input.ScrubMove(110_000, SeekAt + 100));
        Assert.False(fx.Any);                                            // there is no local audio to scrub, and nothing to send yet
        Assert.Equal(110_000, s.ScrubPosMs);

        Step(ref s, ref fx, Playback.Input.ScrubEnd(RemoteTargetMs, SeekAt + 200));

        Assert.True(fx.SendRemote);                                      // ONE forwarded `seek_to`, with U-3's optimistic drop point
        Assert.Equal(RemoteCmd.SeekTo, fx.RemoteCmd);
        Assert.Equal((long)RemoteTargetMs, fx.RemoteArg);
        Assert.False(fx.Seek);
        Assert.False(fx.ScrubBegin || fx.ScrubMove || fx.ScrubEnd || fx.ScrubCancel);
        Assert.Equal(RemoteTargetMs, s.PosMs);
        Assert.Equal(SeekAt + 200, s.RemoteSeekAtMs);
        Assert.Equal(0u, s.SeekGen);
    }

    [Fact]
    public void Video_previews_ride_the_moves_and_the_release_is_one_ordinary_seek()
    {
        var s = Playing();
        s.Kind = Playback.PlayableKind.Video;
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.ScrubBegin(60_000, ScrubT0));
        Assert.False(fx.ScrubBegin);                                     // no grains through Media Foundation
        fx.Clear();

        Step(ref s, ref fx, Playback.Input.ScrubMove(70_000, ScrubT0 + 100));
        Assert.True(fx.ScrubMove);                                       // the host turns this into a keyframe preview
        Assert.Equal(70_000, fx.ScrubMs);
        fx.Clear();

        Step(ref s, ref fx, Playback.Input.ScrubEnd(70_000, ScrubT0 + 200));
        Assert.True(fx.Seek);                                            // the accurate seek, which also ends the preview's mute
        Assert.False(fx.ScrubEnd);
        Assert.Equal(70_000, fx.SeekMs);
    }

    [Fact]
    public void Cancel_releases_an_audible_scrub_once_and_a_later_release_commits_nothing()
    {
        var s = Playing();
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.ScrubBegin(60_000, ScrubT0));
        Step(ref s, ref fx, Playback.Input.ScrubMove(100_000, ScrubT0 + 80));
        fx.Clear();

        Step(ref s, ref fx, Playback.Input.ScrubCancel());
        Assert.True(fx.ScrubCancel);
        Assert.Equal(ScrubModel.State.Idle, s.Scrub.Current);
        Assert.False(s.Scrub.Audible);
        fx.Clear();

        Step(ref s, ref fx, Playback.Input.ScrubCancel());               // idempotent (V-PA24): the unmount cleanup may fire after a cancel
        Assert.False(fx.Any);
        Step(ref s, ref fx, Playback.Input.ScrubEnd(100_000, ScrubT0 + 200));
        Assert.False(fx.Any);                                            // the gesture is over: a late release is not a seek
        Assert.Equal(0u, s.SeekGen);
    }

    [Fact]
    public void Cancelling_a_video_scrub_asks_the_host_to_end_the_preview()
    {
        var s = Playing();
        s.Kind = Playback.PlayableKind.Video;
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.ScrubBegin(60_000, ScrubT0));
        fx.Clear();

        Step(ref s, ref fx, Playback.Input.ScrubCancel());

        Assert.True(fx.ScrubCancel);                                     // the host unmutes the preview
        Assert.False(fx.Seek);
    }

    [Fact]
    public void A_load_under_a_live_scrub_orphans_it_and_its_release_never_seeks_the_new_row()
    {
        var s = Playing(upNext: 3);
        var fx = new Playback.Effects();
        Step(ref s, ref fx, Playback.Input.ScrubBegin(60_000, ScrubT0));
        Step(ref s, ref fx, Playback.Input.Next(nowMs: ScrubT0 + 50));   // the track ended / was skipped mid-drag
        Assert.True(fx.Load);
        Assert.NotEqual(s.ScrubEpoch, s.LoadEpoch);
        uint gen = s.SeekGen;
        fx.Clear();

        Step(ref s, ref fx, Playback.Input.ScrubMove(100_000, ScrubT0 + 100));
        Assert.False(fx.Any);
        Assert.Equal(ScrubModel.State.Idle, s.Scrub.Current);            // the model went back to Idle with the row it belonged to

        Step(ref s, ref fx, Playback.Input.ScrubEnd(100_000, ScrubT0 + 150));
        Assert.False(fx.Seek);
        Assert.False(fx.ScrubEnd);
        Assert.Equal(gen, s.SeekGen);

        // a cancel for a gesture whose row is gone has nothing to release either: the pump's session went with the load
        s.Phase = Playback.Phase.Playing;                                // the new row is playing, so this press is audible
        Step(ref s, ref fx, Playback.Input.ScrubBegin(10_000, ScrubT0 + 200));
        Assert.True(s.Scrub.Audible);
        Step(ref s, ref fx, Playback.Input.Next(nowMs: ScrubT0 + 250));
        fx.Clear();
        Step(ref s, ref fx, Playback.Input.ScrubCancel());
        Assert.False(fx.ScrubCancel);
    }

    [Fact]
    public void Every_scrub_slot_counts_as_a_pending_effect()
    {
        Assert.True(new Playback.Effects { ScrubBegin = true }.Any);
        Assert.True(new Playback.Effects { ScrubMove = true }.Any);
        Assert.True(new Playback.Effects { ScrubEnd = true }.Any);
        Assert.True(new Playback.Effects { ScrubCancel = true }.Any);
        Assert.False(new Playback.Effects { ScrubMs = 5, ScrubVelocity = 2.0 }.Any);   // the payload alone is not an effect
    }
}
