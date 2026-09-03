using System;

namespace Wavee.Features.Detail;

/// <summary>Identity of one slot in the vertical playlist's measured viewport.</summary>
internal enum DetailVerticalItemRole { Hero, Chrome, ExpandableTrack, Footer, Empty }

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
    /// <summary>At or above this column width the artwork sits BESIDE the identity column. Below it, above. Lowered
    /// from 540: the row arm is the same composition with a smaller cover, so a continuous function with a lower floor
    /// (<see cref="RowArtMin"/>) expresses the compact case exactly, instead of stacking it into a much taller band.</summary>
    public const float RowFlowEnterW = 424f;
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
    /// <summary>Row-flow artwork band. Continuous inside it (a fraction of the inner width), clamped at both ends.
    /// <see cref="RowArtMin"/> binds exactly at <see cref="RowFlowLeaveW"/> (0.44 × (400 − 72) = 144), so the curve is
    /// continuous across the flip; 0.44 costs the wide end nothing (206 at the old 540 vs the old 0.34's 200) and moves
    /// the cap to 617 instead of 820 — no width gets a smaller cover than before.</summary>
    public const float RowArtMin = 144f, RowArtMax = 240f, RowArtFraction = 0.44f;

    // ── the text column ─────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The identity column's cap for attribution/meta/description. With the Hero page layout the hero renders
    /// at ANY width, and an uncapped body run would sprawl into 150-character lines on a wide window. The title has its
    /// own, wider cap — <see cref="TitleWMax"/> — because a headline running wider than its body copy is ordinary
    /// editorial typography, and applying THIS cap to a display title is what left 1200-DIP pages with ~250 DIP of
    /// empty identity column (issue #79).</summary>
    public const float ContentWMax = 640f;
    public const float ContentWMin = 160f;

    /// <summary>The title's own measure cap — wider than <see cref="ContentWMax"/>, so a headline can run wider than
    /// the body copy beneath it.</summary>
    public const float TitleWMax = 1000f;

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

    /// <summary>Outer hero padding. Flow-aware: the row arm always takes the full 24/24 pair, because it only exists
    /// where there is width to give away — <see cref="NarrowPadW"/>'s tight pad exists for a STACKED column with no
    /// width to spare. Without this split, <see cref="NarrowPadW"/> = 420 would subtract 40 DIP of inner width at one
    /// column width crossing into the row arm's low floor, producing a cover that SHRINKS as the window widens
    /// (art(419) = 156 → art(420) = 146) — exactly what <c>ArtworkFor_IsMonotoneInWidth</c> exists to catch.</summary>
    public static float HeroPadFor(float colW, bool rowFlow)
        => rowFlow || (colW > 0f ? colW : FallbackW) >= NarrowPadW ? HeroPad : NarrowHeroPad;

    /// <summary>Gap between the artwork and the identity column. Flow-aware for the same reason <see cref="HeroPadFor"/>
    /// is.</summary>
    public static float HeroGapFor(float colW, bool rowFlow)
        => rowFlow || (colW > 0f ? colW : FallbackW) >= NarrowPadW ? HeroGap : NarrowHeroGap;

    /// <summary>The artwork edge. Continuous in both flows (a clamped fraction of the space the column actually has),
    /// rounded to a whole DIP so a resize cannot churn the decode bucket or the cover component's key.</summary>
    public static float ArtworkFor(float colW, bool rowFlow)
    {
        float w = colW > 0f ? colW : FallbackW;
        float pad = HeroPadFor(w, rowFlow);
        if (rowFlow)
        {
            float inner = MathF.Max(1f, w - 2f * pad - HeroGapFor(w, rowFlow));
            return MathF.Round(Math.Clamp(inner * RowArtFraction, RowArtMin, RowArtMax));
        }
        return MathF.Round(Math.Clamp(w - 2f * pad, ArtMin, StackedArtMax));
    }

    /// <summary>The space left for copy beside (row flow) or under (stacked) the artwork, before either the body cap
    /// (<see cref="ContentWMax"/>) or the title cap (<see cref="TitleWMax"/>) is applied — the one measure
    /// <see cref="ContentWidthFor"/> and <see cref="TitleWidthFor"/> share, so the two columns cannot drift apart.</summary>
    static float CopyAvailFor(float colW, bool rowFlow)
    {
        float w = colW > 0f ? colW : FallbackW;
        float pad = HeroPadFor(w, rowFlow);
        return rowFlow
            ? w - 2f * pad - HeroGapFor(w, rowFlow) - ArtworkFor(w, rowFlow)
            : w - 2f * pad;
    }

    /// <summary>The identity column's measure — what attribution, meta and the description wrap to.</summary>
    public static float ContentWidthFor(float colW, bool rowFlow)
        => MathF.Min(ContentWMax, MathF.Max(ContentWMin, CopyAvailFor(colW, rowFlow)));

    /// <summary>The title's OWN measure — wider than the body column beside/under it, because a headline running wider
    /// than its body copy is ordinary editorial typography (issue #79). Shares <see cref="CopyAvailFor"/> with
    /// <see cref="ContentWidthFor"/> so the two widths cannot drift apart at the same column width.</summary>
    public static float TitleWidthFor(float colW, bool rowFlow)
        => MathF.Min(TitleWMax, MathF.Max(ContentWMin, CopyAvailFor(colW, rowFlow)));

    /// <summary>Old rung ladder's line height, paired to a size on the SAME four-rung ramp the deleted
    /// <c>TitleSizeFor</c> used to climb (Subtitle 20/28 · Title 28/36 · TitleLarge 40/52 · the borrowed
    /// <c>WaveeType.ArtistTitle</c> 48/60). The VERTICAL/hero-system title replaced its half of this ladder with a
    /// measured type plan (<see cref="TitleTypeFor(float,bool,string?,bool,bool,bool,bool)"/> and friends, below) —
    /// see that section's doc for why: the natural line box this engine resolves for Segoe UI Variable
    /// (<see cref="NaturalLineRatio"/> = 1.3301 em) is TALLER than every authored pair in this old ladder except its
    /// bottom rung, so every size above 20 was silently under-reserving ~48 DIP at the top of the ladder.
    /// <para>This method survives anyway: the TWO-COLUMN rail hero (<c>DetailShell.Build</c>'s wide-window arm) has no
    /// title-length-vs-cover-height problem to solve — it just picks TitleLarge (40) or Title (28) off the window's
    /// HEIGHT — and reaches this exact static method by name. Keeping the two rungs it is actually called with (40 and
    /// 28) correct is enough; the rest of the old ladder is dead code left in place rather than half-deleted.</para></summary>
    public static float TitleLineHeightFor(float titleSize)
        => titleSize >= 96f ? 104f
         : titleSize >= 72f ? 80f
         : titleSize >= 56f ? 64f
         : titleSize >= 40f ? 52f
         : titleSize >= 28f ? 36f : 28f;

    /// <summary>Description line cap: a touch shorter beside the artwork (the measure is narrower there anyway), a
    /// touch taller when the copy owns the full column.</summary>
    public static int DescriptionMaxLines(bool rowFlow) => rowFlow ? 3 : 4;

    /// <summary>The identity box's minimum height in row flow: the artwork's own edge, so a short identity column
    /// (a bare title + action row, no attribution/meta/description) cannot lay out shorter than the cover beside it —
    /// issue #78's fix. Stacked flow has nothing to defend against (the identity is already BELOW the artwork, so
    /// there is no cross-axis mismatch to open a gap from) and returns 0. This adds no height: identity already lays
    /// out at its natural size, and <see cref="MathF.Max(float,float)"/>-ing that against the artwork edge is exactly
    /// what <see cref="HeroBandHeight"/> already computes for the row's cross size — a MinHeight here only redistributes
    /// the identity column's OWN slack (via its last block's Grow) instead of leaving it as dead space under the action
    /// row.</summary>
    public static float IdentityMinHeightFor(float colW, bool rowFlow)
        => rowFlow ? ArtworkFor(colW, rowFlow) : 0f;

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
    /// <summary>The BOX the band reserves for the list command bar — the real <c>CommandBarSurface</c> height
    /// (<c>DetailTracks.CommandBarSurface</c>), which pads a <see cref="ToolbarPillHeight"/> row by
    /// <see cref="ToolbarSurfacePadY"/> top and bottom. Was 32 (the pill height only), a 12-DIP under-reserve in every
    /// band; <c>CommandBarSurface</c> now reads these four constants directly, so skeleton and live cannot drift again.</summary>
    public const float ToolbarRowHeight = 44f;
    /// <summary>What the skeleton actually DRAWS inside the toolbar band — <c>WaveeSize.ControlH</c>, the pill row's own
    /// height.</summary>
    public const float ToolbarPillHeight = 32f;
    public const float ToolbarSurfacePadX = 6f;
    public const float ToolbarSurfacePadY = 5f;
    // ── the title TYPE PLAN ──────────────────────────────────────────────────────────────────────────────────────
    //
    // WHY THIS REPLACED THE RUNG LADDER. Two measured facts about the engine, verified against its line-layout and
    // auto-fit code (not re-derived here):
    //
    //   1. The engine's default line stacking resolves a line box as max(naturalLineBox, LineHeight). Segoe UI
    //      Variable's natural line box is exactly 1.3301 em (NaturalLineRatio below) — TALLER than every authored
    //      size/line-height pair the old rung ladder published except its bottom rung (20/28 = 1.40). So 96/104
    //      (1.083), 72/80, 56/64 and 40/52 were all silently DISCARDED at render time in favour of the natural box,
    //      and IdentityHeightFor's reservation (which summed the AUTHORED pair) under-reserved by as much as ~48 DIP
    //      at the top rung. Every height in this section is therefore computed from NaturalLineRatio, never from an
    //      authored line-height literal — and the hero clears its own LineHeight to NaN (float.NaN) rather than
    //      inheriting Ui.Title's 36, so there is nothing left for the engine to discard.
    //
    //   2. TextEl's auto-fit (driven by MinSize) is a real binary search over the shaped glyph run that ONLY SHRINKS
    //      (it can grow to at most the authored Size) and only counts LINES — it knows nothing about a height
    //      budget and cannot correct an authored Size that under- or over-shoots one. So the app has to pick a good
    //      Size up front; auto-fit's job is to correct that estimate against real shaped metrics (kerning, the exact
    //      font fallback, subpixel rounding) within a MinSize…Size window, not to discover the size from nothing.
    //      TitleTypeFor's whole job is picking that Size well; SnapTitleSize/MinSize below exist to keep the window
    //      wide enough that auto-fit is actually armed (DetailVerticalLayout's four preconditions: MinSize > 0,
    //      MinSize < Size, MaxLines > 0, Wrap != NoWrap, a definite width — TitleWidthFor/plan.WrapWidth supplies
    //      the last one).
    //
    // "Fill the width" is impossible for a short title in general — "Pony" is ~2.24 em wide, and filling an 820-DIP
    // measure with it needs ~367 px type, which is absurd. The empty band under a short title is a HEIGHT problem,
    // not a width one: the fix below makes the cover's own height the type's budget (TitleHeightBudgetFor), so a
    // one-line "Pony" grows until it has spent the same vertical room a long album title's two lines would.

    /// <summary>The resolved answer <see cref="TitleTypeFor(float,float,float,float,float)"/> hands back: the size to
    /// author, the MinSize to arm the engine's auto-fit with, the line height to reserve (already the NATURAL line
    /// box for that size — see <see cref="NaturalLineRatio"/> — never an authored literal an under-height rung could
    /// silently discard), how many lines the plan spends, and the wrap width it was solved against.
    /// <para><see cref="BlockHeight"/> is the number every reservation actually wants: <c>Lines * LineHeight</c>, the
    /// full vertical extent the title's block occupies in the identity column — what
    /// <c>IdentityHeightFor</c> adds back on top of the rest of
    /// the column's chrome.</para></summary>
    public readonly record struct TitleTypePlan(float Size, float MinSize, float LineHeight, int Lines, float WrapWidth)
    {
        public float BlockHeight => Lines * LineHeight;
    }

    /// <summary>Segoe UI Variable's measured natural line box, in em — the height <c>LineStacking.MaxHeight</c>
    /// resolves a line to when the authored LineHeight is smaller (which, before this file, was true of every rung
    /// above 20). Every height derived from a <see cref="TitleTypePlan.Size"/> below uses THIS ratio, never an
    /// authored line-height literal, so the reserved height and the rendered height cannot drift apart again.</summary>
    public const float NaturalLineRatio = 1.3301f;

    /// <summary>The absolute ends of the title's size range: never larger than a 96-DIP display size (the old
    /// ladder's own top rung), never smaller than 20 (its own bottom rung) for the CHOSEN size, and never smaller
    /// than 18 for the auto-fit MINIMUM (one below the floor, so a pathological width still leaves auto-fit a
    /// non-empty shrink window).</summary>
    public const float TitleSizeCap = 96f, TitleSizeFloor = 20f, TitleMinSizeFloor = 18f;

    /// <summary>The plan never asks for more than two lines. A title long enough to want a third is better served by
    /// the wrap cap's ellipsis than by shrinking type further — the same call the old ladder's fixed two-line cap
    /// made.</summary>
    public const int TitleLinesMax = 2;

    /// <summary>The two ends of <see cref="FluidTitleCapFor"/>'s straight line: a narrow phone column locks the
    /// title's ceiling at 28 (the old ladder's own Title rung) and a wide hero window at 96 (the old ladder's own
    /// top rung) — the same two numbers the deleted ladder used to reach by stepping, reached here by interpolating
    /// instead, so the cap itself has no seam a resize could visibly cross.</summary>
    public const float CapLockMinW = 360f, CapLockMinSize = 28f, CapLockMaxW = 1100f, CapLockMaxSize = TitleSizeCap;

    /// <summary>The size ceiling <see cref="TitleTypeFor(float,float,float,float,float)"/> is clamped to, as a
    /// straight line between the two <c>CapLock</c> anchors (clamped flat outside them). This is a CEILING, not the
    /// chosen size — a short title still only grows until <see cref="TitleHeightBudgetFor"/> or its own width says
    /// stop; this just says how large a one-word title is allowed to get at this column width, so "Pony" cannot spend
    /// an absurd point size chasing an empty band that a length constraint should have stopped first.</summary>
    public static float FluidTitleCapFor(float colW)
    {
        float w = colW > 0f ? colW : FallbackW;
        const float Slope = (CapLockMaxSize - CapLockMinSize) / (CapLockMaxW - CapLockMinW);
        const float Intercept = CapLockMinSize - Slope * CapLockMinW;
        return Math.Clamp(Slope * w + Intercept, CapLockMinSize, CapLockMaxSize);
    }

    /// <summary>The grid a chosen size is rounded to — coarser at the display end (8 DIP above 64), finer in the
    /// UI-label range (2 DIP below 32). This is what keeps a continuous computation from asking the
    /// <c>TextMeasureCache</c> a slightly different question on every frame: a cache keyed on the exact float would
    /// miss (and re-shape) on every sub-pixel resize step, where a handful of shared grid points are cache HITS across
    /// a whole span of widths.</summary>
    public static float TitleSnapStep(float size) => size >= 64f ? 8f : size >= 32f ? 4f : 2f;

    /// <summary>Clamp then round to <see cref="TitleSnapStep"/>'s grid, re-deriving the step from the ROUNDED value
    /// (a size that rounds across a step boundary must snap to the new step's grid, not the old one) and clamping the
    /// final result back inside the floor/cap — the double clamp is belt-and-braces against the re-round nudging the
    /// value back outside by less than a whole step.</summary>
    public static float SnapTitleSize(float size)
    {
        float s = Math.Clamp(size, TitleSizeFloor, TitleSizeCap);
        float snapped = MathF.Round(s / TitleSnapStep(s)) * TitleSnapStep(s);
        float step = TitleSnapStep(snapped);
        return Math.Clamp(MathF.Round(s / step) * step, TitleSizeFloor, TitleSizeCap);
    }

    /// <summary>Cheap per-glyph width estimate, in em, for a title string — NOT real shaping (this file is BCL-only
    /// and cannot call into the engine's shaper), just enough of one to pick a Size that auto-fit's real binary
    /// search then corrects against actual metrics. <paramref name="trackingEm"/> matches <c>WaveeType.DetailHero</c>'s
    /// −20/1000 em CharSpacing, applied once per inter-character gap (Length − 1 gaps, never negative).</summary>
    public static float TitleAdvanceEm(string? title, float trackingEm = -0.020f)
    {
        if (string.IsNullOrEmpty(title)) return 0f;
        float sum = 0f;
        for (int i = 0; i < title.Length; i++) sum += CharAdvanceEm(title[i]);
        return MathF.Max(0.001f, sum + trackingEm * (title.Length - 1));
    }

    /// <summary>The widest single WORD in the title, in em — the true limit on how large a title can grow before it
    /// no longer fits the wrap width on ONE line, regardless of how short the rest of the string is (a title cannot
    /// wrap mid-word). <see cref="TitleTypeFor(float,float,float,float,float)"/> uses this to cap the width-fit term
    /// so a title with one very long word (or none at all — an empty title) never computes a size that would overflow
    /// on its own longest run.</summary>
    public static float TitleLongestWordEm(string? title, float trackingEm = -0.020f)
    {
        if (string.IsNullOrEmpty(title)) return 0f;
        float best = 0f, cur = 0f; int runLen = 0;
        for (int i = 0; i <= title.Length; i++)
        {
            char c = i < title.Length ? title[i] : ' ';
            if (c is ' ' or '\t' or '\n' or ' ')
            {
                if (runLen > 0) best = MathF.Max(best, cur + trackingEm * (runLen - 1));
                cur = 0f; runLen = 0;
            }
            else { cur += CharAdvanceEm(c); runLen++; }
        }
        return MathF.Max(0.001f, best);
    }

    // Six buckets standing in for real shaping: hairline marks and 'i/l/I/j'-class glyphs, narrow serifs/punctuation,
    // the mid-narrow lowercase band, wide caps/round-caps, the widest (M/W-class and CJK, which is set to a full em —
    // conservative, since it is never narrower than that), and everything else at Segoe's typical ~0.57 em average.
    static float CharAdvanceEm(char c) => c switch
    {
        ' ' or '.' or ',' or ':' or ';' or '\'' or '`' or 'i' or 'I' or 'j' or 'l' or '|' or '!'          => 0.26f,
        '(' or ')' or '[' or ']' or '{' or '}' or 'f' or 'r' or 't' or '1' or '*' or '/' or '\\'
            or '-' or '_'                                                                                 => 0.37f,
        's' or 'z' or 'J' or 'L' or 'x' or 'F' or 'E' or '?' or '"'                                        => 0.48f,
        'M' or 'W' or 'm' or 'w' or '@' or '%'                                                             => 0.90f,
        'A' or 'C' or 'D' or 'G' or 'H' or 'K' or 'R' or 'U' or 'V' or 'X' or 'Q' or 'O' or 'N' or '&'
            or '<' or '>' or '=' or '+' or '~' or '^'                                                      => 0.70f,
        _ => c >= '⺀' && c <= '퟿' ? 1.00f : 0.57f,
    };

    /// <summary>The identity column's CHROME height — every block except the title itself — plus a running block
    /// count that STARTS AT ONE (the title, which every caller adds separately: <see cref="TitleHeightBudgetFor"/>
    /// spends the title's OWN height as the thing being solved for, and <see cref="IdentityHeightFor"/> adds
    /// <see cref="TitleTypePlan.BlockHeight"/> back in) so the inter-block gap count agrees between the two.
    /// <paramref name="description"/> is a caller-supplied flag rather than always-on: <see cref="TitleHeightBudgetFor"/>
    /// deliberately calls this with <c>description: false</c> — see that method's doc for why.</summary>
    static (float Height, int Blocks) IdentityChrome(bool eyebrow, bool attribution, bool meta, bool pulse,
                                                    bool description, bool rowFlow)
    {
        float h = 0f; int blocks = 1;
        if (eyebrow) { h += EyebrowRowHeight; blocks++; }
        h += AccentRuleRowHeight; blocks++;
        if (attribution) { h += AttributionRowHeight; blocks++; }
        if (meta) { h += MetaRowHeight; blocks++; }
        if (pulse) { h += PulseRowHeight; blocks++; }
        h += ActionRowHeight; blocks++;
        if (description) { h += DescriptionMaxLines(rowFlow) * DescriptionLineHeight; blocks++; }
        return (h, blocks);
    }

    /// <summary>The title's HEIGHT BUDGET in row flow: whatever the cover's own edge leaves over once the identity
    /// column's other chrome (and the gaps between all of it) are subtracted. This is the fix for the empty band under
    /// a short title — instead of asking "how wide is the title" (unanswerable for a short string without absurd type),
    /// it asks "how much of the cover's height is still unclaimed", and lets <see cref="TitleTypeFor(float,float,float,float,float)"/>
    /// spend that budget on size × lines.
    /// <para>The DESCRIPTION is deliberately EXCLUDED from this budget (<c>description: false</c> below) even though
    /// it is a real block in the finished column: the description is a TAIL that is allowed to run past the bottom
    /// of the cover (the identity column's natural height already exceeds the artwork edge once a multi-line blurb is
    /// present, and <see cref="IdentityMinHeightFor"/> only pins the column's MINIMUM, never its maximum). Charging
    /// the budget for it would make a playlist with a long description pick a SMALLER title than an otherwise-identical
    /// playlist with none — the description's own length would be steering the title's size, which is backwards: the
    /// title is what earns the reader's first look, the description is what they scroll to.</para>
    /// <para>Stacked flow has no such budget — the identity column sits BELOW the artwork with the page's own scroll
    /// beneath it, so there is no cover edge to race against, and this returns 0 (see
    /// <see cref="TitleTypeFor(float,bool,string?,bool,bool,bool,bool)"/>'s <c>heightBudget: 0</c> in that
    /// case, which starves the height-fit term entirely and leaves the width-fit term — plus the fluid cap — in sole
    /// control, exactly like the old ladder's stacked arm.)</para></summary>
    public static float TitleHeightBudgetFor(float colW, bool rowFlow,
        bool eyebrow, bool attribution, bool meta, bool pulse = false)
    {
        if (!rowFlow) return 0f;
        var (chrome, blocks) = IdentityChrome(eyebrow, attribution, meta, pulse, description: false, rowFlow: true);
        return MathF.Max(0f, ArtworkFor(colW, rowFlow) - chrome - (blocks - 1) * IdentityGap);
    }

    // Packing factors convert an em-width SUM into a "how many characters of THIS size fit the measure" estimate.
    // OneLinePacking (1/1.08) accounts for inter-word spaces costing more than CharAdvanceEm's average assumes on a
    // single unbroken run; TwoLinePacking (0.94) is looser because wrapping to two lines only has to fit each HALF of
    // the string per line, so it tolerates a slightly larger size for the same total content; TitleFloorPacking
    // (0.78) is deliberately the loosest of the three — it sizes the auto-fit MINIMUM, which only has to be small
    // enough that the engine's real shaped measurement (always more accurate than this estimate) still fits, not a
    // precise answer.
    public const float OneLinePacking = 1f / 1.08f, TwoLinePacking = 0.94f, TitleFloorPacking = 0.78f;

    /// <summary>The core of the type plan: given how much WIDTH and HEIGHT the title may spend, and how wide the
    /// string itself is (its total advance and its longest unbreakable word), pick the largest size — and the fewest
    /// lines — that fits both budgets, never exceeding <paramref name="sizeCap"/>.
    /// <para>The two-line case is not preferred by default — <see cref="TitleLinesMax"/> is walked from 1 up, and a
    /// candidate only WINS (<c>cand &gt; best + 0.5f</c>) if going to that many lines buys a strictly larger size. A
    /// short title's one-line width-fit is already huge (its own text is nowhere near the measure), so its ONLY
    /// binding constraint is the height budget, which shrinks as lines increase (dividing the same budget over more
    /// lines) — one line always wins for a short string. A LONG title's one-line width-fit is small (long text, same
    /// measure), so splitting to two lines roughly DOUBLES the width each line has to cover the same total content —
    /// worth it only when that doubling actually raises the achievable size, which is exactly the length threshold at
    /// which a title deserves two lines instead of a shrink-and-ellipsis. This is where the height budget earns its
    /// keep: without it, a short title's one-line size would be bounded only by the (rarely binding) width term and
    /// the fluid cap, and would never grow to fill unclaimed cover height at all.</para></summary>
    public static TitleTypePlan TitleTypeFor(float titleW, float heightBudget, float sizeCap,
                                             float advanceEm, float longestWordEm)
    {
        float avail = titleW > 0f ? titleW : ContentWMin;
        float adv   = advanceEm > 0.001f ? advanceEm : 0.001f;
        float word  = Math.Clamp(longestWordEm > 0.001f ? longestWordEm : adv, 0.001f, adv);
        float cap   = Math.Clamp(sizeCap, TitleSizeFloor, TitleSizeCap);

        float best = TitleSizeFloor; int bestLines = 1;
        for (int lines = 1; lines <= TitleLinesMax; lines++)
        {
            float packing  = lines == 1 ? OneLinePacking : TwoLinePacking;
            float widthFit = MathF.Min(packing * lines * avail / adv, avail / word);
            float heightFit = heightBudget > 0f ? heightBudget / (lines * NaturalLineRatio) : float.PositiveInfinity;
            float cand = MathF.Min(MathF.Min(widthFit, heightFit), cap);
            if (cand > best + 0.5f) { best = cand; bestLines = lines; }
        }

        float size = SnapTitleSize(best);
        float step = TitleSnapStep(size);
        // The auto-fit floor: how small the WIDTH alone would force this many lines to go, on the loosest packing —
        // clamped so it sits strictly below the chosen size by at least one snap step (auto-fit needs MinSize < Size
        // to arm at all) and never below the absolute minimum.
        float floorFit = TitleFloorPacking * bestLines * avail / adv;
        float min = Math.Clamp(MathF.Min(SnapTitleSize(floorFit), size - step),
                               TitleMinSizeFloor, MathF.Max(TitleMinSizeFloor, size - step));
        return new TitleTypePlan(size, min, MathF.Round(size * NaturalLineRatio), bestLines, avail);
    }

    /// <summary>The convenience form: given a column width, flow and the title string (plus the same presence flags
    /// <see cref="TitleHeightBudgetFor"/> takes), resolves the wrap width, the height budget, the fluid cap and the
    /// string's own metrics, then calls the pure five-float overload above. This is what
    /// <c>DetailVerticalHero.Build</c> calls with the real title; callers that do not know the title string yet (the
    /// skeleton, and the loaded hero's own pre-measure fallback via <see cref="HeroBandHeight(float,bool,bool,bool,bool,bool,bool)"/>)
    /// pass <c>title: null</c> — <see cref="TitleAdvanceEm"/> then returns 0, which starves both width-fit terms
    /// above (division by the floored 0.001f minimum makes them effectively infinite) and leaves the size bounded
    /// only by the height budget and the fluid cap: exactly the pessimistic "reserve for the largest one-line title
    /// this width could plausibly show" answer a pre-measure reservation needs.</summary>
    public static TitleTypePlan TitleTypeFor(float colW, bool rowFlow, string? title,
        bool eyebrow, bool attribution, bool meta, bool pulse = false)
        => TitleTypeFor(TitleWidthFor(colW, rowFlow),
                        TitleHeightBudgetFor(colW, rowFlow, eyebrow, attribution, meta, pulse),
                        FluidTitleCapFor(colW),
                        TitleAdvanceEm(title), TitleLongestWordEm(title));

    /// <summary>Resize hysteresis for the CHOSEN size, mirroring <see cref="RowFlow(float,bool,bool)"/>'s pattern: a
    /// continuous recompute on every layout pass would otherwise let a 0.3-DIP width jitter flip the snapped size back
    /// and forth across its own snap step. Growing needs the target to clear TWO steps past the current size before
    /// committing (so a title does not grow, overflow its old budget for one frame, then shrink back); shrinking needs
    /// only one step (so a genuinely narrower column is honoured promptly, matching <see cref="ArtworkFor"/>'s own
    /// eager-shrink/reluctant-grow asymmetry elsewhere in this file). Uninitialized state (a fresh mount, or a current
    /// size that has fallen below the floor — never a real committed size) always takes the target outright.</summary>
    public static float StableTitleSize(float target, float current, bool initialized)
    {
        if (!initialized || current < TitleMinSizeFloor) return target;
        float step = TitleSnapStep(current);
        if (target > current) return target - current >= 2f * step ? target : current;
        if (target < current) return current - target >= step ? target : current;
        return current;
    }

    /// <summary>The identity column's height: the CHROME the hero always/conditionally emits, plus this specific
    /// <paramref name="title"/> plan's own block height, plus one <see cref="IdentityGap"/> per boundary between
    /// blocks. Replaces the old float-colW overload — the title's height is no longer a function of width alone, it is
    /// whatever <see cref="TitleTypeFor(float,bool,string?,bool,bool,bool,bool)"/> already decided for THIS title, so
    /// the caller must have built (or been handed) that decision first.</summary>
    public static float IdentityHeightFor(in TitleTypePlan title, bool rowFlow,
        bool eyebrow, bool attribution, bool meta, bool description, bool pulse = false)
    {
        var (chrome, blocks) = IdentityChrome(eyebrow, attribution, meta, pulse, description, rowFlow);
        return chrome + title.BlockHeight + (blocks > 1 ? (blocks - 1) * IdentityGap : 0f);
    }

    /// <summary>The identity column's inter-block gap ceiling: <see cref="IdentityGapFor"/> will widen the resting
    /// <see cref="IdentityGap"/> to spread slack, but never past this, so the column does not read as loosely
    /// double-spaced when a short bare title leaves a lot of the cover's height unclaimed by chrome.</summary>
    public const float IdentityGapMax = 12f;

    /// <summary>Row flow's identity column carries a MinHeight equal to the cover's edge
    /// (<see cref="IdentityMinHeightFor"/>), so a column shorter than the cover opens SLACK somewhere. Before the type
    /// plan, that slack landed entirely on the last metadata block's <c>Grow = 1</c> fill (issue #78) — all of it in
    /// ONE gap, between the metadata and the action row. Now that the title itself claims most of a short column's
    /// spare height, spreading the (smaller) remainder evenly across every inter-block gap instead reads as a more
    /// deliberate, evenly-set page than one oversized gap. Stacked flow has no such slack to spread (the identity
    /// column is never taller than its own natural size there) and returns the resting <see cref="IdentityGap"/>
    /// unchanged — cheap early-out, and it keeps the fill block in <c>DetailVerticalHero</c> as the ONLY consumer of
    /// stacked-flow slack, unchanged from before.</summary>
    public static float IdentityGapFor(float colW, bool rowFlow, in TitleTypePlan title,
        bool eyebrow, bool attribution, bool meta, bool description, bool pulse = false)
    {
        if (!rowFlow) return IdentityGap;
        var (chrome, blocks) = IdentityChrome(eyebrow, attribution, meta, pulse, description, rowFlow);
        int gaps = Math.Max(1, blocks - 1);
        float slack = ArtworkFor(colW, rowFlow) - (chrome + title.BlockHeight + gaps * IdentityGap);
        if (slack <= 0f) return IdentityGap;
        return MathF.Min(IdentityGapMax, MathF.Round((IdentityGap + slack / gaps) / 2f) * 2f);
    }

    /// <summary>The whole expanded hero band: the padded artwork/identity composition (stacked or side-by-side, the
    /// same reflow <see cref="RowFlow(float)"/> selects) plus the list toolbar row that rides under it. Takes the
    /// title's plan directly — the live hero has already built one from the real title string.</summary>
    public static float HeroBandHeight(float colW, bool rowFlow, in TitleTypePlan title,
        bool eyebrow, bool attribution, bool meta, bool description, bool pulse = false)
    {
        float w = colW > 0f ? colW : FallbackW;
        float pad = HeroPadFor(w, rowFlow);
        float art = ArtworkFor(w, rowFlow);
        float identity = IdentityHeightFor(title, rowFlow, eyebrow, attribution, meta, description, pulse);
        // Row flow's cross size is the taller of the two columns (identity carries a MinHeight = art in row flow, so
        // this is max(art, max(art, natural)) = max(art, natural) either way — see IdentityMinHeightFor); stacked adds
        // them over the gap.
        float hero = rowFlow ? MathF.Max(art, identity) : art + HeroGapFor(w, rowFlow) + identity;
        return pad + hero + HeroBottomPad
             + ExpandedToolbarTopPad + ToolbarRowHeight + ExpandedToolbarBottomPad;
    }

    /// <summary>Pre-measure / skeleton overload for a caller that does not know the title STRING yet — the loading
    /// skeleton has no text to shape, and the loaded hero's own pre-measure fallback (<c>DetailTracks.VerticalHeaderHeight</c>)
    /// runs before <c>OnBoundsChanged</c> has published a real measured height. Builds the same PESSIMISTIC plan
    /// <see cref="TitleTypeFor(float,bool,string?,bool,bool,bool,bool)"/> describes for <c>title: null</c> — a
    /// one-line title sized to <see cref="FluidTitleCapFor"/>, the tallest single-line reservation any real title at
    /// this width could need — rather than inventing a representative string. Reserving less risks the very shove
    /// this band exists to prevent (D49); reserving more (e.g. always assuming two lines) would waste band height
    /// that most titles will never claim.</summary>
    public static float HeroBandHeight(float colW, bool rowFlow,
        bool eyebrow, bool attribution, bool meta, bool description, bool pulse = false)
        => HeroBandHeight(colW, rowFlow,
            TitleTypeFor(colW, rowFlow, title: null, eyebrow, attribution, meta, pulse),
            eyebrow, attribution, meta, description, pulse);

    /// <summary>Scroll distance over which the expanded hero becomes the 56-DIP context band.</summary>
    public static float CollapseDistance(float expandedHeight)
        => MathF.Max(1f, expandedHeight - CompactIdentityHeight);

    /// <summary>Actual pinned list-chrome extent. The optional Liked filter rail is part of the same sticky plate, so
    /// paint and input must both account for its 48-DIP rail+gap instead of assuming the base header.</summary>
    public static float ChromeExtent(float contentFilterExtent = 0f)
        => ChromeHeaderHeight + ChromeDividerHeight + MathF.Max(0f, contentFilterExtent);

    public static float StickyClipInset(float contentFilterExtent = 0f)
        => CompactIdentityHeight + ChromeExtent(contentFilterExtent);

    // ── the vertical viewport's slot map ───────────────────────────────────────────────────────────────────────────
    // Two persistent prefix slots (the hero, then the pinned chrome), then the recycled row band, then — in the HERO
    // system only — the metadata-facts FOOTER. The footer is a slot down here rather than a block in the hero's
    // identity column because that column is the page's OPENING: three stacked analytics cards under the description
    // push the first track below the fold, so the page opens on charts instead of on the songs it is about. The
    // two-column/rail arm has no such problem (there the cards sit in a column BESIDE the tracks, never above them)
    // and is deliberately untouched — see DetailRail's `rail:likedfacts` row.
    /// <summary>The persistent prefix slots every vertical viewport leads with (hero, pinned chrome). Track rows — and
    /// the facts footer — recycle beneath them; <c>TrackList.VerticalTrackStart</c> is this number.</summary>
    public const int PrefixCount = 2;

    /// <summary>Row slots the viewport holds open. An empty / still-loading list keeps ONE (the placeholder,
    /// <see cref="DetailVerticalItemRole.Empty"/>), so the footer's index never depends on whether the list loaded.</summary>
    internal static int RowSlots(int visibleTracks) => Math.Max(1, visibleTracks);

    /// <summary>Where the facts footer lands: immediately after the last row slot — the very bottom of the page. Only
    /// addressed when the caller says the footer exists.</summary>
    internal static int FooterIndex(int visibleTracks) => PrefixCount + RowSlots(visibleTracks);

    /// <summary>The viewport's item TOTAL — what the list's count signal carries. The footer is ONE extra item, not a
    /// wrapper around the list, so it scrolls with the rows and costs nothing while it is off-screen.</summary>
    internal static int ItemCount(int visibleTracks, bool hasFacts)
        => PrefixCount + RowSlots(visibleTracks) + (hasFacts ? 1 : 0);

    /// <summary>The first two slots are persistent chrome; every live suffix slot is an expandable track container,
    /// except the optional trailing facts footer. Keeping this pure prevents the vertical playlist from accidentally
    /// bypassing the drawer host — and keeps "where do the facts cards go" a testable answer rather than index
    /// arithmetic buried inside a recycled slot.</summary>
    internal static DetailVerticalItemRole ItemRole(int itemIndex, int visibleTracks, bool hasFacts = false)
    {
        if (itemIndex == 0) return DetailVerticalItemRole.Hero;
        if (itemIndex == 1) return DetailVerticalItemRole.Chrome;
        int display = itemIndex - PrefixCount;
        if (display >= 0 && display < Math.Max(0, visibleTracks)) return DetailVerticalItemRole.ExpandableTrack;
        // After the rows — and after the empty-list placeholder, which owns the single row slot an empty list keeps.
        if (hasFacts && itemIndex == FooterIndex(visibleTracks)) return DetailVerticalItemRole.Footer;
        return DetailVerticalItemRole.Empty;
    }

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
