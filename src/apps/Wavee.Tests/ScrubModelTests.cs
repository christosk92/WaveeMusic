// ── Wavee.Tests/ScrubModelTests.cs — playback smoothness WP-3a (D4; V-PA6, V-PA9) ───────────────────────────────────
//
// `ScrubModel` (Playback/Playback.Scrub.cs) is the pointer gesture as a value: Idle → Pressed → Scrubbing, with Up and
// Cancel back to Idle at once. Pure: no engine, no clock — every call hands in `now` — so these drive the real arithmetic.
// Pinned: Down begins; Move coalesces to ≤ 1 per 50 ms and smooths the (dimensionless, unchanged) velocity; Up commits
// once; Cancel from every live state; Audible is cleared on cancel and survives Up for the reducer's commit decision.

using Xunit;

namespace Wavee.Tests;

public class ScrubModelTests
{
    static readonly ScrubModel.Effects Nothing = default;

    static ScrubModel.Effects BeginAt(long ms) => new(Begin: true, Move: false, Commit: false, Cancel: false, PositionMs: ms, Velocity: 0);
    static ScrubModel.Effects CommitAt(long ms) => new(Begin: false, Move: false, Commit: true, Cancel: false, PositionMs: ms, Velocity: 0);
    static ScrubModel.Effects CancelAt(long ms) => new(Begin: false, Move: false, Commit: false, Cancel: true, PositionMs: ms, Velocity: 0);

    static void AssertMove(ScrubModel.Effects e, long ms, double velocity)
    {
        Assert.True(e.Move);
        Assert.False(e.Begin);
        Assert.False(e.Commit);
        Assert.False(e.Cancel);
        Assert.Equal(ms, e.PositionMs);
        Assert.Equal(velocity, e.Velocity, 9);
    }

    static int Flags(ScrubModel.Effects e) => (e.Begin ? 1 : 0) + (e.Move ? 1 : 0) + (e.Commit ? 1 : 0) + (e.Cancel ? 1 : 0);

    // ── Down ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_default_model_is_idle_and_all_zero()
    {
        var m = new ScrubModel();

        Assert.Equal(ScrubModel.State.Idle, m.Current);
        Assert.False(m.Audible);
        Assert.Equal(0L, m.PositionMs);
        Assert.Equal(0L, m.LastMoveAtMs);
        Assert.Equal(0L, m.LastSentAtMs);
        Assert.Equal(0.0, m.Velocity);
    }

    [Fact]
    public void Down_begins_the_gesture_and_stamps_both_clocks()
    {
        var m = new ScrubModel();

        ScrubModel.Effects e = m.Down(12_345, 1_000);

        Assert.Equal(BeginAt(12_345), e);
        Assert.Equal(ScrubModel.State.Pressed, m.Current);
        Assert.Equal(12_345L, m.PositionMs);
        Assert.Equal(1_000L, m.LastMoveAtMs);
        Assert.Equal(1_000L, m.LastSentAtMs);
        Assert.Equal(0.0, m.Velocity);
    }

    [Fact]
    public void Down_always_begins_even_over_a_live_gesture_and_starts_the_velocity_over()
    {
        var m = new ScrubModel();
        m.Down(0, 0);
        m.Move(1_000, 100);                       // v = 0.5 * 10 = 5
        Assert.Equal(5.0, m.Velocity, 9);

        ScrubModel.Effects e = m.Down(500, 200);

        Assert.Equal(BeginAt(500), e);
        Assert.Equal(ScrubModel.State.Pressed, m.Current);
        Assert.Equal(0.0, m.Velocity);
        Assert.Equal(500L, m.PositionMs);
        Assert.Equal(200L, m.LastMoveAtMs);
        Assert.Equal(200L, m.LastSentAtMs);
    }

    // ── Move: state, coalescing ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_move_while_idle_is_inert_and_changes_nothing()
    {
        var m = new ScrubModel();

        ScrubModel.Effects e = m.Move(5_000, 100);

        Assert.Equal(Nothing, e);
        Assert.Equal(ScrubModel.State.Idle, m.Current);
        Assert.Equal(0L, m.PositionMs);
        Assert.Equal(0L, m.LastMoveAtMs);
        Assert.Equal(0.0, m.Velocity);
    }

