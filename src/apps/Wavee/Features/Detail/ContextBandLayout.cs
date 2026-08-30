using System;

namespace Wavee.Features.Detail;

/// <summary>
/// The PURE geometry behind Wavee's ONE sticky page header — the <b>text-chrome context bar</b> (the Zune pivot
/// idiom): a 56-DIP band carrying a title on the left, the page's own sections as text links in the middle, and text
/// actions on the right. No thumbnail, no plates, no shadow — the chrome IS typography.
///
/// <para><b>The priority order is fixed.</b> The title never drops (it answers "what page is this" once the hero has
/// scrolled away) and the actions never drop (they are the page's primary verbs). The pivot is the one elastic lane:
/// every section stays mounted in order and horizontal overflow scrolls behind an alpha edge fade. Nothing disappears
/// at a responsive breakpoint, so the active section can always reveal itself and never names a hidden tab.</para>
///
/// <para><b>The scroll spy</b> (<see cref="ActiveSection"/>) is the other half: given where each section's top
/// currently sits relative to the viewport, which one is "here". It is a pure scan so the live version — which reads
/// committed layout through <c>SceneStore.AbsoluteRect</c>, exactly like <c>ShyMonthPill</c> — has no arithmetic of
/// its own to get wrong.</para>
///
/// <para>Engine-free by construction (System only) so <c>ContextBandLayoutTests</c> drives the real scroll-spy
/// arithmetic in the <c>MergedChromeLayout</c> / <c>DetailVerticalLayout</c> tradition.</para>
/// </summary>
public static class ContextBandLayout
{
    /// <summary>The identity row's height. Equal to <c>DetailVerticalLayout.CompactIdentityHeight</c> by construction:
    /// the detail band and the artist band are the SAME band, and the detail collapse ladder already targets 56.</summary>
    public const float Height = 56f;

    /// <summary>The band's one hairline. ONE, under the whole stuck surface — including the tracklist column row when
    /// it has joined the band — so a scrolled page never shows two sticky strata with raw rows between them.</summary>
    public const float HairlineHeight = 1f;

    /// <summary>THE OFFSET MODEL'S OTHER HALF. The band paints no fill (see <c>ContextBand</c>), so the page owes it a
    /// CLIP: nothing may render into these DIP, and what shows there is the page's own ground.
    ///
    /// <para>This is the ARTIST arm's inset, where the band is the identity row and nothing else. The track-detail
    /// pages pin a second stratum under it — the tracklist's column row plus the shared hairline — so they clip at
    /// <c>DetailVerticalLayout.StickyClipInset</c> instead, which sums exactly this height plus that row. Two numbers,
    /// one rule.</para></summary>
    public const float ClipInset = Height;

    /// <summary>The feather at the clip edge, so content dissolves into the band instead of being guillotined by it.
    /// The same band the detail pages' item clip already uses, so every surface cuts identically.</summary>
    public const float ClipFadeBand = DetailVerticalLayout.StickyFadeBand;

    /// <summary>Gap between the three clusters (title | pivot | actions).</summary>
    public const float ClusterGap = 24f;      // Spacing.XXL

    /// <summary>Gap between two pivot items.</summary>
    public const float PivotGap = 16f;        // Spacing.L

    /// <summary>Gap between two text actions in the right cluster.</summary>
    public const float ActionGap = 16f;       // Spacing.L

    /// <summary>Horizontal padding INSIDE one pivot item's hit box — also the inset the 2-DIP active underline is
    /// drawn within, so the mark reads as belonging to the word rather than to the slot.</summary>
    public const float PivotPadX = 8f;

    /// <summary>Horizontal padding inside one text action's hit box. Larger than <see cref="PivotPadX"/> because an
    /// action has no underline to widen its perceived target.</summary>
    public const float ActionPadX = 10f;

    /// <summary>The active-section underline: 2 DIP, the tab-strip rung (this is genuine AccentSelection — "you are
    /// here" — not decoration), sitting <see cref="UnderlineGap"/> under the link's line box.</summary>
    public const float UnderlineHeight = 2f;
    public const float UnderlineGap = 4f;     // Spacing.XS

    /// <summary>The widest slot the title claims. Past it the surplus goes to the pivot, which is the affordance that
    /// actually does something with more room.</summary>
    public const float TitleCap = 280f;

    /// <summary>Average advance of one character at the band's rung (14 px / weight 600, Segoe UI Variable Text).
    /// Deliberately on the generous side of the real average (~6.9 for mixed-case Latin), so command clusters reserve
    /// enough room instead of clipping a localized label. Non-Latin scripts run wider per glyph but shorter per word,
    /// and the two errors cancel in the band's favour.</summary>
    public const float AvgCharW = 7.6f;

    /// <summary>The estimated laid-out width of one band label, hit box included.</summary>
    public static float EstimateLabelWidth(int labelLength, float padX)
        => MathF.Max(0f, labelLength) * AvgCharW + 2f * padX;

    /// <inheritdoc cref="EstimateLabelWidth(int,float)"/>
    public static float EstimateLabelWidth(string? label, float padX)
        => EstimateLabelWidth(label?.Length ?? 0, padX);

