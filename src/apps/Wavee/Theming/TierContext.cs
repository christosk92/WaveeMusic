// ── Theming/TierContext.cs — `ctx.Tier()`: how a template reads the window tiers (themes plan 3.2, T3b) ────────────────
//
// A view never compares a width with a number (WVTH013, T3c). It reads the tier it is in and its structure's layout record
// for that tier:
//
//     var tier = ctx.Tier();
//     var layout = tier.Content >= WidthTier.Huge ? HugeLayout : StandardLayout;   // a record the structure's Rules\Tiers*.cs owns
//
// The read subscribes the template to the facts signal `ThemeRoot` provides, which changes only when a TIER crosses (and a
// rung, and the modal size-move edge), never once per resized frame: the engine commits a crossing on the first frame that
// is not resized (plan 3.3), so the template re-renders once, on the frame the crossing animates.

using FluentGpu.Theming;

namespace Wavee.Theming;

/// <summary>The template-side reader of <see cref="TierFacts"/>.</summary>
public static class TierContext
{
    /// <summary>The window tiers on screen now. <see cref="TierFacts.Unknown"/> where no root provides the facts (a bare unit
    /// test of a view); a harness that mounts a view alone provides facts resolved from its window size.</summary>
    public static TierFacts Tier(this in ViewContext ctx)
    {
        var facts = ctx.Hooks.UseContext(ThemeFactsContext.Signal);
        return facts is null ? TierFacts.Unknown : facts.Value.Tiers;
    }
}
