// ── Wavee.Tests/PlaybackRulesTests.cs — the pure rules that live beside the reducer ───────────────────────────────
//
// Every fact here is over a function that takes values and returns a value: no scope, no queue, no engine, no socket.
// That is the whole reason these rules were extracted in 0.2.9 and the reason they are re-homed in `Playback.cs` —
// their behaviour at the edges is decided by a test instead of by a live stream nobody can replay.
//
// Ported suites, name for name: `TimeFormatTests`, `Backend/LiveRailTests`, `Backend/LiveEdgeStateTests`,
// `Backend/NowPlayingLiveWindowTests` (the `HasWindow`/`BehindMs` half), `SmtcTimelineCoalescerTests` (all 9 facts),
// `MediaSwitchLogicTests`, `SeekGateTests`. Plus the one fact that could not exist before Wave 3: the PUT body the
// reducer's snapshot encodes, read back by the GENERATED `PutStateRequest` parser — a hand encoder pinned against the
// canonical decoder rather than against a blob nobody can regenerate.

using Google.Protobuf;
using Wavee;
using Xunit;

using Pb = Wavee.Protocol.Player;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee.Tests;

public class TimeFormatTests
{
    [Theory]
    [InlineData(0L, "0:00")]
    [InlineData(999L, "0:00")]           // sub-second truncates down — a clock never rounds a second into existence
    [InlineData(1_000L, "0:01")]
    [InlineData(9_000L, "0:09")]
    [InlineData(10_000L, "0:10")]        // the two-digit seconds boundary
    [InlineData(59_000L, "0:59")]
    [InlineData(60_000L, "1:00")]
    [InlineData(3_599_000L, "59:59")]    // the last m:ss reading
    public void Below_an_hour_it_is_minutes_and_seconds(long ms, string expected)
        => Assert.Equal(expected, Playback.TimeFormat.Clock(ms));

    [Theory]
    [InlineData(3_600_000L, "1:00:00")]  // the rung itself: the minutes field gains its leading zero HERE
    [InlineData(3_601_000L, "1:00:01")]
    [InlineData(3_660_000L, "1:01:00")]
    [InlineData(3_784_000L, "1:03:04")]
    [InlineData(36_000_000L, "10:00:00")]
    [InlineData(360_000_000L, "100:00:00")]   // hours are never wrapped — a 100-hour stream reads as 100 hours
    public void At_and_above_an_hour_it_grows_an_hours_field(long ms, string expected)
        => Assert.Equal(expected, Playback.TimeFormat.Clock(ms));

    [Fact]
    public void The_hour_rung_is_exact()
    {
        Assert.Equal(3_600_000L, Playback.TimeFormat.HourMs);
        Assert.Equal("59:59", Playback.TimeFormat.Clock(Playback.TimeFormat.HourMs - 1));
        Assert.Equal("1:00:00", Playback.TimeFormat.Clock(Playback.TimeFormat.HourMs));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(-60_000L)]
    public void A_negative_duration_clamps_to_zero(long ms) => Assert.Equal("0:00", Playback.TimeFormat.Clock(ms));
}

public class LiveWindowTests
{
    static Playback.LiveWindow Window(long start, long end, long edge, long position)
        => new(IsLive: true, start, end, edge, position, IsAtLiveEdge: false);

    [Fact]
    public void HasWindow_needs_thirty_seconds()
    {
        Assert.Equal(30_000L, Playback.LiveWindow.MinWindowMs);
        Assert.False(Window(0, 29_999, 29_999, 0).HasWindow);        // a line, not a rail
        Assert.True(Window(0, 30_000, 30_000, 0).HasWindow);
    }

    [Fact]
    public void BehindMs_is_never_negative()
    {
        Assert.Equal(5_000L, Window(0, 100_000, 100_000, 95_000).BehindMs);
        Assert.Equal(0L, Window(0, 100_000, 100_000, 120_000).BehindMs);
    }

    [Fact]
    public void None_is_not_live_and_has_no_window()
    {
        var none = Playback.LiveWindow.None;
        Assert.False(none.IsLive);
        Assert.False(none.HasWindow);
        Assert.Equal(0L, none.WindowMs);
        Assert.Equal(0L, none.BehindMs);
    }
}

public class LiveRailTests
{
    static Playback.LiveWindow Window(long start, long end, long edge, long position)
        => new(IsLive: true, start, end, edge, position, IsAtLiveEdge: false);

    [Fact]
    public void Frac_maps_the_window_and_not_the_clock()
    {
        // A 4-hour DVR window whose start is at 11 400 000 ms would peg the thumb at the far right forever under the
        // position/duration formula. The rail maps the WINDOW.
        Assert.Equal(0.5, Playback.LiveRail.Frac(11_400_000, 11_500_000, 11_450_000), 6);
        Assert.Equal(0.25, Playback.LiveRail.Frac(0, 400_000, 100_000), 6);
    }

