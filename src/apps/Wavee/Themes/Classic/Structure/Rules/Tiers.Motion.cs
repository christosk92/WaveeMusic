// ── Themes/Classic/Structure/Rules/Tiers.Motion.cs — how a part moves when the window crosses a tier (themes plan 3.3) ────────────────
//
// A tier crossing commits on the first frame that is not resized and animates there (plan 3.3 rule 3). A part that SURVIVES the crossing keeps its
// node (its scroll offset, its selection, its focus) and FLIPs into its new box; a part the new tier genuinely draws differently swaps under a
// cross-fade; a part that appears or disappears enters or leaves. The recipes below are the ones Classic's parts declare for that. They are the
// production recipes the plan names (the detail hero's 280 ms SmoothOut for a reflow, the row shove for a pushed sibling, the shared ~160 ms
// cross-fade of a tier swap), gathered so a view never spells its own.
//
// Where the content card itself moves on the crossing (the nav pane changes at 528 and 1008), the card is the one projection root
// (`ClassicFrameRules.ContentCardAnim` suppresses its descendants' transitions for that commit) and the page reflows inside its glide; the
// recipes below run on the crossings the card does not move for (a content-ladder edge, the Huge edge).
//
// Public, so `Wavee.Tests` pins them (the assembly has no InternalsVisibleTo).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The motion of a part across a tier crossing.</summary>
public static class ClassicTierMotion
{
    /// <summary>The reflow's duration (the detail hero's crossing recipe).</summary>
    public const float ReflowMs = 280f;

    /// <summary>A surviving part that moves AND resizes (a zone, a band, a grid): position and size tween, the size revealed (a clip), never
    /// scaled, so the content inside is laid out at its final size from the first frame.</summary>
    public static readonly LayoutTransition Reflow = new(
        TransitionChannels.Position | TransitionChannels.Size, TransitionDynamics.Tween(ReflowMs, Easing.SmoothOut), SizeMode.Reveal);

    /// <summary>A surviving part whose HEIGHT alone changes (the artist chart taking four rows past the Huge 4K rung, five below it): the
    /// layout lands once at the final height, the presented extent tweens from the old one and everything below rides the difference, so
    /// nothing under the part jumps and nothing re-lays out per frame (the engine's flow reveal, vertical only).</summary>
    public static readonly LayoutTransition Resize = new(
        TransitionChannels.Size, TransitionDynamics.Tween(ReflowMs, Easing.SmoothOut), Size: SizeMode.FlowReveal);

    /// <summary>A surviving part whose HEIGHT changes with its form and that draws its own fill (the daylist card between the tuned card and the
    /// banner): the height eases through real layout, so the fill shrinks or grows with it and everything after it rides the re-solve frame by
    /// frame. A presented-size reveal laid the card out at its new height at once and left an empty band under it while the next zone slid up.</summary>
    public static readonly LayoutTransition HeightReflow = new(TransitionChannels.Size, TransitionDynamics.Tween(ReflowMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

    /// <summary>A surviving cell that only moves (a grid cell whose column changed, a card pushed along): the position-only FLIP.</summary>
    public static readonly LayoutTransition Shove = new(TransitionChannels.Position, TransitionDynamics.Tween(ReflowMs, Easing.SmoothOut));

    /// <summary>A part the new tier draws as a different tree enters and leaves on its opacity alone (the shared tier cross-fade).</summary>
    public static readonly EnterExit SwapFade = new(Opacity: 0f, Active: true);

    /// <summary>The cross-fade's timing (the plan's ~160 ms).</summary>
    public static MotionTokenDef SwapFadeMotion => MotionTok.ControlFast;
}
