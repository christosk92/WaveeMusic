using Wavee.Features.Player.Deck.Model;
using Xunit;

namespace Wavee.Tests.Player.Deck;

/// <summary>
/// The smooth playhead (docs/plans/wavee/npv-player-styles-implementation.md, Part 3): the transport reports
/// position at ~1 Hz, and a deck writes an arm angle 30 times a second, so every tick between anchors is an
/// extrapolation. The three things that override it — a committed seek, the host's submitted upper bound, and the
/// track's duration — are what stop the arm running past the groove.
/// </summary>
public class PositionInterpolatorTests
{
    const long Dur = 200_000L;

    [Fact]
    public void Advancing_ExtrapolatesWallClockFromTheAnchor()
    {
        var p = default(PositionInterpolator);
        p.Anchor(1_000L, 5_000L);
        Assert.Equal(5_500L, p.Estimate(1_500L, advancing: true, null, null, Dur));
    }

    [Fact]
    public void NotAdvancing_HoldsTheAnchor()
    {
        var p = default(PositionInterpolator);
        p.Anchor(1_000L, 5_000L);
        Assert.Equal(5_000L, p.Estimate(60_000L, advancing: false, null, null, Dur));
    }

    [Fact]
    public void CommittedSeek_WinsOutright_EvenBeforeItIsAcknowledged()
    {
        var p = default(PositionInterpolator);
        p.Anchor(1_000L, 5_000L);
        Assert.Equal(90_000L, p.Estimate(1_500L, advancing: true, 90_000L, null, Dur));
    }

    [Fact]
    public void CommittedSeek_IsStillClampedToTheDuration()
    {
        var p = default(PositionInterpolator);
        p.Anchor(0L, 0L);
        Assert.Equal(Dur, p.Estimate(0L, advancing: true, 999_999L, null, Dur));
        Assert.Equal(0L, p.Estimate(0L, advancing: true, -5_000L, null, Dur));
    }

    [Fact]
    public void UpperBound_CapsARunawayExtrapolation()
    {
        var p = default(PositionInterpolator);
        p.Anchor(0L, 0L);
        // Ten seconds of wall clock, but the host has only submitted output up to 4 s.
        Assert.Equal(4_000L, p.Estimate(10_000L, advancing: true, null, 4_000L, Dur));
    }

    [Fact]
    public void UpperBound_NeverRaisesAnEstimate()
    {
        var p = default(PositionInterpolator);
        p.Anchor(0L, 0L);
        Assert.Equal(1_000L, p.Estimate(1_000L, advancing: true, null, 50_000L, Dur));
    }

    [Fact]
    public void Estimate_ClampsToTheDuration_SoTheArmStopsAtTheRunOut()
    {
        var p = default(PositionInterpolator);
        p.Anchor(0L, Dur - 1_000L);
        Assert.Equal(Dur, p.Estimate(60_000L, advancing: true, null, null, Dur));
    }

    [Fact]
    public void UnknownDuration_ClampsAtZeroOnly()
    {
        var p = default(PositionInterpolator);
        p.Anchor(10_000L, 0L);
        Assert.Equal(0L, p.Estimate(0L, advancing: true, null, null, 0L));            // a backwards clock is not a negative position
        Assert.Equal(490_000L, p.Estimate(500_000L, advancing: true, null, null, 0L)); // no upper clamp without a duration
    }

    [Fact]
    public void ReAnchoring_ResetsTheDrift()
    {
        var p = default(PositionInterpolator);
        p.Anchor(0L, 0L);
        Assert.Equal(60_000L, p.Estimate(60_000L, advancing: true, null, null, Dur));
        p.Anchor(60_000L, 1_000L);   // the transport reported a much earlier position (a remote seek landed)
        Assert.Equal(1_500L, p.Estimate(60_500L, advancing: true, null, null, Dur));
    }

    [Fact]
    public void AnchorPosition_IsExposedUnextrapolated()
    {
        var p = default(PositionInterpolator);
        p.Anchor(1_000L, 7_777L);
        Assert.Equal(7_777L, p.AnchorPositionMs);
    }

    [Fact]
    public void Unanchored_TreatsWallZeroAsItsAnchor_WhichIsWhyEveryCallerAnchorsFirst()
    {
        // Environment.TickCount64 is machine UPTIME, so an interpolator that has never been anchored extrapolates
        // the whole of it and pins to the end of the track. Both entry points anchor before their first estimate
        // (DeckClock.Seed explicitly; the ticker through its position effect) — this pins why that is not optional.
        var p = default(PositionInterpolator);
        Assert.Equal(Dur, p.Estimate(1_000_000L, advancing: true, null, null, Dur));
        p.Anchor(1_000_000L, 0L);
        Assert.Equal(0L, p.Estimate(1_000_000L, advancing: true, null, null, Dur));
    }
}
