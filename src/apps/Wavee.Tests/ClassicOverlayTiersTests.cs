// ── Wavee.Tests/ClassicOverlayTiersTests.cs — Classic's overlays, dialogs, toasts and states per window tier (themes plan 3.6, TC9) ──────────────
//
// The pure half: the records `Rules\Tiers.Overlays.cs` publishes (one per width band, keyed on Compact so they are right under either window ladder), the
// arithmetic that turns them into a card's width or a panel's height, and the policy the core hosts read. What only a mounted tree can say (a card inside the
// window, a Compact target at least 40 tall) is `Wavee.ShellTests\ClassicOverlayTierTests`.

using FluentGpu.Controls;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests;

public sealed class ClassicOverlayTiersTests
{
    static readonly WidthTier[] Between = [WidthTier.Small, WidthTier.Standard];

    // ── the band mapping ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_records_are_keyed_on_Compact_and_Huge_so_a_moved_window_edge_moves_no_layout()
    {
        Assert.Same(ClassicOverlayTiers.Compact, ClassicOverlayTiers.For(WidthTier.Compact));
        foreach (var tier in Between) Assert.Same(ClassicOverlayTiers.Standard, ClassicOverlayTiers.For(tier));
        Assert.Same(ClassicOverlayTiers.Huge, ClassicOverlayTiers.For(WidthTier.Huge));
        Assert.Same(ClassicOverlayTiers.Huge, ClassicOverlayTiers.For(WidthTier.Huge4K));
    }

    [Fact]
    public void The_policy_the_core_reads_is_the_same_three_records()
    {
        var policy = ClassicOverlayTiers.Policy;
        Assert.Same(ClassicOverlayTiers.Compact.Core, policy.For(WidthTier.Compact));
        foreach (var tier in Between) Assert.Same(ClassicOverlayTiers.Standard.Core, policy.For(tier));
        Assert.Same(ClassicOverlayTiers.Huge.Core, policy.For(WidthTier.Huge4K));
        Assert.Empty(policy.Validate());
        Assert.Empty(ClassicPolicies.Bundle.Validate());
        Assert.Same(policy, ClassicPolicies.Bundle.Overlays);
    }

    [Fact]
    public void A_structure_that_declares_nothing_is_the_shell_as_it_shipped_at_every_band()
    {
        var shipped = OverlayPolicy.Default;
        foreach (WidthTier tier in Enum.GetValues<WidthTier>())
        {
            var layout = shipped.For(tier);
            Assert.False(layout.ContextSheet);
            Assert.False(layout.Dialog.Fill);
            Assert.False(layout.Vacancy.QuietTitle);
            Assert.Equal(ToastPlacement.BottomRight, layout.Toast.Placement);
        }
        Assert.Same(ThemePolicies.Default.Overlays, OverlayPolicy.Default);
    }

    // ── Compact follows its boards ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_Compact_opens_context_menus_as_a_sheet_and_every_Compact_target_is_at_least_40()
    {
        Assert.True(ClassicOverlayTiers.Compact.Core.ContextSheet);
        Assert.False(ClassicOverlayTiers.Standard.Core.ContextSheet);
        Assert.False(ClassicOverlayTiers.Huge.Core.ContextSheet);

        var c = ClassicOverlayTiers.Compact;
        Assert.True(c.CardButtonHeight >= 40f, "the bare cards' buttons");
        Assert.True(c.StripRetryHeight >= 40f, "the offline strip's Retry");
        Assert.True(c.VacancyActionHeight >= 40f, "the offline vacancy's actions");
        Assert.True(c.Core.Vacancy.ActionHeight >= 40f, "the shared vacancy's action");
        Assert.True(c.Core.Selection.MinHeight >= 48f, "the standalone selection bar holds a 40-DIP command");
        Assert.True(ClassicSheetRules.RowHeight >= 40f, "a sheet row");
        Assert.True(ClassicSheetRules.StripCell >= 40f, "a sheet's quick-action cell");
    }

    [Fact]
    public void The_standard_and_huge_bands_keep_the_tuned_design()
    {
        foreach (var tier in new[] { ClassicOverlayTiers.Standard, ClassicOverlayTiers.Huge })
        {
            Assert.False(tier.CardFills);
            Assert.Equal(0f, tier.CardButtonHeight);
            Assert.Equal(0f, tier.StripRetryHeight);
            Assert.Equal(ClassicOverlayRules.OfflineStripH, tier.StripHeight);
            Assert.Equal(36f, tier.StripHeight);
            Assert.False(tier.VacancyQuietTitle);
            Assert.False(tier.VacancyActionsStack);
            Assert.Null(tier.CardGap);
            Assert.Null(tier.StripCorner);
            Assert.False(tier.Core.Dialog.Fill);
            Assert.Null(tier.Core.Dialog.ContentPadding);
            Assert.Equal(ToastPlacement.BottomRight, tier.Core.Toast.Placement);
        }
    }

