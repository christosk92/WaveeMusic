// ── Theming/Policies/TierFacts.cs — the window-tier fact core publishes (themes plan 3.2, T3b) ─────────────────────────
//
// `ThemeHost.Facts.Tiers` (core models) and `ctx.Tier()` (templates). Resolved by `Shell.WindowTiers` from the layout
// viewport (after app zoom) and the content width the shell computes synchronously, through the structure's `TierPolicy`.
//
// WHEN IT CHANGES. The fact is a snapshot taken when a TIER crosses (or a rung, or a modal resize ends), not a per-pixel feed:
// the engine's `UseTier` commits a crossing on the first frame that is not resized (plan 3.3), so a resized frame still
// publishes the old tier while the views lay out at the new size, and a template that reads `ctx.Tier()` re-renders once
// per crossing, never once per resized frame. The three size members are the sizes AT that snapshot; a reader that needs
// the live width reads `Viewport.Size`, never these.

using Wavee.Theming.Contracts;

namespace Wavee.Theming;

/// <summary>The window's tier state.</summary>
/// <param name="Window">The width band of the window's layout viewport.</param>
/// <param name="Height">The height band of the window's layout viewport.</param>
/// <param name="Content">The width band of the content region (the window less the nav pane and an inline panel).</param>
/// <param name="ContentWidth">The content region's width at the snapshot.</param>
/// <param name="ViewportWidth">The window's layout width at the snapshot.</param>
/// <param name="ViewportHeight">The window's layout height at the snapshot.</param>
/// <param name="Rungs">One bit per <see cref="TierPolicy.Rungs"/> entry, set while that rung is crossed.</param>
/// <param name="Policy">The policy the snapshot was resolved against (what <see cref="Rung"/> reads its ids from).</param>
public readonly record struct TierFacts(
    WidthTier Window, HeightTier Height,
    WidthTier Content, float ContentWidth,
    float ViewportWidth, float ViewportHeight,
    ulong Rungs = 0UL, TierPolicy? Policy = null)
{
    /// <summary>No tier information: Compact and Short at size 0, no policy. What a view reads where no frame resolves tiers.</summary>
    public static TierFacts Unknown => default;

    /// <summary>Is the board mark <paramref name="id"/> of the structure's policy crossed? Throws for an id the policy does
    /// not declare (a layout record asked for a mark its structure never drew: a programming error). False where the facts
    /// carry no policy.</summary>
    public bool Rung(string id)
    {
        if (Policy is null) return false;
        int index = Policy.IndexOfRung(id);
        if (index < 0) throw new ArgumentException($"The tier policy declares no rung '{id}'.", nameof(id));
        return ((Rungs >> index) & 1UL) != 0UL;
    }
}
