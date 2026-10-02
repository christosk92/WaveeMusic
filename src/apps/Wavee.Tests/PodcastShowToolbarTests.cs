// ── Wavee.Tests/PodcastShowToolbarTests.cs — the show reader's MEASURED toolbar ──────────────────────────────────────
//
// The toolbar is ONE row at ONE height, and `ShowToolbarLayout.For` is the whole decision: the toolbar's own arranged
// width against what its pieces measured (`ToolbarNeeds`), richest stage first — Full → FindIcon → CompactSort →
// FilterMenu — with 16 DIP of headroom to go RICHER than the stage on screen (the album/playlist command bar's rule).
// The filter chips never shrink: a stage is only picked when they keep their natural width, and the last stage folds
// them into a menu. A width or a measure of 0 is "not measured yet" and reads Full.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class PodcastShowToolbarTests
{
    // chips ≈ 300, the two sort words ≈ 90, the selected chip ("Unplayed 320") ≈ 70 — what the rails report
    static readonly ToolbarNeeds Needs = new(Filters: 300f, Sort: 90f, SelectedWord: 70f);

    static float WidthFor(ToolbarStage stage, bool narrowPad = false, ToolbarNeeds? needs = null)
        => ShowToolbarLayout.NeedOf(stage, needs ?? Needs) + 2f * (narrowPad ? ShowToolbarLayout.PadNarrow : ShowToolbarLayout.Pad);

    // ── unmeasured reads Full ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void Unmeasured_Width_IsFull(float width)
        => Assert.Equal(ToolbarStage.Full, ShowToolbarLayout.For(width, Needs, narrowPad: false));

    [Fact]
    public void Unmeasured_Needs_AreFull_ThoughTheWindowIsTiny()
    {
        Assert.Equal(ToolbarStage.Full, ShowToolbarLayout.For(320f, default, narrowPad: true));
        // both rails must have answered: chips alone are not enough, nor the sort words alone
        Assert.Equal(ToolbarStage.Full, ShowToolbarLayout.For(320f, new ToolbarNeeds(300f, 0f, 70f), narrowPad: true));
        Assert.Equal(ToolbarStage.Full, ShowToolbarLayout.For(320f, new ToolbarNeeds(0f, 90f, 70f), narrowPad: true));
        Assert.False(default(ToolbarNeeds).Measured);
        Assert.True(Needs.Measured);
    }

    // ── the arithmetic ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EachStage_NeedsLessThanTheRicherOne()
    {
        float full = ShowToolbarLayout.NeedOf(ToolbarStage.Full, Needs), icon = ShowToolbarLayout.NeedOf(ToolbarStage.FindIcon, Needs);
        float sort = ShowToolbarLayout.NeedOf(ToolbarStage.CompactSort, Needs), menu = ShowToolbarLayout.NeedOf(ToolbarStage.FilterMenu, Needs);
        Assert.True(full > icon && icon > sort && sort > menu);
        // chips · gap · find 180 · gap · divider · gap · sort · gap · select
        Assert.Equal(300f + 16f + 180f + 16f + 1f + 16f + 90f + 16f + 30f, full);
        Assert.Equal(300f + 16f + 30f + 16f + 30f + 16f + 30f, sort);
    }

    [Fact]
    public void TheMenuStage_IsSizedFromTheSelectedChip_NeverTheWholeRail()
    {
        Assert.Equal(70f + ShowToolbarLayout.MenuChrome, ShowToolbarLayout.LeftGroup(ToolbarStage.FilterMenu, Needs));
        Assert.Equal(300f, ShowToolbarLayout.LeftGroup(ToolbarStage.CompactSort, Needs));
        // a chip not measured yet falls back to a word's width
        Assert.Equal(ShowToolbarLayout.MenuWordFallback + ShowToolbarLayout.MenuChrome,
                     ShowToolbarLayout.LeftGroup(ToolbarStage.FilterMenu, Needs with { SelectedWord = 0f }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Full_AtTheExactFit_AndTheNextStageDownOneDipShort(bool narrowPad)
    {
        float exact = WidthFor(ToolbarStage.Full, narrowPad);
        Assert.Equal(ToolbarStage.Full, ShowToolbarLayout.For(exact, Needs, narrowPad));
        Assert.Equal(ToolbarStage.Full, ShowToolbarLayout.For(exact + 400f, Needs, narrowPad));
        Assert.Equal(ToolbarStage.FindIcon, ShowToolbarLayout.For(exact - 1f, Needs, narrowPad));
    }

    [Theory]
    [InlineData(ToolbarStage.FindIcon)]
    [InlineData(ToolbarStage.CompactSort)]
    public void EveryStage_IsPickedAtItsExactFit_AndLostOneDipShort(ToolbarStage stage)
    {
        float exact = WidthFor(stage);
        Assert.Equal(stage, ShowToolbarLayout.For(exact, Needs, narrowPad: false));
        Assert.Equal(stage + 1, ShowToolbarLayout.For(exact - 1f, Needs, narrowPad: false));
    }

    [Fact]
    public void TheGutters_AreTheReadersOwn_AndTheNarrowArmIsTighter()
    {
        Assert.Equal(24f, ShowToolbarLayout.Pad);
        Assert.Equal(16f, ShowToolbarLayout.PadNarrow);
        Assert.Equal(1000f - 48f, ShowToolbarLayout.InnerWidth(1000f, narrowPad: false));
        Assert.Equal(1000f - 32f, ShowToolbarLayout.InnerWidth(1000f, narrowPad: true));
        Assert.Equal(Controls.FindBoxWidth, ShowToolbarLayout.FindWidth);
    }

    // ── the chips never shrink ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>For EVERY width from 300 to 1600 the chips have at least their natural width — the search, the sort and
    /// the select give way first — unless the toolbar is at the menu stage, where the chips are not a rail at all.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChipRoom_IsAtLeastTheChipsWidth_ForEveryWidth_UnlessTheMenuStage(bool narrowPad)
    {
        for (float width = 300f; width <= 1600f; width += 1f)
        {
            var stage = ShowToolbarLayout.For(width, Needs, narrowPad);
            if (stage == ToolbarStage.FilterMenu) continue;
            float room = ShowToolbarLayout.LeftRoom(width, narrowPad, stage, Needs);
            Assert.True(room >= Needs.Filters, $"width {width}: stage {stage} leaves the chips {room} < {Needs.Filters}");
        }
    }

    /// <summary>The same property while DRAGGING a window both ways (the stage on screen is the hysteresis' previous):
    /// narrowing is immediate and the headroom only ever delays going richer, so a chip is never squeezed on the way.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChipRoom_Holds_WhileTheWindowIsDraggedWiderAndNarrower(bool narrowPad)
    {
        ToolbarStage? previous = null;
        for (float width = 300f; width <= 1600f; width += 1f) previous = Step(width, previous, narrowPad);
        for (float width = 1600f; width >= 300f; width -= 1f) previous = Step(width, previous, narrowPad);

        ToolbarStage Step(float w, ToolbarStage? prev, bool narrow)
        {
            var stage = ShowToolbarLayout.For(w, Needs, narrow, prev);
            if (stage != ToolbarStage.FilterMenu)
                Assert.True(ShowToolbarLayout.LeftRoom(w, narrow, stage, Needs) >= Needs.Filters, $"width {w}: stage {stage}");
            return stage;
        }
    }

    [Fact]
    public void Stages_NeverGetRicherAsTheWindowNarrows_AndNeverPoorerAsItWidens()
    {
        var stage = ShowToolbarLayout.For(1600f, Needs, false);
        Assert.Equal(ToolbarStage.Full, stage);
        for (float width = 1600f; width >= 300f; width -= 1f)
        {
            var next = ShowToolbarLayout.For(width, Needs, false, stage);
            Assert.True(next >= stage, $"width {width}: {stage} → {next} went richer while narrowing");
            stage = next;
        }
        Assert.Equal(ToolbarStage.FilterMenu, stage);
        for (float width = 300f; width <= 1600f; width += 1f)
        {
            var next = ShowToolbarLayout.For(width, Needs, false, stage);
            Assert.True(next <= stage, $"width {width}: {stage} → {next} went poorer while widening");
            stage = next;
        }
        Assert.Equal(ToolbarStage.Full, stage);
    }

    // ── hysteresis: 16 DIP of headroom to go richer ──────────────────────────────────────────────────────────────────

    [Fact]
    public void GoingRicher_NeedsSixteenDipOfHeadroom()
    {
        Assert.Equal(16f, ShowToolbarLayout.Hysteresis);
        float exact = WidthFor(ToolbarStage.Full);
        // on screen: FindIcon. The bare fit of Full is not enough ...
        Assert.Equal(ToolbarStage.FindIcon, ShowToolbarLayout.For(exact, Needs, false, ToolbarStage.FindIcon));
        Assert.Equal(ToolbarStage.FindIcon, ShowToolbarLayout.For(exact + 15.5f, Needs, false, ToolbarStage.FindIcon));
        // ... 16 DIP of headroom is
        Assert.Equal(ToolbarStage.Full, ShowToolbarLayout.For(exact + 16f, Needs, false, ToolbarStage.FindIcon));
        // with no stage on screen the bare fit decides
        Assert.Equal(ToolbarStage.Full, ShowToolbarLayout.For(exact, Needs, false, null));
    }

    [Fact]
    public void GoingPoorer_IsImmediate_NoHeadroomAsked()
    {
        float exact = WidthFor(ToolbarStage.Full);
        Assert.Equal(ToolbarStage.Full, ShowToolbarLayout.For(exact, Needs, false, ToolbarStage.Full));
        Assert.Equal(ToolbarStage.FindIcon, ShowToolbarLayout.For(exact - 0.5f, Needs, false, ToolbarStage.Full));
        float sort = WidthFor(ToolbarStage.CompactSort);
        Assert.Equal(ToolbarStage.FilterMenu, ShowToolbarLayout.For(sort - 0.5f, Needs, false, ToolbarStage.CompactSort));
    }

    [Fact]
    public void Headroom_NeverMakesTheToolbarPoorerThanItWas()
    {
        // between CompactSort's fit and its fit + 16, from FilterMenu: still the menu
        float sort = WidthFor(ToolbarStage.CompactSort);
        Assert.Equal(ToolbarStage.FilterMenu, ShowToolbarLayout.For(sort + 8f, Needs, false, ToolbarStage.FilterMenu));
        Assert.Equal(ToolbarStage.CompactSort, ShowToolbarLayout.For(sort + 16f, Needs, false, ToolbarStage.FilterMenu));
        // a stage already on screen is never traded for a poorer one by the headroom arithmetic
        Assert.Equal(ToolbarStage.Full, ShowToolbarLayout.For(WidthFor(ToolbarStage.Full) + 100f, Needs, false, ToolbarStage.Full));
    }

    [Fact]
    public void TheLastStage_IsWhereTheChipsGiveUpTheirWords()
        => Assert.Equal(ToolbarStage.FilterMenu, ShowToolbarLayout.For(300f, Needs, narrowPad: true));
}
