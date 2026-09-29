// ── Wavee.Tests/HomeUi/WhenCaptionTests.cs — the Recently played / episode-row "when" ladder ─────────────────────────
//
// Classify is asserted exactly (the shape decision); Format is asserted through a loc stub (mirrors
// RecentsViewTests.Localize) so the fact under test is the RUNG chosen, not a translation.

using System;
using System.Globalization;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class WhenCaptionTests
{
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly CultureInfo En = CultureInfo.InvariantCulture;

    static string Localize(string key) => key == Strings.Home.When.PlayingNow ? "Playing now"
        : key == Strings.Home.When.MinAgoKey ? "{n} min ago"
        : key == Strings.Home.When.HAgoKey ? "{n} h ago"
        : key == Strings.Detail.Yesterday ? "Yesterday"
        : key;

    static long At(int y, int m, int d, int h = 12, int min = 0) =>
        new DateTimeOffset(y, m, d, h, min, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void PlayingNow_WinsRegardlessOfTimestamp()
    {
        long now = At(2026, 9, 25, 12, 0);
        WhenResult r = WhenCaption.Classify(playedMs: At(2020, 1, 1), now, playingNow: true, Utc);
        Assert.Equal(WhenKind.PlayingNow, r.Kind);
        Assert.Equal("Playing now", WhenCaption.Format(r, En, Localize));
    }

    [Fact]
    public void UnderAnHour_SameLocalDay_IsMinutesAgo()
    {
        long now = At(2026, 9, 25, 12, 0);
        long played = At(2026, 9, 25, 11, 48); // 12 minutes ago
        WhenResult r = WhenCaption.Classify(played, now, false, Utc);
        Assert.Equal(WhenKind.MinutesAgo, r.Kind);
        Assert.Equal(12, r.Minutes);
        Assert.Equal("12 min ago", WhenCaption.Format(r, En, Localize));
    }

    [Fact]
    public void UnderADay_SameLocalDay_IsHoursAgo()
    {
        long now = At(2026, 9, 25, 20, 0);
        long played = At(2026, 9, 25, 18, 0); // 2 hours ago, same local day
        WhenResult r = WhenCaption.Classify(played, now, false, Utc);
        Assert.Equal(WhenKind.HoursAgo, r.Kind);
        Assert.Equal(2, r.Hours);
        Assert.Equal("2 h ago", WhenCaption.Format(r, En, Localize));
    }

    [Fact]
    public void OneCalendarDayBack_IsYesterday_EvenJustAfterMidnight()
    {
        // Played 23:50 on the 24th, "now" is 00:05 on the 25th — only 15 minutes elapsed, but the local
        // calendar day is one back, so this is "Yesterday" (calendar-relative, not elapsed-time-relative).
        long played = At(2026, 9, 24, 23, 50);
        long now = At(2026, 9, 25, 0, 5);
        WhenResult r = WhenCaption.Classify(played, now, false, Utc);
        Assert.Equal(WhenKind.Yesterday, r.Kind);
        Assert.Equal("Yesterday", WhenCaption.Format(r, En, Localize));
    }

    [Fact]
    public void TwoToSixDaysBack_IsTheWeekdayName()
    {
        long now = At(2026, 9, 25, 12, 0); // Friday
        long played = At(2026, 9, 21, 12, 0); // Monday, 4 days back
        WhenResult r = WhenCaption.Classify(played, now, false, Utc);
        Assert.Equal(WhenKind.Weekday, r.Kind);
        Assert.Equal(DayOfWeek.Monday, r.Weekday);
        Assert.Equal("Monday", WhenCaption.Format(r, En, Localize));
    }

    [Fact]
    public void SevenOrMoreDaysBack_IsAShortDate()
    {
        long now = At(2026, 9, 25, 12, 0);
        long played = At(2026, 9, 10, 12, 0); // 15 days back
        WhenResult r = WhenCaption.Classify(played, now, false, Utc);
        Assert.Equal(WhenKind.DateShort, r.Kind);
        Assert.Equal(9, r.Month);
        Assert.Equal(10, r.Day);
        Assert.Equal("10 Sep", WhenCaption.Format(r, En, Localize));
    }

    [Fact]
    public void Of_ClassifiesAndFormatsInOneCall()
    {
        long now = At(2026, 9, 25, 12, 0);
        long played = At(2026, 9, 25, 11, 55);
        Assert.Equal("5 min ago", WhenCaption.Of(played, now, false, Utc, En, Localize));
    }

    [Fact]
    public void MinutesAgo_NeverReadsZero()
    {
        long now = At(2026, 9, 25, 12, 0, 30);
        long played = At(2026, 9, 25, 12, 0, 0); // < 1 minute elapsed
        WhenResult r = WhenCaption.Classify(played, now, false, Utc);
        Assert.Equal("1 min ago", WhenCaption.Format(r, En, Localize));
    }

    static long At(int y, int m, int d, int h, int min, int s) =>
        new DateTimeOffset(y, m, d, h, min, s, TimeSpan.Zero).ToUnixTimeMilliseconds();
}
