// ── Wavee.Tests/KeyboardScrubLadderTests.cs — playback smoothness WP-3a (D4; V-PA22) ────────────────────────────────
//
// `KeyboardScrubLadder` (Playback/Playback.Scrub.cs) steps a VISUAL target up a ladder on a key press or OS repeat —
// 5 s for the first 0.5 s held, then 15 s, then 30 s; Shift = 1 s — at most once per 150 ms (an OS repeat runs at ~30 Hz),
// clamps it to [minMs, durationMs] (minMs = the live DVR window's start), and commits it from `Tick` once no press has
// arrived for 250 ms (a key-up never arrives). Pure: no engine, no clock — every call hands in `nowMs`.

using Xunit;

namespace Wavee.Tests;

public class KeyboardScrubLadderTests
{
    const int Long = int.MaxValue;                      // a duration that never clamps in the ladder tests

    /// <summary>Holds a key on a fresh ladder with EVERY press accepted — equal gaps, each inside
    /// [MinStepIntervalMs, GraceMs] — the last press landing exactly <paramref name="heldMs"/> after the first, and
    /// returns the target change that last press made.</summary>
    static int DeltaAtHold(long heldMs, int direction = 1, bool fine = false)
    {
        var ladder = new KeyboardScrubLadder();
        int before = 1_000_000;                                  // the position the fresh ladder starts from
        int target = ladder.Step(direction, fine, before, Long, 0);
        if (heldMs > 0)
        {
            long n = (heldMs + KeyboardScrubLadder.GraceMs - 1) / KeyboardScrubLadder.GraceMs;   // fewest presses keeping every gap ≤ GraceMs
            Assert.True(heldMs / n >= KeyboardScrubLadder.MinStepIntervalMs, "the schedule must not trip the step-rate cap");
            for (long i = 1; i <= n; i++)
            {
                before = target;
                target = ladder.Step(direction, fine, 1_000_000, Long, heldMs * i / n);
            }
        }
        return target - before;
    }

    // ── the ladder ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_spec_numbers_are_5_15_30_seconds_with_1_second_fine_a_150_ms_step_cap_and_a_250_ms_grace()
    {
        Assert.Equal(5_000, KeyboardScrubLadder.StepMs);
        Assert.Equal(15_000, KeyboardScrubLadder.MidMs);
        Assert.Equal(30_000, KeyboardScrubLadder.FastMs);
        Assert.Equal(1_000, KeyboardScrubLadder.FineMs);
        Assert.Equal(500, KeyboardScrubLadder.MidAfterMs);
        Assert.Equal(1_500, KeyboardScrubLadder.FastAfterMs);
        Assert.Equal(250, KeyboardScrubLadder.GraceMs);
        Assert.Equal(150, KeyboardScrubLadder.MinStepIntervalMs);
    }

    [Fact]
    public void A_first_press_steps_five_seconds_from_the_given_position_and_returns_the_new_target()
    {
        var ladder = new KeyboardScrubLadder();

        int forward = ladder.Step(+1, false, 60_000, 200_000, 1_000);

        Assert.Equal(65_000, forward);
        Assert.Equal(65_000, ladder.TargetMs);
        Assert.True(ladder.Active);
        Assert.Equal(1_000L, ladder.HeldSinceMs);
        Assert.Equal(1_000L, ladder.LastKeyMs);
        Assert.Equal(1_000L, ladder.LastStepMs);

        Assert.Equal(55_000, new KeyboardScrubLadder().Step(-1, false, 60_000, 200_000, 1_000));
    }

    [Theory]
    [InlineData(0L, 5_000)]
    [InlineData(250L, 5_000)]
    [InlineData(499L, 5_000)]                           // still inside the first half second
    [InlineData(500L, 15_000)]
    [InlineData(1_499L, 15_000)]
    [InlineData(1_500L, 30_000)]
    [InlineData(4_000L, 30_000)]                        // and it stays at the top rung
    public void The_step_is_5_then_15_then_30_seconds_by_how_long_the_key_has_been_held(long heldMs, int expectedStep)
    {
        Assert.Equal(expectedStep, DeltaAtHold(heldMs, direction: +1));
        Assert.Equal(-expectedStep, DeltaAtHold(heldMs, direction: -1));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(700L)]
    [InlineData(2_000L)]
    public void Shift_is_one_second_whatever_the_hold_time(long heldMs)
    {
        Assert.Equal(1_000, DeltaAtHold(heldMs, direction: +1, fine: true));
        Assert.Equal(-1_000, DeltaAtHold(heldMs, direction: -1, fine: true));
    }

