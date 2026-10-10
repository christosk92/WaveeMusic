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
