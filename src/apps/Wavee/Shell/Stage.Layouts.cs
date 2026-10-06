// ── Shell/Stage.Layouts.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// The visualizer LAYOUT axis of the fullscreen stage: Stage.VizLayout (Card · Large art · Centered · Artist), Stage.SpectrumStyle,
// Stage.HeroState, Stage.PaneKind, Stage.Look (what is drawn), Stage.LayoutRules (the pure rules), Stage.HeroRules (the artist
// hero's pure rules) and the Layout allocator's per-look geometry (a partial of Stage.Layout)
//
// Role: CORE
// Owner: K
// Wave: 7
// Budget: 520 lines
// Spec: docs/plans/wavee/visualizer-layouts-implementation.md (the boards Main / Centered / Options / Hero / HeroLyrics.dc.html)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// ONE NEW AXIS, THREE PURE RULES. The stage always drew a "Card" (the face full-bleed, a thumb in an acrylic card, the gallery
// pane). The layout axis adds three more arrangements of the SAME stage — Large art (the cover beside the lyrics), Centered
// (a ring around the cover), Artist (the artist's header photo full-bleed) — without a new Visualizer.Kind: a layout is not a
// face. `LayoutRules` decides what is drawn (effective layout, spectrum, pane, caption, lease), `HeroRules` what the artist hero
// knows, and the Layout partial turns a `Look` into every DIP the renderer lays out (the boards' 1440×900 numbers as fractions).
//
// Rules: `System`-only — no Element, no signal, no entity read — so Wavee.Tests drives the real arithmetic. No allocation (P8);
// no LINQ, no closures, no async, no boxing (P9). Outside Visualizer mode every look is the Card look, so the Mode-based members
// of Stage.Layout keep answering exactly as before.

namespace Wavee;

public static partial class Stage
{
    // ── 1. vocabulary ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The visualizer layout. PERSISTED ints (Platform.Keys.StageLayout) — append only.</summary>
    public enum VizLayout : byte { Card = 0, LargeArt = 1, Centered = 2, Artist = 3 }

    /// <summary>The spectrum drawn by the non-Card layouts. PERSISTED ints (Platform.Keys.StageSpectrum) — append only.</summary>
    public enum SpectrumStyle : byte { Bars = 0, Ring = 1, Line = 2, Off = 3 }

    /// <summary>What the Artist layout knows about the playing artist's header image (not persisted).</summary>
    public enum HeroState : byte { Pending = 0, Header = 1, None = 2 }

    /// <summary>Which pane PaneHost shows — the KeepAlive token, so the one lyrics instance serves Lyrics mode and the layouts.</summary>
    public enum PaneKind : byte { None = 0, Lyrics = 1, Queue = 2, Artist = 3 }

    /// <summary>WHAT IS DRAWN: the mode, the effective layout (Card outside Visualizer mode), the spectrum actually drawn
    /// (<see cref="LayoutRules.Spectrum"/> already applied), whether the Artist layout carries the lyrics pane, and whether the
    /// lyric caption line shows. The one value every geometry method of <see cref="Layout"/> takes.</summary>
    public readonly record struct Look(Mode Mode, VizLayout Eff, SpectrumStyle Spectrum, bool HeroLyrics, bool Caption)
    {
        /// <summary>The look of a mode that draws no layout (Lyrics / Queue / Artist panes, or Visualizer in Card).</summary>
        public static Look Plain(Mode mode, bool caption = false) => new(mode, VizLayout.Card, SpectrumStyle.Off, false, caption);
        /// <summary>The stage draws one of the three non-Card layouts.</summary>
        public bool IsLayout => Eff != VizLayout.Card;
    }

    // ── 2. the layout rules ─────────────────────────────────────────────────────────────────────────────────────────

    public static class LayoutRules
    {
        public const int Count = 4, SpectrumCount = 4;
        public static int Coerce(int stored) => (uint)stored < (uint)Count ? stored : (int)VizLayout.Card;
        public static int CoerceSpectrum(int stored) => (uint)stored < (uint)SpectrumCount ? stored : (int)SpectrumStyle.Bars;
        public static int ClampDim(int dim) => Math.Clamp(dim, 0, 100);

