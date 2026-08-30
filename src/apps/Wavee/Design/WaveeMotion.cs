using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

// Wavee's ONE motion vocabulary. Hover/press motion everywhere is Wavee's identity — a media app should feel alive
// under the pointer where WinUI is deliberately still — but it is a SYSTEM, not 20 hand-picked numbers. This file owns
// both halves of that system:
//
//   · the three interaction SCALE tiers (below) — every HoverScale/PressScale in the app resolves to one of them;
//   · the Fluent duration ladder — every HoverDurationMs/PressDurationMs/BrushTransitionMs snaps to one of its rungs.
//
// The actual animation is still the engine's: MotionRecipes.*/MotionHooks.* for entrances, MotionTok.* for the
// declarative Transition/While* surface, and the engine's own HoverFade/PressFade slab channels for the scale cues.
//
// ── WHY THE TIERS READ Motion.ReducedMotion AND THE ENGINE DOES NOT ─────────────────────────────────────────────
// Investigated before building this (AnimScheduler.Hover.cs → SeedInteractFade → AnimScheduler.SeedEased): the
// hover/press scale path is the ONLY animated channel in the engine that carries no ReducedMotionPolicy. Structural
// motion goes through SeedMotion/KeyframesMotion, which consult ReducedSnap() and place the channel at its end value;
// the declarative While*/Transition surface carries a MotionTokenDef whose .Reduced policy is honoured the same way.
// But InteractionAnim.HoverT/PressT are seeded by SeedEased(), which has no policy parameter, and SceneRecorder.cs
// (~:772) composites `1 + (HoverScale-1)*HoverT` unconditionally. So a reduced-motion user WOULD still get every
// scale cue, fully eased. The engine is read-only for this wave, and the values are authored app-side anyway, so the
// correct minimal fix is app-side and lives HERE: the tier accessors return 1f under reduced motion, which makes the
// recorder's `MathF.Abs(isc - 1f) > 0.0008f` test fail and skips the transform entirely. This is reduced-motion-
// as-a-VALUE (the canon rule) — a value read during Render, never a hook-order-breaking branch.
// NOT double-suppression: nothing else nulls these. If the engine ever grows a policy on the interaction channels,
// delete the `Motion.ReducedMotion ?` reads here and keep the tiers.
public static class WaveeMotion
{
    // ── Interaction scale tiers ────────────────────────────────────────────────────────────────────────────────
    /// <summary>Chips, list/track rows, small toggles, settings rows, inline pickers — a surface the pointer crosses
    /// often. Barely-there, so a scrolling list doesn't shimmer.</summary>
    public static readonly ScaleTier ScaleSubtle = new(1.02f, 0.98f);

    /// <summary>Buttons, CTAs, pills, secondary circles — a deliberate, discrete target. This is the WaveeCta media
    /// pill's tier (it authored 1.04/0.97 first; the press deepened to the ladder's 0.96).</summary>
    public static readonly ScaleTier ScaleStandard = new(1.04f, 0.96f);

    /// <summary>Media FABs, transport + primary play, on-artwork circles, the "…" corner — a round affordance that
    /// floats over media and is the page's loudest interactive object. Absorbs the old 1.06/1.07/1.10/1.16 and
    /// 0.86/0.90/0.92/0.94 family.</summary>
    public static readonly ScaleTier ScaleEmphatic = new(1.07f, 0.92f);

    // ── Fluent duration ladder (ms) — Common_themeresources_any.xaml ───────────────────────────────────────────
    // Interaction durations only. Structural enter/exit choreography (TransitionDynamics.Tween on a page/pane/flyout)
    // stays authored per surface: those are asymmetric by design (enter decelerates long, exit accelerates short) and
    // are not on this ladder.
    /// <summary>WinUI ControlFasterAnimationDuration — brush cross-fades and press acknowledgement.</summary>
    public const float Faster = 83f;
    /// <summary>WinUI ControlFastAnimationDuration — hover reveals, small state changes.</summary>
    public const float Fast = 167f;
    /// <summary>WinUI ControlNormalAnimationDuration — the workhorse: recolor, icon swap, material cross-fade.</summary>
    public const float Standard = 250f;

    /// <summary>List/shelf entrance stagger per item — the offset between one item's entrance and the next's. Reached
    /// through <see cref="WaveeEntrance"/>, never multiplied by hand at a call site (that is what produced the
    /// uncapped, reduced-motion-blind entrances this rung replaced).</summary>
    public const float StaggerMs = 40f;

    /// <summary>A drill-in surface's MASTHEAD stagger: the offset between the display line and the metadata line under
    /// it, assigned to the two-child container's <c>Element.Stagger</c>.
    /// <para>A separate rung from <see cref="StaggerMs"/> on purpose, and the distinction is mechanical rather than
    /// aesthetic: that one is the per-ITEM offset of an unbounded list and is therefore capped by
    /// <see cref="WaveeEntrance.StaggerCap"/>; this one offsets exactly TWO authored lines, so the engine's uncapped
    /// <c>index × ms</c> spelling is safe and the cascade is bounded by construction. It lives here so the surfaces
    /// that wear the masthead (RecentsPage, HomeSectionPage) read ONE number instead of authoring it twice.</para>
    /// <para>Reduced motion is a VALUE at the call site (<c>Motion.ReducedMotion ? 0f : MastheadStaggerMs</c>), never a
    /// branch — <c>Element.Stagger</c> is a plain float and takes no motion token.</para></summary>
    public const float MastheadStaggerMs = 45f;
}

