// ── Wavee.Tests/CommonTiersTests.cs — the Common blocks' per-tier layout records (themes plan 3.2 / 3.5, TC10) ─────────────────────
//
// `Themes\Common\Rules\Tiers.cs`: the Common blocks (history, logs, diagnostics, the Home customizer, the module page, the not-found page) are drawn by every theme and have no
// board in any of them (owner Q18), so each reads the CONTENT tier and the height tier and nothing else. What is pure about that is the record each tier gets: Compact is the one tier
// that stacks and asks for 40-DIP targets, Small steps the gutter down (and gives a head that cannot fit a row two), Standard is today's layout, Huge is the Standard arrangement under
// a max width (the fill rule), Short scrolls a head that would eat the page. The mounted facts are `Wavee.ShellTests\CommonBlocksTierTests`.

using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using FluentGpu.Dsl;
using Xunit;

namespace Wavee.Tests;

public class CommonTiersTests
{
    static readonly WidthTier[] AllTiers = [WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K];

    static TierFacts Facts(WidthTier content, HeightTier height = HeightTier.Regular)
        => new(content, height, content, 0f, 0f, 0f, false);

    // ── what every block shares ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compact_is_the_one_tier_that_stacks_and_it_asks_for_a_40_dip_target()
    {
        foreach (var tier in AllTiers)
        {
            bool compact = tier == WidthTier.Compact;
            Assert.Equal(compact, CommonTiers.Stacks(tier));
            Assert.Equal(compact ? 40f : 0f, CommonTiers.Target(tier));

            var facts = Facts(tier);
            Assert.Equal(compact, CommonDiagnosticsLayout.For(facts).Stacked);
            Assert.Equal(compact ? 40f : 0f, CommonDiagnosticsLayout.For(facts).Target);
            Assert.Equal(compact, CommonLogsLayout.For(facts).TwoLineRows);
            Assert.Equal(compact ? 40f : 0f, CommonLogsLayout.For(facts).Target);
            Assert.Equal(compact, CommonHistoryLayout.For(facts).Compact);
            Assert.Equal(compact ? 40f : 0f, CommonHistoryLayout.For(facts).Target);
            Assert.Equal(compact, CommonCustomizeLayout.For(facts).StackHeader);
            Assert.Equal(compact ? 40f : 0f, CommonCustomizeLayout.For(facts).Target);
            Assert.Equal(compact ? 40f : 0f, CommonModuleLayout.For(facts).Target);
            Assert.Equal(compact ? 40f : 0f, CommonNotFoundLayout.For(facts).Target);
        }
    }

    [Fact]
    public void The_page_gutter_is_16_at_Compact_32_at_Small_and_the_page_margin_above()
    {
        Assert.Equal(Spacing.PageNarrow, CommonTiers.WideGutter(WidthTier.Compact));
        Assert.Equal(Spacing.XXXL, CommonTiers.WideGutter(WidthTier.Small));
        Assert.Equal(Spacing.PageWide, CommonTiers.WideGutter(WidthTier.Standard));
        Assert.Equal(Spacing.PageWide, CommonTiers.WideGutter(WidthTier.Huge));
        Assert.Equal(Spacing.PageWide, CommonTiers.WideGutter(WidthTier.Huge4K));

        float previous = 0f;
        foreach (var tier in AllTiers)
        {
            float gutter = CommonTiers.WideGutter(tier);
            Assert.True(gutter >= previous, "a wider tier never has a narrower gutter");
            previous = gutter;
        }
    }

    [Fact]
    public void Every_tier_caps_every_column_so_no_line_runs_across_3840()
    {
        foreach (var tier in AllTiers)
        {
            var facts = Facts(tier);
            Assert.True(CommonDiagnosticsLayout.For(facts).MaxWidth is > 0f and < 3840f, "diagnostics " + tier);
            Assert.True(CommonLogsLayout.For(facts).MaxWidth is > 0f and < 3840f, "logs " + tier);
            Assert.True(CommonHistoryLayout.For(facts).MaxWidth is > 0f and < 3840f, "history " + tier);
            Assert.True(CommonCustomizeLayout.For(facts).MaxWidth is > 0f and < 3840f, "customize " + tier);
            Assert.True(CommonModuleLayout.For(facts).MaxWidth is > 0f and < 3840f, "module " + tier);
        }
        Assert.Equal(R.Space.PageMaxW, CommonTiers.ColumnMax);
        Assert.Equal(1000f, CommonDiagnosticsLayout.For(Facts(WidthTier.Huge4K)).MaxWidth);
    }