    [Fact]
    public void Frac_clamps_outside_the_window()
    {
        Assert.Equal(0.0, Playback.LiveRail.Frac(1_000, 2_000, 500));     // the window slid past the playhead
        Assert.Equal(1.0, Playback.LiveRail.Frac(1_000, 2_000, 9_000));
    }

    [Fact]
    public void Frac_with_no_window_is_at_the_edge()
    {
        // A station with nothing to rewind IS at the live edge; answering 0 would draw an empty rail under audio that
        // is playing.
        Assert.Equal(1.0, Playback.LiveRail.Frac(5_000, 5_000, 5_000));
        Assert.Equal(1.0, Playback.LiveRail.Frac(5_000, 4_000, 5_000));
    }

    [Fact]
    public void Seek_is_the_inverse_of_Frac()
    {
        Assert.Equal(11_450_000L, Playback.LiveRail.Seek(11_400_000, 11_500_000, 0.5));
        Assert.Equal(100_000L, Playback.LiveRail.Seek(0, 400_000, 0.25));
    }

    [Fact]
    public void Seek_clamps_into_the_window_at_both_ends()
    {
        Assert.Equal(1_000L, Playback.LiveRail.Seek(1_000, 2_000, -5));
        Assert.Equal(2_000L, Playback.LiveRail.Seek(1_000, 2_000, 5));
        Assert.Equal(1_000L, Playback.LiveRail.Seek(1_000, 2_000, double.NaN));
    }

    [Fact]
    public void Seek_with_no_window_commits_to_the_one_position()
    {
        Assert.Equal(5_000L, Playback.LiveRail.Seek(5_000, 5_000, 0.3));
        Assert.Equal(5_000L, Playback.LiveRail.Seek(5_000, 4_000, 0.9));
    }

    [Fact]
    public void Seek_never_leaves_the_window_for_any_fraction()
    {
        for (int i = -5; i <= 15; i++)
        {
            long ms = Playback.LiveRail.Seek(10_000, 70_000, i / 10.0);
            Assert.InRange(ms, 10_000L, 70_000L);
        }
    }

    [Fact]
    public void DisplayFrac_at_the_edge_is_full_whatever_the_measurement_says()
    {
        // The breathing fix: a healthy playhead rides a few seconds inside a window whose two ends BOTH move, so the
        // honest fraction is ~0.88 and a different ~0.88 four times a second. At the edge the rail snaps full and goes
        // dead still between reports.
        Assert.Equal(1.0, Playback.LiveRail.DisplayFrac(0, 50_000, 44_000, isBehind: false));
        Assert.Equal(1.0, Playback.LiveRail.DisplayFrac(0, 50_000, 0, isBehind: false));
    }

    [Theory]
    [InlineData(0L, 50_000L, 44_000L)]
    [InlineData(1_000_000L, 1_060_000L, 1_052_000L)]
    public void DisplayFrac_at_the_edge_is_window_independent(long start, long end, long position)
        => Assert.Equal(1.0, Playback.LiveRail.DisplayFrac(start, end, position, isBehind: false));

    [Theory]
    [InlineData(10_000L)]
    [InlineData(25_000L)]
    public void DisplayFrac_when_behind_is_the_measurement(long position)
        => Assert.Equal(Playback.LiveRail.Frac(0, 50_000, position),
                        Playback.LiveRail.DisplayFrac(0, 50_000, position, isBehind: true), 6);

    [Fact]
    public void The_window_overloads_read_their_own_fields()
    {
        var w = Window(1_000, 61_000, 61_000, 31_000);
        Assert.Equal(0.5, Playback.LiveRail.Frac(in w), 6);
        Assert.Equal(31_000L, Playback.LiveRail.Seek(in w, 0.5));
    }

    [Fact]
    public void BehindAt_reports_the_distance_to_the_edge_for_a_drag_in_flight()
    {
        // What "GO LIVE −m:ss" reads BEFORE anything is committed.
        var w = Window(0, 60_000, 60_000, 60_000);
        Assert.Equal(30_000L, Playback.LiveRail.BehindAt(in w, 0.5));
        Assert.Equal(0L, Playback.LiveRail.BehindAt(in w, 1.0));
        Assert.Equal(0L, Playback.LiveRail.BehindAt(in w, 2.0));
    }
}

public class LiveEdgeStateTests
{
    [Fact]
    public void The_two_lines_are_wide_apart_and_the_enter_line_is_the_higher_one()
    {
        Assert.Equal(15_000L, Playback.LiveEdgeState.EnterBehindMs);
        Assert.Equal(5_000L, Playback.LiveEdgeState.ReturnToEdgeMs);
        Assert.Equal(2, Playback.LiveEdgeState.ConfirmReports);
        Assert.True(Playback.LiveEdgeState.ReturnToEdgeMs < Playback.LiveEdgeState.EnterBehindMs);
    }

