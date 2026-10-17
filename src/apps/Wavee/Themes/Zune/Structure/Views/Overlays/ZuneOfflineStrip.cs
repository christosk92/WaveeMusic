// ── Themes/Zune/Structure/Views/Overlays/ZuneOfflineStrip.cs — Zune's `Overlay.OfflineStrip` (themes plan 6.3, P5 Z8a) ────────────────────────────
//
// The OFFLINE STRIP: a notice over KEPT content, not a replacement of the page. Boards Z-Overlays (the amber slab: "you're offline · cached content still works; playback and
// edits resume when you're back" with "retry" as a word in the same ink) and Z-Overlays-Compact (40 tall, edge to edge, the retry word a 40-DIP target). The cloud-off glyph
// and the notice sit in the caution ink on the caution ground, square, no ring. The content state's offline lane places it in the flow above the page; the core decides when
// it is up (exactly while the session is Offline) and what Retry does (reconnect). The board's trailing "reconnecting..." is not drawn: the app does not reconnect by itself
// while Offline, so the strip keeps the retry that does.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>Zune's offline strip.</summary>
internal static class ZuneOfflineStrip
{
    /// <summary>The registered template of <see cref="ViewKeys.OverlayOfflineStrip"/>.</summary>
    public static Element Template(OfflineStripProps props, in ViewContext ctx)
    {
        var layout = ZuneOverlayTiers.Of(ctx.Tier());
        var kids = new List<Element>(4)
        {
            Icon(ZuneOverlayRules.OfflineGlyph, ZuneOverlayRules.OfflineIcon, Tok.SystemFillCaution),
            ZuneText.Cased(R.Type.DenseMeta(props.Message ?? Loc.Get(layout.StripShortNotice ? Strings.Theme.Zune.Overlays.OfflineNoticeShort : Strings.Theme.Zune.Overlays.OfflineNotice)), LabelKind.Label) with
            {
                Color = Tok.SystemFillCaution, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
            },
        };
        // The retry word sits at the strip's end and is at least the strip tall, so the strip's whole height is the target.
        if (props.OnRetry is not null)
            kids.Add(ZuneOverlayParts.InkWord(Loc.Get(Strings.Common.Retry), props.OnRetry, Tok.SystemFillCaution, layout.StripHeight));
        return new BoxEl
        {
            Key = ZuneOverlayRules.OfflineStripKey,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneOverlayRules.OfflineStripGap, MinWidth = 0f,
            Height = layout.StripHeight, Padding = new Edges4(layout.StripPadX, 0f, 0f, 0f),
            Fill = Tok.SystemFillCautionBackground, Corners = R.Shape.All(ShapeRole.Control),
            Children = kids.ToArray(),
        };
    }
}
