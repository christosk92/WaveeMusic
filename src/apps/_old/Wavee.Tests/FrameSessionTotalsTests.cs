using System;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public sealed class FrameSessionTotalsTests
{
    [Fact]
    public void StrictGateDoesNotRoundToDisplayRefreshBudget()
    {
        var totals = new FrameSessionTotals();
        totals.Add(8.3, 1000.0 / 120);
        totals.Add(8.31, 1000.0 / 120);
        totals.Add(45, 1000.0 / 120);
        Assert.Equal(3, totals.Frames);
        Assert.Equal(2, totals.Over83);
        Assert.Equal(1, totals.OverRefresh);
        Assert.Equal(45, totals.WorstMs);
    }

    [Fact]
    public void UnknownMeasurementsCannotLookLikeCompleteCoverage()
    {
        var totals = new FrameSessionTotals();
        totals.Add(double.NaN, 8.33);
        totals.Add(double.PositiveInfinity, 8.33);
        totals.Add(-1, 8.33);
        totals.Add(10, 0);
        Assert.Equal(4, totals.Invalid);
        Assert.Equal(4, totals.Frames);
        Assert.Equal(1, totals.Over83);
        Assert.Equal(10, totals.WorstMs);
    }
}