    // ── dialogs ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(400f, 368f)]      // the Compact board: 16 either side
    [InlineData(300f, 268f)]      // the window floor
    [InlineData(527f, 495f)]
    public void A_Compact_dialog_fills_the_window_less_16_either_side(float window, float expected)
    {
        var fit = ClassicOverlayTiers.Compact.Core.Dialog;
        Assert.Equal(expected, fit.WidthFor(320f, window));
        Assert.Equal(expected, fit.WidthFor(548f, window));      // whatever rung the opener asked for
        Assert.Equal(MathF.Min(expected, 420f), ClassicOverlayTiers.Compact.CardWidth(380f, 420f, window));   // a bare card keeps its own cap
    }

    [Theory]
    [InlineData(1440f, 320f)]
    [InlineData(1440f, 480f)]
    [InlineData(2560f, 548f)]
    [InlineData(3840f, 548f)]
    public void A_dialog_never_stretches_in_a_wide_window(float window, float requested)
    {
        foreach (var tier in new[] { ClassicOverlayTiers.Standard, ClassicOverlayTiers.Huge })
        {
            float w = tier.Core.Dialog.WidthFor(requested, window);
            Assert.Equal(requested, w);
            Assert.True(w <= 548f, $"{w} at {window}");
        }
        Assert.Equal(380f, ClassicOverlayTiers.Huge.CardWidth(380f, 420f, 3840f));
        Assert.Equal(420f, ClassicOverlayTiers.Huge.CardWidth(420f, 480f, 3840f));
    }

    [Fact]
    public void A_dialog_never_leaves_a_narrow_window_even_where_the_band_keeps_its_own_width()
    {
        var fit = ClassicOverlayTiers.Standard.Core.Dialog;
        Assert.Equal(268f, fit.WidthFor(548f, 300f));
        Assert.Equal(1f, fit.WidthFor(320f, 10f));               // a degenerate window still answers a positive width
        Assert.Equal(268f, ClassicOverlayTiers.Standard.CardWidth(420f, 480f, 300f));
    }

    [Theory]
    [InlineData(330f, 298f, 282f)]      // the window's minimum height
    [InlineData(860f, 828f, 812f)]
    public void A_dialog_taller_than_the_window_clamps_to_it(float window, float compact, float standard)
    {
        Assert.Equal(compact, ClassicOverlayTiers.Compact.Core.Dialog.MaxHeightFor(window));
        Assert.Equal(standard, ClassicOverlayTiers.Standard.Core.Dialog.MaxHeightFor(window));
    }

    [Fact]
    public void A_dialog_body_scrolls_inside_a_short_window_and_keeps_its_cap_in_a_tall_one()
    {
        var fit = ClassicOverlayTiers.Standard.Core.Dialog;
        Assert.Equal(360f, fit.BodyMaxHeight(360f, 900f));               // roomy: the tuned cap
        Assert.Equal(96f, fit.BodyMaxHeight(360f, 330f));                // 330 tall: never under a few rows
        Assert.True(fit.BodyMaxHeight(360f, 500f) < 360f);
        Assert.Equal(336f, ClassicOverlayTiers.Compact.Core.Dialog.BodyWidth(320f, 400f));   // 368 less the 16 + 16 text region
        Assert.Equal(320f, ClassicOverlayTiers.Standard.Core.Dialog.BodyWidth(320f, 1440f)); // the nominal panel
        Assert.Equal(220f, ClassicOverlayTiers.Standard.Core.Dialog.BodyWidth(320f, 300f));   // 268 less the engine's 24 + 24
    }

    [Theory]
    [InlineData(268f, 2, false)]     // a 300-DIP window: two 96-wide buttons fit a 236 inner width side by side
    [InlineData(268f, 1, false)]
    [InlineData(268f, 3, true)]      // three need 304
    [InlineData(368f, 2, false)]     // 400: 336 holds them
    [InlineData(368f, 3, false)]     // and three
    public void A_Compact_dialog_stacks_its_buttons_only_where_they_cannot_sit_side_by_side(float card, int buttons, bool stacks)
        => Assert.Equal(stacks, ClassicOverlayTiers.Compact.Core.Dialog.StacksCommands(card, buttons));

    [Fact]
    public void Compact_gives_every_dialog_the_engines_40_DIP_commands_in_equal_columns_and_the_other_bands_keep_the_engines_own()
    {
        var compact = ClassicOverlayTiers.Compact.Core.Dialog;
        Assert.Equal(ControlSize.Large, compact.CommandSize);
        Assert.Equal(40f, ControlMetrics.For(compact.CommandSize).MinHeight);
        Assert.True(compact.UniformCommands);
        Assert.Equal(96f, compact.CommandMinWidth);
        foreach (var fit in new[] { ClassicOverlayTiers.Standard.Core.Dialog, ClassicOverlayTiers.Huge.Core.Dialog })
        {
            Assert.Equal(ControlSize.Medium, fit.CommandSize);
            Assert.Equal(DialogFit.EngineButtonMinWidth, fit.CommandMinWidth);
            Assert.False(fit.UniformCommands);
        }
    }

