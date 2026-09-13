using Xunit;

namespace Wavee.Tests;

/// <summary>S3 #18: the sidebar/track-row now-playing equalizer animated a 40x40 area every frame with no regard for
/// reduced motion or (before UseInterval's own activation fold was found to already cover it) an inactive window.
/// <see cref="EqualizerMotionPolicy"/> is the one decision both call sites (the tick's `enabled` and the mount/flip
/// effect's shape choice) read, pinned here without a component, a timer, or a window.</summary>
public class EqualizerMotionPolicyTests
{
    // ── ShouldTick ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ticks_WhenPlaying_NotPaused_MotionNotReduced()
        => Assert.True(EqualizerMotionPolicy.ShouldTick(playing: true, hoverPaused: false, reducedMotion: false));

    [Fact]
    public void DoesNotTick_WhenNotPlaying()
        => Assert.False(EqualizerMotionPolicy.ShouldTick(playing: false, hoverPaused: false, reducedMotion: false));

    [Fact]
    public void DoesNotTick_WhenHoverPaused_EvenIfPlaying()
        => Assert.False(EqualizerMotionPolicy.ShouldTick(playing: true, hoverPaused: true, reducedMotion: false));

    [Fact]
    public void DoesNotTick_WhenMotionIsReduced_EvenIfPlayingAndNotHoverPaused()
        => Assert.False(EqualizerMotionPolicy.ShouldTick(playing: true, hoverPaused: false, reducedMotion: true));

    [Fact]
    public void DoesNotTick_WhenEverythingSaysStop()
        => Assert.False(EqualizerMotionPolicy.ShouldTick(playing: false, hoverPaused: true, reducedMotion: true));

    // ── ShouldShowStillShape ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StillShape_Shows_WhenPlaying_AndMotionReduced()
        => Assert.True(EqualizerMotionPolicy.ShouldShowStillShape(playing: true, reducedMotion: true));

    [Fact]
    public void StillShape_NeverShows_WhenNotPlaying_RegardlessOfMotion()
    {
        Assert.False(EqualizerMotionPolicy.ShouldShowStillShape(playing: false, reducedMotion: true));
        Assert.False(EqualizerMotionPolicy.ShouldShowStillShape(playing: false, reducedMotion: false));
    }

    [Fact]
    public void StillShape_DoesNotShow_WhenPlaying_ButMotionIsNotReduced()
        => Assert.False(EqualizerMotionPolicy.ShouldShowStillShape(playing: true, reducedMotion: false));
}
