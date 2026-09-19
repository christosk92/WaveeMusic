// ── Wavee.Tests/PlaybackTransitionTests.cs — the reducer's transition arms (gap batch B3) ──────────────────────────
//
// `Playback.Transitions.cs`'s contract, the same way `PlaybackStepTests` pins `Playback.cs`: POST THESE INPUTS, ASSERT
// STATE AND EFFECTS. One region per gap: the gapless hand-off (G-100, D4), the next-row arm (G-112), the device reload
// (G-108), the video host switch and its recovery (G-140/141/142/143), remote volume (G-073), suspend and wake (G-081/082,
// D13), the launch restore (G-078), sign-out (G-036), the play report (G-076), autoplay and shuffle (G-080), a
// controller's queue write (G-074) and the inbound load (G-071) — and then the pure planners the host applies.
//
// The queue is the live one, so every fact boots a fake scope and joins the entities collection.

using Wavee;
using Xunit;

using RemoteCmd = Wavee.Spotify.Decode.RemoteCmd;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PlaybackTransitionTests
{
    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly EntityId Album = EntityId.ForGid(EntityKind.Album, (UInt128)0xA1B2UL);
    static readonly ulong Phone = Playback.DeviceHash("phone");

    static QueueEdge Row(QueueBucket bucket, ulong id, QueueProvider provider = QueueProvider.Context)
        => new(id, (byte)provider, (byte)bucket);

    static EntityRef Track(int n)
    {
        var id = EntityId.ForGid(EntityKind.Track, (UInt128)(ulong)(0x9000 + n));
        return new EntityRef(EntityKind.Track, Entities.Current.Tracks.Slot(id));
    }

    /// <summary>Row 0 on the deck, playing, with <paramref name="upNext"/> context rows after it.</summary>
    static Playback.State Playing(int upNext, bool withContext = false)
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
        if (withContext) s.Context = Album;
        Playback.Ownership.Claim(ref s.Own, Playback.ClaimCause.UserPlay, 1, 0, 0, acknowledged: false);
        return s;
    }

    static void TakenByPhone(ref Playback.State s, ref Playback.Effects fx, Playback.RemoteState remote = default)
    {
        var frame = new Playback.ClusterFrame(Spotify.Decode.ClusterOrigin.Push, 0, Phone, 1_000);
        Playback.Step(ref s, Playback.Input.Cluster(in frame, in remote, nowMs: 0), ref fx);
        Assert.Equal(Playback.Owner.Foreign, s.Owner);
        fx.Clear();
    }

    static void GiveVideo(EntityRef row) => Entities.Current.Tracks.Flags[row.Slot] |= (uint)TrackFlags.HasVideo;

    static Playback.Input Signal(Playback.AudioSignal signal, uint epoch, long nowMs = 0, long arg = 0)
        => Playback.Input.Audio(signal, epoch, nowMs, arg);

    // ── G-100: the gapless hand-off ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_hand_off_advances_the_cursor_and_emits_no_load()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        uint from = s.LoadEpoch;
        EntityRef next = Queue.RefAt(1);

        Playback.Step(ref s, Playback.Input.HandedOff(from, next.Id, 120, nowMs: 5_000), ref fx);

        Assert.Equal(1, s.Cursor.Index);
        Assert.Equal(next, s.Current);
        Assert.Equal(Playback.Phase.Playing, s.Phase);                  // audible already: not a load
        Assert.Equal(120, s.PosMs);
        Assert.False(fx.Load);
        Assert.True(fx.Adopt);
        Assert.Equal(from, fx.AdoptFrom);
        Assert.Equal(s.LoadEpoch, fx.AdoptTo);
        Assert.NotEqual(from, s.LoadEpoch);
        Assert.True(fx.PublishState);
        Assert.True(fx.Prefetch);                                        // the row after the joined one is warmed
        Assert.Equal(Queue.RefAt(2), fx.PrefetchRow);
        Assert.True(fx.Prefetch2);                                       // and the one after that
        Assert.Equal(Queue.RefAt(3), fx.Prefetch2Row);
    }

    [Fact]
    public void After_a_hand_off_the_old_epoch_is_stale_and_the_adopted_one_is_taken()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        uint from = s.LoadEpoch;
        Playback.Step(ref s, Playback.Input.HandedOff(from, Queue.RefAt(1).Id, 0, nowMs: 5_000), ref fx);
        uint adopted = fx.AdoptTo;

        Playback.Step(ref s, Signal(Playback.AudioSignal.Position, from, 6_000, 99_000), ref fx);
        Assert.Equal(0, s.PosMs);

        Playback.Step(ref s, Signal(Playback.AudioSignal.Position, adopted, 6_000, 1_000), ref fx);
        Assert.Equal(1_000, s.PosMs);
    }

    [Fact]
    public void The_joined_rows_own_end_advances_once_and_never_replays_it()
    {
        // The defect: B played, B ended, and `Ended → Advance` loaded B AGAIN.
        var s = Playing(3);
        var fx = new Playback.Effects();
        uint from = s.LoadEpoch;
        Playback.Step(ref s, Playback.Input.HandedOff(from, Queue.RefAt(1).Id, 0, nowMs: 5_000), ref fx);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Ended(from, nowMs: 9_000), ref fx);       // A's late end: stale
        Assert.False(fx.Load);
        Assert.Equal(1, s.Cursor.Index);

        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 200_000), ref fx);
        Assert.True(fx.Load);
        Assert.Equal(2, s.Cursor.Index);
        Assert.Equal(Queue.RefAt(2), fx.LoadRow);
    }

    [Fact]
    public void A_hand_off_naming_a_row_the_queue_no_longer_puts_next_hard_cuts_to_the_truth()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.HandedOff(s.LoadEpoch, Track(77).Id, 0, nowMs: 5_000), ref fx);

        Assert.True(fx.Load);
        Assert.False(fx.Adopt);
        Assert.Equal(Queue.RefAt(1), fx.LoadRow);
        Assert.Equal(Playback.LoadOrigin.Advance, fx.LoadWhy);
    }

    [Fact]
    public void A_stale_hand_off_changes_nothing()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Next(), ref fx);
        fx.Clear();
        int cursor = s.Cursor.Index;

        Playback.Step(ref s, Playback.Input.HandedOff(s.LoadEpoch - 1, Queue.RefAt(2).Id, 0), ref fx);

        Assert.Equal(cursor, s.Cursor.Index);
        Assert.False(fx.Adopt);
    }

    // ── G-112: prefetch at load, prepare in the endgame, re-arm on change ───────────────────────────────────────────

    [Fact]
    public void A_load_warms_the_next_row_and_prepares_nothing()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Next(), ref fx);

        Assert.True(fx.Prefetch);
        Assert.Equal(Queue.RefAt(2), fx.PrefetchRow);
        Assert.False(fx.PrepareNext);
        Assert.False(s.NextArmed);
    }

    [Fact]
    public void A_load_warms_two_rows_and_the_endgame_prepares_only_the_first()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Next(), ref fx);
        Assert.True(fx.Prefetch);
        Assert.Equal(Queue.RefAt(2), fx.PrefetchRow);
        Assert.True(fx.Prefetch2);
        Assert.Equal(Queue.RefAt(3), fx.Prefetch2Row);
        Assert.Equal(Queue.RefAt(3).Id, s.Next2Id);
        Assert.False(fx.PrepareNext);

        fx.Clear();
        Playback.Step(ref s, Signal(Playback.AudioSignal.EndingSoon, s.LoadEpoch, 170_000), ref fx);
        Assert.True(fx.PrepareNext);
        Assert.Equal(Queue.RefAt(2), fx.NextRow);
        Assert.False(fx.Prefetch2);                                      // the second row is already warm
        Assert.False(fx.Prefetch);
    }

    [Fact]
    public void A_parked_restored_deck_warms_the_next_row_but_prepares_nothing()
    {
        TestScope.Fresh();
        Queue.Replace([Track(3), Track(4)], [Row(QueueBucket.NowPlaying, 1), Row(QueueBucket.NextUp, 2)]);
        var s = Playback.State.Initial;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Restore(Track(3), Track(3).Id, Album, Queue.CursorOf(0), 42_000, 200_000, nowMs: 10), ref fx);

        Assert.True(s.Parked);
        Assert.True(fx.Prefetch);
        Assert.Equal(Track(4), fx.PrefetchRow);
        Assert.False(fx.Prefetch2);                                      // nothing after it
        Assert.False(fx.PrepareNext);
        Assert.False(s.NextArmed);

        fx.Clear();
        Playback.Step(ref s, Signal(Playback.AudioSignal.EndingSoon, s.LoadEpoch, 170_000), ref fx);
        Assert.False(fx.PrepareNext);                                    // parked: there is no voice to join
    }

    [Fact]
    public void The_session_coming_online_re_issues_a_dropped_prefetch()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Next(), ref fx);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.SessionOnline(), ref fx);

        Assert.True(fx.Prefetch);
        Assert.Equal(Queue.RefAt(2), fx.PrefetchRow);
        Assert.True(fx.Prefetch2);
        Assert.Equal(Queue.RefAt(3), fx.Prefetch2Row);
        Assert.False(fx.PrepareNext);
        Assert.False(fx.Load);
    }

    [Fact]
    public void A_one_row_queue_arms_nothing_on_a_second_step()
    {
        var s = Playing(0);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.QueueChanged(), ref fx);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.QueueChanged(), ref fx);

        Assert.False(fx.Prefetch);
        Assert.False(fx.Prefetch2);
        Assert.False(fx.PrepareNext);
        Assert.False(fx.CancelPrepared);
        Assert.True(s.NextId.IsEmpty);
        Assert.True(s.Next2Id.IsEmpty);
    }

    [Fact]
    public void The_endgame_prepares_the_next_row_once()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Signal(Playback.AudioSignal.EndingSoon, s.LoadEpoch, 170_000), ref fx);
        Assert.True(fx.PrepareNext);
        Assert.Equal(Queue.RefAt(1), fx.NextRow);
        Assert.True(s.NextArmed);

        fx.Clear();
        Playback.Step(ref s, Signal(Playback.AudioSignal.EndingSoon, s.LoadEpoch, 171_000), ref fx);
        Assert.False(fx.Any);
    }

    [Fact]
    public void A_queue_change_prepares_the_new_next_row()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Signal(Playback.AudioSignal.EndingSoon, s.LoadEpoch, 170_000), ref fx);
        fx.Clear();

        EntityRef inserted = Track(40);
        Queue.Replace([Track(0), inserted, Track(1)],
            [Row(QueueBucket.NowPlaying, 1), Row(QueueBucket.UserQueue, 9, QueueProvider.Queue), Row(QueueBucket.NextUp, 2)]);
        Playback.Step(ref s, Playback.Input.QueueChanged(), ref fx);

        Assert.True(fx.PrepareNext);
        Assert.Equal(inserted, fx.NextRow);
        Assert.False(fx.CancelPrepared);                                 // the pump replaces the slot itself
    }

    [Fact]
    public void A_next_row_that_disappears_cancels_the_prepared_slot()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Signal(Playback.AudioSignal.EndingSoon, s.LoadEpoch, 170_000), ref fx);
        fx.Clear();

        Queue.Replace([Track(0)], [Row(QueueBucket.NowPlaying, 1)]);
        Playback.Step(ref s, Playback.Input.QueueChanged(), ref fx);

        Assert.True(fx.CancelPrepared);
        Assert.False(fx.PrepareNext);
        Assert.True(s.NextId.IsEmpty);
    }

    [Fact]
    public void Repeat_track_cancels_the_prepared_row_and_its_natural_end_reloads_the_same_row()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Signal(Playback.AudioSignal.EndingSoon, s.LoadEpoch, 170_000), ref fx);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Repeat(RepeatMode.Track), ref fx);
        Assert.True(fx.CancelPrepared);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 180_000), ref fx);
        Assert.True(fx.Load);
        Assert.Equal(Queue.RefAt(0), fx.LoadRow);
        Assert.Equal(0, s.Cursor.Index);
        Assert.Equal(0, fx.LoadFromMs);
    }

    // ── G-108: the device reload ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_device_reload_reloads_the_row_at_its_position_and_keeps_play_intent()
    {
        var s = Playing(3);
        s.PosMs = 10_000;
        s.PosQpc = 1_000;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Signal(Playback.AudioSignal.DeviceReload, s.LoadEpoch, 4_000), ref fx);

        Assert.True(fx.Load);
        Assert.Equal(Queue.RefAt(0), fx.LoadRow);
        Assert.Equal(13_000, fx.LoadFromMs);
        Assert.Equal(Playback.LoadOrigin.DeviceReload, fx.LoadWhy);
        Assert.False(fx.LoadPaused);
        Assert.Equal(180_000, s.DurationMs);                             // the same row: its duration still holds
        Assert.Equal(0, s.Cursor.Index);
    }

    [Fact]
    public void A_paused_device_reload_reloads_paused()
    {
        var s = Playing(3);
        s.Phase = Playback.Phase.Paused;
        s.PosMs = 10_000;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Signal(Playback.AudioSignal.DeviceReload, s.LoadEpoch, 4_000), ref fx);

        Assert.True(fx.Load);
        Assert.True(fx.LoadPaused);
        Assert.Equal(10_000, fx.LoadFromMs);
        Assert.Equal(Playback.Phase.Paused, s.Phase);
    }

    [Fact]
    public void A_stale_or_parked_device_reload_does_nothing()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Signal(Playback.AudioSignal.DeviceReload, s.LoadEpoch + 5, 4_000), ref fx);
        Assert.False(fx.Load);

        s.Parked = true;
        Playback.Step(ref s, Signal(Playback.AudioSignal.DeviceReload, s.LoadEpoch, 4_000), ref fx);
        Assert.False(fx.Load);
    }

    // ── G-140 / G-141 / G-142 / G-143: video ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Placement_on_for_a_row_with_a_video_loads_the_video_host_at_the_position_and_off_loads_audio()
    {
        var s = Playing(3);
        GiveVideo(s.Current);
        s.PosMs = 30_000;
        s.PosQpc = 0;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.VideoPlacement(true, nowMs: 2_000), ref fx);
        Assert.True(fx.Load);
        Assert.Equal(Playback.PlayableKind.Video, fx.LoadKind);
        Assert.Equal(32_000, fx.LoadFromMs);
        Assert.Equal(Playback.LoadOrigin.MediaKindRefresh, fx.LoadWhy);
        Assert.Equal(Playback.PlayableKind.Video, s.Kind);
        Assert.True(fx.PublishState);                                    // track_player changes on the wire

        fx.Clear();
        Playback.Step(ref s, Playback.Input.VideoPlacement(false, nowMs: 2_500), ref fx);
        Assert.True(fx.Load);
        Assert.Equal(Playback.PlayableKind.Audio, fx.LoadKind);
        Assert.Equal(32_000, fx.LoadFromMs);
    }

    [Fact]
    public void Placement_on_for_a_row_without_a_video_only_records_the_wish_for_the_next_row()
    {
        var s = Playing(3);
        GiveVideo(Queue.RefAt(1));
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.VideoPlacement(true), ref fx);
        Assert.False(fx.Load);
        Assert.True(s.VideoWanted);
        Assert.Equal(Playback.PlayableKind.Audio, s.Kind);

        Playback.Step(ref s, Playback.Input.Next(), ref fx);
        Assert.Equal(Playback.PlayableKind.Video, fx.LoadKind);
        Assert.False(fx.Prefetch);                                       // a video row is never prefetched on the pump
    }

    [Fact]
    public void A_failed_video_load_retries_once_then_demotes_to_audio_at_the_carried_position()
    {
        var s = Playing(3);
        GiveVideo(s.Current);
        s.PosMs = 60_000;
        s.PosQpc = 0;
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.VideoPlacement(true, nowMs: 0), ref fx);
        fx.Clear();

        Playback.Step(ref s, Signal(Playback.AudioSignal.Failed, s.LoadEpoch, 1_000, (long)Playback.Fault.Network), ref fx);
        Assert.True(fx.Load);
        Assert.Equal(Playback.PlayableKind.Video, fx.LoadKind);
        Assert.Equal(Playback.LoadOrigin.VideoRecovery, fx.LoadWhy);
        Assert.Equal(60_000, fx.LoadFromMs);
        Assert.Equal(Playback.Fault.None, s.Error);
        Assert.False(fx.VideoDemoted);

        fx.Clear();
        Playback.Step(ref s, Signal(Playback.AudioSignal.Failed, s.LoadEpoch, 2_000, (long)Playback.Fault.Network), ref fx);
        Assert.True(fx.Load);
        Assert.Equal(Playback.PlayableKind.Audio, fx.LoadKind);
        Assert.Equal(60_000, fx.LoadFromMs);
        Assert.True(fx.VideoDemoted);
        Assert.Equal(Playback.Fault.Network, fx.VideoDemotedWhy);
        Assert.False(s.VideoWanted);
        Assert.Equal(Playback.Fault.None, s.Error);                      // a fallback, not a dead track
    }

    [Fact]
    public void A_video_with_no_source_demotes_at_once()
    {
        var s = Playing(3);
        GiveVideo(s.Current);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.VideoPlacement(true), ref fx);
        fx.Clear();

        Playback.Step(ref s, Signal(Playback.AudioSignal.VideoUnavailable, s.LoadEpoch), ref fx);

        Assert.Equal(Playback.PlayableKind.Audio, fx.LoadKind);
        Assert.True(fx.VideoDemoted);
        Assert.Equal(Playback.Fault.Unavailable, fx.VideoDemotedWhy);
    }

    [Fact]
    public void A_video_offer_announces_only_for_the_row_we_own()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.VideoOffer(Queue.RefAt(2).Id), ref fx);
        Assert.False(fx.PublishState);

        Playback.Step(ref s, Playback.Input.VideoOffer(s.CurrentId), ref fx);
        Assert.True(fx.PublishState);
        Assert.Equal(Playback.PublishReason.PlayerStateChanged, fx.PublishWhy);

        TakenByPhone(ref s, ref fx);
        Playback.Step(ref s, Playback.Input.VideoOffer(s.CurrentId), ref fx);
        Assert.False(fx.PublishState);
    }

    // ── G-073: remote volume ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_controllers_volume_sets_ours_announces_and_is_never_forwarded()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        TakenByPhone(ref s, ref fx);

        Playback.Step(ref s, Playback.Input.RemoteVolume(32_768, messageId: 77), ref fx);

        Assert.True(fx.Volume);
        Assert.True(Math.Abs(0.5f - fx.VolumeValue) < 0.001f);
        Assert.False(fx.SendRemote);
        Assert.True(Math.Abs(0.5f - s.Volume) < 0.001f);
        Assert.Equal(77u, s.LastCommandMessageId);
        Assert.Equal(Playback.PublishReason.VolumeChanged, fx.PublishWhy);
    }

    [Fact]
    public void The_slider_mirrors_the_foreign_owner_while_the_put_body_keeps_our_volume()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        var remote = new Playback.RemoteState(false, default, false, true, false, 0, 0, 0, false, RepeatMode.Off,
            32_768, false, false, false);
        TakenByPhone(ref s, ref fx, remote);

        Assert.True(Math.Abs(0.5f - s.SliderVolume) < 0.001f);
        Assert.Equal(1f, s.Volume);
        var identity = new Playback.DeviceIdentity("wavee-device", "Wavee", "cid", "Win32", "1.2.3", "3.2.6");
        Assert.Equal(Playback.MaxWireVolume, Playback.Snapshot.Of(in s, in identity, Playback.PublishReason.VolumeChanged, 1, 1, 0).Volume);
    }

    [Fact]
    public void A_snapshot_carries_the_last_command_message_id_and_the_rows_video_offer()
    {
        var s = Playing(3);
        s.LastCommandMessageId = 42;
        var identity = new Playback.DeviceIdentity("wavee-device", "Wavee", "cid", "Win32", "1.2.3", "3.2.6");

        var snapshot = Playback.Snapshot.Of(in s, in identity, Playback.PublishReason.PlayerStateChanged, 1, 1, 0, hasVideo: true);

        Assert.Equal(42u, snapshot.LastCommandMessageId);
        Assert.True(snapshot.HasVideo);
        Assert.Equal(42u, snapshot.WithMessageId(9).LastCommandMessageId);
        Assert.True(snapshot.WithMessageId(9).HasVideo);
    }

    // ── G-081 / G-082 (D13): suspend and wake ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Suspend_while_a_foreign_device_owns_playback_forwards_nothing()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        TakenByPhone(ref s, ref fx);
        var remote = new Playback.RemoteState(true, Track(8).Id, true, false, false, 0, 0, 200_000, false,
            RepeatMode.Off, 65_535, false, false, false);
        var frame = new Playback.ClusterFrame(Spotify.Decode.ClusterOrigin.Push, 0, Phone, 2_000);
        Playback.Step(ref s, Playback.Input.Cluster(in frame, in remote), ref fx);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Suspend(nowMs: 5_000), ref fx);

        Assert.False(fx.SendRemote);
        Assert.False(fx.PauseHost);
        Assert.Equal(Playback.Phase.Playing, s.Phase);                   // the phone keeps playing
    }

    [Fact]
    public void Suspend_pauses_local_playback_and_refreshes_the_card()
    {
        var s = Playing(3);
        s.PosMs = 1_000;
        s.PosQpc = 0;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Suspend(nowMs: 4_000), ref fx);

        Assert.True(fx.PauseHost);
        Assert.True(fx.Smtc);
        Assert.True(fx.Snapshot);
        Assert.Equal(Playback.Phase.Paused, s.Phase);
        Assert.Equal(5_000, s.PosMs);
    }

    [Fact]
    public void Waking_re_announces_the_device_as_a_new_connection()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Wake(nowMs: 9_000), ref fx);

        Assert.True(fx.PublishState);
        Assert.Equal(Playback.PublishReason.NewConnection, fx.PublishWhy);
        Assert.False(fx.ResumeHost);                                     // waking does not start playback
    }

    // ── G-078: the launch restore ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_restore_seeds_a_paused_parked_deck_without_claiming_or_loading()
    {
        TestScope.Fresh();
        var s = Playback.State.Initial;
        var fx = new Playback.Effects();
        EntityRef row = Track(3);

        Playback.Step(ref s, Playback.Input.Restore(row, row.Id, Album, QueueCursor.None, 42_000, 200_000, nowMs: 10), ref fx);

        Assert.Equal(row.Id, s.CurrentId);
        Assert.Equal(Album, s.Context);
        Assert.Equal(Playback.Phase.Paused, s.Phase);
        Assert.True(s.Parked);
        Assert.Equal(42_000, s.PosMs);
        Assert.Equal(200_000, s.DurationMs);
        Assert.Equal(Playback.Owner.Nobody, s.Owner);                    // never claiming
        Assert.False(fx.Load);
        Assert.False(fx.PublishState);
        Assert.True(fx.Smtc);
    }

    [Fact]
    public void A_restore_is_refused_over_a_live_deck_and_while_another_device_owns_playback()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        EntityId live = s.CurrentId;
        Playback.Step(ref s, Playback.Input.Restore(Track(5), Track(5).Id, Album, QueueCursor.None, 1, 2), ref fx);
        Assert.Equal(live, s.CurrentId);

        TestScope.Fresh();
        var idle = Playback.State.Initial;
        idle.Us = Playback.DeviceHash("wavee-device");
        TakenByPhone(ref idle, ref fx);
        Playback.Step(ref idle, Playback.Input.Restore(Track(5), Track(5).Id, Album, QueueCursor.None, 1, 2), ref fx);
        Assert.True(idle.CurrentId.IsEmpty);
    }

    [Fact]
    public void Resuming_a_parked_deck_loads_it_at_the_saved_position()
    {
        TestScope.Fresh();
        var s = Playback.State.Initial;
        var fx = new Playback.Effects();
        EntityRef row = Track(3);
        Playback.Step(ref s, Playback.Input.Restore(row, row.Id, Album, QueueCursor.None, 42_000, 200_000), ref fx);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Resume(nowMs: 50), ref fx);

        Assert.True(fx.Load);
        Assert.False(fx.ResumeHost);                                     // there is no session to resume
        Assert.Equal(row, fx.LoadRow);
        Assert.Equal(42_000, fx.LoadFromMs);
        Assert.Equal(Playback.LoadOrigin.Claim, fx.LoadWhy);
        Assert.Equal(Playback.Owner.Us, s.Owner);
        Assert.False(s.Parked);
        Assert.Equal(Playback.Phase.Loading, s.Phase);
    }

    [Fact]
    public void A_seek_on_a_parked_deck_moves_its_start_without_a_host_seek()
    {
        TestScope.Fresh();
        var s = Playback.State.Initial;
        var fx = new Playback.Effects();
        EntityRef row = Track(3);
        Playback.Step(ref s, Playback.Input.Restore(row, row.Id, Album, QueueCursor.None, 42_000, 200_000), ref fx);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Seek(60_000), ref fx);

        Assert.False(fx.Seek);
        Assert.Equal(60_000, s.PosMs);
        Assert.True(fx.Snapshot);
    }

    [Fact]
    public void A_restore_point_is_the_local_deck_and_never_a_foreign_mirror()
    {
        var s = Playing(3, withContext: true);
        s.Phase = Playback.Phase.Paused;
        s.PosMs = 12_345;
        Playback.RestorePoint point = Playback.RestorePoint.Of(in s, nowMs: 0);
        Assert.Equal(s.CurrentId, point.Track);
        Assert.Equal(Album, point.Context);
        Assert.Equal(12_345, point.PositionMs);
        Assert.Equal(0, point.CursorIndex);

        var fx = new Playback.Effects();
        TakenByPhone(ref s, ref fx);
        Assert.True(Playback.RestorePoint.Of(in s, nowMs: 0).IsEmpty);
    }

    // ── G-036: sign-out ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Logout_clears_our_deck_announces_inactive_and_moves_the_epochs_on()
    {
        var s = Playing(3);
        ulong us = s.Us;
        uint before = s.Epoch;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Release(Playback.ReleaseCause.Logout), ref fx);

        Assert.False(s.HasCurrent);
        Assert.Equal(Playback.Phase.Idle, s.Phase);
        Assert.Equal(Playback.Owner.Nobody, s.Owner);
        Assert.True(fx.Stop);
        Assert.Equal(Playback.PublishReason.BecameInactive, fx.PublishWhy);
        Assert.True(s.Epoch > before);
        Assert.Equal(s.Epoch, s.LoadEpoch);
        Assert.Equal(us, s.Us);
    }

    [Fact]
    public void Logout_while_a_phone_owns_playback_drops_the_mirror_without_announcing()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        var remote = new Playback.RemoteState(true, Track(8).Id, true, false, false, 0, 0, 200_000, false,
            RepeatMode.Off, 65_535, false, false, false);
        TakenByPhone(ref s, ref fx, remote);
        Assert.True(s.HasCurrent);

        Playback.Step(ref s, Playback.Input.Release(Playback.ReleaseCause.Logout), ref fx);

        Assert.False(s.HasCurrent);
        Assert.False(fx.PublishState);
    }

    // ── G-076: the play report ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_rows_registration_opens_on_its_first_audio_and_closes_when_the_next_row_starts()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        EntityRef row = Queue.RefAt(1);
        Playback.Step(ref s, Playback.Input.Play(row, row.Id, Album, Queue.CursorOf(1), nowMs: 1_000), ref fx);
        Assert.Equal(Playback.PlayEvents.None, fx.Play.Events);           // nothing was registered yet

        Playback.Step(ref s, Signal(Playback.AudioSignal.Started, s.LoadEpoch, 1_200, 0), ref fx);
        Assert.True((fx.Play.Events & Playback.PlayEvents.Started) != 0);
        Assert.Equal(row.Id, fx.Play.StartedId);
        Assert.Equal(Album, fx.Play.Context);
        Assert.Equal(Playback.PlayReason.ClickRow, fx.Play.StartReason);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Next(nowMs: 3_200), ref fx);
        Assert.Equal(Playback.PlayEvents.Ended, fx.Play.Events);
        Assert.Equal(row.Id, fx.Play.EndedId);
        Assert.Equal(Playback.PlayReason.ForwardButton, fx.Play.EndReason);
        Assert.Equal(2_000, fx.Play.EndPosMs);
    }

    [Fact]
    public void A_reload_of_the_same_row_never_opens_a_second_registration()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Signal(Playback.AudioSignal.Started, s.LoadEpoch, 0, 0), ref fx);
        Assert.True(s.ReportOpen);
        fx.Clear();

        Playback.Step(ref s, Signal(Playback.AudioSignal.DeviceReload, s.LoadEpoch, 1_000), ref fx);
        Playback.Step(ref s, Signal(Playback.AudioSignal.Started, s.LoadEpoch, 1_500, 1_000), ref fx);

        Assert.Equal(Playback.PlayEvents.None, fx.Play.Events);
    }

    [Fact]
    public void A_hand_off_closes_one_registration_and_opens_the_next_in_one_step()
    {
        var s = Playing(3);
        s.ReportOpen = true;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.HandedOff(s.LoadEpoch, Queue.RefAt(1).Id, 0, nowMs: 5_000), ref fx);

        Assert.Equal(Playback.PlayEvents.Ended | Playback.PlayEvents.Started, fx.Play.Events);
        Assert.Equal(Queue.RefAt(0).Id, fx.Play.EndedId);
        Assert.Equal(180_000, fx.Play.EndPosMs);                         // a natural end ends at the row's duration
        Assert.Equal(Playback.PlayReason.TrackDone, fx.Play.EndReason);
        Assert.Equal(Queue.RefAt(1).Id, fx.Play.StartedId);
        Assert.Equal(Playback.PlayReason.TrackDone, fx.Play.StartReason);
    }

    [Fact]
    public void Pause_resume_and_seek_report_against_the_open_registration()
    {
        var s = Playing(3);
        s.ReportOpen = true;
        s.PosMs = 5_000;
        s.PosQpc = 0;
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Seek(9_000, nowMs: 1_000), ref fx);
        Assert.True((fx.Play.Events & Playback.PlayEvents.Seeked) != 0);
        Assert.Equal(6_000, fx.Play.SeekFromMs);
        Assert.Equal(9_000, fx.Play.SeekToMs);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Pause(nowMs: 2_000), ref fx);
        Assert.Equal(Playback.PlayEvents.Paused, fx.Play.Events);
        Assert.Equal(10_000, fx.Play.PausePosMs);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Resume(nowMs: 3_000), ref fx);
        Assert.Equal(Playback.PlayEvents.Resumed, fx.Play.Events);
    }

    [Theory]
    [InlineData(Playback.PlayReason.ClickRow, "clickrow")]
    [InlineData(Playback.PlayReason.TrackDone, "trackdone")]
    [InlineData(Playback.PlayReason.ForwardButton, "fwdbtn")]
    [InlineData(Playback.PlayReason.BackButton, "backbtn")]
    [InlineData(Playback.PlayReason.PlayButton, "playbtn")]
    [InlineData(Playback.PlayReason.Remote, "remote")]
    [InlineData(Playback.PlayReason.EndPlay, "endplay")]
    [InlineData(Playback.PlayReason.TrackError, "trackerror")]
    [InlineData(Playback.PlayReason.Logout, "logout")]
    public void Every_reason_has_gabos_word(Playback.PlayReason reason, string word)
        => Assert.Equal(word, Playback.ReasonText(reason));

    // ── G-080: autoplay and shuffle ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_context_that_runs_out_asks_autoplay_and_waits_for_it()
    {
        var s = Playing(1, withContext: true);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Next(), ref fx);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 200_000), ref fx);

        Assert.True(fx.Autoplay);
        Assert.Equal(Album, fx.AutoplayContext);
        Assert.Equal(Playback.AutoplayPhase.Waiting, s.Autoplay);
        Assert.Equal(Playback.Phase.Loading, s.Phase);
        Assert.False(fx.Load);
    }

    [Fact]
    public void Appended_autoplay_rows_advance_the_waiting_deck()
    {
        var s = Playing(1, withContext: true);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Next(), ref fx);
        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 200_000), ref fx);
        fx.Clear();

        EntityRef autoplay = Track(50);
        Queue.Replace([Track(0), Track(1), autoplay],
            [Row(QueueBucket.NowPlaying, 1), Row(QueueBucket.NextUp, 2), Row(QueueBucket.NextUp, 3, QueueProvider.Autoplay)]);
        Playback.Step(ref s, Playback.Input.Autoplayed(Album, appended: 1), ref fx);

        Assert.True(fx.Load);
        Assert.Equal(autoplay, fx.LoadRow);
        Assert.Equal(2, s.Cursor.Index);
        Assert.Equal(Playback.AutoplayPhase.None, s.Autoplay);
    }

    [Fact]
    public void A_declined_autoplay_ends_the_phase()
    {
        var s = Playing(0, withContext: true);
        var fx = new Playback.Effects();
        Playback.Step(ref s, Playback.Input.Ended(s.LoadEpoch, nowMs: 200_000), ref fx);
        fx.Clear();

        Playback.Step(ref s, Playback.Input.Autoplayed(Album, appended: 0), ref fx);

        Assert.Equal(Playback.Phase.Ended, s.Phase);
        Assert.Equal(Playback.AutoplayPhase.Exhausted, s.Autoplay);
        Assert.True(fx.Stop);
        Assert.True(s.Parked);
    }

    [Fact]
    public void The_endgame_asks_autoplay_early_when_nothing_follows()
    {
        var s = Playing(0, withContext: true);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Signal(Playback.AudioSignal.EndingSoon, s.LoadEpoch, 170_000), ref fx);

        Assert.True(fx.Autoplay);
        Assert.Equal(Playback.AutoplayPhase.Requested, s.Autoplay);
        Assert.False(fx.PrepareNext);

        // The answer lands before the end: the first autoplay row is prepared for a gapless join.
        fx.Clear();
        EntityRef autoplay = Track(51);
        Queue.Replace([Track(0), autoplay], [Row(QueueBucket.NowPlaying, 1), Row(QueueBucket.NextUp, 2, QueueProvider.Autoplay)]);
        Playback.Step(ref s, Playback.Input.Autoplayed(Album, appended: 1), ref fx);
        Assert.True(fx.PrepareNext);
        Assert.Equal(autoplay, fx.NextRow);
    }

    [Fact]
    public void Shuffle_asks_the_host_to_reorder_unless_the_queue_was_built_in_that_order()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();

        Playback.Step(ref s, Playback.Input.Shuffle(true), ref fx);
        Assert.True(fx.Reorder);
        Assert.True(fx.ReorderShuffle);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.Shuffle(false), ref fx);
        Assert.True(fx.Reorder);
        Assert.False(fx.ReorderShuffle);

        fx.Clear();
        Playback.Step(ref s, Playback.Input.ShuffleOrdered(true), ref fx);
        Assert.False(fx.Reorder);
        Assert.True(s.Shuffle);
    }

    // ── G-074 / G-071: controller writes and the inbound load ───────────────────────────────────────────────────────

    [Fact]
    public void A_controllers_add_to_queue_claims_and_asks_the_host_to_enqueue()
    {
        var s = Playing(3);
        s.Own = Playback.OwnerState.Initial;
        var fx = new Playback.Effects();
        EntityId queued = Track(60).Id;
        var cmd = new Spotify.Decode.RemoteCommand(RemoteCmd.AddToQueue, Ok: true, MessageId: 12, SeekToMs: 0,
            BoolArg: false, Track: queued, SenderHash: 9, SessionHash: 0, DedupeKey: 1);

        Playback.Step(ref s, Playback.Input.Controller(in cmd, nowMs: 1_000), ref fx);

        Assert.Equal(Playback.Owner.Us, s.Owner);
        Assert.True(fx.QueueAdd);
        Assert.Equal(queued, fx.QueueAddId);
        Assert.Equal(12u, s.LastCommandMessageId);
    }

    [Fact]
    public void An_inbound_load_that_raced_a_takeover_is_dropped_and_never_forwarded()
    {
        var s = Playing(3);
        var fx = new Playback.Effects();
        TakenByPhone(ref s, ref fx);
        EntityRef row = Queue.RefAt(2);

        Playback.Step(ref s, Playback.Input.PlayFrom(row, row.Id, Album, Queue.CursorOf(2), Playback.PlayableKind.Audio,
            0, 1_000, Playback.ClaimCause.InboundPlay, paused: false), ref fx);

        Assert.False(fx.SendRemote);
        Assert.False(fx.Load);
    }

    [Fact]
    public void A_paused_transfer_loads_paused_at_its_position_under_the_inbound_cause()
    {
        var s = Playing(3);
        s.Own = Playback.OwnerState.Initial;
        var fx = new Playback.Effects();
        EntityRef row = Queue.RefAt(2);

        Playback.Step(ref s, Playback.Input.PlayFrom(row, row.Id, Album, Queue.CursorOf(2), Playback.PlayableKind.Audio,
            45_000, 1_000, Playback.ClaimCause.InboundTransfer, paused: true), ref fx);

        Assert.True(fx.Load);
        Assert.True(fx.LoadPaused);
        Assert.Equal(45_000, fx.LoadFromMs);
        Assert.Equal(Playback.Phase.Paused, s.Phase);
        Assert.Equal(Playback.Owner.Us, s.Owner);
        Assert.Equal(Playback.PlayReason.Remote, s.StartReason);
    }

    [Fact]
    public void Which_host_a_row_plays_on_follows_the_placement_wish_its_video_and_its_locality()
    {
        TestScope.Fresh();
        EntityRef plain = Track(1), video = Track(2), local = Track(3);
        GiveVideo(video);
        Entities.Current.Tracks.Flags[local.Slot] |= (uint)TrackFlags.Local;

        Assert.Equal(Playback.PlayableKind.Audio, Playback.KindOfRow(plain, videoWanted: true));
        Assert.Equal(Playback.PlayableKind.Video, Playback.KindOfRow(video, videoWanted: true));
        Assert.Equal(Playback.PlayableKind.Audio, Playback.KindOfRow(video, videoWanted: false));
        Assert.Equal(Playback.PlayableKind.LocalFile, Playback.KindOfRow(local, videoWanted: true));
        Assert.Equal(Playback.PlayableKind.Audio, Playback.KindOfRow(default, videoWanted: true));
    }
}

