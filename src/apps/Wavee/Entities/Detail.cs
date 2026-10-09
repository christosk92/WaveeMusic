// ── Entities/Detail.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// THE SHARED DETAIL FRAME'S PURE RULES: the width breakpoints and their hysteresis, the vertical/hero arithmetic (the
// title type plan, the reserved band, the collapse ladder, the slot map), the rail policy and its persisted keys, the
// header merge, the notice ladder, the context band's geometry and scroll spy, the cover latch, the skeleton's bar
// geometry, the per-kind config table, the header text (eyebrow / byline / meta lines) and the per-context sort keys.
//
// Role: CORE
// Owner: M
// Wave: 4.5
// Budget: 900 lines
// Spec: ch 03 §8-§9 (+ W6-W9, W12, W27, W28 and §3's clip rows), ch 30 §2.1-§2.2, §3.3
//
// Ported from 0.2.9's Features/Detail/{DetailLayoutBreakpoints, DetailVerticalLayout, DetailRailPolicy,
// DetailHeaderMergeRules, PlaylistPageNoticeRules, ContextBandLayout, DetailConfig, DetailSkeleton (geometry only),
// DetailRail.EyebrowText/ShowCollaborators, DetailVerticalHero's byline, DetailPage.ResolveConfig/Map*,
// DetailShell.SortColKey/SortDescKey} and Wavee.Core's ImageSource.SameArt/PreferVisible. THE NUMBERS ARE LOAD-BEARING
// and are ported verbatim (ch 03 §9 "must not be simplified"): the title type plan, NaturalLineRatio 1.3301, the snap
// grid, the packing constants and every hysteresis band. Two deliberate 0.3 changes, both from ch 03: the W28 `chart`
// presence flag (the chart caption is reserved like the daylist pulse) and the real table header height in
// ChromeExtent/StickyClipInset (the Classic skin's 32, parity item 63). One deliberate 2026-09-30 change (the owner's
// "never trim a title" report): the title plan no longer leans on engine auto-fit or a size hysteresis — it is ONE
// size per (bucketed width, title), drawn exactly, wrapping freely up to TitleLineCap (StableTitleSize is gone).
//
// Podcast rework wave P2 (podcast-show-rework-implementation.md §5.4): `DetailKind.Episode` + `Config.Episode` (the show's
// frame, sharing the show's rail pair), the Episode eyebrow arm, and the rail's ROW MODEL — `RailSlotSet` (which rail
// slots a page declared), `RailLayout.RowsFor` (the loaded rail's row decisions as data), `Skeleton.RailPlanFor` (the
// skeleton's prediction, now slot-aware) and `RailLayout.HeightOf` (the one nominal height both are measured by).
//
// Already elsewhere — not restated here: DetailNotice (Entities/Playlist.cs), the reveal ramp (Design.RevealRamp), the
// list state (Playlist.RowsStateOf). Deleted, not ported: DetailLiveRefresh, DetailOwnerIds, HeroCta,
// DetailRail.BilledArtists, DetailConfig.CapTitle/Columns, PreReleaseDerivation/AlbumReleaseFactsRules (Album.cs, Wave 5).
//
// Engine-free except Loc (FluentGpu.Localization) and ItemsSelectionMode (FluentGpu.Controls): nothing here reads a
// signal, mounts a node or ticks a clock.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>Which detail surface a route resolves to. A single / compilation is the Album kind with a different
/// <see cref="Detail.Config"/> (<see cref="Detail.Config.For"/>). <see cref="Episode"/> is a podcast episode's page: the
/// show's frame (<see cref="Detail.Config.Episode"/>), its rail led by the show link rather than an eyebrow.</summary>
public enum DetailKind : byte { Album, Playlist, Liked, Show, Episode }

/// <summary>The right-column content: music tracks, or podcast episodes.</summary>
public enum DetailContent : byte { Tracks, Episodes }

/// <summary>The identity header's badge row style.</summary>
public enum BadgeStyle : byte { None, TypeYear, OwnerRow }

/// <summary>The heart affordance: absent (Liked), save (album), follow (playlist, show).</summary>
public enum HeartMode : byte { None, Save, Follow }

/// <summary>Which persisted rail pair (width + collapsed) a two-column surface reads and writes. One scope per surface
/// family, so Liked never shares the album's pair by fallthrough; <see cref="Uniform"/> is the fifth, synthetic scope
/// "Keep left-rail same size" resolves every surface to (<see cref="Detail.RailPolicy.ScopeFor"/>). The podcast family is
/// ONE scope: an episode's page reads and writes the show's pair (<see cref="Detail.Config.Episode"/>), so dragging the
/// rail on either moves both. The ordinal is the frame's rail-cell index — append only.</summary>
public enum RailScope : byte { Album, Playlist, Liked, Show, Uniform }

/// <summary>Identity of one slot in the vertical (hero-system) list viewport.</summary>
public enum VerticalItemRole : byte { Hero, Chrome, ExpandableTrack, Footer, Empty }

public static partial class Detail
{
    // ══ 1. BREAKPOINTS ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The page's width ladders with resize hysteresis: the table TIER ladder (measured on the right column)
    /// and the page MODE ladder (measured on the page's own content column). Different boxes — keep them apart.</summary>
    public static class Breakpoints
    {
        public const float TierHysteresisDip = 24f;
        public const float ModeHysteresisDip = 24f;

        public static int NominalTierFor(float w) =>
            w <= 0f ? 0 : w >= 860f ? 0 : w >= 720f ? 1 : w >= 560f ? 2 : w >= 440f ? 3 : w >= 340f ? 4 : w >= 300f ? 5 : 6;

        /// <summary>Pre-measure seed, so a 360-DIP launch never composes the wide table for its first frame.</summary>
        public static int InitialTierForViewport(float viewportWidth) => NominalTierFor(viewportWidth);

        /// <summary>The WINDOW viewport overstates the page by roughly a sidebar. Deliberately the sidebar's SMALLEST
        /// plausible footprint: a too-small allowance can seed a too-wide arm (the hero-remount flicker this exists to
        /// prevent); a too-large one only seeds a narrower arm, which the next Measure self-corrects.</summary>
        public const float ShellChromeAllowanceDip = Design.Size.NavPaneW;

        /// <summary>A pre-measure PAGE width from the WINDOW viewport — what the mode/tier seeds and the vertical hero's
        /// pre-measure geometry read instead of the raw viewport.</summary>
        public static float EstimatePageWidthFromViewport(float viewportWidth)
            => MathF.Max(0f, viewportWidth - ShellChromeAllowanceDip);

        /// <summary>Narrow immediately; re-admit a column only once the width clears the threshold by
        /// <see cref="TierHysteresisDip"/>. <paramref name="initialized"/> false ⇒ <paramref name="prev"/> is a seed,
        /// not a tier the user has seen: take the nominal tier outright.</summary>
        public static int TierFor(float w, int prev, bool initialized = true)
        {
            if (w <= 0f) return prev;
            if (!initialized) return NominalTierFor(w);
            int nominal = NominalTierFor(w);
            if (nominal >= prev) return nominal;
            int dipped = NominalTierFor(w - TierHysteresisDip);
            return dipped < prev ? dipped : prev;
        }

        public const int VerticalMode = 3;
        public const float VerticalEnterW = 540f;
        public const float VerticalExitW = 580f;
        public const float TwoColumnContentMinW = 300f;

        /// <summary>The vertical page may use tier 6 all the way down; two-column frames keep the 300-DIP guard.</summary>
        public static float ContentMinWidthForMode(int mode)
            => mode == VerticalMode ? 0f : TwoColumnContentMinW;

        public static int NominalModeFor(float w) =>
            w <= 0f ? 0 : w >= 820f ? 0 : w >= 660f ? 1 : w >= 560f ? 2 : VerticalMode;

        /// <summary>Pre-measure page-system seed. Ultra-narrow launches choose Vertical before the first bounds callback.</summary>
        public static int InitialModeForViewport(float viewportWidth) => NominalModeFor(viewportWidth);

        /// <summary>820/660 crossings use <see cref="ModeHysteresisDip"/>; the 540/580 vertical band is its own. A
        /// Vertical answer on a widening measurement (nominal or dipped) is coerced to 2.</summary>
        public static int ModeFor(float w, int currentMode, bool initialized)
        {
            if (w <= 0f) return currentMode;
            if (!initialized) return NominalModeFor(w);
            if (currentMode == VerticalMode) return w >= VerticalExitW ? NominalModeFor(w) : VerticalMode;
            if (w < VerticalEnterW) return VerticalMode;
            int nominal = NominalModeFor(w);
            if (nominal == VerticalMode) return 2;
            if (nominal >= currentMode) return nominal;
            int dipped = NominalModeFor(w - ModeHysteresisDip);
            if (dipped == VerticalMode) dipped = 2;
            return dipped < currentMode ? dipped : currentMode;
        }
    }

    // ══ 2. THE VERTICAL / HERO ARITHMETIC ════════════════════════════════════════════════════════════════════════════

    /// <summary>Width → layout for the UNIFIED hero: ONE composition (artwork, then eyebrow · title · rule · attribution
    /// · meta · pulse · chart · actions · description), and this class answers only how big its parts are. Below
    /// <see cref="RowFlowEnterW"/> the artwork stacks above the identity column; at or above it the two sit side by side.</summary>
    public static class VerticalLayout
    {
        /// <summary><c>Platform.Keys.DetailPageLayout</c> values: Automatic (rail when wide) · Hero (every width).</summary>
        public const int PageAuto = 0;
        public const int PageHero = 1;

        public const float HeroPad = 24f;
        public const float NarrowHeroPad = 16f;
        /// <summary>Below this column width a STACKED hero uses the narrow pad/gap.</summary>
        public const float NarrowPadW = 420f;
        public const float HeroGap = 24f;
        public const float NarrowHeroGap = 16f;
        /// <summary>Under the hero block, before the list toolbar (which adds its own top pad).</summary>
        public const float HeroBottomPad = 8f;

        // ── the ONE breakpoint: stacked ↔ row flow ──
        public const float RowFlowEnterW = 424f;
        /// <summary>24-DIP hysteresis, so a grip parked on the seam cannot flip the flow every frame.</summary>
        public const float RowFlowLeaveW = RowFlowEnterW - 24f;

        // ── artwork ──
        /// <summary>Stacked artwork fills the content width up to this cap.</summary>
        public const float StackedArtMax = 280f;
        /// <summary>Never smaller: below it the cover reads as a list thumbnail.</summary>
        public const float ArtMin = 96f;
        /// <summary>Row-flow band: a clamped fraction of the inner width. <see cref="RowArtMin"/> binds exactly at
        /// <see cref="RowFlowLeaveW"/> (0.44 × (400 − 72) = 144), so the curve is continuous across the flip.</summary>
        public const float RowArtMin = 144f, RowArtMax = 240f, RowArtFraction = 0.44f;

        // ── the text column ──
        /// <summary>The identity column's body cap (attribution / meta / description).</summary>
        public const float ContentWMax = 640f;
        public const float ContentWMin = 160f;
        /// <summary>The title's own, wider cap — a headline may run wider than its body copy (issue #79).</summary>
        public const float TitleWMax = 1000f;

        public const float CompactIdentityHeight = 56f;
        /// <summary>The stuck band's reveal ramp, ending at the collapse floor. A TIMING constant the artist page shares.</summary>
        public const float CompactRevealBand = 44f;

        public const float ExpandedToolbarTopPad = 8f;
        public const float ExpandedToolbarBottomPad = 4f;
        public const float ExpandedContentFadeDistance = 96f;
        /// <summary>The Modern table's column-header height — the DEFAULT of <see cref="ChromeExtent"/>'s middle term.</summary>
        public const float ChromeHeaderHeight = 36f;
        public const float ChromeDividerHeight = 1f;
        public const float StickyFadeBand = 24f;
        public const float FallbackW = 580f;

        /// <summary>Round a width to an 8-DIP bucket, so a sub-pixel resize cannot churn width-folded keys.</summary>
        public static float BucketW(float availableW)
        {
            float w = availableW > 0f ? availableW : FallbackW;
            float b = MathF.Round(w / 8f) * 8f;
            return b > 0f ? b : FallbackW;
        }

        /// <summary>Nominal flow for first layout and skeleton selection.</summary>
        public static bool RowFlow(float colW) => (colW > 0f ? colW : FallbackW) >= RowFlowEnterW;

        /// <summary>Resize-hysteretic flow. <paramref name="initialized"/> false ⇒ take the nominal answer outright.</summary>
        public static bool RowFlow(float colW, bool current, bool initialized)
        {
            if (!initialized) return RowFlow(colW);
            float w = colW > 0f ? colW : FallbackW;
            return current ? w >= RowFlowLeaveW : w >= RowFlowEnterW;
        }

        /// <summary>Outer hero padding. The row arm always takes 24: without this split <see cref="NarrowPadW"/> would
        /// subtract 40 DIP at one crossing and the cover would SHRINK as the window widens.</summary>
        public static float HeroPadFor(float colW, bool rowFlow)
            => rowFlow || (colW > 0f ? colW : FallbackW) >= NarrowPadW ? HeroPad : NarrowHeroPad;