    [Fact]
    public void Shift_can_be_pressed_and_released_mid_hold_without_losing_the_ladder()
    {
        var ladder = new KeyboardScrubLadder();
        int target = 0;
        for (long t = 0; t <= 1_600; t += 200) target = ladder.Step(+1, false, 0, Long, t);    // held 1.6 s: top rung
        int atTop = target;
        Assert.Equal(120_000, atTop);                                                          // 3 × 5 s (0, 200, 400) + 5 × 15 s (600 … 1400) + 1 × 30 s (1600)

        target = ladder.Step(+1, true, 0, Long, 1_800);
        Assert.Equal(atTop + 1_000, target);                                                   // Shift: 1 s

        target = ladder.Step(+1, false, 0, Long, 2_000);
        Assert.Equal(atTop + 1_000 + 30_000, target);                                          // Shift released: straight back to 30 s
    }

    [Fact]
    public void Reversing_direction_inside_the_grace_continues_the_same_ladder()
    {
        var ladder = new KeyboardScrubLadder();

        Assert.Equal(15_000, ladder.Step(+1, false, 10_000, Long, 0));
        Assert.Equal(10_000, ladder.Step(-1, false, 10_000, Long, 200));
        Assert.Equal(5_000, ladder.Step(-1, false, 10_000, Long, 400));
    }

    // ── the step-rate cap ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_press_inside_the_step_interval_refreshes_the_grace_but_does_not_move_the_target()
    {
        var ladder = new KeyboardScrubLadder();
        Assert.Equal(5_000, ladder.Step(+1, false, 0, Long, 0));

        Assert.Equal(5_000, ladder.Step(+1, false, 0, Long, 100));                                  // 100 ms after the accepted step: swallowed
        Assert.Equal(5_000, ladder.TargetMs);
        Assert.Equal(100L, ladder.LastKeyMs);                                                       // the hold and the grace stay alive …
        Assert.Equal(0L, ladder.LastStepMs);                                                        // … but the step clock does not
        Assert.Equal(0L, ladder.HeldSinceMs);

        Assert.Equal(-1, ladder.Tick(100 + 249));                                                   // the grace runs from the swallowed press
        Assert.Equal(5_000, ladder.Tick(100 + 250));
    }

    [Fact]
    public void A_press_exactly_MinStepIntervalMs_after_the_last_accepted_step_steps()
    {
        var ladder = new KeyboardScrubLadder();
        ladder.Step(+1, false, 0, Long, 0);

        Assert.Equal(5_000, ladder.Step(+1, false, 0, Long, 149));                                  // one ms short: swallowed
        Assert.Equal(10_000, ladder.Step(+1, false, 0, Long, 150));                                 // exactly the interval: accepted
        Assert.Equal(150L, ladder.LastStepMs);
    }

    [Fact]
    public void The_cap_runs_from_the_last_ACCEPTED_step_not_from_the_last_press()
    {
        var ladder = new KeyboardScrubLadder();
        ladder.Step(+1, false, 0, Long, 0);                                                         // accepted

        // Presses every 100 ms: if the cap ran from the last press every one would be swallowed after the first.
        Assert.Equal(5_000, ladder.Step(+1, false, 0, Long, 100));                                  // swallowed (100 after the step)
        Assert.Equal(10_000, ladder.Step(+1, false, 0, Long, 200));                                 // 200 after the step: accepted
        Assert.Equal(10_000, ladder.Step(+1, false, 0, Long, 300));                                 // 100 after that: swallowed
        Assert.Equal(15_000, ladder.Step(+1, false, 0, Long, 400));                                 // accepted
    }

