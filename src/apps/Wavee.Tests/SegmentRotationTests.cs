// ── Wavee.Tests/SegmentRotationTests.cs — `SegmentRotation.Decide` (§6.2) ───────────────────────────────────────────

using System;
using Xunit;

namespace Wavee.Tests;

public class SegmentRotationTests
{
    [Fact]
    public void Keeps_the_open_segment_when_the_day_is_unchanged_and_under_the_size_cap()
    {
        var opened = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 9, 23, 14, 30, 0, DateTimeKind.Utc);
        Assert.Equal(SegmentRotation.Verdict.Keep, SegmentRotation.Decide(opened, now, currentBytes: 100, maxLiveBytes: 1000));
    }

    [Fact]
    public void Rolls_for_a_day_change_even_when_well_under_the_size_cap()
    {
        var opened = new DateTime(2026, 9, 23, 23, 59, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 9, 24, 0, 0, 1, DateTimeKind.Utc);
        Assert.Equal(SegmentRotation.Verdict.RollForDayChange, SegmentRotation.Decide(opened, now, currentBytes: 0, maxLiveBytes: 1000));
    }

    [Fact]
    public void Rolls_for_size_once_the_cap_is_reached_exactly()
    {
        var day = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(SegmentRotation.Verdict.RollForSize, SegmentRotation.Decide(day, day, currentBytes: 1000, maxLiveBytes: 1000));
        Assert.Equal(SegmentRotation.Verdict.Keep, SegmentRotation.Decide(day, day, currentBytes: 999, maxLiveBytes: 1000));
    }

    [Fact]
    public void A_day_change_wins_over_a_simultaneous_size_breach()
    {
        var opened = new DateTime(2026, 9, 23, 23, 59, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 9, 24, 0, 0, 1, DateTimeKind.Utc);
        Assert.Equal(SegmentRotation.Verdict.RollForDayChange, SegmentRotation.Decide(opened, now, currentBytes: 5000, maxLiveBytes: 1000));
    }
}
