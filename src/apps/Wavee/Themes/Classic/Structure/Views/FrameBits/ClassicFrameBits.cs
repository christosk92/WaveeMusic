// ── Themes/Classic/Structure/Views/FrameBits/ClassicFrameBits.cs — Classic's small frame views (themes plan 6.1, T4c) ───────
//
// From `Shell.UI.cs` (the file-drop cue, the setup wizard's cover scrim, the Back/Forward history menu, the drag chip mount). Each is a picture over a decision the core keeps:
//
//   History menu   `MenuFlyout` over the rows the core listed (newest first, a "View all history" tail when the stack is longer).
//   File drop      the centred drop pill: its opacity is BOUND to the drag-over signal, so a drag hover never re-renders the shell.
//   Drag chip      the layer that draws the chip following the pointer; the core resolves what the dragged payload is called.
//   Cover scrim    the setup wizard's dim over the shell (Tok.FillSmoke, 250 ms linear, 0 under reduced motion; nothing ticks while the
//                  cover holds still, and the box is hit-test transparent whenever it is not dimming).
//
// (The frame-rate overlay is `Themes\Common\CommonFps`: a developer HUD is one copy for every theme, T11c.)

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's small frame views.</summary>
internal static class ClassicFrameBits
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellHistoryMenu"/>.</summary>
    public static Element HistoryMenu(HistoryMenuProps props, in ViewContext ctx) => MenuFlyout.Create(props.Items, props.Close);

    /// <summary>The registered template of <see cref="ViewKeys.ShellFileDrop"/>: the centred drop pill — bound opacity, so a drag
    /// hover never re-renders the shell.</summary>
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
                    Padding = ClassicFrameBitsRules.FileDropPillPadding, Corners = CornerRadius4.All(Radii.Control),
                    Fill = Prop.Of(static () => Tok.FillSolidBase), BorderColor = Prop.Of(static () => Tok.AccentDefault),
                    BorderWidth = 1f, Shadow = Elevation.Dialog,
                    Children = [new TextEl(Loc.Get(Strings.LocalFile.DropHint)) { Size = ClassicFrameBitsRules.FileDropTextSize, Color = Tok.TextPrimary }],
                },
            ],
        };
    }

    /// <summary>The registered template of <see cref="ViewKeys.ShellDragChip"/>.</summary>
    public static Element DragChip(DragChipProps props, in ViewContext ctx) => DragPreviewLayer.Of(props.Preview);

    /// <summary>The registered template of <see cref="ViewKeys.ShellCoverScrim"/>: the setup wizard's cover scrim (ch 28 parity item
    /// 22). The plate opens with the engine's own overlay scrim OFF (a shell is always behind it in 0.3), so this box is the only dim.</summary>
    public static Element CoverScrim(CoverScrimProps props, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        var mounted = hooks.UseRef(false);
        bool dim = props.Dim();
        float ms = Design.Reduced ? 0f : ClassicFrameBitsRules.CoverScrimFadeMs;
        float target = dim ? 1f : 0f;
        hooks.UseTransition(AnimChannel.Opacity, mounted.Value ? 1f - target : target, target, ms, Easing.Linear, DepKey.From(dim));
        mounted.Value = true;
        return new BoxEl { Grow = 1f, Fill = Tok.FillSmoke, Opacity = target, HitTestVisible = dim };
    }
}