        /// <summary>The layout actually drawn: Compact always draws Card (no room); Artist without a header draws Large art.
        /// <see cref="HeroState.Pending"/> (the overview is in flight) STAYS Artist: the hero shows the blurred cover until the
        /// answer lands, so a first-time artist never flips Large art → Artist a second later.</summary>
        public static VizLayout Effective(VizLayout stored, Aspect aspect, HeroState hero)
            => aspect == Aspect.Compact ? VizLayout.Card : stored == VizLayout.Artist && hero == HeroState.None ? VizLayout.LargeArt : stored;

        /// <summary>The WHAT-IS-DRAWN value for a mode: the layout applies only in Visualizer mode; elsewhere (and in Card) it is
        /// the plain look of the mode.</summary>
        public static Look LookOf(Mode mode, VizLayout effective, SpectrumStyle storedSpectrum, bool heroLyricsPref, bool caption)
        {
            if (mode != Mode.Visualizer || effective == VizLayout.Card) return Look.Plain(mode, caption);
            return new Look(mode, effective, Spectrum(effective, storedSpectrum), effective == VizLayout.Artist && heroLyricsPref, caption);
        }

        /// <summary>The face is mounted in every layout but Artist (the only layout that REPLACES the visualizer): full-bleed in Card,
        /// as the dimmed backdrop behind the cover and lyrics in Large art and Centered ("every visualizer works with every layout").</summary>
        public static bool ShowsFace(VizLayout eff) => eff != VizLayout.Artist;
        /// <summary>The face runs BEHIND a foreground (Large art, Centered) rather than being the picture.</summary>
        public static bool FaceIsBackdrop(VizLayout eff) => eff == VizLayout.LargeArt || eff == VizLayout.Centered;

        /// <summary>The CONFLICT RULE: behind a layout the visualizer is ATMOSPHERE only — the layout owns the cover, the title and
        /// the lyrics. A face that draws none of them (<see cref="Visualizer.Catalog.NowPlayingOf"/>) runs as chosen; one whose
        /// now-playing part is optional (<see cref="Visualizer.Catalog.HasAmbient"/>: Bloom, Ring) runs as its ambient variant
        /// (<see cref="FaceAmbient"/>, the cover dropped); every other (Verse, Type, Mosaic, Spotlight) falls back to Bloom's ambient
        /// clouds. Card (and Artist) never substitute.</summary>
        public static Visualizer.Kind BackdropKind(VizLayout eff, Visualizer.Kind kind)
            => FaceIsBackdrop(eff) && Visualizer.Catalog.DrawsNowPlaying(kind) && !Visualizer.Catalog.HasAmbient(kind) ? Visualizer.Kind.Bloom : kind;

        /// <summary>The mounted face drops its own now-playing parts (cover) — exactly when it runs behind a layout.</summary>
        public static bool FaceAmbient(VizLayout eff) => FaceIsBackdrop(eff);

        /// <summary>The legibility scrim ABOVE the backdrop face, under the cover / lyrics (Stage's StageInk veil, so it follows
        /// light / dark): 0.42 behind Large art and Centered (no cover-carrying face runs there any more), 0 elsewhere.</summary>
        public static float BackdropScrim(VizLayout eff, Visualizer.Kind kind) => FaceIsBackdrop(eff) ? 0.42f : 0f;

        /// <summary>[ / ] step the FACE in every layout that draws one (the backdrop changes); only Artist steps layouts.</summary>
        public static bool KeysStepFaces(VizLayout eff) => eff != VizLayout.Artist;
        public static bool ShowsNowPlayingCard(VizLayout eff) => eff == VizLayout.Card;
        public static bool GalleryAllowed(VizLayout eff) => eff == VizLayout.Card;

        /// <summary>The spectrum drawn: none for Card (the face is the picture); Ring needs a cover at its centre, which the
        /// Artist layout does not have (a hairline photo overlay), so Artist draws Line.</summary>
        public static SpectrumStyle Spectrum(VizLayout eff, SpectrumStyle stored)
            => eff == VizLayout.Card ? SpectrumStyle.Off : eff == VizLayout.Artist && stored == SpectrumStyle.Ring ? SpectrumStyle.Line : stored;

