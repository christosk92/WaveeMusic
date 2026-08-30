using Xunit;

namespace Wavee.Tests;

public class SetupLayoutTests
{
    [Theory]
    [InlineData(0f, 896f)]
    [InlineData(1200f, 896f)]
    [InlineData(900f, 868f)]
    [InlineData(250f, 300f)]
    public void PlateWidth_ClampsToTheReferencePlate(float viewport, float expected)
        => Assert.Equal(expected, SetupLayout.PlateWidth(viewport));

    [Theory]
    [InlineData(896f, 0)]
    [InlineData(700f, 0)]
    [InlineData(699f, 1)]
    [InlineData(520f, 1)]
    [InlineData(519f, 2)]
    [InlineData(360f, 2)]
    [InlineData(359f, 3)]
    public void NominalTier_UsesTheOrderedPressureLadder(float width, int expected)
        => Assert.Equal(expected, (int)SetupLayout.NominalTierFor(width));

    [Fact]
    public void TierFor_NarrowsImmediately_AndWidensPastTheRecoveryBand()
    {
        var tier = SetupLayoutTier.Wide;
        tier = SetupLayout.TierFor(699f, tier);
        Assert.Equal(SetupLayoutTier.Compact, tier);
        Assert.Equal(SetupLayoutTier.Compact, SetupLayout.TierFor(723f, tier));
        Assert.Equal(SetupLayoutTier.Wide, SetupLayout.TierFor(724f, tier));

        tier = SetupLayout.TierFor(519f, SetupLayoutTier.Compact);
        Assert.Equal(SetupLayoutTier.Narrow, tier);
        Assert.Equal(SetupLayoutTier.Narrow, SetupLayout.TierFor(543f, tier));
        Assert.Equal(SetupLayoutTier.Compact, SetupLayout.TierFor(544f, tier));
    }

    [Fact]
    public void TierCapabilities_DropOnlyTheRequiredStructure()
    {
        Assert.True(SetupLayout.ShowsHero(SetupLayoutTier.Wide));
        Assert.False(SetupLayout.ShowsHero(SetupLayoutTier.Compact));
        Assert.False(SetupLayout.StacksSignIn(SetupLayoutTier.Compact));
        Assert.True(SetupLayout.StacksSignIn(SetupLayoutTier.Narrow));
        Assert.True(SetupLayout.StacksFooter(SetupLayoutTier.Narrow));
        Assert.True(SetupLayout.StacksFooterActions(SetupLayoutTier.UltraNarrow));
        Assert.Equal(80f, SetupLayout.FooterHeightFor(SetupLayoutTier.Wide));
        Assert.Equal(108f, SetupLayout.FooterHeightFor(SetupLayoutTier.Narrow));
        Assert.Equal(144f, SetupLayout.FooterHeightFor(SetupLayoutTier.UltraNarrow));
    }

    // ── the stage/decision grid (work package A) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void StageWidth_Is344_AndTheGridSumsToTheTargetPlate()
    {
        Assert.Equal(344f, SetupLayout.StageWidth);
        Assert.Equal(296f, SetupLayout.StageInnerWidth);
        // 344 (stage) + 480 (decision) + 24 (the gap between them) + 48 (the frame's own 24+24 horizontal padding) == 896.
        Assert.Equal(SetupLayout.TargetWidth,
            SetupLayout.StageWidth + SetupLayout.DecisionWidth(SetupLayout.TargetWidth) + SetupLayout.DecisionGap + 48f);
    }

    [Fact]
    public void DecisionWidth_At896_Is480()
        => Assert.Equal(480f, SetupLayout.DecisionWidth(896f));

    [Fact]
    public void DecisionLaneHeight_At576Wide_Is459()
        => Assert.Equal(459f, SetupLayout.DecisionLaneHeight(576f, SetupLayoutTier.Wide));

    [Theory]
    [InlineData(1, 367f)]
    [InlineData(2, 347f)]
    public void DecisionBodyBudget_SubtractsTheHeaderAndExtraLeadLines(int leadLines, float expected)
        => Assert.Equal(expected, SetupLayout.DecisionBodyBudget(leadLines));

    [Theory]
    [InlineData(480f, false, 298f)]
    [InlineData(480f, true, 248f)]
    public void ControlLane_SubtractsPaddingAndTheLabelLane(float columnWidth, bool sub, float expected)
        => Assert.Equal(expected, SetupLayout.ControlLane(columnWidth, sub));

    [Theory]
    [InlineData(0, 296f)]   // Wide
    [InlineData(1, 412f)]   // Compact
    [InlineData(2, 296f)]   // Narrow
    [InlineData(3, 220f)]   // UltraNarrow
    public void RuntimeBarWidth_PerTier(int tier, float expected)
        => Assert.Equal(expected, SetupLayout.RuntimeBarWidth((SetupLayoutTier)tier));

    [Theory]
    [InlineData(SetupPage.Appearance, true, 2)]   // Live
    [InlineData(SetupPage.Sidebar, true, 2)]      // Live
    [InlineData(SetupPage.Sound, true, 1)]        // Dim
    [InlineData(SetupPage.Terms, true, 1)]        // Dim
    [InlineData(SetupPage.Appearance, false, 0)]  // None
    [InlineData(SetupPage.Sound, false, 0)]       // None
    public void CoverFor_LiveOnlyForAppearanceAndSidebar_NoneWithNoShellBehind(SetupPage page, bool shellBehind, int expected)
        => Assert.Equal((SetupCover)expected, SetupLayout.CoverFor(page, shellBehind));

    [Fact]
    public void RowsHeight_SumsRowsPlusGaps()
    {
        Assert.Equal(340f, SetupLayout.RowsHeight(6, 1));
        Assert.Equal(356f, SetupLayout.RowsHeight(4, 3));
        Assert.Equal(0f, SetupLayout.RowsHeight(0, 0));
    }

    [Fact]
    public void ColumnHeight_Appearance_FitsTheLane_AnEighthRowDoesNot()
    {
        float fits = SetupLayout.ColumnHeight(true, SetupLayout.AppearanceRowPlan);
        Assert.Equal(432f, fits);
        Assert.True(fits <= SetupLayout.DecisionLaneHeight(SetupLayout.TargetHeight, SetupLayoutTier.Wide));

        float[] overflowed = [.. SetupLayout.AppearanceRowPlan, 44f];
        float doesNotFit = SetupLayout.ColumnHeight(true, overflowed);
        Assert.True(doesNotFit > SetupLayout.DecisionLaneHeight(SetupLayout.TargetHeight, SetupLayoutTier.Wide));
    }

    [Fact]
    public void ColumnHeight_Sidebar_Is400_AndFitsTheLane()
    {
        float h = SetupLayout.ColumnHeight(true, SetupLayout.SidebarRowPlan);
        Assert.Equal(400f, h);
        Assert.True(h <= SetupLayout.DecisionLaneHeight(SetupLayout.TargetHeight, SetupLayoutTier.Wide));
    }

    [Theory]
    [InlineData(338f, 2, true)]   // sign-in Idle
    [InlineData(306f, 1, true)]   // local-playback Offer
    [InlineData(341f, 2, true)]   // terms
    [InlineData(368f, 1, false)]  // one over the 367 one-line budget
    public void FitsWide_ComparesAgainstTheBodyBudget(float height, int leadLines, bool expected)
        => Assert.Equal(expected, SetupLayout.FitsWide(height, leadLines));
}
