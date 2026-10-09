// ── Wavee.Tests/DetailTrackCommandBarLayoutTests.cs — the table command bar's priority fit ──────────────────────────
//
// Wave 4.5's gate for `Track.CommandBarLayout` (Entities/Track.Rules.cs), ported VERBATIM from 0.2.9's
// DetailTrackCommandBarLayoutTests (101 lines): DetailTrackCommandBarLayout → Track.CommandBarLayout,
// DetailTrackCommandWidths → Track.CommandWidths, DetailTrackInlineCommand → Track.InlineCommand. The command bar
// PROMOTES, it does not shrink (ch 04 §0 item 10): a command that does not fit is evicted into "…", with 16-DIP promotion
// hysteresis in every mode.
//
// Pure: no scope, no engine.

using Xunit;
using Layout = Wavee.Track.CommandBarLayout;

namespace Wavee.Tests;

public class DetailTrackCommandBarLayoutTests
{
    static readonly Track.CommandWidths Widths = new(120, 92, 96, 156, 144, 82);

    [Fact]
    public void WidePlaylist_LeavesEveryOptionalCommandInline_WithoutAutoExpandingSearch()
    {
        var fit = Layout.Resolve(1000, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);

        Assert.False(fit.SearchExpanded);
        Assert.Equal(Layout.SearchIconWidth, fit.SearchWidth);
        Assert.True(fit.Has(Track.InlineCommand.Shuffle));
        Assert.True(fit.Has(Track.InlineCommand.Sort));
        Assert.True(fit.Has(Track.InlineCommand.Density));
        Assert.True(fit.Has(Track.InlineCommand.Select));
    }

    [Fact]
    public void NarrowAlbum_PreservesRequiredTargetsAndOverflowsOptionalCommands()
    {
        var fit = Layout.Resolve(150, Widths, vertical: false, hasTune: false, hasSelect: true, explicitSearch: false);

        Assert.False(fit.SearchExpanded);
        Assert.Equal(Track.InlineCommand.None, fit.Inline);
        Assert.Equal(Layout.SearchIconWidth, fit.SearchWidth);
    }

    [Fact]
    public void ExplicitSearch_StaysExpandedAtCompactWidth()
    {
        var fit = Layout.Resolve(240, Widths, vertical: true, hasTune: false, hasSelect: false, explicitSearch: true);

        Assert.True(fit.SearchExpanded);
        Assert.True(fit.SearchWidth >= Layout.SearchMinExplicit);
    }

    [Fact]
    public void PromotionUsesHysteresis()
    {
        var rich = Layout.Resolve(800, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);
        var near = Layout.Resolve(620, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false, rich);

        var fresh = Layout.Resolve(620, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);
        Assert.True(near.Richness <= fresh.Richness);
    }

    /// <summary>The regression pin for the search box "jumping around" as it expands. Hysteresis used to be skipped
    /// whenever <c>explicitSearch</c> was set — precisely when evicted commands re-measure mid-animation and feed back in
    /// as slightly different widths. Jittering measurements must converge on ONE answer.</summary>
    [Fact]
    public void ExplicitSearch_DoesNotOscillate_WhenMeasuredWidthsJitter()
    {
        const float Available = 700f;
        var fit = Layout.Resolve(Available, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: true);

        // Sub-pixel measurement noise of the kind a bounds callback actually publishes mid-animation.
        float[] jitter = [0f, 0.4f, -0.6f, 0.9f, -0.3f, 0.7f, -0.8f, 0.2f];
        for (int i = 0; i < jitter.Length; i++)
        {
            float j = jitter[i];
            var noisy = new Track.CommandWidths(
                Widths.Play + j, Widths.Tune - j, Widths.Shuffle + j,
                Widths.Sort - j, Widths.Density + j, Widths.Select - j);
            var next = Layout.Resolve(Available, noisy, vertical: false, hasTune: true, hasSelect: true,
                                      explicitSearch: true, fit);

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
        var wide = Layout.Resolve(900, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: true);
        var narrow = Layout.Resolve(360, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: true, wide);

        Assert.True(narrow.Richness < wide.Richness);
        Assert.True(narrow.SearchExpanded);
    }

    // ── the toolbar's plate lines up with the row plates; the fit does not change ────────────────────────────────

    /// <summary>The left shift moves the command surface, never the box that measures it, so Resolve receives the width it
    /// always did (the measured slot less the 12-DIP inset) at every pane width.</summary>
    [Theory]
    [InlineData(360f)]
    [InlineData(640f)]
    [InlineData(960f)]
    public void ResolverWidth_IsUnchangedByTheToolbarLead(float paneWidth)
    {
        float before = MathF.Max(0f, paneWidth - 12f);   // the pre-change formula
        Assert.Equal(before, Layout.PaneWidth(paneWidth));

        // …and the fit it produces is the same one.
        var was = Layout.Resolve(before, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);
        var now = Layout.Resolve(Layout.PaneWidth(paneWidth), Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);
        Assert.Equal(was.Inline, now.Inline);
        Assert.Equal(was.SearchWidth, now.SearchWidth);
    }

    /// <summary>At every tier the first toolbar plate lands on the row plates' edge (RowInset Modern, 0 Classic): the chrome pads
    /// PadXFor(tier), the surface its own 6, and the lead takes the difference.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    public void ToolbarPlate_SitsOnTheRowPlateEdge(int tier, bool classic)
    {
        float lead = Track.RowMetrics.ToolbarLead(tier, classic);
        float plate = Track.RowMetrics.PadXFor(tier) - lead + Detail.VerticalLayout.ToolbarSurfacePadX;
        Assert.Equal(Track.RowMetrics.PlateX(classic), plate);
        Assert.True(lead > 0f);
    }
}
