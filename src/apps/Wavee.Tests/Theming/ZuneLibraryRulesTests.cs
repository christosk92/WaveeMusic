// ── Wavee.Tests/Theming/ZuneLibraryRulesTests.cs — Zune's library ladder, records and policy (P5 Z7) ──────────────────────────────────────
//
// The pure rules of `Themes\Zune\Structure\Rules\ZuneLibraryRules.cs` against the boards (Z-Library-*, -Compact, -Small, -Huge) and the plan: the layout ladder
// (split from 640 of the page's own width, held 24 either way, resolved by id), the records per content tier, Huge 4K reading Huge's, and the policy the core host reads.

using Wavee.Themes.Common;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ZuneLibraryRulesTests
{
    [Fact]
    public void The_pane_word_drops_at_small_and_the_navigator_is_capped_at_huge()
    {
        // B-Library: Small's 300 pane shrank "open album" to "o..."; the verb stays in the "..." menu there.
        Assert.False(ZuneLibraryTiers.For(WidthTier.Small, single: false).OpenWord);
        Assert.True(ZuneLibraryTiers.For(WidthTier.Standard, single: false).OpenWord);
        Assert.True(ZuneLibraryTiers.For(WidthTier.Compact, single: true).OpenWord);
        // ... and a 2400 page is never one 1960-wide list of tiny rows.
        Assert.Equal(960f, ZuneLibraryTiers.For(WidthTier.Huge, single: false).NavMaxW);
        Assert.Equal(960f, ZuneLibraryTiers.For(WidthTier.Huge4K, single: false).NavMaxW);
        Assert.True(float.IsNaN(ZuneLibraryTiers.For(WidthTier.Standard, single: false).NavMaxW));
    }

    static int Fresh(float width) => ZuneLibraryTiers.Layout.Resolve(width, -1);

    static int From(int previous, float width) => ZuneLibraryTiers.Layout.Resolve(width, previous);

    [Theory]
    [InlineData(300f, 0)]
    [InlineData(639f, 0)]
    [InlineData(640f, 1)]
    [InlineData(1440f, 1)]
    public void A_fresh_resolve_splits_at_the_line(float width, int expected) => Assert.Equal(expected, Fresh(width));

    [Fact]
    public void The_split_holds_24_either_way()
    {
        // Widening from the drill-in commits at 664, narrowing from the split below 616.
        Assert.Equal(0, From(0, 650f));
        Assert.Equal(1, From(0, 664f));
        Assert.Equal(1, From(1, 625f));
        Assert.Equal(0, From(1, 615f));
    }

    [Fact]
    public void The_ladder_is_published_by_id_on_zunes_tier_policy()
    {
        Assert.True(ZuneTiers.Policy.TryGetLadder(LibraryLadders.Layout, out var ladder));
        Assert.Same(ZuneLibraryTiers.Layout, ladder);
        Assert.True(LibraryLadders.IsSingleColumn(Fresh(400f)));
        Assert.False(LibraryLadders.IsSingleColumn(Fresh(1000f)));
    }

    [Fact]
    public void Every_record_stands_on_the_frames_gutter_and_compact_is_the_drill_in()
    {
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
        {
            var rec = ZuneLibraryTiers.For(tier, single: false);
            Assert.Equal(ZuneTiers.Frame(rec.Tier).Gutter, rec.Gutter);
            Assert.False(rec.Single);
        }
        var compact = ZuneLibraryTiers.For(WidthTier.Compact, single: true);
        Assert.True(compact.Single);
        Assert.Equal(WidthTier.Compact, compact.Tier);
        // Every control of the one-column head is a 40 target.
        Assert.True(compact.HeadH >= 40f);
        // A split page in a Compact content tier (the ladders' holds differ by a frame) reads Small's record.
        Assert.Equal(WidthTier.Small, ZuneLibraryTiers.For(WidthTier.Compact, single: false).Tier);
    }

    [Fact]
    public void Standard_is_the_boards_three_columns()
    {
        var rec = ZuneLibraryTiers.For(WidthTier.Standard, single: false);
        Assert.Equal(220f, rec.ReaderNavW);
        Assert.Equal(380f, rec.PaneW);
        Assert.Equal(40f, rec.ColGap);
        Assert.Equal(28f, rec.HeadH);
        Assert.False(rec.SortMenu);
    }

    [Fact]
    public void Small_draws_the_sort_as_one_button_and_a_narrower_pane()
    {
        var rec = ZuneLibraryTiers.For(WidthTier.Small, single: false);
        Assert.True(rec.SortMenu);
        Assert.True(rec.PaneW < ZuneLibraryTiers.For(WidthTier.Standard, single: false).PaneW);
        Assert.Equal(96f, rec.CardCellMin);
    }

    [Fact]
    public void Huge_4K_draws_Huges_page()
    {
        var huge = ZuneLibraryTiers.For(WidthTier.Huge, single: false);
        var k = ZuneLibraryTiers.For(WidthTier.Huge4K, single: false);
        Assert.Equal(huge.PaneW, k.PaneW);
        Assert.Equal(huge.ReaderNavW, k.ReaderNavW);
        Assert.Equal(huge.ColGap, k.ColGap);
    }

    /// <summary>The flex pass of the page's body row (one pass, no redistribution after a clamp: FlexLayout.Arrange): the widths of the navigator and the pane in a row of
    /// <paramref name="row"/> wide with the record's gap, for a navigator capped at <paramref name="navCap"/>.</summary>
    static (float Nav, float Pane) Flex(ZuneLibraryLayout rec, float row, float navCap)
    {
        float free = row - rec.ColGap - rec.PaneW;
        float grow = rec.NavGrow + rec.PaneGrow;
        float nav = free > 0f && grow > 0f ? free * rec.NavGrow / grow : 0f;
        float pane = rec.PaneW + (free > 0f && grow > 0f ? free * rec.PaneGrow / grow : 0f);
        nav = Math.Clamp(nav, rec.NavMinW, float.IsNaN(navCap) ? float.MaxValue : navCap);
        pane = Math.Clamp(pane, rec.PaneW, float.IsNaN(rec.PaneMaxW) ? rec.PaneW : rec.PaneMaxW);
        return (nav, pane);
    }

    [Theory]
    [InlineData(WidthTier.Huge, 1824f)]    // a 1920 window
    [InlineData(WidthTier.Huge, 2464f)]    // a 2560 window: the page column is the window less two gutters
    [InlineData(WidthTier.Huge4K, 2400f)]  // 3840: the 2400 cap
    public void Huge_fills_the_column_with_a_navigator_and_a_growing_pane(WidthTier tier, float row)
    {
        var rec = ZuneLibraryTiers.For(tier, single: false);
        // The board's songs column is 640; it rests there and grows with the navigator (2 shares to its 1) up to the cap.
        Assert.Equal(640f, rec.PaneW);
        Assert.True(rec.PaneGrow > 0f && rec.NavGrow > rec.PaneGrow);
        Assert.Equal(1200f, rec.PaneMaxW);
        // A grid has no cap (its cells fill); a list stops at 960.
        Assert.True(float.IsNaN(rec.GridNavMaxW));
        Assert.Equal(960f, rec.NavMaxW);
        // The pair reaches the column's far edge for a grid and leaves under a fifth empty for a list (it used to leave 816 of 2464).
        var grid = Flex(rec, row, rec.GridNavMaxW);
        Assert.True(grid.Nav + rec.ColGap + grid.Pane >= row * 0.9f, $"grid: {grid} in {row}");
        Assert.True(grid.Nav + rec.ColGap + grid.Pane <= row + 0.5f);
        var list = Flex(rec, row, rec.NavMaxW);
        Assert.True(list.Nav + rec.ColGap + list.Pane >= row * 0.8f, $"list: {list} in {row}");
        Assert.True(list.Nav + rec.ColGap + list.Pane <= row + 0.5f);
        Assert.True(list.Nav <= 960.5f);
    }

    [Fact]
    public void Below_huge_the_pane_is_a_fixed_width_and_the_navigator_takes_the_rest()
    {
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard })
        {
            var rec = ZuneLibraryTiers.For(tier, single: false);
            Assert.Equal(0f, rec.PaneGrow);
            Assert.Equal(1f, rec.NavGrow);
            Assert.True(float.IsNaN(rec.PaneMaxW));
            Assert.True(float.IsNaN(rec.NavMaxW) && float.IsNaN(rec.GridNavMaxW));
        }
    }

    [Fact]
    public void The_artists_list_is_the_boards_280_at_huge()
    {
        Assert.Equal(280f, ZuneLibraryTiers.For(WidthTier.Huge, single: false).ReaderNavW);
        Assert.Equal(280f, ZuneLibraryTiers.For(WidthTier.Huge4K, single: false).ReaderNavW);
    }

    static TierFacts Facts(WidthTier content, HeightTier height) => new(content, height, content, 1000f, 1000f, height == HeightTier.Short ? 330f : 900f, false, 0UL, ZuneTiers.Policy);

    [Fact]
    public void The_short_rung_tightens_the_page_and_folds_the_pane_header()
    {
        var regular = ZuneLibraryTiers.Of(Facts(WidthTier.Standard, HeightTier.Regular), single: false);
        var @short = ZuneLibraryTiers.Of(Facts(WidthTier.Standard, HeightTier.Short), single: false);
        Assert.False(regular.Short);
        Assert.False(regular.PaneBrief);
        Assert.Equal(8f, regular.PageTop);
        Assert.Equal(16f, regular.BodyGap);
        Assert.True(@short.Short);
        Assert.True(@short.PaneBrief);
        Assert.Equal(4f, @short.PageTop);
        Assert.Equal(8f, @short.BodyGap);
        // Everything else is the width tier's: the same columns.
        Assert.Equal(regular.PaneW, @short.PaneW);
        Assert.Equal(regular.ColGap, @short.ColGap);
        // A split head is one row either way.
        Assert.False(@short.OneRow);
    }

    [Fact]
    public void Compact_on_the_short_rung_has_a_one_row_head_of_40_targets()
    {
        var compact = ZuneLibraryTiers.Of(Facts(WidthTier.Compact, HeightTier.Short), single: true);
        Assert.True(compact.OneRow);
        Assert.False(compact.ShowCount);
        Assert.True(compact.ViewMenuOnly);
        Assert.True(compact.HeadH >= 40f);
        Assert.Equal(4f, compact.HeadGap);
        var regular = ZuneLibraryTiers.Of(Facts(WidthTier.Compact, HeightTier.Regular), single: true);
        Assert.False(regular.OneRow);
        Assert.True(regular.ShowCount);
        Assert.False(regular.ViewMenuOnly);
        Assert.Equal(12f, regular.HeadGap);
    }

    [Fact]
    public void A_view_with_no_frame_reads_the_regular_rung()
    {
        // TierFacts.Unknown is Short at size 0 (default HeightTier); with no policy the record is Standard's Regular one.
        var rec = ZuneLibraryTiers.Of(TierFacts.Unknown, single: false);
        Assert.False(rec.Short);
        Assert.Equal(WidthTier.Standard, rec.Tier);
    }

    [Theory]
    [InlineData(WidthTier.Compact, 16f, 40f)]
    [InlineData(WidthTier.Small, 24f, 0f)]
    [InlineData(WidthTier.Standard, 48f, 0f)]
    [InlineData(WidthTier.Huge, 48f, 0f)]
    [InlineData(WidthTier.Huge4K, 64f, 0f)]
    public void The_module_page_stands_on_the_frames_gutter_in_a_centred_2400_column(WidthTier tier, float gutter, float target)
    {
        var layout = ZuneModuleTiers.For(tier);
        Assert.Equal(gutter, layout.Gutter);
        Assert.Equal(ZuneFrameRules.PageCap, layout.MaxWidth);
        Assert.Equal(target, layout.Target);
        Assert.True(layout.Centered);
        Assert.Equal(0f, ZuneModuleTiers.TopReserve);
        // Centred is opt-in: a layout a structure did not build stands at the left edge (Classic's).
        Assert.False(default(CommonModuleLayout).Centered);
    }

    [Fact]
    public void The_policy_states_the_grids_gaps_per_tier_and_smalls_cell_floor_and_no_resting_width()
    {
        var policy = ZuneLibraryTiers.Policy;
        Assert.Equal(96f, policy.SmallCardCellMin);
        Assert.Equal(96f, policy.CardCellMinFor(WidthTier.Small));
        Assert.True(float.IsNaN(policy.NavWidthFor(WidthTier.Huge, reader: false)));
        Assert.Equal(ZuneLibraryTiers.For(WidthTier.Compact, single: true).GridGaps, policy.GridGapsFor(WidthTier.Compact));
        Assert.Equal(ZuneLibraryTiers.For(WidthTier.Standard, single: false).GridGaps, policy.GridGapsFor(WidthTier.Standard));
        Assert.Equal(ZuneLibraryTiers.For(WidthTier.Huge, single: false).GridGaps, policy.GridGapsFor(WidthTier.Huge4K));
        // The bundle carries it.
        Assert.Same(policy, ZunePolicies.For(ZuneParameters.Default).LibraryTiers);
    }
}
