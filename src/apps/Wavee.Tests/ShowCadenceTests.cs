// ── Wavee.Tests/ShowCadenceTests.cs — "weekly" and "usually lands on Tuesdays" (podcast plan §5.2, §10) ─────────────
//
// `ShowCadence.Of` reads the median gap of the newest ≤ 8 dated episodes against four bands, and names a weekday only
// when one UTC weekday holds at least half the sample. 2026-09-15 is a Tuesday.

using Wavee;
using Xunit;
using Kind = Wavee.ShowCadence.Kind;

namespace Wavee.Tests;

public class ShowCadenceTests
{
    const int Day = 86_400;
    static readonly int Tuesday = At(2026, 9, 15);

    static int At(int y, int m, int d, int h = 12, int min = 0)
        => (int)new DateTimeOffset(y, m, d, h, min, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary><paramref name="count"/> dates, newest first, <paramref name="days"/> apart, from <paramref name="newest"/>.</summary>
    static int[] Every(int days, int count, int newest)
    {
        var dates = new int[count];
        for (int k = 0; k < count; k++) dates[k] = newest - k * days * Day;
        return dates;
    }

    static void Is(Kind kind, DayOfWeek? day, (Kind Cadence, DayOfWeek? Day) actual)
    {
        Assert.Equal(kind, actual.Cadence);
        Assert.Equal(day, actual.Day);
    }

    [Fact]
    public void Weekly_OnTuesdays() => Is(Kind.Weekly, DayOfWeek.Tuesday, ShowCadence.Of(Every(7, 8, Tuesday)));

    [Fact]
    public void Fortnightly() => Is(Kind.Fortnightly, DayOfWeek.Tuesday, ShowCadence.Of(Every(14, 8, Tuesday)));

    [Fact]
    public void Daily_HasNoLandingDay() => Is(Kind.Daily, null, ShowCadence.Of(Every(1, 8, Tuesday)));

    [Fact]
    public void Monthly_DriftsAcrossWeekdays() => Is(Kind.Monthly, null, ShowCadence.Of(Every(30, 8, Tuesday)));

    [Fact]
    public void FewerThanFourDates_IsUnknown()
    {
        Is(Kind.Unknown, null, ShowCadence.Of(Every(7, 3, Tuesday)));
        Is(Kind.Unknown, null, ShowCadence.Of([]));
        Is(Kind.Weekly, DayOfWeek.Tuesday, ShowCadence.Of(Every(7, 4, Tuesday)));      // four is enough
    }

    [Fact]
    public void UndatedEpisodes_AreSkipped_NotCounted()
    {
        int[] w = Every(7, 4, Tuesday);
        Is(Kind.Weekly, DayOfWeek.Tuesday, ShowCadence.Of([0, w[0], 0, w[1], w[2], 0, w[3]]));
        Is(Kind.Unknown, null, ShowCadence.Of([0, w[0], w[1], w[2], 0]));                 // three dated
    }

    [Fact]
    public void OnlyTheNewestEightAreRead()
    {
        // A show that went weekly after years of daily drops is weekly now.
        var dates = new List<int>(Every(7, 8, Tuesday));
        dates.AddRange(Every(1, 30, dates[^1] - Day));
        Is(Kind.Weekly, DayOfWeek.Tuesday, ShowCadence.Of(dates.ToArray()));
    }

    [Fact]
    public void ASkippedWeek_IsStillWeekly()
    {
        int[] d = [Tuesday, Tuesday - 7 * Day, Tuesday - 21 * Day, Tuesday - 28 * Day, Tuesday - 35 * Day, Tuesday - 42 * Day];
        Is(Kind.Weekly, DayOfWeek.Tuesday, ShowCadence.Of(d));
    }

    [Fact]
    public void AnEvenSample_AveragesTheTwoMiddleGaps()
    {
        // gaps 1, 1, 13, 13 days → a 7-day median.
        int[] d = [Tuesday, Tuesday - Day, Tuesday - 2 * Day, Tuesday - 15 * Day, Tuesday - 28 * Day];
        Assert.Equal(Kind.Weekly, ShowCadence.Of(d).Cadence);
    }

    [Fact]
    public void OutOfOrderInput_IsSortedFirst()
    {
        int[] w = Every(7, 6, Tuesday);
        Is(Kind.Weekly, DayOfWeek.Tuesday, ShowCadence.Of([w[3], w[0], w[5], w[1], w[4], w[2]]));
    }

    [Fact]
    public void RarerThanMonthly_IsUnknown() => Assert.Equal(Kind.Unknown, ShowCadence.Of(Every(60, 8, Tuesday)).Cadence);

    [Fact]
    public void TwiceAWeek_HasNoWord_AndATiedWeekdayIsNoMode()
    {
        // Mondays and Thursdays: a 3-4 day median falls in the deliberate hole between daily and weekly, and 4 + 4 is a tie.
        int monday = At(2026, 9, 14), thursday = At(2026, 9, 10);
        int[] d = [monday, thursday, monday - 7 * Day, thursday - 7 * Day, monday - 14 * Day, thursday - 14 * Day,
            monday - 21 * Day, thursday - 21 * Day];
        Is(Kind.Unknown, null, ShowCadence.Of(d));
    }

    [Fact]
    public void TheWeekday_NeedsAtLeastHalfTheSample()
    {
        // Weekly with a wandering drop day: Tuesday 4 of 8 is a mode; 3 of 8 (tied with Monday) is not.
        int[] half = [Tuesday, Tuesday - 6 * Day, Tuesday - 14 * Day, Tuesday - 22 * Day, Tuesday - 28 * Day,
            Tuesday - 34 * Day, Tuesday - 42 * Day, Tuesday - 50 * Day];
        Is(Kind.Weekly, DayOfWeek.Tuesday, ShowCadence.Of(half));

        int[] three = [Tuesday, Tuesday - 6 * Day, Tuesday - 14 * Day, Tuesday - 22 * Day, Tuesday - 29 * Day,
            Tuesday - 34 * Day, Tuesday - 42 * Day, Tuesday - 50 * Day];
        Is(Kind.Weekly, null, ShowCadence.Of(three));
    }

    [Fact]
    public void TheWeekday_IsUtc()
    {
        // 23:30 UTC on a Monday is Tuesday in Helsinki; the rule takes no zone, so it is Monday.
        Is(Kind.Weekly, DayOfWeek.Monday, ShowCadence.Of(Every(7, 5, At(2026, 9, 14, 23, 30))));
    }

    [Theory]
    [InlineData(0, Kind.Daily)]                                     // two drops in the same second
    [InlineData(ShowCadence.DailyMax, Kind.Daily)]
    [InlineData(ShowCadence.DailyMax + 1, Kind.Unknown)]
    [InlineData(ShowCadence.WeeklyMin - 1, Kind.Unknown)]
    [InlineData(ShowCadence.WeeklyMin, Kind.Weekly)]
    [InlineData(ShowCadence.WeeklyMax, Kind.Weekly)]
    [InlineData(ShowCadence.WeeklyMax + 1, Kind.Fortnightly)]
    [InlineData(ShowCadence.FortnightlyMax, Kind.Fortnightly)]
    [InlineData(ShowCadence.FortnightlyMax + 1, Kind.Monthly)]
    [InlineData(ShowCadence.MonthlyMax, Kind.Monthly)]
    [InlineData(ShowCadence.MonthlyMax + 1, Kind.Unknown)]
    public void KindOf_TheBands(int medianGapSeconds, Kind expected)
        => Assert.Equal(expected, ShowCadence.KindOf(medianGapSeconds));

    [Fact]
    public void TheBands_AreTheDocumentedDays()
    {
        Assert.Equal(36 * 3600, ShowCadence.DailyMax);
        Assert.Equal(5 * Day, ShowCadence.WeeklyMin);
        Assert.Equal(10 * Day, ShowCadence.WeeklyMax);
        Assert.Equal(20 * Day, ShowCadence.FortnightlyMax);
        Assert.Equal(45 * Day, ShowCadence.MonthlyMax);
    }
}
