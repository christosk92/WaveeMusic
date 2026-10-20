// ── Shell/Stage.cs ─────────────────────────────────────────────────────────────────────────────────────────────────
// Stage.Mode/ModeRules, Stage.Aspect + Stage.Layout (the fullscreen allocator), Stage.Transport, Stage.Tone, Stage.Entry,
// Stage.Caption
//
// Role: CORE
// Owner: K
// Wave: 7
// Budget: 420 lines
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §2, §4.7
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE FULLSCREEN STAGE'S CORE — and nothing that paints:
//
//   • `Stage.Mode`      the four panes as a PERSISTED preference (Prefs.Stage); `ModeRules.Coerce` is the append-only int rule.
//   • `Stage.Layout`    the PURE allocator: four ASPECT classes from the stage policy's three ladders (width, height, ratio;
//                       the structure's `Rules\Tiers.Stage.cs`, TC3) and every DIP the renderer lays out as a fraction of
//                       the viewport, budgeted from the window tier's metrics (`StageTierMetrics`); the cover's two sizes
//                       (hero / thumb) that the one morphing node moves between.
//   • `Stage.Transport` the three predicates the transport card keeps (byte-identical to the previous stage).
//   • `Stage.Tone`      the accent cross-fade and scrim constants (alphas live here, colours in StageInk — ch 00 §4.4;
//                       named Tone because the UI files alias `Ink = Wavee.Theming.Roles.StageInk`, V-U1).
//   • `Stage.Entry`     who may enter (not over fullscreen video — an EMPTY stage is allowed, as the rail ⛶ does today) and
//                       the morph key the bar art and the hero share.
//   • `Stage.Caption`   the lyric caption's clock view (which line it hangs on, when the break dots stand in) and its slots.
//
// Rules: `System`-only — no Element, no signal, no entity read — so Wavee.Tests drives the real arithmetic. No
// allocation after warm-up (P8); no LINQ, no closures, no async, no boxing (P9).

namespace Wavee;

public static partial class Stage
{
    // ── 1. the four modes ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What the right-hand region shows. PERSISTED ints (Platform.Keys.StageMode) — append only.</summary>
    public enum Mode : byte { Lyrics = 0, Visualizer = 1, Queue = 2, Artist = 3 }

    public static class ModeRules
    {
        public const int Count = 4;
        /// <summary>A stored/hand-edited int → a real mode; anything else is Lyrics.</summary>
        public static int Coerce(int stored) => (uint)stored < (uint)Count ? stored : (int)Mode.Lyrics;
        /// <summary>The gallery is a Visualizer-mode affordance: toggled from another mode it first switches the mode (the
        /// prototype's <c>togglePane</c>, Flagship.dc.html:555).</summary>
        public static (Mode Mode, bool Open) ToggleGallery(Mode current, bool open)
            => current == Mode.Visualizer ? (current, !open) : (Mode.Visualizer, true);
        /// <summary>The caption line carries the active lyric where no lyrics pane does: over the face when the user wants
        /// it and the face leaves room for it (<see cref="Visualizer.Catalog.CaptionFriendly"/> — never over Verse, whose
        /// lyrics ARE the face), and in Lyrics mode when the aspect has no room for the pane (Compact) — never without a
        /// timed line.</summary>
        public static bool ShowsCaption(Mode m, bool overlayOn, bool hasTimedLyrics, bool paneShown, Visualizer.Kind kind)
            => hasTimedLyrics && ((m == Mode.Visualizer && overlayOn && Visualizer.Catalog.CaptionFriendly(kind)) || (m == Mode.Lyrics && !paneShown));

        /// <summary>The stage's face keys (Visualizer mode): <c>[</c> the previous shown face, <c>]</c> the next (Shift: the
        /// previous / next GROUP), <c>G</c> the gallery. VK_OEM_4 / VK_OEM_6 — FluentGpu.Foundation.Keys names neither.</summary>
        public const int PreviousFaceKey = 219, NextFaceKey = 221;
        /// <summary>−1 for <c>[</c>, +1 for <c>]</c>, 0 for any other key.</summary>
        public static int FaceStep(int keyCode) => keyCode == PreviousFaceKey ? -1 : keyCode == NextFaceKey ? 1 : 0;
    }

    // ── 2. the allocator ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The four layout classes. Desktop is the 1920×1080 board; the others are the prototypes' container queries.</summary>
    public enum Aspect : byte { Desktop = 0, Ultrawide = 1, Portrait = 2, Compact = 3 }