/// <summary>The pure planners behind a context load (G-070/071/075/080). No queue, no scope: spans in, answers out.</summary>
public class PlaybackPlannerTests
{
    static Spotify.Decode.ClusterBuffer Rows(params (string Uri, string Uid)[] tracks)
    {
        var buffer = Spotify.Decode.ClusterBuffer.Rent();
        foreach (var (uri, uid) in tracks)
        {
            ref var row = ref buffer.AddTrack();
            row.Uri = buffer.AddText(System.Text.Encoding.UTF8.GetBytes(uri));
            row.Uid = buffer.AddText(System.Text.Encoding.UTF8.GetBytes(uid));
        }
        return buffer;
    }

    static TextRef Text(Spotify.Decode.ClusterBuffer buffer, string value)
        => buffer.AddText(System.Text.Encoding.UTF8.GetBytes(value));

    [Fact]
    public void The_start_row_is_the_uid_then_the_uri_then_the_index()
    {
        var buffer = Rows(("spotify:track:a", "u1"), ("spotify:track:b", "u2"), ("spotify:track:c", "u3"));
        try
        {
            var tracks = buffer.Tracks(0, 3);
            Assert.Equal(2, Playback.RemotePlan.StartIndex(buffer, tracks, Text(buffer, "u3"), Text(buffer, "spotify:track:b"), 0));
            Assert.Equal(1, Playback.RemotePlan.StartIndex(buffer, tracks, default, Text(buffer, "spotify:track:b"), 0));
            Assert.Equal(1, Playback.RemotePlan.StartIndex(buffer, tracks, Text(buffer, "nope"), default, 1));
            Assert.Equal(-1, Playback.RemotePlan.StartIndex(buffer, tracks, default, default, 9));
            Assert.Equal(-1, Playback.RemotePlan.StartIndex(buffer, tracks, default, default, -1));
        }
        finally { Spotify.Decode.ClusterBuffer.Return(buffer); }
    }

