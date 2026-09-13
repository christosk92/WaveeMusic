// ── Wavee.Tests/PlaybackStepTests.cs — the reducer (Wave 3, owner G) ──────────────────────────────────────────────
//
// Plan §4.15's shape, exactly: POST THESE INPUTS, ASSERT STATE AND EFFECTS. There is no player here, no socket, no
// window and no clock — `Step` is synchronous and every input carries its own frame stamp, which is the whole reason a
// playback session is a unit test at all (C2).
//
// The queue IS the live one (`Entities/Queue.cs`, Wave 1), so these facts boot a fake scope and join the entities
// collection. `Queue.Replace` asserts its own reading-order invariant, so a fixture that got the buckets wrong fails
// here rather than as "the wrong song" later.

using Wavee;
using Xunit;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PlaybackStepTests
{
    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────────────

    static QueueEdge Row(QueueBucket bucket, ulong id, QueueProvider provider = QueueProvider.Context)
        => new(id, (byte)provider, (byte)bucket);

    /// <summary>A real track row, so <see cref="EntityRef.Id"/> answers a live identity (a synthetic slot past the
    /// table's end honestly answers <c>default</c>, and half these facts are about what the identity does).</summary>
    static EntityRef Track(int n)
    {
        var id = EntityId.ForGid(EntityKind.Track, (UInt128)(ulong)(0x5000 + n));
        return new EntityRef(EntityKind.Track, Entities.Current.Tracks.Slot(id));
    }

    /// <summary>A session on the deck at index 0, with <paramref name="upNext"/> rows of context after it.</summary>
    static Playback.State PlayingQueueOf(int upNext)
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

    // ── C3: one drain, one Load ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ten_next_clicks_produce_one_load_with_the_last_epoch()
    {
        var s = PlayingQueueOf(12);
        var fx = new Playback.Effects();

        for (int i = 0; i < 10; i++) Playback.Step(ref s, new Playback.Input(Playback.InputKind.Next), ref fx);

        // Ten Steps, ten cursor moves — and ONE Load slot, carrying the epoch of the LAST of them. Nine streams were
        // never opened, which is the entire point of an effect SLOT.
        Assert.True(fx.Load);
        Assert.Equal(s.LoadEpoch, fx.LoadEpoch);
        Assert.Equal(s.Epoch, fx.LoadEpoch);
        Assert.Equal(10, s.Cursor.Index);
        Assert.Equal(Queue.RefAt(10), fx.LoadRow);
        Assert.Equal(Playback.Phase.Loading, s.Phase);
    }

    [Fact]
    public void The_state_answers_the_click_inside_the_frame()
    {
        // The row is bound to `Current`; the FIRST Step already moves it, before any shell has been asked for
        // anything. That is why the core needs no "busy" guard.
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();
        EntityRef before = s.Current;

        Playback.Step(ref s, Playback.Input.Next(), ref fx);

        Assert.NotEqual(before, s.Current);
        Assert.Equal(Queue.RefAt(1), s.Current);
        Assert.Equal(0, s.PosMs);
    }

    // ── C4: the epoch drop ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stale_audio_signal_is_ignored()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();
        uint stale = s.LoadEpoch;

        Playback.Step(ref s, Playback.Input.Next(), ref fx);           // supersedes `stale`
        Assert.NotEqual(stale, s.LoadEpoch);
        int posBefore = s.PosMs;
        var phaseBefore = s.Phase;

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Started, stale, nowMs: 500, arg: 90_000), ref fx);

        Assert.Equal(posBefore, s.PosMs);
        Assert.Equal(phaseBefore, s.Phase);
    }

    [Fact]
    public void The_matching_epochs_signal_is_taken()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Next(), ref fx);

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Started, s.LoadEpoch, nowMs: 500, arg: 1_500), ref fx);

        Assert.Equal(Playback.Phase.Playing, s.Phase);
        Assert.Equal(1_500, s.PosMs);
        Assert.Equal(500L, s.PosQpc);
    }

    [Fact]
    public void A_position_report_does_not_bump_the_epoch()
    {
        // If it did, the very load that is reporting would be cancelled by its own tick. This is the ~5 Hz path.
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();
        uint epoch = s.Epoch;

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Position, s.LoadEpoch, nowMs: 1_000, arg: 7_000), ref fx);

        Assert.Equal(epoch, s.Epoch);
        Assert.Equal(7_000, s.PosMs);
        Assert.True(fx.SmtcTimeline);
        Assert.False(fx.Load);
        Assert.False(fx.PublishState);
    }

    [Fact]
    public void A_superseded_transfer_answer_is_ignored()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();
        ulong phone = Playback.DeviceHash("phone");
        ulong tablet = Playback.DeviceHash("tablet");

        Playback.Step(ref s, Playback.Input.Transfer(phone), ref fx);
        uint first = s.TransferEpoch;
        Playback.Step(ref s, Playback.Input.Transfer(tablet), ref fx);
        Assert.NotEqual(first, s.TransferEpoch);
        Assert.Equal(tablet, fx.TransferTo);

        uint epoch = s.Epoch;
        Playback.Step(ref s, Playback.Input.TransferDone(first, ok: false), ref fx);
        Assert.Equal(epoch, s.Epoch);
    }

    // ── play, advance, end ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Play_claims_ownership_loads_and_announces()
    {
        var s = PlayingQueueOf(3);
        s.Own = Playback.OwnerState.Initial;
        var fx = new Playback.Effects();
        EntityRef row = Queue.RefAt(2);
        EntityId context = EntityId.ForGid(EntityKind.Playlist, (UInt128)77UL);

        Playback.Step(ref s, Playback.Input.Play(row, row.Id, context, Queue.CursorOf(2), nowMs: 4_000), ref fx);

        Assert.Equal(Playback.Owner.Us, s.Owner);
        Assert.Equal(Playback.ClaimPhase.Protected, s.Own.Claim);
        Assert.Equal(row, s.Current);
        Assert.Equal(context, s.Context);
        Assert.Equal(Playback.Phase.Loading, s.Phase);
        Assert.True(fx.Load);
        Assert.Equal(Playback.LoadOrigin.Claim, fx.LoadWhy);
        Assert.True(fx.PublishState);
        Assert.True(fx.PublishActive);
        Assert.Equal(Playback.PublishReason.PlayerStateChanged, fx.PublishWhy);
        Assert.True(fx.PrepareNext);
        Assert.Equal(Queue.RefAt(3), fx.NextRow);
        Assert.True(fx.Smtc);
        Assert.True(fx.Snapshot);
    }

    [Fact]
    public void The_end_of_the_queue_ends_the_phase_and_stops_the_host()
    {
        var s = PlayingQueueOf(1);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Next(), ref fx);           // to the last row
        fx.Clear();
        Playback.Step(ref s, Playback.Input.Next(), ref fx);           // off the end

        Assert.Equal(Playback.Phase.Ended, s.Phase);
        Assert.True(fx.Stop);
        Assert.Equal(Playback.StopReason.EndOfQueue, fx.StopWhy);
        Assert.False(fx.Load);                                          // never a silent wrap
        Assert.Equal(1, s.Cursor.Index);                                 // the cursor stayed where it was
    }

    [Fact]
    public void Repeat_context_wraps_to_the_head_instead_of_ending()
    {
        var s = PlayingQueueOf(1);
        s.Repeat = RepeatMode.Context;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Next(), ref fx);
        fx.Clear();
        Playback.Step(ref s, Playback.Input.Next(), ref fx);

        Assert.Equal(Playback.Phase.Loading, s.Phase);
        Assert.True(fx.Load);
        Assert.Equal(0, s.Cursor.Index);
    }

    [Fact]
    public void Repeat_track_restarts_the_row_without_moving_the_cursor()
    {
        var s = PlayingQueueOf(3);
        s.Repeat = RepeatMode.Track;
        s.PosMs = 60_000;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Next(nowMs: 9_000), ref fx);

        Assert.Equal(0, s.Cursor.Index);
        Assert.Equal(0, s.PosMs);
        Assert.True(fx.Seek);
        Assert.Equal(0, fx.SeekMs);
        Assert.False(fx.Load);
    }

    [Fact]
    public void Previous_inside_the_restart_window_steps_back_and_past_it_restarts()
    {
        var s = PlayingQueueOf(3);
        Assert.True(Queue.TryAdvance(ref s.Cursor, out _));             // sit on index 1
        s.PosMs = 1_000;
        s.PosQpc = 0;
        s.Phase = Playback.Phase.Paused;                                 // no extrapolation, so PosMs is the position
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Prev(nowMs: 0), ref fx);
        Assert.Equal(0, s.Cursor.Index);                                 // 1 s in: previous means PREVIOUS
        Assert.True(fx.Load);

        s.PosMs = Playback.RestartWindowMs + 1;
        fx.Clear();
        Playback.Step(ref s, Playback.Input.Prev(nowMs: 0), ref fx);
        Assert.Equal(0, s.Cursor.Index);                                 // 3 s in: it restarts THIS row
        Assert.False(fx.Load);
        Assert.True(fx.Seek);
    }

    [Fact]
    public void Ended_advances_in_the_same_drain_so_the_join_is_gapless()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 180_000), ref fx);

        Assert.True(fx.Load);                                            // the next stream is asked for in THIS drain
        Assert.Equal(1, s.Cursor.Index);
        Assert.True(s.HasBeenPlayingForMs > 0);
    }

    [Fact]
    public void A_stale_ended_never_advances()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();
        uint stale = s.LoadEpoch;
        Playback.Step(ref s, Playback.Input.Next(), ref fx);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Ended(stale, nowMs: 1), ref fx);

        Assert.False(fx.Load);
        Assert.Equal(1, s.Cursor.Index);
    }

    // ── pause / resume / seek / volume ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pause_freezes_the_extrapolated_position_and_never_releases_ownership()
    {
        var s = PlayingQueueOf(3);
        s.PosMs = 10_000;
        s.PosQpc = 1_000;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Pause(nowMs: 4_000), ref fx);

        Assert.Equal(13_000, s.PosMs);                                   // 10 s + the 3 s that elapsed
        Assert.Equal(Playback.Phase.Paused, s.Phase);
        Assert.Equal(Playback.Owner.Us, s.Owner);                        // ownership is not audibility
        Assert.True(fx.PauseHost);
        Assert.True(fx.PublishState);
    }

    [Fact]
    public void Seek_clamps_into_the_duration_and_a_no_seek_restriction_refuses()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Seek(999_999, nowMs: 2_000), ref fx);
        Assert.Equal(180_000, fx.SeekMs);
        Assert.Equal(180_000, s.PosMs);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Seek(-5, nowMs: 2_000), ref fx);
        Assert.Equal(0, fx.SeekMs);

        s.NoSeek = true;
        fx.Clear();
        Playback.Step(ref s, Playback.Input.Seek(1_000, nowMs: 2_000), ref fx);
        Assert.False(fx.Seek);
    }

    [Fact]
    public void Volume_is_the_wire_scale_in_and_a_linear_fraction_out()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Volume(0.5f), ref fx);

        Assert.True(fx.Volume);
        Assert.True(Math.Abs(0.5f - s.Volume) < 0.001f);
        Assert.Equal(Playback.PublishReason.VolumeChanged, fx.PublishWhy);

        // The same value again is not a change: no effect, no PUT.
        fx.Clear();
        Playback.Step(ref s, Playback.Input.Volume(0.5f), ref fx);
        Assert.False(fx.Volume);
        Assert.False(fx.PublishState);
    }

    // ── routing: every verb reads the ownership verdict ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_transport_verb_forwards_to_the_device_that_owns_playback()
    {
        var s = PlayingQueueOf(3);
        ulong phone = Playback.DeviceHash("phone");
        var fx = new Playback.Effects();

        // A newer-than-the-fence cluster naming the phone is a real takeover.
        var frame = new Playback.ClusterFrame(Spotify.Decode.ClusterOrigin.Push, 0, phone, 1_000);
        Playback.Step(ref s, Playback.Input.Cluster(in frame, default, nowMs: 0), ref fx);
        Assert.Equal(Playback.Owner.Foreign, s.Owner);
        Assert.False(s.RoutesLocal);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Next(), ref fx);

        Assert.False(fx.Load);                                           // nothing loads here any more
        Assert.True(fx.SendRemote);
        Assert.Equal(phone, fx.RemoteDevice);
        Assert.Equal(Spotify.Decode.RemoteCmd.SkipNext, fx.RemoteCmd);
    }

    [Fact]
    public void Volume_while_a_phone_owns_playback_is_a_volume_put_to_the_phone()
    {
        var s = PlayingQueueOf(3);
        ulong phone = Playback.DeviceHash("phone");
        var fx = new Playback.Effects();
        var frame = new Playback.ClusterFrame(Spotify.Decode.ClusterOrigin.Push, 0, phone, 1_000);
        Playback.Step(ref s, Playback.Input.Cluster(in frame, default, nowMs: 0), ref fx);
        float ours = s.Volume;
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Volume(0.25f), ref fx);

        Assert.True(fx.SendRemote);
        Assert.True(fx.RemoteIsVolume);                                  // its own route, not a player command
        Assert.Equal((long)Playback.Input.WireVolume(0.25f), fx.RemoteArg);
        Assert.False(fx.Volume);
        Assert.Equal(ours, s.Volume);                                     // our own sink is untouched
    }

    [Fact]
    public void A_transfer_stops_the_local_deck_immediately()
    {
        // Waiting for the cluster to confirm is what made 0.2.9 play here while the bar said "Playing on iPhone".
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();
        ulong phone = Playback.DeviceHash("phone");

        Playback.Step(ref s, Playback.Input.Transfer(phone, rosterSlot: 2, nowMs: 1_000), ref fx);

        Assert.Equal(Playback.Phase.Idle, s.Phase);
        Assert.Equal(Playback.Owner.Nobody, s.Owner);
        Assert.True(fx.Stop);
        Assert.True(fx.Transfer);
        Assert.Equal(phone, fx.TransferTo);
        Assert.Equal(2, fx.TransferSlot);
        Assert.Equal(Playback.PublishReason.BecameInactive, fx.PublishWhy);
        Assert.Equal(s.Epoch, s.LoadEpoch);                              // the in-flight load is superseded
    }

    [Fact]
    public void A_transfer_to_ourselves_is_refused_rather_than_sent()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Transfer(s.Us), ref fx);

        Assert.False(fx.Transfer);
        Assert.Equal(Playback.Phase.Playing, s.Phase);
    }

    // ── the mirror, the restrictions, the position ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_foreign_cluster_mirrors_the_remotes_row_so_the_bar_paints_it()
    {
        var s = PlayingQueueOf(3);
        ulong phone = Playback.DeviceHash("phone");
        var fx = new Playback.Effects();
        var frame = new Playback.ClusterFrame(Spotify.Decode.ClusterOrigin.Push, 0, phone, 1_000);
        EntityId remote = EntityId.ForGid(EntityKind.Track, (UInt128)0xBEEFUL);
        var mirrored = new Playback.RemoteState(true, remote, true, false, false,
            42_000, 1_000, 200_000, true, RepeatMode.Track, 32_768, NoPrev: true, NoNext: false, NoSeek: true);

        Playback.Step(ref s, Playback.Input.Cluster(in frame, in mirrored, nowMs: 5_000), ref fx);

        Assert.Equal(remote, s.CurrentId);
        Assert.Equal(Playback.Phase.Playing, s.Phase);
        Assert.Equal(42_000, s.PosMs);
        Assert.Equal(200_000, s.DurationMs);
        Assert.True(s.Shuffle);
        Assert.Equal(RepeatMode.Track, s.Repeat);
        Assert.True(Math.Abs(0.5f - s.Volume) < 0.001f);
        Assert.True(fx.Fetch);                                           // we have no row for the phone's track yet
        Assert.Equal(remote, fx.FetchId);
    }

    [Fact]
    public void Restrictions_fold_into_the_three_can_flags_and_refuse_the_verbs()
    {
        var s = PlayingQueueOf(3);
        Assert.True(s.CanSkipNext);
        Assert.True(s.CanSkipPrev);
        Assert.True(s.CanSeek);

        s.NoNext = true;
        s.NoPrev = true;
        Assert.False(s.CanSkipNext);
        Assert.False(s.CanSkipPrev);

        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Next(), ref fx);
        Assert.False(fx.Load);

        // An error disarms the whole transport, whatever the restrictions say.
        s.NoNext = false;
        s.Error = Playback.Fault.Network;
        Assert.False(s.CanTransport);
        Assert.False(s.CanSkipNext);
        Assert.False(s.CanSeek);
    }

    [Fact]
    public void An_unknown_duration_disables_the_rail_rather_than_drawing_a_full_grey_one()
    {
        var s = PlayingQueueOf(3);
        s.DurationMs = 0;
        Assert.False(s.CanSeek);
    }

    [Fact]
    public void The_position_extrapolates_from_the_frame_stamp_only_while_playing()
    {
        var s = PlayingQueueOf(3);
        s.PosMs = 5_000;
        s.PosQpc = 1_000;

        Assert.Equal(8_000, s.Position(4_000));
        Assert.Equal(180_000, s.Position(999_999));                      // clamped into the duration

        s.Phase = Playback.Phase.Paused;
        Assert.Equal(5_000, s.Position(4_000));                          // a paused clock does not run
    }

    [Fact]
    public void The_ticker_folds_the_extrapolation_and_produces_no_other_effect()
    {
        var s = PlayingQueueOf(3);
        s.PosMs = 5_000;
        s.PosQpc = 1_000;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Tick(nowMs: 2_000), ref fx);

        Assert.Equal(6_000, s.PosMs);
        Assert.Equal(2_000L, s.PosQpc);
        Assert.True(fx.SmtcTimeline);
        Assert.False(fx.PublishState);                                   // a tick that announced would be a PUT/second
        Assert.False(fx.Load);
        Assert.False(fx.Seek);
    }

    [Fact]
    public void Buffering_is_a_bit_beside_playing_and_never_a_phase()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Buffering, s.LoadEpoch, nowMs: 1_000), ref fx);
        Assert.True(s.Buffering);
        Assert.Equal(Playback.Phase.Playing, s.Phase);                   // the bar must not say "paused" mid-rebuffer

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Buffered, s.LoadEpoch, nowMs: 2_000), ref fx);
        Assert.False(s.Buffering);
        Assert.Equal(Playback.Phase.Playing, s.Phase);
    }

    [Fact]
    public void A_failed_load_keeps_the_row_on_the_deck_and_releases_nothing()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();
        EntityRef row = s.Current;

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Failed, s.LoadEpoch, 1_000, (long)Playback.Fault.Network), ref fx);

        Assert.Equal(Playback.Fault.Network, s.Error);
        Assert.Equal(Playback.Phase.Paused, s.Phase);
        Assert.Equal(row, s.Current);                                    // the bar can still say WHAT failed
        Assert.Equal(Playback.Owner.Us, s.Owner);                        // a failed load is not a transfer
        Assert.True(fx.Stop);
        Assert.Equal(Playback.StopReason.Failed, fx.StopWhy);
    }

    // ── inbound controller verbs ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_controllers_skip_claims_ownership_and_advances()
    {
        var s = PlayingQueueOf(3);
        s.Own = Playback.OwnerState.Initial;
        var fx = new Playback.Effects();
        var cmd = new Spotify.Decode.RemoteCommand(Spotify.Decode.RemoteCmd.SkipNext, Ok: true, MessageId: 4,
            SeekToMs: 0, BoolArg: false, Track: default, SenderHash: 9, SessionHash: 0, DedupeKey: 1);

        Playback.Step(ref s, Playback.Input.Controller(in cmd, nowMs: 1_000), ref fx);

        Assert.Equal(Playback.Owner.Us, s.Owner);
        Assert.Equal(1, s.Cursor.Index);
        Assert.True(fx.Load);
    }

    [Fact]
    public void A_command_we_could_not_read_is_never_acted_on()
    {
        // The glue already ACKED it — a retry storm from a phone in a pocket is worse than a body we cannot parse —
        // but `Ok == false` means the body was garbled and the reducer must do nothing with it.
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();
        var cmd = new Spotify.Decode.RemoteCommand(Spotify.Decode.RemoteCmd.SkipNext, Ok: false, MessageId: 4,
            SeekToMs: 0, BoolArg: false, Track: default, SenderHash: 9, SessionHash: 0, DedupeKey: 1);

        Playback.Step(ref s, Playback.Input.Controller(in cmd, nowMs: 1_000), ref fx);

        Assert.Equal(0, s.Cursor.Index);
        Assert.False(fx.Load);
    }

    [Fact]
    public void A_controllers_seek_goes_through_the_same_clamp_as_a_local_one()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();
        var cmd = new Spotify.Decode.RemoteCommand(Spotify.Decode.RemoteCmd.SeekTo, Ok: true, MessageId: 4,
            SeekToMs: 999_999, BoolArg: false, Track: default, SenderHash: 9, SessionHash: 0, DedupeKey: 1);

        Playback.Step(ref s, Playback.Input.Controller(in cmd, nowMs: 1_000), ref fx);

        Assert.True(fx.Seek);
        Assert.Equal(180_000, fx.SeekMs);
    }

    // ── the effect slots themselves ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_empties_every_slot_in_one_assignment()
    {
        var fx = new Playback.Effects();
        fx.Load = true;
        fx.LoadEpoch = 7;
        fx.PublishState = true;
        fx.Transfer = true;
        Assert.True(fx.Any);

        fx.Clear();

        Assert.False(fx.Any);
        Assert.False(fx.Load);
        Assert.Equal(0u, fx.LoadEpoch);
        Assert.False(fx.PublishState);
        Assert.False(fx.Transfer);
    }

    // ── P8: no allocation after warm-up ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_warm_step_allocates_nothing()
    {
        var s = PlayingQueueOf(64);
        var fx = new Playback.Effects();
        var next = Playback.Input.Next(nowMs: 1);
        var tick = Playback.Input.Tick(nowMs: 2);
        var position = Playback.Input.Audio(Playback.AudioSignal.Position, s.LoadEpoch, nowMs: 3, arg: 1_000);

        // Warm up: the JIT, the queue's spans, the ownership fold.
        for (int i = 0; i < 8; i++)
        {
            Playback.Step(ref s, in next, ref fx);
            Playback.Step(ref s, in tick, ref fx);
            fx.Clear();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            Playback.Step(ref s, in next, ref fx);
            Playback.Step(ref s, in tick, ref fx);
            Playback.Step(ref s, in position, ref fx);
            fx.Clear();
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void A_warm_ownership_fold_allocates_nothing()
    {
        var owner = Playback.OwnerState.Initial;
        ulong us = Playback.DeviceHash("wavee-device");
        ulong phone = Playback.DeviceHash("phone");
        var mine = new Playback.ClusterFrame(Spotify.Decode.ClusterOrigin.Push, 0, us, 1_000);
        var theirs = new Playback.ClusterFrame(Spotify.Decode.ClusterOrigin.Push, 0, phone, 2_000);

        for (int i = 0; i < 8; i++) { Playback.Ownership.Fold(ref owner, in mine, us); Playback.Ownership.Fold(ref owner, in theirs, us); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++)
        {
            Playback.Ownership.Fold(ref owner, in mine, us);
            Playback.Ownership.Fold(ref owner, in theirs, us);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }
}
