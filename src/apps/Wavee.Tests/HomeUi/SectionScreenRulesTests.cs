using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public sealed class SectionScreenRulesTests
{
    [Fact]
    public void Fraction_UnderReportingTotal_UsesAssumedPageAsDenominator()
    {
        // total (5) <= have (10): denom = 10 + assumed(20) = 30.
        float f = SectionWalk.Fraction(haveCount: 10, total: 5, assumedPageSize: 20);
        Assert.Equal(10f / 30f, f, 3);
    }

    [Fact]
    public void Fraction_TotalAhead_UsesRealTotal()
    {
        float f = SectionWalk.Fraction(haveCount: 10, total: 40, assumedPageSize: 20);
        Assert.Equal(0.25f, f, 3);
    }

    [Fact]
    public void Fraction_ZeroAssumedPage_ClampsToOne()
    {
        // assumedPageSize 0 must not divide by zero when have == total == 0 too.
        float f = SectionWalk.Fraction(haveCount: 0, total: 0, assumedPageSize: 0);
        Assert.Equal(0f, f);
    }

    [Fact]
    public void Fraction_NeverExceedsOne()
    {
        float f = SectionWalk.Fraction(haveCount: 50, total: 10, assumedPageSize: 1);
        Assert.True(f <= 1f);
    }

    [Theory]
    [InlineData(0, 20, 8, false)]   // fresh window: far from the tail
    [InlineData(11, 20, 8, false)]  // 9 items of runway left
    [InlineData(12, 20, 8, true)]   // 8 items of runway left: triggers
    [InlineData(20, 20, 8, true)]   // at the very end
    public void NearTail_TriggersWithinThreshold(int lastExclusive, int count, int threshold, bool expected)
        => Assert.Equal(expected, SectionScreenRules.NearTail(lastExclusive, count, threshold));

    [Fact]
    public void NearTail_EmptyList_NeverTriggers()
        => Assert.False(SectionScreenRules.NearTail(0, 0));

    [Theory]
    [InlineData(false, true, false, false, true)]   // ordinary section, has more, idle: may page
    [InlineData(true, true, false, false, false)]   // chart: never auto-pages this way
    [InlineData(false, false, false, false, false)] // nothing more to ask for
    [InlineData(false, true, true, false, false)]   // a request is already in flight
    [InlineData(false, true, false, true, false)]   // latched exhausted
    public void CanAutoPage_Matrix(bool chart, bool hasMore, bool loading, bool exhausted, bool expected)
        => Assert.Equal(expected, SectionScreenRules.CanAutoPage(chart, hasMore, loading, exhausted));
}