    [Fact]
    public void A_play_starts_paused_when_it_says_so_and_a_transfer_restores_the_remotes_pause_unless_killed()
    {
        var play = new Spotify.Decode.RemoteLoad { Kind = RemoteCmd.Play, InitiallyPaused = true };
        Assert.True(Playback.RemotePlan.StartsPaused(in play));

        var transfer = new Spotify.Decode.RemoteLoad { Kind = RemoteCmd.Transfer, HasPlayback = true, Paused = true };
        Assert.True(Playback.RemotePlan.StartsPaused(in transfer));
        transfer.ForcePlay = true;
        Assert.False(Playback.RemotePlan.StartsPaused(in transfer));
    }

    [Fact]
    public void A_playing_transfer_extrapolates_its_position_and_a_paused_one_does_not()
    {
        var transfer = new Spotify.Decode.RemoteLoad
        {
            Kind = RemoteCmd.Transfer, HasPlayback = true, PositionAsOfMs = 10_000, TimestampMs = 1_000, Speed = 1.0,
            Extrapolate = true,
        };
        Assert.Equal(13_000L, Playback.RemotePlan.StartPositionMs(in transfer, unixNowMs: 4_000));

        transfer.Paused = true;
        Assert.Equal(10_000L, Playback.RemotePlan.StartPositionMs(in transfer, unixNowMs: 4_000));

        var play = new Spotify.Decode.RemoteLoad { Kind = RemoteCmd.Play, SeekToMs = -1 };
        Assert.Equal(0L, Playback.RemotePlan.StartPositionMs(in play, 4_000));
        play.SeekToMs = 5_000;
        Assert.Equal(5_000L, Playback.RemotePlan.StartPositionMs(in play, 4_000));
    }

