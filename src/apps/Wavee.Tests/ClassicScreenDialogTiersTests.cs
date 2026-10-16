// ── Wavee.Tests/ClassicScreenDialogTiersTests.cs — the report and crash dialogs' records per window tier (themes plan 3.5, 3.6; TC8) ─────────────
//
// Pure rules, no host: `ClassicScreenDialogTiers` (Rules\Tiers.Dialogs.cs) carries the numbers of the boards C-Feedback / C-Crash at -Compact, -Small and -Huge, and
// the overlay fit the two form dialogs open through (`ClassicOverlayTiers`: the Compact 40-DIP command row, the Huge two-pane 1040 card). Asserted on the records and
// the arithmetic, never on pixels. What only a mounted tree can say (the card inside the window, no horizontal overflow, Compact targets, a 330-tall card) is
// `Wavee.ShellTests\TierLayoutTests.Dialogs`.

using FluentGpu.Controls;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public sealed class ClassicScreenDialogTiersTests
{
    // ── the records ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_record_is_the_window_tiers_and_huge_4K_reads_hugeS()
    {
        foreach (WidthTier tier in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge })
            Assert.Equal(tier, ClassicScreenDialogTiers.For(tier).Tier);
        Assert.Same(ClassicScreenDialogTiers.For(WidthTier.Huge), ClassicScreenDialogTiers.For(WidthTier.Huge4K));
    }

    [Fact]
    public void A_dialog_mounted_with_no_frame_roots_tiers_is_the_standard_design()
        => Assert.Same(ClassicScreenDialogTiers.For(WidthTier.Standard), ClassicScreenDialogTiers.Of(TierFacts.Unknown));

    [Theory]
    [InlineData(400f, WidthTier.Compact)]
    [InlineData(800f, WidthTier.Small)]
    [InlineData(1440f, WidthTier.Standard)]
    [InlineData(2560f, WidthTier.Huge)]
    [InlineData(3840f, WidthTier.Huge)]
    public void The_record_follows_the_facts_the_window_resolves(float width, WidthTier expected)
        => Assert.Equal(expected, ClassicScreenDialogTiers.Of(WindowTiers.Resolve(ClassicTiers.Policy, width, 900f, width)).Tier);

    [Fact]
    public void Compact_folds_the_preview_and_lifts_every_target_to_40()
    {
        var c = ClassicScreenDialogTiers.For(WidthTier.Compact);
        Assert.Equal(ClassicReportPreview.Collapsed, c.Preview);
        Assert.True(c.PreviewFolds);
        Assert.True(c.Touch);
        Assert.True(c.Target >= 40f);
        Assert.True(c.TextFieldHeight >= 40f, "C-Feedback-Compact: 40-tall fields");
        Assert.False(c.TwoPane);
        Assert.Equal(0f, c.FormPaneWidth);
    }

    [Fact]
    public void Small_and_Standard_keep_the_tuned_548_card_and_its_stock_controls()
    {
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard })
        {
            var l = ClassicScreenDialogTiers.For(tier);
            Assert.Equal(ClassicReportPreview.Inline, l.Preview);
            Assert.False(l.Touch);
            Assert.Equal(0f, l.Target);
            Assert.Equal(32f, l.TextFieldHeight);
            Assert.Equal(500f, l.NominalBodyWidth);          // 548 less the engine's 24 either side
            Assert.Equal(220f, l.PreviewHeight);
            Assert.False(l.TwoPane);
        }
    }

    [Fact]
    public void Huge_is_a_two_pane_card_with_the_form_pane_of_the_board()
    {
        var h = ClassicScreenDialogTiers.For(WidthTier.Huge);
        Assert.True(h.TwoPane);
        Assert.Equal(ClassicReportPreview.Pane, h.Preview);
        Assert.Equal(472f, h.FormPaneWidth);                 // C-Feedback-Huge: the 520 pane less its 24 padding either side
        Assert.Equal(1040f, ClassicScreenDialogTiers.HugeCardWidth);
        Assert.False(h.Touch);
    }

    // ── the body width ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(400f, 336f)]       // the card fills the window less 16 (368), less its 16 + 16 text region
    [InlineData(300f, 236f)]
    [InlineData(527f, 463f)]
    public void A_Compact_body_fills_the_card_the_window_leaves_it(float window, float expected)
        => Assert.Equal(expected, ClassicScreenDialogTiers.BodyWidth(ClassicScreenDialogTiers.For(WidthTier.Compact), window));

    [Theory]
    [InlineData(1440f, 500f)]      // the stock card's 548 less 24 + 24
    [InlineData(800f, 500f)]
    [InlineData(528f, 448f)]       // the card is the window less 16 either side (496), less the engine's 48
    public void A_Standard_or_Small_body_is_the_stock_500_and_never_wider_than_its_card(float window, float expected)
    {
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard })
            Assert.Equal(expected, ClassicScreenDialogTiers.BodyWidth(ClassicScreenDialogTiers.For(tier), window));
    }

    [Theory]
    [InlineData(1920f)]
    [InlineData(2560f)]
    [InlineData(3840f)]
    public void A_Huge_body_is_the_whole_text_region_of_the_1040_card(float window)
    {
        var h = ClassicScreenDialogTiers.For(WidthTier.Huge);
        Assert.Equal(992f, ClassicScreenDialogTiers.BodyWidth(h, window));
        // The panes add up: the form, a gap either side of the divider, the divider, the preview.
        float body = ClassicScreenDialogTiers.BodyWidth(h, window);
        Assert.Equal(body, h.FormPaneWidth + 2f * ClassicScreenDialogTiers.PaneGap + ClassicScreenDialogTiers.DividerWidth + ClassicScreenDialogTiers.PreviewPaneWidth(h, body), 0.01f);
        Assert.True(ClassicScreenDialogTiers.PreviewPaneWidth(h, body) > 400f, "the report pane stays readable");
    }

    [Fact]
    public void The_form_column_is_the_pane_in_two_panes_and_the_body_elsewhere()
    {
        var huge = ClassicScreenDialogTiers.For(WidthTier.Huge);
        Assert.Equal(472f, ClassicScreenDialogTiers.FormWidth(huge, 992f));
        var compact = ClassicScreenDialogTiers.For(WidthTier.Compact);
        Assert.Equal(336f, ClassicScreenDialogTiers.FormWidth(compact, 336f));
    }

    [Fact]
    public void The_shape_a_body_opens_with_prefers_the_live_viewport_then_the_facts_then_the_nominal_width()
    {
        var facts = WindowTiers.Resolve(ClassicTiers.Policy, 400f, 860f, 400f);
        Assert.Equal(336f, ClassicScreenDialogTiers.Open(in facts, 400f).BodyWidth);       // the live width
        Assert.Equal(236f, ClassicScreenDialogTiers.Open(in facts, 300f).BodyWidth);       // a within-tier resize the facts have not seen
        Assert.Equal(336f, ClassicScreenDialogTiers.Open(in facts, 0f).BodyWidth);         // no live width: the facts' own 400
        var unknown = TierFacts.Unknown;
        var bare = ClassicScreenDialogTiers.Open(in unknown, 0f);
        Assert.Equal(WidthTier.Standard, bare.Layout.Tier);
        Assert.Equal(500f, bare.BodyWidth);
    }

    // ── the overlay fit the two form dialogs open through ────────────────────────────────────────────────────────────────

    [Fact]
    public void Compact_lifts_the_form_dialogs_commands_to_40_and_fills_the_window()
    {
        var fit = ClassicOverlayTiers.Compact.Core.Dialog;
        Assert.Equal(ClassicScreenDialogTiers.CompactTarget, ControlMetrics.For(fit.CommandSize).MinHeight);   // the engine's own buttons, at 40
        Assert.Null(fit.FormWidth);
        Assert.Equal(368f, fit.FormCardWidthFor(548f, 400f));
        Assert.Equal(268f, fit.FormCardWidthFor(548f, 300f));
        Assert.Equal(fit.BodyWidth(500f, 400f), fit.FormBodyWidth(500f, 400f));
    }

    [Fact]
    public void Only_Huge_gives_a_form_dialog_a_wider_card_and_every_other_dialog_keeps_its_548_cap()
    {
        var huge = ClassicOverlayTiers.Huge.Core.Dialog;
        Assert.Equal(ClassicScreenDialogTiers.HugeCardWidth, huge.FormWidth);
        Assert.Equal(1040f, huge.FormCardWidthFor(548f, 2560f));
        Assert.Equal(992f, huge.FormBodyWidth(500f, 2560f));
        Assert.Equal(548f, huge.MaxWidth);
        Assert.Equal(548f, huge.WidthFor(548f, 2560f));                              // any other dialog never stretches
        Assert.Equal(ControlSize.Medium, huge.CommandSize);
        foreach (var fit in new[] { ClassicOverlayTiers.Standard.Core.Dialog })
        {
            Assert.Null(fit.FormWidth);
            Assert.Equal(ControlSize.Medium, fit.CommandSize);
            Assert.Equal(548f, fit.FormCardWidthFor(548f, 1440f));
            Assert.Equal(500f, fit.FormBodyWidth(500f, 1440f));
        }
    }

    [Fact]
    public void A_form_card_never_leaves_a_window_narrower_than_it()
    {
        var huge = ClassicOverlayTiers.Huge.Core.Dialog;
        Assert.Equal(868f, huge.FormCardWidthFor(548f, 900f));                       // 900 less 16 either side
        Assert.Equal(1f, huge.FormCardWidthFor(548f, 10f));
    }
}
