namespace Wavee.Features.Home;

/// <summary>Home artist-row Mixview width tier — Wide (side-by-side, full hex ring) vs Spine (stacked, vertical
/// artist list). One boundary, same resize-hysteresis shape as the detail page's breakpoint ladder. Pure static —
/// source-included by Wavee.Tests.</summary>
public static class HomeArtistRowLayout
{
    public const float TierHysteresisDip = 24f;

    public const int TierWide = 0;
    public const int TierSpine = 1;

    /// <summary>The tier a width would take with no memory of the previous one. A zero/absent width reads as
    /// <see cref="TierWide"/> so the nominal function itself never invents a narrow arm out of "not measured yet" —
    /// <see cref="TierFor"/> owns that case.</summary>
    public static int NominalTierFor(float w) =>
        w <= 0f ? TierWide : w >= 900f ? TierWide : TierSpine;

    /// <summary>Safe pre-measure seed from the window viewport.</summary>
    public static int InitialTierForViewport(float viewportWidth) => NominalTierFor(viewportWidth);

    /// <summary>Narrow (drop to Spine) immediately; re-admit Wide only once the width clears the threshold by
    /// <see cref="TierHysteresisDip"/> — the safe asymmetry, since the cost of the wrong guess in the widening
    /// direction is a side-by-side row the module cannot hold.
    ///
    /// <paramref name="initialized"/> false ⇒ the caller has not measured yet, so <paramref name="prev"/> is a
    /// construction default / a pre-measure viewport seed rather than a tier the user has actually seen: take the
    /// nominal tier outright and let hysteresis start from there.</summary>
    public static int TierFor(float w, int prev, bool initialized = true)
    {
        if (w <= 0f) return prev;
        if (!initialized) return NominalTierFor(w);
        int nominal = NominalTierFor(w);
        if (nominal >= prev) return nominal;
        int dipped = NominalTierFor(w - TierHysteresisDip);
        return dipped < prev ? dipped : prev;
    }
}
