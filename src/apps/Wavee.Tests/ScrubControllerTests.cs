// ── Wavee.Tests/ScrubControllerTests.cs — the seek rail's scrub as a core controller (themes plan C2, X2/X3) ──────────
// (Shell/ScrubController.cs.) Pure: a FAKE `IScrubSource` and a fake clock stand in for the reducer, so nothing here posts an
// input, reads a signal of the playback model or mounts a rail.
//
//   THE DWELL         how long the playhead sits on one pixel: clamp(span / railPx, 33, 250) ms; the span is the track's
//                     duration or a DVR window's width.
//   THE GENERATION HOLD  after a commit the rail keeps showing the DROP point until a seek NEWER than the one the commit
//                     sampled lands (V-PA12), or 750 ms pass: a ticker frame between the release and the drain must never
//                     repaint the old position.
//   THE KEY LADDER    Left/Right step a visual target (5 s, 15 s, 30 s while held; Shift = 1 s) and ONE seek commits when the
//                     key goes quiet; the target is painted until that seek lands.

using Xunit;

namespace Wavee.Tests;

/// <summary>The model, as a bag of fields and a call log.</summary>
public sealed class FakeScrubSource : Shell.IScrubSource
{
    public long Now = 1_000;
    public bool HasCurrent = true;
    public Playback.Fault Error = Playback.Fault.None;
    public Playback.Phase Phase = Playback.Phase.Playing;
    public bool Buffering = false;
    public bool CanSeek = true;
    public Playback.LiveWindow LiveWindow = Playback.LiveWindow.None;
    public long Duration = 200_000;
    public bool Behind = false;
    public long Playhead = 10_000;
    public long Reported = 10_000;
    public EntityId Current = default;
    public uint Gen = 0;
    public uint Landed = 0;
    public readonly List<string> Calls = [];

    public long NowMs() => Now;
    public Shell.ScrubGate Gate(bool track) => new(HasCurrent, Error, Phase, Buffering, CanSeek);
    public Playback.LiveWindow Live(bool track) => LiveWindow;
    public long DurationMs(bool track) => Duration;
    public bool IsBehindLive() => Behind;
    public long PlayheadMs(long nowMs) => Playhead;
    public long ReportedPositionMs() => Reported;
    public EntityId CurrentId() => Current;
    public uint SeekGen() => Gen;
    public uint LastSeekLandedGen() => Landed;
    public void ScrubBegin(int ms) => Calls.Add("begin:" + ms);
    public void ScrubMove(int ms) => Calls.Add("move:" + ms);
    public void ScrubEnd(int ms) => Calls.Add("end:" + ms);
    public void ScrubCancel() => Calls.Add("cancel");
    public void SeekTo(int ms) => Calls.Add("seek:" + ms);
}

public class ScrubDwellTests
{
    static Shell.ScrubController Rail(FakeScrubSource src, float railPx)
    {
        var scrub = new Shell.ScrubController(src, new Shell.KeyboardScrub(src));
        scrub.SetRailWidth(railPx);
        return scrub;
    }

    [Fact]
    public void The_dwell_is_the_span_over_the_rail_width_clamped_to_33_and_250_ms()
    {
        var src = new FakeScrubSource { Duration = 60_000 };
        Assert.Equal(60f, Rail(src, 1000f).DwellMs());        // 60 000 ms / 1 000 px
        src.Duration = 10_000;
        Assert.Equal(Shell.SeekRail.MinDwellMs, Rail(src, 1000f).DwellMs());   // 10 ms would oversample: floor
        src.Duration = 200_000;
        Assert.Equal(Shell.SeekRail.MaxDwellMs, Rail(src, 400f).DwellMs());    // 500 ms would stutter: ceiling
    }

    [Fact]
    public void The_dwell_is_the_unknown_default_until_the_span_or_the_width_is_known()
    {
        Assert.Equal(Shell.SeekRail.UnknownDwellMs, Rail(new FakeScrubSource { Duration = 0 }, 1000f).DwellMs());
        Assert.Equal(Shell.SeekRail.UnknownDwellMs, Rail(new FakeScrubSource(), 0f).DwellMs());
    }