    /// <summary>A text-action cluster's total claim: every action at its estimated width plus the gaps between.</summary>
    public static float ActionsWidth(ReadOnlySpan<float> actionWidths)
    {
        if (actionWidths.Length == 0) return 0f;
        float w = 0f;
        for (int i = 0; i < actionWidths.Length; i++) w += MathF.Max(0f, actionWidths[i]);
        return w + (actionWidths.Length - 1) * ActionGap;
    }

    // ── the scroll spy ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>How far PAST the band's lower edge a section top may still be and count as "arrived". Without it the
    /// active mark flickers between two neighbours while a section top sits exactly on the boundary during a slow
    /// scroll — one sub-pixel layout jitter is enough. 8 DIP is below the smallest deliberate scroll step and above
    /// any layout noise.</summary>
    public const float SpyProbe = 8f;

    /// <summary>The incoming section becomes current at the upper quarter of the viewport that remains below the
    /// sticky band. This keeps the mark aligned with what dominates the page instead of waiting until the heading is
    /// already disappearing behind the chrome.</summary>
    public const float SpyViewportFraction = 0.25f;

    /// <summary>How close to the real maximum offset counts as the end of the page. Reuses the spy's 8-DIP tolerance:
    /// large enough to survive fractional layout/scroll settling, too small to turn ordinary near-bottom reading into
    /// an early section change.</summary>
    public const float EndProbe = SpyProbe;

    /// <summary>Whether a genuinely scrollable viewport has reached its real lower limit. A non-scrollable page and
    /// the untouched top of a tiny-overflow page are deliberately not "at end".</summary>
    public static bool IsAtScrollEnd(float offsetY, float viewportHeight, float contentHeight)
    {
        if (!float.IsFinite(offsetY) || !float.IsFinite(viewportHeight) || !float.IsFinite(contentHeight)
            || viewportHeight <= 0f || contentHeight <= viewportHeight) return false;
        float maxOffset = contentHeight - viewportHeight;
        return offsetY > 0.5f && offsetY >= maxOffset - EndProbe;
    }

    /// <summary>The viewport-relative Y line an incoming section crosses to become active.</summary>
    public static float SpyLine(float bandBottom, float viewportHeight)
    {
        float band = MathF.Max(0f, bandBottom);
        float usable = MathF.Max(0f, viewportHeight - band);
        return band + usable * SpyViewportFraction + SpyProbe;
    }

    /// <summary>Which pivot item is "here". <paramref name="viewportRelativeTops"/> is each section's top edge
    /// measured DOWN from the viewport's own top (so negative = already scrolled past), in pivot order.
    /// <paramref name="bandBottom"/> is the lower edge of the sticky chrome and <paramref name="viewportHeight"/> is
    /// the live viewport height. A section is "arrived" at the upper quarter of the usable region below the band,
    /// plus the small boundary tolerance in <see cref="SpyProbe"/>. At the real scroll limit, the last contiguous
    /// measured section wins because a short final section may have no trailing content with which to reach that line.
    ///
    /// <para>Returns 0 while the page is at the top (the first section is the answer before anything has crossed:
    /// a pivot with no mark reads as broken, and the visitor IS looking at the first section). A
    /// <see cref="float.NaN"/> top means "not realized yet" and STOPS the scan — an unmeasured section cannot be
    /// behind the band, and treating NaN as arrived would jump the mark to the end of a page whose lower sections have
    /// not laid out.</para>
    ///
    /// <para>Returns −1 — "NO ANSWER, hold whatever the caller already had" — for an empty pivot AND for the case
    /// where not even the FIRST section has a measurement. The second arm is the honest reading of a scan that learned
    /// nothing, and it is load-bearing: returning 0 there publishes "you are in section one" as a positive fact
    /// derived from zero evidence, which is exactly how a spy whose registry had been emptied (D40) looked like a
    /// working spy stuck on the first item rather than a dead one.</para></summary>
    public static int ActiveSection(ReadOnlySpan<float> viewportRelativeTops, float bandBottom, float viewportHeight,
                                    bool atScrollEnd)
    {
        if (viewportRelativeTops.Length == 0 || float.IsNaN(viewportRelativeTops[0])) return -1;
        if (atScrollEnd)
        {
            int lastMeasured = 0;
            for (int i = 1; i < viewportRelativeTops.Length; i++)
            {
                if (float.IsNaN(viewportRelativeTops[i])) break;
                lastMeasured = i;
            }
            return lastMeasured;
        }
        float line = SpyLine(bandBottom, viewportHeight);
        int active = 0;
        for (int i = 0; i < viewportRelativeTops.Length; i++)
        {
            float top = viewportRelativeTops[i];
            if (float.IsNaN(top)) break;
            if (top <= line) active = i; else break;
        }
        return active;
    }

    /// <summary>The content-space offset that parks section <paramref name="index"/>'s top just under the band. The
    /// live path hands the node to <c>ScrollIntoView.BringInto</c> with this as its margin; this overload exists for
    /// the model-driven case (and for the tests) where the destination is arithmetic rather than a realized node.</summary>
    public static float ScrollTargetFor(float currentOffset, float viewportRelativeTop, float bandBottom)
        => MathF.Max(0f, currentOffset + viewportRelativeTop - bandBottom);
}