    [Fact]
    public void The_default_state_is_at_the_edge()
    {
        Assert.False(default(Playback.LiveEdgeState).IsBehind);
        Assert.Equal(0, default(Playback.LiveEdgeState).PendingReports);
        Assert.Equal(Playback.LiveEdgeState.AtEdge, default(Playback.LiveEdgeState));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(5_000L)]
    [InlineData(8_000L)]
    [InlineData(15_000L)]
    public void A_healthy_ride_never_leaves_the_edge_however_many_reports_arrive(long behindMs)
    {
        var s = Playback.LiveEdgeState.AtEdge;
        for (int i = 0; i < 40; i++) s = Playback.LiveEdgeState.Next(s, behindMs, hasWindow: true);
        Assert.False(s.IsBehind);
        Assert.Equal(0, s.PendingReports);
    }

    [Fact]
    public void A_wobble_across_the_enter_line_never_confirms()
    {
        var s = Playback.LiveEdgeState.AtEdge;
        for (int i = 0; i < 20; i++)
        {
            s = Playback.LiveEdgeState.Next(s, 16_000, hasWindow: true);   // one report out
            s = Playback.LiveEdgeState.Next(s, 9_000, hasWindow: true);    // …and back in, which CLEARS the counter
            Assert.False(s.IsBehind);
        }
    }

    [Fact]
    public void One_report_past_the_line_arms_the_confirmation_without_entering_behind()
    {
        var s = Playback.LiveEdgeState.Next(Playback.LiveEdgeState.AtEdge, 20_000, hasWindow: true);
        Assert.False(s.IsBehind);
        Assert.Equal(1, s.PendingReports);
    }

    [Fact]
    public void Two_consecutive_reports_past_the_line_enter_behind()
    {
        var s = Playback.LiveEdgeState.Next(Playback.LiveEdgeState.AtEdge, 20_000, hasWindow: true);
        s = Playback.LiveEdgeState.Next(s, 21_000, hasWindow: true);
        Assert.True(s.IsBehind);
        Assert.Equal(0, s.PendingReports);
    }

    [Fact]
    public void The_enter_line_is_exact()
    {
        var at = Playback.LiveEdgeState.Next(Playback.LiveEdgeState.AtEdge, Playback.LiveEdgeState.EnterBehindMs, true);
        Assert.Equal(0, at.PendingReports);

        var past = Playback.LiveEdgeState.Next(Playback.LiveEdgeState.AtEdge, Playback.LiveEdgeState.EnterBehindMs + 1, true);
        Assert.Equal(1, past.PendingReports);
    }

    [Theory]
    [InlineData(5_001L)]
    [InlineData(10_000L)]
    [InlineData(14_999L)]
    public void Between_the_two_lines_a_behind_playable_stays_behind(long behindMs)
    {
        var s = Playback.LiveEdgeState.Next(Playback.LiveEdgeState.Behind, behindMs, hasWindow: true);
        Assert.True(s.IsBehind);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(5_000L)]
    public void At_or_under_the_return_line_it_is_back_at_the_edge_on_the_first_report(long behindMs)
    {
        var s = Playback.LiveEdgeState.Next(Playback.LiveEdgeState.Behind, behindMs, hasWindow: true);
        Assert.False(s.IsBehind);
    }

    [Fact]
    public void The_return_line_is_exact()
    {
        Assert.False(Playback.LiveEdgeState.Next(Playback.LiveEdgeState.Behind, Playback.LiveEdgeState.ReturnToEdgeMs, true).IsBehind);
        Assert.True(Playback.LiveEdgeState.Next(Playback.LiveEdgeState.Behind, Playback.LiveEdgeState.ReturnToEdgeMs + 1, true).IsBehind);
    }

    [Fact]
    public void After_returning_the_confirmation_starts_over()
    {
        var s = Playback.LiveEdgeState.Next(Playback.LiveEdgeState.Behind, 1_000, hasWindow: true);
        Assert.False(s.IsBehind);
        s = Playback.LiveEdgeState.Next(s, 20_000, hasWindow: true);
        Assert.False(s.IsBehind);                                           // ONE report is not a fall
        s = Playback.LiveEdgeState.Next(s, 20_000, hasWindow: true);
        Assert.True(s.IsBehind);
    }

    [Fact]
    public void With_nothing_to_rewind_it_is_always_at_the_edge_and_the_machine_is_cleared()
    {
        var armed = Playback.LiveEdgeState.Next(Playback.LiveEdgeState.AtEdge, 20_000, hasWindow: true);
        Assert.Equal(1, armed.PendingReports);

        var cleared = Playback.LiveEdgeState.Next(armed, 999_999, hasWindow: false);
        Assert.False(cleared.IsBehind);
        Assert.Equal(0, cleared.PendingReports);
        Assert.False(Playback.LiveEdgeState.Next(Playback.LiveEdgeState.Behind, 999_999, hasWindow: false).IsBehind);
    }

