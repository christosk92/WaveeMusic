using System;

namespace Wavee.Features.Player.Deck.Model;

/// <summary>
/// The smooth playhead: <c>SeekBar.Recompute</c>'s arithmetic, minus the DVR arm (a deck draws a TRACK's medium; a
/// live broadcast has no groove to be a fraction of).
/// <para>The transport reports position at ~1 Hz. A deck writing an arm angle 30 times a second off that raw number
/// would step once a second and stand still in between, so each tick extrapolates from the last anchor's wall clock
/// instead. Anchoring is a separate call because the anchor edge is a SIGNAL change (a new <c>PositionMs</c>), while
/// the estimate is wanted on every tick.</para>
/// </summary>
public struct PositionInterpolator
{
    long _anchorWallMs, _anchorPosMs;

    /// <summary>Re-anchor: at wall time <paramref name="wallMs"/> the transport reported <paramref name="positionMs"/>.
    /// Call on every reported-position change AND on a play/pause edge — otherwise a resume extrapolates across the
    /// whole paused gap for one frame.</summary>
    public void Anchor(long wallMs, long positionMs)
    {
        _anchorWallMs = wallMs;
        _anchorPosMs = positionMs;
    }

    /// <summary>
    /// The position to draw at <paramref name="nowMs"/>.
    /// <para>A committed <paramref name="seekTargetMs"/> WINS outright: the user asked for that position and the
    /// medium must be there now, not after the acknowledgement lands. <paramref name="upperBoundMs"/> is the host's
    /// "I have not submitted past here" bound, which is what stops the extrapolation running ahead of real audio at
    /// the end of a track.</para>
    /// </summary>
    public readonly long Estimate(long nowMs, bool advancing, long? seekTargetMs, long? upperBoundMs, long durationMs)
    {
        if (seekTargetMs is { } t) return Clamp(t, durationMs);
        long est = advancing ? _anchorPosMs + (nowMs - _anchorWallMs) : _anchorPosMs;
        if (upperBoundMs is { } ub && est > ub) est = ub;
        return Clamp(est, durationMs);
    }

    /// <summary>The last anchored position, unextrapolated (what a paused deck rests at).</summary>
    public readonly long AnchorPositionMs => _anchorPosMs;

    static long Clamp(long v, long dur) => dur > 0 ? Math.Clamp(v, 0, dur) : Math.Max(0, v);
}