    [Fact]
    public void The_first_press_of_a_fresh_ladder_always_steps_even_right_after_the_clock_epoch()
    {
        // A default ladder has LastStepMs = 0: a first press at t = 10 must not be read as "10 ms after a step".
        var ladder = new KeyboardScrubLadder();

        Assert.Equal(15_000, ladder.Step(+1, false, 10_000, Long, 10));
        Assert.Equal(1_000, new KeyboardScrubLadder().Step(+1, true, 0, Long, 3));
    }

    [Fact]
    public void The_cap_applies_to_Shift_too()
    {
        var ladder = new KeyboardScrubLadder();
        int target = 0;
        for (long t = 0; t <= 990; t += 33) target = ladder.Step(+1, true, 0, Long, t);             // a 1 s Shift hold at ~30 Hz

        Assert.Equal(7_000, target);                                                                // accepted at 0, 165, 330 … 990 (7 steps of 1 s)
    }

    [Fact]
    public void Thirty_hertz_repeats_advance_at_most_one_step_per_150_ms()
    {
        var ladder = new KeyboardScrubLadder();
        int accepted = 0, previous = 0; long lastAcceptedAt = -1, lastPressAt = 0;

        for (long t = 0; t <= 2_970; t += 33)                                                       // 91 presses, 33 ms apart (~30 Hz)
        {
            int target = ladder.Step(+1, false, 0, 100_000_000, t);
            lastPressAt = t;
            if (target != previous)
            {
                if (lastAcceptedAt >= 0) Assert.True(t - lastAcceptedAt >= KeyboardScrubLadder.MinStepIntervalMs, $"two steps landed {t - lastAcceptedAt} ms apart");
                accepted++; lastAcceptedAt = t; previous = target;
            }
            Assert.Equal(lastPressAt, ladder.LastKeyMs);                                            // every press — swallowed or not — keeps the grace alive
        }

        Assert.Equal(19, accepted);                                                                 // t = 0, 165, 330 … 2970: five repeats (165 ms) per step
        Assert.Equal(previous, ladder.TargetMs);
    }

    // ── clamping ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_target_clamps_to_the_start_and_to_the_duration()
    {
        Assert.Equal(12_000, new KeyboardScrubLadder().Step(+1, false, 10_000, 12_000, 0));         // 15 000 would overrun
        Assert.Equal(0, new KeyboardScrubLadder().Step(-1, false, 3_000, 100_000, 0));              // −2 000 would underrun
        Assert.Equal(12_000, new KeyboardScrubLadder().Step(-1, false, 20_000, 12_000, 0));         // a stale position past the end still clamps
    }

    [Fact]
    public void A_clamped_target_stays_put_while_the_key_keeps_repeating()
    {
        var ladder = new KeyboardScrubLadder();

        Assert.Equal(12_000, ladder.Step(+1, false, 10_000, 12_000, 0));
        Assert.Equal(12_000, ladder.Step(+1, false, 10_000, 12_000, 200));
        Assert.Equal(7_000, ladder.Step(-1, false, 10_000, 12_000, 400));                           // and it comes back off the bound
    }

    [Fact]
    public void A_non_positive_duration_clamps_the_target_to_zero_instead_of_throwing()
    {
        Assert.Equal(0, new KeyboardScrubLadder().Step(+1, false, 5_000, 0, 0));
        Assert.Equal(0, new KeyboardScrubLadder().Step(+1, false, 5_000, -1, 0));
        Assert.Equal(0, new KeyboardScrubLadder().Step(-1, false, 5_000, int.MinValue, 0));
    }

    [Fact]
    public void The_arithmetic_is_done_in_long_so_a_target_near_int_max_does_not_overflow()
    {
        Assert.Equal(int.MaxValue, new KeyboardScrubLadder().Step(+1, false, int.MaxValue - 1_000, int.MaxValue, 0));
    }

    // ── minMs: the live DVR window ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_target_never_leaves_the_seekable_window_minMs_to_durationMs()
    {
        var ladder = new KeyboardScrubLadder();

        Assert.Equal(53_000, ladder.Step(-1, false, 58_000, 100_000, 0, minMs: 50_000));            // 5 s back, inside the window
        Assert.Equal(50_000, ladder.Step(-1, false, 58_000, 100_000, 200, minMs: 50_000));          // 5 s more would leave it (48 000): pinned to the start
        Assert.Equal(50_000, ladder.Step(-1, false, 58_000, 100_000, 400, minMs: 50_000));          // and it stays there
        Assert.Equal(100_000, new KeyboardScrubLadder().Step(+1, false, 95_000, 100_000, 0, minMs: 50_000));   // the end still clamps
    }