        /// <summary>What the lease needs: Card → the face's needs; Large art / Centered → the (substituted) backdrop face's needs
        /// plus the spectrum when a strip / ring is drawn; Artist → the spectrum, else the LEVEL the base Field breathes with.
        /// Lyrics / Covers needs add no tier (the layout's own pane and cover feed them).</summary>
        public static Visualizer.Need NeedsOf(VizLayout eff, SpectrumStyle stored, Visualizer.Kind kind)
            => eff == VizLayout.Card ? Visualizer.Catalog.NeedsOf(kind)
             : FaceIsBackdrop(eff) ? Visualizer.Catalog.NeedsOf(BackdropKind(eff, kind)) | (Spectrum(eff, stored) == SpectrumStyle.Off ? Visualizer.Need.None : Visualizer.Need.Spectrum)
             : Spectrum(eff, stored) == SpectrumStyle.Off ? Visualizer.Need.Level : Visualizer.Need.Spectrum;

        /// <summary>The pane: the mode's pane outside Visualizer mode; in Visualizer mode the LYRICS pane for Large art and for
        /// Artist with "lyrics over the image" (<paramref name="heroLyrics"/> = <see cref="Look.HeroLyrics"/>).</summary>
        public static PaneKind Pane(Mode mode, VizLayout eff, bool heroLyrics) => mode switch
        {
            Mode.Lyrics => PaneKind.Lyrics,
            Mode.Queue => PaneKind.Queue,
            Mode.Artist => PaneKind.Artist,
            _ => eff == VizLayout.LargeArt || (eff == VizLayout.Artist && heroLyrics) ? PaneKind.Lyrics : PaneKind.None,
        };

        /// <summary>The lyric caption is the Card scaffold's: Card keeps <see cref="ModeRules.ShowsCaption"/>; in Visualizer mode the
        /// layouts never draw it (Large art / Centered own the title and lyrics — the stage's big caption line ran through the
        /// Centered title; Artist has its pane, or nothing).</summary>
        public static bool ShowsCaption(Mode mode, VizLayout eff, bool overlayOn, bool hasTimed, bool paneShown, Visualizer.Kind kind)
            => (mode != Mode.Visualizer || eff == VizLayout.Card) && ModeRules.ShowsCaption(mode, overlayOn, hasTimed, paneShown, kind);

        /// <summary>The previous / next context lines: Centered shows the active line alone.</summary>
        public static bool CaptionContext(VizLayout eff, bool layoutContext) => eff == VizLayout.Centered ? false : layoutContext;

        /// <summary>[ / ] in a non-Card layout step the STORED layout through the three non-Card layouts (Card is entered and left
        /// through the options popover or G; from Card the keys keep stepping faces).</summary>
        public static VizLayout Step(VizLayout stored, int delta)
        {
            int i = stored == VizLayout.Card ? 0 : (int)stored - 1;   // 0..2 over LargeArt, Centered, Artist
            int next = (((i + delta) % 3) + 3) % 3;
            return (VizLayout)(next + 1);
        }

        /// <summary>The scrim over the backdrop: Card keeps <see cref="Tone.ScrimFor"/>; the layouts sit under lyrics / a photo, so
        /// they take the deep (Lyrics-mode) depth.</summary>
        public static float ScrimFor(Mode mode, VizLayout eff, Visualizer.Kind kind)
            => mode == Mode.Visualizer && eff != VizLayout.Card ? Tone.ScrimA : Tone.ScrimFor(mode, kind);

        /// <summary>The base Field's opacity: near-full under a Card face, breathing (the Lyrics-mode look) under a layout.</summary>
        public static float BaseFieldFor(float low, Mode mode, VizLayout eff)
            => Visualizer.Field.BaseOpacity(low, mode == Mode.Visualizer && ShowsFace(eff));

        /// <summary>The options popover's 2×2 layout grid: the cursor after an arrow key (Left 37, Up 38, Right 39, Down 40),
        /// wrapping within its row / column; any other key leaves it.</summary>
        public static int CardNav(int index, int keyCode)
        {
            int row = index >> 1, col = index & 1;
            switch (keyCode)
            {
                case 37: col ^= 1; break;
                case 39: col ^= 1; break;
                case 38: row ^= 1; break;
                case 40: row ^= 1; break;
                default: return index;
            }
            return (row << 1) | col;
        }
    }

    // ── 3. the artist hero's rules ──────────────────────────────────────────────────────────────────────────────────

