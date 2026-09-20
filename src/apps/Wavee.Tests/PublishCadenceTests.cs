// ── Wavee.Tests/PublishCadenceTests.cs — the scroll-time publish cadence (W2-A1) ──────────────────────────────────
//
// Gate for `Shell/PublishCadence.cs`: the pure decision behind both of `Shell.Host.cs`'s publish sites (the frame tick
// and the posted-answer wrapper) and the palette pump's self re-arm. The defect it exists for: in real mode something
// is dirty on almost every rendered frame, and publishing every frame made every `Changed` subscriber (13 track rows,
// the section hosts, 75 tooltips) re-render on every scroll frame — 0.5–1 MB per frame, gen2 collections inside a
// fling. `--fake` never showed it because nothing is dirty there while scrolling.
//
// No engine, no clock, no shell: the frame counter and the elapsed milliseconds are the caller's, so every boundary is
// a plain call.

using Xunit;

namespace Wavee.Tests;

public class PublishCadenceTests
{
    [Theory]
    [InlineData(0, 0f)]
    [InlineData(1, 8f)]
    [InlineData(3, 49f)]
    [InlineData(4, 50f)]
    [InlineData(400, 5000f)]
    public void Not_scrolling_always_publishes(int frames, float ms)
    {
        // The pre-W2 behaviour is untouched outside a scroll: every frame and every posted answer publishes at once,
        // however recently the last publication went out.
        Assert.True(PublishCadence.ShouldPublish(scrollActive: false, frames, ms));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Scrolling_holds_for_the_first_three_frames_when_the_lag_is_under_the_bound(int frames)
    {
        Assert.False(PublishCadence.ShouldPublish(scrollActive: true, frames, msSinceLast: 0f));
        Assert.False(PublishCadence.ShouldPublish(scrollActive: true, frames, msSinceLast: 25f));
        Assert.False(PublishCadence.ShouldPublish(scrollActive: true, frames, msSinceLast: 49f));
    }

    [Fact]
    public void Scrolling_publishes_on_the_fourth_frame()
    {
        // Four frames at 120 Hz is ~33 ms: a row realized during a fling still gets its data within two 60 Hz
        // refreshes, while the re-render rate of every `Changed` subscriber drops 4×.
        Assert.Equal(4, PublishCadence.ScrollEveryFrames);
        Assert.True(PublishCadence.ShouldPublish(scrollActive: true, framesSinceLast: 4, msSinceLast: 0f));
        Assert.True(PublishCadence.ShouldPublish(scrollActive: true, framesSinceLast: 5, msSinceLast: 0f));
    }

    [Fact]
    public void Scrolling_publishes_once_the_lag_bound_is_reached_whatever_the_frame_count()
    {
        // The wall-clock bound: at 60 Hz four frames would be ~67 ms, so the 50 ms rule fires first; it is also what
        // the shell's one-shot wake relies on when no frame renders inside the post-scroll hold.
        Assert.Equal(50f, PublishCadence.ScrollMaxLagMs);
        Assert.False(PublishCadence.ShouldPublish(scrollActive: true, framesSinceLast: 0, msSinceLast: 49f));
        Assert.False(PublishCadence.ShouldPublish(scrollActive: true, framesSinceLast: 0, msSinceLast: 49.9f));
        Assert.True(PublishCadence.ShouldPublish(scrollActive: true, framesSinceLast: 0, msSinceLast: 50f));
        Assert.True(PublishCadence.ShouldPublish(scrollActive: true, framesSinceLast: 0, msSinceLast: 51f));
        Assert.True(PublishCadence.ShouldPublish(scrollActive: true, framesSinceLast: 1, msSinceLast: 500f));
    }

    [Fact]
    public void Either_rule_alone_is_enough()
    {
        // Frames without lag, and lag without frames — the two bounds are independent, not both-required.
        Assert.True(PublishCadence.ShouldPublish(scrollActive: true, framesSinceLast: 4, msSinceLast: 1f));
        Assert.True(PublishCadence.ShouldPublish(scrollActive: true, framesSinceLast: 0, msSinceLast: 50f));
        Assert.False(PublishCadence.ShouldPublish(scrollActive: true, framesSinceLast: 3, msSinceLast: 49f));
    }

    [Fact]
    public void The_palette_pump_may_re_arm_itself_only_when_no_scroll_is_live()
    {
        // While scrolling, a landed batch leaves its pending rows queued and the shell re-arms on the scroll-end edge;
        // otherwise a fling's every batch would queue the next one AND publish, which made the pump a per-frame publisher.
        Assert.True(PublishCadence.PalettePumpAllowed(scrollActive: false));
        Assert.False(PublishCadence.PalettePumpAllowed(scrollActive: true));
    }
}
