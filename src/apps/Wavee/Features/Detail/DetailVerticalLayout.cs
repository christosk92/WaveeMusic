using System;

namespace Wavee.Features.Detail;

/// <summary>Identity of one slot in the vertical playlist's measured viewport.</summary>
internal enum DetailVerticalItemRole { Hero, Chrome, ExpandableTrack, Empty }

/// <summary>Pure width→layout rules for the UNIFIED detail hero. BCL-only (no FluentGpu types) so it is source-included
/// by Wavee.Tests.
///
/// <para><b>There is ONE hero composition</b> — artwork, then eyebrow · title · accent rule · attribution · meta ·
/// actions · description, left-aligned — and this file answers only how big its parts are at a given column width. It
/// used to publish a second, independent breakpoint ladder (a hero-orientation enum with its own hysteresis
/// bands at 560–600 and 400–440) that selected between three hero VARIANTS which shared almost nothing: a full-bleed
/// immersive square with on-media white ink and hand-rolled glass circles, a 96/64-DIP thumbnail row, and a
/// side-by-side arm that auto mode could never actually reach. All three are gone. What is left is a REFLOW: below
/// <see cref="RowFlowEnterW"/> the artwork stacks above the identity column, at or above it the two sit side by side —
/// same elements, same order, same ink.</para>
///
/// <para>The persisted page-layout preference is an int (<see cref="PageAuto"/> · <see cref="PageHero"/>) that selects
/// the page SYSTEM (rail-when-wide vs always-hero); the hero's own stacked ↔ row flow is always width-driven.</para></summary>
public static class DetailVerticalLayout
{
    // WaveeSettings.DetailPageLayout values: Automatic = the responsive rail↔hero behavior; Hero = the vertical hero
    // system at EVERY width (the metadata rail is never composed for track pages).
    public const int PageAuto = 0;
    public const int PageHero = 1;

    /// <summary>The hero's outer padding, and the tighter one a phone-width column uses. Both are 4-grid.</summary>
    public const float HeroPad = 24f;
    public const float NarrowHeroPad = 16f;
    /// <summary>Below this column width the hero uses <see cref="NarrowHeroPad"/> / <see cref="NarrowHeroGap"/>.</summary>
    public const float NarrowPadW = 420f;

    /// <summary>Gap between the artwork and the identity column in row flow (and between hero blocks).</summary>
    public const float HeroGap = 24f;
    public const float NarrowHeroGap = 16f;

    /// <summary>Padding under the hero block, before the list toolbar. Small: the toolbar adds its own
    /// <see cref="ExpandedToolbarTopPad"/> on top of it.</summary>
    public const float HeroBottomPad = 8f;

    // ── the ONE breakpoint: stacked ↔ row flow ──────────────────────────────────────────────────────────────────
    /// <summary>At or above this column width the artwork sits BESIDE the identity column. Below it, above.</summary>
    public const float RowFlowEnterW = 540f;
    /// <summary>…and it stays beside until the column drops this far (24-DIP hysteresis, the same asymmetry
    /// <c>DetailLayoutBreakpoints</c> uses), so a resize grip parked on the seam cannot flip the flow every frame.</summary>
    public const float RowFlowLeaveW = RowFlowEnterW - 24f;

    // ── artwork ─────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Stacked artwork fills the content width up to this cap — past it a square cover stops being a hero and
    /// starts being a wall.</summary>
    public const float StackedArtMax = 280f;
    /// <summary>Artwork never shrinks below this: smaller and the cover reads as a list thumbnail, not the page's
    /// subject.</summary>
    public const float ArtMin = 96f;
    /// <summary>Row-flow artwork band. Continuous inside it (a fraction of the inner width), clamped at both ends.</summary>
    public const float RowArtMin = 200f, RowArtMax = 240f, RowArtFraction = 0.34f;

    // ── the text column ─────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The identity column's cap. With the Hero page layout the hero renders at ANY width, and an uncapped
    /// title/description would sprawl into 150-character lines on a wide window.</summary>
    public const float ContentWMax = 640f;
    public const float ContentWMin = 160f;

    /// <summary>Title RUNG breakpoints. Stepped, never fluid and never length-driven: every release opens at one of
    /// three sizes, all of them rungs of the engine's type ramp (TitleLarge 40/52 · Title 28/36 · Subtitle 20/28).</summary>
    public const float TitleLargeW = 640f, TitleMidW = 360f;

