// ── Themes/Classic/Structure/Views/Overlays/ClassicOfflineStrip.cs — Classic's `Overlay.OfflineStrip` (themes plan 6.1, T11a) ─────
//
// The OFFLINE STRIP (the fourth voice's other shape), moved from `Controls.cs`: a notice over KEPT content, not a replacement of the page.
// The cloud-off glyph in the caution ink, the notice ("You're offline · showing saved content."), a spacer and, when the host can retry, a
// standard "Retry". Tuned Classic X1 (board C-Offline): a 36-tall caution-tinted bar, r4, 12 either side, 10 between the glyph and the body-rung
// notice. The content state's offline lane (T4a) places it in the flow above the page; the core decides when it is up
// (`Shell.FrameRules.ShowsOfflineStrip`: exactly while the session is Offline) and what Retry does (reconnect). The board's trailing
// "Reconnecting…" is not drawn: the app does not reconnect by itself while Offline, so the strip keeps the Retry that does.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's offline strip.</summary>
internal static class ClassicOfflineStrip
{
    /// <summary>The registered template of <see cref="ViewKeys.OverlayOfflineStrip"/>.</summary>
    public static Element Template(OfflineStripProps props, in ViewContext ctx)
    {
        var kids = new List<Element>(4)
        {
            Icon(ClassicOverlayRules.OfflineGlyph, ClassicOverlayRules.OfflineIcon, Tok.SystemFillCaution),
            Body(props.Message ?? Loc.Get(Strings.Common.Offline)) with { Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
            new BoxEl { Grow = 1 },
        };
        if (props.OnRetry is not null) kids.Add(Button.Standard(Loc.Get(Strings.Common.Retry), props.OnRetry));
        return new BoxEl
        {
            Key = "offline-strip",
            Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicOverlayRules.OfflineStripGap, MinWidth = 0f,
            Height = ClassicOverlayRules.OfflineStripH, Padding = new Edges4(ClassicOverlayRules.OfflineStripPadX, 0f, ClassicOverlayRules.OfflineStripPadX, 0f),
            Fill = Tok.SystemFillCautionBackground, Corners = CornerRadius4.All(Radii.Control),
            Children = kids.ToArray(),
        };
    }
}
