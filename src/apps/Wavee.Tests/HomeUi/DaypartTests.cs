// ── Wavee.Tests/HomeUi/DaypartTests.cs — hour windows, title parsing, elapsed fraction, the zero-alloc countdown ────

using System;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class DaypartTests
{
    [Theory]
    [InlineData(4, Daypart.EarlyMorning)]
    [InlineData(6, Daypart.EarlyMorning)]
    [InlineData(7, Daypart.Morning)]
    [InlineData(11, Daypart.Morning)]
    [InlineData(12, Daypart.Afternoon)]
    [InlineData(16, Daypart.Afternoon)]
    [InlineData(17, Daypart.Evening)]
    [InlineData(20, Daypart.Evening)]
    [InlineData(21, Daypart.Night)]
    [InlineData(23, Daypart.Night)]
    [InlineData(0, Daypart.Night)]
    [InlineData(3, Daypart.Night)]
    public void OfHour_Windows(int hour, Daypart expected) => Assert.Equal(expected, DaypartRules.OfHour(hour));

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    [InlineData(28)]
    public void OfHour_NormalisesOutOfRange_NeverThrows(int hour)
    {
        Daypart d = DaypartRules.OfHour(hour);
        Assert.True(Enum.IsDefined(typeof(Daypart), d));
    }

    [Fact]
    public void OfHour_WrapsModulo24_Exactly()
    {
        Assert.Equal(DaypartRules.OfHour(4), DaypartRules.OfHour(28));
        Assert.Equal(DaypartRules.OfHour(23), DaypartRules.OfHour(-1));
    }

    [Theory]
    [InlineData("scream teen pop friday early morning", Daypart.EarlyMorning)]
    [InlineData("scream teen pop friday morning", Daypart.Morning)]
    [InlineData("chill sunday afternoon", Daypart.Afternoon)]
    [InlineData("garage band evening", Daypart.Evening)]
    [InlineData("lofi late night", Daypart.Night)]
    [InlineData("lofi night", Daypart.Night)]
    public void TryFromTitle_ReadsTrailingToken(string title, Daypart expected)
    {
        Assert.True(DaypartRules.TryFromTitle(title, out Daypart d));
        Assert.Equal(expected, d);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nothing recognisable here")]
    public void TryFromTitle_FalseWhenNoTrailingToken(string? title)
    {
        Assert.False(DaypartRules.TryFromTitle(title, out _));
    }

    [Fact]
    public void TryFromTitle_EarlyMorning_NotMisreadAsMorning()
    {
        Assert.True(DaypartRules.TryFromTitle("belter early morning", out Daypart d));
        Assert.Equal(Daypart.EarlyMorning, d);
    }

    [Fact]
    public void Elapsed_ClampsToZeroAndOne()
    {
        Assert.Equal(0.0, DaypartRules.Elapsed(1000, 2000, 500));
        Assert.Equal(1.0, DaypartRules.Elapsed(1000, 2000, 3000));
        Assert.Equal(0.5, DaypartRules.Elapsed(1000, 2000, 1500), 6);
    }

    [Fact]
    public void Elapsed_NonPositiveWindow_IsOneWhenPastElseZero()
    {
        Assert.Equal(1.0, DaypartRules.Elapsed(2000, 2000, 2000));
        Assert.Equal(0.0, DaypartRules.Elapsed(2000, 1000, 500));
    }

    [Fact]
    public void FormatCountdown_MatchesSheetExample()
    {
        // "01:35:05" (row 2)
        long remainingMs = ((1L * 3600) + (35 * 60) + 5) * 1000;
        Span<char> dst = stackalloc char[16];
        int len = DaypartRules.FormatCountdown(remainingMs, dst);
        Assert.Equal("01:35:05", new string(dst[..len]));
    }

    [Fact]
    public void FormatCountdown_Zero_IsAllZeroes()
    {
        Span<char> dst = stackalloc char[16];
        int len = DaypartRules.FormatCountdown(0, dst);
        Assert.Equal("00:00:00", new string(dst[..len]));
    }

    [Fact]
    public void FormatCountdown_Negative_ClampsToZero()
    {
        Span<char> dst = stackalloc char[16];
        int len = DaypartRules.FormatCountdown(-5000, dst);
        Assert.Equal("00:00:00", new string(dst[..len]));
    }

    [Fact]
    public void FormatCountdown_SingleDigitParts_ArePadded()
    {
        long remainingMs = ((0L * 3600) + (5 * 60) + 9) * 1000;
        Span<char> dst = stackalloc char[16];
        int len = DaypartRules.FormatCountdown(remainingMs, dst);
        Assert.Equal("00:05:09", new string(dst[..len]));
    }

    [Fact]
    public void FormatCountdown_HoursPast99_GrowsWidthRatherThanTruncating()
    {
        long remainingMs = (100L * 3600) * 1000; // 100h
        Span<char> dst = stackalloc char[16];
        int len = DaypartRules.FormatCountdown(remainingMs, dst);
        Assert.Equal("100:00:00", new string(dst[..len]));
    }
}