    public const float CompactIdentityHeight = 56f;

    /// <summary>The scroll distance the stuck band's reveal ramp occupies, ending at the collapse floor. 44 is
    /// inherited verbatim from the floating identity capsule this band replaced (it was that capsule's height, so the
    /// reveal took exactly one capsule of travel) and is kept as a TIMING constant, not a geometry one — the band has
    /// no capsule in it any more, and the artist page shares the same window through
    /// <c>ArtistHeroLayout.CompactRevealStart</c>, so changing it re-times two pages at once.</summary>
    public const float CompactRevealBand = 44f;

    public const float ExpandedToolbarTopPad = 8f;
    public const float ExpandedToolbarBottomPad = 4f;
    public const float ExpandedContentFadeDistance = 96f;
    public const float ChromeHeaderHeight = 36f;
    public const float ChromeDividerHeight = 1f;
    public const float StickyFadeBand = 24f;   // 4-grid (Spacing.XXL) — was an off-grid 22, and the hero's own rhythm is 24
    public const float FallbackW = 580f;

    /// <summary>Round a measured width down to an 8-DIP bucket before deriving geometry from it, so a sub-pixel resize
    /// frame cannot churn the InlineEdit facades' width-folding keys (which would remount them per frame).</summary>
    public static float BucketW(float availableW)
    {
        float w = availableW > 0f ? availableW : FallbackW;
        float b = MathF.Round(w / 8f) * 8f;
        return b > 0f ? b : FallbackW;
    }

    /// <summary>Nominal flow for first layout and skeleton selection: artwork beside the identity column at
    /// <see cref="RowFlowEnterW"/> and above.</summary>
    public static bool RowFlow(float colW) => (colW > 0f ? colW : FallbackW) >= RowFlowEnterW;

    /// <summary>Resize-hysteretic flow. <paramref name="initialized"/> false ⇒ nothing has been measured yet, so
    /// <paramref name="current"/> is a construction default rather than a flow the visitor has seen: take the nominal
    /// answer outright (the same first-measure rule <c>DetailLayoutBreakpoints.ModeFor</c> follows).</summary>
    public static bool RowFlow(float colW, bool current, bool initialized)
    {
        if (!initialized) return RowFlow(colW);
        float w = colW > 0f ? colW : FallbackW;
        return current ? w >= RowFlowLeaveW : w >= RowFlowEnterW;
    }

    public static float HeroPadFor(float colW)
        => (colW > 0f ? colW : FallbackW) < NarrowPadW ? NarrowHeroPad : HeroPad;

    public static float HeroGapFor(float colW)
        => (colW > 0f ? colW : FallbackW) < NarrowPadW ? NarrowHeroGap : HeroGap;

    /// <summary>The artwork edge. Continuous in both flows (a clamped fraction of the space the column actually has),
    /// rounded to a whole DIP so a resize cannot churn the decode bucket or the cover component's key.</summary>
    public static float ArtworkFor(float colW, bool rowFlow)
    {
        float w = colW > 0f ? colW : FallbackW;
        float pad = HeroPadFor(w);
        if (rowFlow)
        {
            float inner = MathF.Max(1f, w - 2f * pad - HeroGapFor(w));
            return MathF.Round(Math.Clamp(inner * RowArtFraction, RowArtMin, RowArtMax));
        }
        return MathF.Round(Math.Clamp(w - 2f * pad, ArtMin, StackedArtMax));
    }

    /// <summary>The identity column's measure — what the title, attribution and description wrap to.</summary>
    public static float ContentWidthFor(float colW, bool rowFlow)
    {
        float w = colW > 0f ? colW : FallbackW;
        float pad = HeroPadFor(w);
        float avail = rowFlow
            ? w - 2f * pad - HeroGapFor(w) - ArtworkFor(w, rowFlow)
            : w - 2f * pad;
        return MathF.Min(ContentWMax, MathF.Max(ContentWMin, avail));
    }

    /// <summary>The title's RUNG for a column width — one of exactly three, and never a function of the title's own
    /// length. A long name is handled by the wrap cap + ellipsis on the run, which is the job the old length-driven
    /// step-down (42/34/28/24, none of them a rung of anything) was doing badly.</summary>
    public static float TitleSizeFor(float colW)
    {
        float w = colW > 0f ? colW : FallbackW;
        return w >= TitleLargeW ? 40f : w >= TitleMidW ? 28f : 20f;
    }