    public static class HeroRules
    {
        /// <summary>How many billed artists are asked for a header.</summary>
        public const int MaxBilled = 3;
        /// <summary>One ping-pong of the Ken Burns loop, the keyframe rows' cadence, the photo cross-fade, how early the next
        /// track's photo is decoded under the hero.</summary>
        public const float SpanMs = 40_000f, MotionHz = 30f, FadeMs = 900f, PrefetchAheadMs = 10_000f;
        /// <summary>Spotify's header sources top out at 1920 wide for most artists (a few offer 2660); the decode never asks for more.</summary>
        public const int DecodeMax = 1920;
        public const float ScaleFrom = 1.03f, ScaleTo = 1.10f;
        /// <summary>The prototype's 0.85 foot alpha is dim 75 (<see cref="FootAlpha"/>).</summary>
        public const int DefaultDim = 75;

        /// <summary>1.03 → 1.10, ±2.5 % of the width across (alternating by seed = the artist slot), a hashed ±1 % drift.</summary>
        public static Visualizer.Covers.Spot.KenBurns Pan(int seed)
            => new(ScaleFrom, ScaleTo, (seed & 1) == 0 ? 0.025f : -0.025f, (Visualizer.Bands.Hash(seed, 0x5B07) - 0.5f) * 0.02f);

        /// <summary>The bottom scrim's foot alpha from the 0–100 dimming: 0.40 … 1.00.</summary>
        public static float FootAlpha(int dim) => 0.40f + 0.60f * (LayoutRules.ClampDim(dim) / 100f);

        /// <summary>The decode width in device px for a stage <paramref name="wDip"/> × <paramref name="hDip"/> at this scale: WIC
        /// fits the picture INSIDE a square box of that size, so a 16:9 source needs max(W, H·16/9) to fill the stage's cover-fit,
        /// and the Ken Burns zoom (<see cref="ScaleTo"/>) enlarges it again; never past <see cref="DecodeMax"/>.</summary>
        public static int DecodePx(float wDip, float hDip, float scale)
        {
            float need = MathF.Max(wDip, hDip * 16f / 9f) * MathF.Max(1f, scale) * ScaleTo;
            return Math.Clamp((int)MathF.Ceiling(need), 320, DecodeMax);
        }

        /// <summary>The state and the slot index of the header to show, walking the billed artists IN ORDER: an artist whose
        /// overview has not answered stops the walk at <see cref="HeroState.Pending"/> (the photo and the name never switch
        /// from artist #2 to #1 when #1 lands); a known artist WITH a header wins; known without one is skipped; no artist at
        /// all (a local file) or none with a header → <see cref="HeroState.None"/>. <c>known[i]</c> = Knows(Header),
        /// <c>has[i]</c> = the header id is not empty.</summary>
        public static (HeroState State, int Index) Pick(ReadOnlySpan<bool> known, ReadOnlySpan<bool> has)
        {
            for (int i = 0; i < known.Length; i++)
            {
                if (!known[i]) return (HeroState.Pending, -1);
                if (i < has.Length && has[i]) return (HeroState.Header, i);
            }
            return (HeroState.None, -1);
        }

        /// <summary>The next track's header is worth decoding under the hero this close to the end.</summary>
        public static bool PrefetchDue(long positionMs, long durationMs) => durationMs > 0 && durationMs - positionMs <= PrefetchAheadMs;
    }

    // ── 4. the allocator's per-look geometry ────────────────────────────────────────────────────────────────────────

