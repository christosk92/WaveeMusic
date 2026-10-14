// ── Themes/Classic/Style/ClassicRoles.cs — the roles of Wavee Classic (themes plan 6.1, T2a + T2b) ───────────────────────
//
// Classic's table IS today's `Design`: every number below was a literal or a rung in `Design.Type`, `Design.Size`,
// `Design.Dock` and `Design.Motion` before T2a, and every colour a recipe in `Design.Colors`, `Design.OnMedia`, `Design.StageArm`,
// `Design.Wash`, `Design.Accent` and the lyrics ink before T2b, copied as it stood, so a view that reads `R.*` gets exactly the value
// it read from `Design.*`. Nothing here reads a token (`Tok`), a pref or the app WHILE THE TABLE IS BUILT: the type, space and
// motion roles are plain numbers, and a colour role is a pair of values or a RECIPE (`ColorRole.By`) that reads the engine's live
// tokens only when a view reads the role. So building the table works at any moment, including while the assembly loads (see the
// module initializer below).
//
// The type roles keep the old doc's contract: every role resolves a SIZE, a LINE HEIGHT and a WEIGHT (its rung's unless it
// states them) and the weight policy is 400 and 600 with SIX documented divergences: the three display-face identity roles
// that keep 700 (ArtistDisplay, ArtistTitle, ArtistCompactTitle: the masthead voice, not UI labels) and the three that keep
// SemiLight 350 (PivotLabel, NpvLyric, StatHero: the same WinUI SemiLight the engine's own pivot control uses). A SEVENTH is a
// regression. PivotLabel (19/25) is the second and last sanctioned off-ramp size (the other is the picker label's own line box).

using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using ShellPalette = FluentGpu.Dsl.ShellPalette;
using ThemeKind = FluentGpu.Dsl.ThemeKind;
using TokenSet = FluentGpu.Dsl.TokenSet;
using Tok = FluentGpu.Dsl.Tok;

namespace Wavee.Themes.Classic;

/// <summary>The roles of Wavee Classic.</summary>
public static class ClassicRoles
{
    private const string DisplayFace = "Segoe UI Variable Display";

    /// <summary>Classic's role table. One immutable instance: the style publishes it on its definition and every read of
    /// <c>R</c> before a definition is installed falls back to it.</summary>
    public static WaveeStyleRoles Table { get; } = new(Type(), Ink(), Layer(), Space(), OnMedia(), StageArm(), Wash(), Lyrics(), Motion(), Case());

    /// <summary>Registers this table as the roles a read uses while no installed definition carries any (the boot steps ahead of
    /// the appearance seed, and a unit-test process that never boots the shell). Runs when the assembly loads, so core code
    /// never names a theme and the first read of <c>R</c> cannot see an empty baseline.</summary>
    [ModuleInitializer]
    internal static void RegisterAsBaseline() => R.SetBaseline(Table);

