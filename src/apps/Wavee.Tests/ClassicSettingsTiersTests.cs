// ── Wavee.Tests/ClassicSettingsTiersTests.cs — Classic's Settings records per content tier (themes plan 3.5, 3.6; TC8) ────────────────────────────
//
// Pure rules, no host: the record the Settings page reads (`ClassicSettingsTiers`) carries the numbers of the boards — C-Settings-Compact / -Small / -Huge,
// C-SettingsSections-*, C-ThemeOptions-* — and Standard is the tuned design unchanged (the 1000 column, the 208 x 172 cards). Asserted on the records, never on pixels.

using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class ClassicSettingsTiersTests
{
    [Fact]
    public void Compact_stacks_the_rows_lifts_every_target_to_40_and_scrolls_the_tabs()
    {
        var l = ClassicSettingsTiers.For(WidthTier.Compact);
        Assert.Equal(WidthTier.Compact, l.Tier);
        Assert.True(l.Touch);
        Assert.Equal(40f, l.Target);
        Assert.True(l.StacksControls);                       // the control sits under the label (C-SettingsSections-Compact)
        Assert.Equal(48f, l.ToggleMinWidth);                 // a toggle keeps label | state | 48 x 40
        Assert.Equal(40f, l.ToggleMinHeight);
        Assert.Equal(44f, l.TabHeight);                      // the Compact tab strip is 44 tall, scrolls, bleeds, fades
        Assert.True(l.TabsScroll);
        Assert.True(l.TabsBleed);
        Assert.Equal(1, l.Columns);
        Assert.Equal(ClassicThemePickerForm.Grid, l.Picker);  // 2-up grid, 72 thumbs, the name alone
        Assert.Equal(72f, l.PickerThumbH);
        Assert.False(l.PickerShowsText);
        Assert.Equal(12f, l.PickerPad);
    }

    [Fact]
    public void Small_keeps_the_32_controls_and_scrolls_the_tabs_and_the_picker_as_strips()
    {
        var l = ClassicSettingsTiers.For(WidthTier.Small);
        Assert.False(l.Touch);
        Assert.False(l.StacksControls);
        Assert.Equal(36f, l.ToggleMinHeight);                // the engine's compact switch
        Assert.Equal(0f, l.ToggleMinWidth);
        Assert.Equal(40f, l.TabHeight);
        Assert.True(l.TabsScroll);
        Assert.Equal(1, l.Columns);
        Assert.Equal(1000f, l.ColumnMaxW);
        Assert.Equal(ClassicThemePickerForm.Strip, l.Picker);
        Assert.Equal(190f, l.PickerCardW);                   // C-Settings-Small: 190-wide cards, 72 thumbs, one line of text
        Assert.Equal(72f, l.PickerThumbH);
        Assert.True(l.PickerShowsText);
    }

    [Fact]
    public void Standard_is_the_tuned_design_unchanged()
    {
        var l = ClassicSettingsTiers.For(WidthTier.Standard);
        Assert.False(l.Touch);
        Assert.False(l.StacksControls);
        Assert.Equal(0f, l.TabHeight);                       // the stock strip
        Assert.False(l.TabsScroll);
        Assert.Equal(1, l.Columns);
        Assert.Equal(1000f, l.ColumnMaxW);                   // N1: the 1000 left-aligned column
        Assert.Equal(ClassicThemePickerForm.Wrap, l.Picker);
        Assert.Equal(208f, l.PickerCardW);
        Assert.Equal(172f, l.PickerCardH);
        Assert.Equal(92f, l.PickerThumbH);
        Assert.Equal(16f, l.PickerPad);
    }

    [Fact]
    public void Huge_is_two_1000_columns_32_apart_left_aligned_with_the_five_up_cards()
    {
        var l = ClassicSettingsTiers.For(WidthTier.Huge);
        Assert.Equal(2, l.Columns);
        Assert.Equal(1000f, l.ColumnMaxW);
        Assert.Equal(32f, l.ColumnGap);
        Assert.Equal(ClassicThemePickerForm.Wrap, l.Picker);
        Assert.Equal(184f, l.PickerCardW);                   // C-Settings-Huge: five-up inside the 1000 column
        Assert.Equal(92f, l.PickerThumbH);
        Assert.False(l.Touch);
        Assert.False(l.TabsScroll);
    }

    [Theory]
    // tier, title glyph, caption on the title's line, space above, space under (C-Settings-* and C-SettingsSections-*; Standard is the tuned 32 / 8, stacked, no glyph)
    [InlineData(WidthTier.Compact, true, false, 20f, 6f)]
    [InlineData(WidthTier.Small, true, true, 24f, 8f)]
    [InlineData(WidthTier.Standard, false, false, 32f, 8f)]
    [InlineData(WidthTier.Huge, true, true, 28f, 4f)]
    public void The_head_and_the_section_headers_follow_the_boards(WidthTier tier, bool glyph, bool inline, float top, float bottom)
    {
        var l = ClassicSettingsTiers.For(tier);
        Assert.Equal(glyph, l.TitleGlyph);
        Assert.Equal(inline, l.SectionInline);
        Assert.Equal(top, l.SectionTop);
        Assert.Equal(bottom, l.SectionBottom);
    }

    [Fact]
    public void Huge_4K_reads_the_Huge_record_because_Classic_draws_no_4K_band()
        => Assert.Same(ClassicSettingsTiers.For(WidthTier.Huge), ClassicSettingsTiers.For(WidthTier.Huge4K));

    [Fact]
    public void A_view_mounted_alone_reads_the_Standard_record()
        => Assert.Same(ClassicSettingsTiers.For(WidthTier.Standard), ClassicSettingsTiers.Of(TierFacts.Unknown));

    [Fact]
    public void The_record_follows_the_content_tier_the_ladder_resolves()
    {
        // 400 wide: Compact; 800 wide (less the 48 rail): Small; 1440 (less the 320 pane): Standard; 2560 (less the pane): Huge. The page reads the CONTENT tier.
        static ClassicSettingsLayout At(float window, float content)
            => ClassicSettingsTiers.Of(WindowTiers.Resolve(ClassicTiers.Policy, window, 960f, content));
        Assert.Equal(WidthTier.Compact, At(400f, 400f).Tier);
        Assert.Equal(WidthTier.Small, At(800f, 752f).Tier);
        Assert.Equal(WidthTier.Standard, At(1440f, 1120f).Tier);
        Assert.Equal(WidthTier.Huge, At(2560f, 2240f).Tier);
    }

    // ── the column split (the Huge boards' Appearance and Playback) ────────────────────────────────────────────────────────

    [Fact]
    public void A_tab_with_fewer_than_two_sections_stays_one_column()
    {
        Assert.Equal(-1, ClassicSettingsTiers.SplitAt([]));
        Assert.Equal(-1, ClassicSettingsTiers.SplitAt([9]));
    }

    [Theory]
    [InlineData(new[] { 1, 1 }, 1)]
    [InlineData(new[] { 6, 2, 7, 1 }, 2)]            // Appearance: Theme (6) and Lyrics (2) | Full screen (7) and Home (1): 8 against 8
    [InlineData(new[] { 1, 8, 3, 2 }, 2)]            // Playback: Runtime + Audio | Sound + Player bar and Video ... the boundary that balances
    [InlineData(new[] { 10, 1, 1 }, 1)]              // the first section alone outweighs the rest: it still gets a column to itself
    [InlineData(new[] { 1, 1, 10 }, 2)]
    public void The_split_is_the_boundary_that_balances_the_two_columns(int[] weights, int expectedAt)
        => Assert.Equal(expectedAt, ClassicSettingsTiers.SplitAt(weights));

    [Fact]
    public void A_section_with_no_rows_still_weighs_one()
        => Assert.Equal(1, ClassicSettingsTiers.SplitAt([0, 0]));

    // ── the Compact picker grid ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(400f, 165f)]    // 400 - 2 x 16 gutter - 2 x 12 card padding - 2 border - 12 gap = 330, over two columns
    [InlineData(360f, 145f)]
    [InlineData(300f, 115f)]
    [InlineData(200f, 96f)]     // never under the grid's floor
    public void A_grid_card_is_half_the_card_less_its_gaps(float window, float expected)
        => Assert.Equal(expected, ClassicSettingsTiers.For(WidthTier.Compact).GridCardWidth(window, ClassicTiers.Page(WidthTier.Compact).Gutter));

    [Fact]
    public void A_grid_card_with_its_name_row_is_a_target_over_40()
        => Assert.True(ClassicSettingsTiers.For(WidthTier.Compact).PickerThumbH + ClassicSettingsTiers.GridNameRowH >= 40f);
}