    [Fact]
    public void A_dvr_rail_dwells_over_its_window_not_the_duration()
    {
        var src = new FakeScrubSource
        {
            Duration = 3_000_000,
            LiveWindow = new Playback.LiveWindow(true, 0, 120_000, 120_000, 100_000, false),
        };
        Assert.Equal(150f, Rail(src, 800f).DwellMs());        // 120 000 ms window / 800 px
    }

    [Fact]
    public void The_mode_is_decided_by_the_source_timeline_not_by_a_reported_duration()
    {
        var src = new FakeScrubSource();
        var scrub = Rail(src, 1000f);
        Assert.Equal(Shell.SeekRailMode.Track, scrub.Mode());
        src.LiveWindow = new Playback.LiveWindow(true, 0, 120_000, 120_000, 100_000, false);
        Assert.Equal(Shell.SeekRailMode.Dvr, scrub.Mode());
        src.LiveWindow = new Playback.LiveWindow(true, 0, 0, 0, 0, true);     // live, nothing to rewind
        Assert.Equal(Shell.SeekRailMode.Line, scrub.Mode());
    }

    [Fact]
    public void A_width_change_under_half_a_pixel_or_a_non_positive_width_is_ignored()
    {
        var src = new FakeScrubSource();
        var scrub = Rail(src, 1000f);
        scrub.SetRailWidth(1000.4f);
        scrub.SetRailWidth(0f);
        scrub.SetRailWidth(-5f);
        Assert.Equal(1000f, scrub.RailPx.Peek());
        scrub.SetRailWidth(1001f);
        Assert.Equal(1001f, scrub.RailPx.Peek());
    }

    [Fact]
    public void The_resting_fraction_follows_the_playhead_snapped_to_the_pixel_grid()
    {
        var src = new FakeScrubSource { Playhead = 10_000 };
        var scrub = Rail(src, 1000f);
        scrub.Recompute();
        Assert.Equal(0.05f, scrub.Display.Peek(), 5);
        src.Phase = Playback.Phase.Paused;                    // not advancing: the REPORTED position is the model's
        src.Reported = 100_000;
        scrub.Recompute();
        Assert.Equal(0.5f, scrub.Display.Peek(), 5);
    }
}

public class ScrubGestureTests
{
    static (Shell.ScrubController Scrub, FakeScrubSource Src, List<bool> Edges) Rail(FakeScrubSource? source = null)
    {
        var src = source ?? new FakeScrubSource();
        var edges = new List<bool>();
        var scrub = new Shell.ScrubController(src, new Shell.KeyboardScrub(src), edges.Add);
        scrub.SetRailWidth(1000f);
        return (scrub, src, edges);
    }

    static int Target(float fraction, long duration = 200_000)
        => (int)Shell.SeekRail.CommitTargetMs(fraction, duration, default);

    [Fact]
    public void A_press_begins_the_gesture_at_the_pointer_target_and_reports_the_edge()
    {
        var (scrub, src, edges) = Rail();
        Assert.True(scrub.Begin(0.5f));
        Assert.True(scrub.Scrubbing.Peek());
        Assert.Equal(0.5f, scrub.Fraction.Peek());
        Assert.Equal(new[] { "begin:" + Target(0.5f) }, src.Calls);
        Assert.Equal(new[] { true }, edges);
    }

    [Fact]
    public void A_drag_moves_the_target_and_the_drawn_fraction_ignores_the_model_while_the_finger_is_down()
    {
        var (scrub, src, _) = Rail();
        scrub.Begin(0.25f);
        src.Playhead = 150_000; src.Reported = 150_000;       // the model keeps reporting; the thumb must not move under the finger
        Assert.True(scrub.Update(0.75f));
        Assert.Equal(Target(0.75f), (int)scrub.TargetMsAt(scrub.Fraction.Peek()));
        Assert.Equal("move:" + Target(0.75f), src.Calls[^1]);
        Assert.Equal(0.75f, scrub.Display.Peek());
    }

