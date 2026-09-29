// ── Wavee.Tests/HomeUi/DaylistNextTests.cs — DaypartRules.Next/Countdown, DaylistNext.Caption ──────────────────────
//
// Next/Countdown are asserted exactly (pure, no loc dependency). Caption's DECISION (which weekday, which daypart
// bucket, which time-zone-local hour) is asserted by rebuilding the expected string from the SAME primitives
// (culture day names, DaypartRules.OfHour, "HH:mm") rather than a hardcoded calendar date's real weekday — the shape
// under test is "does this land on the right bucket/day/time", never a specific date's trivia. The daypart WORD
// itself goes through a `localize` stub (mirrors WhenCaptionTests.Localize) so the fact under test stays independent
// of translation text; the outer "{when} arrives at {time}" template is asserted through the SAME production call
// (`Strings.Home.Daylist.ArrivesAt`) the subject uses, the same self-consistency device `RecentsCellsTests` already
// uses for `Strings.Detail.SongCount`.

using System;
using System.Globalization;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class DaylistNextTests
{
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly CultureInfo En = CultureInfo.InvariantCulture;

    static string Localize(string key) => key == Strings.Home.Daypart.Early ? "Early morning"
        : key == Strings.Home.Daypart.Morning ? "Morning"
        : key == Strings.Home.Daypart.Afternoon ? "Afternoon"
        : key == Strings.Home.Daypart.Evening ? "Evening"
        : key == Strings.Home.Daypart.Night ? "Night"
        : key;

    // ══ DaypartRules.Next ════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(Daypart.EarlyMorning, Daypart.Morning)]
    [InlineData(Daypart.Morning, Daypart.Afternoon)]
    [InlineData(Daypart.Afternoon, Daypart.Evening)]
    [InlineData(Daypart.Evening, Daypart.Night)]
    [InlineData(Daypart.Night, Daypart.EarlyMorning)]
    public void Next_StepsTheFiveEditionCycle(Daypart current, Daypart expected)
        => Assert.Equal(expected, DaypartRules.Next(current));

    [Fact]
    public void Next_AppliedFiveTimes_ReturnsToStart()
    {
        Daypart d = Daypart.Afternoon;
        for (int i = 0; i < 5; i++) d = DaypartRules.Next(d);
        Assert.Equal(Daypart.Afternoon, d);
    }

    // ══ DaypartRules.Countdown ═══════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Countdown_MatchesTheSheetExample()
    {
        long remainingMs = ((1L * 3600) + (35 * 60) + 5) * 1000;
        Assert.Equal("01:35:05", DaypartRules.Countdown(remainingMs));
    }

    [Fact]
    public void Countdown_Zero_IsAllZeroes() => Assert.Equal("00:00:00", DaypartRules.Countdown(0));

    [Fact]
    public void Countdown_Negative_ClampsToZero() => Assert.Equal("00:00:00", DaypartRules.Countdown(-5000));

    [Fact]
    public void Countdown_HoursPast99_GrowsWidthRatherThanTruncating()
        => Assert.Equal("100:00:00", DaypartRules.Countdown(100L * 3600 * 1000));

    [Fact]
    public void Countdown_MatchesFormatCountdown_ForTheSameInput()
    {
        long remainingMs = ((0L * 3600) + (5 * 60) + 9) * 1000;
        Span<char> dst = stackalloc char[16];
        int len = DaypartRules.FormatCountdown(remainingMs, dst);
        Assert.Equal(new string(dst[..len]), DaypartRules.Countdown(remainingMs));
    }

    // ══ DaylistNext.Caption ══════════════════════════════════════════════════════════════════════════════════════

    static long AtUtc(int y, int m, int d, int h, int min) =>
        new DateTimeOffset(y, m, d, h, min, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void Caption_NoWindow_IsEmpty() => Assert.Equal("", DaylistNext.Caption(0, Utc, En, Localize));

    [Fact]
    public void Caption_NegativeWindow_IsEmpty() => Assert.Equal("", DaylistNext.Caption(-1, Utc, En, Localize));

    [Theory]
    [InlineData(4, "Early morning")]
    [InlineData(6, "Early morning")]
    [InlineData(7, "Morning")]
    [InlineData(11, "Morning")]
    [InlineData(12, "Afternoon")]
    [InlineData(16, "Afternoon")]
    [InlineData(17, "Evening")]
    [InlineData(20, "Evening")]
    [InlineData(21, "Night")]
    [InlineData(0, "Night")]
    [InlineData(3, "Night")]
    public void Caption_NamesTheDaypartBucketOfTheArrivalHour(int hour, string expectedLabel)
    {
        var local = new DateTime(2026, 9, 25, hour, 4, 0, DateTimeKind.Utc);
        long expiresAtMs = AtUtc(2026, 9, 25, hour, 4);

        string caption = DaylistNext.Caption(expiresAtMs, Utc, En, Localize);

        string weekday = En.DateTimeFormat.GetDayName(local.DayOfWeek).ToLower(En);
        string expectedWhen = weekday + " " + expectedLabel.ToLower(En);
        string expected = Strings.Home.Daylist.ArrivesAt(expectedWhen, $"{hour:00}:04");
        Assert.Equal(expected, caption);
    }

    [Fact]
    public void Caption_MatchesTheSheetExample_FridayAfternoonAt1304()
    {
        // The canvas's own literal: "friday afternoon arrives at 13:04".
        var local = new DateTime(2026, 9, 25, 13, 4, 0, DateTimeKind.Utc);
        long expiresAtMs = AtUtc(2026, 9, 25, 13, 4);

        string caption = DaylistNext.Caption(expiresAtMs, Utc, En, Localize);

        string weekday = En.DateTimeFormat.GetDayName(local.DayOfWeek).ToLower(En);
        Assert.Equal(Strings.Home.Daylist.ArrivesAt(weekday + " afternoon", "13:04"), caption);
    }

    [Fact]
    public void Caption_ConvertsThroughTheGivenTimeZone_NotUtc()
    {
        // 23:30 UTC is 01:30 local the NEXT calendar day in a +2 zone — a different weekday AND daypart bucket
        // (Night, not whatever the UTC hour would have bucketed to) if the conversion is genuinely applied.
        var plus2 = TimeZoneInfo.CreateCustomTimeZone("daylist-next-tests+2", TimeSpan.FromHours(2), "+2", "+2");
        var utcInstant = new DateTime(2026, 9, 25, 23, 30, 0, DateTimeKind.Utc);
        long expiresAtMs = new DateTimeOffset(utcInstant).ToUnixTimeMilliseconds();
        var localInstant = utcInstant.AddHours(2);

        string caption = DaylistNext.Caption(expiresAtMs, plus2, En, Localize);

        Assert.NotEqual(utcInstant.DayOfWeek, localInstant.DayOfWeek);   // sanity: this case really crosses midnight
        Daypart bucket = DaypartRules.OfHour(localInstant.Hour);
        Assert.Equal(Daypart.Night, bucket);

        string weekday = En.DateTimeFormat.GetDayName(localInstant.DayOfWeek).ToLower(En);
        string expected = Strings.Home.Daylist.ArrivesAt(weekday + " night", localInstant.ToString("HH:mm", En));
        Assert.Equal(expected, caption);
    }

    [Fact]
    public void Caption_NoLocalizeOverride_FallsBackToLocGet()
    {
        // The optional-delegate shape mirrors WhenCaption.Format: omitting `localize` must not throw and must produce a
        // caption. (The test process loads no loc catalog, so the text itself is the catalog's missing-key form — the
        // formatted content is pinned by the localized test above.)
        long expiresAtMs = AtUtc(2026, 9, 25, 13, 4);
        string caption = DaylistNext.Caption(expiresAtMs, Utc, En);
        Assert.False(string.IsNullOrEmpty(caption));
    }
}
