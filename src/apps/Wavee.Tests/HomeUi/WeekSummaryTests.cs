// ── Wavee.Tests/HomeUi/WeekSummaryTests.cs — group play counts, Saved rows are zero, local-day bucketing ────────────

using System;
using FluentGpu.Foundation;
using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class WeekSummaryTests
{
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly DateOnly Today = new(2026, 9, 25); // Friday

    static long LocalMidnightMs(DateOnly date, int hour = 12) =>
        new DateTimeOffset(date.Year, date.Month, date.Day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    static RecentsEdge Single(long playedAtMs, RecentsReason reason = RecentsReason.Played) =>
        new(StringId.Empty, playedAtMs, ChildCount: 0, Reason: (byte)reason, ContentType: 0,
            Kind: (byte)RecentsRowKind.Single, MembersStart: 0, MembersLen: 0);

    static RecentsEdge Group(long playedAtMs, int childCount, RecentsReason reason = RecentsReason.Played) =>
        new(StringId.Empty, playedAtMs, ChildCount: childCount, Reason: (byte)reason, ContentType: 0,
            Kind: (byte)RecentsRowKind.Group, MembersStart: 0, MembersLen: childCount);

    [Fact]
    public void SingleRow_CountsAsOnePlay()
    {
        var rows = new[] { Single(LocalMidnightMs(Today)) };
        RecentsWeek week = WeekSummary.Of(rows, Today, Utc);
        Assert.Equal(1, week.D0Today);
        Assert.Equal(1, week.Total);
    }

    [Fact]
    public void GroupRow_CountsAsItsDeclaredChildCount()
    {
        var rows = new[] { Group(LocalMidnightMs(Today), childCount: 7) };
        RecentsWeek week = WeekSummary.Of(rows, Today, Utc);
        Assert.Equal(7, week.D0Today);
        Assert.Equal(7, week.Total);
    }

    [Fact]
    public void SavedRow_CountsAsZero_EvenAsAGroup()
    {
        var rows = new[]
        {
            Single(LocalMidnightMs(Today), RecentsReason.Saved),
            Group(LocalMidnightMs(Today), childCount: 5, RecentsReason.Saved),
        };
        RecentsWeek week = WeekSummary.Of(rows, Today, Utc);
        Assert.Equal(0, week.D0Today);
        Assert.Equal(0, week.Total);
    }

    [Fact]
    public void DistributesAcrossTheSevenDayWindow()
    {
        var rows = new[]
        {
            Single(LocalMidnightMs(Today)),                    // D0
            Single(LocalMidnightMs(Today.AddDays(-1))),          // D1
            Single(LocalMidnightMs(Today.AddDays(-6))),          // D6 (oldest bar still in window)
        };
        RecentsWeek week = WeekSummary.Of(rows, Today, Utc);
        Assert.Equal(1, week.D0Today);
        Assert.Equal(1, week.D1);
        Assert.Equal(1, week.D6);
        Assert.Equal(0, week.D2);
        Assert.Equal(3, week.Total);
        Assert.Equal(1, week.Max);
    }

    [Fact]
    public void OutsideTheSevenDayWindow_IsExcluded()
    {
        var rows = new[]
        {
            Single(LocalMidnightMs(Today.AddDays(-7))),  // one day too old
            Single(LocalMidnightMs(Today.AddDays(1))),   // in the future relative to `today`
        };
        RecentsWeek week = WeekSummary.Of(rows, Today, Utc);
        Assert.Equal(0, week.Total);
    }

    [Fact]
    public void Indexer_MatchesTheNamedProperties()
    {
        var week = new RecentsWeek(Total: 10, D0Today: 1, D1: 2, D2: 3, D3: 4, D4: 0, D5: 0, D6: 0);
        Assert.Equal(week.D0Today, week[0]);
        Assert.Equal(week.D3, week[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => week[7]);
    }

    [Fact]
    public void LocalDayBoundary_UsesTheGivenTimeZone_NotUtc()
    {
        // A play at 23:30 UTC on "yesterday" is already "today" in a timezone UTC+1 (00:30 local).
        TimeZoneInfo plusOne = TimeZoneInfo.CreateCustomTimeZone("test-utc+1", TimeSpan.FromHours(1), "UTC+1", "UTC+1");
        long playedUtcMs = new DateTimeOffset(Today.AddDays(-1).Year, Today.AddDays(-1).Month, Today.AddDays(-1).Day, 23, 30, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();

        RecentsWeek atUtc = WeekSummary.Of(new[] { Single(playedUtcMs) }, Today, Utc);
        RecentsWeek atPlusOne = WeekSummary.Of(new[] { Single(playedUtcMs) }, Today, plusOne);

        Assert.Equal(1, atUtc.D1);    // still "yesterday" in UTC
        Assert.Equal(0, atUtc.D0Today);
        Assert.Equal(1, atPlusOne.D0Today); // "today" once shifted to UTC+1's local calendar day
        Assert.Equal(0, atPlusOne.D1);
    }
}