    private static TypeRoles Type()
    {
        // The pair of rungs a role keeps from the engine ramp: Caption 12/16, Body 14/20, BodyStrong 14/20/600, BodyLarge 18/24,
        // Subtitle 20/28/600, Title 28/36/600, TitleLarge 40/52/600, Display 68/92/600. The rest of each role is its deviation.
        var pickQuote = new TextRole(TypeRung.Title, Face: DisplayFace, Weight: 400, Tracking: -12f);
        return new TypeRoles(
            DisplayFace: DisplayFace,
            TrackTitle: new TextRole(TypeRung.BodyStrong),                                           // 14/20/600
            CardTitle: new TextRole(TypeRung.BodyStrong),                                            // the TrackTitle rung, named for the card
            TrackMeta: new TextRole(TypeRung.Caption, Secondary: true),                               // 12/16, secondary ink
            Eyebrow: new TextRole(TypeRung.Caption, Weight: 600, Tracking: 30f),                      // 12/16/600, 30/1000 em, never uppercased
            MicroMeta: new TextRole(TypeRung.Caption, Size: 11f, LineHeight: 15f),                    // 11/15
            DenseMeta: new TextRole(TypeRung.Body, Size: 13f, LineHeight: 18f),                       // 13/18
            DenseTitle: new TextRole(TypeRung.BodyStrong, Size: 13f, LineHeight: 18f),                // 13/18/600
            SheetTitle: new TextRole(TypeRung.BodyLarge, Size: 16f, LineHeight: 22f, Weight: 600),    // 16/22/600
            RailHeader: new TextRole(TypeRung.Subtitle),                                             // 20/28/600
            ModuleHeader: new TextRole(TypeRung.Subtitle, Face: DisplayFace, Tracking: -6f),          // 20/28/600, display face, -6
            PageHero: new TextRole(TypeRung.Title),                                                  // 28/36/600
            DetailHero: new TextRole(TypeRung.Title, Face: DisplayFace, Tracking: -20f),              // 28/36/600, display face, -20
            SurfaceDisplay: new TextRole(TypeRung.TitleLarge, Face: DisplayFace, Weight: 400, Tracking: -12f),   // 40/52/400, display face
            HeroTitle: new TextRole(TypeRung.TitleLarge, Face: DisplayFace, Weight: 600, Tracking: -12f),        // 40/52/600, display face
            ArtistDisplay: new TextRole(TypeRung.Display, Face: DisplayFace, Size: 84f, LineHeight: 96f, Weight: 700, Tracking: -28f, MinSize: 68f),
            ArtistTitle: new TextRole(TypeRung.TitleLarge, Face: DisplayFace, Size: 48f, LineHeight: 60f, Weight: 700, Tracking: -20f, MinSize: 40f),
            ArtistCompactTitle: new TextRole(TypeRung.Title, Face: DisplayFace, Size: 32f, LineHeight: 40f, Weight: 700, Tracking: -12f, MinSize: 28f),
            NowPlayingTitle: new TextRole(TypeRung.Subtitle),                                        // 20/28/600
            PickQuote: pickQuote,                                                                    // 28/36/400, display face, -12
            FoldTitle: pickQuote,                                                                    // the same cut, named for the Fold tile
            PivotLabel: new TextRole(TypeRung.BodyLarge, Face: DisplayFace, Size: 19f, LineHeight: 25f, Weight: 350, Tracking: -6f),
            NpvLyric: new TextRole(TypeRung.Subtitle, Face: DisplayFace, Weight: 350, Tracking: -6f), // 20/28/350, display face
            StatHero: new TextRole(TypeRung.Title, Face: DisplayFace, Weight: 350, Tracking: -6f),    // 28/36/350, display face
            // The Home facet row: 28/36 display words, 400 at rest and 600 selected, 8-DIP plates 40 tall, 24 apart, the first item
            // pulled -8 so its WORD sits on the page's leading edge; the compact form is one rung down with the plates 8 apart.
            FacetTitle: new FacetRole(28f, 36f, DisplayFace, 0f, 400, 600, new Edges4(8f, 2f, 8f, 2f), 40f, 24f, -8f),
            FacetCompact: new FacetRole(20f, 28f, DisplayFace, 0f, 400, 600, new Edges4(8f, 2f, 8f, 2f), 40f, 8f, -8f));
    }

    private static SpaceRoles Space() => new(
        SectionGap: 32f, SectionGapWide: 40f, PageMaxW: 1600f,
        ShelfCardMin: 148f, ShelfCardMax: 188f, GridGap: 12f,
        WideTileMin: 330f, WideTileMax: 440f, WideTileAspect: 16f / 9f,
        FadeChip: 16f, FadeShelf: 24f, FadeRail: 36f,
        FacePileFlyoutW: 280f, FacePileFlyoutH: 360f, FacePileListW: 264f, FacePileListH: 344f,
        DockReserve: 72f);   // the player bar's height (Design.Size.PlayerBarH)

