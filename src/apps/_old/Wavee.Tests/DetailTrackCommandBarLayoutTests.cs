using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

public class DetailTrackCommandBarLayoutTests
{
    static readonly DetailTrackCommandWidths Widths = new(120, 92, 96, 156, 144, 82);

    [Fact]
    public void WidePlaylist_LeavesEveryOptionalCommandInline_WithoutAutoExpandingSearch()
    {
        var fit = DetailTrackCommandBarLayout.Resolve(
            1000, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);

        Assert.False(fit.SearchExpanded);
        Assert.Equal(DetailTrackCommandBarLayout.SearchIconWidth, fit.SearchWidth);
        Assert.True(fit.Has(DetailTrackInlineCommand.Shuffle));
        Assert.True(fit.Has(DetailTrackInlineCommand.Sort));
        Assert.True(fit.Has(DetailTrackInlineCommand.Density));
        Assert.True(fit.Has(DetailTrackInlineCommand.Select));
    }

    [Fact]
    public void NarrowAlbum_PreservesRequiredTargetsAndOverflowsOptionalCommands()
    {
        var fit = DetailTrackCommandBarLayout.Resolve(
            150, Widths, vertical: false, hasTune: false, hasSelect: true, explicitSearch: false);

        Assert.False(fit.SearchExpanded);
        Assert.Equal(DetailTrackInlineCommand.None, fit.Inline);
        Assert.Equal(DetailTrackCommandBarLayout.SearchIconWidth, fit.SearchWidth);
    }

    [Fact]
    public void ExplicitSearch_StaysExpandedAtCompactWidth()
    {
        var fit = DetailTrackCommandBarLayout.Resolve(
            240, Widths, vertical: true, hasTune: false, hasSelect: false, explicitSearch: true);

        Assert.True(fit.SearchExpanded);
        Assert.True(fit.SearchWidth >= DetailTrackCommandBarLayout.SearchMinExplicit);
    }

    [Fact]
    public void PromotionUsesHysteresis()
    {
        var rich = DetailTrackCommandBarLayout.Resolve(
            800, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);
        var near = DetailTrackCommandBarLayout.Resolve(
            620, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false, rich);

        var fresh = DetailTrackCommandBarLayout.Resolve(
            620, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);
        Assert.True(near.Richness <= fresh.Richness);
    }

    /// <summary>The regression pin for the search box "jumping around" as it expands.
    ///
    /// Hysteresis used to be skipped entirely whenever <c>explicitSearch</c> was set, which is precisely when it is
    /// load-bearing: opening the field evicts commands, their exit animation re-measures them, those measurements feed
    /// back in as slightly different widths, and the promoted set oscillates — handing the width tween a new target on
    /// every frame of its flight. Jittering measurements must now converge on ONE answer.</summary>
    [Fact]
    public void ExplicitSearch_DoesNotOscillate_WhenMeasuredWidthsJitter()
    {
        const float Available = 700f;
        var fit = DetailTrackCommandBarLayout.Resolve(
            Available, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: true);

        // Sub-pixel measurement noise of the kind OnBoundsChanged actually publishes mid-animation.
        float[] jitter = [0f, 0.4f, -0.6f, 0.9f, -0.3f, 0.7f, -0.8f, 0.2f];
        for (int i = 0; i < jitter.Length; i++)
        {
            float j = jitter[i];
            var noisy = new DetailTrackCommandWidths(
                Widths.Play + j, Widths.Tune - j, Widths.Shuffle + j,
                Widths.Sort - j, Widths.Density + j, Widths.Select - j);
            var next = DetailTrackCommandBarLayout.Resolve(
                Available, noisy, vertical: false, hasTune: true, hasSelect: true, explicitSearch: true, fit);

            Assert.Equal(fit.Inline, next.Inline);
            Assert.True(next.Richness <= fit.Richness);
            fit = next;
        }
    }

    /// <summary>A genuine pane resize must still re-fit while search is open — the freeze is against measurement
    /// feedback, not against the window actually changing size.</summary>
    [Fact]
    public void ExplicitSearch_StillNarrowsWhenThePaneShrinks()
    {
        var wide = DetailTrackCommandBarLayout.Resolve(
            900, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: true);
        var narrow = DetailTrackCommandBarLayout.Resolve(
            360, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: true, wide);

        Assert.True(narrow.Richness < wide.Richness);
        Assert.True(narrow.SearchExpanded);
    }
}