    [Theory]
    [InlineData("queue", QueueProvider.Queue)]
    [InlineData("autoplay", QueueProvider.Autoplay)]
    [InlineData("context", QueueProvider.Context)]
    [InlineData("", QueueProvider.Context)]
    public void The_wire_provider_word_is_the_queue_provenance(string word, QueueProvider expected)
        => Assert.Equal(expected, Playback.RemotePlan.ProviderOf(System.Text.Encoding.UTF8.GetBytes(word)));

    [Theory]
    [InlineData("spotify:album:4aawyAB9vmqN3uQ7FjRGTy", true)]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", true)]
    [InlineData("spotify:user:bob:collection", true)]
    [InlineData("spotify:station:track:7idegBIikag5rTZP4WZihP", false)]
    [InlineData("spotify:radio:7idegBIikag5rTZP4WZihP", false)]
    [InlineData("spotify:autoplay:7idegBIikag5rTZP4WZihP", false)]
    // A bare track or episode "context" — a card's single-row play — continues too: the request sends the bare uri
    // as `context_uri` and the server resolves its station server-side, the way Spotify's own clients do.
    [InlineData("spotify:track:7idegBIikag5rTZP4WZihP", true)]
    [InlineData("spotify:episode:7idegBIikag5rTZP4WZihP", true)]
    [InlineData("", false)]
    public void Autoplay_continues_containers_and_a_bare_track_but_never_an_infinite_context(string uri, bool continues)
        => Assert.Equal(continues, Playback.RemotePlan.AutoplayContinues(System.Text.Encoding.UTF8.GetBytes(uri)));