    [Fact]
    public void The_fold_is_deterministic()
    {
        var a = Playback.LiveEdgeState.AtEdge;
        var b = Playback.LiveEdgeState.AtEdge;
        long[] reports = [1_000, 20_000, 20_000, 9_000, 30_000, 30_000, 4_000];
        foreach (long r in reports)
        {
            a = Playback.LiveEdgeState.Next(a, r, true);
            b = Playback.LiveEdgeState.Next(b, r, true);
            Assert.Equal(a, b);
        }
    }
}

public class SmtcTimelineCoalescerTests
{
    [Fact]
    public void The_first_push_schedules_a_flush()
    {
        var c = default(Playback.SmtcTimelineCoalescer);
        Assert.False(c.FlushQueued);
        Assert.True(c.Push(1_000));
        Assert.True(c.FlushQueued);
    }

    [Fact]
    public void A_burst_of_ticks_schedules_exactly_one_flush()
    {
        // THE point: N queued ticks cost one cross-process COM RPC, not N.
        var c = default(Playback.SmtcTimelineCoalescer);
        Assert.True(c.Push(1_000));
        for (int i = 0; i < 50; i++) Assert.False(c.Push(1_000 + i * 1_000));
    }

    [Fact]
    public void A_bursts_flush_carries_the_newest_position()
    {
        var c = default(Playback.SmtcTimelineCoalescer);
        c.Push(1_000);
        c.Push(2_000);
        c.Push(3_000);
        Assert.True(c.TryTake(180_000, out long position));
        Assert.Equal(3_000L, position);
    }

    [Fact]
    public void After_a_flush_the_next_tick_schedules_again()
    {
        var c = default(Playback.SmtcTimelineCoalescer);
        c.Push(1_000);
        Assert.True(c.TryTake(180_000, out _));
        Assert.False(c.FlushQueued);
        Assert.True(c.Push(2_000));
    }

    [Fact]
    public void The_same_whole_second_is_deduped()
    {
        var c = default(Playback.SmtcTimelineCoalescer);
        c.Push(1_000);
        Assert.True(c.TryTake(180_000, out _));

        c.Push(1_400);
        Assert.False(c.TryTake(180_000, out _));                            // still second 1
        c.Push(2_000);
        Assert.True(c.TryTake(180_000, out long position));
        Assert.Equal(2_000L, position);
    }

    [Fact]
    public void The_first_push_at_second_zero_is_not_deduped()
    {
        // `_hasLast` exists for exactly this: `default(struct)` must not dedupe against second 0, or a track that
        // starts at 0:00 never pushes its first timeline.
        var c = default(Playback.SmtcTimelineCoalescer);
        c.Push(0);
        Assert.True(c.TryTake(180_000, out long position));
        Assert.Equal(0L, position);
    }

    [Fact]
    public void The_position_is_clamped_to_the_duration()
    {
        var c = default(Playback.SmtcTimelineCoalescer);
        c.Push(999_999);
        Assert.True(c.TryTake(180_000, out long position));
        Assert.Equal(180_000L, position);

        c.Push(-5_000);
        Assert.True(c.TryTake(180_000, out position));
        Assert.Equal(0L, position);
    }

    [Fact]
    public void An_unknown_duration_pushes_nothing()
    {
        // A live module stream: no progress and no total, and the previous track's duration must be gone.
        var c = default(Playback.SmtcTimelineCoalescer);
        c.Push(5_000);
        Assert.False(c.TryTake(0, out long position));
        Assert.Equal(0L, position);
        Assert.False(c.TryTake(-1, out _));
    }

    [Fact]
    public void A_bailed_out_flush_still_clears_the_latch()
    {
        // Drop this and one bail-out wedges the latch armed forever: the scrub bar and the taskbar bar stop moving for
        // the rest of the process, with no error anywhere.
        var c = default(Playback.SmtcTimelineCoalescer);
        c.Push(5_000);
        Assert.True(c.FlushQueued);
        Assert.False(c.TryTake(0, out _));
        Assert.False(c.FlushQueued);
        Assert.True(c.Push(6_000));                                          // and it can be armed again
    }
}

public class MediaSwitchTests
{
    [Fact]
    public void A_video_track_is_a_video_wherever_it_came_from()
    {
        Assert.Equal(Playback.PlayableKind.Video, Playback.MediaSwitch.KindOf(isVideoTrack: true, isLocalFile: true));
        Assert.Equal(Playback.PlayableKind.Video, Playback.MediaSwitch.KindOf(isVideoTrack: true, isLocalFile: false));
        Assert.Equal(Playback.PlayableKind.LocalFile, Playback.MediaSwitch.KindOf(false, true));
        Assert.Equal(Playback.PlayableKind.Audio, Playback.MediaSwitch.KindOf(false, false));
    }