        /// <summary>Gap between the artwork and the identity column; flow-aware for the same reason.</summary>
        public static float HeroGapFor(float colW, bool rowFlow)
            => rowFlow || (colW > 0f ? colW : FallbackW) >= NarrowPadW ? HeroGap : NarrowHeroGap;

        /// <summary>The artwork edge, continuous in both flows and rounded to a whole DIP (decode bucket / cover key).</summary>
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

        /// <summary>The copy measure before either cap — the ONE number the body and title widths share.</summary>
        public static float CopyAvailFor(float colW, bool rowFlow)
        {
            float w = colW > 0f ? colW : FallbackW;
            float pad = HeroPadFor(w, rowFlow);
            return rowFlow
                ? w - 2f * pad - HeroGapFor(w, rowFlow) - ArtworkFor(w, rowFlow)
                : w - 2f * pad;
        }

        /// <summary>What attribution, meta and the description wrap to.</summary>
        public static float ContentWidthFor(float colW, bool rowFlow)
            => MathF.Min(ContentWMax, MathF.Max(ContentWMin, CopyAvailFor(colW, rowFlow)));

        /// <summary>The title's own measure (wider cap, same copy column).</summary>
        public static float TitleWidthFor(float colW, bool rowFlow)
            => MathF.Min(TitleWMax, MathF.Max(ContentWMin, CopyAvailFor(colW, rowFlow)));

        /// <summary>Row flow: two lines at the wide measure hold what three held in the column; stacked keeps four.</summary>
        public static int DescriptionMaxLines(bool rowFlow) => rowFlow ? 2 : 4;

        /// <summary>The description's widest reading measure in row flow (≈ 100 characters at 13 px). It spans under the
        /// cover AND the column, so the cap is on reading comfort, not on the column.</summary>
        public const float DescriptionWMax = 720f;
        /// <summary>Space between the hero row (or stacked column) and the description beneath it.</summary>
        public const float DescriptionGapRow = 12f;

        /// <summary>The description's measure: row flow spans the padded hero width (capped); stacked keeps the content
        /// width the identity column already uses.</summary>
        public static float DescriptionWidthFor(float colW, bool rowFlow)
            => rowFlow ? MathF.Min(DescriptionWMax, MathF.Max(ContentWMin, colW - 2f * HeroPadFor(colW, rowFlow)))
                       : ContentWidthFor(colW, rowFlow);

        /// <summary>The gap above the description: stacked keeps the identity rhythm it had inside the column.</summary>
        public static float DescriptionGapFor(bool rowFlow) => rowFlow ? DescriptionGapRow : IdentityGap;

        /// <summary>The description band's full height (gap + lines), 0 when there is none.</summary>
        public static float DescriptionBandHeight(bool rowFlow, bool description)
            => description ? DescriptionGapFor(rowFlow) + DescriptionMaxLines(rowFlow) * DescriptionLineHeight : 0f;

        /// <summary>The identity column's MinHeight: always 0, in both flows. Issue #78 once forced this to the
        /// artwork's edge in row flow so a short column's slack could be redistributed as growth of one of its own
        /// rows rather than left as dead space under the action row — but that only RELOCATED the same blank band
        /// to inside the column (between the metadata and the actions, or under a bare accent rule when nothing
        /// else was reserved), which is exactly the "large band of dead whitespace" a column with a tall cover and
        /// little text produces. The BAND already covers the artwork (<see cref="HeroBandHeight(float,bool,in TitleTypePlan,bool,bool,bool,bool,bool,bool)"/>'s
        /// <c>MathF.Max(art, identity)</c>): a taller cover simply runs on past a shorter column, top-aligned
        /// (<c>AlignItems.Start</c>) — nothing inside the column is ever stretched to fill space it has no content
        /// for. Kept as a named decision, not inlined, so a future author cannot silently reintroduce the forcing
        /// without breaking a test.</summary>
        public static float IdentityMinHeightFor(float colW, bool rowFlow) => 0f;

        // ── the hero BAND, as a height: ONE arithmetic with two consumers (the loading skeleton's reserved band and the
        //    loaded hero's pre-measure collapse binds) that must never disagree (D49). Nominal natural heights, in order.
        public const float EyebrowRowHeight = 16f;
        public const float AccentRuleRowHeight = 4f;       // the 20×2 rule + its 2-DIP top margin
        public const float AttributionRowHeight = 16f;
        public const float MetaRowHeight = 16f;
        /// <summary>The daylist flip-countdown digit row.</summary>
        public const float PulseRowHeight = 28f;
        /// <summary>The chart playlist's "N new entries · date" caption (W28): reserved like the pulse, one line.</summary>
        public const float ChartRowHeight = 16f;
        /// <summary>The editable title's hover-pill pencil: a 20-DIP box after an 8-DIP gap (#92).</summary>
        public const float EditableTitlePencilW = 20f, EditableTitlePencilGap = 8f;
        /// <summary>The editable title run's own width — the wrap width less the pencil slot, floored at 0 (#92).</summary>
        public static float EditableTitleMeasure(float wrapWidth) => MathF.Max(0f, wrapWidth - EditableTitlePencilW - EditableTitlePencilGap);
        public const float ActionRowHeight = 36f;          // Play button 32 + the row's 4-DIP top margin
        public const float DescriptionLineHeight = 18f;    // the 13px expandable blurb
        public const float IdentityGap = 4f;
        /// <summary>The BOX reserved for the list command bar (a 32 pill row padded 5 top and bottom).</summary>
        public const float ToolbarRowHeight = 44f;
        /// <summary>What the skeleton DRAWS inside the toolbar band.</summary>
        public const float ToolbarPillHeight = 32f;
        public const float ToolbarSurfacePadX = 6f;
        public const float ToolbarSurfacePadY = 5f;

        // ── the title TYPE PLAN ──
        // ONE engine fact drives it: a line box resolves to max(natural, LineHeight) (the text seam's MaxHeight stacking)
        // and Segoe UI Variable's natural box is 1.3301 em, so every height here derives from NaturalLineRatio and every
        // title clears its LineHeight to NaN — the line box drawn is always the natural one OF THE SIZE DRAWN. The plan is
        // ONE size per (bucketed width, title): a pure function, no size hysteresis, and NO engine auto-fit
        // (TextEl.MinSize) behind it — the engine draws exactly the plan's size, so the plan's lines, its block height and
        // the pixels describe the same text. A short title's empty band is a HEIGHT problem (the cover's own height is the
        // type's budget); a long one WRAPS — at the floor if it must — and only a pathological one, past TitleLineCap
        // lines, ends in an ellipsis (2026-09-30: "never trim a title unless it's REALLY long").

        /// <summary>The resolved plan: the ONE size drawn, its NATURAL line box, the lines it spends at that size, and
        /// the wrap width.</summary>
        public readonly record struct TitleTypePlan(float Size, float LineHeight, int Lines, float WrapWidth)
        {
            public float BlockHeight => Lines * LineHeight;
        }

        /// <summary>Segoe UI Variable's measured natural line box, in em.</summary>
        public const float NaturalLineRatio = 1.3301f;

        /// <summary>The natural line box of a size, whole DIP — what the engine resolves for a title's NaN LineHeight.</summary>
        public static float NaturalLineHeightFor(float size) => MathF.Round(size * NaturalLineRatio);

        public const float TitleSizeCap = 96f, TitleSizeFloor = 20f;
        /// <summary>The hero's size-search line budget: a line count only WINS when it buys a strictly larger size.</summary>
        public const int TitleLinesMax = 2;

        /// <summary>THE trim rule, and the ONLY one: a title is ellipsised only past this many lines AT THE SIZE DRAWN — a
        /// pathological-length guard, never a layout budget. Eight lines at the 20-DIP floor in the narrowest title
        /// measure any surface gives (the 180-DIP rail's 156-DIP cover, ≈ 13 characters a line) hold ≈ 100 characters of
        /// real words — a sentence or two, past any real album / playlist / show name.</summary>
        public const int TitleLineCap = 8;

        /// <summary>The fluid cap's two locks: 28 at a 360 column, 96 at 1100 (the old ladder's Title and top rungs).</summary>
        public const float CapLockMinW = 360f, CapLockMinSize = 28f, CapLockMaxW = 1100f, CapLockMaxSize = TitleSizeCap;

        /// <summary>The size CEILING at this width: a straight line between the locks, clamped flat outside them.</summary>
        public static float FluidTitleCapFor(float colW)
        {
            float w = colW > 0f ? colW : FallbackW;
            const float Slope = (CapLockMaxSize - CapLockMinSize) / (CapLockMaxW - CapLockMinW);
            const float Intercept = CapLockMinSize - Slope * CapLockMinW;
            return Math.Clamp(Slope * w + Intercept, CapLockMinSize, CapLockMaxSize);
        }

        /// <summary>The snap grid: 8 at ≥ 64, 4 at ≥ 32, else 2 — shared grid points are text-measure-cache hits.</summary>
        public static float TitleSnapStep(float size) => size >= 64f ? 8f : size >= 32f ? 4f : 2f;

        /// <summary>Clamp, round to the grid, re-derive the step from the ROUNDED value, clamp again.</summary>
        public static float SnapTitleSize(float size)
        {
            float s = Math.Clamp(size, TitleSizeFloor, TitleSizeCap);
            float snapped = MathF.Round(s / TitleSnapStep(s)) * TitleSnapStep(s);
            float step = TitleSnapStep(snapped);
            return Math.Clamp(MathF.Round(s / step) * step, TitleSizeFloor, TitleSizeCap);
        }

        /// <summary>How far below a grid point the size it was snapped FROM can sit: half the grid step just under it
        /// (<see cref="SnapTitleSize"/> rounds to the nearest point — 64 stands for [62, 68), 32 for [31, 34), the floor 20
        /// for [20, 21]).</summary>
        public static float SnapTolerance(float gridSize) => 0.5f * TitleSnapStep(gridSize - 0.01f);

        /// <summary>A cheap per-glyph advance estimate in em (not shaping: it sizes the plan and its pre-measure block;
        /// the painted hero's MEASURED height replaces the block once laid out, and nothing ever trims on it). The
        /// tracking matches the DetailHero face's −20/1000 em, once per inter-character gap.</summary>
        public static float TitleAdvanceEm(string? title, float trackingEm = -0.020f)
        {
            if (string.IsNullOrEmpty(title)) return 0f;
            float sum = 0f;
            for (int i = 0; i < title.Length; i++) sum += CharAdvanceEm(title[i]);
            return MathF.Max(0.001f, sum + trackingEm * (title.Length - 1));
        }

        /// <summary>The widest single WORD in em — the true one-line limit (a title cannot wrap mid-word).</summary>
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

        /// <summary>Six buckets standing in for shaping: hairline, narrow, mid-narrow, widest (M/W), wide caps, and the
        /// ~0.57 em average (CJK set to a full em).</summary>
        public static float CharAdvanceEm(char c) => c switch
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

        /// <summary>The identity column's CHROME height (every block but the title) and a block count that STARTS AT ONE
        /// (the title), so the gap count agrees between <see cref="TitleHeightBudgetFor"/> and
        /// <see cref="IdentityHeightFor"/>. Every hero block has its flag here — a new row needs a new flag (ch 03 §9).
        /// The description is no longer one of them: it left the column for the band beneath it
        /// (<see cref="DescriptionBandHeight"/>).</summary>
        public static (float Height, int Blocks) IdentityChrome(bool eyebrow, bool attribution, bool meta, bool pulse,
                                                               bool rowFlow, bool chart = false)
        {
            float h = 0f; int blocks = 1;
            if (eyebrow) { h += EyebrowRowHeight; blocks++; }
            h += AccentRuleRowHeight; blocks++;
            if (attribution) { h += AttributionRowHeight; blocks++; }
            if (meta) { h += MetaRowHeight; blocks++; }
            if (pulse) { h += PulseRowHeight; blocks++; }
            if (chart) { h += ChartRowHeight; blocks++; }
            h += ActionRowHeight; blocks++;
            return (h, blocks);
        }

        /// <summary>Row flow's title HEIGHT BUDGET: the cover edge less the other chrome and its gaps. The description
        /// lives in the band under the hero now, never in this column, so it never steers the title's size. Stacked ⇒ 0.</summary>
        public static float TitleHeightBudgetFor(float colW, bool rowFlow,
            bool eyebrow, bool attribution, bool meta, bool pulse = false, bool chart = false)
        {
            if (!rowFlow) return 0f;
            var (chrome, blocks) = IdentityChrome(eyebrow, attribution, meta, pulse, rowFlow: true, chart);
            return MathF.Max(0f, ArtworkFor(colW, rowFlow) - chrome - (blocks - 1) * IdentityGap);
        }

        /// <summary>One-line packing (1/1.08: spaces cost more than the average) and the packing of every further line
        /// (0.94: each line only fits its own words).</summary>
        public const float OneLinePacking = 1f / 1.08f, TwoLinePacking = 0.94f;

