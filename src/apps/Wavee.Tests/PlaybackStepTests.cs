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
        // A load only WARMS the next row (G-112); the full prepare waits for the endgame.
        Assert.True(fx.Prefetch);
        Assert.Equal(Queue.RefAt(3), fx.PrefetchRow);
        Assert.False(fx.PrepareNext);
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
    public void Ended_with_nothing_prepared_hard_cuts_to_the_next_row_in_the_same_drain()
    {
        // D4: a prepared row arrives as HandedOff; Ended is the hard-cut fallback, and it still asks in THIS drain.
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
        Assert.True(Math.Abs(0.5f - s.SliderVolume) < 0.001f);           // the slider follows the phone…
        Assert.Equal(1f, s.Volume);                                      // …and our own volume stays ours
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
        // The row the LISTENER chose (LoadOrigin.Claim — a click, a Retry): even a fault that is terminal for the row
        // parks it with Retry and says what is wrong. Stepping past a dead row is only for rows the deck moved onto by
        // itself (the auto-skip facts below).
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();
        EntityRef row = s.Current;
        Playback.Step(ref s, Playback.Input.Play(row, row.Id, default, Queue.CursorOf(0)), ref fx);
        Assert.Equal(Playback.LoadOrigin.Claim, s.LoadWhy);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Failed, s.LoadEpoch, 1_000, (long)Playback.Fault.Unavailable), ref fx);

        Assert.Equal(Playback.Fault.Unavailable, s.Error);
        Assert.Equal(Playback.Phase.Paused, s.Phase);
        Assert.True(s.Parked);
        Assert.Equal(row, s.Current);                                    // the bar can still say WHAT failed
        Assert.Equal(0, s.Cursor.Index);
        Assert.Equal(Playback.Owner.Us, s.Owner);                        // a failed load is not a transfer
        Assert.False(fx.Load);                                           // nothing else was asked for
        Assert.False(fx.SkippedUnavailable);
        Assert.True(fx.Stop);
        Assert.Equal(Playback.StopReason.Failed, fx.StopWhy);
    }

    // ── dead rows: the deck never strands on a row the catalog cannot play ──────────────────────────────────────────

    /// <summary>Rule <paramref name="row"/> unavailable with no release instant — exactly <see cref="Track.Unplayable()"/>,
    /// the shape a 404 envelope commits for a track the catalog no longer resolves.</summary>
    static void Dead(EntityRef row)
    {
        TrackTable t = Entities.Current.Tracks;
        t.Known[row.Slot] |= (uint)TrackFields.Availability;
        t.Flags[row.Slot] |= (uint)TrackFlags.Unavailable;
        t.AvailableAt[row.Slot] = 0;
    }

    static Playback.Input Failed(in Playback.State s, Playback.Fault fault, long nowMs)
        => Playback.Input.Audio(Playback.AudioSignal.Failed, s.LoadEpoch, nowMs, (long)fault);

    [Fact]
    public void A_terminal_failure_on_a_natural_advance_skips_to_the_next_playable_row()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 180_000), ref fx);   // the deck moved on its own → row 1
        Assert.Equal(Playback.LoadOrigin.Advance, s.LoadWhy);
        EntityId dead = s.CurrentId;
        fx.Clear();

        Playback.Step(ref s, Failed(in s, Playback.Fault.Unavailable, 181_000), ref fx);

        // Not parked on "0:00": the next playable row is loading, in THIS drain, and the error is healed with it.
        Assert.Equal(2, s.Cursor.Index);
        Assert.Equal(Queue.RefAt(2), s.Current);
        Assert.Equal(Playback.Fault.None, s.Error);
        Assert.False(s.Parked);
        Assert.Equal(Playback.Phase.Loading, s.Phase);
        Assert.True(fx.Load);
        Assert.Equal(Queue.RefAt(2), fx.LoadRow);
        Assert.Equal(s.LoadEpoch, fx.LoadEpoch);
        Assert.Equal(Playback.LoadOrigin.Advance, fx.LoadWhy);          // a chain of dead rows keeps skipping
        Assert.False(fx.Stop);
        // …and the shell is told which row vanished from the listen.
        Assert.True(fx.SkippedUnavailable);
        Assert.Equal(dead, fx.SkippedId);
        Assert.Equal(1, s.AutoSkips);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.Started, s.LoadEpoch, 182_000), ref fx);
        Assert.Equal(0, s.AutoSkips);                                    // audio is out: the run is over
    }

    [Fact]
    public void A_session_level_fault_on_an_advance_still_parks()
    {
        // Network / RuntimeMissing / Unknown are not about the row: the next one would fail the same way.
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 180_000), ref fx);
        fx.Clear();

        Playback.Step(ref s, Failed(in s, Playback.Fault.Network, 181_000), ref fx);

        Assert.Equal(1, s.Cursor.Index);
        Assert.True(s.Parked);
        Assert.Equal(Playback.Fault.Network, s.Error);
        Assert.False(fx.Load);
        Assert.False(fx.SkippedUnavailable);
        Assert.True(fx.Stop);
        Assert.Equal(0, s.AutoSkips);
    }

    [Fact]
    public void Auto_skip_stops_after_three_consecutive_dead_rows()
    {
        var s = PlayingQueueOf(6);                                       // rows 0..6
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 180_000), ref fx);   // → row 1

        for (int n = 1; n <= Playback.AutoSkip.MaxConsecutive; n++)
        {
            fx.Clear();
            Playback.Step(ref s, Failed(in s, Playback.Fault.Unavailable, 180_000 + n), ref fx);
            Assert.Equal(1 + n, s.Cursor.Index);
            Assert.True(fx.Load);
            Assert.True(fx.SkippedUnavailable);
            Assert.Equal(n, s.AutoSkips);
        }

        // Row 4 is on the deck and three dead rows were stepped past with no audio between them: the fourth parks.
        fx.Clear();
        Playback.Step(ref s, Failed(in s, Playback.Fault.Unavailable, 180_010), ref fx);

        Assert.Equal(4, s.Cursor.Index);
        Assert.True(s.Parked);
        Assert.Equal(Playback.Phase.Paused, s.Phase);
        Assert.Equal(Playback.Fault.Unavailable, s.Error);
        Assert.False(fx.Load);
        Assert.False(fx.SkippedUnavailable);
        Assert.True(fx.Stop);
        Assert.Equal(Playback.StopReason.Failed, fx.StopWhy);
        Assert.True(s.NextAllowedByContext);                             // Next stays the way out of a parked deck
    }

    [Fact]
    public void Repeat_one_never_auto_skips()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 180_000), ref fx);   // → row 1, LoadOrigin.Advance
        Playback.Step(ref s, Playback.Input.Repeat(RepeatMode.Track), ref fx);
        fx.Clear();

        Playback.Step(ref s, Failed(in s, Playback.Fault.Unavailable, 181_000), ref fx);

        Assert.Equal(1, s.Cursor.Index);                                 // the only next row IS the dead one
        Assert.True(s.Parked);
        Assert.False(fx.Load);
    }

    [Fact]
    public void Advance_skips_unplayable_rows_in_both_directions()
    {
        var s = PlayingQueueOf(4);                                       // rows 0..4, the deck on 0
        Dead(Queue.RefAt(1));
        Dead(Queue.RefAt(2));
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Next(), ref fx);

        Assert.Equal(3, s.Cursor.Index);                                 // 1 and 2 are ruled dead: never landed on
        Assert.Equal(Queue.RefAt(3), s.Current);
        Assert.Equal(Queue.RefAt(3), fx.LoadRow);
        Assert.Equal(Playback.LoadOrigin.Advance, s.LoadWhy);
        Assert.True(fx.Prefetch);                                        // the next-row arm walks the same rule
        Assert.Equal(Queue.RefAt(4), fx.PrefetchRow);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Prev(nowMs: 1_000), ref fx); // inside the restart window: a real Previous

        Assert.Equal(0, s.Cursor.Index);                                 // back past both dead rows
        Assert.Equal(Queue.RefAt(0), fx.LoadRow);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 180_000), ref fx);   // a natural end, the same walk

        Assert.Equal(3, s.Cursor.Index);
        Assert.Equal(Queue.RefAt(3), fx.LoadRow);
    }

    [Fact]
    public void A_context_whose_remaining_rows_are_all_dead_has_run_out()
    {
        var s = PlayingQueueOf(2);
        Dead(Queue.RefAt(1));
        Dead(Queue.RefAt(2));
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 180_000), ref fx);

        Assert.Equal(Playback.Phase.Ended, s.Phase);                     // no context to page or autoplay: the end
        Assert.Equal(0, s.Cursor.Index);
        Assert.False(fx.Load);
        Assert.True(fx.Stop);
        Assert.Equal(Playback.StopReason.EndOfQueue, fx.StopWhy);
    }

    [Fact]
    public void A_play_from_a_dead_row_starts_at_the_next_playable_one()
    {
        var s = PlayingQueueOf(3);
        Dead(Queue.RefAt(1));
        EntityRef dead = Queue.RefAt(1);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Play(dead, dead.Id, default, Queue.CursorOf(1)), ref fx);

        Assert.Equal(2, s.Cursor.Index);
        Assert.Equal(Queue.RefAt(2), s.Current);
        Assert.Equal(Queue.RefAt(2).Id, s.CurrentId);
        Assert.Equal(Queue.RefAt(2), fx.LoadRow);
        Assert.Equal(Playback.LoadOrigin.Claim, fx.LoadWhy);
    }

    [Fact]
    public void A_retry_of_the_dead_row_on_the_deck_re_issues_that_row()
    {
        // The bar's Retry is a Play of the deck's own row, context and cursor: it must keep meaning "try THIS again".
        var s = PlayingQueueOf(3);
        Dead(Queue.RefAt(0));
        EntityRef deck = s.Current;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Play(deck, deck.Id, default, Queue.CursorOf(0)), ref fx);

        Assert.Equal(0, s.Cursor.Index);
        Assert.Equal(deck, s.Current);
        Assert.Equal(deck, fx.LoadRow);
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

    // ── R4-1: context paging (G-242) ────────────────────────────────────────────────────────────────────────────────

    static readonly EntityId BigPlaylist = EntityId.ForGid(EntityKind.Playlist, (UInt128)0xB16UL);

    /// <summary>The deck row on index 0 with <paramref name="upNext"/> rows after it — the queue after a page appended.</summary>
    static void LayPages(int upNext)
    {
        var refs = new EntityRef[upNext + 1];
        var rows = new QueueEdge[upNext + 1];
        refs[0] = Track(0);
        rows[0] = Row(QueueBucket.NowPlaying, 1);
        for (int i = 1; i <= upNext; i++) { refs[i] = Track(i); rows[i] = Row(QueueBucket.NextUp, (ulong)(i + 1)); }
        Queue.Replace(refs, rows);
    }

    [Fact]
    public void A_context_with_a_next_page_pages_before_it_asks_autoplay()
    {
        var s = PlayingQueueOf(0);
        s.Context = BigPlaylist;
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.ContextPages(EntityId.ForGid(EntityKind.Album, (UInt128)1UL), morePages: true), ref fx);
        Assert.False(s.MorePages);                                       // another context's page is not this one's
        Playback.Step(ref s, Playback.Input.ContextPages(BigPlaylist, morePages: true), ref fx);
        Assert.True(s.MorePages);

        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.EndingSoon, s.LoadEpoch, 170_000), ref fx);

        Assert.True(fx.Page);
        Assert.Equal(BigPlaylist, fx.PageContext);
        Assert.False(fx.Autoplay);
        Assert.Equal(Playback.AutoplayPhase.Requested, s.Autoplay);
    }

    [Fact]
    public void A_context_that_lands_complete_asks_autoplay_immediately_not_just_at_the_endgame()
    {
        var s = PlayingQueueOf(3);                                       // rows still ahead of the deck
        s.Context = BigPlaylist;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.ContextPages(BigPlaylist, morePages: false), ref fx);

        Assert.True(fx.Autoplay);
        Assert.Equal(BigPlaylist, fx.AutoplayContext);
        Assert.Equal(Playback.AutoplayPhase.Requested, s.Autoplay);

        // The endgame is the fallback, not a second ask: the load-landed request is already in flight.
        fx.Clear();
        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.EndingSoon, s.LoadEpoch, 170_000), ref fx);
        Assert.False(fx.Autoplay);
        Assert.Equal(Playback.AutoplayPhase.Requested, s.Autoplay);
    }

    [Fact]
    public void A_context_that_lands_with_a_next_page_does_not_ask_autoplay_yet()
    {
        var s = PlayingQueueOf(10);                                      // plenty ahead: nothing is asked yet either
        s.Context = BigPlaylist;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.ContextPages(BigPlaylist, morePages: true), ref fx);

        Assert.False(fx.Autoplay);
        Assert.False(fx.Page);
        Assert.Equal(Playback.AutoplayPhase.None, s.Autoplay);
        Assert.True(s.MorePages);
    }

    [Fact]
    public void Repeat_track_never_asks_autoplay_on_landing()
    {
        var s = PlayingQueueOf(0);
        s.Context = BigPlaylist;
        s.Repeat = RepeatMode.Track;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.ContextPages(BigPlaylist, morePages: false), ref fx);

        Assert.False(fx.Autoplay);
        Assert.Equal(Playback.AutoplayPhase.None, s.Autoplay);
    }

    [Fact]
    public void A_page_that_lands_while_the_deck_waits_advances_into_it_and_the_last_page_hands_over_to_autoplay()
    {
        var s = PlayingQueueOf(0);
        s.Context = BigPlaylist;
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.ContextPages(BigPlaylist, morePages: true), ref fx);
        Assert.True(fx.Page);                                            // one row ahead of nothing: the page is asked at once
        Assert.False(fx.Autoplay);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 180_000), ref fx);
        Assert.False(fx.Page);                                           // already out: the deck waits for it
        Assert.False(fx.Autoplay);
        Assert.False(fx.Load);
        Assert.True(fx.Stop);
        Assert.Equal(Playback.AutoplayPhase.Waiting, s.Autoplay);

        fx.Clear();
        LayPages(1);
        Playback.Step(ref s, Playback.Input.Paged(BigPlaylist, appended: 1, morePages: false), ref fx);
        Assert.True(fx.Load);
        Assert.Equal(1, s.Cursor.Index);
        Assert.False(s.MorePages);
        Assert.True(fx.Autoplay);                                        // the last page landed: autoplay's turn, now
        Assert.False(fx.Page);
        Assert.Equal(Playback.AutoplayPhase.Requested, s.Autoplay);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 400_000), ref fx);
        Assert.False(fx.Autoplay);                                       // not a second ask: the deck waits for the first
        Assert.False(fx.Page);
        Assert.Equal(Playback.AutoplayPhase.Waiting, s.Autoplay);
    }

    [Fact]
    public void A_station_keeps_paging_for_as_long_as_its_pages_name_a_next_one()
    {
        var s = PlayingQueueOf(0);
        EntityId station = EntityId.Parse("spotify:station:track:7idegBIikag5rTZP4WZihP".AsSpan());
        s.Context = station;
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.ContextPages(station, morePages: true), ref fx);
        Assert.True(fx.Page);                                            // asked as soon as the page is known: one row is nearly consumed
        Assert.False(fx.Autoplay);

        for (int page = 1; page <= 3; page++)
        {
            fx.Clear();
            Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: page * 200_000L), ref fx);
            Assert.False(fx.Autoplay);                                   // never autoplay, which refuses a station
            Assert.True(fx.Stop);                                        // the page is out: the deck waits for it
            Assert.Equal(Playback.AutoplayPhase.Waiting, s.Autoplay);

            fx.Clear();
            LayPages(page);
            Playback.Step(ref s, Playback.Input.Paged(station, appended: 1, morePages: true), ref fx);
            Assert.True(s.MorePages);
            Assert.True(fx.Load);
            Assert.Equal(page, s.Cursor.Index);
            Assert.True(fx.Page);                                        // the row it advanced into is the last: the next page, at once
            Assert.False(fx.Autoplay);
        }
    }

    [Fact]
    public void Repeat_context_pages_a_context_before_it_wraps_to_its_head()
    {
        var s = PlayingQueueOf(1);
        s.Repeat = RepeatMode.Context;
        s.Context = BigPlaylist;
        s.MorePages = true;
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Next(), ref fx);             // to the last row of the first page
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Next(), ref fx);             // off its end
        Assert.True(fx.Page);
        Assert.False(fx.Load);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Paged(BigPlaylist, appended: 0, morePages: false), ref fx);
        Assert.True(fx.Load);                                            // no page after all: now it wraps
        Assert.Equal(0, s.Cursor.Index);
    }

    // ── the refill rule: asks ahead of the run-out (Playback.Refill.cs) ────────────────────────────────────────────

    /// <summary>The deck row on index 0 followed by <paramref name="autoplay"/> autoplay rows.</summary>
    static void LayAutoplayRows(int autoplay)
    {
        var refs = new EntityRef[autoplay + 1];
        var rows = new QueueEdge[autoplay + 1];
        refs[0] = Track(0);
        rows[0] = Row(QueueBucket.NowPlaying, 1);
        for (int i = 1; i <= autoplay; i++) { refs[i] = Track(100 + i); rows[i] = Row(QueueBucket.NextUp, (ulong)(i + 1), QueueProvider.Autoplay); }
        Queue.Replace(refs, rows);
    }

    /// <summary>A complete context whose autoplay answer brought eight rows and a page, played down to three ahead: the
    /// point the autoplay page is asked for. Leaves <paramref name="fx"/> holding that ask.</summary>
    static Playback.State ConsumedAutoplayRun(ref Playback.Effects fx)
    {
        var s = PlayingQueueOf(0);
        s.Context = BigPlaylist;
        Playback.Step(ref s, Playback.Input.ContextPages(BigPlaylist, morePages: false), ref fx);
        Assert.True(fx.Autoplay);
        fx.Clear();
        LayAutoplayRows(8);
        Playback.Step(ref s, Playback.Input.Autoplayed(BigPlaylist, appended: 8, morePages: true), ref fx);
        Assert.Equal(Playback.AutoplayPhase.None, s.Autoplay);
        Assert.True(s.AutoplayPages);
        Assert.False(fx.AutoplayPage);
        for (int k = 1; k <= 4; k++)
        {
            fx.Clear();
            Playback.Step(ref s, Playback.Input.Next(nowMs: k * 1_000), ref fx);
            Assert.False(fx.Autoplay);
            Assert.False(fx.AutoplayPage);                               // four or more ahead: not yet
        }
        fx.Clear();
        Playback.Step(ref s, Playback.Input.Next(nowMs: 5_000), ref fx);
        Assert.Equal(5, s.Cursor.Index);
        return s;
    }

    [Fact]
    public void A_restored_deck_asks_autoplay_as_soon_as_its_context_is_known_complete_before_any_play()
    {
        TestScope.Fresh();
        Queue.Replace([Track(0)], [Row(QueueBucket.NowPlaying, 1)]);
        var s = Playback.State.Initial;
        s.Us = Playback.DeviceHash("wavee-device");
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Restore(Track(0), Track(0).Id, BigPlaylist, Queue.CursorOf(0), 42_000, 200_000, nowMs: 10), ref fx);
        Assert.False(fx.Autoplay);                                       // the host has not said whether the context pages
        Assert.True(s.Parked);

        Playback.Step(ref s, Playback.Input.ContextPages(BigPlaylist, morePages: false, nowMs: 10), ref fx);
        Assert.True(fx.Autoplay);
        Assert.Equal(BigPlaylist, fx.AutoplayContext);
        Assert.Equal(Playback.AutoplayPhase.Requested, s.Autoplay);
        Assert.False(fx.Load);                                           // still parked: nothing played
        Assert.True(s.Parked);
    }

    [Fact]
    public void An_offline_autoplay_answer_defers_the_ask_and_the_session_coming_online_asks_once()
    {
        var s = PlayingQueueOf(0);
        s.Context = BigPlaylist;
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.ContextPages(BigPlaylist, morePages: false), ref fx);
        Assert.True(fx.Autoplay);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Autoplayed(BigPlaylist, appended: 0, morePages: false, retry: true), ref fx);
        Assert.Equal(Playback.AutoplayPhase.Deferred, s.Autoplay);
        Assert.False(fx.Autoplay);
        Assert.False(fx.Stop);                                           // the deck was not waiting: it plays on

        Playback.Step(ref s, Playback.Input.SessionOnline(), ref fx);
        Assert.True(fx.Autoplay);
        Assert.Equal(BigPlaylist, fx.AutoplayContext);
        Assert.Equal(Playback.AutoplayPhase.Requested, s.Autoplay);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.SessionOnline(), ref fx);
        Assert.False(fx.Any);                                            // the ask is out: a second edge changes nothing
    }

    [Fact]
    public void A_consumed_autoplay_run_asks_for_its_next_page_not_for_autoplay_again()
    {
        var fx = new Playback.Effects();
        var s = ConsumedAutoplayRun(ref fx);

        Assert.True(fx.AutoplayPage);
        Assert.Equal(BigPlaylist, fx.AutoplayPageContext);
        Assert.False(fx.Autoplay);
        Assert.Equal(Playback.AutoplayPhase.Requested, s.Autoplay);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Next(nowMs: 6_000), ref fx);
        Assert.False(fx.AutoplayPage);                                   // one ask at a time
        Assert.False(fx.Autoplay);
    }

    [Fact]
    public void An_empty_autoplay_page_re_asks_autoplay_once_and_a_decline_then_exhausts_it()
    {
        var fx = new Playback.Effects();
        var s = ConsumedAutoplayRun(ref fx);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.AutoplayPaged(BigPlaylist, appended: 0, morePages: false), ref fx);
        Assert.False(s.AutoplayPages);
        Assert.True(fx.Autoplay);                                        // the page brought nothing: a fresh ask
        Assert.False(fx.AutoplayPage);
        Assert.Equal(Playback.AutoplayPhase.Requested, s.Autoplay);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Autoplayed(BigPlaylist, appended: 0), ref fx);
        Assert.Equal(Playback.AutoplayPhase.Exhausted, s.Autoplay);
        Assert.False(fx.Autoplay);
        Assert.False(fx.Stop);                                           // rows are still ahead: the deck plays on
    }

    [Fact]
    public void A_waiting_deck_advances_into_an_autoplay_page()
    {
        var s = PlayingQueueOf(0);
        s.Context = BigPlaylist;
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.ContextPages(BigPlaylist, morePages: false), ref fx);
        fx.Clear();
        LayAutoplayRows(1);
        Playback.Step(ref s, Playback.Input.Autoplayed(BigPlaylist, appended: 1, morePages: true), ref fx);
        Assert.True(fx.AutoplayPage);                                    // one autoplay row ahead: its page, at once
        Assert.Equal(Playback.AutoplayPhase.Requested, s.Autoplay);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 180_000), ref fx);
        Assert.True(fx.Load);
        Assert.Equal(1, s.Cursor.Index);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 360_000), ref fx);
        Assert.False(fx.Load);
        Assert.True(fx.Stop);
        Assert.Equal(Playback.AutoplayPhase.Waiting, s.Autoplay);

        fx.Clear();
        LayAutoplayRows(2);
        Playback.Step(ref s, Playback.Input.AutoplayPaged(BigPlaylist, appended: 1, morePages: false), ref fx);
        Assert.True(fx.Load);
        Assert.Equal(2, s.Cursor.Index);
        Assert.Equal(Queue.RefAt(2), fx.LoadRow);
        Assert.True(fx.Autoplay);                                        // the page was the last and is consumed: a fresh ask
    }

    [Fact]
    public void Context_paging_is_asked_at_three_quarters_of_the_laid_page_and_the_endgame_does_not_ask_again()
    {
        var s = PlayingQueueOf(99);                                      // a hundred-row page
        s.Context = BigPlaylist;
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.ContextPages(BigPlaylist, morePages: true), ref fx);
        Assert.False(fx.Page);
        Assert.Equal(Playback.AutoplayPhase.None, s.Autoplay);

        for (int k = 1; k <= 74; k++)
        {
            fx.Clear();
            Playback.Step(ref s, Playback.Input.Next(nowMs: k * 1_000), ref fx);
            Assert.False(fx.Page);
            Assert.False(fx.Autoplay);
        }
        fx.Clear();
        Playback.Step(ref s, Playback.Input.Next(nowMs: 75_000), ref fx);
        Assert.Equal(75, s.Cursor.Index);
        Assert.True(fx.Page);
        Assert.Equal(BigPlaylist, fx.PageContext);
        Assert.False(fx.Autoplay);
        Assert.Equal(Playback.AutoplayPhase.Requested, s.Autoplay);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Audio(Playback.AudioSignal.EndingSoon, s.LoadEpoch, 170_000), ref fx);
        Assert.False(fx.Page);
        Assert.False(fx.Autoplay);
        Assert.Equal(Playback.AutoplayPhase.Requested, s.Autoplay);
    }

    // ── R4-1: attribution ages (G-246) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_local_next_eleven_seconds_after_a_remote_pause_is_attributed_to_no_command()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();
        var identity = new Playback.DeviceIdentity("wavee-device", "Wavee", "cid", "Win32", "1.2.3", "3.2.6");
        ulong phone = Playback.DeviceHash("phone");
        var pause = new Spotify.Decode.RemoteCommand(Spotify.Decode.RemoteCmd.Pause, Ok: true, MessageId: 44, SeekToMs: 0,
            BoolArg: false, Track: default, SenderHash: phone, SessionHash: 0, DedupeKey: 1);

        Playback.Step(ref s, Playback.Input.Controller(in pause, nowMs: 1_000), ref fx);
        Assert.Equal(phone, s.LastCommandSender);
        Assert.Equal(44u, Playback.Snapshot.Of(in s, in identity, Playback.PublishReason.PlayerStateChanged, 1, 1, 1_050).LastCommandMessageId);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Next(nowMs: 12_000), ref fx);

        Assert.True(fx.PublishState);
        Assert.Equal(0u, Playback.Snapshot.Of(in s, in identity, Playback.PublishReason.PlayerStateChanged, 1, 1, 12_000).LastCommandMessageId);
    }

    [Fact]
    public void Playing_a_row_while_a_phone_owns_playback_forwards_a_play_naming_the_context_and_the_row()
    {
        var s = PlayingQueueOf(3);
        ulong phone = Playback.DeviceHash("phone");
        var fx = new Playback.Effects();
        var frame = new Playback.ClusterFrame(Spotify.Decode.ClusterOrigin.Push, 0, phone, 1_000);
        Playback.Step(ref s, Playback.Input.Cluster(in frame, default, nowMs: 0), ref fx);
        Assert.Equal(Playback.Owner.Foreign, s.Owner);
        fx.Clear();

        EntityRef row = Queue.RefAt(2);
        EntityId context = EntityId.ForGid(EntityKind.Playlist, (UInt128)77UL);
        var before = s.Current;
        Playback.Step(ref s, Playback.Input.Play(row, row.Id, context, Queue.CursorOf(2), nowMs: 4_000), ref fx);

        // Nothing loads here: the click is a command to the owner, and it names WHAT to play, not a bare resume.
        Assert.False(fx.Load);
        Assert.True(fx.SendRemote);
        Assert.Equal(Spotify.Decode.RemoteCmd.PlayContext, fx.RemoteCmd);
        Assert.Equal(phone, fx.RemoteDevice);
        Assert.Equal(context, fx.RemoteContext);
        Assert.Equal(row.Id, fx.RemoteTrack);
        Assert.Equal(before, s.Current);
        Assert.Equal(Playback.Owner.Foreign, s.Owner);
        fx.Clear();

        // A row played on its own (no context) is its own context, as the desktop plays a single track.
        Playback.Step(ref s, Playback.Input.Play(row, row.Id, default, Queue.CursorOf(2), nowMs: 5_000), ref fx);
        Assert.Equal(Spotify.Decode.RemoteCmd.PlayContext, fx.RemoteCmd);
        Assert.Equal(row.Id, fx.RemoteContext);
    }

    // ── R4-1: queue writes while another device owns playback (G-248) ───────────────────────────────────────────────

    [Fact]
    public void Queueing_while_a_phone_owns_playback_forwards_add_to_queue_or_set_queue_to_it()
    {
        var s = PlayingQueueOf(3);
        ulong phone = Playback.DeviceHash("phone");
        var fx = new Playback.Effects();
        var frame = new Playback.ClusterFrame(Spotify.Decode.ClusterOrigin.Push, 0, phone, 1_000);
        Playback.Step(ref s, Playback.Input.Cluster(in frame, default, nowMs: 0), ref fx);
        Assert.Equal(Playback.Owner.Foreign, s.Owner);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.QueueToOwner(1, next: false, offset: 4), ref fx);
        Assert.True(fx.SendRemote);
        Assert.Equal(phone, fx.RemoteDevice);
        Assert.Equal(Spotify.Decode.RemoteCmd.AddToQueue, fx.RemoteCmd);
        Assert.Equal((4L << 32) | 1L, fx.RemoteArg);                     // the staged run: offset high, count low
        Assert.False(fx.RemoteFlag);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.QueueToOwner(3, next: false), ref fx);
        Assert.Equal(Spotify.Decode.RemoteCmd.SetQueue, fx.RemoteCmd);   // several rows are one set_queue
        Assert.Equal(3L, fx.RemoteArg);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.QueueToOwner(1, next: true), ref fx);
        Assert.Equal(Spotify.Decode.RemoteCmd.SetQueue, fx.RemoteCmd);   // "play next" lands at the head of its queue
        Assert.True(fx.RemoteFlag);
    }

    [Fact]
    public void Queueing_while_the_deck_is_ours_forwards_nothing()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.QueueToOwner(2, next: false), ref fx);

        Assert.False(fx.SendRemote);
    }

    // ── R4-1: the controllers hear about the queue (G-240, G-245) ───────────────────────────────────────────────────

    [Fact]
    public void A_queue_change_under_our_deck_is_announced_and_one_under_nobodys_is_not()
    {
        var s = PlayingQueueOf(3);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.QueueChanged(), ref fx);
        Assert.True(fx.PublishState);

        fx.Clear();
        s.Own = Playback.OwnerState.Initial;
        Playback.Step(ref s, Playback.Input.QueueChanged(), ref fx);
        Assert.False(fx.PublishState);
    }

    [Fact]
    public void A_controllers_update_context_claims_and_is_announced()
    {
        var s = PlayingQueueOf(3);
        s.Own = Playback.OwnerState.Initial;
        var fx = new Playback.Effects();
        var cmd = new Spotify.Decode.RemoteCommand(Spotify.Decode.RemoteCmd.UpdateContext, Ok: true, MessageId: 5, SeekToMs: 0,
            BoolArg: false, Track: default, SenderHash: 9, SessionHash: 0, DedupeKey: 1);

        Playback.Step(ref s, Playback.Input.Controller(in cmd, nowMs: 1_000), ref fx);

        Assert.Equal(Playback.Owner.Us, s.Owner);
        Assert.True(fx.PublishState);
        Assert.Equal(5u, s.LastCommandMessageId);
    }

    [Fact]
    public void A_booked_uid_reads_back_as_its_text_and_a_packed_one_formats_itself()
    {
        var book = new Playback.UidBook();
        ulong booked = book.ItemIdOf("5bd5aabfe4434940c96f"u8);
        ulong packed = book.ItemIdOf("0ab15c9f39e1de3b"u8);

        Assert.Equal("5bd5aabfe4434940c96f", book.TextOf(booked));
        Assert.Null(book.TextOf(packed));
        Assert.Null(book.TextOf(0));
    }

    // ── R4-1: the deck across the welcome's scope switch (G-241) ────────────────────────────────────────────────────

    [Fact]
    public void A_restored_deck_is_carried_across_a_scope_switch_by_identity()
    {
        TestScope.Fresh();
        Spotify.Connect.Clear();
        Playback.ToUi = static a => a();
        Playback.ResetForTests();
        try
        {
            EntityRef t0 = Track(10), t1 = Track(11), t2 = Track(12);
            EntityId id1 = t1.Id;
            Queue.Replace([t0, t1, t2], [Row(QueueBucket.History, 1), Row(QueueBucket.NowPlaying, 2), Row(QueueBucket.NextUp, 3)]);
            var point = new Playback.RestorePoint(id1, EntityId.ForGid(EntityKind.Album, (UInt128)0xA11UL), 1, 30_000, 200_000,
                false, RepeatMode.Off);
            Playback.Restore(in point);
            Assert.Equal(id1, Playback.Snap().CurrentId);

            Entities.Switch(CatalogScope.Fake(locale: "pt-PT", market: "PT"));
            Assert.Equal(0, Queue.Count);                                // the new scope's queue is empty…
            Playback.Rebind();

            Playback.State s = Playback.Snap();
            Assert.Equal(id1, s.CurrentId);
            Assert.Equal(id1, s.Current.Id);                             // …and the deck row is a slot of the NEW scope's table
            Assert.Equal(3, Queue.Count);
            Assert.Equal(1, s.Cursor.Index);
            Assert.Equal(s.Current, Queue.RefAt(s.Cursor.Index));
            Assert.Equal(3UL, Queue.Rows[2].ItemId);                     // the rows keep their item ids
            Assert.Equal(30_000, s.PosMs);
            Assert.True(s.Parked);
        }
        finally { Playback.ResetForTests(); }
    }

    [Fact]
    public void A_fresh_graph_is_not_a_switch_and_inherits_no_queue()
    {
        TestScope.Fresh();
        Playback.ToUi = static a => a();
        Playback.ResetForTests();
        try
        {
            Queue.Replace([Track(20), Track(21)], [Row(QueueBucket.NowPlaying, 1), Row(QueueBucket.NextUp, 2)]);
            TestScope.Fresh();                                          // a Boot: its scope epoch starts over

            Playback.Rebind();

            Assert.Equal(0, Queue.Count);
        }
        finally { Playback.ResetForTests(); }
    }

    // ── fix 5: a deck slot that no longer names its identity (player bar "Loading…") ───────────────────────────────

    [Fact]
    public void RowNamesId_is_false_for_none_a_slot_past_the_table_and_another_row()
    {
        PlayingQueueOf(1);                                              // stands up the scope and its track table

        Assert.True(Playback.RowNamesId(Track(0), Track(0).Id));
        Assert.False(Playback.RowNamesId(default, Track(0).Id));
        Assert.False(Playback.RowNamesId(new EntityRef(EntityKind.Track, 4_000_000), Track(0).Id));
        Assert.False(Playback.RowNamesId(Track(1), Track(0).Id));
    }

    [Fact]
    public void Resuming_a_parked_deck_whose_row_no_longer_names_its_identity_asks_the_host_to_fetch_it()
    {
        // A retired scope's slot: the identity survives (CurrentId), the row does not — the row that used to be at
        // this slot in the OLD scope's table is not the row at this slot in the current one. Before the fix, only
        // `Current.IsNone` asked for a fetch, so a non-none-but-wrong row silently played by id while the bar painted
        // whatever this slot happens to name now (or nothing at all): "Loading…", forever.
        var s = PlayingQueueOf(1);
        s.Phase = Playback.Phase.Paused;
        s.Parked = true;
        s.Current = new EntityRef(EntityKind.Track, 4_000_000);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Resume(), ref fx);

        Assert.True(fx.Load);
        Assert.True(fx.Fetch);
        Assert.Equal(s.CurrentId, fx.FetchId);
    }

    [Fact]
    public void A_play_whose_row_names_another_identity_asks_for_a_fetch()
    {
        var s = PlayingQueueOf(3);
        s.Own = Playback.OwnerState.Initial;
        var fx = new Playback.Effects();
        EntityId context = EntityId.ForGid(EntityKind.Playlist, (UInt128)77UL);

        // The row and the identity disagree — a stale slot handed in alongside a fresh id (a foreign device's uri
        // resolved against the wrong scope's table). `RowNamesId` catches it even though the row is not none.
        Playback.Step(ref s, Playback.Input.Play(Track(1), Track(0).Id, context, Queue.CursorOf(1), nowMs: 4_000), ref fx);

        Assert.True(fx.Fetch);
        Assert.Equal(Track(0).Id, fx.FetchId);
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

    // ── the context switched under the deck: a radio parked behind the current track (G-251) ────────────────────────

    static readonly EntityId RadioPlaylist = EntityId.ForGid(EntityKind.Playlist, (UInt128)0x5AD10UL);

    [Fact]
    public void Switching_the_context_under_the_deck_changes_what_it_plays_from_without_touching_the_row_or_its_audio()
    {
        var s = PlayingQueueOf(3);
        s.Context = BigPlaylist;
        s.MorePages = true;
        s.Autoplay = Playback.AutoplayPhase.Requested;
        EntityRef current = s.Current;
        uint loadEpoch = s.LoadEpoch;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.SwitchContext(RadioPlaylist, Queue.CursorOf(0), nowMs: 5_000), ref fx);

        Assert.Equal(RadioPlaylist, s.Context);
        Assert.Equal(current, s.Current);                                 // the same row…
        Assert.Equal(0, s.Cursor.Index);                                  // …at the same place…
        Assert.Equal(Playback.Phase.Playing, s.Phase);                    // …still playing…
        Assert.Equal(loadEpoch, s.LoadEpoch);                             // …on the same load: the pump is not touched
        Assert.False(fx.Load);
        Assert.False(fx.Start || fx.Stop || fx.Seek || fx.PauseHost || fx.ResumeHost);
        Assert.False(s.MorePages);                                        // the old context's paging is forgotten
        Assert.Equal(Playback.AutoplayPhase.None, s.Autoplay);           // …and its autoplay phase starts over
        Assert.True(fx.Snapshot);                                         // the restore point names the radio now
        Assert.True(fx.Prefetch);                                         // the next row is re-armed off the rewritten queue
        Assert.Equal(Track(1).Id, fx.PrefetchId);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 180_000), ref fx);

        Assert.True(fx.Load);                                             // the natural end flows into the row behind it
        Assert.Equal(1, s.Cursor.Index);
        Assert.Equal(Track(1), s.Current);
        Assert.Equal(RadioPlaylist, s.Context);                           // …and that row plays FROM the radio
    }

    [Fact]
    public void A_deck_waiting_at_the_end_of_its_context_flows_into_the_parked_radio_at_once()
    {
        // EndOfContext left the deck Loading and Waiting for the OLD context's autoplay; that answer will name a context no
        // longer on the deck, so the switch itself advances into the radio rather than stranding the deck.
        var s = PlayingQueueOf(2);
        s.Context = BigPlaylist;
        s.Autoplay = Playback.AutoplayPhase.Waiting;
        s.Phase = Playback.Phase.Loading;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.SwitchContext(RadioPlaylist, Queue.CursorOf(0), nowMs: 5_000), ref fx);

        Assert.True(fx.Load);
        Assert.Equal(1, s.Cursor.Index);
        Assert.Equal(RadioPlaylist, s.Context);
        Assert.Equal(Playback.AutoplayPhase.None, s.Autoplay);
    }

    [Fact]
    public void A_context_switch_is_refused_on_an_idle_deck_and_a_stale_cursor_keeps_the_old_one()
    {
        TestScope.Fresh();
        var idle = Playback.State.Initial;
        idle.Us = Playback.DeviceHash("wavee-device");
        var fx = new Playback.Effects();
        Playback.Step(ref idle, Playback.Input.SwitchContext(RadioPlaylist, QueueCursor.None), ref fx);
        Assert.True(idle.Context.IsEmpty);
        Assert.False(fx.Any);

        var s = PlayingQueueOf(3);
        fx.Clear();
        Playback.Step(ref s, Playback.Input.SwitchContext(RadioPlaylist, Queue.CursorOf(2), nowMs: 5_000), ref fx);   // row 2 is not the deck
        Assert.Equal(RadioPlaylist, s.Context);
        Assert.Equal(0, s.Cursor.Index);
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
