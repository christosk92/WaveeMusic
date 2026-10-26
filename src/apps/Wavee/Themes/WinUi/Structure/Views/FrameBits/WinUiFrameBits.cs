// ── Themes/WinUi/Structure/Views/FrameBits/WinUiFrameBits.cs — WinUI's small frame views (themes plan 6.2; P6 W2a) ──────────────────────
//
// Each is a picture over a decision the core keeps:
//
//   History menu   WinUI's MenuFlyout (W-Flyouts: acrylic r8, 36 rows, the icon 16 at 12) over the rows the core listed (newest first, a
//                  "View all history" tail); the title bar's ⋯ and the tab menus reuse it.
//   File drop      the centred drop pill (a TeachingTip-like card: the solid base, the accent stroke, the dialog shadow); its opacity is BOUND
//                  to the drag-over signal, so a drag hover never re-renders the shell.
//   Drag chip      the layer that draws the chip following the pointer (the engine's drag preview).
//   Cover scrim    the setup wizard's dim over the shell (smoke, 167 ms linear, 0 under reduced motion; hit-test transparent unless dimming).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's small frame views.</summary>
internal static class WinUiFrameBits
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellHistoryMenu"/>.</summary>
    public static Element HistoryMenu(HistoryMenuProps props, in ViewContext ctx) => MenuFlyout.Create(props.Items, props.Close, size: OverlayFit.MenuSize);

    /// <summary>The registered template of <see cref="ViewKeys.ShellFileDrop"/>: the centred drop pill, its opacity bound to the drag-over.</summary>
    public static Element FileDrop(FileDropProps props, in ViewContext ctx)
    {
        var over = props.Over;
        return new BoxEl
        {
            Grow = 1f, HitTestPassThrough = true, Direction = 1, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Opacity = Prop.Of(() => over.Value ? 1f : 0f),
            Children =
            [
                new BoxEl
                {
                    HitTestVisible = false,
                    Padding = WinUiFrameRules.FileDropPadding, Corners = CornerRadius4.All(Radii.Card),
                    Fill = Prop.Of(static () => Tok.FillSolidBase), BorderColor = Prop.Of(static () => Tok.AccentDefault),
                    BorderWidth = 1f, Shadow = Elevation.Flyout,
                    Children = [new TextEl(Loc.Get(Strings.LocalFile.DropHint)) { Size = WinUiFrameRules.FileDropTextSize, Color = Tok.TextPrimary }],
                },
            ],
        };
    }

    /// <summary>The registered template of <see cref="ViewKeys.ShellDragChip"/>.</summary>
    public static Element DragChip(DragChipProps props, in ViewContext ctx) => DragPreviewLayer.Of(props.Preview);

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverScrim"/>: the setup wizard's dim (the only dim: the plate opens with the
    /// engine's own scrim off).</summary>
    public static Element CoverScrim(CoverScrimProps props, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        var mounted = hooks.UseRef(false);
        bool dim = props.Dim();
        float ms = Design.Reduced ? 0f : WinUiFrameRules.CoverScrimFadeMs;
        float target = dim ? 1f : 0f;
        hooks.UseTransition(AnimChannel.Opacity, mounted.Value ? 1f - target : target, target, ms, Easing.Linear, DepKey.From(dim));
        mounted.Value = true;
        return new BoxEl { Grow = 1f, Fill = Tok.FillSmoke, Opacity = target, HitTestVisible = dim };
    }
}