    // Chips and small toggles barely move; buttons and pills a little; a round media affordance the most. Stagger: 40 ms per list
    // item (capped by Design.Entrance), 45 ms for the two lines of a drill-in masthead.
    private static MotionRoles Motion() => new(
        ScaleSubtle: new ScaleTier(1.02f, 0.98f),
        ScaleStandard: new ScaleTier(1.04f, 0.96f),
        ScaleEmphatic: new ScaleTier(1.07f, 0.92f),
        StaggerMs: 40f,
        MastheadStaggerMs: 45f);

    // Classic writes every label as written; sentence case is the Fluent register and a transform on a localized string is a bug.
    private static CaseRoles Case() => new(TextCase.AsWritten, TextCase.AsWritten, TextCase.AsWritten, TextCase.AsWritten);

    // ═════════════════════════════════════════════ THE COLOUR ROLES (T2b) ═════════════════════════════════════════════════════
    //
    // Every recipe below is the body of the `Design` expression it replaced, with `Tok.Theme == ThemeKind.Light` read as the KIND the
    // role is asked for (the facade passes `Tok.Theme`, so a live read is identical). A recipe reads the engine's LIVE tokens
    // (`Tok.FillSubtleTertiary`, `Tok.Palette.LightShell`, the accent ramp): the table is built before any token exists and evaluates
    // nothing until a view reads a role, so it stays valid while the assembly loads.

    private static TokenSet SetOf(ThemeKind k) => k == ThemeKind.Light ? Tok.Palette.Light : Tok.Palette.Dark;

    private static ShellPalette ShellOf(ThemeKind k) => k == ThemeKind.Light ? Tok.Palette.LightShell : Tok.Palette.DarkShell;

    /// <summary>THE zebra stripe. DARK derives it from the engine's SUBTLE-FILL ink at its quietest rung because the shell's own dark zebra is
    /// LITERALLY the hover fill (both 0x0F), so hovering a striped row moved the surface by five alpha steps and read as nothing at all. A
    /// stripe MUST be quieter than hover or the row has no hover. LIGHT takes the shell's own row ladder, which is the black-alpha stripe solved
    /// TOGETHER with the light hover/press rungs against the art-derived page tone.</summary>
    private static ColorF Zebra(ThemeKind k) => k == ThemeKind.Light ? ShellOf(k).RowZebra : Tok.FillSubtleTertiary;

    private static ColorF ShellGroundOf(ThemeKind k) => ColorScience.ShellGround(SetOf(k), k);

    private static ColorF ContentSurfaceOf(ThemeKind k) => ColorScience.ContentSurface(SetOf(k), k);

