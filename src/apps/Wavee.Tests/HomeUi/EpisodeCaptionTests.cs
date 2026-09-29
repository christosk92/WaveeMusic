// ── Wavee.Tests/HomeUi/EpisodeCaptionTests.cs — the podcast row caption rules, fully pure (no table commit needed) ─
//
// Date is asserted through a loc stub (mirrors RecentsViewTests.Localize / WhenCaptionTests.Localize) so the fact
// under test is the RUNG chosen, not a translation.

using System;
using System.Globalization;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class EpisodeCaptionTests
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;   // the test host runs globalization-invariant
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    static long Ms(int year, int month, int day, int hour = 12) => new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    static string Localize(string key) => key == Strings.Detail.Today ? "Today"
        : key == Strings.Detail.Yesterday ? "Yesterday"
        : key;

    // ── Date: the release-day ladder ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Date_SameDay_IsToday()
    {
        long now = Ms(2026, 9, 25);
        long released = Ms(2026, 9, 25, hour: 6);
        Assert.Equal("Today", EpisodeCaption.Date(released, now, Utc, En, Localize));
    }

    [Fact]
    public void Date_OneDayBack_IsYesterday()
    {
        long now = Ms(2026, 9, 25);
        long released = Ms(2026, 9, 24);
        Assert.Equal("Yesterday", EpisodeCaption.Date(released, now, Utc, En, Localize));
    }

    [Theory]
    [InlineData(2, "Wednesday")]   // 2026-09-23, capture day Fri 25 Sep 2026 (06 §3.3's own P1 lead row)
    [InlineData(3, "Tuesday")]     // 2026-09-22
    [InlineData(5, "Sunday")]      // 2026-09-20
    public void Date_TwoToSixDaysBack_IsTheWeekdayName(int daysBack, string expected)
    {
        long now = Ms(2026, 9, 25);
        long released = now - daysBack * 86_400_000L;
        Assert.Equal(expected, EpisodeCaption.Date(released, now, Utc, En, Localize));
    }

    [Fact]
    public void Date_SevenOrMoreDaysBack_SameYear_IsShortDateWithoutYear()
    {
        long now = Ms(2026, 9, 25);
        long released = Ms(2026, 9, 17); // 8 days back — 06 §3.3 P1 row 3: "Huberman Lab · 17 Sep"
        Assert.Equal("17 Sep", EpisodeCaption.Date(released, now, Utc, En, Localize));
    }

    [Fact]
    public void Date_PriorYear_CarriesTheYearSuffix()
    {
        long now = Ms(2026, 9, 25);
        long released = Ms(2025, 5, 6); // 06 §3.3's own illustrative "6 May 2025"
        Assert.Equal("6 May 2025", EpisodeCaption.Date(released, now, Utc, En, Localize));
    }

    [Fact]
    public void Date_SameYear_NeverCarriesAYear_EvenMonthsBack()
    {
        long now = Ms(2026, 9, 25);
        long released = Ms(2026, 1, 25); // 06 §3.3 P5 row 5: "25 Jan" — same year as the capture, no year suffix
        Assert.Equal("25 Jan", EpisodeCaption.Date(released, now, Utc, En, Localize));
    }

    [Fact]
    public void Date_Unknown_IsEmpty()
    {
        Assert.Equal("", EpisodeCaption.Date(0, Ms(2026, 9, 25), Utc, En, Localize));
        Assert.Equal("", EpisodeCaption.Date(-1, Ms(2026, 9, 25), Utc, En, Localize));
    }

    // ── Duration / Remaining ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(27 * 60_000L, "27 min")]
    [InlineData(61 * 60_000L, "1 h 1 min")]
    [InlineData(130 * 60_000L, "2 h 10 min")]
    [InlineData(60 * 60_000L, "1 h")]
    public void Duration_FormatsAsTheSheetDoes(long durationMs, string expected)
        => Assert.Equal(expected, EpisodeCaption.Duration(durationMs));

    [Fact]
    public void Duration_RoundsToTheNearestMinute_NeverTruncates()
    {
        // 06 §3.3 P2: "8 334 ms of 44 min ≈ 0.3%" — the episode's own duration is ~44 min but not exactly; a
        // duration a hair under 27.5 minutes must still read "27 min", not "26 min" (floor) nor "28 min" (ceiling).
        Assert.Equal("27 min", EpisodeCaption.Duration(27 * 60_000L + 20_000L));
    }

    [Fact]
    public void Remaining_IsDurationMinusPosition_WithTheLeftSuffix()
    {
        // The capture's own in-progress episode: 44 min duration, 8 334 ms played → "44 min left" (06 §3.3 P2).
        long duration = 44 * 60_000L;
        long position = 8_334L;
        Assert.Equal("44 min left", EpisodeCaption.Remaining(duration, position));
    }

    [Fact]
    public void Remaining_NeverGoesNegative_WhenPositionPassesDuration()
        // Floored like `Duration`'s own minimum: a spent/overrun position reads "1 min left", never "0 min left"
        // (which would look broken) or a negative number.
        => Assert.Equal("1 min left", EpisodeCaption.Remaining(10_000L, 20_000L));

    // ── Progress fraction — clamped 0..1 ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProgressFraction_IsClampedToZeroToOne()
    {
        Assert.Equal(0.5, EpisodeCaption.ProgressFraction(500, 1000), 3);
        Assert.Equal(0d, EpisodeCaption.ProgressFraction(0, 0));
        Assert.Equal(1d, EpisodeCaption.ProgressFraction(2000, 1000));
    }
}