    [Fact]
    public void The_first_move_promotes_Pressed_to_Scrubbing_even_when_the_coalescing_swallows_it()
    {
        var m = new ScrubModel();
        m.Down(0, 0);

        ScrubModel.Effects e = m.Move(100, 10);

        Assert.Equal(Nothing, e);                                   // 10 ms after the Down: inside the window
        Assert.Equal(ScrubModel.State.Scrubbing, m.Current);
        Assert.Equal(100L, m.PositionMs);
    }

    [Fact]
    public void A_swallowed_move_still_tracks_the_position_and_the_move_clock_but_not_the_sent_clock()
    {
        var m = new ScrubModel();
        m.Down(1_000, 0);

        ScrubModel.Effects e = m.Move(1_500, 49);

        Assert.Equal(Nothing, e);
        Assert.Equal(1_500L, m.PositionMs);
        Assert.Equal(49L, m.LastMoveAtMs);
        Assert.Equal(0L, m.LastSentAtMs);
        Assert.True(m.Velocity > 0.0);
    }

    [Fact]
    public void A_move_exactly_CoalesceMs_after_the_last_sent_one_goes_out()
    {
        Assert.Equal(50, ScrubModel.CoalesceMs);
        var m = new ScrubModel();
        m.Down(0, 1_000);

        Assert.Equal(Nothing, m.Move(100, 1_049));
        ScrubModel.Effects sent = m.Move(200, 1_050);
        AssertMove(sent, 200, m.Velocity);
        Assert.Equal(1_050L, m.LastSentAtMs);

        Assert.Equal(Nothing, m.Move(300, 1_099));                  // 49 ms after the last SENT one
        ScrubModel.Effects next = m.Move(400, 1_100);
        AssertMove(next, 400, m.Velocity);
        Assert.Equal(1_100L, m.LastSentAtMs);
    }

    [Fact]
    public void A_thousand_hertz_drag_reaches_the_audio_side_at_most_twenty_times_a_second()
    {
        var m = new ScrubModel();
        m.Down(0, 0);
        int sent = 0; long lastSentAt = 0;

        for (long t = 1; t <= 1_000; t++)
        {
            ScrubModel.Effects e = m.Move(t * 10, t);
            if (!e.Move) continue;
            sent++;
            Assert.True(t - lastSentAt >= ScrubModel.CoalesceMs, $"two moves went out {t - lastSentAt} ms apart");
            Assert.Equal(t * 10, e.PositionMs);                     // each sent move carries ITS position, not a stale one
            lastSentAt = t;
        }

        Assert.Equal(20, sent);                                     // 50, 100, … 1000
        Assert.Equal(10_000L, m.PositionMs);                        // the position kept tracking through the swallowed ones
    }

    // ── Move: velocity ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_velocity_is_smoothed_audio_ms_per_wall_ms_over_every_move_not_only_the_sent_ones()
    {
        var m = new ScrubModel();
        m.Down(1_000, 0);

        // 600 audio-ms in 100 wall-ms = 6; folded half-and-half into 0 → 3.
        AssertMove(m.Move(1_600, 100), 1_600, 3.0);

        // 600 in 20 = 30 → 0.5·3 + 0.5·30 = 16.5; swallowed (20 ms after the sent one) but it still folds in.
        Assert.Equal(Nothing, m.Move(2_200, 120));
        Assert.Equal(16.5, m.Velocity, 9);

        // The pointer rests: 0 in 10 → 8.25, still swallowed.
        Assert.Equal(Nothing, m.Move(2_200, 130));
        Assert.Equal(8.25, m.Velocity, 9);

        // 0 in 20 → 4.125, and now 50 ms after the last sent move: it goes out carrying the smoothed value.
        AssertMove(m.Move(2_200, 150), 2_200, 4.125);
    }

    [Fact]
    public void The_velocity_reaches_the_audio_side_unchanged_with_no_rate_clamp_here()
    {
        var m = new ScrubModel();
        m.Down(0, 0);

        ScrubModel.Effects e = m.Move(60_000, 50);                  // 60 000 audio-ms in 50 wall-ms = 1200 → 600 smoothed

        AssertMove(e, 60_000, 600.0);
        Assert.Equal(m.Velocity, e.Velocity);                       // dimensionless, passed through as the model holds it (V-PA9)
    }

    [Fact]
    public void A_backward_drag_has_a_negative_velocity()
    {
        var m = new ScrubModel();
        m.Down(10_000, 0);

        AssertMove(m.Move(9_000, 100), 9_000, -5.0);                // −1000 in 100 = −10 → −5
    }