    [Fact]
    public void Huge_and_Huge4K_are_the_Standard_arrangement_under_the_cap()
    {
        // No theme draws a Huge board for these blocks, so the Huge records ARE the Standard records (the cap is the Huge rule, and it holds on Standard too).
        foreach (var huge in new[] { WidthTier.Huge, WidthTier.Huge4K })
        {
            Assert.Equal(CommonDiagnosticsLayout.For(Facts(WidthTier.Standard)), CommonDiagnosticsLayout.For(Facts(huge)));
            Assert.Equal(CommonLogsLayout.For(Facts(WidthTier.Standard)), CommonLogsLayout.For(Facts(huge)));
            Assert.Equal(CommonHistoryLayout.For(Facts(WidthTier.Standard)), CommonHistoryLayout.For(Facts(huge)));
            Assert.Equal(CommonCustomizeLayout.For(Facts(WidthTier.Standard)), CommonCustomizeLayout.For(Facts(huge)));
            Assert.Equal(CommonModuleLayout.For(Facts(WidthTier.Standard)), CommonModuleLayout.For(Facts(huge)));
        }
    }

    [Fact]
    public void Facts_that_no_root_provides_read_as_Compact_and_Short_never_as_a_wide_layout()
    {
        var unknown = TierFacts.Unknown;
        Assert.True(CommonDiagnosticsLayout.For(unknown).Stacked);
        Assert.True(CommonLogsLayout.For(unknown).TwoLineRows);
        Assert.True(CommonHistoryLayout.For(unknown).Compact);
        Assert.True(CommonCustomizeLayout.For(unknown).StackHeader);
    }

    // ── Standard is today's layout ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Standard_keeps_the_numbers_the_blocks_had_before_the_tiers()
    {
        var facts = Facts(WidthTier.Standard);

        var diagnostics = CommonDiagnosticsLayout.For(facts);
        Assert.Equal((1000f, false, 0f, 460f, 80f, 200f, 16f), (diagnostics.MaxWidth, diagnostics.Stacked, diagnostics.Target, diagnostics.WireBoxWidth, diagnostics.PixelBoxWidth, diagnostics.TagBoxWidth, diagnostics.TreeStep));

        var logs = CommonLogsLayout.For(facts);
        Assert.Equal(Spacing.PageWide, logs.Gutter);
        Assert.False(logs.StackHeader);
        Assert.False(logs.BareHeader);
        Assert.True(logs.ShowCrumbs);
        Assert.False(logs.TwoLineRows);
        Assert.False(logs.OneLineToolbar);
        Assert.Equal(36f, logs.RowExtent);
        Assert.Equal(44f, logs.DetailInset);

        var history = CommonHistoryLayout.For(facts);
        Assert.Equal(Spacing.PageWide, history.Gutter);
        Assert.False(history.Compact);
        Assert.False(history.HeaderScrolls);

        var customize = CommonCustomizeLayout.For(facts);
        Assert.Equal((Spacing.XXXL, false, false, 48f, 8f), (customize.Gutter, customize.StackHeader, customize.Scrolls, customize.RowHeight, customize.RowGap));

        Assert.Equal(32f, CommonModuleLayout.For(facts).Gutter);
    }

