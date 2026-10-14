// ── Themes/Classic/Structure/Views/Overlays/ClassicOfflineStrip.cs — Classic's `Overlay.OfflineStrip` (themes plan 6.1, T11a) ─────
//
// The OFFLINE STRIP (the fourth voice's other shape), moved from `Controls.cs`: a notice over KEPT content, not a replacement of the page.
// A caution glyph, the notice, a spacer and, when the host can retry, a standard "Retry". The content state's offline lane (T4a) places it;
// the core decides when it is up (`Shell.FrameRules.ShowsOfflineStrip`: exactly while the session is Offline) and what Retry does.

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
            Icon(Icons.InfoBarBackgroundCircle, ClassicOverlayRules.OfflineIcon, Tok.SystemFillCaution),
            R.Type.TrackMeta(props.Message ?? Loc.Get(Strings.Common.Offline)),
            new BoxEl { Grow = 1 },
        };
        if (props.OnRetry is not null) kids.Add(Button.Standard(Loc.Get(Strings.Common.Retry), props.OnRetry));
        return new BoxEl
        {
            Key = "offline-strip",
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.S),
            Fill = Tok.SystemFillCautionBackground, Corners = CornerRadius4.All(Radii.Control),
            Children = kids.ToArray(),
        };
    }
}