    public readonly partial record struct Layout
    {
        // authored constants: the boards' 1440×900 numbers as fractions of the stage
        public const float LargeArtFrac = 0.34f, LargeArtMax = 440f, LargeArtGapFrac = 0.06f, LargeArtPadFrac = 0.06f, LargeArtTextGap = 22f, LargeArtTitleGap = 6f;
        public const float CenteredCoverFrac = 0.355f, CenteredCoverWFrac = 0.25f, CenteredCoverMin = 200f, CenteredCoverMax = 480f, RingBoxRatio = 1.6875f;
        public const float RingRadiusRatio = 0.725f, RingBarW = 0.0125f, RingLen0 = 0.019f, RingGain = 0.12f;
        public const float CenteredTitleGap = 4f, CenteredMetaGap = 10f, CenteredCaptionLine = 32f, CenteredCaptionMaxW = 920f;
        public const float HeroPadFrac = 0.0445f, HeroNameFrac = 0.124f, HeroNameMin = 56f, HeroNameMax = 160f, HeroNameMaxWFrac = 0.7f, HeroLyricsPaneFrac = 0.54f;
        public const float HeroCoverSize = 64f, HeroLyricsCoverSize = 48f, HeroRowBottomGap = 28f, HeroNameGap = 18f, HeroEyebrowH = 16f;
        public const float StripBarsFrac = 0.13f, StripBarsMin = 72f, StripBarsMax = 160f;
        public const float StripLineFrac = 0.062f, StripLineMin = 36f, StripLineMax = 72f, StripLyricsFrac = 0.04f, StripLyricsMin = 28f, StripLyricsMax = 48f;
        public const float StripGutter = 8f, StripPadFrac = 0.03f, ContentGap = 16f;

        /// <summary>The Artist layout's size unit: H / 900 clamped 1–1.5 (the covers and the name's gaps scale by it).</summary>
        public float UnitK => Math.Clamp(H / 900f, 1f, 1.5f);

        // ── the spectrum strip ──
        /// <summary>The strip's height: 0 for Off, Ring (the ring is drawn around the cover) and Card. Bars use the Main board's
        /// 120-DIP strip (0.13 H); Line the hero boards' 56 (0.062 H) — 36 under the lyrics pane (0.04 H).</summary>
        public float StripH(in Look k)
        {
            if (!k.IsLayout || k.Spectrum is SpectrumStyle.Off or SpectrumStyle.Ring) return 0f;
            if (k.Spectrum == SpectrumStyle.Bars) return Math.Clamp(Q4(StripBarsFrac * H), StripBarsMin, StripBarsMax);
            return k.HeroLyrics ? Math.Clamp(Q4(StripLyricsFrac * H), StripLyricsMin, StripLyricsMax) : Math.Clamp(Q4(StripLineFrac * H), StripLineMin, StripLineMax);
        }
        /// <summary>The strip's left/right inset: the Artist boards' pad (64), 3 % elsewhere.</summary>
        public float StripPadX(in Look k) => k.Eff == VizLayout.Artist ? PadXFor(in k) : Q4(StripPadFrac * W);
        /// <summary>The strip's top edge (the transport's gutter when there is none).</summary>
        public float StripTop(in Look k) => StripH(in k) > 0f ? TransportTop - StripGutter - StripH(in k) : TransportTop - Pad;
        public (float X, float Y, float W, float H) StripRect(in Look k)
        {
            float pad = StripPadX(in k), sh = StripH(in k);
            return (pad, TransportTop - StripGutter - sh, MathF.Max(0f, W - 2f * pad), sh);
        }

        /// <summary>The look's horizontal margin: Large art 6 % of W, the Artist boards' 64.</summary>
        public float PadXFor(in Look k) => k.Eff switch
        {
            VizLayout.LargeArt => Q4(LargeArtPadFrac * W),
            VizLayout.Artist => MathF.Max(Pad, MathF.Round(HeroPadFrac * W / ArtQuantum) * ArtQuantum),
            _ => PadX,
        };

        /// <summary>The vertical room the centred layouts use: under the top bar, above the strip (or the transport's gutter).</summary>
        float RegionTopFor(in Look k) => TopBarH + 8f;
        public float ContentBottom(in Look k) => StripH(in k) > 0f ? StripTop(in k) - ContentGap : TransportTop - Pad;

        // ── the cover ──
        /// <summary>The cover node's size per look. Card = the Mode-based sizes (<see cref="CoverSize(Mode)"/>).</summary>
        public float CoverSize(in Look k) => k.Eff switch
        {
            VizLayout.LargeArt => k.Spectrum == SpectrumStyle.Ring ? Q4(LargeArtSlot(in k) / RingBoxRatio) : LargeArtSlot(in k),
            VizLayout.Centered => CenteredCover(in k),
            VizLayout.Artist => (k.HeroLyrics ? HeroLyricsCoverSize : HeroCoverSize) * UnitK,
            _ => CoverSize(k.Mode),
        };
        public float CoverX(in Look k) => k.Eff switch
        {
            VizLayout.LargeArt => Aspect == Aspect.Portrait ? (W - LargeArtSlot(in k)) * 0.5f + RingInset(in k) : PadXFor(in k) + RingInset(in k),
            VizLayout.Centered => (W - CoverSize(in k)) * 0.5f,
            VizLayout.Artist => PadXFor(in k),
            _ => CoverX(k.Mode),
        };
        public float CoverY(in Look k) => k.Eff switch
        {
            VizLayout.LargeArt => LargeArtBlockTop(in k) + RingInset(in k),
            VizLayout.Centered => CenteredBlockTop(in k) + (CenteredSlot(in k) - CoverSize(in k)) * 0.5f,
            VizLayout.Artist => k.HeroLyrics ? TopBarH + 16f : StripTop(in k) - HeroRowBottomGap - CoverSize(in k),
            _ => CoverY(k.Mode),
        };
        /// <summary>The cover's offset inside its slot: the ring shrinks the cover to its box / 1.6875, centred in the slot.</summary>
        float RingInset(in Look k) => k.Eff == VizLayout.LargeArt && k.Spectrum == SpectrumStyle.Ring ? (LargeArtSlot(in k) - CoverSize(in k)) * 0.5f : 0f;

        /// <summary>The ring's box (window DIP): the slot the cover and its ring share, 1.6875 × the cover — Centered (when the
        /// spectrum is Ring) and Large art (the cover's own slot). Empty when no ring is drawn.</summary>
        public (float X, float Y, float Size) RingBox(in Look k)
        {
            if (k.Spectrum != SpectrumStyle.Ring) return default;
            if (k.Eff == VizLayout.LargeArt) return (CoverX(in k) - RingInset(in k), LargeArtBlockTop(in k), LargeArtSlot(in k));
            if (k.Eff == VizLayout.Centered) return ((W - CenteredSlot(in k)) * 0.5f, CenteredBlockTop(in k), CenteredSlot(in k));
            return default;
        }

        // ── Large art ──
        float LargeArtRegionH(in Look k) => MathF.Max(0f, ContentBottom(in k) - RegionTopFor(in k));
        float LargeArtSlot(in Look k)
        {
            if (Aspect == Aspect.Portrait) return Math.Clamp(Q4(MathF.Min(0.5f * W, 0.3f * H)), 160f, 480f);
            float cap = LargeArtMax * MathF.Max(1f, H / 900f);
            float want = MathF.Min(LargeArtFrac * W, cap);
            float room = LargeArtRegionH(in k) - LargeArtTextGap - TitleBlockH(in k, 1);   // the cover plus its one-line text must fit
            return Math.Clamp(Q4(MathF.Min(want, room)), SmallHeroMin, HeroMax);
        }
        float LargeArtBlockH(in Look k) => LargeArtSlot(in k) + LargeArtTextGap + TitleBlockH(in k, TitleMaxLines(in k));
        float LargeArtBlockTop(in Look k)
        {
            float top = RegionTopFor(in k);
            if (Aspect == Aspect.Portrait) return TopBarH + 16f;
            return top + MathF.Max(0f, (LargeArtRegionH(in k) - LargeArtBlockH(in k)) * 0.5f);
        }

        // ── Centered ──
        float CenteredTextH(in Look k)
            => CenteredTitleGap + TitleFont(in k).Line + CenteredTitleGap + MetaFont(in k).Line + (k.Caption ? CenteredMetaGap + CenteredCaptionLine : 0f);
        /// <summary>The block's slot for the cover: the ring box when the ring is drawn, else the cover alone.</summary>
        public float CenteredSlot(in Look k) => k.Spectrum == SpectrumStyle.Ring ? CenteredCover(in k) * RingBoxRatio : CenteredCover(in k);
        float CenteredCover(in Look k)
        {
            float want = Math.Clamp(Q4(MathF.Min(CenteredCoverFrac * H, CenteredCoverWFrac * W)), CenteredCoverMin, CenteredCoverMax);
            float room = MathF.Max(0f, ContentBottom(in k) - RegionTopFor(in k) - CenteredTextH(in k));
            float fit = k.Spectrum == SpectrumStyle.Ring ? room / RingBoxRatio : room;
            return Math.Clamp(Q4(MathF.Min(want, fit)), SmallHeroMin, CenteredCoverMax);
        }
        float CenteredBlockH(in Look k) => CenteredSlot(in k) + CenteredTextH(in k);
        float CenteredBlockTop(in Look k)
        {
            float top = RegionTopFor(in k), room = ContentBottom(in k) - top;
            return top + MathF.Max(0f, (room - CenteredBlockH(in k)) * 0.5f);
        }

        // ── the titles ──
        public float TitleX(in Look k) => k.Eff switch
        {
            VizLayout.LargeArt => PadXFor(in k),
            VizLayout.Centered => Pad,
            VizLayout.Artist => PadXFor(in k) + CoverSize(in k) + (k.HeroLyrics ? 14f : 16f),
            _ => TitleX(k.Mode),
        };
        public float TitleY(in Look k) => k.Eff switch
        {
            VizLayout.LargeArt => Aspect == Aspect.Portrait ? CoverY(in k) + CoverSize(in k) + 16f : LargeArtBlockTop(in k) + LargeArtSlot(in k) + LargeArtTextGap,
            VizLayout.Centered => CenteredBlockTop(in k) + CenteredSlot(in k) + CenteredTitleGap,
            VizLayout.Artist => CoverY(in k) + MathF.Max(0f, (CoverSize(in k) - TitleBlockH(in k, 1)) * 0.5f),
            _ => TitleY(k.Mode),
        };
        public float TitleW(in Look k) => k.Eff switch
        {
            VizLayout.LargeArt => Aspect == Aspect.Portrait ? MathF.Max(160f, W - 2f * PadXFor(in k)) : LargeArtSlot(in k),
            VizLayout.Centered => MathF.Max(160f, W - 2f * Pad),
            VizLayout.Artist => MathF.Max(160f, (k.HeroLyrics ? HeroLyricsPaneFrac : 0.6f) * W - TitleX(in k)),
            _ => TitleW(k.Mode),
        };
        /// <summary>The title / meta column centres its lines (a TextEl has no text-align): Centered always, Portrait Large art.</summary>
        public bool TitlesCentered(in Look k) => k.Eff == VizLayout.Centered || (k.Eff == VizLayout.LargeArt && Aspect == Aspect.Portrait);
        /// <summary>The identity block shows the format / lyrics chips: Card (outside the thumb) and Large art; the hero boards have none.</summary>
        public bool ShowChipsFor(in Look k) => k.Eff is VizLayout.Centered or VizLayout.Artist ? false : ShowChips;

        public (float Size, float Line) TitleFont(in Look k) => k.Eff switch
        {
            VizLayout.LargeArt => Aspect == Aspect.Portrait ? (24f, 30f) : (34f, 40f),
            VizLayout.Centered => (30f, 36f),
            VizLayout.Artist => k.HeroLyrics ? (17f, 22f) : (22f, 28f),
            _ => TitleFont(k.Mode),
        };
        public (float Size, float Line) MetaFont(in Look k) => k.Eff switch
        {
            VizLayout.LargeArt => Aspect == Aspect.Portrait ? (14f, 20f) : (17f, 24f),
            VizLayout.Centered => (16f, 22f),
            VizLayout.Artist => k.HeroLyrics ? (14f, 20f) : (15f, 20f),
            _ => MetaFont(k.Mode),
        };
        /// <summary>The gap between the title, the meta line and the chips.</summary>
        public float TitleGapFor(in Look k) => k.Eff == VizLayout.LargeArt ? LargeArtTitleGap : k.Eff == VizLayout.Artist ? 2f : TitleGap;
        /// <summary>The identity block's height with <paramref name="lines"/> title lines: title · meta · (chips).</summary>
        public float TitleBlockH(in Look k, int lines)
        {
            if (!k.IsLayout) return TitleBlockH(k.Mode, lines);
            float gap = TitleGapFor(in k);
            return lines * TitleFont(in k).Line + gap + MetaFont(in k).Line + (ShowChipsFor(in k) ? gap + ChipsTop + ChipH : 0f);
        }
        public int TitleMaxLines(in Look k)
        {
            switch (k.Eff)
            {
                case VizLayout.LargeArt:
                    if (Aspect == Aspect.Portrait) return 1;
                    return LargeArtSlot(in k) + LargeArtTextGap + TitleBlockH(in k, 2) <= LargeArtRegionH(in k) ? 2 : 1;
                case VizLayout.Centered:
                case VizLayout.Artist:
                    return 1;
                default:
                    return TitleMaxLines(k.Mode);
            }
        }

        // ── the lyrics pane ──
        /// <summary>The pane's rect per look: the Mode's pane (Lyrics / Queue / Artist) outside a layout; Large art's column right
        /// of the cover; the Artist hero's column over the image (54 % of W).</summary>
        public (float X, float Y, float W, float H) PaneRect(in Look k)
        {
            switch (k.Eff)
            {
                case VizLayout.LargeArt:
                {
                    if (Aspect == Aspect.Portrait)
                    {
                        float top = TitleY(in k) + TitleBlockH(in k, 1) + ContentGap;
                        float pad = PadXFor(in k);
                        return (pad, top, MathF.Max(0f, W - 2f * pad), MathF.Max(0f, ContentBottom(in k) - top));
                    }
                    float x = PadXFor(in k) + LargeArtSlot(in k) + Q4(LargeArtGapFrac * W);
                    float y = RegionTopFor(in k);
                    return (x, y, MathF.Max(0f, W - x - PadXFor(in k)), MathF.Max(0f, ContentBottom(in k) - y));
                }
                case VizLayout.Artist when k.HeroLyrics:
                {
                    float pad = PadXFor(in k);
                    float top = TopBarH + 16f + HeroLyricsCoverSize * UnitK + 18f;
                    float w = Aspect == Aspect.Portrait ? W - 2f * pad : HeroLyricsPaneFrac * W - 2f * pad;
                    float bottom = StripTop(in k) - 8f;
                    return (pad, top, MathF.Max(0f, w), MathF.Max(0f, bottom - top));
                }
                default:
                    return (PaneX, PaneTop, PaneW, PaneH);
            }
        }
        /// <summary>The lyrics type scale for the pane a look gives it: the pane's width over its authored 30-char budget, as
        /// <see cref="LyricsTypeScale"/> reads it for the Lyrics mode's pane.</summary>
        public float LyricsTypeScaleFor(in Look k)
        {
            if (!k.IsLayout) return LyricsTypeScale;
            var pane = PaneRect(in k);
            float size = Math.Clamp(MathF.Round(MathF.Min(0.046f * H, pane.W / 30f) * 0.5f) * 2f, LyricsTypeMin, LyricsTypeMax);
            return size / LyricsTypeMin;
        }

        // ── the Artist hero's name ──
        public float HeroNameSize => Math.Clamp(Q4(HeroNameFrac * H), HeroNameMin, HeroNameMax);
        public float HeroNameLine => MathF.Round(HeroNameSize * 0.95f);
        public float HeroNameMaxW => HeroNameMaxWFrac * W;
        /// <summary>The distance from the stage's bottom edge to the BOTTOM of the name block (eyebrow + name): the identity row's
        /// top (the cover) minus the name gap — the block grows upward from it.</summary>
        public float HeroNameBottomOffset(in Look k) => H - (CoverY(in k) - HeroNameGap);

        // ── the lyric caption ──
        /// <summary>The caption block per look: Card keeps the allocator's (centred in the face's region, above the transport);
        /// Centered sits under the meta line (one line, no context).</summary>
        public float CaptionWFor(in Look k, bool galleryShown)
            => k.Eff == VizLayout.Centered ? MathF.Min(Q4(CaptionMaxWFrac * W), CenteredCaptionMaxW) : CaptionW(k.Mode, galleryShown);
        public float CaptionXFor(in Look k, bool galleryShown)
            => k.Eff == VizLayout.Centered ? (W - CaptionWFor(in k, galleryShown)) * 0.5f : CaptionX(k.Mode, galleryShown);
        public float CaptionBottomFor(in Look k)
            => k.Eff == VizLayout.Centered ? H - (CenteredBlockTop(in k) + CenteredBlockH(in k)) : CaptionBottom;
        public bool CaptionContextFor(in Look k) => LayoutRules.CaptionContext(k.Eff, CaptionShowsContext);
        /// <summary>The caption block's tallest height: Centered's single line vs Card's three-line block.</summary>
        public float CaptionBlockMaxHFor(in Look k) => k.Eff == VizLayout.Centered ? CaptionFont.Line * CaptionActiveMaxLines : CaptionBlockMaxH;

        // ── the ring (Centered / Large art) ──
        /// <summary>The ring's radius numbers for a cover of <paramref name="cover"/> DIP: inner radius, capsule width, rest length, gain.</summary>
        public static (float Radius, float Width, float Len0, float Gain) RingMetrics(float cover)
            => (RingRadiusRatio * cover, MathF.Max(2f, RingBarW * cover), RingLen0 * cover, RingGain * cover);
    }
}
