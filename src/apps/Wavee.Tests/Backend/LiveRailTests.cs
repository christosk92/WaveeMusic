using Wavee.Backend.Playback;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The DVR rail's arithmetic. Every case here is one the live path can actually produce and none of them can be
/// reproduced on demand against a real broadcast, which is the whole reason <see cref="LiveRail"/> is a pure function:
/// a window whose left end is eleven million milliseconds in, a window that slid past the playhead, a station with no
/// rewind at all.
/// </summary>
public class LiveRailTests
{
    // A four-hour DVR window that started 3h10m into the broadcast's own clock — the shape a long-running YouTube live
    // stream actually reports, and the one the position/duration formula renders as "pinned at the far right forever".
    const long Start = 11_400_000, End = 25_800_000;

    [Fact]
    public void Frac_MapsTheWindow_NotTheClock()
    {
        Assert.Equal(0.0, LiveRail.Frac(Start, End, Start), 6);
        Assert.Equal(1.0, LiveRail.Frac(Start, End, End), 6);
        Assert.Equal(0.5, LiveRail.Frac(Start, End, Start + (End - Start) / 2), 6);
    }

    [Fact]
    public void Frac_ClampsOutsideTheWindow()
    {
        // The window slid forward past a playhead that fell behind — the rail pins left rather than going negative.
        Assert.Equal(0.0, LiveRail.Frac(Start, End, Start - 60_000), 6);
        Assert.Equal(1.0, LiveRail.Frac(Start, End, End + 60_000), 6);
    }

    [Fact]
    public void Frac_WithNoWindow_IsAtTheEdge()
    {
        // A station with nothing to rewind IS at the live edge; answering 0 would draw an empty rail under audio that
        // is playing.
        Assert.Equal(1.0, LiveRail.Frac(500, 500, 500), 6);
        Assert.Equal(1.0, LiveRail.Frac(500, 100, 500), 6);
    }

    [Fact]
    public void Seek_IsTheInverseOfFrac()
    {
        foreach (double f in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            long pos = LiveRail.Seek(Start, End, f);
            Assert.Equal(f, LiveRail.Frac(Start, End, pos), 5);
        }
    }

    [Fact]
    public void Seek_ClampsIntoTheWindowAtBothEnds()
    {
        Assert.Equal(Start, LiveRail.Seek(Start, End, -3.0));
        Assert.Equal(End, LiveRail.Seek(Start, End, 4.0));
        Assert.Equal(Start, LiveRail.Seek(Start, End, double.NaN));
    }

    [Fact]
    public void Seek_WithNoWindow_CommitsToTheOnePosition()
    {
        Assert.Equal(500, LiveRail.Seek(500, 500, 0.5));
        Assert.Equal(500, LiveRail.Seek(500, 100, 0.9));
    }

    [Fact]
    public void Seek_NeverLeavesTheWindow_ForAnyFraction()
    {
        for (int i = -5; i <= 105; i++)
        {
            long pos = LiveRail.Seek(Start, End, i / 100.0);
            Assert.InRange(pos, Start, End);
        }
    }

    // ── DisplayFrac: what the rail DRAWS, which is not always what Frac MEASURES ────────────────────────────────────

    /// <summary>AT the edge the rail is full, full stop. The measurement against a window whose two ends both keep
    /// moving answers a number that is neither 1 nor stable (a healthy playhead rides seconds inside the end, and the
    /// window republishes several times a second), so drawn literally it is a rail that never fills under a stream
    /// that IS live and a fill that slides on every report.</summary>
    [Fact]
    public void DisplayFrac_AtTheEdge_IsFull_WhateverTheMeasurementSays()
    {
        // 6 s inside the end of a 50 s window: the measurement is 0.88, the drawing is 1.
        const long start = 0, end = 50_000, pos = 44_000;
        Assert.Equal(0.88, LiveRail.Frac(start, end, pos), 6);
        Assert.Equal(1.0, LiveRail.DisplayFrac(start, end, pos, isBehind: false), 6);
    }

    /// <summary>…and it is full REGARDLESS of the window, which is the whole point: the value does not depend on the
    /// two moving ends, so consecutive window reports under a stationary playhead draw the identical rail.</summary>
    [Theory]
    [InlineData(0L, 50_000L, 44_000L)]
    [InlineData(1_000L, 51_200L, 44_300L)]     // the same instant, one window report later
    [InlineData(11_400_000L, 25_800_000L, 11_400_000L)]   // a four-hour window with the playhead at its far LEFT
    public void DisplayFrac_AtTheEdge_IsWindowIndependent(long start, long end, long pos)
        => Assert.Equal(1.0, LiveRail.DisplayFrac(start, end, pos, isBehind: false), 6);

    /// <summary>BEHIND the edge the measurement is the whole point — it is the ground the user is about to scrub back
    /// through — so it is drawn exactly as <c>Frac</c> gives it, clamps and all.</summary>
    [Theory]
    [InlineData(11_400_000L)]
    [InlineData(18_600_000L)]
    [InlineData(25_800_000L)]
    [InlineData(1_000L)]           // fell off the back of the window → clamped to 0, not negative
    public void DisplayFrac_WhenBehind_IsTheMeasurement(long pos)
        => Assert.Equal(LiveRail.Frac(Start, End, pos), LiveRail.DisplayFrac(Start, End, pos, isBehind: true), 6);

    // ── the LiveWindow overloads ────────────────────────────────────────────────────────────────────────────────────

    static LiveWindow Window(long pos) => new(
        IsLive: true, SeekableStartMs: Start, SeekableEndMs: End, LiveEdgeMs: End, PositionMs: pos, IsAtLiveEdge: false);

    [Fact]
    public void WindowOverloads_ReadTheirOwnFields()
    {
        LiveWindow w = Window(Start + 3_600_000);
        Assert.Equal(LiveRail.Frac(Start, End, w.PositionMs), LiveRail.Frac(in w), 6);
        Assert.Equal(LiveRail.Seek(Start, End, 0.25), LiveRail.Seek(in w, 0.25));
    }

    [Fact]
    public void BehindAt_ReportsTheDistanceToTheEdge_ForADragInFlight()
    {
        LiveWindow w = Window(End);
        Assert.Equal(0, LiveRail.BehindAt(in w, 1.0));
        Assert.Equal(End - Start, LiveRail.BehindAt(in w, 0.0));
    }

    // ── LiveWindow itself: the two derived facts the bar branches on ────────────────────────────────────────────────

    [Fact]
    public void HasWindow_NeedsThirtySeconds()
    {
        Assert.False(new LiveWindow(true, 0, 29_999, 29_999, 0, false).HasWindow);
        Assert.True(new LiveWindow(true, 0, 30_000, 30_000, 0, false).HasWindow);
        Assert.False(default(LiveWindow).HasWindow);
    }

    [Fact]
    public void BehindMs_IsNeverNegative()
    {
        // The playhead can read a hair PAST the edge between two 10 Hz publishes; "-0:01 behind" is not a thing.
        Assert.Equal(0, new LiveWindow(true, 0, 100_000, 100_000, 100_500, true).BehindMs);
        Assert.Equal(5_000, new LiveWindow(true, 0, 100_000, 100_000, 95_000, false).BehindMs);
    }

    [Fact]
    public void None_IsNotLive_AndHasNoWindow()
    {
        Assert.False(LiveWindow.None.IsLive);
        Assert.False(LiveWindow.None.HasWindow);
        Assert.Equal(0, LiveWindow.None.WindowMs);
    }
}