    [Fact]
    public void The_same_kind_reloads_and_a_different_kind_swaps()
    {
        Assert.Equal(Playback.MediaSwitch.SwitchAction.LoadOnCurrent,
            Playback.MediaSwitch.Decide(Playback.PlayableKind.Audio, Playback.PlayableKind.Audio));
        Assert.Equal(Playback.MediaSwitch.SwitchAction.SwapThenLoad,
            Playback.MediaSwitch.Decide(Playback.PlayableKind.Audio, Playback.PlayableKind.Video));
    }

    [Fact]
    public void Crossfade_is_audio_only_and_same_kind()
    {
        Assert.True(Playback.MediaSwitch.AllowCrossfade(Playback.PlayableKind.Audio, Playback.PlayableKind.Audio));
        Assert.False(Playback.MediaSwitch.AllowCrossfade(Playback.PlayableKind.LocalFile, Playback.PlayableKind.LocalFile));
        Assert.False(Playback.MediaSwitch.AllowCrossfade(Playback.PlayableKind.Audio, Playback.PlayableKind.Video));
        Assert.False(Playback.MediaSwitch.AllowCrossfade(Playback.PlayableKind.Video, Playback.PlayableKind.Video));
    }

    [Fact]
    public void A_local_file_reports_audio_on_the_wire_because_it_plays_through_the_audio_host()
    {
        Assert.Equal("video", Playback.MediaSwitch.TrackPlayer(Playback.PlayableKind.Video));
        Assert.Equal("audio", Playback.MediaSwitch.TrackPlayer(Playback.PlayableKind.Audio));
        Assert.Equal("audio", Playback.MediaSwitch.TrackPlayer(Playback.PlayableKind.LocalFile));
    }

    [Fact]
    public void Only_a_video_boundary_actually_moves_the_host_instance()
    {
        // Audio↔LocalFile is a KIND change (so the outgoing host stops) but the same host reloads, which is what keeps
        // the fast-start / prepared-next path untouched.
        Assert.True(Playback.MediaSwitch.ShouldStopOutgoingHost(Playback.PlayableKind.Audio, Playback.PlayableKind.LocalFile));
        Assert.False(Playback.MediaSwitch.HostChanges(Playback.PlayableKind.Audio, Playback.PlayableKind.LocalFile));

        Assert.True(Playback.MediaSwitch.HostChanges(Playback.PlayableKind.Audio, Playback.PlayableKind.Video));
        Assert.True(Playback.MediaSwitch.HostChanges(Playback.PlayableKind.Video, Playback.PlayableKind.LocalFile));
        Assert.False(Playback.MediaSwitch.HostChanges(Playback.PlayableKind.Video, Playback.PlayableKind.Video));

        // At every real host swap the stop-first rule agrees with the swap trigger.
        foreach (var from in new[] { Playback.PlayableKind.Audio, Playback.PlayableKind.Video, Playback.PlayableKind.LocalFile })
            foreach (var to in new[] { Playback.PlayableKind.Audio, Playback.PlayableKind.Video, Playback.PlayableKind.LocalFile })
                if (Playback.MediaSwitch.HostChanges(from, to))
                    Assert.True(Playback.MediaSwitch.ShouldStopOutgoingHost(from, to));
    }
}

public class PlaybackHostGateTests
{
    [Fact]
    public void A_seek_the_source_cannot_serve_yet_is_parked_rather_than_deadlocking_the_pump()
    {
        Assert.Equal(Playback.SeekAdmission.ApplyNow, Playback.SeekGate.Decide(hasSession: true, sourceCanServeBeyondHead: true));
        Assert.Equal(Playback.SeekAdmission.Defer, Playback.SeekGate.Decide(hasSession: true, sourceCanServeBeyondHead: false));
        Assert.Equal(Playback.SeekAdmission.Defer, Playback.SeekGate.Decide(hasSession: false, sourceCanServeBeyondHead: true));
        Assert.Equal(Playback.SeekAdmission.Defer, Playback.SeekGate.Decide(false, false));
    }

    [Fact]
    public void A_parked_seek_is_what_gets_reported_not_the_sessions_stale_clock()
    {
        // Otherwise a track the user resumed at 3:45 shows 0:00 until the body attaches.
        Assert.Equal(225_000L, Playback.SeekGate.ReportedPositionMs(225_000, 0));
        Assert.Equal(0L, Playback.SeekGate.ReportedPositionMs(0, 9_000));    // 0 IS a parked target
        Assert.Equal(9_000L, Playback.SeekGate.ReportedPositionMs(-1, 9_000));
    }

    [Fact]
    public void A_load_nobody_asked_to_hear_announces_no_buffering()
    {
        Assert.False(Playback.PlayIntentGate.ShouldAnnounceBuffering(playIntent: false));
        Assert.True(Playback.PlayIntentGate.ShouldAnnounceBuffering(playIntent: true));
    }

