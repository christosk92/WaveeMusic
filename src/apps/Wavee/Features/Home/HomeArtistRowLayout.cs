using System;

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

    // ── E — the top-artist podium ramp (#82) ────────────────────────────────────────────────────────────────────────
    // `size = i===0 ? 76 : i<3 ? 60 : 46` — the prototype's own rank-encodes-scale ramp, at scale 1. The podium used
    // to render this verbatim regardless of measured width, which is what left ~400 DIP dead on a wide card (ten
    // artists' intrinsic footprint is 702 DIP against a ≥900 DIP card). ArtSize below multiplies it by a
    // measured-width SCALE so the strip stretches to fill instead of packing left.
    public static float BaseArtSize(int i) => i == 0 ? 76f : i < 3 ? 60f : 46f;

    /// <summary>Never SHRINK the ramp below the prototype's own sizing — only grow it to fill spare width.</summary>
    public const float MinArtScale = 1f;
    /// <summary>A strip of two or three top artists on a very wide card must not turn into portrait-sized avatars.</summary>
    public const float MaxArtScale = 1.6f;

    /// <summary>The scale that stretches the podium to fill the width a caller already fitted <paramref name="count"/>
    /// equal columns into (<c>FillRowVirtualLayout.Fit</c>'s own arithmetic, forced to exactly <paramref name="count"/>
    /// columns via <c>perPageOverride</c> — the engine-touching half of this fix, done by the caller so this class
    /// stays BCL-only). <paramref name="fittedColumnWidth"/> is that per-column result.
    /// <para>The scale is solved against the ramp's OWN AVERAGE box width — mean over <see cref="BaseArtSize"/>(0..
    /// <paramref name="count"/>) plus <paramref name="podChrome"/> (the per-pod width the art size itself does not
    /// cover) — not the rank-1 pod alone: rank-1 sits ABOVE the ramp's average (76 vs. an ~60-DIP mean for ten
    /// artists), so comparing a uniform-column fit against it alone under-scales every time. Scaling the whole ramp by
    /// <c>fittedColumnWidth / average</c> is exact: the SCALED ramp's total footprint then equals
    /// <paramref name="count"/> × <paramref name="fittedColumnWidth"/> — the same total width Fit already solved
    /// for.</para></summary>
    public static float RampScaleFor(float fittedColumnWidth, int count, float podChrome)
    {
        if (count <= 0 || fittedColumnWidth <= 0f) return MinArtScale;
        float sumDefault = 0f;
        for (int i = 0; i < count; i++) sumDefault += BaseArtSize(i) + podChrome;
        float avgDefault = sumDefault / count;
        if (avgDefault <= 0f) return MinArtScale;
        float scale = fittedColumnWidth / avgDefault;
        return Math.Clamp(scale, MinArtScale, MaxArtScale);
    }

    public static float ArtSize(int i, float scale) => BaseArtSize(i) * scale;

    /// <summary>This row's OWN module-bottom gap — deliberately NOT <c>HomeModuleLayout.Gap</c>, which is shared by
    /// every other Home row (a change there would compact every module, not just this one). 40/32 → 32/24, the same
    /// 1080-DIP tier boundary the shared helper uses.</summary>
    public static float ModuleGap(float width) => width >= 1080f ? 32f : 24f;

    // ── F — pod / Mixview-node double-click-to-navigate (#83) ───────────────────────────────────────────────────────
    // The engine's DoubleTap gesture is reserved but not yet routed (UseGesture.cs), and OnClick carries no click
    // count — so double-click is detected by hand: the same uri clicked twice inside the window counts as a double.
    public const long DoubleClickWindowMs = 400;

    /// <summary>Whether a click on <paramref name="uri"/> at <paramref name="nowTick"/> (an <c>Environment.TickCount64</c>
    /// reading) completes a double-click against the PREVIOUS click recorded at (<paramref name="lastUri"/>,
    /// <paramref name="lastTick"/>). A negative gap (a caller passing ticks out of order) never reads as a double.</summary>
    public static bool IsDoubleClick(string uri, string? lastUri, long lastTick, long nowTick)
        => uri.Length > 0 && string.Equals(uri, lastUri, StringComparison.Ordinal)
           && nowTick >= lastTick && nowTick - lastTick <= DoubleClickWindowMs;
}