        /// <summary>The core: the largest size — and the fewest lines — that fits both budgets under the cap, searching up
        /// to <paramref name="preferredLines"/> lines. A line count only WINS when it buys a strictly larger size
        /// (<c>cand &gt; best + 0.5</c>). The size is snapped to the grid exactly as ever and is the size DRAWN (no auto-fit
        /// behind it); the plan's lines are the lines OF THAT SIZE (<see cref="TitleLinesAt"/>) — never more than the
        /// search budgeted for a size it solved, and, for a title no preferred line count holds above the floor, the
        /// floor size wrapped to as many lines as it takes, never trimmed short of <see cref="TitleLineCap"/>. Pure: the
        /// same inputs always give the same plan.</summary>
        public static TitleTypePlan TitleTypeFor(float titleW, float heightBudget, float sizeCap,
                                                 float advanceEm, float longestWordEm, int preferredLines = TitleLinesMax)
        {
            float avail = titleW > 0f ? titleW : ContentWMin;
            float adv   = advanceEm > 0.001f ? advanceEm : 0.001f;
            float word  = Math.Clamp(longestWordEm > 0.001f ? longestWordEm : adv, 0.001f, adv);
            float cap   = Math.Clamp(sizeCap, TitleSizeFloor, TitleSizeCap);
            int search  = Math.Clamp(preferredLines, 1, TitleLineCap);

            float best = TitleSizeFloor;
            for (int lines = 1; lines <= search; lines++)
            {
                float widthFit = MathF.Min((lines == 1 ? OneLinePacking : TwoLinePacking) * lines * avail / adv, avail / word);
                float heightFit = heightBudget > 0f ? heightBudget / (lines * NaturalLineRatio) : float.PositiveInfinity;
                float cand = MathF.Min(MathF.Min(widthFit, heightFit), cap);
                if (cand > best + 0.5f) best = cand;
            }

            float size = SnapTitleSize(best);
            return new TitleTypePlan(size, NaturalLineHeightFor(size), TitleLinesAt(size, avail, adv), avail);
        }

        /// <summary>The lines a title of <paramref name="advanceEm"/> spends at the GRID size <paramref name="size"/> in
        /// <paramref name="titleW"/> — the size search's own packing model read backwards (one line at
        /// <see cref="OneLinePacking"/>, n ≥ 2 at <see cref="TwoLinePacking"/> each) for the fit that grid point stands
        /// for (<see cref="SnapTolerance"/> below it, so a solved plan never claims more lines than the search gave it),
        /// clamped to [1, <see cref="TitleLineCap"/>] (the engine ellipsises at the cap, so the drawn block never exceeds
        /// it). Non-decreasing over the grid.</summary>
        public static int TitleLinesAt(float size, float titleW, float advanceEm)
        {
            const float Eps = 1e-4f;   // a fit sitting exactly on its n-line width must not round to n + 1 on float noise
            float avail = titleW > 0f ? titleW : ContentWMin;
            float fit = MathF.Max(0f, size - SnapTolerance(size));
            float need = fit * MathF.Max(0.001f, advanceEm) / avail;   // the run's length, in measures
            if (need <= OneLinePacking + Eps) return 1;
            return Math.Clamp((int)MathF.Ceiling(need / TwoLinePacking - Eps), 2, TitleLineCap);
        }

        /// <summary>The convenience form over a column width and the title string. <c>title: null</c> starves both width
        /// terms and yields the PESSIMISTIC plan (a one-line title at the fluid cap) the skeleton and the pre-measure
        /// fallback reserve when no title is known yet.</summary>
        public static TitleTypePlan TitleTypeFor(float colW, bool rowFlow, string? title,
            bool eyebrow, bool attribution, bool meta, bool pulse = false, bool chart = false)
            => TitleTypeFor(TitleWidthFor(colW, rowFlow),
                            TitleHeightBudgetFor(colW, rowFlow, eyebrow, attribution, meta, pulse, chart),
                            FluidTitleCapFor(colW),
                            TitleAdvanceEm(title), TitleLongestWordEm(title));

        /// <summary>The two-column RAIL's search budget: the rail held three lines before it would shrink the type.</summary>
        public const int RailTitleLinesPreferred = 3;

        /// <summary>The two-column RAIL's title plan — the SAME rule as the hero's: the window rung (40 tall / 28 short)
        /// is the size CAP, the cover edge the measure, and there is no height budget (the rail scrolls; a row that wraps
        /// grows below its reservation). The size steps down the snap grid only as far as it takes to hold the title in
        /// <see cref="RailTitleLinesPreferred"/> lines, never below <see cref="TitleSizeFloor"/>; past that the title
        /// wraps at the floor, in its natural line box, and is ellipsised only past <see cref="TitleLineCap"/>.</summary>
        public static TitleTypePlan RailTitleTypeFor(float coverW, float rungSize, string? title)
            => TitleTypeFor(coverW, heightBudget: 0f, rungSize, TitleAdvanceEm(title), TitleLongestWordEm(title),
                            RailTitleLinesPreferred);

        /// <summary>The identity column's height: chrome + this plan's block + one gap per block boundary. The
        /// description no longer contributes here — it is a sibling block under the whole hero row
        /// (<see cref="DescriptionBandHeight"/>), not a row inside this column.</summary>
        public static float IdentityHeightFor(in TitleTypePlan title, bool rowFlow,
            bool eyebrow, bool attribution, bool meta, bool pulse = false, bool chart = false)
        {
            var (chrome, blocks) = IdentityChrome(eyebrow, attribution, meta, pulse, rowFlow, chart);
            return chrome + title.BlockHeight + (blocks > 1 ? (blocks - 1) * IdentityGap : 0f);
        }

        /// <summary>The widest the identity gap may spread.</summary>
        public const float IdentityGapMax = 12f;

        /// <summary>Row flow spreads the slack a short column leaves under the cover evenly over every gap (even 2-DIP
        /// steps, ≤ <see cref="IdentityGapMax"/>); stacked keeps the resting gap.</summary>
        public static float IdentityGapFor(float colW, bool rowFlow, in TitleTypePlan title,
            bool eyebrow, bool attribution, bool meta, bool pulse = false, bool chart = false)
        {
            if (!rowFlow) return IdentityGap;
            var (chrome, blocks) = IdentityChrome(eyebrow, attribution, meta, pulse, rowFlow, chart);
            int gaps = Math.Max(1, blocks - 1);
            float slack = ArtworkFor(colW, rowFlow) - (chrome + title.BlockHeight + gaps * IdentityGap);
            if (slack <= 0f) return IdentityGap;
            return MathF.Min(IdentityGapMax, MathF.Round((IdentityGap + slack / gaps) / 2f) * 2f);
        }

        /// <summary>The whole expanded band: padded artwork/identity (row flow = the taller of the two, stacked = both
        /// over the gap), the description band beneath it when there is one, plus the toolbar row under that.</summary>
        public static float HeroBandHeight(float colW, bool rowFlow, in TitleTypePlan title,
            bool eyebrow, bool attribution, bool meta, bool description, bool pulse = false, bool chart = false)
        {
            float w = colW > 0f ? colW : FallbackW;
            float pad = HeroPadFor(w, rowFlow);
            float art = ArtworkFor(w, rowFlow);
            float identity = IdentityHeightFor(title, rowFlow, eyebrow, attribution, meta, pulse, chart);
            float hero = rowFlow ? MathF.Max(art, identity) : art + HeroGapFor(w, rowFlow) + identity;
            return pad + hero + DescriptionBandHeight(rowFlow, description) + HeroBottomPad
                 + ExpandedToolbarTopPad + ToolbarRowHeight + ExpandedToolbarBottomPad;
        }

        /// <summary>The pre-measure / skeleton form. With the page's KNOWN <paramref name="title"/> it reserves exactly the
        /// plan the loaded hero draws at this width (one pure rule, so the skeleton → hero swap does not jump); with none
        /// (null / empty) the PESSIMISTIC null-title plan — never an invented string.</summary>
        public static float HeroBandHeight(float colW, bool rowFlow,
            bool eyebrow, bool attribution, bool meta, bool description, bool pulse = false, bool chart = false,
            string? title = null)
            => HeroBandHeight(colW, rowFlow,
                TitleTypeFor(colW, rowFlow, title, eyebrow, attribution, meta, pulse, chart),
                eyebrow, attribution, meta, description, pulse, chart);

        /// <summary>Scroll distance over which the expanded hero becomes the 56-DIP band.</summary>
        public static float CollapseDistance(float expandedHeight)
            => MathF.Max(1f, expandedHeight - CompactIdentityHeight);

        /// <summary>The pinned list-chrome extent: the table's REAL column-header height (Modern 36 / Classic 32 —
        /// <c>Track.TableRules.HeaderHeightFor</c>), its divider, and the optional Liked filter rail.</summary>
        public static float ChromeExtent(float contentFilterExtent = 0f, float headerHeight = ChromeHeaderHeight)
            => headerHeight + ChromeDividerHeight + MathF.Max(0f, contentFilterExtent);

        /// <summary>The rows' clip inset under the band (56 + header + 1, + the filter rail).</summary>
        public static float StickyClipInset(float contentFilterExtent = 0f, float headerHeight = ChromeHeaderHeight)
            => CompactIdentityHeight + ChromeExtent(contentFilterExtent, headerHeight);

        /// <summary>The trailing shelves' own clip inset (the album "Also by" / release-panel band under the rows,
        /// Track.Table.cs's <c>TrailingBody</c>): the same line the hero collapses to, so a shelf cannot show through
        /// the compact band while still riding under it. Equal to <see cref="CompactIdentityHeight"/> by construction —
        /// named separately so a caller states what it means, not <see cref="StickyClipInset"/>'s.</summary>
        public static float TrailingClipInset => CompactIdentityHeight;

        // ── the vertical viewport's slot map: hero, pinned chrome, the recycled rows, then (hero system only) the facts
        //    FOOTER — a slot, not a block in the identity column, so the page opens on its songs, not on charts.
        public const int PrefixCount = 2;

        /// <summary>Row slots held open; an empty / loading list keeps ONE (the placeholder).</summary>
        public static int RowSlots(int visibleTracks) => Math.Max(1, visibleTracks);

        /// <summary>The facts footer: immediately after the last row slot.</summary>
        public static int FooterIndex(int visibleTracks) => PrefixCount + RowSlots(visibleTracks);

        /// <summary>The viewport's item total; the footer is ONE extra item.</summary>
        public static int ItemCount(int visibleTracks, bool hasFacts)
            => PrefixCount + RowSlots(visibleTracks) + (hasFacts ? 1 : 0);

        public static VerticalItemRole ItemRole(int itemIndex, int visibleTracks, bool hasFacts = false)
        {
            if (itemIndex == 0) return VerticalItemRole.Hero;
            if (itemIndex == 1) return VerticalItemRole.Chrome;
            int display = itemIndex - PrefixCount;
            if (display >= 0 && display < Math.Max(0, visibleTracks)) return VerticalItemRole.ExpandableTrack;
            if (hasFacts && itemIndex == FooterIndex(visibleTracks)) return VerticalItemRole.Footer;
            return VerticalItemRole.Empty;
        }

        /// <summary>The expanded hero stays readable until its final 96 DIP.</summary>
        public static float ExpandedFadeStart(float collapseDistance)
            => MathF.Max(0f, collapseDistance - ExpandedContentFadeDistance);

        /// <summary>The band's crossfade occupies only the last <see cref="CompactRevealBand"/> DIP of the collapse.</summary>
        public static float CompactRevealStart(float collapseDistance)
            => MathF.Max(0f, collapseDistance - CompactRevealBand);

        /// <summary>Decode bucket for the hero cover.</summary>
        public static int ArtworkDecodePx(float artworkSize)
            => artworkSize <= 128f ? 256 : artworkSize <= 288f ? 512 : 1024;

        /// <summary>Unmeasured always asks 256 — the bucket the preview/skeleton/grid already resolved — instead of
        /// chasing a guessed artwork size.</summary>
        public static int ArtworkDecodePx(float artworkSize, bool widthMeasured)
            => widthMeasured ? ArtworkDecodePx(artworkSize) : 256;

        /// <summary>The hero band the page tone measures against, floored before the hero is measured.</summary>
        public static float BackdropBandFor(float heroHeight)
            => MathF.Max(CompactIdentityHeight * 2f, heroHeight);
    }

    // ══ 3. THE RAIL POLICY ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Which surfaces get a rail grip at which mode, the bounds, and which persisted pair a scope reads.</summary>
    public static class RailPolicy
    {
        /// <summary>The WIDEST two-column arm (page ≥ 820). Its rail rests at the scope's own persisted width; modes 1/2
        /// rest at their breakpoint width instead — but all three RESIZE (issue: the grip vanished the moment a
        /// transcript/lyrics side rail pushed a detail page under 820, on every detail page).</summary>
        public const int WideMode = 0;

        /// <summary>The two narrow two-column arms' RESTING rail — <c>DetailShell.RailW</c>'s 224 / 188. A scope the user
        /// has never dragged opens here; a dragged one carries its persisted width into these modes too.</summary>
        public const float MidRestWidth = 224f, NarrowRestWidth = 188f;

