// ── Themes/Zune/Structure/Rules/ZuneMotion.cs — how Zune's parts move, on a tier crossing and in a pivot (themes plan 3.3, 6.3; P5 Z0) ──
//
// The board's "how it moves" (Z-Sizes-Breakpoints), as recipes the Zune views declare so none spells its own:
//
//   · pivots slide, never jump: the pivot row and its panes move with the 250 ms pivot slide (crossing 640 the rotating pivot reorders
//     with it; crossing back it settles in place).
//   · titles scale: a panorama title 64 ↔ 120 ↔ 150 scales from its left edge over 200 ms; the line height of the LARGER size is
//     reserved, so nothing under the title moves.
//   · columns fade, then give way: a track-row column that drops fades out over 120 ms BEFORE its width collapses; one that is added
//     reserves its width first, then fades in.
//   · grids reflow by fading: a column-count change cross-fades the grid; cards never slide across the window.
//   · the play bar keeps its height: the page reserves 72 (`ZuneTiers.BarReserveH`) and the mini 64 ↔ 72 cross-fades.
//
// A crossing commits on the first frame that is not resized and animates there (plan 3.3 rule 3); a surviving part keeps its node.
// Production motion is deliberate: a lane never "fixes" an entrance choreography by changing a recipe here.
//
// Public, so `Wavee.Tests` pins them (the assembly has no InternalsVisibleTo).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Zune;

/// <summary>Zune's motion recipes.</summary>
public static class ZuneMotion
{
    /// <summary>The pivot slide's duration.</summary>
    public const float PivotSlideMs = 250f;

    /// <summary>A pivot word or pane that survives a reorder (the rotating pivot at Compact, a pane change): the position-only slide.</summary>
    public static readonly LayoutTransition PivotSlide = new(TransitionChannels.Position, TransitionDynamics.Tween(PivotSlideMs, Easing.SmoothOut));

    /// <summary>The panorama title's scale duration.</summary>
    public const float TitleScaleMs = 200f;

    /// <summary>A panorama title whose size step changes (64 ↔ 120 ↔ 150): it scales from its left edge; its box reserves the larger
    /// size's line height, so the scale never moves a sibling.</summary>
    public static readonly LayoutTransition TitleScale = new(
        TransitionChannels.Size, TransitionDynamics.Tween(TitleScaleMs, Easing.SmoothOut), SizeMode.ScaleCorrect);

    /// <summary>A dropping column's fade, run before its width collapses.</summary>
    public const float ColumnFadeMs = 120f;

    /// <summary>The width collapse that follows a column's fade (and the reservation that precedes an added column's fade-in).</summary>
    public const float ColumnCollapseMs = 200f;

    /// <summary>A track-row column that leaves or enters: its opacity alone.</summary>
    public static readonly EnterExit ColumnFade = new(Opacity: 0f, Active: true);

    /// <summary>The column's width giving way once it has faded (position and size, revealed: the cells inside lay out at their final
    /// size from the first frame). Delayed by <see cref="ColumnFadeMs"/> where it follows a fade.</summary>
    public static readonly LayoutTransition ColumnCollapse = new(
        TransitionChannels.Position | TransitionChannels.Size, TransitionDynamics.Tween(ColumnCollapseMs, Easing.SmoothOut), SizeMode.Reveal);

    /// <summary>A block whose HEIGHT a crossing (or a docked panel that narrows the content tier) changes while the content below it must not jump:
    /// a hero, the air above or below its copy, the body's top. The height eases through real layout (a size channel: the engine's reflow-shove
    /// rule snaps a position slide that commits beside a live reflow, and the panel's dock IS one), so the copy parked by a spring rides the
    /// block's foot and everything after it rides the re-solve. Padding cannot ease, so such air is a spacer carrying this.</summary>
    public static readonly LayoutTransition HeightResize = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ColumnCollapseMs, Easing.SmoothOut), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

    /// <summary>The slot of a panorama title whose size step changes inside a <see cref="HeightResize"/> block: its height eases with the content
    /// riding its FOOT (the trailing anchor), so the title's baseline never leaves the copy under it while the glyphs scale (<see cref="TitleScale"/>
    /// on the hugging title inside, from its bottom-left: <see cref="TitleOriginX"/>, <see cref="TitleOriginY"/>).</summary>
    public static readonly LayoutTransition TitleSlot = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ColumnCollapseMs, Easing.SmoothOut), Size: SizeMode.Reflow, Anchor: SizeAnchor.Trailing, Axes: SizeAxes.Height);

    /// <summary>The panorama title's scale origin inside its <see cref="TitleSlot"/>: its left edge and its foot.</summary>
    public const float TitleOriginX = 0f, TitleOriginY = 1f;

    /// <summary>The grid cross-fade's duration (the shared tier swap's ~160 ms).</summary>
    public const float GridCrossFadeMs = 160f;

    /// <summary>A grid whose column count changes: the old arrangement fades out and the new one in, under one key per arrangement
    /// inside an unkeyed box (so the two sit in a children list); cards never slide.</summary>
    public static readonly EnterExit GridCrossFade = new(Opacity: 0f, Active: true);

    /// <summary>A part the new tier draws as a different tree (the mini bar ↔ the desktop bar, a panel's sheet ↔ overlay): the same
    /// opacity cross-fade as the grid's.</summary>
    public static readonly EnterExit SwapFade = SwapFamilies.Declare("Zune.SwapFade", new(Opacity: 0f, Active: true));

    /// <summary>A swap host's Enter: <see cref="SwapFade"/> once its view has rendered before (a crossing mounts the other tree, which fades in over
    /// the leaving one), none on the view's first render (a part that arrives with its page arrives with the page's own entrance, as before). Its
    /// Exit is always <see cref="SwapFade"/>. <paramref name="rendered"/> is <see cref="Rendered"/>'s answer.</summary>
    public static EnterExit? SwapEnter(bool rendered) => rendered ? SwapFade : null;

    /// <summary>Has this view rendered before (false on its first render, true after)? Call it once per template, unconditionally, before any branch.</summary>
    public static bool Rendered(FluentGpu.Hooks.RenderContext hooks)
    {
        var seen = hooks.UseRef(false);
        bool was = seen.Value;
        seen.Value = true;
        return was;
    }
}