    private static LayerRoles Layer() => new(
        // The translucent MUX rungs. The authenticated shell no longer PAINTS them (every chrome band is a paint-site omission over the
        // ground) but they stay published verbatim: the palette contrast gates measure the tint through them.
        Toolbar: ColorRole.By(static k => ShellOf(k).Toolbar),
        Sidebar: ColorRole.By(static k => ShellOf(k).Sidebar),
        PlayerBar: ColorRole.By(static k => ShellOf(k).PlayerBar),
        FileArea: ColorRole.By(static k => ShellOf(k).FileArea),
        Content: ColorRole.By(static k => ShellOf(k).Content),
        ContentAlt: ColorRole.By(static k => ShellOf(k).ContentAlt),
        ShellGround: ColorRole.By(ShellGroundOf),
        ContentSurface: ColorRole.By(ContentSurfaceOf),
        ContentLayer: ColorRole.By(ColorScience.ContentLayer),
        FloatingChrome: ColorRole.By(ShellGroundOf),       // the two names mark intent (docked surface vs floating stand-in), not different colours
        FloatingPane: ColorRole.By(ContentSurfaceOf),
        RowZebra: ColorRole.By(Zebra),
        RowHover: ColorRole.By(static k => ShellOf(k).RowHover),
        RowPressed: ColorRole.By(static k => ShellOf(k).RowPressed),
        // Hover/press ON a striped row: the row state SOURCE-OVER the stripe, collapsed to ONE translucent fill (a row paints a single
        // Fill, never two stacked plates). `Over` is associative, so the merged rung composites pixel-identically to painting the two in
        // sequence. THE PRESS DIRECTION INVERTS BY POLARITY and BOTH arms are correct: LIGHT pressed (7.1%) is ABOVE hover (5.1%), black
        // ink deepening; DARK pressed (0x0A) is BELOW hover (0x0F), the white subtle-fill ladder's own shape. Do NOT "fix" one to match.
        RowHoverZebra: ColorRole.By(static k => ColorContrast.Over(ShellOf(k).RowHover, Zebra(k))),
        RowPressedZebra: ColorRole.By(static k => ColorContrast.Over(ShellOf(k).RowPressed, Zebra(k))),
        // "You are here" for a NAV row (accent role 2): THREE RUNGS THAT ONLY EVER GO UP. Rest is WinUI's own AccentSubtle; hover is the
        // standard hover veil composed OVER that plate (strictly stronger); pressed is the quieter press veil over the same plate.
        SelectedRest: ColorRole.By(static _ => Tok.AccentSubtle),
        SelectedHover: ColorRole.By(static _ => ColorContrast.Over(Tok.FillSubtleSecondary, Tok.AccentSubtle)),
        SelectedPressed: ColorRole.By(static _ => ColorContrast.Over(Tok.FillSubtleTertiary, Tok.AccentSubtle)),
        ChromeHover: ColorRole.By(static _ => Tok.FillSubtleSecondary),
        ChromePressed: ColorRole.By(static _ => Tok.FillSubtleTertiary),
        Badge: ColorRole.By(static _ => Tok.AccentDefault),
        // The concert base fills that are NOT token swaps: a fixed near-black in dark, the card's secondary fill in light.
        EventPlate: ColorRole.By(static k => k == ThemeKind.Dark ? ColorF.FromRgba(0x1C, 0x1C, 0x1E) : Tok.FillCardSecondary),
        EventHeroPlate: ColorRole.By(static k => k == ThemeKind.Dark ? ColorF.FromRgba(0x1B, 0x1B, 0x1D) : Tok.FillCardSecondary),
        // Artwork is OPAQUE content: explicit opaque neutrals, never a translucent theme brush.
        ArtPlaceholder: ColorRole.Pair(light: ColorF.FromRgba(0xF2, 0xF2, 0xF2), dark: ColorF.FromRgba(0x2A, 0x2A, 0x2A)));

    private static InkRoles Ink() => new(
        AccentAction: ColorRole.By(static _ => Tok.AccentDefault),
        AccentSelection: ColorRole.By(static _ => Tok.AccentDefault),
        AccentDecor: ColorRole.By(static _ => Tok.AccentTextPrimary),
        PremiumText: ColorRole.By(static k => k == ThemeKind.Light ? Tok.SystemFillSuccess : ColorF.FromRgba(0x1D, 0xB9, 0x54)),
        DataDot: ColorScience.DataDotInk);

    // The on-media literals are theme-INVARIANT by contract, so they are plain values and not token reads. (Properties, not fields: the
    // `Table` initializer above runs first, and a static field declared below it would still be empty when the table is built.)
    private static ColorF ScrimHoverC => ColorF.FromRgba(0, 0, 0, 190);
    private static ColorF ScrimPressedC => ColorF.FromRgba(0, 0, 0, 220);
    private static ColorF CoverScrimC => ColorF.FromRgba(0, 0, 0, 110);
    private static ColorF StrokeC => ColorF.FromRgba(255, 255, 255, 58);

    // The light on-media button ramp, derived from the on-media ink rather than hardcoded greys, so a palette preset that ever re-tints the
    // on-media whites carries the button's whole ramp with it (the old literals were 255/235/215 with (12,12,14) ink).
    private const float LightHoverDrop = 0.08f, LightPressedDrop = 0.157f;

