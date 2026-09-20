// ── Wavee.Tests/PodcastShowToolbarTests.cs — the show reader's toolbar collapse ladder ───────────────────────────────
//
// The three-row toolbar is gone: the reader's toolbar is ONE row at ONE height, and `ShowToolbarLayout` is the whole
// decision — two DETERMINISTIC breakpoints on the measured rail width (never a per-label measure, never a pressure
// classifier, never a horizontal scroller). `Narrow` is kept as the one bit the find box needs and stays a thin
// wrapper over the stage.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class PodcastShowToolbarTests
{
    [Fact]
    public void Narrow_IsFalseUntilMeasured()
        => Assert.False(ShowToolbarLayout.Narrow(0f));

    [Theory]
    [InlineData(320f, true)]
    [InlineData(639f, true)]
    [InlineData(640f, false)]
    [InlineData(1200f, false)]
    public void Narrow_CollapsesTheFindBoxBelowOneThreshold(float width, bool narrow)
        => Assert.Equal(narrow, ShowToolbarLayout.Narrow(width));

    // ── the ladder ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stage_IsFull_UntilMeasured()
    {
        // 0 is "not measured yet" and reads WIDE — the first frame must not flash a collapsed arm at a 1200-DIP window.
        Assert.Equal(ToolbarStage.Full, ShowToolbarLayout.Of(0f));
        Assert.Equal(ToolbarStage.Full, ShowToolbarLayout.Of(-1f));
    }

    [Theory]
    [InlineData(1200f, ToolbarStage.Full)]
    [InlineData(641f, ToolbarStage.Full)]
    [InlineData(640f, ToolbarStage.Full)]          // AT the threshold the find field still fits
    [InlineData(639f, ToolbarStage.FindIcon)]
    [InlineData(540f, ToolbarStage.FindIcon)]
    [InlineData(480f, ToolbarStage.FindIcon)]      // AT the second threshold the sort words still fit
    [InlineData(479f, ToolbarStage.CompactSort)]
    [InlineData(320f, ToolbarStage.CompactSort)]
    public void Stage_WalksTheLadderDownByTwoConstants(float width, ToolbarStage stage)
        => Assert.Equal(stage, ShowToolbarLayout.Of(width));

    [Fact]
    public void TheTwoBreakpointsAreOrdered_AndTheSecondIsTheNarrowerOne()
    {
        Assert.True(ShowToolbarLayout.SortCollapsesBelow < ShowToolbarLayout.FindCollapsesBelow);
        Assert.Equal(640f, ShowToolbarLayout.FindCollapsesBelow);
        Assert.Equal(480f, ShowToolbarLayout.SortCollapsesBelow);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(320f)]
    [InlineData(479f)]
    [InlineData(480f)]
    [InlineData(639f)]
    [InlineData(640f)]
    [InlineData(1200f)]
    public void Narrow_IsExactlyNotFull(float width)
        => Assert.Equal(ShowToolbarLayout.Of(width) != ToolbarStage.Full, ShowToolbarLayout.Narrow(width));
}