        /// <summary>180, not 220: every rail's content survives it (cover floor, a title that wraps at its 20-DIP floor, wrapping CTAs).
        /// <see cref="MaxWidth"/> is the ABSOLUTE ceiling; the live one is <see cref="MaxWidthForPage"/>.</summary>
        public const float MinWidth = 180f, MaxWidth = 480f;

        /// <summary>The seam the row always pays between rail and content (<see cref="Splitter.StripW"/>), and the two
        /// widths the COLLAPSED arm composes instead (the 96-DIP identity strip + its 20-DIP re-open grip).</summary>
        public const float GripStripW = Splitter.StripW;
        public const float CompactStripW = 96f, CollapsedGripW = 20f;

        /// <summary>The live maximum moves in 8-DIP steps, so a per-pixel window resize cannot churn the frame's render
        /// (and the floor never rounds the cap UP past what the page can actually give).</summary>
        public const float MaxQuantum = 8f;

        /// <summary>Does this mode compose a rail at all? Modes 0/1/2 are the two-column arms; mode 3
        /// (<see cref="Breakpoints.VerticalMode"/>) is the single-column hero/vertical page — there is no rail, no grip
        /// and nothing to resize. Anything outside the ladder is treated as no rail.</summary>
        public static bool ComposesRail(int mode) => mode >= WideMode && mode < Breakpoints.VerticalMode;

        /// <summary>EVERY two-column arm resizes, not just the wide one. Only a single-column arm (and a config that opts
        /// out) has no grip.</summary>
        public static bool ResizableFor(bool railResizable, int mode) => railResizable && ComposesRail(mode);

        /// <summary>The widest rail a page THIS wide may carry: whatever is left after the grip strip and the content
        /// column's own floor (<see cref="Breakpoints.TwoColumnContentMinW"/>), never above <see cref="MaxWidth"/> and
        /// never below <see cref="MinWidth"/>. An unmeasured page (width ≤ 0) answers the absolute ceiling — the
        /// pre-measure seed, corrected by the first bounds callback. Mode 0 needs 820 DIP, and 480 + 16 + 300 = 796, so
        /// the wide arm's answer is always 480: this rule changes nothing there.</summary>
        public static float MaxWidthForPage(float pageWidth, int mode)
        {
            if (!ComposesRail(mode) || pageWidth <= 0f) return MaxWidth;
            float room = MathF.Min(pageWidth, Design.Size.PageMaxW) - GripStripW - Breakpoints.ContentMinWidthForMode(mode);
            room = MathF.Floor(room / MaxQuantum) * MaxQuantum;
            return Math.Clamp(room, MinWidth, MaxWidth);
        }

        /// <summary>The breakpoint rail modes 1/2 rest at when the scope has never been dragged.</summary>
        public static float NarrowRestWidthFor(int mode) => mode == 1 ? MidRestWidth : NarrowRestWidth;

        /// <summary>Has the user ever moved this scope's rail? The same test Settings' "Clear all remembered sizes"
        /// uses (<see cref="HasCustomizedRailPrefs"/>): a stored width that is no longer the authored default.</summary>
        public static bool HasDraggedWidth(float storedWidth, RailScope scope)
            => ClampStored(storedWidth, scope) != DefaultWidthFor(scope);

        /// <summary>A LIVE width clamped to the page-aware bounds. Applied on read as well as on every width change, so a
        /// remembered width wider than this page can give is merely held back, never lost.</summary>
        public static float ClampLive(float width, RailScope scope, float maxWidth)
        {
            float min = MinWidthFor(scope);
            return Math.Clamp(width, min, MathF.Max(maxWidth, min));
        }

        /// <summary>THE resting width: what the live rail shows for a (scope, mode, maximum), derived from the REMEMBERED
        /// width alone. Wide arm — the persisted width, as ever. Narrow arms — the breakpoint rail until the user has
        /// dragged this scope, then their own width. Always clamped through <see cref="ClampLive"/>, and the clamp is
        /// never written back to the store, so widening the window restores the remembered width.</summary>
        public static float RestingWidth(float storedWidth, RailScope scope, int mode, float maxWidth)
        {
            float stored = ClampStored(storedWidth, scope);
            float want = mode == WideMode || HasDraggedWidth(storedWidth, scope) ? stored : NarrowRestWidthFor(mode);
            return ClampLive(want, scope, maxWidth);
        }

        /// <summary>The width a rail COMPOSES in a render. The live width is re-rested (<see cref="RestingWidth"/>) from an
        /// EFFECT, i.e. only after the render that first sees a new (mode, maximum) — the pre-measure seed mode, the first
        /// page measure, a breakpoint cross — so composing the live width there paints the rail at the PREVIOUS arm's
        /// width for a frame or two and then snaps it narrower, the title re-wrapping underneath (2026-09-30: "the full
        /// title shows for 1 or 2 frames"). Until the rail has rested for exactly (<paramref name="mode"/>,
        /// <paramref name="maxWidth"/>) this answers what that rest WILL write; once it has, the live (possibly dragged)
        /// width, clamped on read as ever.</summary>
        public static float ComposedWidth(float liveWidth, float storedWidth, RailScope scope,
                                          int restedMode, float restedMax, int mode, float maxWidth)
            => restedMode == mode && restedMax == maxWidth
                ? ClampLive(liveWidth, scope, maxWidth)
                : RestingWidth(storedWidth, scope, mode, maxWidth);

        /// <summary>What a drag RELEASE persists. A drag that merely parked against a page-imposed cap keeps the wider
        /// remembered width (the cap is the page's verdict, not the user's); any other release is the user's own number.</summary>
        public static float CommitWidth(float draggedWidth, float storedWidth, float maxWidth)
            => draggedWidth >= maxWidth && storedWidth > maxWidth ? storedWidth : draggedWidth;

        /// <summary>What the right column is left with — the guard every arm must clear
        /// (<see cref="Breakpoints.ContentMinWidthForMode"/>).</summary>
        public static float ContentWidthFor(float pageWidth, float railWidth, float gripWidth)
            => MathF.Min(pageWidth, Design.Size.PageMaxW) - railWidth - gripWidth;

        /// <summary>The COLLAPSED arm's content width: the 96-DIP identity strip plus its 20-DIP re-open grip. 96 + 20 +
        /// 300 = 416, well under the narrowest two-column page (540), so collapsing can never squeeze the content out.</summary>
        public static float CollapsedContentWidthFor(float pageWidth)
            => ContentWidthFor(pageWidth, CompactStripW, CollapsedGripW);

        /// <summary>Album-like (album, show — and so an episode) 280; list-like (playlist, Liked) and Uniform 240.</summary>
        public static float DefaultWidthFor(RailScope scope) => scope switch
        {
            RailScope.Album or RailScope.Show => Design.Size.RailAlbum,
            _ => Design.Size.RailPlaylist,
        };

        /// <summary>The grip floor per scope (one value today; the seam for a surface that cannot take 180).</summary>
        public static float MinWidthFor(RailScope scope) => MinWidth;

        /// <summary>A STORED width clamped to the live bounds before it may seed the layout.</summary>
        public static float ClampStored(float stored, RailScope scope)
            => Math.Clamp(stored, MinWidthFor(scope), MaxWidth);

        /// <summary>The requested scope, or <see cref="RailScope.Uniform"/> when "Keep left-rail same size" is on. The
        /// Episode arm is its config's: <see cref="Config.Episode"/> requests <see cref="RailScope.Show"/>.</summary>
        public static RailScope ScopeFor(RailScope requested, bool uniform) => uniform ? RailScope.Uniform : requested;

        /// <summary>THE persisted pair a scope reads and writes — the ten <c>Platform.Keys.Detail*Rail*</c> keys, one
        /// pair per scope, never another scope's by fallthrough (G-190). An episode page reads the SHOW pair (its scope is
        /// <see cref="RailScope.Show"/>): the podcast family persists one rail, not two.</summary>
        public static (SettingKey<float> Width, SettingKey<bool> Collapsed) KeysFor(RailScope scope) => scope switch
        {
            RailScope.Album => (Platform.Keys.DetailAlbumRailWidth, Platform.Keys.DetailAlbumRailCollapsed),
            RailScope.Playlist => (Platform.Keys.DetailPlaylistRailWidth, Platform.Keys.DetailPlaylistRailCollapsed),
            RailScope.Liked => (Platform.Keys.DetailLikedRailWidth, Platform.Keys.DetailLikedRailCollapsed),
            RailScope.Show => (Platform.Keys.DetailShowRailWidth, Platform.Keys.DetailShowRailCollapsed),
            _ => (Platform.Keys.DetailUniformRailWidth, Platform.Keys.DetailUniformRailCollapsed),
        };

        /// <summary>Has any of the four per-scope pairs moved from its authored default? The enable gate for Settings'
        /// "Clear all remembered sizes" (the Uniform pair is excluded on purpose).</summary>
        public static bool HasCustomizedRailPrefs(
            float albumWidth, bool albumCollapsed,
            float playlistWidth, bool playlistCollapsed,
            float likedWidth, bool likedCollapsed,
            float showWidth, bool showCollapsed)
            => albumWidth != DefaultWidthFor(RailScope.Album) || albumCollapsed
            || playlistWidth != DefaultWidthFor(RailScope.Playlist) || playlistCollapsed
            || likedWidth != DefaultWidthFor(RailScope.Liked) || likedCollapsed
            || showWidth != DefaultWidthFor(RailScope.Show) || showCollapsed;
    }

    // ══ 3b. THE INSIGHTS SHEET ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>WHERE the facts bento lives, per arm, and how wide the vertical arm's sheet is.
    /// <para>The two-column arms are unchanged: the bento is a rail row, on screen with the list beside it. The
    /// VERTICAL arm has no rail, and the bento used to fall to the bottom of the page as a footer nobody scrolled to —
    /// so there it becomes a dismissable sheet laid OVER the content, reached only through the toolbar toggle. The two
    /// hosts are mutually exclusive by construction (<see cref="RailHostsFacts"/> / <see cref="SheetHostsFacts"/> read
    /// the same mode ladder from opposite ends), and there is no third, inline host any more.</para></summary>
    public static class InsightsSheet
    {
        /// <summary>The sheet's preferred width — one step wider than the rail's own comfortable measure.</summary>
        public const float PreferredWidth = 390f;

        /// <summary>…or this share of the page when the page is narrower, so a strip of the list stays visible behind
        /// the sheet and it never reads as a full-screen route change.</summary>
        public const float PageFraction = 0.76f;

        /// <summary>The resolved sheet width for a page this wide. An UNMEASURED page (width ≤ 0) answers the preferred
        /// width — the pre-measure seed, corrected by the first bounds callback, exactly like the rail's ceiling.
        /// Quantized to whole DIP so a per-pixel window resize cannot churn the pane's layout.</summary>
        public static float WidthFor(float pageWidth)
            // `!(w > 0)` and not `w <= 0`: a NaN width is an unmeasured page too (the pre-measure seed), never a NaN
            // pane that would take the layout with it.
            => !(pageWidth > 0f)
                ? PreferredWidth
                : MathF.Max(1f, MathF.Min(PreferredWidth, MathF.Floor(pageWidth * PageFraction)));

        /// <summary>Only the two list-like surfaces carry facts at all (<c>User.FactsHas</c> answers false for any
        /// other kind — this is the same gate, one step earlier).</summary>
        public static bool KindHasFacts(DetailKind kind) => kind is DetailKind.Liked or DetailKind.Playlist;

        /// <summary>The two-column arms: the bento is a rail row, as it has always been. Nothing about them changes.</summary>
        public static bool RailHostsFacts(int mode, DetailKind kind, bool factsSlot)
            => factsSlot && KindHasFacts(kind) && RailPolicy.ComposesRail(mode);

        /// <summary>The vertical arm: the bento is reachable ONLY through the sheet.</summary>
        public static bool SheetHostsFacts(int mode, DetailKind kind, bool factsSlot)
            => factsSlot && KindHasFacts(kind) && mode == Breakpoints.VerticalMode;

        /// <summary>The bento is never appended to the page body again — every mode is answered by exactly one host,
        /// and a mode that composes neither simply has no facts to show.</summary>
        public static bool AppendsFactsToPageBody(int mode, DetailKind kind, bool factsSlot) => false;

        /// <summary>Is the toggle composed at all? The vertical arm only, on a facts-bearing page whose facts have
        /// actually arrived, and only on a TRACK page (an episode list's vertical arm has no bento).
        /// <para>ONE predicate, THREE readers: the hero's toolbar button, the pinned band's word, and the sheet itself.
        /// The band does not re-derive it — the frame threads the answer down as the presence of the toggle object
        /// (<c>VerticalSpec.Insights</c>), so the two entry points cannot disagree about whether they exist.</para></summary>
        public static bool ShowsToggle(int mode, DetailKind kind, bool factsSlot, DetailContent content)
            => content == DetailContent.Tracks && SheetHostsFacts(mode, kind, factsSlot);

