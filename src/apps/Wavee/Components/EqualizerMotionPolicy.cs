namespace Wavee;

/// <summary>The now-playing equalizer's motion DECISION (S3 #18), split out so it is testable without a window or a
/// GPU: whether the 30 Hz tick should run at all, and — when it must not because motion is reduced — whether the
/// bars should settle into the "still playing" shape rather than the flat "not playing" rest height.
///
/// <para>Two independent stop conditions compose in <see cref="ShouldTick"/>: the track itself is paused/hover-
/// paused (the bars settle FLAT, the pre-existing "not playing" look — <see cref="ShouldShowStillShape"/> is false
/// then too, so nothing here fights that), or motion is reduced (the bars settle to a fixed non-uniform "equalizer"
/// shape instead, so a reduced-motion user can still tell "is this the one playing" from a mid-list glance without
/// anything ever looping).</para>
///
/// <para>Window-inactive/minimized is deliberately NOT one of these inputs: <c>UseInterval</c> already folds
/// <c>Activation.IsActive</c> internally (<c>FluentGpu.Engine.Hooks.RenderContext.Timers.cs</c>) and auto-pauses
/// every interval — this one included — while Wavee is minimized or power-suspended, with no `enabled` help needed
/// from the caller. There is nothing for app code to decide for that half.</para></summary>
internal static class EqualizerMotionPolicy
{
    /// <summary>Whether the per-frame tick should run.</summary>
    internal static bool ShouldTick(bool playing, bool hoverPaused, bool reducedMotion)
        => playing && !hoverPaused && !reducedMotion;

    /// <summary>Whether the bars should settle to the fixed "still playing" shape rather than the flat "not
    /// playing" rest height. False whenever <paramref name="playing"/> is false — a paused/stopped track is always
    /// flat, reduced motion or not.</summary>
    internal static bool ShouldShowStillShape(bool playing, bool reducedMotion)
        => playing && reducedMotion;
}