    [Fact]
    public void The_gapless_join_is_expressed_from_the_clock_now_and_never_from_track_start()
    {
        // The mid-track reopen bug: a track-absolute join frame computed once at open scheduled the join hundreds of
        // seconds into the future after a device-format reload rebased the sample clock.
        Assert.Equal(44_100L, Playback.GaplessJoinClock.MsToFrames(1_000, 44_100));
        Assert.Equal(2_251_680L + 44_100L * 2L, Playback.GaplessJoinClock.JoinFrameFor(2_251_680, 180_000, 178_000, 44_100));
        Assert.Equal(2_251_680L, Playback.GaplessJoinClock.JoinFrameFor(2_251_680, 180_000, 999_000, 44_100));   // never negative
    }

    [Fact]
    public void A_stale_join_estimate_degrades_to_a_butt_join_and_never_to_a_stall()
    {
        long now = 1_000_000;
        // A join estimate far in the future is bounded by the remaining time + 100 ms.
        long bounded = Playback.GaplessJoinClock.ScheduleJoin(9_486_174, now, remainingMs: 1_966, rate: 44_100);
        Assert.InRange(bounded, now, now + Playback.GaplessJoinClock.MsToFrames(1_966, 44_100) + 4_410);

        // And one in the PAST is pulled up to now.
        Assert.Equal(now, Playback.GaplessJoinClock.ScheduleJoin(500, now, 10_000, 44_100));
    }

    [Fact]
    public void A_primed_voice_only_splices_into_a_mixer_at_its_own_rate()
    {
        Assert.True(Playback.GaplessJoinClock.PrimedSlotMatches(48_000, 48_000));
        Assert.False(Playback.GaplessJoinClock.PrimedSlotMatches(44_100, 48_000));
    }

    [Fact]
    public void The_join_never_commits_into_a_reloading_session_or_off_a_stale_clock()
    {
        Assert.True(Playback.GaplessJoinClock.CanCommit(clockStale: false, softReloading: false));
        Assert.False(Playback.GaplessJoinClock.CanCommit(clockStale: true, softReloading: false));
        Assert.False(Playback.GaplessJoinClock.CanCommit(clockStale: false, softReloading: true));
    }

    [Fact]
    public void A_rate_change_ends_in_an_audible_session_or_a_retry_and_never_in_silence()
    {
        Assert.Equal(Playback.DeviceRecoveryAction.ReopenNewGraph,
            Playback.DeviceRecoveryPlan.Decide(requiresGraphRebuild: true, canReopen: true));
        Assert.Equal(Playback.DeviceRecoveryAction.AdoptIntoExistingGraph,
            Playback.DeviceRecoveryPlan.Decide(requiresGraphRebuild: false, canReopen: true));
        Assert.Equal(Playback.DeviceRecoveryAction.ReloadThroughController,
            Playback.DeviceRecoveryPlan.Decide(requiresGraphRebuild: true, canReopen: false));
        Assert.Equal(Playback.DeviceRecoveryAction.KeepSession,
            Playback.DeviceRecoveryPlan.Decide(requiresGraphRebuild: false, canReopen: false));
    }

    [Fact]
    public void An_endpoints_short_name_prefers_its_description_and_strips_the_adapter_parenthetical()
    {
        Assert.Equal("Speakers", Playback.AudioDeviceNaming.Shorten("Speakers", "Speakers (Realtek(R) Audio)"));
        Assert.Equal("Speakers", Playback.AudioDeviceNaming.Shorten("  Speakers  ", null));
        Assert.Equal("Speakers", Playback.AudioDeviceNaming.Shorten(null, "Speakers (Realtek(R) Audio)"));
        Assert.Equal("Headphones", Playback.AudioDeviceNaming.Shorten("", "Headphones"));
        Assert.Null(Playback.AudioDeviceNaming.Shorten(null, null));
        Assert.Equal("", Playback.AudioDeviceNaming.Shorten(null, ""));
    }
}

// ── the PUT body: the hand encoder against the canonical parser ─────────────────────────────────────────────────────

public class PutStateEncodeTests
{
    static Playback.DeviceIdentity Identity() => new(
        DeviceId: "wavee-device-id",
        DeviceName: "Christos's PC",
        ClientId: "65b708073fc0480ea92a077233ca87bd",
        Platform: "Win32_x86_64",
        SoftwareVersion: "1.2.94.583.g60394bd5",
        SpircVersion: "3.2.6");

    static Playback.State Playing()
    {
        var s = Playback.State.Initial;
        s.Us = Playback.DeviceHash("wavee-device-id");
        s.CurrentId = EntityId.ForGid(EntityKind.Track, (UInt128)0x1234_5678_9ABC_DEF0UL);
        s.Context = EntityId.ForGid(EntityKind.Playlist, (UInt128)0x0FED_CBA9_8765_4321UL);
        s.Phase = Playback.Phase.Playing;
        s.PosMs = 42_000;
        s.DurationMs = 234_959;
        s.Volume = 0.5f;
        s.Shuffle = true;
        s.Repeat = RepeatMode.Track;
        s.StartedPlayingAtMs = 1_700_000_000_000;
        s.HasBeenPlayingForMs = 42_000;
        Playback.Ownership.Claim(ref s.Own, Playback.ClaimCause.UserPlay, 1, s.StartedPlayingAtMs, 0, acknowledged: true);
        return s;
    }

