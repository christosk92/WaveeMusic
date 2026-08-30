using Wavee.Backend.Playback;
using Xunit;

namespace Wavee.Tests;

/// <summary>The transport clock itself. It lives in <c>Backend/Playback/TimeFormat.cs</c> — engine-free, and therefore
/// source-included here — so the formatting the player bar renders is pinned where it is actually decided.
/// <c>PlayerBarContent.Fmt</c> is a one-line delegation to it.
///
/// <para>The hours rung is the case that regresses: a live broadcast's elapsed-since-tune-in has no duration bounding it
/// and will sit past 59:59 on any stream left running, where the old m:ss-only formatter answered "73:04".</para></summary>
public class PlayerBarFmtTests
{
    [Theory]
    [InlineData(0L, "0:00")]
    [InlineData(999L, "0:00")]           // sub-second truncates down — a clock never rounds a second into existence
    [InlineData(1_000L, "0:01")]
    [InlineData(9_000L, "0:09")]
    [InlineData(10_000L, "0:10")]        // the two-digit seconds boundary
    [InlineData(59_000L, "0:59")]
    [InlineData(60_000L, "1:00")]
    [InlineData(3_599_000L, "59:59")]    // the last m:ss reading
    public void BelowAnHour_ItIsMinutesAndSeconds(long ms, string expected)
        => Assert.Equal(expected, TimeFormat.Clock(ms));

    [Theory]
    [InlineData(3_600_000L, "1:00:00")]  // the rung itself: the minutes field gains its leading zero HERE
    [InlineData(3_601_000L, "1:00:01")]
    [InlineData(3_660_000L, "1:01:00")]
    [InlineData(3_784_000L, "1:03:04")]
    [InlineData(36_000_000L, "10:00:00")]
    [InlineData(360_000_000L, "100:00:00")]   // hours are never wrapped — a 100-hour stream reads as 100 hours
    public void AtAndAboveAnHour_ItGrowsAnHoursField(long ms, string expected)
        => Assert.Equal(expected, TimeFormat.Clock(ms));

    /// <summary>The rung is exact, and it is the only place the shape changes: one millisecond below an hour is still
    /// m:ss, one millisecond above the hour mark is h:mm:ss.</summary>
    [Fact]
    public void TheHourRung_IsExact()
    {
        Assert.Equal("59:59", TimeFormat.Clock(TimeFormat.HourMs - 1));
        Assert.Equal("1:00:00", TimeFormat.Clock(TimeFormat.HourMs));
        Assert.Equal(3_600_000L, TimeFormat.HourMs);
    }

    /// <summary>A clock never counts backwards. Negative input (a position behind a window that has already slid past
    /// it, an over-subtracted tune-in stamp) clamps to zero rather than rendering "-1:-3".</summary>
    [Theory]
    [InlineData(-1L)]
    [InlineData(-1_000L)]
    [InlineData(long.MinValue + 1)]
    public void NegativeInput_ClampsToZero(long ms) => Assert.Equal("0:00", TimeFormat.Clock(ms));

    /// <summary>Invariant digits and an invariant separator: these are clock DURATIONS, not dates, and a culture that
    /// writes Eastern Arabic numerals must still line the transport's two labels up under the same rail.</summary>
    [Fact]
    public void ItIsCultureInvariant()
    {
        // The test project runs globalization-invariant, so named cultures cannot be constructed; a cloned invariant
        // culture with Arabic-Indic digits and an odd negative pattern exercises the same thing — the clock must not
        // consult the current culture's number formatting at all.
        var prior = System.Globalization.CultureInfo.CurrentCulture;
        var hostile = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
        hostile.NumberFormat.NativeDigits = ["٠", "١", "٢", "٣", "٤", "٥", "٦", "٧", "٨", "٩"];
        hostile.NumberFormat.NegativeSign = "−";
        hostile.NumberFormat.NumberGroupSeparator = ".";
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = hostile;
            Assert.Equal("1:03:04", TimeFormat.Clock(3_784_000L));
            Assert.Equal("4:05", TimeFormat.Clock(245_000L));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = prior; }
    }
}