    [Fact]
    public void A_position_below_the_window_is_pulled_up_to_its_start()
    {
        // The window slid forward since the last report: forward from before its start lands on the start, not 5 s past the stale position.
        Assert.Equal(50_000, new KeyboardScrubLadder().Step(+1, false, 10_000, 100_000, 0, minMs: 50_000));
    }

    [Fact]
    public void A_non_positive_duration_pins_the_target_to_minMs_not_to_zero()
    {
        Assert.Equal(7_000, new KeyboardScrubLadder().Step(+1, false, 5_000, 0, 0, minMs: 7_000));
        Assert.Equal(7_000, new KeyboardScrubLadder().Step(-1, false, 5_000, -5, 0, minMs: 7_000));
        Assert.Equal(0, new KeyboardScrubLadder().Step(+1, false, 5_000, 0, 0));                    // the default minMs is the old behaviour
    }

    [Fact]
    public void A_window_that_ends_before_it_starts_pins_to_minMs_instead_of_throwing()
    {
        Assert.Equal(10_000, new KeyboardScrubLadder().Step(+1, false, 5_000, 4_000, 0, minMs: 10_000));
    }

    [Fact]
    public void A_negative_minMs_counts_as_zero()
    {
        Assert.Equal(0, new KeyboardScrubLadder().Step(-1, false, 3_000, 100_000, 0, minMs: -5_000));
    }

    // ── Tick: the commit ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tick_commits_only_after_the_grace_has_elapsed_and_exactly_once()
    {
        var ladder = new KeyboardScrubLadder();
        ladder.Step(+1, false, 10_000, Long, 1_000);                                                // target 15 000

        Assert.Equal(-1, ladder.Tick(1_000));
        Assert.Equal(-1, ladder.Tick(1_249));                                                       // 249 ms of silence: not yet
        Assert.Equal(15_000, ladder.Tick(1_250));                                                   // exactly GraceMs: commit
        Assert.False(ladder.Active);

        Assert.Equal(-1, ladder.Tick(1_300));                                                       // exactly once
        Assert.Equal(-1, ladder.Tick(60_000));
    }

    [Fact]
    public void Tick_on_a_ladder_that_never_started_is_silent()
    {
        var ladder = new KeyboardScrubLadder();

        Assert.Equal(-1, ladder.Tick(0));
        Assert.Equal(-1, ladder.Tick(1_000_000));
        Assert.False(ladder.Active);
    }

    [Fact]
    public void A_committed_target_of_zero_is_distinguishable_from_no_commit()
    {
        var ladder = new KeyboardScrubLadder();
        ladder.Step(-1, false, 2_000, 100_000, 0);                                                  // clamps to 0

        Assert.Equal(0, ladder.Tick(250));                                                          // 0 is a real target; −1 is "none"
    }

    [Fact]
    public void Tick_stays_silent_while_presses_keep_arriving_even_swallowed_ones()
    {
        var ladder = new KeyboardScrubLadder();

        for (long t = 0; t <= 600; t += 100)                                                        // 100 ms apart: every other press is inside the step cap
        {
            ladder.Step(+1, false, 0, Long, t);
            Assert.Equal(-1, ladder.Tick(t + 50));                                                  // 50 ms after each press
            Assert.Equal(-1, ladder.Tick(t + 249));
        }
        Assert.True(ladder.Active);
    }

    // ── re-press: continue or restart ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_re_press_within_the_grace_continues_the_ladder_from_its_own_target_not_the_given_position()
    {
        var ladder = new KeyboardScrubLadder();

        Assert.Equal(15_000, ladder.Step(+1, false, 10_000, Long, 0));
        Assert.Equal(20_000, ladder.Step(+1, false, 10_000, Long, 200));                            // the position has not moved yet: the ladder ignores it
        Assert.Equal(0L, ladder.HeldSinceMs);                                                       // and keeps its own hold clock
        Assert.Equal(25_000, ladder.Step(+1, false, 10_000, Long, 450));                            // exactly GraceMs after the last press still continues
    }