    private static OnMediaLadder OnMedia() => new(
        ScrimRest: ColorRole.By(static _ => Tok.MediaScrim),
        ScrimHover: ColorRole.Fixed(ScrimHoverC),
        ScrimPressed: ColorRole.Fixed(ScrimPressedC),
        CoverScrim: ColorRole.Fixed(CoverScrimC),
        Stroke: ColorRole.Fixed(StrokeC),
        Ink: ColorRole.By(static _ => Tok.OnMediaPrimary),
        InkSecondary: ColorRole.By(static _ => Tok.OnMediaSecondary),
        InkTertiary: ColorRole.By(static _ => Tok.OnMediaTertiary),
        GlassRest: ColorRole.Fixed(ColorF.Transparent),
        GlassHover: ColorRole.By(static _ => Tok.OnMediaPrimary with { A = 0.10f }),
        GlassPressed: ColorRole.By(static _ => Tok.OnMediaPrimary with { A = 0.16f }),
        GlassPlate: ColorRole.By(static _ => Tok.OnMediaPrimary with { A = 0.14f }),
        GlassPlateHover: ColorRole.By(static _ => Tok.OnMediaPrimary with { A = 0.22f }),
        GlassPlatePressed: ColorRole.By(static _ => Tok.OnMediaPrimary with { A = 0.28f }),
        BackdropDim: ColorRole.Fixed(ColorF.FromRgba(8, 8, 10) with { A = 0.45f }),
        SpotlightInner: ColorRole.Fixed(ColorF.FromRgba(255, 255, 255, 46)),
        SpotlightMid: ColorRole.Fixed(ColorF.FromRgba(255, 255, 255, 20)),
        LightButton: ColorRole.By(static _ => Tok.OnMediaPrimary),
        LightButtonHover: ColorRole.By(static _ => ColorRamp.Darken(Tok.OnMediaPrimary, LightHoverDrop)),
        LightButtonPressed: ColorRole.By(static _ => ColorRamp.Darken(Tok.OnMediaPrimary, LightPressedDrop)),
        LightButtonInk: ColorRole.By(static _ => Tok.MediaStage),
        LightHoverDrop: LightHoverDrop,
        LightPressedDrop: LightPressedDrop);

    // ── the immersive stage's arms. The dark arm delegates to the on-media ladder VERBATIM (pinned by test); the light arm mirrors the ground
    //    and keeps the scrim ALPHAS (the sRGB transfer curve does the work: mixing toward black at a partial alpha destroys far more perceptual
    //    luminance than mixing toward white, so the light arm's alpha'd ink clears a HIGHER ratio than the dark arm already ships).

    private const float StageScrimBaseA = 0.46f;

    private static bool ArmDark(ThemeKind k) => k == ThemeKind.Dark;

    /// <summary>The colour every scrim alpha is mixed FROM and the opaque floor under the backdrop. Light takes the app's own canon achromatic
    /// light ground rather than minting a near-duplicate; achromatic on purpose, so the cover's own colour comes through the veil.</summary>
    private static ColorF ArmVeil(ThemeKind k) => ArmDark(k) ? Tok.MediaStage : ColorScience.PageToneNeutralLight;

    /// <summary>Light ink is <c>Tok.MediaStage</c>, an ON-MEDIA token and deliberately NOT <c>Tok.TextPrimary</c>: the stage's ink must stay
    /// opaque (the theme text rung is black at 0.894, a second, quieter ladder).</summary>
    private static ColorF ArmInk(ThemeKind k) => ArmDark(k) ? Tok.OnMediaPrimary : Tok.MediaStage;

