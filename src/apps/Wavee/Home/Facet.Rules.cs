// ── Home/Facet.Rules.cs — the pure decision behind HomeScreen's facet dim host ──────────────────────────────────
//
// Role: CORE
// Wave: 2D (Home redesign remediation, docs/plans/wavee/home-redesign-remediation.md §3.5)
// Spec: docs/plans/wavee/home-redesign/06-facet-design.md §2.6 (the switch motion table: dim .6 over 167ms while
//   Loading; restore to 1.0 over 167ms otherwise — Idle/Refreshing/a Failed revert all share the "normal" 167ms
//   restore).
//
// F18: the dim-and-block wrapper this plan once had in Home/Facet.UI.cs (`Facet.DimWrap`) is deleted — HomeScreen
// now owns ONE dim host for the whole zone list, bound via `Prop.Of(() => FacetDimPlan.Of(_switch.Value.Phase).Opacity)`
// (compositor-only, no re-render) — this file is that host's only caller.
//
// The remediation also removes `FacetPhase.FadingOut` (docs/plans/wavee/home-redesign-remediation.md §3.9): the
// pre-remediation instant-swap tail no longer exists as a phase of its own — a facet switch's content swap is now
// the reconciler's own keyed Exit/Enter on HomeScreen's facet-content root, not a phase this plan tracks opacity
// for. `FacetDimPlan` therefore only ever sees Loading (dim) or a rest phase (Idle/Refreshing/Failed — full
// opacity); there is no third "fading to zero" case to plan for here any more.
//
// Engine-free and pure (no signals, no Element, no I/O) so it can be unit-tested without a host — the "no
// source-text tests" rule's pattern (ButtonRulesTests/AppUpdateToasts/etc.): extract the DECISION, test it directly.

using Wavee;

namespace Wavee.HomeUi;

/// <summary>The old content's target opacity and transition duration during a facet switch, keyed purely on the
/// switch machine's <see cref="FacetPhase"/> (06 §2.6). HomeScreen's dim host is the only caller — it applies this
/// plan to a bound <c>Opacity</c> prop, which this file never touches.</summary>
public static class FacetDimPlan
{
    /// <summary>0.6 opacity over 167ms while Loading (dim), else 1 opacity over 167ms (Idle/Refreshing at rest, or
    /// a Failed revert restoring to full — every non-Loading phase reads the same "at rest" plan).</summary>
    public static (float Opacity, float DurationMs) Of(FacetPhase phase) => phase switch
    {
        FacetPhase.Loading => (0.6f, Design.Motion.Fast),
        _ => (1f, Design.Motion.Fast),
    };
}

/// <summary>Which form the facet row takes at its measured width (06 §2.1): the 28-px page-title words while the row
/// holds four of them and the Following toggle, the 20-px compact words below that. Never wraps, never scrolls.</summary>
public static class FacetForm
{
    /// <summary>The narrowest row that still fits the four English title words plus the Following toggle: on the
    /// owner's 1717×1150 @150 % window the row measures 632 and holds both with ≈ 40 to spare, so the edge sits at 600.
    /// (The prototype's "~720" is a 1440-board figure with wider spacing.)</summary>
    public const float TitleMinWidth = 600f;

    /// <summary>True → <c>Design.FacetTitleStyle</c>; false → <c>Design.FacetCompactStyle</c>.</summary>
    public static bool IsTitle(float rowWidth) => rowWidth >= TitleMinWidth;
}
