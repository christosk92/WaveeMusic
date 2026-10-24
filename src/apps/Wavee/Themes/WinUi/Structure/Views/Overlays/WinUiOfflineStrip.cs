// ── Themes/WinUi/Structure/Views/Overlays/WinUiOfflineStrip.cs — WinUI's `Overlay.OfflineStrip` (themes plan 6.1, P6 W8a) ─────
//
// The OFFLINE STRIP: a notice over KEPT content, not a replacement of the page. WinUI draws it as the Warning InfoBar of W-Offline (the caution glyph,
// "You're offline" semibold and the notice after it on one line, a standard Retry at the end: 48 tall at least, r4); at Compact (W-Offline-Compact)
// the title stacks over the notice and Retry is a 40-tall target. The content state's offline lane places it in the flow above the page; the core
// decides when it is up (`Shell.FrameRules.ShowsOfflineStrip`: exactly while the session is Offline) and what Retry does (reconnect).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>Wavee WinUI's offline strip.</summary>
internal static class WinUiOfflineStrip
{
    /// <summary>The registered template of <see cref="ViewKeys.OverlayOfflineStrip"/>.</summary>
    public static Element Template(OfflineStripProps props, in ViewContext ctx)
    {
        var tier = WinUiOverlayTiers.For(ctx.Tier().Window);
        string title = Loc.Get(Strings.Common.OfflineTitle);
        string notice = props.Message ?? Loc.Get(Strings.Theme.Winui.Overlays.OfflineStripBody);

        // The caution glyph: the InfoBar's filled circle with the "!" over it in the inverse ink.
        Element glyph = new BoxEl
        {
            ZStack = true, Width = WinUiOverlayRules.OfflineIcon, Height = WinUiOverlayRules.OfflineIcon, Shrink = 0f,
            AlignSelf = tier.StripStacked ? FlexAlign.Start : FlexAlign.Center,
            Margin = tier.StripStacked ? new Edges4(0f, WinUiOverlayRules.OfflineStackedGlyphTop, 0f, 0f) : default,
            Children =
            [
                new TextEl(WinUiOverlayRules.OfflineGlyphCircle) { Size = WinUiOverlayRules.OfflineIcon, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.SystemFillCaution },
                new TextEl(WinUiOverlayRules.OfflineGlyphMark) { Size = WinUiOverlayRules.OfflineIcon, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextInverse },
            ],
        };

        Element words = tier.StripStacked
            ? new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Padding = new Edges4(0f, WinUiOverlayRules.OfflineStackedPadY, 0f, WinUiOverlayRules.OfflineStackedPadY),
                Children = [Body(title).Strong(), Body(notice)],
            }
            : new BoxEl
            {
                Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = WinUiOverlayRules.OfflineWordsGap,
                Children =
                [
                    Body(title).Strong() with { Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f },
                    Body(notice) with { Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                ],
            };

        var kids = new List<Element>(3) { glyph, words };
        if (props.OnRetry is not null)
        {
            var retry = Button.Standard(Loc.Get(Strings.Common.Retry), props.OnRetry);
            kids.Add((tier.StripRetryHeight > 0f ? retry with { Height = tier.StripRetryHeight, MinHeight = tier.StripRetryHeight } : retry) with { Shrink = 0f });
        }
        return new BoxEl
        {
            Key = "offline-strip", Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiOverlayRules.OfflineStripGap, MinWidth = 0f,
            MinHeight = WinUiOverlayRules.OfflineStripH, Padding = new Edges4(WinUiOverlayRules.OfflineStripPadL, 0f, WinUiOverlayRules.OfflineStripPadR, 0f),
            Fill = Tok.SystemFillCautionBackground, Corners = CornerRadius4.All(Radii.Control),
            BorderWidth = 1f, BorderColor = WinUiOverlayRules.OfflineStripBorder,
            Children = kids.ToArray(),
        };
    }
}