    private static StageArmTable StageArm() => new(
        IsDark: ArmDark,
        Veil: ColorRole.By(ArmVeil),
        Floor: ColorRole.By(ArmVeil),   // the floor IS the veil: the code once painted the solid base and flashed near-white under the dark scrim
        Ink: ColorRole.By(ArmInk),
        InkSecondary: ColorRole.By(static k => ArmDark(k) ? Tok.OnMediaSecondary : Tok.MediaStage with { A = Tok.OnMediaSecondary.A }),
        InkTertiary: ColorRole.By(static k => ArmDark(k) ? Tok.OnMediaTertiary : Tok.MediaStage with { A = Tok.OnMediaTertiary.A }),
        GlassRest: ColorRole.Fixed(ColorF.Transparent),
        GlassHover: ColorRole.By(static k => ArmInk(k) with { A = 0.10f }),
        GlassPressed: ColorRole.By(static k => ArmInk(k) with { A = 0.16f }),
        GlassPlate: ColorRole.By(static k => ArmInk(k) with { A = 0.14f }),
        GlassPlateHover: ColorRole.By(static k => ArmInk(k) with { A = 0.22f }),
        GlassPlatePressed: ColorRole.By(static k => ArmInk(k) with { A = 0.28f }),
        ScrimRest: ColorRole.By(static k => ArmDark(k) ? Tok.MediaScrim : ArmVeil(k) with { A = Tok.MediaScrim.A }),
        ScrimHover: ColorRole.By(static k => ArmDark(k) ? ScrimHoverC : ArmVeil(k) with { A = ScrimHoverC.A }),
        ScrimPressed: ColorRole.By(static k => ArmDark(k) ? ScrimPressedC : ArmVeil(k) with { A = ScrimPressedC.A }),
        // The hairline inverts WITH the ink: a white hairline on a light plate is not a quiet ring, it is an absent one.
        Stroke: ColorRole.By(static k => ArmDark(k) ? StrokeC : ArmInk(k) with { A = StrokeC.A }),
        // Named ButtonFill: on a light stage the loudest affordance is a DARK disc. Same 0.08 / 0.157 ramp, mirrored direction.
        ButtonFill: ColorRole.By(static k => ArmDark(k) ? Tok.OnMediaPrimary : ArmInk(k)),
        ButtonFillHover: ColorRole.By(static k => ArmDark(k)
            ? ColorRamp.Darken(Tok.OnMediaPrimary, LightHoverDrop) : ColorRamp.Lighten(ArmInk(k), LightHoverDrop)),
        ButtonFillPressed: ColorRole.By(static k => ArmDark(k)
            ? ColorRamp.Darken(Tok.OnMediaPrimary, LightPressedDrop) : ColorRamp.Lighten(ArmInk(k), LightPressedDrop)),
        ButtonInk: ColorRole.By(static k => ArmDark(k) ? Tok.MediaStage : ArmVeil(k)),
        SkeletonBar: ColorRole.By(static k => ArmInk(k) with { A = 0.12f }),
        Plate: ColorRole.By(static k => ArmInk(k) with { A = 0.045f }),
        PlateHover: ColorRole.By(static k => ArmInk(k) with { A = 0.075f }),
        // The dark arm is the in-app base recipe; the light arm is its light-theme twin (WinUI's light AcrylicInAppFillColorBase).
        Card: static k => ArmDark(k)
            ? AcrylicSpec.InAppBase
            : new AcrylicSpec(ColorF.FromRgba(0xF3, 0xF3, 0xF3), 0f, 30f, 0.02f, 0.90f, ColorF.FromRgba(0xEE, 0xEE, 0xEE)),
        // Dark takes the CHROME accent as is. Light cannot (a lifted accent on a pale veil is a highlighter): it re-solves the SAME hue as INK
        // against the brightest ground this stage can produce, the plateau veil over a white cover, so it never goes illegible on a pale sleeve.
        AccentFrom: static (k, chrome) => ArmDark(k)
            ? chrome
            : ColorScience.TextInk(chrome, ThemeKind.Light, ColorContrast.Over(ArmVeil(k) with { A = StageScrimBaseA }, Tok.OnMediaPrimary)),
        SelectedTileWash: new ModePair<float>(Light: 0.08f, Dark: 0.12f),
        ScrimBaseA: StageScrimBaseA);