        // ── the two entry points (Detail.Insights.cs §6/§7) ──

        /// <summary>Has this page DECIDED it has no facts, or has it merely not answered yet? The page publishes its
        /// bento as a SLOT, and that slot is derived from a scan of the live row source — which reads EMPTY while the
        /// list's open is holding its reveal, and can therefore say "no facts" about a page that plainly has them. So
        /// the frame LATCHES the answer for the route: once the facts have been offered they are settled, and a later
        /// null is the list re-folding, not the page losing its bento.
        /// <para>The latch is one-way and route-scoped (the frame drops it with the route, like the open state —
        /// <see cref="SurvivesRouteChange"/>), which is exactly the asymmetry the surface wants: a page that has never
        /// offered facts shows nothing and hints at nothing, while a page whose facts arrive LATE grows the toggle when
        /// they land instead of carrying a button that flickers with every re-fold.</para></summary>
        public static bool FactsSettled(bool everSeen, bool slotNow) => everSeen || slotNow;

        /// <summary>Which entry point owns INPUT at this scroll position. The pinned band takes hits only once its
        /// chrome is stuck; the collapsing hero's presentation stops taking them at the same edge. Exactly one of the
        /// two answers true for any <paramref name="bandStuck"/> — which is why BOTH are composed: neither alone covers
        /// the whole scroll range, the pair does, and there is no position at which both are live.</summary>
        public static bool BandToggleTakesInput(bool bandStuck) => bandStuck;

        /// <inheritdoc cref="BandToggleTakesInput"/>
        public static bool HeroToggleTakesInput(bool bandStuck) => !bandStuck;

        /// <summary>The band action cluster's width claim — Find · Filter · Play, and Insights between the view verbs
        /// and the terminal primary when this arm hosts the sheet. The compact search field derives its own width by
        /// subtracting exactly this, so the toggle joining the cluster NARROWS the field rather than pushing the words
        /// past the band's right edge.</summary>
        public static float BandActionsWidth(float find, float filter, float play, float insights, bool showsToggle)
        {
            if (!showsToggle)
            {
                Span<float> three = [find, filter, play];
                return BandLayout.ActionsWidth(three);
            }
            Span<float> four = [find, filter, insights, play];
            return BandLayout.ActionsWidth(four);
        }

        /// <summary>The LIVE open state: a sheet whose toggle has gone (the window widened back into a two-column arm,
        /// or the facts went away) is CLOSED, never merely hidden — so re-entering the vertical arm never restores a
        /// sheet the user cannot remember leaving open.</summary>
        public static bool OpenFor(bool wanted, int mode, DetailKind kind, bool factsSlot, DetailContent content)
            => wanted && ShowsToggle(mode, kind, factsSlot, content);

        /// <summary>A route change closes the sheet outright: it never survives navigation (the frame is keyed by
        /// subject, but a same-subject route swap reuses the host, so the host closes it itself).</summary>
        public const bool SurvivesRouteChange = false;
    }

    // ══ 4. THE HEADER MERGE ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The initial-load merge for a ROLLING-IDENTITY container (a daylist): its resident row can be the
    /// previous edition while the nav preview is fresh, so the preview's title/cover win — for rolling containers only
    /// (a non-rolling preview is just as likely to be the stale side).</summary>
    public static class HeaderMerge
    {
        /// <summary>Either side carrying a rollover window is enough.</summary>
        public static bool IsRollingIdentity(long loadedExpiresAtMs, long previewExpiresAtMs)
            => loadedExpiresAtMs > 0 || previewExpiresAtMs > 0;

        public static string ResolveTitle(bool rollingIdentity, string loadedTitle, string? previewTitle)
            => rollingIdentity && !string.IsNullOrEmpty(previewTitle) ? previewTitle : loadedTitle;

        /// <summary>The cover <see cref="CoverLatch.PreferVisible"/> treats as incoming. Without it a rolled-over
        /// container's stale cover reads as DIFFERENT art from the correct preview cover and wins. A blank preview url
        /// is no cover (0.2.9 tested the Image record for null).</summary>
        public static string? ResolveIncomingCover(bool rollingIdentity, string? loadedCoverUrl, string? previewCoverUrl)
            => rollingIdentity && !string.IsNullOrEmpty(previewCoverUrl) ? previewCoverUrl : loadedCoverUrl;
    }

    // ══ 5. THE NOTICE LADDER ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Why the page shows a notice strip. A notice never un-renders the page: the rows stay, the edit
    /// affordances go, one sentence says what happened.</summary>
    public static class NoticeRules
    {
        /// <summary>The notice for the next model. <paramref name="capabilitiesKnown"/> false (a thin header) can neither
        /// accuse nor acquit; an owner is never revoked from their own list; a pending create's absence is expected;
        /// <see cref="DetailNotice.CreateFailed"/> is sticky.</summary>
        public static DetailNotice Next(DetailNotice prev, bool freshIsNull, bool headerDeleted, bool capabilitiesKnown,
                                       bool canView, bool isOwner, bool isCreatePending)
        {
            if (prev == DetailNotice.CreateFailed) return DetailNotice.CreateFailed;
            if (isCreatePending) return prev == DetailNotice.Deleted ? DetailNotice.None : prev;
            if (headerDeleted || freshIsNull) return DetailNotice.Deleted;
            if (!capabilitiesKnown) return prev == DetailNotice.Deleted ? DetailNotice.None : prev;
            if (!canView && !isOwner) return DetailNotice.AccessRevoked;
            return DetailNotice.None;
        }

        /// <summary>A page opened COLD: no previous state and no create in flight, so the header alone decides.</summary>
        public static DetailNotice Cold(bool headerDeleted, bool capabilitiesKnown, bool canView, bool isOwner)
            => Next(DetailNotice.None, freshIsNull: false, headerDeleted, capabilitiesKnown, canView, isOwner, isCreatePending: false);

        /// <summary>The ALBUM verdict: <see cref="DetailNotice.MinifiedAlbum"/> while any row is <see cref="Unnamed"/>.
        /// Stateless (it clears itself when the repair lands); an EMPTY tracklist is still loading, not minified.</summary>
        public static DetailNotice ForAlbum(ReadOnlySpan<Track> tracks)
        {
            for (int i = 0; i < tracks.Length; i++)
                if (Unnamed(tracks[i])) return DetailNotice.MinifiedAlbum;
            return DetailNotice.None;
        }

        /// <summary>0.2.9's <c>HydrationLevels.TrackUnnamed</c> mapped to <c>Knows(...)</c>: "blank or uri-placeholder
        /// title" is <c>!Knows(TrackFields.Title)</c> (a gid-only disc row has no Identity group), and "artist refs with
        /// no names" is an <c>Edges.TrackArtists</c> target that <c>!Knows(ArtistFields.Name)</c>. A known title with a
        /// zero duration is NOT thin. A relinked row is judged by the row it DISPLAYS (<c>Track.ForDisplay</c>): its
        /// canonical track's title and credits are what the page paints.</summary>
        public static bool Unnamed(Track t)
        {
            Track d = t.ForDisplay;
            if (!d.Knows(TrackFields.Title)) return true;
            var artists = d.ArtistSlots;
            for (int i = 0; i < artists.Length; i++)
                if (!new Artist(artists[i]).Knows(ArtistFields.Name)) return true;
            return false;
        }
    }

    // ══ 6. THE CONTEXT BAND ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The ONE sticky page header's geometry (the text-chrome band: title · pivot · actions, no fill) and its
    /// scroll spy. Shared with the artist page. The title and actions never drop; the pivot is the elastic lane.</summary>
    public static class BandLayout
    {
        /// <summary>The identity row — the detail collapse floor by construction.</summary>
        public const float Height = VerticalLayout.CompactIdentityHeight;
        /// <summary>ONE hairline, under the whole stuck surface.</summary>
        public const float HairlineHeight = 1f;
        /// <summary>The artist arm's clip (the band alone). The detail arm clips at <see cref="VerticalLayout.StickyClipInset"/>.</summary>
        public const float ClipInset = Height;
        /// <summary>The clip edge's feather — the same band the detail item clip uses.</summary>
        public const float ClipFadeBand = VerticalLayout.StickyFadeBand;
        public const float ClusterGap = 24f;
        public const float PivotGap = 16f;
        public const float ActionGap = 16f;
        /// <summary>Inside one pivot item's hit box — also the inset the active underline is drawn within.</summary>
        public const float PivotPadX = 8f;
        /// <summary>Inside one text action's hit box (no underline to widen its target, so larger).</summary>
        public const float ActionPadX = 10f;
        public const float UnderlineHeight = 2f;
        public const float UnderlineGap = 4f;
        /// <summary>The widest slot the title claims; the surplus goes to the pivot.</summary>
        public const float TitleCap = 280f;

        // ── ONE BASELINE ──
        // The title, the section tabs and the trailing text actions share ONE 20-DIP line box, vertically centred in the
        // same 32-DIP item slot, so their baselines are the same line. The pivot used to be a text-plus-underline COLUMN
        // centred in 32, which put its word 3 DIP above the title's; the underline is an overlay now, at a fixed y.

        /// <summary>THE line box of every text in a band (<c>Controls.TextActionLineHeight</c>, 20).</summary>
        public const float TextLine = Controls.TextActionLineHeight;
        /// <summary>Every group's slot (title, tabs, actions): the band less its 12-DIP air above and below (32).</summary>
        public const float ItemHeight = Height - 2f * FluentGpu.Dsl.Spacing.M;
        /// <summary>The top of a <see cref="TextLine"/> centred in a box of <paramref name="box"/> DIP.</summary>
        public static float TextTopIn(float box) => (box - TextLine) * 0.5f;
        /// <summary>The active mark's top inside an item slot: under the text line plus <see cref="UnderlineGap"/> (30), so it sits
        /// on the slot's bottom edge (<see cref="ItemHeight"/> − <see cref="UnderlineHeight"/>).</summary>
        public const float UnderlineY = (ItemHeight - TextLine) * 0.5f + TextLine + UnderlineGap;
        /// <summary>The 1-DIP divider between the title and the tabs.</summary>
        public const float DividerH = 16f;
        /// <summary>The air on each side of the divider.</summary>
        public const float DividerGap = ClusterGap * 0.5f;

        // ── THE FLOOR ──
        // Under Zune an entity page (artist, profile, episode, show) publishes its band into the Zune band's second row, so its
        // in-page band is not composed and its sticky floor is 0 instead of the band's 56. The hero collapses to that floor
        // itself (its Collapse floor is the same number), so no hero remnant stays pinned at the card top.

        /// <summary>The WANTED placement: the band lives in the Zune band's row 2 while Zune is the PRESENTED style (the page
        /// reads <c>Shell.Ui.PresentedNavStyle</c>, never the live one, so the change lands in the quiet commit).</summary>
        public static bool InRow2(ShellNavStyle presented) => presented == ShellNavStyle.Zune;

        /// <summary>The stuck height: the band's 56 in the page, 0 once the band lives in row 2.</summary>
        public static float StuckHeight(bool inRow2) => inRow2 ? 0f : Height;

        /// <summary>The clip a stuck page content is cut at: exactly the stuck height.</summary>
        public static float ClipInsetFor(bool inRow2) => StuckHeight(inRow2);

        /// <summary>The scroll offset below which a 56-to-0 floor flip is INVISIBLE. THE PROOF. A hero of laid-out height H
        /// collapses LINEARLY: the engine (ScrollEffect.Collapse) runs the presented height from H to its floor over
        /// <c>over = H − floor</c> scroll DIP, and Collapse, Parallax, Fade, Reveal and Sticky are paint channels (PresentedH,
        /// ClipBottom, TransY, opacity), not layout. Collapse: with floor 56 the presented height at offset o is
        /// <c>H − o·(H−56)/(H−56) = H − o</c>; with floor 0 it is <c>H − o·H/H = H − o</c>, and the parallax slope is −1 in both.
        /// Sticky: a <c>Sticky(floor)</c> element right below the hero sits at H − o &gt; 56 there, so it is engaged in NEITHER
        /// pairing, and the clip it carries is not engaged either. Fade and Reveal are NOT floor-free: each runs over the hero's
        /// own collapse distance, so the expanded copy fades over <c>[ExpandedFadeStart(H−floor), H−floor]</c> and the band
        /// reveals from <c>CompactRevealStart(H−floor)</c>. At floor 56 the fade starts at H − 56 − 96 and the reveal at
        /// H − 56 − 44; at floor 0 they start 56 DIP later. Both stay at 0 (copy fully opaque, band unrevealed or absent) for every
        /// offset below the EARLIER of the two starts at floor 56, which is therefore the flip line (o &lt; <see cref="FlipLine"/>):
        /// every channel is identical there and the flip cannot be seen. Above it the flip would pop the copy and the band and snap
        /// the stuck header and clip by 56 DIP, so the floor HOLDS (latches) until the scroll next crosses back under the line.</summary>
        public static float FlipLine(float heroH)
        {
            float collapseAt56 = heroH - Height;
            return MathF.Min(VerticalLayout.ExpandedFadeStart(collapseAt56), VerticalLayout.CompactRevealStart(collapseAt56));
        }

