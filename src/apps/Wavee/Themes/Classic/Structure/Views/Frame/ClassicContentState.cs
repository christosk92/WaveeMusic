// ── Themes/Classic/Structure/Views/Frame/ClassicContentState.cs — Classic's `Shell.ContentState` (themes plan 6.1, T4a) ────
//
// From `Shell.UI.cs`: `PageBody`'s body box, `OfflineLane`. The core content host decides WHICH state the
// content region is in (`Shell.FrameRules.ContentBodyFor` / `ShowsOfflineStrip`: a page, an empty body, the not-found page, the offline lane) and builds
// the page; this template DRAWS the state it was handed. The elements, keys and numbers are what the shell built before the move:
//
//   Page / EmptyBody   one body box keyed `props.Key` (`page:<route>` / `page-empty:<route>`) that fills the card; the page, when
//                      there is one, is its only child.
//   NotFound           the Common not-found page (`Themes\Common\CommonNotFound`, T11c): a centred column keyed
//                      `page-notfound:<route>|<arg>` with the destination's own glyph, the not-found line and Go home.
//   OfflineLane        the strip in the flow ABOVE the kept content (tuned Classic X1, board C-Offline): the core stacks the lane over the
//                      page column, so the page opens under the strip instead of under a floating card; caution-tinted, the chrome's
//                      accent Reconnect is the loud layer.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's content states.</summary>
internal static class ClassicContentState
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellContentState"/>.</summary>
    public static Element Template(ContentStateProps props, in ViewContext ctx) => props.Kind switch
    {
        ContentStateKind.NotFound => CommonNotFound.Block(props.Key, props.Glyph, props.GoHome),
        ContentStateKind.OfflineLane => OfflineLane(props),
        _ => PageBox(props.Key, props.Page),
    };

    private static BoxEl PageBox(string key, Element? page) => new()
    {
        Key = key, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children = page is null ? [] : [page],
    };

    /// <summary>The page-scale offline strip (ch 29 W9 C / W10) in the flow above the kept content: the page gutter either side, 16 from the
    /// card's top (tuned Classic X1).</summary>
    private static BoxEl OfflineLane(ContentStateProps props) => new()
    {
        Key = "offline-lane",
        Direction = 1, AlignItems = FlexAlign.Stretch, MinWidth = 0f, Shrink = 0f,
        Padding = ClassicFrameRules.OfflineLanePadding,
        Children = [Slots.View(ViewKeys.OverlayOfflineStrip, props.Strip)],
    };
}