/// <summary>
/// THE list/shelf entrance recipe — the one place a Wavee surface says "my items arrive in sequence".
///
/// <para>It is a short rise + fade + a hair of blur, offset by <see cref="WaveeMotion.StaggerMs"/> per item and
/// <b>capped</b> at <see cref="StaggerCap"/>: item 0..8 cascade, everything after shares item 8's delay and lands
/// together. The cap is not a nicety — <c>Element.Stagger</c> (the engine's declarative parent-side spelling) is
/// <i>index × ms</i> with no ceiling, so a 50-row list authored that way takes two seconds to finish arriving, and the
/// rows nobody is looking at are the ones still animating.</para>
///
/// <para>REDUCED MOTION IS A VALUE, NEVER A BRANCH (the engine's animation canon, and the rule a previous stagger
/// attempt broke: gating an entrance HOOK on <c>Motion.ReducedMotion</c> changes the hook COUNT between renders and
/// crashes the reconciler the moment the flag flips mid-session — a resize grip flips it). So there is no
/// <c>if (reduced)</c> here and none at any call site: <see cref="DelayMs"/> reads the flag and returns 0, and the
/// engine's own <c>ReducedSnap</c> parks the rise and the blur at their end state while still cross-fading opacity
/// (a fade aids orientation; it is not motion). Same shape as <see cref="ScaleTier.Hover"/>.</para>
///
/// <para>WHERE IT MAY BE USED. A surface qualifies only if its items mount ONCE. A virtualized list mounts items as
/// they scroll in, and an entrance replayed mid-scroll reads as flicker — so the recipe belongs on eager stacks and on
/// the engine's BOUND recycler path (<c>ItemsView.CreateBound</c>/<c>VirtualListEl.RowBind</c>), where a slot is
/// re-bound rather than re-mounted and <c>scope.Index.Peek()</c> at realize IS the item's initial position. On a
/// <c>RenderItem</c>-path virtual list (Home's measured row list, the library master list) it must NOT be used; those
/// surfaces get their initial-mount cascade from <c>Skel.Region(reveal: SkelReveal.StaggerRows)</c>, which the engine
/// fires once on the shimmer→real swap and never again on scroll.</para>
/// </summary>
public static class WaveeEntrance
{
    /// <summary>The last item index that gets its own rung. Items past it all ride this delay, so the whole entrance
    /// is bounded at <c>StaggerCap × StaggerMs</c> (360 ms) no matter how long the list is.</summary>
    public const int StaggerCap = 8;

    /// <summary>How far an entering item rises, in DIP. Deliberately small — the same 8 the engine's own skeleton
    /// reveal uses, so a staggered list and a skeleton-revealed one arrive with the same gesture.</summary>
    public const float RiseDip = Expressive.DistBase;

    /// <summary>The un-delayed spec. Tween (not spring) because a staggered cascade wants every item to take the same
    /// time regardless of when it starts; <c>Channels = Opacity</c> so the node takes NO layout FLIP from this — the
    /// recipe is an entrance, not a layout animation.</summary>
    static readonly LayoutTransition Rise = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(Expressive.Slow, Easing.SmoothOut),
        Enter: new EnterExit(Dy: RiseDip, Opacity: 0f, Active: true, Blur: Expressive.BlurSmall));

    /// <summary>Item <paramref name="index"/>'s entrance delay in ms: capped, and 0 under reduced motion.</summary>
    public static float DelayMs(int index)
        => Motion.ReducedMotion ? 0f : Math.Clamp(index, 0, StaggerCap) * WaveeMotion.StaggerMs;

    /// <summary>Assign to <c>BoxEl.Animate</c> on the item's own wrapper box (never on a node whose Opacity is already
    /// bound — a bound channel and an Enter opacity track fight over the same row).</summary>
    public static LayoutTransition Row(int index) => Rise with { DelayMs = DelayMs(index) };
}

/// <summary>One interaction scale tier: the authored hover/press targets plus the reduced-motion-safe accessors every
/// call site must use. Construct only through the <see cref="WaveeMotion"/> tiers — there is no fourth tier.</summary>
public readonly struct ScaleTier
{
    /// <summary>The authored target, BEFORE the reduced-motion read. For tests/diagnostics; call sites use
    /// <see cref="Hover"/>.</summary>
    public readonly float HoverTarget;
    /// <summary>The authored target, BEFORE the reduced-motion read. For tests/diagnostics; call sites use
    /// <see cref="Press"/>.</summary>
    public readonly float PressTarget;

    internal ScaleTier(float hoverTarget, float pressTarget)
    {
        HoverTarget = hoverTarget;
        PressTarget = pressTarget;
    }

    /// <summary>Assign to <c>BoxEl.HoverScale</c>. Collapses to 1f under reduced motion.</summary>
    public float Hover => Motion.ReducedMotion ? 1f : HoverTarget;

    /// <summary>Assign to <c>BoxEl.PressScale</c>. Collapses to 1f under reduced motion.</summary>
    public float Press => Motion.ReducedMotion ? 1f : PressTarget;

    /// <summary>Hover scale for an affordance that can be dead (a disabled transport button, an unavailable filter):
    /// a surface that cannot be clicked must not answer the pointer.</summary>
    public float HoverIf(bool enabled) => enabled ? Hover : 1f;

    /// <summary>Press scale for an affordance that can be dead. See <see cref="HoverIf"/>.</summary>
    public float PressIf(bool enabled) => enabled ? Press : 1f;
}