    /// <summary>The ramp line height PAIRED with <see cref="TitleSizeFor"/> — a rung is a size AND a line height.</summary>
    public static float TitleLineHeightFor(float titleSize)
        => titleSize >= 40f ? 52f : titleSize >= 28f ? 36f : 28f;

    /// <summary>Description line cap: a touch shorter beside the artwork (the measure is narrower there anyway), a
    /// touch taller when the copy owns the full column.</summary>
    public static int DescriptionMaxLines(bool rowFlow) => rowFlow ? 3 : 4;

    // ── the hero BAND, as a height ───────────────────────────────────────────────────────────────────────────────
    // ONE arithmetic for "how tall is this hero at this width", with TWO consumers that must never disagree:
    //   · the LOADING skeleton reserves exactly this band, so the hero fades into a slot that was already its size
    //     instead of arriving and shoving the toolbar, the column header and every row down the page (D49); and
    //   · the loaded hero's own PRE-MEASURE fallback — the height its collapse binds assume on the frame before
    //     OnBoundsChanged publishes the real one. That used to be two hand-picked constants (420 stacked / 320 row
    //     flow) which were not a function of anything: at 400 DIP the artwork alone is 280 and the padding another
    //     32, so 420 left ~100 DIP for a title, a rule, an attribution, a meta line, an action row and a toolbar.
    // The blocks below are the natural heights of the runs DetailVerticalHero composes, in its order. They are
    // nominal — the skeleton has no text to shape, so its natural height IS this sum by construction, and the loaded
    // hero settles onto its measured height on the first layout pass either way.
    public const float EyebrowRowHeight = 16f;        // WaveeType.Eyebrow, one line
    public const float AccentRuleRowHeight = 4f;      // Surfaces.AccentRule (2) + its 2-DIP top margin
    public const float AttributionRowHeight = 16f;    // owner / billed artists, 12px run on one line
    public const float MetaRowHeight = 16f;           // "50 songs · 3 hr 12 min", one line
    // The daylist flip-countdown digit row (FlipCountdown.HeroRowHeight, restated — this file is engine-free and
    // test-included, so it cannot reference the component).
    public const float PulseRowHeight = 28f;
    public const float ActionRowHeight = 40f;         // WaveeCta.Play (36) + the row's Spacing.XS top margin
    public const float DescriptionLineHeight = 18f;   // the 13px expandable blurb
    public const float IdentityGap = 4f;              // Spacing.XS — the identity column's inter-block gap
    public const float ToolbarRowHeight = 32f;        // WaveeSize.ControlH — the list command bar's pill row
    /// <summary>The hero title's wrap cap (DetailVerticalHero's <c>MaxLines</c>) — the band reserves the full two
    /// lines, because a one-line title that becomes two is the shove this band exists to prevent.</summary>
    public const int TitleMaxLines = 2;

    /// <summary>The identity column's height: the blocks the hero actually composes for THIS model, at this width,
    /// separated by <see cref="IdentityGap"/>. Title, accent rule and action row are unconditional; the rest are
    /// present exactly when the hero would emit them.</summary>
    public static float IdentityHeightFor(float colW, bool rowFlow,
        bool eyebrow, bool attribution, bool meta, bool description, bool pulse = false)
    {
        float w = colW > 0f ? colW : FallbackW;
        int blocks = 0;
        float h = 0f;
        if (eyebrow) { h += EyebrowRowHeight; blocks++; }
        h += TitleLineHeightFor(TitleSizeFor(w)) * TitleMaxLines; blocks++;
        h += AccentRuleRowHeight; blocks++;
        if (attribution) { h += AttributionRowHeight; blocks++; }
        if (meta) { h += MetaRowHeight; blocks++; }
        if (pulse) { h += PulseRowHeight; blocks++; }   // the daylist countdown sits between meta and the action row
        h += ActionRowHeight; blocks++;
        if (description) { h += DescriptionMaxLines(rowFlow) * DescriptionLineHeight; blocks++; }
        return h + (blocks > 1 ? (blocks - 1) * IdentityGap : 0f);
    }

