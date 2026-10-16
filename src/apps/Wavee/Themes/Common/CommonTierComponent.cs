// ── Themes/Common/CommonTierComponent.cs — the tier a Common component reads (themes plan 3.2, TC10) ─────────────────────────────
//
// A template reads `ctx.Tier()`; a COMPONENT (a stateful card the page embeds) has no view context, so it reads the same fact through the same context: the facts signal
// `ThemeRoot` provides. The read subscribes the component to a tier CROSSING, never to a resized frame (the engine commits crossings on the first frame that is not resized).

using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming;

namespace Wavee.Themes.Common;

/// <summary>A component that reads the window tiers of the theme it is drawn in.</summary>
internal abstract class CommonTierComponent : Component
{
    /// <summary>The tiers on screen now (<see cref="TierFacts.Unknown"/> where no root provides them: a bare unit test of a card).</summary>
    protected TierFacts ReadTier()
    {
        var facts = UseContext(ThemeFactsContext.Signal);
        return facts is null ? TierFacts.Unknown : facts.Value.Tiers;
    }
}
