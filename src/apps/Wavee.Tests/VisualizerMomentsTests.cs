// ── Wavee.Tests/VisualizerMomentsTests.cs — "change with the music" (Shell/Visualizer.Moments.cs) ───────────────────
//
// Pure: `Visualizer.Moments.Schedule` is a value the clock owns; the tests drive it with bars and downbeat edges directly.
// Pins viz-app-plan §3.5: a moment every 8 bars ON a crossed downbeat, never on a seek, never while off; a forced moment
// lands at once and the scheduled one inside the same phrase is skipped; the fade lengths (900 ms, 1.4 s calm).

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class VisualizerMomentsTests
{
    const long NoForce = 0;

    [Fact]
    public void Scheduled_moments_land_on_every_eighth_crossed_downbeat()
    {
        Assert.True(Visualizer.Moments.Scheduled(8, downbeatEdge: true));
        Assert.True(Visualizer.Moments.Scheduled(16, downbeatEdge: true));
        Assert.False(Visualizer.Moments.Scheduled(8, downbeatEdge: false));   // a seek onto bar 8 is not a downbeat
        Assert.False(Visualizer.Moments.Scheduled(7, downbeatEdge: true));
        Assert.False(Visualizer.Moments.Scheduled(0, downbeatEdge: true));    // the song's start: the palette just arrived

        var s = new Visualizer.Moments.Schedule();
        int fired = 0;
        for (int bar = 1; bar <= 40; bar++)
            if (s.Step(bar, downbeatEdge: true, enabled: true, NoForce)) { fired++; Assert.Equal(0, bar % 8); }
        Assert.Equal(5, fired);                                               // 8, 16, 24, 32, 40
        Assert.False(s.Step(48, downbeatEdge: false, enabled: true, NoForce)); // a tick between edges
    }

    [Fact]
    public void Off_means_never_and_drops_a_pending_force()
    {
        var s = new Visualizer.Moments.Schedule();
        Assert.False(s.Step(8, true, enabled: false, NoForce));
        Assert.False(s.Step(9, false, enabled: false, 1));                    // a force while off …
        Assert.False(s.Step(10, false, enabled: true, 1));                    // … is gone when it is switched back on
        Assert.True(s.Step(16, true, enabled: true, 1));
    }

    [Fact]
    public void A_force_lands_once_and_a_stale_one_is_ignored()
    {
        var s = new Visualizer.Moments.Schedule();
        Assert.False(s.Step(3, false, true, forcedSequence: 5));              // a request made before the first step is history
        Assert.True(s.Step(4, false, true, forcedSequence: 6));               // a NEW request lands at once, off the bar grid
        Assert.False(s.Step(4, false, true, forcedSequence: 6));              // and only once
        Assert.True(s.Step(5, false, true, forcedSequence: 8));               // two requests between ticks are one moment
    }

    [Fact]
    public void A_forced_moment_skips_the_scheduled_one_in_the_same_phrase()
    {
        var s = new Visualizer.Moments.Schedule();
        s.Step(12, false, true, NoForce);
        Assert.True(s.Step(13, false, true, 1));                              // Verse's chorus entry at bar 13
        Assert.False(s.Step(16, true, true, 1));                              // 3 bars later: skipped
        Assert.True(s.Step(24, true, true, 1));                               // the next phrase turns as usual

        // a new track forgets the last one's moment
        var t = new Visualizer.Moments.Schedule();
        t.Step(12, false, true, NoForce);
        Assert.True(t.Step(13, false, true, 1));
        t.Reset();
        Assert.True(t.Step(16, true, true, 1));
    }

    [Fact]
    public void A_scheduled_bar_re_crossed_after_a_backward_seek_turns_again()
    {
        var s = new Visualizer.Moments.Schedule();
        Assert.True(s.Step(8, true, true, NoForce));                          // bar 8's downbeat
        Assert.False(s.Step(7, false, true, NoForce));                        // a seek back into bar 7 …
        Assert.True(s.Step(8, true, true, NoForce));                          // … and playing on crosses bar 8 again

        // only a FORCED moment holds the next one back, and a seek back before it forgets it
        var f = new Visualizer.Moments.Schedule();
        f.Step(12, false, true, NoForce);
        Assert.True(f.Step(13, false, true, 1));                              // forced at bar 13
        Assert.False(f.Step(10, false, true, 1));                             // rewound before the forced bar
        Assert.True(f.Step(16, true, true, 1));                               // bar 16 is no longer "3 bars after it"
    }

    [Fact]
    public void Force_bumps_the_shared_sequence()
    {
        long before = Visualizer.Moments.ForcedSequence;
        Visualizer.Moments.Force();
        Assert.Equal(before + 1, Visualizer.Moments.ForcedSequence);
    }

    [Fact]
    public void The_fade_is_900_ms_and_1400_ms_calm()
    {
        Assert.Equal(900f, Visualizer.Moments.FadeMsFor(calm: false));
        Assert.Equal(1400f, Visualizer.Moments.FadeMsFor(calm: true));
        Assert.Equal(Stage.Tone.MomentFadeMs, Visualizer.Moments.FadeMsFor(false));
        Assert.Equal(8, Visualizer.Moments.Bars);
    }

    [Fact]
    public void A_model_driven_song_turns_on_the_downbeats_only()
    {
        // 120 BPM tempo, ticked at 30 Hz for 40 s: bars of 2 s, so the moments land at 16 s and 32 s and nowhere else
        var m = new Visualizer.Model();
        var s = new Visualizer.Moments.Schedule();
        var landed = new System.Collections.Generic.List<long>();
        for (long pos = 0; pos <= 40_000; pos += 33)
        {
            var f = m.Tick(new Visualizer.Input(true, false, 1f, false, false, 0f, pos, 180_000, false, false, 1200, true), default, default, default, 1f / 30f);
            if (s.Step(f.Bar, f.DownbeatEdge, enabled: true, NoForce)) landed.Add(pos);
        }
        Assert.Equal(2, landed.Count);
        Assert.InRange(landed[0], 16_000, 16_033);
        Assert.InRange(landed[1], 32_000, 32_033);
    }
}