    [Fact]
    public void A_rail_that_may_not_be_dragged_ignores_every_edge_but_still_releases_the_gate_on_commit()
    {
        var (scrub, src, edges) = Rail();
        src.CanSeek = false;
        Assert.False(scrub.Begin(0.5f));
        Assert.False(scrub.Update(0.6f));
        Assert.Empty(src.Calls);
        Assert.Empty(edges);

        Assert.False(scrub.Commit());                          // nothing to commit: cancelled, never left set
        Assert.Equal(new[] { "cancel" }, src.Calls);
        Assert.False(scrub.Scrubbing.Peek());
        Assert.Equal(new[] { false }, edges);
    }

    [Fact]
    public void A_commit_with_no_duration_and_no_window_cancels_instead_of_seeking_to_zero()
    {
        var (scrub, src, _) = Rail(new FakeScrubSource { Duration = 0 });
        scrub.Begin(0.5f);
        src.Calls.Clear();
        Assert.False(scrub.Commit());
        Assert.Equal(new[] { "cancel" }, src.Calls);
    }

    [Fact]
    public void A_commit_posts_the_gestures_one_seek_and_releases_the_gate()
    {
        var (scrub, src, edges) = Rail();
        scrub.Begin(0.5f);
        src.Calls.Clear();
        Assert.True(scrub.Commit());
        Assert.Equal(new[] { "end:" + Target(0.5f) }, src.Calls);
        Assert.False(scrub.Scrubbing.Peek());
        Assert.Equal(new[] { true, false }, edges);
    }

    [Fact]
    public void After_a_commit_the_rail_holds_the_drop_point_until_a_NEWER_seek_lands()
    {
        var (scrub, src, _) = Rail();
        src.Gen = 7; src.Landed = 7;
        scrub.Begin(0.5f);
        scrub.Commit();                                        // samples Gen = 7 BEFORE the post
        Assert.Equal(0.5f, scrub.Display.Peek());

        // The committing drain publishes the PRE-seek position: a report is not the release.
        src.Now += 40;
        scrub.OnModelReport();
        Assert.Equal(0.5f, scrub.Display.Peek());

        // A seek that landed is not newer than the one the commit sampled: still held.
        src.Landed = 7;
        scrub.OnModelReport();
        Assert.Equal(0.5f, scrub.Display.Peek());

        // The commit's own seek (generation 8) lands: the model paints the position again.
        src.Landed = 8; src.Playhead = 120_000;
        scrub.OnModelReport();
        Assert.Equal(0.6f, scrub.Display.Peek(), 5);
    }

    [Fact]
    public void A_commit_that_the_reducer_never_turns_into_a_seek_is_released_by_the_750_ms_timeout()
    {
        var (scrub, src, _) = Rail();
        src.Gen = 3; src.Landed = 3;
        scrub.Begin(0.5f);
        scrub.Commit();
        src.Now += Shell.SeekRail.CommitHoldMs - 1;
        scrub.Recompute();
        Assert.Equal(0.5f, scrub.Display.Peek());              // still held at 749 ms
        src.Now += 1;
        scrub.Recompute();
        Assert.Equal(0.05f, scrub.Display.Peek(), 5);          // the model's playhead (10 000 / 200 000)
    }

    [Fact]
    public void A_cancel_drops_the_hold_and_tells_the_reducer_idempotently()
    {
        var (scrub, src, edges) = Rail();
        scrub.Begin(0.5f);
        scrub.Commit();
        src.Calls.Clear();
        scrub.Cancel();
        Assert.Equal(new[] { "cancel" }, src.Calls);
        Assert.Equal(0.05f, scrub.Display.Peek(), 5);
        Assert.False(edges[^1]);
    }

    [Fact]
    public void An_unmount_mid_gesture_cancels_the_scrub_but_an_idle_rail_does_nothing()
    {
        var (scrub, src, _) = Rail();
        Assert.False(scrub.AbortIfScrubbing());
        Assert.Empty(src.Calls);
        scrub.Begin(0.5f);
        src.Calls.Clear();
        Assert.True(scrub.AbortIfScrubbing());
        Assert.Equal(new[] { "cancel" }, src.Calls);
    }

    [Fact]
    public void The_readout_is_wanted_on_a_plain_track_only()
    {
        var (scrub, src, _) = Rail();
        Assert.True(scrub.ReadoutWanted);
        src.LiveWindow = new Playback.LiveWindow(true, 0, 120_000, 120_000, 100_000, false);
        Assert.False(scrub.ReadoutWanted);                     // a DVR window's labels say how far behind live
        src.LiveWindow = default;
        src.Current = EntityId.Parse("spotify:episode:512ojhOuo1ktJprKbVcKyQ");
        Assert.False(scrub.ReadoutWanted);                     // an episode's chapter timeline names the chapter
    }

