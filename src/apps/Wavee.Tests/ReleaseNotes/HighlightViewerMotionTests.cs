using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core.ReleaseNotes;
using Xunit;

namespace Wavee.Tests;

// The viewer's slide recipe: a directional ENTER over a direction-free EXIT. Pinned here because the engine seeds
// an orphan's exit from the spec it MOUNTED with — a directional exit would replay the previous step's direction on
// the way out, so every recipe below must exit the same way regardless of how it entered.
public class HighlightViewerMotionTests
{
    [Fact]
    public void SlideDistance_MatchesTheEngineEntranceOffset()
        => Assert.Equal(Motion.EntranceOffsetPx, HighlightViewerMotion.SlideDistance);

    [Fact]
    public void SlideForward_EntersFromPositive24()
    {
        var t = HighlightViewerMotion.SlideForward;
        Assert.Equal(HighlightViewerMotion.SlideDistance, t.Enter.Dx);
        Assert.Equal(0f, t.Enter.Opacity);
        Assert.True(t.Enter.Active);
    }

    [Fact]
    public void SlideBack_EntersFromNegative24()
    {
        var t = HighlightViewerMotion.SlideBack;
        Assert.Equal(-HighlightViewerMotion.SlideDistance, t.Enter.Dx);
        Assert.Equal(0f, t.Enter.Opacity);
        Assert.True(t.Enter.Active);
    }

    [Fact]
    public void ExitOnly_HasNoEntrance()
        => Assert.False(HighlightViewerMotion.ExitOnly.Enter.Active);

    // Every recipe's exit is direction-free: fade in place, active, regardless of which way it entered.
    [Theory]
    [MemberData(nameof(AllRecipes))]
    public void EveryRecipe_ExitsInPlace(LayoutTransition t)
    {
        Assert.Equal(0f, t.Exit.Dx);
        Assert.Equal(0f, t.Exit.Opacity);
        Assert.True(t.Exit.Active);
    }

    public static TheoryData<LayoutTransition> AllRecipes => new()
    {
        HighlightViewerMotion.SlideForward,
        HighlightViewerMotion.SlideBack,
        HighlightViewerMotion.ExitOnly,
    };

    [Theory]
    [InlineData(HighlightSlideDirection.Forward)]
    [InlineData(HighlightSlideDirection.Back)]
    public void For_DirectionalSteps_EnterIsActive(HighlightSlideDirection direction)
        => Assert.True(HighlightViewerMotion.For(direction).Enter.Active);

    [Fact]
    public void For_None_EnterIsNotActive()
        => Assert.False(HighlightViewerMotion.For(HighlightSlideDirection.None).Enter.Active);

    [Fact]
    public void For_Forward_MatchesSlideForward()
        => Assert.Equal(HighlightViewerMotion.SlideForward, HighlightViewerMotion.For(HighlightSlideDirection.Forward));

    [Fact]
    public void For_Back_MatchesSlideBack()
        => Assert.Equal(HighlightViewerMotion.SlideBack, HighlightViewerMotion.For(HighlightSlideDirection.Back));

    [Fact]
    public void For_None_MatchesExitOnly()
        => Assert.Equal(HighlightViewerMotion.ExitOnly, HighlightViewerMotion.For(HighlightSlideDirection.None));
}
