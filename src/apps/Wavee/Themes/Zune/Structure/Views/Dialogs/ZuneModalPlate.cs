// ── Themes/Zune/Structure/Views/Dialogs/ZuneModalPlate.cs — Zune's bare modal plate (themes plan 6.3, P5 Z8a) ───────────────────────────────────────
//
// The off-ladder modal plate the log-out card and the play-a-link card share. Board Z-Dialogs: the dialog ground (#1f1f1f on dark, the control table's plate), no ring, square,
// the dialog shadow, padded 24 with a 14 gap, the title in the heading voice (26/300, lowercase) and the commands as words 20 apart: the accent disc with its word first, the
// other commands as plain words. Not a ContentDialog: no title band, no separator, no command-row fill. The popup around it (modal chrome, focus trap, no light dismiss) is the
// core's `PopupOptions`.
//
// The plate's width, padding and target come from the window tier's record (`ZuneOverlayTiers`): at Compact it fills the window less 16 either side (board Z-Dialogs-Compact)
// with 16 / 20 padding and 40-DIP commands; at every other tier it keeps its own width, never wider than the window less the margin, so a narrow Small window never overflows it.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Zune;

/// <summary>The bare modal plate.</summary>
internal static class ZuneModalPlate
{
    /// <summary>The plate: <paramref name="width"/> wide at the tiers that keep their own width, bounded by <paramref name="max"/> and the window.</summary>
    public static Element Plate(in ViewContext ctx, float width, float max, Element[] children)
    {
        var layout = ZuneOverlayTiers.Of(ctx.Tier());
        float viewport = ctx.Hooks.UseContextSignal(Viewport.Size).Value.Width;
        float w = layout.CardWidth(width, max, viewport);
        return new BoxEl
        {
            Key = ZuneOverlayRules.PlateKey, Direction = 1, Width = w, MaxWidth = w,
            Corners = R.Shape.All(ShapeRole.Overlay), Fill = Tok.FillLayerAlt, Shadow = Elevation.Dialog,
            Padding = layout.CardPadding, Gap = ZuneOverlayRules.PlateGap,
            Children = children,
        };
    }

    /// <summary>The plate's inner width in a window <paramref name="viewportWidth"/> wide (the plate less its padding).</summary>
    public static float InnerWidth(ZuneOverlayLayout layout, float width, float max, float viewportWidth)
        => MathF.Max(1f, layout.CardWidth(width, max, viewportWidth) - layout.CardPadding.Horizontal);

    /// <summary>The plate's title: the heading voice, lowercase.</summary>
    public static Element Title(string text) => ZuneText.Heading(text);

    /// <summary>The command row: the words 20 apart, starting at the leading edge, wrapping where the window is narrow (a Compact plate stacks them).</summary>
    public static Element Commands(params Element[] words) => new BoxEl
    {
        Key = ZuneOverlayRules.CommandsKey, Direction = 0, Gap = ZuneOverlayRules.CommandGap, AlignItems = FlexAlign.Center, Wrap = true, MinWidth = 0f, Children = words,
    };
}
