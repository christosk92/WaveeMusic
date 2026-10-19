// ── Wavee.Tests/Theming/WinUiOverlayTests.cs — WinUI's overlay and dialog numbers, pure (P6 W8a) ─────────────────────────────────────────
//
// The dialog widths W8b's bodies read (588 the card, 760 the report form at Standard, the two-pane 1040 at Huge, the whole window under 641), the
// Compact touch targets, and the policy's three bands.

using Wavee.Themes.WinUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class WinUiOverlayTests
{
    static DialogFit Dialog(WidthTier tier) => WinUiOverlayTiers.For(tier).Core.Dialog;

    [Fact]
    public void Card_caps_at_588_and_fills_below_641()
    {
        Assert.True(Dialog(WidthTier.Compact).Fill);
        Assert.Equal(0f, Dialog(WidthTier.Compact).Inset);
        Assert.Equal(588f, Dialog(WidthTier.Standard).MaxWidth);
        Assert.Equal(588f, Dialog(WidthTier.Small).MaxWidth);
        Assert.Equal(588f, Dialog(WidthTier.Huge).MaxWidth);
    }

    [Fact]
    public void Report_form_is_760_at_standard_588_at_small_and_two_pane_at_huge()
    {
        Assert.Equal(760f, Dialog(WidthTier.Standard).FormCardWidthFor(548f, 1440f, report: true));
        Assert.Equal(588f, Dialog(WidthTier.Small).FormCardWidthFor(548f, 800f, report: true));
        Assert.Equal(1040f, Dialog(WidthTier.Huge).FormCardWidthFor(548f, 2560f, report: true));
        // the crash prompt keeps the card
        Assert.Equal(548f, Dialog(WidthTier.Standard).FormCardWidthFor(548f, 1440f));
        // never wider than the window less the inset
        Assert.Equal(700f - 32f, Dialog(WidthTier.Standard).FormCardWidthFor(548f, 700f, report: true));
    }

    [Fact]
    public void Form_body_is_the_card_less_its_padding()
    {
        var fit = Dialog(WidthTier.Standard);
        Assert.True(fit.FormBodyWidth(548f, 1440f, report: true) < 760f);
        Assert.True(fit.FormBodyWidth(548f, 1440f, report: true) > fit.FormBodyWidth(548f, 1440f));
    }

    [Fact]
    public void Compact_targets_are_40()
    {
        var compact = WinUiOverlayTiers.Compact;
        Assert.Equal(40f, compact.ChipTarget);
        Assert.Equal(40f, compact.StripRetryHeight);
        Assert.Equal(40f, compact.CardButtonHeight);
        Assert.Equal(0f, WinUiOverlayTiers.Standard.ChipTarget);
    }

    [Fact]
    public void Policy_publishes_the_bands()
    {
        Assert.Same(WinUiOverlayTiers.Standard.Core, WinUiOverlayTiers.Policy.Standard);
        Assert.Same(WinUiOverlayTiers.Compact.Core, WinUiOverlayTiers.Policy.Compact);
        Assert.Same(WinUiOverlayTiers.Huge.Core, WinUiOverlayTiers.Policy.Huge);
    }
}
