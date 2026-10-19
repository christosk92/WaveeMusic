// ── Themes/WinUi/Structure/Views/Frame/WinUiContentState.cs — WinUI's `Shell.ContentState` (themes plan 6.2; P6 W2a) ───────────────────
//
// The core content host decides WHICH state the content card is in (a page, an empty body, the not-found page, the offline lane) and builds the
// page; this template draws the state it was handed:
//
//   Page / EmptyBody   one body box keyed `props.Key` that fills the card; the page, when there is one, is its only child.
//   NotFound           the Common not-found page (the destination's glyph, the line and Go home).
//   OfflineLane        the strip (W-Offline: an InfoBar in the page) in the flow above the kept content, on the tier's inset (16 / 24 / 36 / 48
//                      either side, 16 / 20 / 24 / 32 from the card's top), so the page opens under it.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's content states.</summary>
internal static class WinUiContentState
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellContentState"/>.</summary>
    public static Element Template(ContentStateProps props, in ViewContext ctx)
    {
        var frame = WinUiTiers.FrameOf(ctx.Tier());   // read unconditionally: the facts read is a hook
        return props.Kind switch
        {
            ContentStateKind.NotFound => CommonNotFound.Block(props.Key, props.Glyph, props.GoHome),
            ContentStateKind.OfflineLane => new BoxEl
            {
                Key = "offline-lane",
                Direction = 1, AlignItems = FlexAlign.Stretch, MinWidth = 0f, Shrink = 0f,
                Padding = WinUiFrameRules.OfflineLanePadding(frame.InsetX, frame.InsetTop),
                Children = [Slots.View(ViewKeys.OverlayOfflineStrip, props.Strip)],
            },
            _ => new BoxEl
            {
                Key = props.Key, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = props.Page is { } page ? [page] : [],
            },
        };
    }
}