    [Fact]
    public void A_press_more_than_the_grace_after_the_last_one_starts_a_fresh_ladder_from_the_given_position()
    {
        var ladder = new KeyboardScrubLadder();
        ladder.Step(+1, false, 10_000, Long, 0);
        ladder.Step(+1, false, 10_000, Long, 200);                                                  // held-since 0, target 20 000

        int target = ladder.Step(+1, false, 12_345, Long, 451);                                     // 251 ms of silence, and no Tick ran

        Assert.Equal(17_345, target);                                                               // fresh: from 12 345, first rung
        Assert.Equal(451L, ladder.HeldSinceMs);
        Assert.Equal(451L, ladder.LastStepMs);
    }

    [Fact]
    public void A_press_after_a_commit_starts_a_fresh_ladder_at_the_first_rung()
    {
        var ladder = new KeyboardScrubLadder();
        for (long t = 0; t <= 2_000; t += 200) ladder.Step(+1, false, 0, Long, t);                  // up to the top rung
        int committed = ladder.Tick(2_250);
        Assert.True(committed > 0);

        int target = ladder.Step(+1, false, committed, Long, 2_260);                                // 10 ms after the commit, well inside the old grace

        Assert.Equal(committed + 5_000, target);                                                    // Tick ended the old ladder: back to 5 s
        Assert.Equal(2_260L, ladder.HeldSinceMs);
    }

    // ── a held key, end to end ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_held_key_at_the_OS_repeat_rate_accumulates_up_the_ladder_and_commits_once_after_release()
    {
        // 61 presses 33 ms apart (t = 0 … 1980), the bar's 50 ms tick running throughout. The 150 ms step cap accepts every
        // fifth press (165 ms apart: t = 165·j, j = 0…12 — the last accepted press IS the last press); the hold clock is the
        // accepted press's own time, since the first press is at 0:
        //   held < 500    → j = 0…3  (0, 165, 330, 495)           4 × 5 s  =  20 000
        //   held < 1500   → j = 4…9  (660 … 1485)                 6 × 15 s =  90 000
        //   held ≥ 1500   → j = 10…12 (1650, 1815, 1980)          3 × 30 s =  90 000       ⇒ 200 000 from 0
        var ladder = new KeyboardScrubLadder();
        int commits = 0, committed = -1; long committedAt = -1; int visual = 0;

        for (long t = 0; t <= 3_000; t++)
        {
            if (t <= 1_980 && t % 33 == 0) visual = ladder.Step(+1, false, 0, 10_000_000, t);
            if (t % 50 == 0)
            {
                int c = ladder.Tick(t);
                if (c >= 0) { commits++; committed = c; committedAt = t; }
            }
        }

        Assert.Equal(200_000, visual);
        Assert.Equal(1, commits);
        Assert.Equal(200_000, committed);
        Assert.Equal(2_250L, committedAt);                                                          // the first 50 ms tick ≥ 1980 + 250 (the last press, accepted or not, restarts the grace)
    }

    // ── allocation ───────────────────────────────────────────────────────────────────────────────────────────────────

    static long Hold(ref KeyboardScrubLadder ladder, long t0)
    {
        long sum = 0;
        for (int i = 0; i < 60; i++)
        {
            sum += ladder.Step(i % 7 == 0 ? -1 : +1, i % 11 == 0, 100_000, 3_600_000, t0 + i * 33L, i % 3 == 0 ? 20_000 : 0);
            sum += ladder.Tick(t0 + i * 33L + 10);
        }
        sum += ladder.Tick(t0 + 5_000);
        return sum;
    }

    [Fact]
    public void A_warm_hold_allocates_nothing()
    {
        var ladder = new KeyboardScrubLadder();
        long sink = 0;
        for (int i = 0; i < 3; i++) sink += Hold(ref ladder, i * 10_000L);                          // warm-up: the JIT, nothing else to grow

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) sink += Hold(ref ladder, 100_000L + i * 10_000L);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
        Assert.NotEqual(0L, sink);                                                                  // the loop was not optimised away
    }
}