    // ── Compact ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compact_fits_the_narrowest_card_and_gives_the_narrow_gutter()
    {
        var facts = Facts(WidthTier.Compact);

        // 300 DIP less two narrow gutters, the card's border and its padding: the text boxes must fit what is left.
        float interior = 300f - 2f * Spacing.PageNarrow - 2f * (1f + 12f);
        var diagnostics = CommonDiagnosticsLayout.For(facts);
        Assert.True(diagnostics.WireBoxWidth <= interior, "the wire boxes fit a 300 window");
        Assert.True(diagnostics.TagBoxWidth <= interior, "the tag box fits a 300 window");
        Assert.True(diagnostics.TreeStep < CommonDiagnosticsLayout.For(Facts(WidthTier.Standard)).TreeStep, "the tree indents less");

        Assert.Equal(Spacing.PageNarrow, CommonLogsLayout.For(facts).Gutter);
        Assert.Equal(Spacing.PageNarrow, CommonHistoryLayout.For(facts).Gutter);
        Assert.Equal(Spacing.PageNarrow, CommonCustomizeLayout.For(facts).Gutter);
        Assert.Equal(Spacing.PageNarrow, CommonModuleLayout.For(facts).Gutter);

        var customize = CommonCustomizeLayout.For(facts);
        Assert.True(customize.RowHeight >= 40f, "the row holds a 40-DIP Up and Down");
        Assert.True(customize.Scrolls, "the list alone does not fit a Compact window");

        var logs = CommonLogsLayout.For(facts);
        Assert.True(logs.ShowCrumbs, "the crumbs stand at Compact: the stock Large crumbs are 40 x 40");
        Assert.True(logs.StackHeader);
        Assert.True(logs.RowExtent > CommonLogsLayout.For(Facts(WidthTier.Standard)).RowExtent, "a two-line row estimates taller");
        Assert.True(logs.DetailInset < CommonLogsLayout.For(Facts(WidthTier.Standard)).DetailInset);
    }

    // ── Small ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Small_is_the_Standard_arrangement_narrowed()
    {
        var small = Facts(WidthTier.Small);

        Assert.Equal(Spacing.XXXL, CommonLogsLayout.For(small).Gutter);
        Assert.Equal(Spacing.XXXL, CommonHistoryLayout.For(small).Gutter);
        Assert.False(CommonLogsLayout.For(small).TwoLineRows, "rows keep their columns");
        Assert.True(CommonLogsLayout.For(small).ShowCrumbs);
        Assert.True(CommonLogsLayout.For(small).StackHeader, "the title and the picker cluster do not fit one row");
        Assert.False(CommonHistoryLayout.For(small).Compact);
        Assert.False(CommonCustomizeLayout.For(small).StackHeader);
        Assert.Equal(0f, CommonLogsLayout.For(small).Target);
    }

    // ── Short height ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Short_scrolls_a_head_that_would_eat_the_page()
    {
        Assert.False(CommonHistoryLayout.For(Facts(WidthTier.Standard)).HeaderScrolls);
        Assert.True(CommonHistoryLayout.For(Facts(WidthTier.Standard, HeightTier.Short)).HeaderScrolls);
        Assert.True(CommonHistoryLayout.For(Facts(WidthTier.Compact)).HeaderScrolls, "a Compact head is tall: it scrolls at any height");

        Assert.False(CommonCustomizeLayout.For(Facts(WidthTier.Standard)).Scrolls);
        Assert.True(CommonCustomizeLayout.For(Facts(WidthTier.Standard, HeightTier.Short)).Scrolls);
    }

    [Fact]
    public void Short_strips_the_log_viewers_head_only_where_the_cluster_is_stacked()
    {
        Assert.False(CommonLogsLayout.For(Facts(WidthTier.Standard, HeightTier.Short)).BareHeader, "a wide short window has room for the full head");
        Assert.False(CommonLogsLayout.For(Facts(WidthTier.Compact)).BareHeader);

        foreach (var tier in new[] { WidthTier.Compact, WidthTier.Small })
        {
            var logs = CommonLogsLayout.For(Facts(tier, HeightTier.Short));
            Assert.True(logs.BareHeader, tier.ToString());
            Assert.True(logs.OneLineToolbar, tier.ToString());
            Assert.False(logs.ShowCrumbs, tier.ToString());
        }
        Assert.False(CommonLogsLayout.For(Facts(WidthTier.Standard, HeightTier.Short)).OneLineToolbar);
    }
}
