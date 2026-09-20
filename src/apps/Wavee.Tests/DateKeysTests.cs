// ── Wavee.Tests/DateKeysTests.cs — the ONE date-key family (Show.Rules.cs §4b) ──────────────────────────────────────
//
// Two encodings were alive in the show reader at once — a packed `year*12 + month-1` month ordinal (the shape's group
// key) beside `year*100 + month` (its date rail's) — and a third, `year*10000 + month*100 + day`, in the episode row.
// A key minted in one space and decoded by another is an impossible date: 202409 read as year 16867, anything below 12
// read as year 0, and `new DateTime(...)` threw `ArgumentOutOfRangeException` out of a FormatCache thunk on the RENDER
// path, which crashed the app loop on the show, episode and podcasts routes.
//
// `DateKeys` is now the only minter and the only decoder, its two keys are decimal, and EVERY decoder is total. These
// facts are that contract: round-trip each producer through its consumers, then feed every decoder the keys that threw
// — a key below 12, a key from the deleted packed space, a zero month, a zero day, a day key handed to a month decoder
// — and require an answer, never an exception.

using System.Globalization;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DateKeysTests
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    static int AtUtc(int y, int m, int d) => (int)new DateTimeOffset(y, m, d, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>Noon LOCAL, so the day key of a local-clock stamp is that day whatever the box's zone.</summary>
    static int AtLocal(int y, int m, int d)
        => (int)new DateTimeOffset(new DateTime(y, m, d, 12, 0, 0, DateTimeKind.Local)).ToUnixTimeSeconds();

    // ── the two encodings, stated once ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2026, 9, 202609)]
    [InlineData(2025, 1, 202501)]
    [InlineData(1999, 12, 199912)]
    [InlineData(1970, 1, 197001)]
    public void MonthKey_IsYearTimesHundredPlusMonth(int year, int month, int expected)
    {
        Assert.Equal(expected, DateKeys.MonthKey(year, month));
        Assert.Equal(expected, DateKeys.MonthKeyOfUtc(AtUtc(year, month, 28)));
    }

    [Theory]
    [InlineData(2026, 9, 15, 20260915)]
    [InlineData(2000, 1, 1, 20000101)]
    [InlineData(2024, 2, 29, 20240229)]
    public void DayKey_IsYearTimesTenThousandPlusMonthPlusDay(int year, int month, int day, int expected)
    {
        Assert.Equal(expected, DateKeys.DayKey(year, month, day));
        Assert.Equal(expected, DateKeys.DayKeyOfLocal(AtLocal(year, month, day)));
    }

    // ── round trips: every producer through every consumer ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2026, 9)]
    [InlineData(2025, 1)]
    [InlineData(1999, 12)]
    [InlineData(1970, 1)]
    public void AMintedMonthKey_RoundTripsThroughEveryMonthDecoder(int year, int month)
    {
        int key = DateKeys.MonthKeyOfUtc(AtUtc(year, month, 28));

        Assert.True(DateKeys.IsMonthKey(key));
        Assert.True(DateKeys.TryMonth(key, out int y, out int m));
        Assert.Equal((year, month), (y, m));
        Assert.Equal(year, DateKeys.YearOfMonth(key));
        Assert.Equal(month, DateKeys.MonthOfMonth(key));
        Assert.True(DateKeys.TryMonthStart(key, out var date));
        Assert.Equal(new DateTime(year, month, 1), date);
        Assert.Equal(new DateTime(year, month, 1).ToString("MMMM yyyy", Invariant), DateKeys.MonthLabel(key, Invariant));
    }

    [Theory]
    [InlineData(2026, 9, 15)]
    [InlineData(2024, 2, 29)]
    [InlineData(2026, 12, 31)]
    public void AMintedDayKey_RoundTripsThroughEveryDayDecoder(int year, int month, int day)
    {
        int key = DateKeys.DayKeyOfLocal(AtLocal(year, month, day));

        Assert.True(DateKeys.IsDayKey(key));
        Assert.True(DateKeys.TryDay(key, out int y, out int m, out int d));
        Assert.Equal((year, month, day), (y, m, d));
        Assert.True(DateKeys.TryDayDate(key, out var date));
        Assert.Equal(new DateTime(year, month, day), date);
        Assert.Equal(new DateTime(year, month, day).ToString("MMM d", Invariant), DateKeys.DayLabel(key, Invariant));
    }

    [Fact]
    public void AnUnknownStamp_IsNone_AndNeverTheEpoch()
    {
        Assert.Equal(DateKeys.None, DateKeys.MonthKeyOfUtc(0));
        Assert.Equal(DateKeys.None, DateKeys.MonthKeyOfUtc(-86_400));
        Assert.Equal(DateKeys.None, DateKeys.DayKeyOfLocal(0));
        Assert.Equal(DateKeys.None, DateKeys.DayKeyOfLocal(int.MinValue));
        Assert.Equal(-1, DateKeys.None);
    }

    // ── totality: the keys that threw ───────────────────────────────────────────────────────────────────────────────
    //
    // Every value below reached a `new DateTime(...)` before this type existed. None of them may throw now, and none of
    // them may claim to be a date.

    public static TheoryData<int> NotMonthKeys => new()
    {
        DateKeys.None,          // the undated run / an unpinned sticky header
        0,                      // a recycled slot's default
        -7,
        11,                     // "a key below 12": the packed space's january of year 0 → new DateTime(0, 12, 1)
        0 + 8,                  // the packed space's key for year 0
        24_296,                 // "a key from the other space": year*12 + month-1 for 2026-09 → year 242, month 96
        202_400,                // a ZERO month
        202_413,                // month 13
        202_499,                // month 99
        20_260_915,             // a DAY key handed to a month decoder
        1_000_000_000,          // year 10,000,000 — past DateTime
        int.MaxValue,
        int.MinValue,
    };

    [Theory]
    [MemberData(nameof(NotMonthKeys))]
    public void AMonthDecoder_AnswersInsteadOfThrowing_ForAKeyItDidNotMint(int key)
    {
        Assert.False(DateKeys.IsMonthKey(key));
        Assert.False(DateKeys.TryMonth(key, out int year, out int month));
        Assert.Equal(DateKeys.None, year);
        Assert.Equal(0, month);
        Assert.Equal(DateKeys.None, DateKeys.YearOfMonth(key));
        Assert.Equal(0, DateKeys.MonthOfMonth(key));
        Assert.False(DateKeys.TryMonthStart(key, out var date));
        Assert.Equal(default(DateTime), date);
        Assert.Equal("", DateKeys.MonthLabel(key, Invariant));
    }

    public static TheoryData<int> NotDayKeys => new()
    {
        DateKeys.None,
        0,
        -20_260_915,
        202_609,                // a MONTH key handed to a day decoder
        24_296,                 // the deleted packed space
        20_260_900,             // a zero DAY
        20_260_015,             // a zero MONTH
        20_261_315,             // month 13
        20_260_932,             // day 32
        20_260_931,             // september 31st
        20_250_229,             // february 29th of a non-leap year
        int.MaxValue,
        int.MinValue,
    };

    [Theory]
    [MemberData(nameof(NotDayKeys))]
    public void ADayDecoder_AnswersInsteadOfThrowing_ForAKeyItDidNotMint(int key)
    {
        Assert.False(DateKeys.IsDayKey(key));
        Assert.False(DateKeys.TryDay(key, out int year, out int month, out int day));
        Assert.Equal(DateKeys.None, year);
        Assert.Equal((0, 0), (month, day));
        Assert.False(DateKeys.TryDayDate(key, out var date));
        Assert.Equal(default(DateTime), date);
        Assert.Equal("", DateKeys.DayLabel(key, Invariant));
    }

    /// <summary>The two spaces can no longer be confused for one another: a month key is never a day key and the
    /// reverse, so a mixed-up key FAILS rather than naming the wrong date.</summary>
    [Fact]
    public void AMonthKeyAndADayKey_AreNeverEachOther()
    {
        int month = DateKeys.MonthKey(2026, 9), day = DateKeys.DayKey(2026, 9, 15);
        Assert.False(DateKeys.IsDayKey(month));
        Assert.False(DateKeys.IsMonthKey(day));
        Assert.NotEqual(month, day);
    }

    // ── the label the render path actually calls ────────────────────────────────────────────────────────────────────

    /// <summary>The episode row's date: the row binds `Episode.DateKey` and formats it through the same cache, so the
    /// label falls back to "" — never an exception — for an unknown date, and hands back the cached instance.</summary>
    [Fact]
    public void TheEpisodeRowsDate_FallsBackInsteadOfThrowing_AndIsCachedByDay()
    {
        int published = AtLocal(2026, 9, 15);

        Assert.Equal(20260915, Episode.DateKey(published));
        Assert.Equal(DateKeys.DayLabel(Episode.DateKey(published), CultureInfo.CurrentCulture), Episode.DateLabel(published));
        Assert.Same(Episode.DateLabel(published), Episode.DateLabel(published + 60));   // same day, same instance

        Assert.Equal(DateKeys.None, Episode.DateKey(0));
        Assert.Equal("", Episode.DateLabel(0));
        Assert.Equal("", Episode.DateLabel(-1));
        Assert.Equal("", Episode.DateLabel(int.MinValue));
    }
}
