using Wavee.Core.ReleaseNotes;
using Xunit;

namespace Wavee.Tests;

// The viewer plate's geometry (design §B.1) and its stepping rule (§B.2), pure — the view maps FluentGpu's Keys.*
// onto HighlightNavKey so this stays engine-free and directly testable.
public class HighlightViewerLayoutTests
{
    // W = min(max(320, min(960, vpW − 96, (vpH − 360)·16⁄9)), vpW − 96). The floor never pushes the plate past the
    // window edge.
    [Theory]
    [InlineData(1440f, 900f, 960f)]
    [InlineData(1100f, 700f, 604f)]
    [InlineData(900f, 600f, 427f)]
    [InlineData(500f, 420f, 320f)]
    [InlineData(320f, 600f, 224f)]
    public void PlateWidth_MatchesTheClampLadder(float vpW, float vpH, float expected)
        => Assert.Equal(expected, HighlightViewerLayout.PlateWidth(vpW, vpH));

    [Fact]
    public void ImageHeight_IsThe16By9BandWhenAPosterExists()
    {
        Assert.Equal(540f, HighlightViewerLayout.ImageHeight(960f, hasPoster: true));
        Assert.Equal(240f, HighlightViewerLayout.ImageHeight(427f, hasPoster: true));
    }

    // L4 (issue #89): a no-poster slide used to get a flat 120-DIP band; the prototype keeps the SAME w·9/16 rule
    // either way (a tinted band, not a smaller one) — so ImageHeight must now agree for both poster states at every
    // plate width, not just report a poster-derived value.
    [Theory]
    [InlineData(960f)]
    [InlineData(427f)]
    [InlineData(320f)]
    public void ImageHeight_IsTheSame16By9BandWithOrWithoutAPoster(float plateWidth)
        => Assert.Equal(
            HighlightViewerLayout.ImageHeight(plateWidth, hasPoster: true),
            HighlightViewerLayout.ImageHeight(plateWidth, hasPoster: false));

    /// <summary>The chevron chrome (a 36 DIP circle inset 12 DIP from each edge) must fit inside the band at the
    /// SMALLEST plate width the ladder ever produces (<see cref="HighlightViewerLayout.PlateMinWidth"/>), poster or
    /// not — the "always rendered at a fixed position" chrome would overflow a band that shrank below it.</summary>
    [Fact]
    public void Chrome_FitsInsideTheSmallestBand()
        => Assert.True(HighlightViewerLayout.ChromeInset * 2f + HighlightViewerLayout.ChromeCircle
                        <= HighlightViewerLayout.ImageHeight(HighlightViewerLayout.PlateMinWidth, hasPoster: false));

    // ── Step: clamped, no wrap ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 3, HighlightNavKey.Previous, 0, HighlightSlideDirection.None)]   // clamped at the start
    [InlineData(1, 3, HighlightNavKey.Previous, 0, HighlightSlideDirection.Back)]
    [InlineData(1, 3, HighlightNavKey.Next, 2, HighlightSlideDirection.Forward)]
    [InlineData(2, 3, HighlightNavKey.Next, 2, HighlightSlideDirection.None)]        // clamped at the end
    [InlineData(2, 3, HighlightNavKey.First, 0, HighlightSlideDirection.Back)]
    [InlineData(0, 3, HighlightNavKey.First, 0, HighlightSlideDirection.None)]       // already first
    [InlineData(0, 3, HighlightNavKey.Last, 2, HighlightSlideDirection.Forward)]
    [InlineData(2, 3, HighlightNavKey.Last, 2, HighlightSlideDirection.None)]        // already last
    public void Step_IsClampedAndNeverWraps(int current, int count, HighlightNavKey key, int expectedIndex,
        HighlightSlideDirection expectedDirection)
    {
        var step = HighlightViewerLayout.Step(current, count, key);
        Assert.Equal(expectedIndex, step.Index);
        Assert.Equal(expectedDirection, step.Direction);
    }

    [Theory]
    [InlineData(HighlightNavKey.Previous)]
    [InlineData(HighlightNavKey.Next)]
    [InlineData(HighlightNavKey.First)]
    [InlineData(HighlightNavKey.Last)]
    public void Step_WithASingleItem_IsAlwaysNone(HighlightNavKey key)
    {
        var step = HighlightViewerLayout.Step(0, 1, key);
        Assert.Equal(0, step.Index);
        Assert.Equal(HighlightSlideDirection.None, step.Direction);
    }

    // ── StepTo: a direct jump (a pip click) — direction is the SIGN of the move ─────────────────────────────────

    [Theory]
    [InlineData(1, 3, 5, 3, HighlightSlideDirection.Forward)]
    [InlineData(3, 1, 5, 1, HighlightSlideDirection.Back)]
    [InlineData(2, 2, 5, 2, HighlightSlideDirection.None)]
    public void StepTo_DirectionIsTheSignOfTheMove(int current, int target, int count, int expectedIndex,
        HighlightSlideDirection expectedDirection)
    {
        var step = HighlightViewerLayout.StepTo(current, target, count);
        Assert.Equal(expectedIndex, step.Index);
        Assert.Equal(expectedDirection, step.Direction);
    }

    [Fact]
    public void StepTo_WithASingleItem_IsAlwaysNone()
    {
        var step = HighlightViewerLayout.StepTo(0, 0, 1);
        Assert.Equal(0, step.Index);
        Assert.Equal(HighlightSlideDirection.None, step.Direction);
    }
}