    [Fact]
    public void Compact_menus_and_the_toast_lane_take_the_40_DIP_target_and_the_lane_fills_the_window_less_16()
    {
        Assert.Equal(ControlSize.Large, ClassicOverlayTiers.Compact.Core.MenuSize);
        Assert.Equal(40f, MenuFlyout.RowHeightFor(ClassicOverlayTiers.Compact.Core.MenuSize));
        var lane = ClassicOverlayTiers.Compact.Core.Toast.Lane;
        Assert.NotNull(lane);
        Assert.True(lane!.FillWidth);
        Assert.Equal(8f, lane.SideMargin);
        Assert.Equal(ControlSize.Large, lane.ActionSize);
        foreach (var tier in new[] { ClassicOverlayTiers.Standard, ClassicOverlayTiers.Huge })
        {
            Assert.Equal(ControlSize.Medium, tier.Core.MenuSize);
            Assert.Null(tier.Core.Toast.Lane);   // the engine's lane: 24 in, 300-380 cards, a 32-DIP action
        }
    }

    [Theory]
    [InlineData(320f, 2)]            // the engine's own compact rung holds its two buttons
    [InlineData(480f, 3)]            // and its wide rung its three
    [InlineData(548f, 3)]
    public void A_standard_dialog_never_stacks_the_rungs_it_was_designed_for(float card, int buttons)
        => Assert.False(ClassicOverlayTiers.Standard.Core.Dialog.StacksCommands(card, buttons));

    // ── panels ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_panel_is_never_wider_than_the_window_less_the_margin_and_scrolls_inside_a_short_one()
    {
        Assert.Equal(284f, ClassicOverlayTiers.Compact.PanelWidth(380f, 300f));
        Assert.Equal(380f, ClassicOverlayTiers.Compact.PanelWidth(380f, 400f));
        Assert.Equal(380f, ClassicOverlayTiers.Huge.PanelWidth(380f, 3840f));
        Assert.Equal(266f, ClassicOverlayTiers.Compact.PanelHeight(520f, 330f));    // 330 less the 48 chrome row less 2 x 8
        Assert.Equal(520f, ClassicOverlayTiers.Standard.PanelHeight(520f, 1440f));
        // The feed gives way first: the header and the chip rail above it keep their 98.
        Assert.Equal(168f, ClassicOverlayTiers.Compact.PanelBodyHeight(420f, 520f, 98f, 330f));
        Assert.Equal(420f, ClassicOverlayTiers.Standard.PanelBodyHeight(420f, 520f, 98f, 1440f));
    }

    [Fact]
    public void A_Short_window_gives_the_vacancys_glyph_and_lift_up_so_the_words_and_the_actions_fit_210_DIP()
    {
        Assert.True(ClassicOverlayTiers.VacancyShowsGlyph(HeightTier.Regular));
        Assert.False(ClassicOverlayTiers.VacancyShowsGlyph(HeightTier.Short));
        foreach (var tier in new[] { ClassicOverlayTiers.Compact, ClassicOverlayTiers.Standard, ClassicOverlayTiers.Huge })
        {
            Assert.Equal(ClassicOverlayRules.OfflineVacancyLift, tier.VacancyLiftAt(HeightTier.Regular));
            Assert.Equal(0f, tier.VacancyLiftAt(HeightTier.Short));
        }
    }

    // ── toasts ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_Compact_toast_floats_8_above_the_mini_bar_and_a_standard_one_stays_24_above_the_bar_as_shipped()
    {
        var compact = ClassicOverlayTiers.Compact.Core.Toast;
        Assert.Equal(ToastPlacement.BottomCenter, compact.Placement);
        // The engine docks the lane 24 in from the window edge, then `EdgeInset` more: 24 + 40 = 64 = the bar (56) and the 8 gap.
        Assert.Equal(40f, compact.EdgeInsetFor(72f));
        Assert.Equal(40f, compact.EdgeInsetFor(56f));            // the policy's own height is not asked while the band names the mini bar

        var standard = ClassicOverlayTiers.Standard.Core.Toast;
        Assert.Equal(ToastPlacement.BottomRight, standard.Placement);
        Assert.Equal(72f, standard.EdgeInsetFor(72f));           // what `Toast.EdgeInset = PlayerBar.Height` was
    }

    // ── the vacancy and the selection bar ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_Compact_vacancy_is_the_quiet_20_28_rung_with_a_16_gutter_and_the_standard_one_is_the_page_hero()
    {
        var compact = ClassicOverlayTiers.Compact.Core.Vacancy;
        Assert.True(compact.QuietTitle);
        Assert.Equal(16f, compact.Padding);
        Assert.False(ClassicOverlayTiers.Standard.Core.Vacancy.QuietTitle);
        Assert.Null(ClassicOverlayTiers.Standard.Core.Vacancy.Padding);      // the spacing scale's own XXL
        Assert.Null(ClassicOverlayTiers.Standard.Core.Selection.SideInset);  // the spacing scale's own L
    }
}