    static Pb.PutStateRequest Encode(in Playback.Snapshot snapshot)
    {
        Span<byte> buffer = new byte[8 * 1024];
        int written = Spotify.Decode.PutState(in snapshot, buffer);
        Assert.True(written > 0);
        return Pb.PutStateRequest.Parser.ParseFrom(buffer[..written].ToArray());
    }

    [Fact]
    public void PutState_encodes_a_snapshot_the_generated_parser_reads_back()
    {
        var state = Playing();
        var identity = Identity();
        var snapshot = Playback.Snapshot.Of(in state, in identity, Playback.PublishReason.PlayerStateChanged,
            messageId: 0, unixMs: 1_700_000_042_000, frameNowMs: 0, uid: "2a826aa43895001e");

        Pb.PutStateRequest request = Encode(snapshot.WithMessageId(17));

        Assert.Equal(Pb.MemberType.ConnectState, request.MemberType);
        Assert.True(request.IsActive);
        Assert.Equal(Pb.PutStateReason.PlayerStateChanged, request.PutStateReason);
        Assert.Equal(17u, request.MessageId);
        Assert.Equal(1_700_000_000_000UL, request.StartedPlayingAt);
        Assert.Equal(42_000UL, request.HasBeenPlayingForMs);
        Assert.Equal(1_700_000_042_000UL, request.ClientSideTimestamp);

        Pb.DeviceInfo info = request.Device.DeviceInfo;
        Assert.True(info.CanPlay);
        Assert.Equal("Christos's PC", info.Name);
        Assert.Equal("wavee-device-id", info.DeviceId);
        Assert.Equal("65b708073fc0480ea92a077233ca87bd", info.ClientId);
        Assert.Equal("3.2.6", info.SpircVersion);
        Assert.Equal("1.2.94.583.g60394bd5", info.DeviceSoftwareVersion);
        Assert.Equal(Pb.DeviceType.Computer, info.DeviceType);
        Assert.Equal((uint)Playback.Input.WireVolume(0.5f), info.Volume);
        Assert.Equal("Win32_x86_64", request.Device.PrivateDeviceInfo.Platform);

        Pb.PlayerState player = request.Device.PlayerState;
        Assert.Equal("spotify:playlist:" + Base62Of(state.Context), player.ContextUri);
        Assert.Equal("spotify:track:" + Base62Of(state.CurrentId), player.Track.Uri);
        Assert.Equal("2a826aa43895001e", player.Track.Uid);
        Assert.Equal("context", player.Track.Provider);
        Assert.Equal("audio", player.Track.Metadata["track_player"]);
        Assert.Equal(42_000L, player.PositionAsOfTimestamp);
        Assert.Equal(234_959L, player.Duration);
        Assert.True(player.IsPlaying);
        Assert.False(player.IsPaused);
        Assert.False(player.IsBuffering);
        Assert.Equal(1.0, player.PlaybackSpeed);
        Assert.True(player.Options.ShufflingContext);
        Assert.True(player.Options.RepeatingTrack);
        Assert.False(player.Options.RepeatingContext);
        Assert.Equal(1_700_000_042_000L, player.Timestamp);
    }

    [Fact]
    public void The_desktop_parity_capabilities_survive_the_round_trip()
    {
        // These are anti-fraud and feature-eligibility surface, proven byte-exact over 24 captured desktop PUTs.
        // `license = premium` is Recently-Played eligibility; `needs_full_player_state` is why we get whole clusters.
        var state = Playing();
        var identity = Identity();
        var snapshot = Playback.Snapshot.Of(in state, in identity, Playback.PublishReason.PlayerStateChanged, 1, 1, 0);

        Pb.DeviceInfo info = Encode(in snapshot).Device.DeviceInfo;

        Assert.Equal("spotify", info.Brand);
        Assert.Equal("PC laptop", info.Model);
        Assert.Equal("premium", info.License);
        Assert.Equal("1", info.MetadataMap["debug_level"]);
        Assert.Equal("0", info.MetadataMap["tier1_port"]);

        Pb.Capabilities c = info.Capabilities;
        Assert.True(c.CanBePlayer);
        Assert.True(c.NeedsFullPlayerState);
        Assert.True(c.SupportsTransferCommand);
        Assert.True(c.SupportsCommandRequest);
        Assert.True(c.SupportsGzipPushes);
        Assert.True(c.CommandAcks);
        Assert.True(c.IsControllable);
        Assert.Equal(64, c.VolumeSteps);
        Assert.Equal(15, c.SupportedTypes.Count);
        Assert.Contains("audio/track", c.SupportedTypes);
        Assert.Contains("video/track", c.SupportedTypes);
        Assert.Equal(Wavee.Protocol.Media.AudioQuality.VeryHigh, c.SupportedAudioQuality);
        Assert.True(c.SupportsHifi.FullySupported);
        Assert.True(c.UnknownCapability33);
        Assert.True(c.UnknownCapability38);
    }