    [Fact]
    public void The_pointer_fraction_is_clamped_on_the_measured_rail()
    {
        var (scrub, _, _) = Rail();
        Assert.Equal(0.25f, scrub.FractionAt(250f));
        Assert.Equal(0f, scrub.FractionAt(-30f));
        Assert.Equal(1f, scrub.FractionAt(1400f));
    }
}

public class KeyboardScrubTests
{
    static (Shell.KeyboardScrub Keys, FakeScrubSource Src) Ladder(long position = 60_000)
    {
        var src = new FakeScrubSource { Now = 1_000, Playhead = position, Reported = position };
        return (new Shell.KeyboardScrub(src), src);
    }

    static void Press(Shell.KeyboardScrub keys, FakeScrubSource src, long at, int direction = 1, bool fine = false)
    {
        src.Now = at;
        keys.Step(direction, fine);
    }

    [Fact]
    public void A_press_steps_a_visual_target_five_seconds_and_arms_the_tick()
    {
        var (keys, src) = Ladder();
        Press(keys, src, 1_000);
        Assert.Equal(65_000L, keys.PreviewMs.Peek());
        Assert.True(keys.Active.Peek());
        Assert.Empty(src.Calls);                               // a press never seeks; the tick does
    }

    [Fact]
    public void Holding_the_key_climbs_the_ladder_5_then_15_seconds_and_shift_is_a_one_second_fine_step()
    {
        var (keys, src) = Ladder();
        Press(keys, src, 1_000);
        Press(keys, src, 1_100);                               // inside the 150 ms step cap: the target does not move
        Assert.Equal(65_000L, keys.PreviewMs.Peek());
        Press(keys, src, 1_300);
        Assert.Equal(70_000L, keys.PreviewMs.Peek());
        Press(keys, src, 1_450);
        Assert.Equal(75_000L, keys.PreviewMs.Peek());
        Press(keys, src, 1_650);                               // held 650 ms >= 500: the mid rung
        Assert.Equal(90_000L, keys.PreviewMs.Peek());

        var (fineKeys, fineSrc) = Ladder();
        Press(fineKeys, fineSrc, 1_000, direction: -1, fine: true);
        Assert.Equal(59_000L, fineKeys.PreviewMs.Peek());
    }

    [Fact]
    public void One_seek_commits_when_the_key_has_been_quiet_for_the_grace()
    {
        var (keys, src) = Ladder();
        Press(keys, src, 1_000);
        Press(keys, src, 1_200);                               // target 70 000
        src.Now = 1_300; keys.Tick();                          // 100 ms of silence: still stepping
        Assert.Empty(src.Calls);
        src.Gen = 4;
        src.Now = 1_450; keys.Tick();                          // 250 ms: commit, once
        Assert.Equal(new[] { "seek:70000" }, src.Calls);
        src.Now = 1_500; keys.Tick();
        Assert.Single(src.Calls);
    }

    [Fact]
    public void The_painted_target_stays_until_the_seek_lands_and_then_the_model_paints_again()
    {
        var (keys, src) = Ladder();
        Press(keys, src, 1_000);
        src.Gen = 4; src.Landed = 4;
        src.Now = 1_250; keys.Tick();                          // commit (generation 4 sampled before the post)
        Assert.Equal(65_000L, keys.PreviewMs.Peek());

        src.Now = 1_400; keys.Tick();                          // nothing newer landed: held
        Assert.Equal(65_000L, keys.PreviewMs.Peek());
        Assert.True(keys.Active.Peek());

        src.Landed = 5; src.Now = 1_450; keys.Tick();          // a NEWER seek landed
        Assert.Equal(-1L, keys.PreviewMs.Peek());
        Assert.False(keys.Active.Peek());
    }

