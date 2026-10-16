// ── Wavee.Tests/ClassicSetupTiersTests.cs — Classic's setup wizard records, one per window tier (themes plan 3.5, 3.6; TC8) ─────────────────────
//
// `ClassicSetupTiers` (Rules\Tiers.Setup.cs) replaced the hand-rolled 770-DIP icon breakpoint. These pin the records the boards draw (C-Setup, -Compact, -Small, -Huge), the
// window ladder they are read through (Compact < 528, Small 528-1007, Standard 1008-1919, Huge from 1920; the 40-DIP directional holds), and the arithmetic over them: the plate's
// size in a window, the body's width in the plate, the Compact sheet. Pure: no window, no frame.

using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests;

public class ClassicSetupTiersTests
{
    static TierFacts Window(float width, float height = 760f, float? from = null)
        => from is { } start
            ? WindowTiers.Resolve(ClassicTiers.Policy, width, height, width, previous: WindowTiers.Resolve(ClassicTiers.Policy, start, height, start))
            : WindowTiers.Resolve(ClassicTiers.Policy, width, height, width);

    [Theory]
    [InlineData(300f, null, WidthTier.Compact)]
    [InlineData(527f, null, WidthTier.Compact)]
    [InlineData(528f, null, WidthTier.Small)]
    [InlineData(567f, 400f, WidthTier.Compact)]     // widening past 528 holds 40: Small only at 568
    [InlineData(568f, 400f, WidthTier.Small)]
    [InlineData(1007f, null, WidthTier.Small)]
    [InlineData(1008f, null, WidthTier.Standard)]
    [InlineData(1047f, 800f, WidthTier.Small)]      // widening past 1008 holds 40: Standard only at 1048
    [InlineData(1048f, 800f, WidthTier.Standard)]
    [InlineData(1919f, null, WidthTier.Standard)]
    [InlineData(1920f, null, WidthTier.Huge)]
    [InlineData(1879f, 2560f, WidthTier.Standard)]  // narrowing past 1920 holds 40: Standard only below 1880
    [InlineData(1880f, 2560f, WidthTier.Huge)]
    [InlineData(3840f, null, WidthTier.Huge)]
    public void The_record_follows_the_window_ladder_and_its_directional_holds(float width, float? from, WidthTier expected)
    {
        TierFacts facts = Window(width, from: from);
        Assert.Equal(expected, ClassicSetupTiers.Of(facts).Tier);
    }

    [Fact]
    public void A_view_mounted_with_no_frame_reads_the_standard_record_and_huge_4k_reads_huges()
    {
        Assert.Equal(WidthTier.Standard, ClassicSetupTiers.Of(TierFacts.Unknown).Tier);
        Assert.Same(ClassicSetupTiers.For(WidthTier.Huge), ClassicSetupTiers.For(WidthTier.Huge4K));
    }

    [Fact]
    public void Compact_is_a_full_bleed_sheet_under_the_title_row_with_a_stacked_footer_and_40_dip_targets()
    {
        var c = ClassicSetupTiers.For(WidthTier.Compact);
        Assert.True(c.Sheet);
        Assert.False(c.ShowsIcon);
        Assert.False(c.SignInSideBySide);
        Assert.True(c.FooterStacked);
        Assert.Equal(48f, c.TopInset);
        Assert.Equal(48f, c.RootTopPad);
        Assert.Equal(16f, c.ContentPadding.Left);
        Assert.Equal(24f, c.ContentPadding.Top);
        Assert.Equal(16f, c.ContentPadding.Right);
        Assert.Equal(16f, c.ContentPadding.Bottom);
        Assert.Equal(40f, c.ButtonHeight);
        Assert.Equal(40f, c.SignatureHeight);
        Assert.Equal(40f, c.BackSize);
        Assert.Equal(40f, c.Target);
        Assert.True(c.BackInContent);
    }

    [Fact]
    public void Small_is_the_762_plate_without_the_icon_column_and_the_cards_side_by_side()
    {
        var s = ClassicSetupTiers.For(WidthTier.Small);
        Assert.False(s.Sheet);
        Assert.False(s.ShowsIcon);
        Assert.Equal(0f, s.IconSpan);
        Assert.Equal(762f, s.PlateWidth);
        Assert.Equal(490f, s.PlateHeight);
        Assert.False(s.FooterStacked);
        Assert.Equal(80f, s.FooterHeight);
        Assert.Equal(210f, s.ProgressColumn);
        Assert.True(s.SignInSideBySide);
        Assert.Equal(132f, s.SignInCardMinHeight);
        Assert.Equal(20f, s.SignInCardPadding);
        Assert.True(s.BackInContent);
        Assert.Equal(32f, s.ButtonHeight);
    }

    [Fact]
    public void Standard_keeps_the_tuned_plate_with_the_192_icon_column_and_stacked_cards()
    {
        var s = ClassicSetupTiers.For(WidthTier.Standard);
        Assert.False(s.Sheet);
        Assert.True(s.ShowsIcon);
        Assert.Equal(192f, s.IconColumn);
        Assert.Equal(24f, s.IconGap);
        Assert.Equal(216f, s.IconSpan);
        Assert.Equal(762f, s.PlateWidth);
        Assert.Equal(490f, s.PlateHeight);
        Assert.False(s.SignInSideBySide);
        Assert.False(s.BackInContent);       // Back stays over the icon column's corner
        Assert.Equal(210f, s.ProgressColumn);
        Assert.Equal(80f, s.FooterHeight);
    }