    [Fact]
    public void A_video_playable_reports_track_player_video()
    {
        var state = Playing();
        state.Kind = Playback.PlayableKind.Video;
        var identity = Identity();
        var snapshot = Playback.Snapshot.Of(in state, in identity, Playback.PublishReason.PlayerStateChanged, 1, 1, 0);

        Assert.Equal("video", Encode(in snapshot).Device.PlayerState.Track.Metadata["track_player"]);
    }

    [Fact]
    public void While_another_device_owns_playback_the_player_half_is_empty_but_our_volume_is_not()
    {
        // librespot's rule, and the fix for 0.2.9 announcing a phone's track as ours.
        //
        // Ownership only moves through the FENCE, so the setup has to move it the way the app does. A dealer PUSH
        // naming the phone while our claim is still Protected is row P2: it is RECORDED and deliberately cannot
        // revoke (`P2_a_foreign_push_during_protection_does_not_revoke` pins exactly that), so driving the setup
        // with one left this fact asserting an empty player half against an owner that was still US. Bind the claim
        // to its put and let the server's own VERDICT name the phone — P3, the real takeover path.
        var state = Playing();
        ulong phone = Playback.DeviceHash("phone");
        Playback.Ownership.PutSent(ref state.Own, 1, isActive: true);
        Playback.Ownership.Fold(ref state.Own,
            new Playback.ClusterFrame(Spotify.Decode.ClusterOrigin.PutResponse, 1, phone, 9_000),
            state.Us);
        Assert.Equal(Playback.Owner.Foreign, state.Owner);
        Assert.Equal(phone, state.Own.Device);

        var identity = Identity();
        var snapshot = Playback.Snapshot.Of(in state, in identity, Playback.PublishReason.BecameInactive, 1, 5_000, 0);
        Pb.PutStateRequest request = Encode(in snapshot);

        Assert.False(request.IsActive);
        Assert.Equal(Pb.PutStateReason.BecameInactive, request.PutStateReason);
        Assert.Equal("", request.Device.PlayerState.ContextUri);
        Assert.Null(request.Device.PlayerState.Track);
        Assert.False(request.Device.PlayerState.IsPlaying);
        Assert.Equal((uint)Playback.Input.WireVolume(0.5f), request.Device.DeviceInfo.Volume);
    }

    [Fact]
    public void The_put_state_reason_is_the_protos_own_ordinal_and_not_the_glues()
    {
        // The BODY carries the PROTO's ordinal, read off connect.proto: BECAME_INACTIVE is 7, and 6 is PICKER_OPENED.
        // `Spotify.Connect.PutReason` spelled BecameInactive = 6 when this was written (reported, and owner F has
        // since corrected it); the host still maps between the two BY NAME rather than by cast, so the two enums are
        // free to diverge again without this body ever announcing the wrong reason.
        Assert.Equal(7, (int)Playback.PublishReason.BecameInactive);
        Assert.Equal(6, (int)Playback.PublishReason.PickerOpened);
        var state = Playing();
        var identity = Identity();
        foreach (var why in new[]
        {
            Playback.PublishReason.SpircHello, Playback.PublishReason.NewDevice,
            Playback.PublishReason.PlayerStateChanged, Playback.PublishReason.VolumeChanged,
            Playback.PublishReason.BecameInactive,
        })
        {
            var snapshot = Playback.Snapshot.Of(in state, in identity, why, 1, 1, 0);
            Assert.Equal((int)why, (int)Encode(in snapshot).PutStateReason);
        }
    }

    [Fact]
    public void A_warm_encode_allocates_only_the_output_buffer_it_was_given()
    {
        var state = Playing();
        var identity = Identity();
        var snapshot = Playback.Snapshot.Of(in state, in identity, Playback.PublishReason.PlayerStateChanged, 1, 1, 0);
        byte[] buffer = new byte[8 * 1024];
        for (int i = 0; i < 4; i++) Spotify.Decode.PutState(in snapshot, buffer);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) Spotify.Decode.PutState(in snapshot, buffer);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }

    /// <summary>The base62 tail of a gid uri, read back through the formatter the encoder uses — so the fact asserts
    /// the WHOLE uri without hard-coding a 22-character string a reader cannot verify.</summary>
    static string Base62Of(EntityId id)
    {
        string text = id.Text;
        return text[(text.LastIndexOf(':') + 1)..];
    }
}
