// ── Themes/Common/CommonPageBusy.cs — a page's loading bar, one copy for every theme ────────────────────────────────────────
//
// The owner (2026-11-15): placeholders are flat and say nothing about time; a page whose placeholders are still up after half a second
// says so once, with one thin bar, shown at least 0.4 s. The core owns the WHEN (`Shell.UI.cs` `DeferredPage`: the slot's
// `LoadingScope` and `UseDelayedPending`, handed to the template as `ContentStateProps.Busy`); each theme's `Shell.ContentState`
// template owns the WHERE and draws it through this one helper, at its own busy-bar height from its Rules record:
//
//   the layer   a pass-through overlay over the page body box (`page:<route>`), the last layer of its ZStack, so it travels with the
//               page through its enter, exit and park, sits above the page and below the shell's masthead, the card stroke and popups
//   the slot    a fixed box on the body box's top edge at the bar's height, always laid out in the overlay, so the page never moves
//   the bar     the active theme's own `ProgressBar.Indeterminate` across the full width (Classic and WinUI: the stock Fluent bar; Zune:
//               its `LineProgress` line), mounted and unmounted by a `Flow.Show` with an opacity Enter/Exit: a fade in and out
//
// The same shape as Home's busy bars (`ClassicHomeFacetRow`, `WinUiHomeFacetRow`, `ZuneHomePage`'s status layer): a fixed slot, a
// Show, an opacity fade, the indeterminate bar. Nothing here hit-tests.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee.Themes.Common;

/// <summary>The page's loading bar, drawn over the page body box's top edge while the page is busy.</summary>
internal static class CommonPageBusy
{
    /// <summary>The fixed slot's key and the bar's key (the inspector finds the bar as <c>page-busy:bar</c>).</summary>
    public const string SlotKey = "page-busy", BarKey = "page-busy:bar";

    static readonly MotionTokenDef s_fade = MotionTokenDef.Eased(Design.Motion.Fast, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);

    /// <summary>A content state's page body box keyed <paramref name="key"/> (<c>page:&lt;route&gt;</c> / <c>page-empty:&lt;route&gt;</c>) with the
    /// page's loading bar over its top edge. With a page and a <paramref name="busy"/> signal the box is a ZStack of two layers: the page in
    /// a column that is what the body box was before (so the page's own layout is untouched: a page that hugs its content still hugs), then
    /// <see cref="Layer"/>. Without either it is the plain column (an empty body draws no bar).</summary>
    public static BoxEl PageBox(string key, Element? page, IReadSignal<bool>? busy, float height) => new()
    {
        Key = key, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, ZStack = page is not null && busy is not null,
        Children = page is not { } pg ? [] : busy is null ? [pg] :
        [
            new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Children = [pg] },
            Layer(busy, height),
        ],
    };

    /// <summary>The bar's layer: a pass-through box that fills the page body box, whose first child is the fixed slot on the box's top
    /// edge, <paramref name="height"/> tall. The bar shows while <paramref name="busy"/> is true.</summary>
    public static Element Layer(IReadSignal<bool> busy, float height) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, AlignItems = FlexAlign.Stretch,
        Justify = FlexJustify.Start, HitTestPassThrough = true,
        Children =
        [
            new BoxEl
            {
                Key = SlotKey, Height = height, MinWidth = 0f, Shrink = 0f, HitTestVisible = false, Direction = 1,
                Children =
                [
                    Flow.Show(() => busy.Value, new BoxEl
                    {
                        Key = BarKey, Height = height, MinWidth = 0f, HitTestVisible = false,
                        Enter = new EnterExit(Opacity: 0f, Active: true),
                        Exit = new EnterExit(Opacity: 0f, Active: true),
                        Transition = s_fade,
                        Children = [ProgressBar.Indeterminate(width: float.NaN)],
                    }),
                ],
            },
        ],
    };
}