        /// <summary>The floor placement to use now. Below <see cref="FlipLine"/> the WANTED placement is adopted (the flip is
        /// invisible); at or above it the LATCHED one holds, so a nav-style switch while scrolled never snaps a stuck element.</summary>
        public static bool FloorLatch(bool latched, bool wanted, double offset, float heroH)
            => offset < FlipLine(heroH) ? wanted : latched;

        /// <summary>Average advance at the band's 14/600 rung, deliberately generous so a localized label reserves.</summary>
        public const float AvgCharW = 7.6f;

        public static float EstimateLabelWidth(int labelLength, float padX)
            => MathF.Max(0f, labelLength) * AvgCharW + 2f * padX;

        public static float EstimateLabelWidth(string? label, float padX)
            => EstimateLabelWidth(label?.Length ?? 0, padX);

        /// <summary>A text-action cluster's claim: every action plus the gaps between.</summary>
        public static float ActionsWidth(ReadOnlySpan<float> actionWidths)
        {
            if (actionWidths.Length == 0) return 0f;
            float w = 0f;
            for (int i = 0; i < actionWidths.Length; i++) w += MathF.Max(0f, actionWidths[i]);
            return w + (actionWidths.Length - 1) * ActionGap;
        }

        // ── the scroll spy ──
        /// <summary>Boundary tolerance past the line: below the smallest scroll step, above layout noise.</summary>
        public const float SpyProbe = 8f;
        /// <summary>A section becomes current at the upper quarter of the usable viewport below the band.</summary>
        public const float SpyViewportFraction = 0.25f;
        public const float EndProbe = SpyProbe;
        /// <summary>The spy's "no pivot section is here" ANSWER — the page is above its first section (the hero, the
        /// chart band), so no underline is drawn. Distinct from −1, which is "no answer, hold what you had" (D40).</summary>
        public const int NoSection = -2;

        /// <summary>Which pivot link draws the active underline for a spy answer <paramref name="active"/> over
        /// <paramref name="shown"/> links (−1 = none). Pure; the pivot's render and its bound underline fills both read it,
        /// so the two can never disagree.</summary>
        public static int PivotCurrent(int active, int shown) => shown > 0 && active >= 0 ? Math.Min(active, shown - 1) : -1;

        /// <summary>A genuinely scrollable viewport at its real lower limit (a non-scrollable page is never "at end").</summary>
        public static bool IsAtScrollEnd(float offsetY, float viewportHeight, float contentHeight)
        {
            if (!float.IsFinite(offsetY) || !float.IsFinite(viewportHeight) || !float.IsFinite(contentHeight)
                || viewportHeight <= 0f || contentHeight <= viewportHeight) return false;
            float maxOffset = contentHeight - viewportHeight;
            return offsetY > 0.5f && offsetY >= maxOffset - EndProbe;
        }

        /// <summary>The viewport-relative line an incoming section crosses to become active.</summary>
        public static float SpyLine(float bandBottom, float viewportHeight)
        {
            float band = MathF.Max(0f, bandBottom);
            float usable = MathF.Max(0f, viewportHeight - band);
            return band + usable * SpyViewportFraction + SpyProbe;
        }

        /// <summary>Which pivot item is "here": the last section whose top has crossed the activation line; at the scroll
        /// end the last contiguous measured section; a NaN top stops the scan. <see cref="NoSection"/> when the FIRST
        /// section has not crossed the line yet — the hero or the Top tracks band is what is on screen, and no pivot word
        /// is lit (RCA 2026-09-25 E: this used to answer 0, lighting "Singles &amp; EPs" over Top tracks). −1 = NO ANSWER
        /// (empty pivot, or not even the first section measured) — the caller holds what it had (D40).</summary>
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
            int active = NoSection;
            for (int i = 0; i < viewportRelativeTops.Length; i++)
            {
                float top = viewportRelativeTops[i];
                if (float.IsNaN(top)) break;
                if (top <= line) active = i; else break;
            }
            return active;
        }

