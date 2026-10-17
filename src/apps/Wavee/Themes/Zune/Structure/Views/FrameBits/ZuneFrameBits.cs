// ── Themes/Zune/Structure/Views/FrameBits/ZuneFrameBits.cs — Zune's small frame views (themes plan 6.3; P5 Z2a) ────────────────────────
//
// No board draws these (owner Q18: built from Zune's parts). Each is a picture over a decision the core keeps:
//
//   History menu   the engine menu over the rows the core listed (newest first, "View all history" when the stack is longer); the style's
//                  menu entry makes it Zune's square plate with 36-tall rows inset 16.
//   File drop      the centred cue: the square raised plate with the flyout shadow and the lowercase hint; its opacity is BOUND to the
//                  drag-over signal, so a drag hover never re-renders the shell.
//   Drag chip      the engine layer that draws the chip under the pointer; the core resolves what the dragged payload is called.
//   Cover scrim    the setup wizard's dim (the smoke fill, 250 ms linear, 0 under reduced motion; hit-test transparent unless dimming).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>Zune's small frame views.</summary>
internal static class ZuneFrameBits
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellHistoryMenu"/>.</summary>
    public static Element HistoryMenu(HistoryMenuProps props, in ViewContext ctx)
        => MenuFlyout.Create(props.Items, props.Close, size: OverlayFit.MenuSize) with { Key = ZuneFrameRules.HistoryMenuKey };

    /// <summary>The registered template of <see cref="ViewKeys.ShellFileDrop"/>.</summary>
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
                    HitTestVisible = false, Padding = ZuneFrameRules.FileDropPadding, Corners = R.Shape.All(ShapeRole.Overlay),
                    Fill = Prop.Of(static () => R.Layer.FloatingChrome), Shadow = Elevation.Flyout,
                    Children = [R.Type.TrackTitle(Loc.Get(Strings.LocalFile.DropHint)) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label) }],
                },
            ],
        };
    }

    /// <summary>The registered template of <see cref="ViewKeys.ShellDragChip"/>.</summary>
    public static Element DragChip(DragChipProps props, in ViewContext ctx) => DragPreviewLayer.Of(props.Preview);

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverScrim"/>: the setup wizard's dim (the plate opens with the engine's
    /// own overlay scrim off, so this box is the only dim).</summary>
    public static Element CoverScrim(CoverScrimProps props, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        var mounted = hooks.UseRef(false);
        bool dim = props.Dim();
        float ms = Design.Reduced ? 0f : ZuneFrameRules.CoverScrimFadeMs;
        float target = dim ? 1f : 0f;
        hooks.UseTransition(AnimChannel.Opacity, mounted.Value ? 1f - target : target, target, ms, Easing.Linear, DepKey.From(dim));
        mounted.Value = true;
        return new BoxEl { Grow = 1f, Fill = Tok.FillSmoke, Opacity = target, HitTestVisible = dim };
    }
}
