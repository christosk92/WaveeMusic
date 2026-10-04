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
//   • `Stage.Layout`    the PURE allocator: four ASPECT classes with hysteresis (the prototypes' container queries,
//                       Main.dc.html:142-170) and every DIP the renderer lays out as a fraction of the viewport; the
//                       cover's two sizes (hero / thumb) that the one morphing node moves between.
//   • `Stage.Transport` the three predicates the transport card keeps (byte-identical to the previous stage).
//   • `Stage.Tone`      the accent cross-fade and scrim constants (alphas live here, colours in Design.StageInk — ch 00 §4.4;
//                       named Tone because the UI files alias `Ink = Wavee.Design.StageInk`, V-U1).
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
        /// <summary>Visualizer mode has no pane: the face takes the stage and the caption shows the lyric.</summary>
        public static bool ShowsPane(Mode m) => m != Mode.Visualizer;
        /// <summary>The gallery is a Visualizer-mode affordance: toggled from another mode it first switches the mode (the
        /// prototype's <c>togglePane</c>, Flagship.dc.html:555).</summary>
        public static (Mode Mode, bool Open) ToggleGallery(Mode current, bool open)
            => current == Mode.Visualizer ? (current, !open) : (Mode.Visualizer, true);
        /// <summary>The caption line carries the active lyric where no lyrics pane does: over the face when the user wants
        /// it, and in Lyrics mode when the aspect has no room for the pane (Compact) — never without a timed line.</summary>
        public static bool ShowsCaption(Mode m, bool overlayOn, bool hasTimedLyrics, bool paneShown)
            => hasTimedLyrics && ((m == Mode.Visualizer && overlayOn) || (m == Mode.Lyrics && !paneShown));
    }

    // ── 2. the allocator ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The four layout classes. Desktop is the 1920×1080 board; the others are the prototypes' container queries.</summary>
    public enum Aspect : byte { Desktop = 0, Ultrawide = 1, Portrait = 2, Compact = 3 }

    /// <summary>Every DIP the renderer lays out, resolved from (W, H). One structure, one <see cref="Aspect"/> flag: a
    /// call site never re-derives a breakpoint. Demotion is immediate; promotion needs the hysteresis reserve, so a
    /// drag across an edge flips once per crossing.</summary>
    public readonly record struct Layout(
        Aspect Aspect, float W, float H,
        float HeroArt, float ThumbArt, float PadX, float IdentityTop,
        float PaneX, float PaneRight, float PaneTop, float PaneBottom,
        float GalleryW, float GalleryH, float TransportH,
        bool ShowPane, bool ShowChips, bool ShowGallery, bool ShowVolume, bool IconOnlySelector)
    {
        // ── authored constants (the prototype's chrome, Flagship.dc.html:184-237) ──────────────────────────────────
        public const float TopBarH = 48f;
        public const float TransportDesktopH = 112f, TransportCompactH = 96f;
        public const float Pad = 24f;
        public const float NowPlayingCardX = 24f, NowPlayingCardY = 64f, NowPlayingCardW = 460f, NowPlayingCardH = 136f;
        public const float GalleryDesktopW = 444f, GalleryTop = 64f, GalleryBottom = 160f, GalleryRight = 24f;
        /// <summary>How far the face is inset on the right while the gallery pane is open (pane + 2 × 24).</summary>
        public const float GalleryInset = GalleryDesktopW + 2f * Pad;
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

        // ── thresholds (§2.2) ──────────────────────────────────────────────────────────────────────────────────────
        /// <summary>Under this width the stage is Compact whatever the height (the previous stage's one threshold, kept).</summary>
        public const float WideEnterW = 600f;
        public const float PortraitEnter = 0.80f, PortraitLeave = 0.85f;
        public const float UltrawideEnter = 2.00f, UltrawideLeave = 1.95f;
        public const float CompactEnterH = 460f, CompactLeaveH = 484f;   // + the previous stage's FoldHysteresisH 24

        public static float Q4(float v) => MathF.Floor(v / ArtQuantum) * ArtQuantum;

        /// <summary>The class for (w, h) given the previous one (hysteresis). A degenerate size keeps the previous class.</summary>
        public static Aspect ClassOf(float w, float h, Aspect? previous)
        {
            if (w <= 0f || h <= 0f) return previous ?? Aspect.Desktop;
            if (w < WideEnterW) return Aspect.Compact;
            float ratio = w / h;
            bool wasCompact = previous == Aspect.Compact, wasPortrait = previous == Aspect.Portrait, wasUltra = previous == Aspect.Ultrawide;
            if (ratio >= 1f && (h <= CompactEnterH || (wasCompact && h < CompactLeaveH))) return Aspect.Compact;
            if (ratio <= PortraitEnter || (wasPortrait && ratio < PortraitLeave)) return Aspect.Portrait;
            if (ratio >= UltrawideEnter || (wasUltra && ratio > UltrawideLeave)) return Aspect.Ultrawide;
            return Aspect.Desktop;
        }

        public static Layout Seed(float w, float h) => Resolve(w, h, null);

        /// <summary>The live resolve. Every fraction is the prototype's (Main.dc.html:46 `--hw:min(28cqw, 82cqh − 340px)` and
        /// the three container queries at :142-170); the hero is clamped and quantised.</summary>
        public static Layout Resolve(float w, float h, Layout? previous)
        {
            w = MathF.Max(0f, w);
            h = MathF.Max(0f, h);
            var a = ClassOf(w, h, previous?.Aspect);
            switch (a)
            {
                case Aspect.Ultrawide:
                {
                    // The height term is the REAL column budget, not the prototype's 0.82·H − 340 (which reserves a
                    // two-line title the art then never gets): from the identity top to the transport gutter, minus the
                    // title gap and a ONE-line title block. TitleMaxLines already drops a long title to one line when
                    // two do not clear the transport, so the art may take that room.
                    float identityTop = Q4(0.12f * h);
                    float column = h - Pad - TransportDesktopH - Pad - identityTop - Pad - HeroTitleBlock1H;
                    float hero = Math.Clamp(Q4(MathF.Min(UltrawideHeroFrac * w, column)), HeroMin, HeroMax);
                    float padX = Q4(0.05f * w);
                    return new Layout(a, w, h, hero, ThumbDesktop, padX, identityTop,
                        PaneX: padX + hero + Q4(0.045f * w), PaneRight: Q4(0.04f * w), PaneTop: 88f, PaneBottom: 168f,
                        GalleryW: Q4(0.19f * w), GalleryH: MathF.Max(0f, h - GalleryTop - GalleryBottom), TransportH: TransportDesktopH,
                        ShowPane: true, ShowChips: true, ShowGallery: true, ShowVolume: true, IconOnlySelector: false);
                }
                case Aspect.Portrait:
                {
                    float hero = Math.Clamp(Q4(MathF.Min(0.14f * h, 0.26f * w)), SmallHeroMin, SmallHeroMax);
                    float padX = Q4(0.07f * w);
                    float identityTop = TopBarH + 44f;
                    return new Layout(a, w, h, hero, ThumbSmall, padX, identityTop,
                        PaneX: padX, PaneRight: padX, PaneTop: identityTop + hero + Pad, PaneBottom: 168f,
                        GalleryW: w, GalleryH: Q4(0.55f * h), TransportH: TransportDesktopH,
                        ShowPane: true, ShowChips: true, ShowGallery: true, ShowVolume: w >= 700f, IconOnlySelector: true);
                }
                case Aspect.Compact:
                {
                    float hero = Math.Clamp(Q4(MathF.Min(0.62f * h, w - 420f)), SmallHeroMin, SmallHeroMax);
                    const float padX = 28f;
                    return new Layout(a, w, h, hero, ThumbSmall, padX, TopBarH + 28f,
                        PaneX: padX + hero + 28f, PaneRight: padX, PaneTop: TopBarH + 28f, PaneBottom: TransportCompactH + Pad,
                        GalleryW: 0f, GalleryH: 0f, TransportH: TransportCompactH,
                        ShowPane: false, ShowChips: false, ShowGallery: false, ShowVolume: false, IconOnlySelector: true);
                }
                default:
                {
                    float hero = Math.Clamp(Q4(MathF.Min(0.28f * w, 0.82f * h - 340f)), HeroMin, HeroMax);
                    float padX = Q4(0.058f * w);
                    return new Layout(Aspect.Desktop, w, h, hero, ThumbDesktop, padX, Q4(0.122f * h),
                        PaneX: padX + hero + Q4(0.05f * w), PaneRight: Q4(0.05f * w), PaneTop: 88f, PaneBottom: 168f,
                        GalleryW: GalleryDesktopW, GalleryH: MathF.Max(0f, h - GalleryTop - GalleryBottom), TransportH: TransportDesktopH,
                        ShowPane: true, ShowChips: true, ShowGallery: true, ShowVolume: true, IconOnlySelector: false);
                }
            }
        }

        // ── derived reads the renderer uses ─────────────────────────────────────────────────────────────────────────

        /// <summary>The ONE cover node's size for a mode: the hero everywhere but the Visualizer thumb.</summary>
        public float CoverSize(Mode mode) => mode == Mode.Visualizer ? ThumbArt : HeroArt;
        public float CoverX(Mode mode) => mode == Mode.Visualizer ? NowPlayingCardX + 20f : PadX;
        public float CoverY(Mode mode) => mode == Mode.Visualizer ? NowPlayingCardY + 20f : IdentityTop;
        /// <summary>Portrait and Compact lay the identity out as a ROW (art left, title right of it); Desktop/Ultrawide stack it.</summary>
        public bool IdentityIsRow => Aspect is Aspect.Portrait or Aspect.Compact;
        /// <summary>The title column: beside the thumb in Visualizer mode; beside the hero on the small classes (V-U23); under it otherwise.</summary>
        public float TitleX(Mode mode) => mode == Mode.Visualizer ? CoverX(mode) + ThumbArt + 16f : IdentityIsRow ? PadX + HeroArt + Pad : PadX;
        public float TitleY(Mode mode) => mode == Mode.Visualizer ? NowPlayingCardY + 18f : IdentityIsRow ? IdentityTop : IdentityTop + HeroArt + Pad;
        /// <summary>The title column's width. Under the hero (Desktop/Ultrawide) it runs past the art's edge up to the pane's
        /// gutter (§3.2: the pane starts at PaneX), so a long title reads instead of being cut at the cover's width; beside the
        /// art on the small classes it takes the rest of the row; in the now-playing card it is the card minus the thumb.</summary>
        public float TitleW(Mode mode) => mode == Mode.Visualizer ? NowPlayingCardW - (ThumbArt + 56f)
            : IdentityIsRow ? MathF.Max(160f, W - TitleX(mode) - PadX) : MathF.Max(HeroArt, PaneX - Pad - PadX);

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
            return lines * titleLine + TitleGap + metaLine + (ShowChips ? TitleGap + ChipsTop + ChipH : 0f);
        }
        /// <summary>The title's line budget: the thumb card holds ONE line (the card is a fixed 136 DIP); the hero takes TWO
        /// whenever the whole block (two lines, the meta line, the chips) still clears the transport card, else one. The
        /// meta and chips FLOW under it (a column), so nothing below the title is positioned by a hard-coded offset.</summary>
        public int TitleMaxLines(Mode mode)
            => mode == Mode.Visualizer ? 1 : TitleY(mode) + TitleBlockH(mode, 2) <= TransportTop - Pad ? 2 : 1;

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
        /// <summary>The card's width (full width at Pad, or right of the art on Compact) and its top edge.</summary>
        public float TransportW => MathF.Max(0f, W - TransportLeft - Pad);
        public float TransportTop => H - Pad - TransportH;
        /// <summary>Each side of the centred cluster: the "Playing from" column and the right cluster split it equally.</summary>
        public float TransportSideW => MathF.Max(0f, (TransportW - 2f * TransportPadX - TransportCentreW) * 0.5f);
        /// <summary>The volume slider shows where the class offers it (<see cref="ShowVolume"/>) AND the right cluster still fits
        /// beside it; otherwise the mute glyph alone — the cluster never runs off the card.</summary>
        public bool VolumeFits => ShowVolume && TransportSideW >= TransportRightMinW + TransportRightGap + VolumeSliderW;

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
        public float CaptionBottom => TransportH + Pad + CaptionAboveTransport;
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
        /// <summary>Compact: the transport card sits RIGHT of the art, not under it (V-U23). Everywhere else it spans the width at Pad.</summary>
        public float TransportLeft => Aspect == Aspect.Compact ? PadX + HeroArt + Pad : Pad;
        /// <summary>The face's right inset while the gallery is open (the prototype's <c>.pane .stg { right: 492px }</c>) — THIS
        /// layout's gallery width plus the two gutters (Ultrawide's docked column is 19 % of W, not 444 — V-U45).</summary>
        public float FaceRight(bool galleryOpen) => galleryOpen && ShowGallery && Aspect != Aspect.Portrait ? GalleryW + 2f * Pad : 0f;
        /// <summary>The pane's width from its two edges; never negative.</summary>
        public float PaneW => MathF.Max(0f, W - PaneX - PaneRight);

        /// <summary>The lyrics pane's type: the authored 36 DIP (<c>Lyrics.Surface.Timed(large: true)</c>) grown on a big
        /// stage to min(4.6 % of H, the pane's width / 30), an even size, clamped 36–64. The board (1920×1080, a 1084-DIP
        /// pane) stays at 36; a 2560×1080 ultrawide reads 50; a 1400-DIP-tall stage with a wide pane reaches 64.</summary>
        public const float LyricsTypeMin = 36f, LyricsTypeMax = 64f;
        public float LyricsTypeSize => Math.Clamp(MathF.Round(MathF.Min(0.046f * H, PaneW / 30f) * 0.5f) * 2f, LyricsTypeMin, LyricsTypeMax);
        /// <summary><see cref="LyricsTypeSize"/> over the authored size — what the lyrics view scales its row metrics by.</summary>
        public float LyricsTypeScale => LyricsTypeSize / LyricsTypeMin;
        public float PaneH => MathF.Max(0f, H - PaneTop - PaneBottom);

        /// <summary>Title type per aspect and mode: the hero reads TitleLarge 40/52, the thumb Subtitle 20/28; small
        /// classes step down (Title 28/36 · 24/30).</summary>
        public (float Size, float Line) TitleFont(Mode mode)
        {
            if (mode == Mode.Visualizer) return (20f, 28f);
            return Aspect switch { Aspect.Portrait => (28f, 36f), Aspect.Compact => (24f, 30f), _ => (40f, 52f) };
        }
        public (float Size, float Line) MetaFont(Mode mode)
            => mode != Mode.Visualizer && (Aspect is Aspect.Desktop or Aspect.Ultrawide) ? (18f, 24f) : (14f, 20f);

        /// <summary>A monotone "how much is on screen" score for the narrowing-never-adds test: the five affordance flags and the
        /// transport height. The hero is EXCLUDED on purpose — it is a per-class formula (0.28·W vs Ultrawide's 0.21·W) and
        /// legitimately shrinks when a wider window promotes to Ultrawide (V-U24).</summary>
        public int Richness => (ShowPane ? 1 : 0) + (ShowChips ? 1 : 0) + (ShowGallery ? 1 : 0) + (ShowVolume ? 1 : 0)
            + (IconOnlySelector ? 0 : 1) + (int)(TransportH * 0.01f);
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

    // ── 4. tone arithmetic (alphas and the cross-fade; the colours are Design.StageInk's) ────────────────────────────

    public static class Tone
    {
        /// <summary>The accent cross-fade on a track change (the prototype's <c>transition: --acc 1s</c>, tightened to the
        /// Fluent "slow" neighbourhood so a skip-skip-skip never lags the art).</summary>
        public const float CrossFadeMs = 600f;
        /// <summary>The scrim over the backdrop: deep under lyrics/queue/artist, light under a face.</summary>
        public const float ScrimA = 0.56f, ScrimVisualizerA = 0.22f;
        /// <summary>The caption's inks: unsung glyphs, the previous and next context lines (alphas over the stage ink), the
        /// active line's accent bloom (its layer opacity and blur σ), the soft veil's centre, and the rise every line hand-off
        /// travels (in from below, out above).</summary>
        public const float CaptionUnsungA = 0.45f, CaptionPrevA = 0.35f, CaptionNextA = 0.45f;
        public const float CaptionBloomA = 0.55f, CaptionBloomSigma = 10f, CaptionVeilA = 0.40f, CaptionRiseDip = 16f;
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
        /// stage today (Rail.UI.cs:253-255, "a track with no lyrics still opens a full stage") and so does every door here (V-U53).</summary>
        public static bool CanEnter(bool videoFullscreen) => !videoFullscreen;
    }

    // ── 6. the caption's clock view (fed the lyrics view's OWN resolves — Lyrics.ResolveLine / AdvancePastInterlude) ──

    public static class Caption
    {
        /// <summary>What the caption hangs on at <paramref name="nowMs"/>. <c>Anchor</c> is the line the view centres on
        /// (−1 = before the first line); <c>Dots</c> says the break's breathing dots stand in for the active line.
        /// <paramref name="leadLine"/> is the lyrics view's lead-resolved line (<c>ResolveLine(now + LeadMs)</c>);
        /// <paramref name="gapStart"/>/<paramref name="gapEnd"/> the real break <c>AdvancePastInterlude</c> reported for it
        /// (equal ⇒ none). The dots hold the slot from the sung-out point until the NEXT line resolves on the same lead, so
        /// the hand-off is dots → next line with nothing between (never the finished line flashing back, never a line-synced
        /// line lit before it is sung). A long intro (≥ <paramref name="minGapMs"/>) breathes the same dots toward line 0.</summary>
        public static (int Anchor, bool Dots) View(int leadLine, long gapStart, long gapEnd, long firstStartMs, long nowMs, long leadMs, long minGapMs)
        {
            if (leadLine < 0) return (-1, firstStartMs >= minGapMs && nowMs + leadMs < firstStartMs);
            return (leadLine, gapEnd > gapStart && nowMs >= gapStart && nowMs + leadMs < gapEnd);
        }

        /// <summary>One int for the view (the host's ONE signal — a re-render only when the line or the break edge moves).</summary>
        public static int Pack(int anchor, bool dots) => ((anchor + 1) << 1) | (dots ? 1 : 0);
        public static int AnchorOf(int packed) => (packed >> 1) - 1;
        public static bool DotsOf(int packed) => (packed & 1) != 0;

        /// <summary>The three slots' line indices (−1 = empty): the previous line above, the active line (none while the dots
        /// stand in), the next line below. In a break the previous slot is the line just sung. Without context (Compact, a
        /// short window) only the centre shows.</summary>
        public static (int Prev, int Centre, int Next) Slots(int anchor, bool dots, bool context, int count)
        {
            int centre = dots ? -1 : Valid(anchor, count);
            int prev = context ? Valid(dots ? anchor : anchor - 1, count) : -1;
            int next = context ? Valid(anchor + 1, count) : -1;
            return (prev, centre, next);

            static int Valid(int i, int n) => (uint)i < (uint)n ? i : -1;
        }
    }
}