    /// <summary>Every DIP the renderer lays out, resolved from (W, H) and the window tier's metrics. One structure, one
    /// <see cref="Aspect"/> flag: a call site never re-derives a breakpoint. Each class edge holds (the policy's ladders), so a
    /// drag across an edge flips once per crossing.</summary>
    public readonly partial record struct Layout(
        Aspect Aspect, float W, float H,
        float HeroArt, float ThumbArt, float PadX, float IdentityTop,
        float PaneX, float PaneRight, float PaneTop, float PaneBottom,
        float GalleryW, float GalleryH, float TransportH,
        bool ShowPane, bool ShowChips, bool ShowGallery, bool ShowVolume, bool IconOnlySelector)
    {
        // ── authored constants (the prototype's chrome, Flagship.dc.html:184-237) ──────────────────────────────────
        public const float TopBarH = 48f;
        /// <summary>The Standard transport card (the wide classes' card is the window tier's, <see cref="Theming.StageTierMetrics.TransportH"/>)
        /// and the Compact class's short one.</summary>
        public const float TransportDesktopH = 112f, TransportCompactH = 96f;
        /// <summary>The stacked transport row (heart · previous · play · next · more at its 12 padding): the narrowest card that holds
        /// it. The Compact class parks its card under the art when the strip beside the art is narrower.</summary>
        public const float TransportStackedMinW = 256f;
        /// <summary>The gap between the pane's foot and the transport card (the pane's 168 foot on the Standard 112 card at 24).</summary>
        public const float PaneAboveTransport = 32f;
        /// <summary>Portrait drops its pane (Lyrics mode then speaks the caption) when less than this is left for it.</summary>
        public const float PortraitPaneMinH = 120f;
        /// <summary>Portrait's identity row at its tallest title column: two 28/36 title lines, the 14/20 meta line and the chips.</summary>
        public const float PortraitIdentityMinH = 2f * 36f + TitleGap + 20f + TitleGap + ChipsTop + ChipH;
        public const float Pad = 24f;
        public const float NowPlayingCardX = 24f, NowPlayingCardY = 64f, NowPlayingCardW = 460f, NowPlayingCardH = 136f;
        public const float GalleryDesktopW = 444f, GalleryTop = 64f, GalleryBottom = 160f, GalleryRight = 24f;
        public const float HairlineH = 3f;
        public const float SelectorItemW = 128f, SelectorIconOnlyW = 44f;
        public const float ThumbDesktop = 96f, ThumbSmall = 64f;
        /// <summary>The hero's clamp. The ceiling was 640 — the 1920×1080 board's art plus a little — which left a big
        /// monitor's stage mostly empty; it is now only a sanity bound, the per-class fractions do the sizing.</summary>
        public const float HeroMin = 168f, HeroMax = 960f, SmallHeroMin = 96f, SmallHeroMax = 320f;
        /// <summary>Ultrawide's hero share of W (was 0.21 — the prototype's — which tied a 2560×1080 stage's art to the
        /// board's 536 while 1000+ DIP of pane sat empty beside short lyric lines).</summary>
        public const float UltrawideHeroFrac = 0.26f;
        /// <summary>The art is quantised to this grid so a resize pixel re-renders nothing (the previous stage's rule, kept).</summary>
        public const float ArtQuantum = 4f;

        public static float Q4(float v) => MathF.Floor(v / ArtQuantum) * ArtQuantum;

        /// <summary>The class for (w, h) given the previous one. A degenerate size keeps the previous class. The edges are the
        /// stage policy's three ladders (<paramref name="aspect"/>; null = the active one), each resolved against the band the
        /// previous class sat in so its hold applies: a tall window (the Ratio ladder's Portrait band) is Portrait at every width
        /// (C-Stage-Compact / C-Lyrics-Compact draw the 400 x 860 stage as a column with its pane); a narrow one is Portrait while it
        /// is not landscape (so widening a narrow window never takes its pane away) and Compact once it is; a short landscape one is
        /// Compact; and the ratio splits the rest into Desktop and Ultrawide.</summary>
        public static Aspect ClassOf(float w, float h, Aspect? previous, Theming.AspectThresholds? aspect = null)
        {
            if (w <= 0f || h <= 0f) return previous ?? Aspect.Desktop;
            var t = ShellPolicy.AspectOr(aspect);
            float ratio = w / h;
            int band = t.Ratio.Resolve(ratio, RatioBandOf(previous));
            if (band == Theming.AspectThresholds.PortraitBand) return Aspect.Portrait;
            if (t.Width.Resolve(w, WidthBandOf(previous)) == Theming.AspectThresholds.NarrowBand) return ratio >= 1f ? Aspect.Compact : Aspect.Portrait;
            if (ratio >= 1f && t.Height.Resolve(h, HeightBandOf(previous)) == Theming.AspectThresholds.ShortBand) return Aspect.Compact;
            return band == Theming.AspectThresholds.UltrawideBand ? Aspect.Ultrawide : Aspect.Desktop;
        }

        /// <summary>The band each ladder's previous resolve sat in, from the previous class (-1: none, a fresh resolve uses the edge).</summary>
        static int RatioBandOf(Aspect? previous) => previous switch
        {
            null => -1,
            Aspect.Portrait => Theming.AspectThresholds.PortraitBand,
            Aspect.Ultrawide => Theming.AspectThresholds.UltrawideBand,
            _ => Theming.AspectThresholds.DesktopBand,
        };

        static int HeightBandOf(Aspect? previous) => previous switch { null => -1, Aspect.Compact => Theming.AspectThresholds.ShortBand, _ => 1 };

        static int WidthBandOf(Aspect? previous) => previous switch { null => -1, Aspect.Compact => Theming.AspectThresholds.NarrowBand, _ => 1 };

        public static Layout Seed(float w, float h, Theming.AspectThresholds? aspect = null, Theming.StageTierMetrics? metrics = null)
            => Resolve(w, h, null, aspect, metrics);

        /// <summary>The live resolve. Every fraction is the prototype's (Main.dc.html:46 `--hw:min(28cqw, 82cqh − 340px)` and
        /// the three container queries at :142-170); the hero is clamped and quantised. <paramref name="metrics"/> are the window
        /// tier's (null: the Standard stage's): the wide classes' transport card and its gutter, the lyrics pane's type range.</summary>
        public static Layout Resolve(float w, float h, Layout? previous, Theming.AspectThresholds? aspect = null, Theming.StageTierMetrics? metrics = null)
        {
            w = MathF.Max(0f, w);
            h = MathF.Max(0f, h);
            var m = metrics ?? Theming.StageTierMetrics.Default;
            var a = ClassOf(w, h, previous?.Aspect, aspect);
            return Allocate(a, w, h, m) with { Metrics = m };
        }

        static Layout Allocate(Aspect a, float w, float h, Theming.StageTierMetrics m)
        {
            float transportH = m.TransportH, inset = m.TransportInset;
            switch (a)
            {
                case Aspect.Ultrawide:
                {
                    // The height term is the REAL column budget, not the prototype's 0.82·H − 340 (which reserves a
                    // two-line title the art then never gets): from the identity top to the transport gutter, minus the
                    // title gap and a ONE-line title block. TitleMaxLines already drops a long title to one line when
                    // two do not clear the transport, so the art may take that room.
                    float identityTop = Q4(0.12f * h);
                    float column = h - inset - transportH - Pad - identityTop - Pad - HeroTitleBlock1H;
                    float hero = Math.Clamp(Q4(MathF.Min(UltrawideHeroFrac * w, column)), HeroMin, HeroMax);
                    float padX = Q4(0.05f * w);
                    return new Layout(a, w, h, hero, ThumbDesktop, padX, identityTop,
                        PaneX: padX + hero + Q4(0.045f * w), PaneRight: Q4(0.04f * w), PaneTop: 88f, PaneBottom: transportH + inset + PaneAboveTransport,
                        GalleryW: Q4(0.19f * w), GalleryH: MathF.Max(0f, h - GalleryTop - GalleryBottomOf(m)), TransportH: transportH,
                        ShowPane: true, ShowChips: true, ShowGallery: true, ShowVolume: true, IconOnlySelector: false);
                }
                case Aspect.Portrait:
                {
                    float hero = Math.Clamp(Q4(MathF.Min(0.14f * h, 0.26f * w)), SmallHeroMin, SmallHeroMax);
                    float padX = Q4(0.07f * w);
                    float identityTop = TopBarH + 44f;
                    // The pane starts under the identity ROW: the art, or the title column beside it when that is taller (a narrow
                    // window's small art beside a two-line title, the meta line and the chips).
                    // The identity CARD (a Compact window, C-Lyrics-Compact) is shorter: the pane starts a card gap under it.
                    float paneTop = m.IdentityCard ? IdentityCardBottom + IdentityCardTop : identityTop + MathF.Max(hero, PortraitIdentityMinH) + Pad;
                    float paneBottom = transportH + inset + PaneAboveTransport;
                    return new Layout(a, w, h, hero, ThumbSmall, padX, identityTop,
                        PaneX: padX, PaneRight: padX, PaneTop: paneTop, PaneBottom: paneBottom,
                        GalleryW: w, GalleryH: Q4(0.55f * h), TransportH: transportH,
                        ShowPane: h - paneTop - paneBottom >= PortraitPaneMinH, ShowChips: true, ShowGallery: true, ShowVolume: w >= 700f, IconOnlySelector: true);
                }
                case Aspect.Compact:
                {
                    float hero = Math.Clamp(Q4(MathF.Min(0.62f * h, w - 420f)), SmallHeroMin, SmallHeroMax);
                    const float padX = 28f;
                    // The card sits right of the art (V-U23) while that strip holds the stacked row; a narrower window parks it
                    // under the art at the full width, and the art keeps to the room left above it.
                    bool under = w - (padX + hero + Pad) - inset < TransportStackedMinW;
                    if (under) hero = Math.Clamp(Q4(MathF.Min(hero, h - inset - TransportCompactH - Pad - (TopBarH + 28f))), SmallHeroMin, SmallHeroMax);
                    return new Layout(a, w, h, hero, ThumbSmall, padX, TopBarH + 28f,
                        PaneX: padX + hero + 28f, PaneRight: padX, PaneTop: TopBarH + 28f, PaneBottom: TransportCompactH + inset,
                        GalleryW: 0f, GalleryH: 0f, TransportH: TransportCompactH,
                        ShowPane: false, ShowChips: false, ShowGallery: false, ShowVolume: false, IconOnlySelector: true) { TransportUnder = under };
                }
                default:
                {
                    float hero = Math.Clamp(Q4(MathF.Min(0.28f * w, 0.82f * h - 340f)), HeroMin, HeroMax);
                    float padX = Q4(0.058f * w);
                    return new Layout(Aspect.Desktop, w, h, hero, ThumbDesktop, padX, Q4(0.122f * h),
                        PaneX: padX + hero + Q4(0.05f * w), PaneRight: Q4(0.05f * w), PaneTop: 88f, PaneBottom: transportH + inset + PaneAboveTransport,
                        GalleryW: GalleryDesktopW, GalleryH: MathF.Max(0f, h - GalleryTop - GalleryBottomOf(m)), TransportH: transportH,
                        ShowPane: true, ShowChips: true, ShowGallery: true, ShowVolume: true, IconOnlySelector: false);
                }
            }
        }

        // ── the window tier's metrics and the Compact card's place ──────────────────────────────────────────────────

        /// <summary>The window tier's metrics this layout was resolved with (null on a default layout: the Standard stage's).</summary>
        public Theming.StageTierMetrics? Metrics { get; init; }

        Theming.StageTierMetrics M => Metrics ?? Theming.StageTierMetrics.Default;

        /// <summary>The Compact class's card is parked under the art at the full width (the strip beside the art is too narrow).</summary>
        public bool TransportUnder { get; init; }

        /// <summary>The transport card's gutter to the window's edges (the window tier's: 24, 16 on a Compact or Small window).</summary>
        public float TransportInset => M.TransportInset;

        /// <summary>The visualizer layouts the window tier may draw (P6 WK, <see cref="Theming.StageTierMetrics.Layouts"/>; every layout on a default layout).</summary>
        public Theming.StageLayouts AllowedLayouts => M.Layouts;

        // ── the window tier's regions: the gallery to the foot, the Up next column, the bars under the cover, the identity card ─────

        /// <summary>The docked gallery's gap to the stage's foot: the transport card's gutter where the tier runs it to the foot (C-Stage-Huge),
        /// else clear of the full-width card (<see cref="GalleryBottom"/>).</summary>
        static float GalleryBottomOf(Theming.StageTierMetrics m) => m.GalleryToFoot ? m.TransportInset : GalleryBottom;

        /// <summary>The docked gallery's gap to the stage's foot (the root's margin under the column).</summary>
        public float GalleryBottomInset => GalleryBottomOf(M);

        /// <summary>The transport card's right gutter: beside a docked gallery that runs to the foot it ends a gutter short of the face's inset
        /// (C-Stage-Huge: the card stops 516 from the right edge of a 2560 stage, the 444 gallery's column plus three 24 gutters); else the gutter.</summary>
        public float TransportRight(bool galleryShown)
            => galleryShown && M.GalleryToFoot && ShowGallery && Aspect != Aspect.Portrait ? FaceRight(true) + TransportInset : TransportInset;

        /// <summary>The Up next column's gap to the lyrics pane (C-Stage-Huge4K: the 1600 lyrics column stops 336 short of the column).</summary>
        public const float UpNextGap = 336f;

        /// <summary>Lyrics mode docks the Up next column beside the lyrics (C-Stage-Huge4K): the tier has one and the stage is a wide class with its pane.</summary>
        public bool UpNextShown(Mode mode) => M.UpNextW > 0f && mode == Mode.Lyrics && ShowPane && Aspect is Aspect.Desktop or Aspect.Ultrawide;

        /// <summary>The Up next column's rect: the tier's width at the right gutter, from the gallery's top down to a gutter above the transport card.</summary>
        public (float X, float Y, float W, float H) UpNextRect
            => (MathF.Max(0f, W - TransportInset - M.UpNextW), GalleryTop, M.UpNextW, MathF.Max(0f, TransportTop - Pad - GalleryTop));

        /// <summary>The lyrics pane's width beside the Up next column (never wider than the pane).</summary>
        public float LyricsPaneW(Mode mode) => UpNextShown(mode) ? MathF.Max(0f, MathF.Min(PaneW, UpNextRect.X - UpNextGap - PaneX)) : PaneW;

        /// <summary>The gap between the stacked identity block (two title lines' worth, so a title change never moves them) and the bars.</summary>
        public const float ArtBarsGap = 48f;

        /// <summary>The visualizer bars under the cover's title column (C-Stage-Huge4K): their rect in Lyrics mode, under the stacked identity's
        /// two-line budget, the cover's width; empty where the tier has none, the class stacks no identity or the room above the card is short.</summary>
        public (float X, float Y, float W, float H) ArtBarsRect(Mode mode)
        {
            if (M.ArtBarsH <= 0f || mode != Mode.Lyrics || IdentityIsRow) return default;
            float y = TitleY(mode) + TitleBlockH(mode, 2) + ArtBarsGap;
            if (y + M.ArtBarsH > TransportTop - Pad) return default;
            return (PadX, y, HeroArt, M.ArtBarsH);
        }

        /// <summary>The bars under the cover show (<see cref="ArtBarsRect"/> is not empty): the clock leases the spectrum for them.</summary>
        public bool ArtBarsShown(Mode mode) => ArtBarsRect(mode).H > 0f;

        /// <summary>The identity card (C-Stage-Compact, C-Lyrics-Compact): its gap under the top bar, its height and padding, the cover in it and
        /// the gap from the cover to the titles; the face chip's gap under the card and its height (a 40 target).</summary>
        public const float IdentityCardTop = 8f, IdentityCardH = 72f, IdentityCardPad = 8f, IdentityCardArt = 56f, IdentityCardGap = 12f;
        public const float FaceChipGap = 12f, FaceChipH = 40f;

        /// <summary>The card's foot (under the top bar, its gap and its height).</summary>
        public const float IdentityCardBottom = TopBarH + IdentityCardTop + IdentityCardH;

        /// <summary>The identity is the now-playing CARD in <paramref name="mode"/>: the tier draws one, and the mode is Visualizer (the card
        /// stands where the 460 card stood) or the stage is Portrait (the column draws the card in every mode).</summary>
        public bool IdentityCardOn(Mode mode) => M.IdentityCard && (mode == Mode.Visualizer || Aspect == Aspect.Portrait);

        /// <summary>The now-playing card's rect in <paramref name="mode"/>: the identity card across the stage at the tier's gutter, or the
        /// 460 x 136 card at 24, 64.</summary>
        public (float X, float Y, float W, float H) NowPlayingCardRect(Mode mode)
            => IdentityCardOn(mode) ? (TransportInset, TopBarH + IdentityCardTop, MathF.Max(0f, W - 2f * TransportInset), IdentityCardH)
                                    : (NowPlayingCardX, NowPlayingCardY, NowPlayingCardW, NowPlayingCardH);

        /// <summary>The face chip (the face's name; it opens the gallery) shows under the identity card in Visualizer mode.</summary>
        public bool FaceChipShown(Mode mode) => mode == Mode.Visualizer && IdentityCardOn(mode);

        /// <summary>The face chip's top edge.</summary>
        public float FaceChipY => IdentityCardBottom + FaceChipGap;

        /// <summary>The gallery shows from the stored flag, or (a tier whose gallery opens only on request: C-Stage-Compact) once it was asked
        /// for during the stage's life.</summary>
        public bool GalleryOpenFor(bool stored, bool asked) => stored && (!M.GalleryOnDemand || asked);

        // ── derived reads the renderer uses ─────────────────────────────────────────────────────────────────────────

        /// <summary>The ONE cover node's size for a mode: the hero everywhere but the Visualizer thumb.</summary>
        public float CoverSize(Mode mode) => IdentityCardOn(mode) ? IdentityCardArt : mode == Mode.Visualizer ? ThumbArt : HeroArt;
        public float CoverX(Mode mode) => IdentityCardOn(mode) ? TransportInset + IdentityCardPad : mode == Mode.Visualizer ? NowPlayingCardX + 20f : PadX;
        public float CoverY(Mode mode) => IdentityCardOn(mode) ? TopBarH + IdentityCardTop + IdentityCardPad : mode == Mode.Visualizer ? NowPlayingCardY + 20f : IdentityTop;
        /// <summary>Portrait and Compact lay the identity out as a ROW (art left, title right of it); Desktop/Ultrawide stack it.</summary>
        public bool IdentityIsRow => Aspect is Aspect.Portrait or Aspect.Compact;
        /// <summary>The title column: beside the thumb in Visualizer mode; beside the hero on the small classes (V-U23); under it otherwise.</summary>
        public float TitleX(Mode mode) => IdentityCardOn(mode) ? CoverX(mode) + IdentityCardArt + IdentityCardGap
            : mode == Mode.Visualizer ? CoverX(mode) + ThumbArt + 16f : IdentityIsRow ? PadX + HeroArt + Pad : PadX;
        public float TitleY(Mode mode) => IdentityCardOn(mode) ? CoverY(mode) + MathF.Max(0f, (IdentityCardArt - TitleBlockH(mode, 1)) * 0.5f)
            : mode == Mode.Visualizer ? NowPlayingCardY + 18f : IdentityIsRow ? IdentityTop : IdentityTop + HeroArt + Pad;
        /// <summary>The title column's width. Under the hero (Desktop/Ultrawide) it runs past the art's edge up to the pane's
        /// gutter (§3.2: the pane starts at PaneX), so a long title reads instead of being cut at the cover's width; beside the
        /// art on the small classes it takes the rest of the row; in the now-playing card it is the card minus the thumb.</summary>
        public float TitleW(Mode mode) => IdentityCardOn(mode) ? MathF.Max(0f, W - TransportInset - IdentityCardPad - TitleX(mode))
            : mode == Mode.Visualizer ? NowPlayingCardW - (ThumbArt + 56f)
            : IdentityIsRow ? MathF.Max(MathF.Min(TitleRowMinW, W - TitleX(mode) - TitleRowEdge), W - TitleX(mode) - PadX) : MathF.Max(HeroArt, PaneX - Pad - PadX);
        /// <summary>The row's title column's floor, and the least gap it keeps to the window's edge when the floor would cross it.</summary>
        public const float TitleRowMinW = 160f, TitleRowEdge = 16f;

        /// <summary>The titles column's rhythm (Hero: Gap XS between title · meta · chips; the chips row's top margin S and
        /// its 24-DIP chip) — what <see cref="TitleBlockH"/> adds up.</summary>
        public const float TitleGap = 4f, ChipsTop = 8f, ChipH = 24f;
        /// <summary>The hero identity block with ONE title line on the wide classes (TitleLarge 52 · meta 24 · chips) —
        /// <see cref="TitleBlockH"/>(Lyrics, 1) as a constant, so <see cref="Resolve"/> can budget the art before the
        /// layout exists. A test pins the two.</summary>
        public const float HeroTitleBlock1H = 52f + TitleGap + 24f + TitleGap + ChipsTop + ChipH;
        /// <summary>The identity block's height with <paramref name="lines"/> title lines: title · meta · (chips).</summary>
        public float TitleBlockH(Mode mode, int lines)
        {
            var (_, titleLine) = TitleFont(mode);
            var (_, metaLine) = MetaFont(mode);
            if (IdentityCardOn(mode)) return lines * titleLine + metaLine;   // the card's column: title over meta, no gap, no chips
            return lines * titleLine + TitleGap + metaLine + (ShowChips ? TitleGap + ChipsTop + ChipH : 0f);
        }
        /// <summary>The title's line budget: the thumb card holds ONE line (the card is a fixed 136 DIP); the hero takes TWO
        /// whenever the whole block (two lines, the meta line, the chips) still clears the transport card, else one. The
        /// meta and chips FLOW under it (a column), so nothing below the title is positioned by a hard-coded offset.</summary>
        public int TitleMaxLines(Mode mode)
            => mode == Mode.Visualizer || IdentityCardOn(mode) ? 1 : TitleY(mode) + TitleBlockH(mode, 2) <= TransportTop - Pad ? 2 : 1;

        // ── the top bar: brand · SelectorBar · [gallery][exit] ──────────────────────────────────────────────────────

        /// <summary>The top bar's own metrics (TopBar: Padding 16 · 8, Gap 8; the button column's Gap 4), the 40-DIP glass
        /// button, and the labelled exit button's budget (glyph + "Exit full screen" in the longest shipped locale).</summary>
        public const float TopBarPadL = 16f, TopBarPadR = 8f, TopBarGap = 8f, SelectorGap = 4f;
        public const float BarButtonW = 40f, BarButtonGap = 4f, ExitLabelW = 220f;
        /// <summary>The SelectorBar's four items, labelled or icon-only.</summary>
        public float SelectorW => 4f * (IconOnlySelector ? SelectorIconOnlyW : SelectorItemW) + 3f * SelectorGap;
        /// <summary>Each side column of the top bar: the brand and the button cluster split what the SelectorBar leaves
        /// (both Grow 1 · Basis 0), so the selector stays centred.</summary>
        public float TopBarSideW => MathF.Max(0f, (W - TopBarPadL - TopBarPadR - SelectorW - 2f * TopBarGap) * 0.5f);
        /// <summary>The exit button carries its label only beside a labelled selector AND when its column holds the gallery
        /// toggle plus the labelled button; otherwise it is the glyph alone with the label as its tooltip — it never
        /// truncates to "Ex…" (the icon-only classes show "⤢" alone, §3.5).</summary>
        public bool ExitLabelShown => !IconOnlySelector && TopBarSideW >= BarButtonW + BarButtonGap + ExitLabelW;

        // ── the transport card's clusters ───────────────────────────────────────────────────────────────────────────

        /// <summary>The card's horizontal padding · the centred cluster (shuffle · previous · PLAY 56 · next · repeat at Gap 8)
        /// · the right cluster's gap, the device button's floor (glyph + chevron, the name trimmed away) and the volume slider.</summary>
        public const float TransportPadX = 20f, TransportCentreW = 4f * BarButtonW + 56f + 4f * 8f;
        public const float TransportRightGap = 6f, DeviceMinW = 72f, VolumeSliderW = 128f;
        /// <summary>heart · mute · device (at its floor) · more.</summary>
        public const float TransportRightMinW = 3f * BarButtonW + DeviceMinW + 3f * TransportRightGap;
        /// <summary>The card's width (full width at the gutter, or right of the art on Compact) and its top edge.</summary>
        public float TransportW => MathF.Max(0f, W - TransportLeft - TransportInset);
        public float TransportTop => H - TransportInset - TransportH;
        /// <summary>Each side of the centred cluster: the "Playing from" column and the right cluster split it equally.</summary>
        public float TransportSideW => MathF.Max(0f, (TransportW - 2f * TransportPadX - TransportCentreW) * 0.5f);
        /// <summary>The volume slider shows where the class offers it (<see cref="ShowVolume"/>) AND the right cluster still fits
        /// beside it; otherwise the mute glyph alone — the cluster never runs off the card.</summary>
        public bool VolumeFits => ShowVolume && TransportSideW >= TransportRightMinW + TransportRightGap + VolumeSliderW;
        /// <summary>The card cannot seat its three clusters (the right cluster at its floor beside the centred one): it stacks to the
        /// narrow row instead (C-Stage-Compact: the seek row over heart · previous · play · next · more), never running a cluster
        /// off the card.</summary>
        public bool TransportStacked => TransportSideW < TransportRightMinW;

        // ── the caption: the lyric over the face (and Compact Lyrics mode's lyric line) ──────────────────────────────

        /// <summary>The caption block's widest share of its region · its gutter above the transport card · the gap between
        /// its lines · the active line's type range · the context lines' share of it · the height under which the context
        /// lines drop · the soft veil's overhang around the block.</summary>
        public const float CaptionMaxWFrac = 0.72f, CaptionAboveTransport = 24f, CaptionLineGap = 8f;
        public const float CaptionActiveMin = 40f, CaptionActivePortraitMin = 32f, CaptionActiveMax = 56f, CaptionContextRatio = 0.45f;
        public const float CaptionContextMinH = 600f, CaptionVeilPadX = 120f, CaptionVeilPadY = 56f;
        public const int CaptionActiveMaxLines = 2;

        /// <summary>The ACTIVE line's type: the hero-title scale, 40–56 DIP by height (an even size, the line 8 DIP taller);
        /// Portrait is width-capped (floor 32), Compact steps down to its title size.</summary>
        public (float Size, float Line) CaptionFont
        {
            get
            {
                if (Aspect == Aspect.Compact) return (24f, 30f);
                float s = MathF.Round(MathF.Min(0.04f * H, 0.05f * W) * 0.5f) * 2f;
                s = Math.Clamp(s, Aspect == Aspect.Portrait ? CaptionActivePortraitMin : CaptionActiveMin, CaptionActiveMax);
                return (s, s + 8f);
            }
        }
        /// <summary>The previous / next context lines: ≈0.45 of the active size (floor 14).</summary>
        public (float Size, float Line) CaptionContextFont
        {
            get
            {
                float c = MathF.Max(14f, MathF.Round(CaptionFont.Size * CaptionContextRatio));
                return (c, MathF.Round(c * 1.3f));
            }
        }
        /// <summary>The previous and next lines need vertical room: never on Compact, and only on a window this tall.</summary>
        public bool CaptionShowsContext => Aspect != Aspect.Compact && H >= CaptionContextMinH;
        /// <summary>The region the caption centres in: the face's width (the open gallery's inset excluded, so the line never
        /// slides under the pane).</summary>
        float CaptionRegionW(Mode mode, bool galleryOpen) => MathF.Max(0f, W - (mode == Mode.Visualizer ? FaceRight(galleryOpen) : 0f));
        /// <summary>The caption block's width: 72 % of its region, on the quantum; on Compact the strip right of the art.</summary>
        public float CaptionW(Mode mode, bool galleryOpen)
            => Aspect == Aspect.Compact ? TransportW : Q4(CaptionMaxWFrac * CaptionRegionW(mode, galleryOpen));
        /// <summary>The caption block's left edge: centred in its region; on Compact flush with the transport card.</summary>
        public float CaptionX(Mode mode, bool galleryOpen)
            => Aspect == Aspect.Compact ? TransportLeft : (CaptionRegionW(mode, galleryOpen) - CaptionW(mode, galleryOpen)) * 0.5f;
        /// <summary>The block's BOTTOM margin from the stage's bottom edge: above the transport card and its gutter, so the
        /// line never sits under the controls or the hairline — the lower third of the stage.</summary>
        public float CaptionBottom => TransportH + TransportInset + CaptionAboveTransport;
        /// <summary>The block at its tallest: two active lines plus the two context lines when shown.</summary>
        public float CaptionBlockMaxH
        {
            get
            {
                float ctx = CaptionShowsContext ? 2f * (CaptionContextFont.Line + CaptionLineGap) : 0f;
                return CaptionActiveMaxLines * CaptionFont.Line + ctx;
            }
        }
        /// <summary>The block's top edge at its tallest.</summary>
        public float CaptionTopMin => H - CaptionBottom - CaptionBlockMaxH;
        /// <summary>The Lyrics mode's caption (no pane) has room on a row-identity class: its tallest block clears the title column (and the
        /// art too where it spans the width) by the gutter. A short narrow stage keeps the art, the title and the controls and drops the
        /// caption rather than lay it over them. Every other mode and class: true.</summary>
        public bool CaptionFits(Mode mode)
        {
            if (mode != Mode.Lyrics || !IdentityIsRow) return true;
            float title = TitleY(mode) + TitleBlockH(mode, TitleMaxLines(mode));
            float bottom = Aspect == Aspect.Compact && !TransportUnder ? title : MathF.Max(title, CoverY(mode) + CoverSize(mode));
            return CaptionTopMin >= bottom + Pad;
        }
        /// <summary>Compact: the transport card sits RIGHT of the art, not under it (V-U23), unless the strip there is too narrow
        /// (<see cref="TransportUnder"/>). Everywhere else it spans the width at the gutter.</summary>
        public float TransportLeft => Aspect == Aspect.Compact && !TransportUnder ? PadX + HeroArt + Pad : TransportInset;
        /// <summary>The face's right inset while the gallery is open (the prototype's <c>.pane .stg { right: 492px }</c>) — THIS
        /// layout's gallery width plus the two gutters (Ultrawide's docked column is 19 % of W, not 444 — V-U45).</summary>
        public float FaceRight(bool galleryOpen) => galleryOpen && ShowGallery && Aspect != Aspect.Portrait ? GalleryW + 2f * Pad : 0f;

        /// <summary>The top edge of Portrait's gallery SHEET (55 % of H, parked above the transport and its two gutters).</summary>
        public float SheetTop => H - (TransportH + 2f * Pad) - GalleryH;

        /// <summary>The FACE-SAFE rect (viz-app-plan §3.7, §5) in stage coordinates: where a face may centre its subject without
        /// the chrome rows over it — left of the open gallery; below the now-playing card when it shows (64 + 136 + 24 = 224),
        /// else below the top bar; above the caption block at its tallest when the caption shows, else above the transport
        /// card's gutter; above Portrait's open sheet; on Compact the strip right of the art. Never inverted (at least one
        /// DIP tall and wide). Chrome hidden (idle) does not grow it: a face keeps its geometry (only Verse uses the room).</summary>
        public FaceSafeRect FaceSafe(bool galleryOpen, bool captionShown)
        {
            bool compact = Aspect == Aspect.Compact;
            float left = compact ? TransportLeft : 0f;
            float right = compact ? W - Pad : W - FaceRight(galleryOpen);
            float top = IdentityCardOn(Mode.Visualizer) ? FaceChipY + FaceChipH + Pad : ShowChips ? NowPlayingCardY + NowPlayingCardH + Pad : TopBarH + Pad;
            float bottom = captionShown ? CaptionTopMin - Pad : TransportTop - Pad;
            if (Aspect == Aspect.Portrait && galleryOpen && ShowGallery) bottom = MathF.Min(bottom, SheetTop - Pad);
            right = MathF.Max(right, left + 1f);
            bottom = MathF.Max(bottom, top + 1f);
            return new FaceSafeRect(left, top, right - left, bottom - top);
        }
        /// <summary>The pane's width from its two edges; never negative.</summary>
        public float PaneW => MathF.Max(0f, W - PaneX - PaneRight);

        /// <summary>The lyrics pane's type: min(4.6 % of H, the pane's width / 30), an even size, clamped to the window tier's range
        /// (<see cref="Theming.StageTierMetrics"/>): Standard grows the authored 36 DIP (<c>Lyrics.Surface.Timed(large: true)</c>) up to 64
        /// (the 1920×1080 board's 1084-DIP pane stays at 36; a 2560×1080 ultrawide reads 50); Huge caps it at 48 (C-Lyrics-Huge,
        /// C-Stage-Huge4K); Small reads 30-36 (C-Lyrics-Small) and Compact 26 (C-Lyrics-Compact).</summary>
        public const float LyricsTypeMin = 36f, LyricsTypeMax = 64f;
        public float LyricsTypeSize => Math.Clamp(MathF.Round(MathF.Min(0.046f * H, PaneW / 30f) * 0.5f) * 2f, M.LyricsTypeMin, M.LyricsTypeMax);
        /// <summary><see cref="LyricsTypeSize"/> over the authored size (<see cref="LyricsTypeMin"/>, 36): what the lyrics view scales its
        /// row metrics by.</summary>
        public float LyricsTypeScale => LyricsTypeSize / LyricsTypeMin;

        /// <summary>The Up next rows' scale: min(the pane's width / 1100, H / 864), floored to an eighth so a resize steps it
        /// rarely, clamped 1–1.5. The board (a 1084-DIP pane) stays at 1; a 2560×1080 ultrawide reads 1.25; 3440×1440 reaches
        /// the 1.5 ceiling. Every row metric (height, art, type, gaps) scales by it; the pane's gutters do not.</summary>
        public const float QueueScaleMax = 1.5f, QueueListBaseW = 1100f;
        public float QueueScale => Math.Clamp(MathF.Floor(MathF.Min(PaneW / QueueListBaseW, H / 864f) * 8f) / 8f, 1f, QueueScaleMax);
        /// <summary>Where the Up next list stops (left-aligned): 1100 DIP × <see cref="QueueScale"/>, so the duration sits beside
        /// the song instead of across a 1500-DIP pane.</summary>
        public float QueueListMaxW => MathF.Min(PaneW, QueueListBaseW * QueueScale);
        public float PaneH => MathF.Max(0f, H - PaneTop - PaneBottom);

        /// <summary>Title type per aspect and mode: the hero reads TitleLarge 40/52, the thumb Subtitle 20/28; small
        /// classes step down (Title 28/36 · 24/30).</summary>
        public (float Size, float Line) TitleFont(Mode mode)
        {
            if (IdentityCardOn(mode)) return (16f, 22f);
            if (mode == Mode.Visualizer) return (20f, 28f);
            return Aspect switch { Aspect.Portrait => (28f, 36f), Aspect.Compact => (24f, 30f), _ => (40f, 52f) };
        }
        public (float Size, float Line) MetaFont(Mode mode)
            => IdentityCardOn(mode) ? (13f, 18f) : mode != Mode.Visualizer && (Aspect is Aspect.Desktop or Aspect.Ultrawide) ? (18f, 24f) : (14f, 20f);

        /// <summary>A monotone "how much is on screen" score for the narrowing-never-adds test: the five affordance flags and the
        /// transport height. The hero is EXCLUDED on purpose — it is a per-class formula (0.28·W vs Ultrawide's 0.21·W) and
        /// legitimately shrinks when a wider window promotes to Ultrawide (V-U24).</summary>
        public int Richness => (ShowPane ? 1 : 0) + (ShowChips ? 1 : 0) + (ShowGallery ? 1 : 0) + (ShowVolume ? 1 : 0)
            + (IconOnlySelector ? 0 : 1) + (int)(TransportH * 0.01f);
    }