    [Fact]
    public void A_commit_that_never_lands_is_released_by_the_hold_timeout()
    {
        var (keys, src) = Ladder();
        Press(keys, src, 1_000);
        src.Gen = 4; src.Landed = 4;
        src.Now = 1_250; keys.Tick();
        src.Now = 1_250 + Shell.SeekRail.CommitHoldMs; keys.Tick();
        Assert.Equal(-1L, keys.PreviewMs.Peek());
    }

    [Fact]
    public void A_row_change_under_a_live_ladder_drops_it_without_a_seek()
    {
        var (keys, src) = Ladder();
        src.Current = EntityId.Parse("spotify:track:0VjIjW4GlUZAMYd2vXMi3b");
        Press(keys, src, 1_000);
        src.Current = EntityId.Parse("spotify:track:11dFghVXANMlKmJXsNCbNl");
        src.Now = 1_300; keys.Tick();
        Assert.Equal(-1L, keys.PreviewMs.Peek());
        Assert.Empty(src.Calls);
    }

    [Fact]
    public void A_rail_that_may_not_seek_and_a_live_source_without_a_window_step_nothing()
    {
        var (keys, src) = Ladder();
        src.CanSeek = false;
        Press(keys, src, 1_000);
        Assert.Equal(-1L, keys.PreviewMs.Peek());

        var (noSpan, noSpanSrc) = Ladder();
        noSpanSrc.Duration = 0;
        Press(noSpan, noSpanSrc, 1_000);
        Assert.Equal(-1L, noSpan.PreviewMs.Peek());
        Assert.False(noSpan.Active.Peek());
    }

    [Fact]
    public void A_dvr_ladder_works_in_the_window_and_commits_offset_by_its_start()
    {
        var (keys, src) = Ladder();
        // Window [100 000, 220 000]; the playhead sits 30 s into it.
        src.LiveWindow = new Playback.LiveWindow(true, 100_000, 220_000, 220_000, 130_000, false);
        Press(keys, src, 1_000);
        Assert.Equal(135_000L, keys.PreviewMs.Peek());         // BaseMs 100 000 + (30 000 + 5 000)
        src.Now = 1_250; keys.Tick();
        Assert.Equal(new[] { "seek:135000" }, src.Calls);
    }

    [Fact]
    public void The_last_commit_tick_leaving_drops_a_live_ladder_so_labels_never_hold_its_target()
    {
        var (keys, src) = Ladder();
        keys.TickMounted();
        keys.TickMounted();
        Press(keys, src, 1_000);
        keys.TickUnmounted();
        Assert.Equal(65_000L, keys.PreviewMs.Peek());          // one rail left to commit it
        keys.TickUnmounted();
        Assert.Equal(-1L, keys.PreviewMs.Peek());
        Assert.False(keys.Active.Peek());
    }
}

public class SeekByTests
{
    [Fact]
    public void A_step_button_seeks_once_straight_from_the_position()
    {
        var src = new FakeScrubSource { Playhead = 60_000 };
        Shell.ScrubController.SeekBy(src, 10_000);
        Assert.Equal(new[] { "seek:70000" }, src.Calls);
    }

    [Fact]
    public void A_step_back_near_the_start_lands_on_zero_and_one_near_the_end_lands_before_the_tail_guard()
    {
        var back = new FakeScrubSource { Playhead = 4_000 };
        Shell.ScrubController.SeekBy(back, -10_000);
        Assert.Equal(new[] { "seek:0" }, back.Calls);

        var forward = new FakeScrubSource { Playhead = 195_000 };
        Shell.ScrubController.SeekBy(forward, 10_000);
        Assert.Equal(new[] { "seek:" + (200_000 - Playback.SeekTarget.TailGuardMs) }, forward.Calls);
    }

    [Fact]
    public void A_rail_that_may_not_seek_ignores_the_step_buttons()
    {
        var src = new FakeScrubSource { CanSeek = false };
        Shell.ScrubController.SeekBy(src, 10_000);
        Assert.Empty(src.Calls);
    }

    [Fact]
    public void A_dvr_step_clamps_into_the_window_from_the_window_position()
    {
        var src = new FakeScrubSource
        {
            LiveWindow = new Playback.LiveWindow(true, 100_000, 220_000, 220_000, 215_000, false),
        };
        Shell.ScrubController.SeekBy(src, 10_000);
        Assert.Equal(new[] { "seek:220000" }, src.Calls);
    }
}