    /// <summary>The whole expanded hero band: the padded artwork/identity composition (stacked or side-by-side, the
    /// same reflow <see cref="RowFlow(float)"/> selects) plus the list toolbar row that rides under it.</summary>
    public static float HeroBandHeight(float colW, bool rowFlow,
        bool eyebrow, bool attribution, bool meta, bool description, bool pulse = false)
    {
        float w = colW > 0f ? colW : FallbackW;
        float pad = HeroPadFor(w);
        float art = ArtworkFor(w, rowFlow);
        float identity = IdentityHeightFor(w, rowFlow, eyebrow, attribution, meta, description, pulse);
        // Row flow bottom-aligns the two columns, so the band is whichever is taller; stacked adds them over the gap.
        float hero = rowFlow ? MathF.Max(art, identity) : art + HeroGapFor(w) + identity;
        return pad + hero + HeroBottomPad
             + ExpandedToolbarTopPad + ToolbarRowHeight + ExpandedToolbarBottomPad;
    }

    /// <summary>Scroll distance over which the expanded hero becomes the 56-DIP context band.</summary>
    public static float CollapseDistance(float expandedHeight)
        => MathF.Max(1f, expandedHeight - CompactIdentityHeight);

    /// <summary>Actual pinned list-chrome extent. The optional Liked filter rail is part of the same sticky plate, so
    /// paint and input must both account for its 48-DIP rail+gap instead of assuming the base header.</summary>
    public static float ChromeExtent(float contentFilterExtent = 0f)
        => ChromeHeaderHeight + ChromeDividerHeight + MathF.Max(0f, contentFilterExtent);

    public static float StickyClipInset(float contentFilterExtent = 0f)
        => CompactIdentityHeight + ChromeExtent(contentFilterExtent);

    /// <summary>The first two slots are persistent chrome; every live suffix slot is an expandable track container.
    /// Keeping this decision pure prevents the vertical playlist from accidentally bypassing the drawer host.</summary>
    internal static DetailVerticalItemRole ItemRole(int itemIndex, int visibleTracks)
        => itemIndex switch
        {
            0 => DetailVerticalItemRole.Hero,
            1 => DetailVerticalItemRole.Chrome,
            _ when itemIndex >= 2 && itemIndex - 2 < Math.Max(0, visibleTracks)
                => DetailVerticalItemRole.ExpandableTrack,
            _ => DetailVerticalItemRole.Empty,
        };

    /// <summary>The expanded hero stays readable until its final 96 DIP, then yields continuously to the context band.</summary>
    public static float ExpandedFadeStart(float collapseDistance)
        => MathF.Max(0f, collapseDistance - ExpandedContentFadeDistance);

    /// <summary>The stuck band's quiet crossfade/4-DIP slide occupies only the last <see cref="CompactRevealBand"/>
    /// DIP of the collapse.</summary>
    public static float CompactRevealStart(float collapseDistance)
        => MathF.Max(0f, collapseDistance - CompactRevealBand);

    /// <summary>Decode bucket for the hero cover. The source mapper retains the largest CDN rendition; this controls
    /// the decoded texture size without churning a cache key on every resize pixel.</summary>
    public static int ArtworkDecodePx(float artworkSize)
        => artworkSize <= 128f ? 256 : artworkSize <= 288f ? 512 : 1024;

    /// <summary>The hero's decode bucket gated on whether a REAL width has landed yet. Unmeasured always asks for 256 —
    /// the same bucket the Home shelf card, the grid tiles and <c>DetailRail.HeroCoverDecodePx</c> use — instead of
    /// <see cref="ArtworkDecodePx(float)"/> off a pre-measure artwork size that is itself only a guess (derived from
    /// <see cref="FallbackW"/> or a page-width estimate, never the page's real geometry): first frame is then a
    /// synchronous cache hit against the SAME texture the preview/skeleton/grid already resolved, instead of a
    /// probably-wrong bucket that forces a fresh decode the moment the real width lands anyway. The measured case is
    /// unchanged.</summary>
    public static int ArtworkDecodePx(float artworkSize, bool widthMeasured)
        => widthMeasured ? ArtworkDecodePx(artworkSize) : 256;

    /// <summary>The hero band the page tone measures against: the hero's own measured extent, floored so a
    /// not-yet-measured hero still yields a plausible band rather than a hairline. Sole consumer is hero-only mode's
    /// fade start (<c>CoverPageTonePlane.HeroOnlyVeil</c>) — the blurred artwork band that used to share it is deleted,
    /// and <c>BackdropFadeFraction</c> (its mask feather, 0.6) went with it.</summary>
    public static float BackdropBandFor(float heroHeight)
        => MathF.Max(CompactIdentityHeight * 2f, heroHeight);
}
