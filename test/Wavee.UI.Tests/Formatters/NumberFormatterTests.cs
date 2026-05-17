using FluentAssertions;
using Wavee.UI.Formatters;
using Xunit;

namespace Wavee.UI.Tests.Formatters;

public sealed class NumberFormatterTests
{
    [Theory]
    [InlineData(0L, "0")]
    [InlineData(-5L, "0")]
    [InlineData(1L, "1")]
    [InlineData(999L, "999")]
    [InlineData(1_000L, "1K")]
    [InlineData(1_500L, "1.5K")]
    [InlineData(12_345L, "12K")]
    [InlineData(999_999L, "1000K")]
    [InlineData(1_000_000L, "1M")]
    [InlineData(1_500_000L, "1.5M")]
    [InlineData(12_500_000L, "13M")]
    [InlineData(999_999_999L, "1000M")]  // billions NOT enabled by default
    public void FormatCompactCount_WithoutBillions(long input, string expected)
    {
        NumberFormatter.FormatCompactCount(input, includeBillions: false).Should().Be(expected);
    }

    [Theory]
    [InlineData(999_999_999L, "1B")]
    [InlineData(1_000_000_000L, "1B")]
    [InlineData(1_500_000_000L, "1.5B")]
    [InlineData(10_000_000_000L, "10B")]
    public void FormatCompactCount_WithBillions(long input, string expected)
    {
        NumberFormatter.FormatCompactCount(input, includeBillions: true).Should().Be(expected);
    }

    [Fact]
    public void FormatListenerCount_NeverShowsBillions()
    {
        NumberFormatter.FormatListenerCount(2_500_000_000L).Should().EndWith("M");
    }

    [Fact]
    public void FormatFollowerCount_NeverShowsBillions()
    {
        NumberFormatter.FormatFollowerCount(2_500_000_000L).Should().EndWith("M");
    }

    [Fact]
    public void FormatPlayCount_ShowsBillions()
    {
        NumberFormatter.FormatPlayCount(2_500_000_000L).Should().Be("2.5B");
    }
}