    // The shell material tint: LIGHT lifts the cover's TEXT role to a 5% whisper; DARK takes the tinted background role at 14%. Two different
    // ROLES of the grading, not one role at two alphas: a lifted background role in light is a pastel smear across the chrome.
    private const float TintAlphaLight = 0.05f, TintAlphaDark = 0.14f;

    private static ColorF LiftPayload(uint argb) => ColorScience.Lift(ColorScience.ToColor(argb));

    // Both arms of the artist blend wash follow the ladder (graded, payload, neutral). LIGHT lifts the hue toward the accent; DARK sinks it to
    // the dark background role, so the first frame already carries the record's hue at the brightness the grading will settle on.
    private static AccentLadder.Result BlendLadder(ThemeKind k, Scheme? pageScheme, uint payloadAccent, bool definite)
        => k == ThemeKind.Light
            ? AccentLadder.Resolve(
                new AccentLadder.Input(pageScheme is { } wp ? ColorScience.Lift(ColorScience.Accent(wp)) : null, payloadAccent, definite),
                null, Tok.AccentDefault, LiftPayload)
            : AccentLadder.Resolve(
                new AccentLadder.Input(pageScheme is { } dp ? ColorScience.BackgroundDark(dp) : null, payloadAccent, definite),
                null, ColorScience.BackgroundDark(ColorScience.Neutral), ColorScience.DarkFromPayload);

    // DARK carries roughly TWICE the light strength (the same colour reads far weaker over the dark ground; the light ground has less headroom
    // before a wash turns into a smudge). Pairs are (light, dark).
    private static WashRoles Wash() => new(
        // Never Transparent: transparent is premultiplied BLACK, and cross-fading into or out of it drags the ramp toward black.
        NeutralGround: ColorRole.By(static k => ShellGroundOf(k) with { A = 0.03f }),
        HeroAlpha: new ModePair<float>(0.055f, 0.10f),
        ShelfAlpha: new ModePair<float>(0.05f, 0.085f),
        HeroVeilPull: new ModePair<float>(0.16f, 0.24f),
        HeroVeilPeak: new ModePair<float>(0.42f, 0.78f),
        CoverMix: new ModePair<float>(0.10f, 0.18f),
        ConcertMediaMix: new ModePair<float>(0.18f, 0.30f),
        // The detail page's tone plane is a WHISPER over the material: 0.20 dark passes ~80% of dark Mica; light stays a little higher.
        PageTonePlaneAlpha: new ModePair<float>(0.30f, 0.20f),
        BlendWashTop: new ModePair<float>(0.20f, 0.30f),
        BlendWashBoundary: new ModePair<float>(0.06f, 0.08f),
        ShellTintAlpha: new ModePair<float>(TintAlphaLight, TintAlphaDark),
        PageToneFromHue: static (k, hue) => ColorScience.PageToneFromHue(hue, k),
        ShellTintFromScheme: static (k, scheme) => k == ThemeKind.Light
            ? ColorScience.Lift(ColorScience.ToColor(scheme.TextBase)) with { A = TintAlphaLight }
            : ColorScience.TintedDark(scheme) with { A = TintAlphaDark },
        BlendWashLadder: BlendLadder);

    private static LyricsInkRoles Lyrics() => new(
        ThemePlateAlpha: 0.92f,
        RingTrackThemeAlpha: 0.55f,
        RingTrackOnMediaAlpha: 0.30f,
        // On a dark stage the ink (a halo adds luminance), on a light stage the VEIL (blurred near-black under near-black subtracts and
        // reads as a smudge), and half of it survives on the light stage.
        BloomOnMedia: ColorRole.By(static k => ArmDark(k) ? ArmInk(k) : ArmVeil(k)),
        BloomScale: new ModePair<float>(0.5f, 1f));
}
