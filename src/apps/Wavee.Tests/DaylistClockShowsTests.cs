// ── Wavee.Tests/DaylistClockShowsTests.cs — what each daylist clock mode shows, and what it costs (#185) ─────────────
//
// Pure: a mode (and the window's phase) goes in; which parts are on screen, whether a 1-Hz tick runs and when the
// one-shot at the window's end fires come out. No clock, no signals, no engine loop. The flip cell's reduced-motion
// choice is pinned here too: it is a value (a transition or none), not a render.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DaylistClockShowsTests
{
    static readonly DaylistCountdown.Phase[] AllPhases =
        [DaylistCountdown.Phase.Idle, DaylistCountdown.Phase.Counting, DaylistCountdown.Phase.Rolling];

    // ── the persisted int ────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, DaylistClockMode.Both)]
    [InlineData(1, DaylistClockMode.UpdateTime)]
    [InlineData(2, DaylistClockMode.Countdown)]
    [InlineData(3, DaylistClockMode.Both)]       // a newer build's mode this one does not have
    [InlineData(-1, DaylistClockMode.Both)]      // a hand-edited value
    [InlineData(int.MaxValue, DaylistClockMode.Both)]
    public void A_stored_int_reads_as_a_real_mode_never_an_empty_clock(int stored, DaylistClockMode expected)
        => Assert.Equal(expected, DaylistClockShows.FromSetting(stored));

    [Fact]
    public void The_stored_value_is_the_declaration_order_and_the_combo_index()
    {
        Assert.Equal(0, (int)DaylistClockMode.Both);
        Assert.Equal(1, (int)DaylistClockMode.UpdateTime);
        Assert.Equal(2, (int)DaylistClockMode.Countdown);
        Assert.Equal(3, DaylistClockShows.ModeCount);
    }

    [Fact]
    public void The_setting_defaults_to_both_which_is_what_every_earlier_build_showed()
    {
        Assert.Equal("appearance.daylistClock", Platform.Keys.DaylistClock.Name);
        Assert.Equal(DaylistClockMode.Both, DaylistClockShows.FromSetting(Platform.Keys.DaylistClock.Default));
    }

    // ── what each mode shows ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DaylistClockMode.Both, true, true)]
    [InlineData(DaylistClockMode.UpdateTime, false, true)]
    [InlineData(DaylistClockMode.Countdown, true, false)]
    public void Each_mode_shows_its_parts(DaylistClockMode mode, bool countdown, bool updateTime)
    {
        Assert.Equal(countdown, DaylistClockShows.Countdown(mode));
        Assert.Equal(updateTime, DaylistClockShows.UpdateTime(mode));
    }

    [Fact]
    public void Every_mode_shows_something()
    {
        foreach (var mode in new[] { DaylistClockMode.Both, DaylistClockMode.UpdateTime, DaylistClockMode.Countdown })
            Assert.True(DaylistClockShows.Countdown(mode) || DaylistClockShows.UpdateTime(mode), $"{mode} shows nothing.");
    }

    // ── what each mode costs ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DaylistClockMode.Both)]
    [InlineData(DaylistClockMode.Countdown)]
    public void A_countdown_ticks_once_a_second_only_while_the_window_is_open(DaylistClockMode mode)
    {
        Assert.True(DaylistClockShows.SecondTick(mode, DaylistCountdown.Phase.Counting));
        Assert.False(DaylistClockShows.SecondTick(mode, DaylistCountdown.Phase.Rolling));
        Assert.False(DaylistClockShows.SecondTick(mode, DaylistCountdown.Phase.Idle));
    }

    [Fact]
    public void Update_time_only_never_ticks_once_a_second()
    {
        // The whole point of the mode: nothing on it changes per second, so no 1-Hz timer wakes the app for it.
        foreach (var phase in AllPhases)
            Assert.False(DaylistClockShows.SecondTick(DaylistClockMode.UpdateTime, phase));
    }

    [Fact]
    public void The_one_shot_lands_just_past_the_window_so_the_rolling_face_shows_on_time()
    {
        long expiresAt = 10_000_000, now = 9_000_000;
        float delay = DaylistClockShows.ExpiryDelayMs(expiresAt, now);
        Assert.Equal(1_000_000f + DaylistClockShows.ExpirySlackMs, delay);
        // Fired at that delay, the phase the clock reads is Rolling — never a hair before the boundary.
        Assert.Equal(DaylistCountdown.Phase.Rolling, DaylistCountdown.PhaseOf(expiresAt, now + (long)delay));
        Assert.Equal(DaylistCountdown.Phase.Counting, DaylistCountdown.PhaseOf(expiresAt, now + (long)delay - DaylistClockShows.ExpirySlackMs - 1));
    }

    [Fact]
    public void A_window_already_past_fires_after_just_the_slack()
        => Assert.Equal(DaylistClockShows.ExpirySlackMs, DaylistClockShows.ExpiryDelayMs(1_000, 5_000));

    [Fact]
    public void A_far_window_is_capped_at_a_real_timers_ceiling()
        => Assert.Equal(int.MaxValue - 1, DaylistClockShows.ExpiryDelayMs(long.MaxValue / 2, 0));

    // ── the flip cell under reduced motion ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reduced_motion_swaps_a_flip_digit_in_place()
        => Assert.Null(Controls.FlipTransition(Controls.FlipHeroRowHeight, reduced: true));

    [Theory]
    [InlineData(Controls.FlipHeroRowHeight)]
    [InlineData(Controls.FlipCompactRowHeight)]
    public void Full_motion_slides_and_fades_a_flip_digit_by_its_row(float rowHeight)
    {
        var t = Controls.FlipTransition(rowHeight, reduced: false);
        Assert.NotNull(t);
        var spec = t.Value;
        Assert.True(spec.Enter.Active && spec.Exit.Active);
        Assert.Equal(rowHeight * 0.6f, spec.Enter.Dy);      // rises in from below
        Assert.Equal(-rowHeight * 0.6f, spec.Exit.Dy);      // leaves upwards
        Assert.Equal(0f, spec.Enter.Opacity);
        Assert.Equal(0f, spec.Exit.Opacity);
    }
}
