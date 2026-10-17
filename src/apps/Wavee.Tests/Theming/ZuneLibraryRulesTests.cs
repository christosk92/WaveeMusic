// ── Wavee.Tests/Theming/ZuneLibraryRulesTests.cs — Zune's library ladder, records and policy (P5 Z7) ──────────────────────────────────────
//
// The pure rules of `Themes\Zune\Structure\Rules\ZuneLibraryRules.cs` against the boards (Z-Library-*, -Compact, -Small, -Huge) and the plan: the layout ladder
// (split from 640 of the page's own width, held 24 either way, resolved by id), the records per content tier, Huge 4K reading Huge's, and the policy the core host reads.

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