    [Fact]
    public void A_sixteen_hex_uid_round_trips_through_the_queue_item_id_and_nothing_else_packs()
    {
        ulong item = Playback.QueueUid.ItemIdOf("2a826aa43895001e"u8);
        Assert.NotEqual(0UL, item);
        Span<char> text = stackalloc char[Playback.QueueUid.Chars];
        Assert.Equal(16, Playback.QueueUid.Format(item, text));
        Assert.Equal("2a826aa43895001e", text.ToString());

        Assert.Equal(0UL, Playback.QueueUid.ItemIdOf("2A826AA43895001E"u8));   // uppercase would not round-trip exactly
        Assert.Equal(0UL, Playback.QueueUid.ItemIdOf("q0"u8));
        Assert.Equal(0UL, Playback.QueueUid.ItemIdOf("2a826aa43895001"u8));
        Assert.Equal(0, Playback.QueueUid.Format(0, text));
    }

    [Fact]
    public void A_shuffle_is_a_deterministic_permutation()
    {
        int[] a = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        int[] b = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        Playback.ShuffleOrder.Permute(a, seed: 42);
        Playback.ShuffleOrder.Permute(b, seed: 42);
        Assert.Equal(a, b);
        int[] sorted = (int[])a.Clone();
        Array.Sort(sorted);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, sorted);
        Assert.NotEqual(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, a);
    }

    [Fact]
    public void Unshuffling_restores_the_context_order_and_keeps_rows_queued_since_after_it()
    {
        int[] savedRefs = [10, 20, 30, 20];
        QueueEdge[] savedRows = [new(1, 0, 3), new(2, 0, 3), new(3, 0, 3), new(4, 0, 3)];
        // Now: row 1 was consumed, a row 99 arrived, and the rest are in shuffled order.
        int[] refs = [99, 20, 30, 20];
        QueueEdge[] rows = [new(9, 0, 3), new(4, 0, 3), new(3, 0, 3), new(2, 0, 3)];

        Playback.ShuffleOrder.Unshuffle(savedRefs, savedRows, refs, rows);

        Assert.Equal(new[] { 20, 30, 20, 99 }, refs);
        Assert.Equal(new[] { 2UL, 3UL, 4UL, 9UL }, new[] { rows[0].ItemId, rows[1].ItemId, rows[2].ItemId, rows[3].ItemId });
    }
}