    [Fact]
    public void The_wall_time_between_moves_is_floored_at_one_millisecond()
    {
        var m = new ScrubModel();
        m.Down(0, 500);

        m.Move(400, 500);                                           // the same frame stamp: dt = 1, not 0 (no division by zero)

        Assert.Equal(200.0, m.Velocity, 9);                         // 400 / 1 → 0.5 · 400
        Assert.True(double.IsFinite(m.Velocity));
    }

    [Fact]
    public void A_stationary_pointer_decays_the_velocity_toward_zero_without_sending()
    {
        var m = new ScrubModel();
        m.Down(0, 0);
        m.Move(1_000, 50);                                          // v = 0.5 · 20 = 10
        double previous = m.Velocity;

        for (long t = 51; t < 100; t++)
        {
            Assert.Equal(Nothing, m.Move(1_000, t));                // no motion, inside the window of the last send
            Assert.True(m.Velocity < previous);
            previous = m.Velocity;
        }
        Assert.True(previous < 1e-6);
    }

    // ── Up ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Up_commits_once_at_the_release_position_and_returns_to_Idle()
    {
        var m = new ScrubModel();
        m.Down(0, 0);
        m.Move(5_000, 100);

        ScrubModel.Effects e = m.Up(7_777, 150);

        Assert.Equal(CommitAt(7_777), e);
        Assert.Equal(ScrubModel.State.Idle, m.Current);
        Assert.Equal(7_777L, m.PositionMs);

        Assert.Equal(Nothing, m.Up(9_999, 160));                    // exactly once
        Assert.Equal(ScrubModel.State.Idle, m.Current);
        Assert.Equal(7_777L, m.PositionMs);
    }

    [Fact]
    public void A_click_without_any_move_commits_at_the_release_position()
    {
        var m = new ScrubModel();
        m.Down(2_000, 0);

        Assert.Equal(CommitAt(2_500), m.Up(2_500, 80));             // Down → Up commits; the release position wins over the press
        Assert.Equal(ScrubModel.State.Idle, m.Current);
    }

    [Fact]
    public void Up_while_idle_is_inert()
    {
        var m = new ScrubModel();

        Assert.Equal(Nothing, m.Up(1_000, 0));
        Assert.Equal(ScrubModel.State.Idle, m.Current);
        Assert.Equal(0L, m.PositionMs);
    }

    [Fact]
    public void After_Up_further_moves_are_inert_until_the_next_Down()
    {
        var m = new ScrubModel();
        m.Down(0, 0);
        m.Up(100, 10);

        Assert.Equal(Nothing, m.Move(5_000, 500));
        Assert.Equal(ScrubModel.State.Idle, m.Current);
        Assert.Equal(100L, m.PositionMs);

        Assert.Equal(BeginAt(300), m.Down(300, 600));
        Assert.Equal(ScrubModel.State.Pressed, m.Current);
    }

    [Fact]
    public void Up_leaves_Audible_alone_because_the_reducer_reads_it_after_Up()
    {
        var m = new ScrubModel();
        m.Down(0, 0);
        m.Audible = true;                                           // the reducer's ScrubBegin arm sets this to fx.ScrubBegin

        m.Up(1_000, 50);

        Assert.True(m.Audible);                                     // the ScrubEnd arm: audible ⇒ fx.ScrubEnd instead of fx.Seek
        Assert.Equal(ScrubModel.State.Idle, m.Current);
    }

    // ── Cancel ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cancel_from_Pressed_reports_a_cancel_at_the_press_position_and_goes_idle()
    {
        var m = new ScrubModel();
        m.Down(3_000, 0);
        m.Audible = true;

        ScrubModel.Effects e = m.Cancel();

        Assert.Equal(CancelAt(3_000), e);
        Assert.Equal(ScrubModel.State.Idle, m.Current);
        Assert.False(m.Audible);
    }

    [Fact]
    public void Cancel_from_Scrubbing_reports_a_cancel_at_the_latest_position_and_goes_idle()
    {
        var m = new ScrubModel();
        m.Down(0, 0);
        m.Move(4_000, 10);                                          // swallowed, but the position still tracks
        m.Move(4_500, 60);
        m.Audible = true;
        Assert.Equal(ScrubModel.State.Scrubbing, m.Current);

        ScrubModel.Effects e = m.Cancel();

        Assert.Equal(CancelAt(4_500), e);
        Assert.Equal(ScrubModel.State.Idle, m.Current);
        Assert.False(m.Audible);
    }

