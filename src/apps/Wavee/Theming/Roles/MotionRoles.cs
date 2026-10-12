// ── Theming/Roles/MotionRoles.cs — the interaction personality of a style (themes plan 5.4) ─────────────────────────────
//
// What was `Design.Motion`'s scale tiers and stagger rungs. Hover/press motion is Wavee's identity (a media app feels alive
// under the pointer where WinUI is deliberately still), and HOW alive is the style's: the three scale tiers and the two
// stagger rungs are roles. The Fluent duration ladder (83 / 167 / 250 / 333 ms, WinUI's Control*AnimationDuration) stays in
// `Design.Motion`: it is a platform constant the engine's own `MotionTok` mirrors, and a handful of `static readonly`
// transitions bake it in, so retiming it is a decision for the style that needs it.
//
// WHY THE TIERS READ ReducedMotion. The hover/press SCALE path is the one animated channel in the engine with no
// reduced-motion policy (the recorder composites the scale unconditionally), so the tiers return 1 under reduced motion,
// which makes the recorder skip the transform. Reduced motion is a VALUE read during render, never a hook-order-breaking branch.

using FgMotion = FluentGpu.Dsl.Motion;

namespace Wavee.Theming.Roles;

/// <summary>One interaction scale tier: the authored hover/press targets plus the reduced-motion-safe accessors every call site
/// must use. A style has exactly three (subtle, standard, emphatic): there is no fourth.</summary>
/// <param name="HoverTarget">The authored hover scale, BEFORE the reduced-motion read. For tests and diagnostics; call sites use <see cref="Hover"/>.</param>
/// <param name="PressTarget">The authored press scale, BEFORE the reduced-motion read.</param>
public readonly record struct ScaleTier(float HoverTarget, float PressTarget)
{
    /// <summary>Assign to <c>BoxEl.HoverScale</c>. Collapses to 1 under reduced motion.</summary>
    public float Hover => FgMotion.ReducedMotion ? 1f : HoverTarget;

    /// <summary>Assign to <c>BoxEl.PressScale</c>. Collapses to 1 under reduced motion.</summary>
    public float Press => FgMotion.ReducedMotion ? 1f : PressTarget;

    /// <summary>Hover scale for an affordance that can be DEAD (a disabled transport button, an unavailable filter): a surface
    /// that cannot be clicked must not answer the pointer.</summary>
    public float HoverIf(bool enabled) => enabled ? Hover : 1f;

    /// <summary>The press counterpart of <see cref="HoverIf"/>.</summary>
    public float PressIf(bool enabled) => enabled ? Press : 1f;
}

/// <summary>The motion roles of a style. Every style defines all of them (themes plan S2). Equality is by value.</summary>
/// <param name="ScaleSubtle">Chips, small toggles, settings rows, inline pickers: a surface the pointer crosses often, so
/// barely-there. NOT for a near-full-width track or list row, whose press acknowledgement is its pressed fill alone.</param>
/// <param name="ScaleStandard">Buttons, CTAs, pills, secondary circles: a deliberate, discrete target.</param>
/// <param name="ScaleEmphatic">Media FABs, transport and primary play, on-artwork circles, the corner "...": a round affordance that
/// floats over media and is the page's loudest interactive object.</param>
/// <param name="StaggerMs">List and shelf entrance stagger per item. Reached through <c>Design.Entrance</c>, never multiplied by hand
/// at a call site (that is what produced the uncapped, reduced-motion-blind entrances the recipe replaced).</param>
/// <param name="MastheadStaggerMs">A drill-in surface's masthead stagger: the offset between the display line and the metadata line
/// under it. A separate rung from <paramref name="StaggerMs"/> on purpose: that one is the per-ITEM offset of an unbounded list
/// and is capped; this one offsets exactly two authored lines, so the engine's uncapped <c>index x ms</c> is safe.</param>
public sealed record MotionRoles(
    ScaleTier ScaleSubtle, ScaleTier ScaleStandard, ScaleTier ScaleEmphatic,
    float StaggerMs, float MastheadStaggerMs);
