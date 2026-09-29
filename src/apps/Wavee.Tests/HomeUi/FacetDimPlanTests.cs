// ── Wavee.Tests/HomeUi/FacetDimPlanTests.cs — the dim-and-block opacity/duration decision (Home/Facet.Rules.cs) ────
//
// FacetPhase.FadingOut was removed by the Home redesign remediation (docs/plans/wavee/home-redesign-remediation.md
// §3.9): the facet-switch content swap is now the reconciler's own keyed Exit/Enter, not a phase this plan tracks
// opacity for. FacetDimPlan therefore only distinguishes Loading (dim) from every other (rest) phase.

using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class FacetDimPlanTests
{
    [Fact]
    public void Loading_dims_to_point6_over_the_normal_167ms()
    {
        var (opacity, ms) = FacetDimPlan.Of(FacetPhase.Loading);
        Assert.Equal(0.6f, opacity);
        Assert.Equal(Design.Motion.Fast, ms);
    }

    [Theory]
    [InlineData(FacetPhase.Idle)]
    [InlineData(FacetPhase.Refreshing)]
    [InlineData(FacetPhase.Failed)]
    public void Every_other_phase_rests_at_full_opacity_over_the_normal_167ms(FacetPhase phase)
    {
        var (opacity, ms) = FacetDimPlan.Of(phase);
        Assert.Equal(1f, opacity);
        Assert.Equal(Design.Motion.Fast, ms);
    }
}

/// <summary>The facet row's form at its measured width (Home/Facet.Rules.cs <see cref="FacetForm"/>).</summary>
public class FacetFormTests
{
    [Theory]
    [InlineData(1316f, true)]    // the prototype's wide board
    [InlineData(632f, true)]     // the owner's 1717×1150 @150 % row with the right panel open (measured)
    [InlineData(600f, true)]     // the edge is inclusive
    [InlineData(599.9f, false)]
    [InlineData(464f, false)]    // the owner's 1440×900 @150 % row (measured)
    public void Title_words_only_while_the_row_holds_them(float width, bool title)
        => Assert.Equal(title, FacetForm.IsTitle(width));
}
