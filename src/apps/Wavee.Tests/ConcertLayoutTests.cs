// ── Wavee.Tests/ConcertLayoutTests.cs — the concert layout hysteresis, ported from 0.2.9 ─────────────────────────────
//
// Ported from `_old/Wavee.Tests/ConcertLayoutTests.cs`; the width ladders (T10b3) live in Classic's `Rules/Tiers.Concert.cs`.

using Wavee;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests;

public class ConcertLayoutTests
{
    [Fact]
    public void ScheduleWide_UsesSeparateEnterAndLeaveThresholds()
    {
        Assert.False(ClassicConcertTiers.ScheduleWide(740f, wasWide: false));
        Assert.True(ClassicConcertTiers.ScheduleWide(760f, wasWide: false));
        Assert.True(ClassicConcertTiers.ScheduleWide(740f, wasWide: true));
        Assert.False(ClassicConcertTiers.ScheduleWide(719f, wasWide: true));
    }

    [Fact]
    public void DetailWide_UsesSeparateEnterAndLeaveThresholds()
    {
        Assert.False(ClassicConcertTiers.DetailWide(900f, wasWide: false));
        Assert.True(ClassicConcertTiers.DetailWide(920f, wasWide: false));
        Assert.True(ClassicConcertTiers.DetailWide(880f, wasWide: true));
        Assert.False(ClassicConcertTiers.DetailWide(859f, wasWide: true));
    }
}