        /// <summary>The content offset that parks a section's top just under the band.</summary>
        public static float ScrollTargetFor(float currentOffset, float viewportRelativeTop, float bandBottom)
            => MathF.Max(0f, currentOffset + viewportRelativeTop - bandBottom);
    }

    // ══ 7. THE COVER LATCH ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"The cover never flashes" (ch 03 §0.2): the same ARTWORK at another CDN size keeps the rendition already
    /// on screen. Over url strings; identity is <see cref="Palette.ArtIdentityOf"/>, never the url.</summary>
    public static class CoverLatch
    {
        const string ImageUriPrefix = "spotify:image:", CdnPrefix = "https://i.scdn.co/image/";
        const int ImageIdLength = 40;

        /// <summary>Trim; a <c>spotify:image:</c> token becomes its CDN url.</summary>
        public static string? Normalize(string? url)
        {
            if (url is null) return null;
            var trimmed = url.Trim();
            if (trimmed.StartsWith(ImageUriPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var id = trimmed[ImageUriPrefix.Length..].Trim();
                return id.Length == 0 ? "" : CdnPrefix + id;
            }
            return trimmed;
        }

        /// <summary>A resolvable url: non-blank and not an unresolved <c>spotify:image:</c> provider token.</summary>
        public static bool IsUsable(string? url)
            => !string.IsNullOrWhiteSpace(url) && !url.Trim().StartsWith(ImageUriPrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>The identity url of a cover: its own url, else its mosaic's LEAD tile — the reduction the art surface
        /// applies before painting a 1-3-tile mosaic as a single cover.</summary>
        public static string? Reduce(string? coverUrl, string? leadTile)
            => !string.IsNullOrWhiteSpace(coverUrl) ? coverUrl : leadTile;

        /// <summary>Same ARTWORK: the same normalized url, or two 40-char Spotify ids differing only in their size prefix.
        /// Null/blank is never the same art.</summary>
        public static bool SameArt(string? a, string? b)
        {
            var ua = Normalize(a) ?? "";
            var ub = Normalize(b) ?? "";
            if (ua.Length == 0 || ub.Length == 0) return false;
            if (string.Equals(ua, ub, StringComparison.OrdinalIgnoreCase)) return true;
            var ia = Palette.ImageIdOf(ua);
            var ib = Palette.ImageIdOf(ub);
            if (ia.Length != ImageIdLength || ib.Length != ImageIdLength) return false;
            return Palette.ArtIdentityOf(ia).Equals(Palette.ArtIdentityOf(ib), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>SAME ART ⇒ keep the sharper 40-char id (a later 640 must replace a first-paint 64; a later 64
        /// must never replace a visible 640); identical ids keep <paramref name="visible"/> (no re-decode, no re-fade).
        /// DIFFERENT ART ⇒ <paramref name="incoming"/>. Only one side usable ⇒ that side. Pure and order-safe.
        /// Size prefixes are the ones documented on <see cref="Palette"/>:
        /// <c>0000b273</c> 640 &gt; <c>00001e02</c> 300 &gt; <c>00004851</c> 64.</summary>
        public static string? PreferVisible(string? incoming, string? visible)
        {
            bool inOk = IsUsable(incoming);
            bool visOk = IsUsable(visible);
            if (!inOk) return visOk ? visible : incoming ?? visible;
            if (!visOk) return incoming;
            if (!SameArt(incoming, visible)) return incoming;

            var inId = Palette.ImageIdOf((Normalize(incoming) ?? "").AsSpan());
            var visId = Palette.ImageIdOf((Normalize(visible) ?? "").AsSpan());
            if (inId.Length != ImageIdLength || visId.Length != ImageIdLength)
                return inId.Equals(visId, StringComparison.OrdinalIgnoreCase) ? visible : incoming;
            if (inId.Equals(visId, StringComparison.OrdinalIgnoreCase)) return visible;
            int inRank = RenditionRank(inId), visRank = RenditionRank(visId);
            if (visRank > inRank) return visible;
            return incoming;
        }

        /// <summary>THE COMMIT-SIDE COVER RULE, shared by every kind's <c>CommitX</c> arm (playlists, shows, albums,
        /// artists, episodes, tracks). May <paramref name="incoming"/> be written over <paramref name="visible"/>?
        /// <list type="bullet">
        /// <item>nothing there yet ⇒ YES, always: a first paint is never a downgrade.</item>
        /// <item>nothing incoming ⇒ NO: an answer that carries no cover never blanks one a fuller read already set.
        /// Nothing legitimately removes a cover (S2).</item>
        /// <item>otherwise <see cref="PreferVisible"/> decides, which is the SAME comparison the page-level latch
        /// makes: different art replaces; the same art keeps whichever id names the sharper rendition.</item>
        /// </list>
        /// <para>Without this a second answer carrying a THINNER rendition of the art already on screen — a 64 from a
        /// card or a list row landing after a 640 the header asked for — overwrote it, and the cover went soft with no
        /// state change to explain it. The authority ladder cannot catch that: both answers are legitimate and often
        /// carry the SAME authority; the difference is in the pixels, which only the image id knows.</para></summary>
        public static bool AcceptsImage(StringId visible, StringId incoming)
        {
            if (visible.IsEmpty) return true;
            if (incoming.IsEmpty) return false;
            string? incUrl = Controls.ArtUrl(incoming);
            string? visUrl = Controls.ArtUrl(visible);
            return PreferVisible(incUrl, visUrl) == incUrl;
        }

        /// <summary>The PIXEL EDGE a 40-char Spotify image id's size marker names (the last 8 hex of the 16-char prefix) —
        /// albums 640/300/64, artist portraits 640/320/160, user avatars 300/64 (profile pages research §5). Only the
        /// ORDER matters (<see cref="PreferVisible"/> compares two ranks); pixels make the families comparable. Unknown
        /// prefixes rank 0 — a later unknown id still replaces a first paint (upgrade-optimistic) unless the visible side
        /// is a known-larger prefix.</summary>
        public static int RenditionRank(ReadOnlySpan<char> imageId)
        {
            if (imageId.Length != ImageIdLength) return 0;
            var size = imageId.Slice(8, 8);
            if (size.Equals("0000b273", StringComparison.OrdinalIgnoreCase) || size.Equals("0000e5eb", StringComparison.OrdinalIgnoreCase)) return 640;
            if (size.Equals("00005174", StringComparison.OrdinalIgnoreCase)) return 320;
            if (size.Equals("00001e02", StringComparison.OrdinalIgnoreCase) || size.Equals("0000ee85", StringComparison.OrdinalIgnoreCase)) return 300;
            if (size.Equals("0000f178", StringComparison.OrdinalIgnoreCase)) return 160;
            if (size.Equals("00004851", StringComparison.OrdinalIgnoreCase) || size.Equals("00003b82", StringComparison.OrdinalIgnoreCase)) return 64;
            return 0;
        }
    }

    // ══ 8. THE SKELETON'S GEOMETRY ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The loading band's bar shapes (the band's HEIGHT is <see cref="VerticalLayout.HeroBandHeight(float,bool,bool,bool,bool,bool,bool,bool,string)"/>).
    /// Every bar is <c>max(32, round(measure · fraction))</c> wide at radius 4.</summary>
    public static class Skeleton
    {
        public const float EyebrowFraction = 0.32f, AttributionFraction = 0.40f, MetaFraction = 0.62f,
                           PulseFraction = 0.35f, TitleLastLineFraction = 0.68f, DescriptionLastLineFraction = 0.55f;
        /// <summary>W28's chart caption (new in 0.3, no 0.2.9 bar): a one-line caption the length of an attribution run.</summary>
        public const float ChartFraction = AttributionFraction;
        public const float BarFloor = 32f, BarRadius = 4f;
        /// <summary>The accent-rule placeholder: 20 × 2 at radius 1, 2-DIP top margin.</summary>
        public const float RuleWidth = Controls.AccentRuleWidth, RuleHeight = Controls.AccentRuleHeight,
                           RuleGap = Controls.AccentRuleGap, RuleRadius = 1f;
        /// <summary>The action row: a 120-wide Play button (<see cref="Controls.PrimaryMinWidth"/>), then four 32-DIP
        /// satellites. Renamed from <c>PlayPillWidth</c> (Workstream B migration group C).</summary>
        public const float PlayButtonWidth = 120f;
        public const int SatelliteCount = 4;
        /// <summary>The toolbar band's three leading pills (the search pill is the command bar's SearchPreferred 240).</summary>
        public const float ToolbarPillA = 72f, ToolbarPillB = 88f, ToolbarPillC = 64f;

        public static float BarWidth(float measure, float fraction) => MathF.Max(BarFloor, MathF.Round(measure * fraction));

        /// <summary>A wrapped block's line count (never zero).</summary>
        public static int LineCount(int lines) => Math.Max(1, lines);

        /// <summary>N runs at the measure's width, the last one short — the shape any wrapped paragraph has.</summary>
        public static float LineWidth(float measure, int index, int lines, float lastFraction)
            => index == LineCount(lines) - 1 ? BarWidth(measure, lastFraction) : measure;

        /// <summary>The two-column RAIL's skeleton: a title block of two lines, a three-line blurb at most, the owner
        /// avatar at the owner block's 24.</summary>
        public const int RailTitleLines = 2, RailDescriptionLines = 3;
        public const float OwnerAvatar = 24f;

        /// <summary>The podcast rows' skeleton shapes: the badge placeholder beside (or instead of) the eyebrow bar, the
        /// rating run, the ledger's counts line.</summary>
        public const float BadgeBarWidth = 48f, RatingFraction = 0.55f, LedgerLineFraction = 0.80f;

        /// <summary>The rail's rows top to bottom — what the skeleton RESERVES (<see cref="RailPlanFor"/>) and, through
        /// <see cref="RailLayout.RowsFor"/>, what the loaded column LAYS OUT; <see cref="RailLayout.HeightOf"/> measures
        /// either. <c>Detail.RailColumn</c> / <c>RailSkeletonColumn</c> walk it row for row: cover · eyebrow (TypeYear;
        /// <c>Badges</c> may share its line) or the LEAD row (<c>Owner</c>: an OwnerRow owner block, or an episode's show
        /// link) · title · attribution (<c>Artists</c>: billed artists; a podcast's Attribution slot) · <c>Rating</c> ·
        /// meta (everything but a TypeYear album) · an episode's badges · <c>Ledger</c> · the CTA cluster (a primary +
        /// <c>Fabs</c> FABs: the fixed heart / Share / ⋯ group, or a page's <c>Satellites</c>) · <c>Topics</c> · the blurb
        /// (clamped to the rail's own description lines). The trailing fields default to "absent", so every rail without
        /// the podcast slots is the plan it always was.</summary>
        public readonly record struct RailPlan(bool Eyebrow, bool Owner, bool Artists, bool Meta, int TitleLines, int Fabs,
                                               int DescriptionLines, RailBadgeRow Badges = RailBadgeRow.None,
                                               bool Rating = false, bool Ledger = false, bool Satellites = false,
                                               bool Topics = false);

        /// <summary>The blurb's reserved lines: the rail's own cap, never more than the window's, never negative.</summary>
        public static int RailDescriptionLinesFor(int descriptionMaxLines)
            => Math.Min(RailDescriptionLines, Math.Max(0, descriptionMaxLines));

        /// <summary>The skeleton's PREDICTION, made before any data: per kind and badge style, plus the rail slots the page
        /// declared (<paramref name="slots"/> — presence is known at mount, the data is not). Default slots ⇒ exactly the
        /// pre-podcast plan. An episode with an Attribution slot is led by it (the <see cref="RailPlan.Owner"/> shape), and
        /// has no eyebrow and no after-title attribution.</summary>
        public static RailPlan RailPlanFor(DetailKind kind, BadgeStyle badges, bool heart, int descriptionMaxLines,
                                           RailSlotSet slots = default)
        {
            bool typeYear = badges == BadgeStyle.TypeYear;
            bool lead = RailLayout.LeadsWithAttribution(kind, slots);
            bool eyebrow = typeYear && !lead;
            bool blurb = kind is DetailKind.Playlist or DetailKind.Show or DetailKind.Episode;
            return new RailPlan(
                Eyebrow: eyebrow,
                Owner: lead || badges == BadgeStyle.OwnerRow,
                Artists: typeYear && kind != DetailKind.Episode,
                Meta: !typeYear || RailLayout.IsPodcast(kind),
                TitleLines: RailTitleLines,
                Fabs: slots.Satellites ? Math.Max(0, slots.SatelliteCount) : (heart ? 1 : 0) + 1 + (kind != DetailKind.Album ? 1 : 0),
                DescriptionLines: blurb ? RailDescriptionLinesFor(descriptionMaxLines) : 0,
                Badges: RailLayout.BadgeRowFor(kind, slots.Badges, eyebrow),
                Rating: slots.Rating,
                Ledger: slots.Ledger,
                Satellites: slots.Satellites,
                Topics: slots.Topics);
        }
    }

    // ══ 8b. THE RAIL'S ROW MODEL ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Which rail slots a page declared — <c>FrameSlots</c> PRESENCE (the same bits its equality compares), plus
    /// the length of the array its <c>Satellites</c> builder returned. The one input the loaded rail's row decision
    /// (<see cref="RailLayout.RowsFor"/>) and the skeleton's prediction (<see cref="Skeleton.RailPlanFor"/>) share.
    /// Default = none: every rail exactly as it was before the podcast seams.</summary>
    public readonly record struct RailSlotSet(bool Attribution = false, bool Badges = false, bool Rating = false,
                                              bool Ledger = false, bool Topics = false, bool Satellites = false,
                                              int SatelliteCount = 0);

    /// <summary>Where the <c>Badges</c> slot's row sits: beside the eyebrow on its line (a show: "Podcast [exclusive] [E]"),
    /// on its own line where the eyebrow would be (a kind with no eyebrow), or under the meta line (an episode, W4).</summary>
    public enum RailBadgeRow : byte { None, BesideEyebrow, OwnRow, AfterMeta }

    /// <summary>The two-column rail's column metrics and its NOMINAL row heights — one number per row, shared by the loaded
    /// column, its skeleton twin and <see cref="HeightOf"/>. A row whose content wraps (a three-line title, a two-line
    /// meta, a third topic line, a longer primary label) grows BELOW its reservation: nothing above it ever moves. The
    /// frame gives each single-line podcast slot row (badges on their own line, rating, ledger) a MinHeight of its nominal,
    /// so a slot body shorter than its row never pulls the rows under it up on reveal.</summary>
    public static class RailLayout
    {
        /// <summary>Between rows, and the column's own padding above and below.</summary>
        public const float Gap = 14f, PadTop = 24f, PadBottom = 24f;
        /// <summary>The CTA cluster sits 4 DIP lower than the gap alone would put it.</summary>
        public const float CtaTopMargin = 4f;
        /// <summary>The fixed group: [primary] 12 [heart · Share · ⋯], the group 8 apart, standard 32/r4 icon buttons
        /// (Workstream B: was 40-DIP round FABs); the two units wrap.</summary>
        public const float CtaGap = 12f, FabGap = 8f, FabSize = 32f;
        /// <summary>A page's Satellites (W1's "36 FABs", now the standard 32 ladder): [primary][each satellite] in ONE
        /// wrap, 8 apart both ways.</summary>
        public const float SatelliteSize = 32f, SatelliteGap = 8f;
        /// <summary>The CTA primary's height — Workstream B's 32/r4 <see cref="Controls.PrimaryButton"/>, not the old
        /// 36-DIP capsule (<see cref="Controls.PillHeight"/>, kept compiling until Wave 5 deletes it).</summary>
        public const float PillHeight = Controls.ButtonHeight;
        public const float EyebrowHeight = VerticalLayout.EyebrowRowHeight;
        /// <summary>The lead row: the owner block's 24 avatar, or an episode's show link (a 24 swatch + 13/600 name).</summary>
        public const float LeadHeight = Skeleton.OwnerAvatar;
        public const float AttributionHeight = VerticalLayout.AttributionRowHeight;
        public const float MetaHeight = VerticalLayout.MetaRowHeight;
        /// <summary>One line of <c>Controls.Chip</c>s, 6 apart (also 6 from the eyebrow they share a line with).</summary>
        public const float BadgeHeight = Controls.BadgeHeight, BadgeGap = 6f;
        /// <summary>The rating row: ★ 4.8 · 12,431 ratings at 12.5 on a 2-DIP padded hit box.</summary>
        public const float RatingHeight = 20f;
        /// <summary>The ledger: the 4-DIP <c>Controls.LedgerBar</c>, 6, then one 12/16 counts line.</summary>
        public const float LedgerBarHeight = Controls.LedgerHeight, LedgerGap = 6f, LedgerLineHeight = 16f;
        public const float LedgerHeight = LedgerBarHeight + LedgerGap + LedgerLineHeight;
        /// <summary>The topic words (<c>Controls.Words.Links</c>): two nominal lines of 18, 4 between.</summary>
        public const int TopicLines = 2;
        public const float TopicLineHeight = Controls.Words.LinkLine, TopicLineGap = Controls.Words.LinkGapY;
        public const float TopicsHeight = TopicLines * TopicLineHeight + (TopicLines - 1) * TopicLineGap;
        public const float DescriptionLineHeight = VerticalLayout.DescriptionLineHeight;

        /// <summary>The podcast family (show, episode): its meta line is always drawn and its Attribution slot is a row of
        /// its own, whatever the billed-artist list says.</summary>
        public static bool IsPodcast(DetailKind kind) => kind is DetailKind.Show or DetailKind.Episode;

        /// <summary>An episode's rail is LED by its Attribution slot (the show link, W4) — above the title, where every
        /// other TypeYear kind shows its eyebrow.</summary>
        public static bool LeadsWithAttribution(DetailKind kind, in RailSlotSet slots)
            => kind == DetailKind.Episode && slots.Attribution;

        /// <summary>Where a declared Badges row sits (<see cref="RailBadgeRow"/>).</summary>
        public static RailBadgeRow BadgeRowFor(DetailKind kind, bool declared, bool eyebrow)
            => !declared ? RailBadgeRow.None
             : kind == DetailKind.Episode ? RailBadgeRow.AfterMeta
             : eyebrow ? RailBadgeRow.BesideEyebrow
             : RailBadgeRow.OwnRow;

        /// <summary>The rows the LOADED rail lays out — <c>Detail.RailColumn</c>'s row decisions as data (it walks this
        /// plan; its album-only / playlist-only late rows — daylist, chart, prerelease, release panel, liked facts — are
        /// outside the model, as they are outside the skeleton). <paramref name="fabs"/> is the fixed group's count;
        /// <paramref name="description"/> whether a blurb (or its inline editor) exists.</summary>
        public static Skeleton.RailPlan RowsFor(DetailKind kind, BadgeStyle badges, bool eyebrowText, bool ownerKnown,
            bool artists, bool metaShown, int fabs, bool description, int descriptionMaxLines, RailSlotSet slots)
        {
            bool typeYear = badges == BadgeStyle.TypeYear;
            bool lead = LeadsWithAttribution(kind, slots);
            bool eyebrow = typeYear && eyebrowText && !lead;
            return new Skeleton.RailPlan(
                Eyebrow: eyebrow,
                Owner: lead || (badges == BadgeStyle.OwnerRow && (slots.Attribution || ownerKnown)),
                Artists: typeYear && !lead && (artists || (IsPodcast(kind) && slots.Attribution)),
                Meta: (!typeYear || IsPodcast(kind)) && metaShown,
                TitleLines: Skeleton.RailTitleLines,
                Fabs: slots.Satellites ? Math.Max(0, slots.SatelliteCount) : Math.Max(0, fabs),
                DescriptionLines: description ? Skeleton.RailDescriptionLinesFor(descriptionMaxLines) : 0,
                Badges: BadgeRowFor(kind, slots.Badges, eyebrow),
                Rating: slots.Rating,
                Ledger: slots.Ledger,
                Satellites: slots.Satellites,
                Topics: slots.Topics);
        }

        /// <summary>The column's nominal height for a plan: the padding, the cover square, each row at its nominal (the title
        /// at <paramref name="titleLineHeight"/> × its lines), the CTA's wrapped lines (<see cref="CtaHeight"/>) and one
        /// <see cref="Gap"/> between rows. <paramref name="coverEdge"/> is also the content measure the CTA wraps in.</summary>
        public static float HeightOf(in Skeleton.RailPlan p, float coverEdge, float titleLineHeight)
        {
            float h = 0f;
            int rows = 0;
            Add(MathF.Max(0f, coverEdge));
            if (p.Eyebrow) Add(p.Badges == RailBadgeRow.BesideEyebrow ? MathF.Max(EyebrowHeight, BadgeHeight) : EyebrowHeight);
            if (p.Owner) Add(LeadHeight);
            if (p.Badges == RailBadgeRow.OwnRow) Add(BadgeHeight);
            Add(Skeleton.LineCount(p.TitleLines) * titleLineHeight);
            if (p.Artists) Add(AttributionHeight);
            if (p.Rating) Add(RatingHeight);
            if (p.Meta) Add(MetaHeight);
            if (p.Badges == RailBadgeRow.AfterMeta) Add(BadgeHeight);
            if (p.Ledger) Add(LedgerHeight);
            Add(CtaHeight(p, coverEdge));
            if (p.Topics) Add(TopicsHeight);
            if (p.DescriptionLines > 0) Add(p.DescriptionLines * DescriptionLineHeight);
            return PadTop + h + (rows - 1) * Gap + PadBottom;

            void Add(float rowHeight) { h += rowHeight; rows++; }
        }

        /// <summary>The CTA cluster: its top margin plus its wrapped lines, laid out as the engine's flex wrap does (greedy,
        /// the gap on both axes). The fixed arm wraps two units, the primary and the FAB group; the satellites arm wraps the
        /// primary and every satellite one by one. The primary's nominal width is the skeleton capsule's.</summary>
        public static float CtaHeight(in Skeleton.RailPlan p, float coverEdge)
        {
            int fabs = Math.Max(0, p.Fabs);
            if (!p.Satellites)
            {
                if (fabs == 0) return CtaTopMargin + PillHeight;
                float group = fabs * FabSize + (fabs - 1) * FabGap;
                bool oneLine = Skeleton.PlayButtonWidth + CtaGap + group <= coverEdge + WrapSlack;
                return CtaTopMargin + (oneLine ? MathF.Max(PillHeight, FabSize) : PillHeight + CtaGap + FabSize);
            }
            float used = Skeleton.PlayButtonWidth, line = PillHeight, closed = 0f;
            for (int i = 0; i < fabs; i++)
            {
                if (used + SatelliteGap + SatelliteSize <= coverEdge + WrapSlack)
                {
                    used += SatelliteGap + SatelliteSize;
                    line = MathF.Max(line, SatelliteSize);
                }
                else
                {
                    closed += line + SatelliteGap;
                    used = SatelliteSize;
                    line = SatelliteSize;
                }
            }
            return CtaTopMargin + closed + line;
        }

        /// <summary>The engine's wrap tolerance (a run that overshoots by less still fits its line).</summary>
        const float WrapSlack = 0.01f;
    }

    // ══ 9. THE PER-KIND CONFIG ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The closed per-kind knob set (ch 03 §8's matrix). <c>Columns</c> and <c>CapTitle</c> are gone (the
    /// column tracks are <c>Track.ColumnSet</c>; CapTitle had no reader).</summary>
    public readonly record struct Config(
        DetailKind Kind,
        bool TwoColumn,                 // false → single-column (no literal uses it)
        float RailWidth,
        BadgeStyle Badges,
        bool ShowArtThumb,              // playlist/liked: an art thumb in the title cell
        bool ShowAlbumColumn,           // playlist/liked: the Album lane
        ItemsSelectionMode Selection,   // Extended, or None (single, show)
        bool HasTrailing,               // album family: About / Fans / More-by below the rows
        HeartMode Heart,
        bool ShowPlays = false,         // album family: the Plays lane + the top-track star
        bool ShowTrackArtist = false,   // the per-track artist subline (playlist/liked, compilations)
        DetailContent Content = DetailContent.Tracks,
        bool Recommendations = false,   // playlist: the "Recommended songs" extender
        bool ShowTempo = false,         // the BPM · Key lane may be offered
        bool ShowVersions = false,      // the expand chevron + versions drawer
        bool PlaysColumnOptIn = false,  // the Plays column is a user opt-in here (NOT album semantics)
        RailScope RailScope = RailScope.Album,
        bool RailResizable = true)
    {
        public static Config Playlist => new(
            Kind: DetailKind.Playlist, TwoColumn: true, RailWidth: Design.Size.RailPlaylist, Badges: BadgeStyle.OwnerRow,
            ShowArtThumb: true, ShowAlbumColumn: true,
            Selection: ItemsSelectionMode.Extended, HasTrailing: false, Heart: HeartMode.Follow, ShowTrackArtist: true,
            Recommendations: true, ShowTempo: true, ShowVersions: true, PlaysColumnOptIn: true,
            RailScope: RailScope.Playlist);

        public static Config Album => new(
            Kind: DetailKind.Album, TwoColumn: true, RailWidth: Design.Size.RailAlbum, Badges: BadgeStyle.TypeYear,
            ShowArtThumb: false, ShowAlbumColumn: false,
            Selection: ItemsSelectionMode.Extended, HasTrailing: true, Heart: HeartMode.Save, ShowPlays: true,
            ShowVersions: true, RailScope: RailScope.Album);

        /// <summary>A single / EP-sized release: the album surface unchanged. Singles select like albums (owner decision,
        /// interaction-consistency plan) - one gesture set on every track list - so this is the album literal.</summary>
        public static Config Single => Album;

        /// <summary>The album surface with the per-track artist subline (various artists).</summary>
        public static Config Compilation => Album with { ShowTrackArtist = true };

        public static Config Liked => new(
            Kind: DetailKind.Liked, TwoColumn: true, RailWidth: Design.Size.RailPlaylist, Badges: BadgeStyle.None,
            ShowArtThumb: true, ShowAlbumColumn: true,
            Selection: ItemsSelectionMode.Extended, HasTrailing: false, Heart: HeartMode.None, ShowTrackArtist: true,
            ShowTempo: true, ShowVersions: true, PlaysColumnOptIn: true,
            RailScope: RailScope.Liked);

        /// <summary>A podcast show: the album-style rail with EPISODES in the right column.</summary>
        public static Config Show => new(
            Kind: DetailKind.Show, TwoColumn: true, RailWidth: Design.Size.RailAlbum, Badges: BadgeStyle.TypeYear,
            ShowArtThumb: false, ShowAlbumColumn: false,
            Selection: ItemsSelectionMode.None, HasTrailing: false, Heart: HeartMode.Follow,
            Content: DetailContent.Episodes, RailScope: RailScope.Show);

        /// <summary>A podcast EPISODE (podcast rework §5.4): the show's frame with three knobs turned — its own kind (the
        /// rail is led by the show link, its eyebrow reads "Episode"), no heart in the fixed group (the episode page's ♥ is
        /// one of its Satellites), and the SHOW's rail pair, so the podcast family persists one rail width.</summary>
        public static Config Episode => Show with { Kind = DetailKind.Episode, Heart = HeartMode.None, RailScope = RailScope.Show };

        /// <summary>0.2.9's <c>DetailPage.ResolveConfig</c>: playlist / liked / show / episode are fixed by route; the album
        /// route branches Single → Single, Compilation → Compilation, and Album AND EP → Album.</summary>
        public static Config For(DetailKind kind, AlbumKind releaseKind) => kind switch
        {
            DetailKind.Playlist => Playlist,
            DetailKind.Liked => Liked,
            DetailKind.Show => Show,
            DetailKind.Episode => Episode,
            _ => releaseKind switch
            {
                AlbumKind.Single => Single,
                AlbumKind.Compilation => Compilation,
                _ => Album,
            },
        };
    }

    // ══ 10. THE HEADER TEXT ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The identity strings every arm shows — ONE composition, so a release is never worded two ways across a
    /// layout cross. Built at map time, never per frame.</summary>
    public static class Text
    {
        /// <summary>The release-kind badge ("ALBUM" / "EP" / "SINGLE" / "COMPILATION").</summary>
        public static string KindLabel(AlbumKind kind) => kind switch
        {
            AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
            AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
            AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
            _ => Loc.Get(Strings.Detail.Badge.Album),
        };

        /// <summary>The EYEBROW, answered for EVERY kind (the vertical hero shows it unconditionally; the rail only for
        /// TypeYear — W27's asymmetry is the caller's). TypeYear: "ALBUM · 2013", the kind alone when the year is
        /// unknown (0). OwnerRow: Collaborative wins the slot, then Private — only once visibility is KNOWN (an unknown
        /// block must not read as private) — else "Playlist". Anything else (Liked): "Your Library".</summary>
        public static string Eyebrow(BadgeStyle badges, AlbumKind releaseKind, int year, bool collaborative, bool isPublic,
                                     bool visibilityKnown)
            => badges switch
            {
                BadgeStyle.TypeYear => WithYear(KindLabel(releaseKind), year),
                BadgeStyle.OwnerRow => collaborative ? Loc.Get(Strings.Nav.PlaylistCollaborative)
                    : visibilityKnown && !isPublic ? Loc.Get(Strings.Nav.PlaylistPrivate)
                    : Loc.Get(Strings.Nav.Playlist),
                _ => Loc.Get(Strings.Nav.YourLibrary),
            };

        /// <summary>The kind-aware form: a SHOW's TypeYear badge is "Podcast" (0.2.9 <c>MapShow</c>), an EPISODE's is
        /// "Episode" — never an album kind. (The episode's two-column rail leads with its show link instead; the vertical
        /// header, the band's byline fallback and an episode page without a show link still read this.)</summary>
        public static string Eyebrow(DetailKind kind, BadgeStyle badges, AlbumKind releaseKind, int year, bool collaborative,
                                     bool isPublic, bool visibilityKnown)
            => badges != BadgeStyle.TypeYear ? Eyebrow(badges, releaseKind, year, collaborative, isPublic, visibilityKnown)
             : kind == DetailKind.Show ? WithYear(Loc.Get(Strings.Podcast.Show), year)
             : kind == DetailKind.Episode ? WithYear(Loc.Get(Strings.Nav.Episode), year)
             : Eyebrow(badges, releaseKind, year, collaborative, isPublic, visibilityKnown);

        static string WithYear(string kind, int year)
            => year > 0 ? kind + " · " + year.ToString(CultureInfo.InvariantCulture) : kind;

        /// <summary>The collaborator face pile replaces the owner row when there are members AND the list is
        /// collaborative or has two or more.</summary>
        public static bool ShowCollaborators(int count, bool collaborative) => count > 0 && (collaborative || count >= 2);

        /// <summary>The band's byline — a FOUR-RUNG fallback (W12): "owner · meta" when both exist ▸ owner ▸ meta ▸ the
        /// eyebrow. An album has no owner, so its byline is its meta line. Null when every rung is blank.</summary>
        public static string? Byline(string? owner, string? meta, string eyebrow)
            => owner is { Length: > 0 } o && meta is { Length: > 0 } m ? o + " · " + m
             : owner is { Length: > 0 } ? owner
             : meta is { Length: > 0 } ? meta
             : eyebrow is { Length: > 0 } ? eyebrow : null;

        /// <summary>"13 songs · 1 hr 14 min". <paramref name="durationsKnown"/> false (any row still thin) DROPS the
        /// duration segment — a sum over zero-duration disc rows is a lie. No year: it lives in the eyebrow and the
        /// release panel, never a third time on this line.</summary>
        public static string? AlbumMeta(int trackCount, long totalMs, bool durationsKnown)
        {
            string songs = Strings.Detail.SongCount(trackCount);
            return durationsKnown ? Strings.Detail.MetaLine(songs, Track.Format.TotalTime(totalMs)) : songs;
        }

        /// <summary>"50 songs · 18.7M saves · 2 hr 59 min"; the saves segment only when &gt; 0 (never "0 saves"). A MIXED
        /// membership states both kinds ("48 songs · 3 episodes"), the songs half being the total minus the episodes.</summary>
        public static string? PlaylistMeta(int trackCount, long totalMs, bool durationsKnown, int saves, int episodes = 0)
        {
            string songs = episodes > 0
                ? Strings.Detail.SongCount(Math.Max(0, trackCount - episodes)) + " · " + Strings.Podcast.EpisodeCount(episodes)
                : Strings.Detail.SongCount(trackCount);
            if (!durationsKnown) return saves > 0 ? songs + " · " + SaveCountText(saves) : songs;
            string total = Track.Format.TotalTime(totalMs);
            return saves > 0
                ? Strings.Detail.MetaLineSaved(songs, SaveCountText(saves), total)
                : Strings.Detail.MetaLine(songs, total);
        }

        /// <summary>"1,204 songs · 71 hr 3 min".</summary>
        public static string? LikedMeta(int count, long totalMs, bool durationsKnown)
        {
            string songs = Strings.Detail.SongCount(count);
            return durationsKnown ? Strings.Detail.MetaLine(songs, Track.Format.TotalTime(totalMs)) : songs;
        }

        /// <summary>"Publisher · 700 episodes" — the count is what the show HAS, not what is resident.</summary>
        public static string? ShowMeta(string? publisher, int totalEpisodes)
        {
            string episodes = Strings.Podcast.EpisodeCount(totalEpisodes);
            return publisher is { Length: > 0 } ? publisher + " · " + episodes : episodes;
        }

        /// <summary>The save count as it reads in the meta line: compact above 999, the plural key below.</summary>
        public static string SaveCountText(long n)
            => n >= 1000 ? Strings.Detail.SaveCountCompact(CompactCount(n)) : Strings.Detail.SaveCount(n);

        /// <summary>0.2.9's <c>HomeCards.CompactNumber</c> (1.2K / 18.7M / 1.1B, current culture).</summary>
        public static string CompactCount(long n)
        {
            var c = CultureInfo.CurrentCulture;
            return n >= 1_000_000_000 ? (n / 1_000_000_000d).ToString("0.#", c) + "B"
                 : n >= 1_000_000 ? (n / 1_000_000d).ToString("0.#", c) + "M"
                 : n >= 1_000 ? (n / 1_000d).ToString("0.#", c) + "K"
                 : n.ToString("N0", c);
        }
    }

    // ══ 11. THE SORT KEYS ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The per-context sort pair. Column's default is the −1 "never chosen" sentinel (⇒ the table profile's
    /// default sort, e.g. Liked's DateAdded desc) — which is why <c>Platform.Keys.DetailSortCol</c> (default 0 = a real
    /// column) must NOT be used for it.</summary>
    public static class SortKeys
    {
        public static SettingKey<int> Column(EntityUri context) => new("detail.sort.col:" + context.Text, -1);
        public static SettingKey<bool> Descending(EntityUri context) => new("detail.sort.desc:" + context.Text, false);
    }
}