    /// <summary>A rectangle in stage DIPs (Stage.cs stays System-only, so not the engine's RectF).</summary>
    public readonly record struct FaceSafeRect(float X, float Y, float W, float H)
    {
        public float Right => X + W;
        public float Bottom => Y + H;
    }

    // ── 3. what the transport may do (unchanged from the previous stage) ────────────────────────────────────────────

    public static class Transport
    {
        public static bool PrimaryEnabled(bool hasTrack, bool loading) => hasTrack && !loading;
        /// <summary>The format chip names the PLAYING stream or nothing: no published format, or another Connect device
        /// is active, ⇒ no chip. Silence is the correct answer, not a fallback.</summary>
        public static bool ShowsQualityBadge(bool hasFormat, bool remoteActive) => hasFormat && !remoteActive;
        /// <summary>The identity title: the track's own title, or "nothing playing" when it is absent or still EQUALS the
        /// uri (a placeholder row before its metadata landed — never surface a raw uri).</summary>
        public static bool UsesTitle(string? title, string? uri) => title is { Length: > 0 } && title != uri;
    }

    // ── 4. tone arithmetic (alphas and the cross-fade; the colours are StageInk's) ────────────────────────────

    public static class Tone
    {
        /// <summary>The accent cross-fade on a track change (the prototype's <c>transition: --acc 1s</c>, tightened to the
        /// Fluent "slow" neighbourhood so a skip-skip-skip never lags the art).</summary>
        public const float CrossFadeMs = 600f;
        /// <summary>The scrim over the backdrop: deep under lyrics/queue/artist, light under a face, between the two under
        /// Verse (big words over a bright cover need it — verse-plan D4).</summary>
        public const float ScrimA = 0.56f, ScrimVisualizerA = 0.22f, ScrimVerseA = 0.40f;
        /// <summary>The scrim ABOVE a backdrop face (Large art, Centered) per face (<see cref="LayoutRules.BackdropScrim"/>): deep over a
        /// cover-carrying face running ambient, lighter over a face that draws no now-playing part.</summary>
        public const float BackdropScrimCoverA = 0.42f, BackdropScrimAtmosphereA = 0.30f;
        /// <summary>The scrim for a mode and the selected face.</summary>
        public static float ScrimFor(Mode mode, Visualizer.Kind kind)
            => mode != Mode.Visualizer ? ScrimA : kind == Visualizer.Kind.Verse ? ScrimVerseA : ScrimVisualizerA;
        /// <summary>A moment's palette fade ("change with the music", viz-app-plan §3.5), and its calm length.</summary>
        public const float MomentFadeMs = 900f, MomentCalmFadeMs = 1400f;
        /// <summary>The caption's inks: unsung glyphs (an alpha over the stage ink; the context lines are the arm's secondary ink), the
        /// active line's accent bloom (its layer opacity and blur σ), the soft veil's centre (0.50: at 0.40 a context line read 4 : 1 over a bright
        /// visualizer), and the rise every line hand-off
        /// travels (in from below, out above).</summary>
        public const float CaptionUnsungA = 0.45f;
        public const float CaptionBloomA = 0.55f, CaptionBloomSigma = 10f, CaptionVeilA = 0.50f, CaptionRiseDip = 16f;
        /// <summary>The bottom smoke under the face so the transport card reads (prototype <c>.smk</c>).</summary>
        public const float SmokeH = 440f, SmokeA = 0.60f;
        /// <summary>The base Field's opacity: breathing under a pane, near-full under a face.</summary>
        public const float BaseFieldA = 0.62f, BaseFieldBreathA = 0.20f, BaseFieldVisualizerA = 0.90f;
        /// <summary>The LINEAR cross-fade position for a change that began <paramref name="elapsedMs"/> ago: 0 at the change,
        /// 1 at <see cref="CrossFadeMs"/> and after. The clock lerps from the colour CAPTURED at the change, so the fade
        /// completes in exactly CrossFadeMs whatever the tick rate (an exponential step never reaches the target — V-U17).</summary>
        public static float Progress(float elapsedMs) => elapsedMs <= 0f ? 0f : MathF.Min(1f, elapsedMs / CrossFadeMs);
    }

    // ── 5. entry ───────────────────────────────────────────────────────────────────────────────────────────────────

    public static class Entry
    {
        /// <summary>The shared-element key the player bar's art and the stage hero both carry (an IMAGE node — the
        /// engine captures only image nodes, ConnectedAnimation.cs:246-250).</summary>
        public const string MorphKey = "stage:art";
        /// <summary>Never over a fullscreen VIDEO (F11 owns that). Nothing playing is NOT a bar: the rail ⛶ opens an empty
        /// stage today (the rail's ⛶: "a track with no lyrics still opens a full stage") and so does every door here (V-U53).</summary>
        public static bool CanEnter(bool videoFullscreen) => !videoFullscreen;
    }
}
