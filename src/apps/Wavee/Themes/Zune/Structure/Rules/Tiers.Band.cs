// ── Themes/Zune/Structure/Rules/Tiers.Band.cs — the band's finer tier rules (themes plan 3.2, 6.3; P5 ZT1) ──────────────────────────
//
// The band's per-tier geometry is Z2b's `ZuneBandLayout` (`ZuneBandRules.Band`). What ZT1 adds on top, from Z-Part-Band and Z-Shell-Short:
//
//   TYPE      the pivots and row 2 are the style's roles (`Pivot` 28/36, `SubPivot` 14/20) at every tier but Huge 4K, where the board sets the
//             pivots at 40/48 and row 2 at 15/20 (`ZuneBandType`; NaN keeps the role's own size).
//   COLLAPSE  the band folds row 2 away on the page's first scroll down where its record collapses (Compact, Small, Standard) AND wherever
//             the window is Short (below 700 tall, Z-Shell-Short: "330 tall: band collapses to 52 at the first scroll"), whatever the width:
//             a short Huge window has no vertical room either. Huge and Huge 4K at a Regular height never collapse.
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`). Public, so `Wavee.Tests` pins them.

using FluentGpu.Animation;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The band's type in one window tier: the pivot words and row 2's words. NaN keeps the role's own size and line height.</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="PivotSize">The pivot words' size (40 at Huge 4K).</param>
/// <param name="PivotLine">The pivot words' line height (48 at Huge 4K).</param>
/// <param name="SubSize">Row 2's words' size (15 at Huge 4K).</param>
/// <param name="SubLine">Row 2's words' line height (20 at Huge 4K).</param>
public sealed record ZuneBandType(WidthTier Tier, float PivotSize, float PivotLine, float SubSize, float SubLine)
{
    /// <summary>The pivots keep the role's size.</summary>
    public bool PivotIsRole => float.IsNaN(PivotSize);

    /// <summary>Row 2 keeps the role's size.</summary>
    public bool SubIsRole => float.IsNaN(SubSize);
}

/// <summary>The band's finer tier rules (ZT1).</summary>
public static class ZuneBandTiers
{
    private static readonly ZuneBandType[] s_types =
    [
        new(WidthTier.Compact, float.NaN, float.NaN, float.NaN, float.NaN),
        new(WidthTier.Small, float.NaN, float.NaN, float.NaN, float.NaN),
        new(WidthTier.Standard, float.NaN, float.NaN, float.NaN, float.NaN),
        new(WidthTier.Huge, float.NaN, float.NaN, float.NaN, float.NaN),
        // Z-Part-Band 4k: `font-size: 40px; line-height: 48px` on the pivot run, `font-size: 15px; line-height: 20px` on row 2.
        new(WidthTier.Huge4K, PivotSize: 40f, PivotLine: 48f, SubSize: 15f, SubLine: 20f),
    ];

    /// <summary>The band's lead inset across a crossing (16 / 24 / 48 / 64): its spacer's width eases over the pivot slide's 250 ms, a reflow
    /// on the width axis, so the rows ride the re-solve (a size channel: the engine's reflow-shove rule, which snaps position FLIPs beside a
    /// reflowing page, leaves it running). Null under reduced motion at the call site.</summary>
    public static readonly LayoutTransition InsetMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.PivotSlideMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width);

    /// <summary>The band's type for window tier <paramref name="window"/>.</summary>
    public static ZuneBandType Type(WidthTier window) => s_types[Math.Clamp((int)window, 0, s_types.Length - 1)];

    /// <summary>The band's type for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneBandType TypeOf(in TierFacts facts) => Type(facts.Policy is null ? WidthTier.Standard : facts.Window);

    /// <summary>The window rests with row 2 folded away: it is Short, below the Regular height rung (Z-Shell-Short: the band stands at row 1, static; a scroll
    /// neither returns nor folds it). A view mounted alone (no policy) does not.</summary>
    public static bool RestsCollapsed(in TierFacts facts) => facts.Policy is not null && facts.Height == HeightTier.Short;

    /// <summary>Row 2 can be folded away in <paramref name="facts"/>: its record ties it to the scroll in this width tier (Compact), or the window is Short and
    /// rests with it folded (Z-Shell-Short). Pinned elsewhere. A view mounted alone (no policy) answers the record's own.</summary>
    public static bool Collapses(ZuneBandLayout band, in TierFacts facts)
        => band.Collapses || (facts.Policy is not null && facts.Height == HeightTier.Short);

    /// <summary>Row 2 is scroll-TIED in <paramref name="facts"/>: the record collapses (Compact), the `collapseBand` parameter is on and the window does not rest
    /// collapsed (a Short window folds row 2 statically instead).</summary>
    public static bool ScrollTied(ZuneBandLayout band, in TierFacts facts, bool parameter)
        => band.Collapses && parameter && !RestsCollapsed(facts);
}