    [Fact]
    public void Cancel_while_idle_is_inert_but_still_clears_Audible()
    {
        var m = new ScrubModel();
        m.Audible = true;

        Assert.Equal(Nothing, m.Cancel());

        Assert.False(m.Audible);
        Assert.Equal(ScrubModel.State.Idle, m.Current);
    }

    [Fact]
    public void Cancel_cancels_exactly_once_and_a_cancelled_gesture_commits_nothing()
    {
        var m = new ScrubModel();
        m.Down(1_000, 0);
        m.Move(2_000, 100);

        Assert.True(m.Cancel().Cancel);
        Assert.Equal(Nothing, m.Cancel());                          // idempotent — the unmount cleanup may run after an Escape
        Assert.Equal(Nothing, m.Up(2_000, 200));                    // the pointer-up that follows an Escape commits nothing
        Assert.Equal(Nothing, m.Move(3_000, 300));
        Assert.Equal(ScrubModel.State.Idle, m.Current);
    }

    // ── whole gestures ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_second_gesture_after_a_commit_starts_clean()
    {
        var m = new ScrubModel();
        m.Down(0, 0);
        m.Move(60_000, 50);                                         // v = 600
        m.Up(60_000, 60);

        m.Down(100, 1_000);
        Assert.Equal(0.0, m.Velocity);
        AssertMove(m.Move(200, 1_100), 200, 0.5);                   // 100 in 100 = 1 → 0.5; nothing left over from the last drag
    }

    [Fact]
    public void A_random_drive_agrees_with_a_two_state_oracle()
    {
        var m = new ScrubModel();
        bool live = false; long now = 0, lastSent = 0; uint seed = 12_345;

        for (int i = 0; i < 20_000; i++)
        {
            seed = seed * 1_664_525u + 1_013_904_223u;
            now += (seed >> 8) % 40;
            long ms = (seed >> 4) % 300_000;
            int op = (int)((seed >> 24) % 8);                       // Down, Up, Cancel 1/8 each; Move 5/8
            ScrubModel.Effects e;

            switch (op)
            {
                case 0:
                    e = m.Down(ms, now); live = true; lastSent = now;
                    Assert.Equal(BeginAt(ms), e);
                    break;
                case 1:
                    e = m.Up(ms, now);
                    Assert.Equal(live ? CommitAt(ms) : Nothing, e);
                    live = false;
                    break;
                case 2:
                    e = m.Cancel();
                    Assert.Equal(live, e.Cancel);
                    Assert.False(e.Begin || e.Move || e.Commit);
                    live = false;
                    break;
                default:
                    e = m.Move(ms, now);
                    if (!live) { Assert.Equal(Nothing, e); break; }
                    Assert.Equal(now - lastSent >= ScrubModel.CoalesceMs, e.Move);
                    if (e.Move) { Assert.Equal(ms, e.PositionMs); Assert.Equal(m.Velocity, e.Velocity); lastSent = now; }
                    else Assert.Equal(Nothing, e);
                    Assert.Equal(ScrubModel.State.Scrubbing, m.Current);
                    Assert.True(double.IsFinite(m.Velocity));
                    break;
            }

            Assert.True(Flags(e) <= 1, "an effect never asks for two things at once");
            Assert.Equal(live, m.Current != ScrubModel.State.Idle);
        }
    }

    // ── allocation ───────────────────────────────────────────────────────────────────────────────────────────────────

    static long Drag(ref ScrubModel m, long t0)
    {
        long sum = m.Down(1_000, t0).PositionMs;
        for (int i = 1; i <= 200; i++)
        {
            ScrubModel.Effects e = m.Move(1_000 + i * 7, t0 + i * 5);
            if (e.Move) sum += (long)e.Velocity;
        }
        sum += m.Up(2_400, t0 + 1_100).PositionMs;
        sum += m.Down(0, t0 + 1_200).PositionMs;
        sum += m.Cancel().PositionMs;
        return sum;
    }

    [Fact]
    public void A_warm_gesture_allocates_nothing()
    {
        var m = new ScrubModel();
        long sink = 0;
        for (int i = 0; i < 3; i++) sink += Drag(ref m, i * 2_000L);                  // warm-up: the JIT, nothing else to grow

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) sink += Drag(ref m, 10_000L + i * 2_000L);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
        Assert.NotEqual(0L, sink);                                                    // the loop was not optimised away
    }
}
