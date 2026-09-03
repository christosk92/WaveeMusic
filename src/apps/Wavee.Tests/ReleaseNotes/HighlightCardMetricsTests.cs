using Wavee.Core.ReleaseNotes;
using Xunit;

namespace Wavee.Tests;

// The card's fixed geometry and the after-update dialog's height budget. The dialog RENDERS from these constants
// (HighlightCardMetrics.PlateWidth/PlatePadX/CardGap/RowPadTop/RowPadBottom/PlateMaxHeight), so the 620 DIP plate
// cap this class asserts is a real gate: a padding nudge that pushes the worst-case row past it fails a test
// instead of a screenshot.
public class HighlightCardMetricsTests
{
    // The dialog's full height budget (design §A.4), worst case throughout (two-line titles): cards in the row ×
    // whether the row's store card grows a footer × the hero's tagline line count.
    [Theory]
    [InlineData(3, false, 2, 505f)]
    [InlineData(3, true, 2, 525f)]
    [InlineData(2, false, 2, 568f)]
    [InlineData(2, true, 2, 588f)]
    [InlineData(1, false, 2, 583f)]
    [InlineData(1, true, 2, 603f)]
    [InlineData(3, false, 1, 486f)]
    public void DialogHeight_MatchesTheBudgetTable(int cardCount, bool store, int taglineLines, float expected)
        => Assert.Equal(expected, HighlightCardMetrics.DialogHeight(cardCount, store, taglineLines));

    // 10 + title + 4 + 68 + tail + 12 → 132 / 150 regular (one- / two-line title), 152 / 170 store.
    [Theory]
    [InlineData(1, false, 132f)]
    [InlineData(2, false, 150f)]
    [InlineData(1, true, 152f)]
    [InlineData(2, true, 170f)]
    public void TextBlockHeight_MatchesTheArithmetic(int titleLines, bool store, float expected)
        => Assert.Equal(expected, HighlightCardMetrics.TextBlockHeight(titleLines, store));

    // 668 inner: three-up 216, two-up 329, lone min(668, 356) = 356.
    [Theory]
    [InlineData(3, 216f)]
    [InlineData(2, 329f)]
    [InlineData(1, 356f)]
    public void DialogCardWidth_SplitsTheInnerPlateEvenlyAndCapsTheLoneCard(int cardCount, float expected)
        => Assert.Equal(expected, HighlightCardMetrics.DialogCardWidth(cardCount));

    // The 16:9 band: 216 → 122, 329 → 185, 356 → 200, 420 → 236.
    [Theory]
    [InlineData(216f, 122f)]
    [InlineData(329f, 185f)]
    [InlineData(356f, 200f)]
    [InlineData(420f, 236f)]
    public void BandHeight_IsTheRounded16By9Band(float cardWidth, float expected)
        => Assert.Equal(expected, HighlightCardMetrics.BandHeight(cardWidth));

    // Natural heights are 51 / 68 / 85 (whole lines at 17 DIP each), so "exactly four lines" (68) does not overflow
    // and anything past the 68.5 threshold does.
    [Theory]
    [InlineData(51f)]
    [InlineData(68f)]
    [InlineData(68.5f)]
    public void Overflows_IsFalseAtOrBelowFourLines(float naturalHeight)
        => Assert.False(HighlightCardMetrics.Overflows(naturalHeight));

    [Theory]
    [InlineData(85f)]
    [InlineData(102f)]
    public void Overflows_IsTrueAboveFourLines(float naturalHeight)
        => Assert.True(HighlightCardMetrics.Overflows(naturalHeight));

    /// <summary>The real gate: every (card count × store × tagline-line-count) shape the dialog can actually render
    /// must fit the 620 DIP plate cap. This is what makes the individual budget-table rows above a spec rather than
    /// a coincidence — nobody can nudge a padding constant without this sweep catching the worst case.</summary>
    [Fact]
    public void DialogHeight_NeverExceedsThePlateCap_ForEveryRenderableShape()
    {
        for (int cards = 1; cards <= 3; cards++)
            foreach (bool store in new[] { false, true })
                for (int taglineLines = 1; taglineLines <= 2; taglineLines++)
                {
                    float height = HighlightCardMetrics.DialogHeight(cards, store, taglineLines);
                    Assert.True(height <= HighlightCardMetrics.PlateMaxHeight,
                        $"cards={cards} store={store} taglineLines={taglineLines} → {height} > {HighlightCardMetrics.PlateMaxHeight}");
                }
    }
}