    [Fact]
    public void Huge_is_the_960_by_600_plate_with_its_own_icon_column_padding_and_qr()
    {
        var h = ClassicSetupTiers.For(WidthTier.Huge);
        Assert.False(h.Sheet);
        Assert.Equal(960f, h.PlateWidth);
        Assert.Equal(600f, h.PlateHeight);
        Assert.Equal(240f, h.IconColumn);
        Assert.Equal(32f, h.IconGap);
        Assert.Equal(32f, h.ContentPadding.Left);
        Assert.Equal(32f, h.FooterPadding.Left);
        Assert.Equal(120f, h.QrSize);
        Assert.True(h.SignInSideBySide);
        Assert.True(h.BackInContent);
        Assert.Equal(32f, h.ButtonHeight);
    }

    [Fact]
    public void The_icon_column_exists_only_at_standard_and_huge_and_the_progress_column_everywhere_but_compact()
    {
        foreach (var tier in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge })
        {
            var layout = ClassicSetupTiers.For(tier);
            Assert.Equal(tier is WidthTier.Standard or WidthTier.Huge, layout.ShowsIcon);
            Assert.Equal(tier != WidthTier.Compact, layout.ProgressColumn > 0f);
            Assert.Equal(tier == WidthTier.Compact, layout.FooterStacked);
            Assert.Equal(tier == WidthTier.Compact ? 40f : 0f, layout.Target);
        }
    }

    [Theory]
    [InlineData(WidthTier.Compact, 400f, 400f)]
    [InlineData(WidthTier.Compact, 300f, 300f)]
    [InlineData(WidthTier.Small, 800f, 762f)]      // the board's 800: 19 each side
    [InlineData(WidthTier.Small, 528f, 496f)]      // the plate shrinks to the window less 16 either side
    [InlineData(WidthTier.Standard, 1200f, 762f)]
    [InlineData(WidthTier.Huge, 2560f, 960f)]
    [InlineData(WidthTier.Huge, 1920f, 960f)]
    public void The_root_width_is_the_window_for_the_sheet_and_the_clamped_plate_otherwise(WidthTier tier, float window, float expected)
        => Assert.Equal(expected, ClassicSetupTiers.For(tier).RootWidth(window));

    [Theory]
    [InlineData(WidthTier.Compact, 860f, 860f)]
    [InlineData(WidthTier.Compact, 330f, 330f)]
    [InlineData(WidthTier.Small, 600f, 490f)]
    [InlineData(WidthTier.Small, 330f, 298f)]      // the 330-tall window: the plate is the window less 16 either side, its body scrolls
    [InlineData(WidthTier.Standard, 960f, 490f)]
    [InlineData(WidthTier.Huge, 1440f, 600f)]
    public void The_root_height_is_the_window_for_the_sheet_and_the_clamped_plate_otherwise(WidthTier tier, float window, float expected)
        => Assert.Equal(expected, ClassicSetupTiers.For(tier).RootHeight(window));

    [Fact]
    public void The_sheet_keeps_the_title_row_clear_and_the_plate_has_no_top_pad()
    {
        Assert.Equal(48f, ClassicSetupTiers.For(WidthTier.Compact).RootTopPad);
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard, WidthTier.Huge })
            Assert.Equal(0f, ClassicSetupTiers.For(tier).RootTopPad);
    }

    [Theory]
    [InlineData(WidthTier.Compact, 400f, 368f)]
    [InlineData(WidthTier.Small, 800f, 714f)]
    [InlineData(WidthTier.Standard, 1440f, 498f)]  // 762 - 48 - 216: the cards stack
    [InlineData(WidthTier.Huge, 2560f, 624f)]      // 960 - 64 - 272: two cards of 306
    public void The_body_is_the_root_less_the_pages_padding_and_the_icon_column(WidthTier tier, float window, float expected)
        => Assert.Equal(expected, ClassicSetupTiers.For(tier).BodyWidth(window));

    [Fact]
    public void The_stacked_footers_bar_is_as_wide_as_the_footer_and_the_rows_is_the_columns()
    {
        Assert.Equal(368f, ClassicSetupTiers.For(WidthTier.Compact).ProgressBarWidth(400f, 162f));
        Assert.Equal(236f, ClassicSetupTiers.For(WidthTier.Compact).ProgressBarWidth(268f, 162f));
        Assert.Equal(162f, ClassicSetupTiers.For(WidthTier.Small).ProgressBarWidth(800f, 162f));
    }

    [Fact]
    public void Back_beside_the_title_clears_it_with_a_spacer_on_the_pages_that_show_back()
    {
        foreach (var tier in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Huge })
        {
            var layout = ClassicSetupTiers.For(tier);
            Assert.True(layout.BackSpacerApplies(Setup.WizardPage.Theme));
            Assert.True(layout.BackSpacerApplies(Setup.WizardPage.LocalPlayback));
            Assert.False(layout.BackSpacerApplies(Setup.WizardPage.Terms));
            Assert.True(layout.BackSpacer >= layout.BackSize);
        }
        Assert.False(ClassicSetupTiers.For(WidthTier.Standard).BackSpacerApplies(Setup.WizardPage.Theme));
    }
}
