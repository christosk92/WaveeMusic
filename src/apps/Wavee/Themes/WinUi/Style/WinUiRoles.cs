// ── Themes/WinUi/Style/WinUiRoles.cs — the roles of the WinUI style (themes plan 5.4, 6.2, W1) ─────────────────────────────────
//
// Every number and colour below is read off the WinUI boards (W-Tokens first, then W-RowStates and the light boards W-HomeLight, W-AlbumLight,
// W-PlayerLight): the Mica base #202020 / #f3f3f3 under the title bar, the pane and the player, the content layer #282828 / #f9f9f9 (the stock
// LayerFillColorDefault over that base), the stock WinUI text ladder, the subtle fills for row states (hover 6 % / 3.7 %, pressed 4.2 % / 2.4 %,
// a selected row the hover fill plus a 3 x 16 accent pill, selected-and-hovered 8.4 % / 5.8 %), no zebra; the Segoe UI Variable ramp (Caption
// 12/16, Body 14/20, Body Strong 14/20, Body Large 18/24, Subtitle 20/28, Title 28/36, Title Large 40/52, Display 68/92; the Display face from
// Subtitle up); radii 4 for controls, rows and small art, 8 for overlays, cards, covers and the content layer; sections 24 apart on a 4-px grid;
// every label AS WRITTEN (sentence case is the Fluent register).
//
// ONE TABLE. Nothing in the table depends on an option: the accent roles read the live accent tokens (`Tok.AccentDefault`, `Tok.AccentTextPrimary`),
// which the definition's ramp drives (`WinUiAccent`), so the OS accent and the theme's blue are both colour-only changes of the DEFINITION and the
// table stays one instance.
//
// THE PANORAMA ROLES (S2; the WinUI structure never reads them, Panorama's Zune structure does). The values are the Panorama canvas's (technical
// plan 6.2 W1): Panorama Display 68/92 600, PanoramaCompact Title Large 40/52, Pivot Title 28/36 600 with ONE weight at rest and selected (the
// selection is the ink and the 3-px pill, so it never reflows), SubPivot Body 14/20, Numeral 20/28 regular, NumeralLarge 28/36 regular,
// NowPlayingDisplay 68, LyricsLine Title Large 40/52.

using FluentGpu.Foundation;
using ThemeKind = FluentGpu.Dsl.ThemeKind;
using Tok = FluentGpu.Dsl.Tok;

namespace Wavee.Themes.WinUi;

/// <summary>The colours the WinUI boards name (W-Tokens, W-RowStates), per kind. Plain values: the role table and the definition's token sets are
/// both built from them, so the two cannot drift.</summary>
public static class WinUiTokens
{
    /// <summary>The dark Mica base: the toolbar, the pane and the player (and the solid ground when the backdrop is None).</summary>
    public static ColorF DarkBase { get; } = ColorF.FromRgba(0x20, 0x20, 0x20);
    /// <summary>The dark Mica Alt: the tab strip.</summary>
    public static ColorF DarkBaseAlt { get; } = ColorF.FromRgba(0x1C, 0x1C, 0x1C);
    /// <summary>The dark content layer as it composites over the base (#282828).</summary>
    public static ColorF DarkContent { get; } = ColorF.FromRgba(0x28, 0x28, 0x28);
    /// <summary>The light Mica base.</summary>
    public static ColorF LightBase { get; } = ColorF.FromRgba(0xF3, 0xF3, 0xF3);
    /// <summary>The light Mica Alt: the tab strip.</summary>
    public static ColorF LightBaseAlt { get; } = ColorF.FromRgba(0xE9, 0xE9, 0xE9);
    /// <summary>The light content layer as it composites over the base (#f9f9f9).</summary>
    public static ColorF LightContent { get; } = ColorF.FromRgba(0xF9, 0xF9, 0xF9);

    /// <summary>The dark tertiary text: white at 0x8B (#9e9e9e over the content layer), WinUI's TextFillColorTertiary.</summary>
    public static ColorF DarkTextTertiary { get; } = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x8B);

    /// <summary>The light tertiary text: black at 0x8C, not the board's #8a8a8a (black at 0x72), which reads 3.3 : 1 on the content layer and the
    /// cards under the 12-14 DIP counts, plays, dates and eyebrows it inks. At 0x8C it holds 4.6 : 1 on the #f2f2f2 pane plate and 4.75 : 1 on
    /// white, and stays a step lighter than Text 2 (0x9E).</summary>
    public static ColorF LightTextTertiary { get; } = ColorF.FromRgba(0x00, 0x00, 0x00, 0x8C);

    /// <summary>A row's hover fill (subtle fill 1): white 6.05 % on dark, black 3.73 % on light (W-RowStates).</summary>
    public const float HoverDarkA = 0.0605f, HoverLightA = 0.0373f;
    /// <summary>A row's pressed fill (subtle fill 2): white 4.19 % on dark, black 2.41 % on light.</summary>
    public const float PressedDarkA = 0.0419f, PressedLightA = 0.0241f;
    /// <summary>A selected row under the pointer (subtle fill 3): white 8.37 % on dark, black 5.78 % on light.</summary>
    public const float SelectedHoverDarkA = 0.0837f, SelectedHoverLightA = 0.0578f;
    /// <summary>A selected row pressed: between the selected rest (the hover fill) and the selected hover, so the selection ladder only goes up.</summary>
    public const float SelectedPressedDarkA = 0.072f, SelectedPressedLightA = 0.047f;

    /// <summary>The content layer as a white wash over the base: (40 - 32) / (255 - 32) on dark, (249 - 243) / (255 - 243) on light.</summary>
    public const float ContentLayerDarkA = 8f / 223f, ContentLayerLightA = 0.5f;

    /// <summary>The base of <paramref name="kind"/>.</summary>
    public static ColorF BaseOf(ThemeKind kind) => kind == ThemeKind.Light ? LightBase : DarkBase;

    /// <summary>The composited content layer of <paramref name="kind"/>.</summary>
    public static ColorF ContentOf(ThemeKind kind) => kind == ThemeKind.Light ? LightContent : DarkContent;

    /// <summary>The ink a state wash is made of: white on dark, black on light.</summary>
    public static ColorF WashInk(ThemeKind kind, float alpha)
        => kind == ThemeKind.Light ? ColorF.FromRgba(0, 0, 0) with { A = alpha } : ColorF.FromRgba(0xFF, 0xFF, 0xFF) with { A = alpha };
}

/// <summary>The roles of the WinUI style.</summary>
public static class WinUiRoles
{
    /// <summary>The display face: Subtitle and up (W-Tokens' ramp).</summary>
    public const string DisplayFace = "Segoe UI Variable Display";

    /// <summary>The text face chain: everything under Subtitle, and the definition's body family (the font registry walks it).</summary>
    public const string TextFaceChain = "Segoe UI Variable Text, Segoe UI";

    /// <summary>The WinUI table. One immutable instance: the definition carries it for every option set.</summary>
    public static WaveeStyleRoles Table { get; } = new(Type(), Ink(), Layer(), Shape(), Space(), OnMedia(), StageArm(), Wash(), Lyrics(), Motion(), Case());

    // ═════════════════════════════════════════════ TYPE ═════════════════════════════════════════════════════════════════════
    //
    // The ramp's rungs as they stand (sizes, line heights and weights are the engine's FluentDefault ramp, which IS the WinUI ramp); a role names
    // its rung and, from Subtitle up, the Display face. The only off-ramp numbers are the Panorama canvas's numerals (20/28 and 28/36 at 400) and
    // the auto-fit floors of the artist names.

    private static TextRole Text(TypeRung rung, bool secondary = false) => new(rung, Secondary: secondary);

    private static TextRole Display(TypeRung rung, ushort? weight = null, float? minSize = null)
        => new(rung, Face: DisplayFace, Weight: weight, MinSize: minSize);

    private static TypeRoles Type()
    {
        var body = Text(TypeRung.Body);                          // 14/20/400: row and card titles (W-RowStates, W-HomeLight)
        var caption = Text(TypeRung.Caption);                    // 12/16/400
        var subtitle = Display(TypeRung.Subtitle);               // 20/28/600: "Jump back in", "More by Edward Maya"
        var title = Display(TypeRung.Title);                     // 28/36/600
        var titleLarge = Display(TypeRung.TitleLarge);           // 40/52/600: page titles, the album and the daylist title
        var display = Display(TypeRung.Display);                 // 68/92/600: the artist's name
        return new TypeRoles(
            DisplayFace: DisplayFace,
            TrackTitle: body,
            CardTitle: body,
            TrackMeta: Text(TypeRung.Caption, secondary: true),  // 12/16, Text 2 (W-RowStates' subline)
            Eyebrow: caption,                                   // "Your daylist", "Album · 2012": the caption, as written
            MicroMeta: caption,                                 // the ramp has nothing under Caption 12
            DenseMeta: caption,
            DenseTitle: body,
            SheetTitle: subtitle,                               // a dialog's or sheet's title: Subtitle (ContentDialog's own)
            RailHeader: subtitle,
            ModuleHeader: subtitle,
            PageHero: titleLarge,                               // W-Settings, W-Library, W-Queue: the page title is Title Large
            DetailHero: titleLarge,                             // W-Album, W-Playlist: the record's title
            SurfaceDisplay: titleLarge,
            HeroTitle: titleLarge,                              // the daylist card's title (W-HomeLight)
            ArtistDisplay: Display(TypeRung.Display, minSize: 40f),
            ArtistTitle: Display(TypeRung.TitleLarge, minSize: 28f),
            ArtistCompactTitle: Display(TypeRung.Title, minSize: 20f),
            NowPlayingTitle: subtitle,
            PickQuote: Text(TypeRung.BodyLarge),                // 18/24: a quote is body copy, not a heading
            FoldTitle: title,
            PivotLabel: body,                                   // a tab header is the SelectorBar's word, 14/20
            NpvLyric: subtitle,
            StatHero: title,
            // The first pivot row is the Pivot voice in the 48 PivotRow, words 24 apart from the page's x; the second is a SelectorBar (W-Tokens,
            // W-HomeLight "All · Music · Podcasts · Audiobooks": 14/20 words padded 12 in 40-tall items 4 apart, the pill under the word). One weight
            // at rest and selected in both rows: WinUI marks the selection with the ink and the pill, so a row never reflows.
            FacetTitle: new FacetRole(28f, 36f, DisplayFace, 0f, 600, 600, new Edges4(0f, 6f, 0f, 6f), 48f, 24f, 0f),
            FacetCompact: new FacetRole(14f, 20f, null, 0f, 400, 400, new Edges4(12f, 10f, 12f, 10f), 40f, 4f, 0f),
            // ── the pivot-and-panorama vocabulary (the Panorama canvas) ──
            Panorama: display,                                  // Display 68/92/600, one line
            PanoramaCompact: titleLarge,                        // Title Large 40/52 below StyleMetrics.PanoramaCompactBelow
            Hero: titleLarge,
            Pivot: title,                                       // Title 28/36/600 at rest and selected (rest Text 3, selected Text 1 + pill)
            SubPivot: body,                                     // Body 14/20
            Numeral: new TextRole(TypeRung.Subtitle, Size: 20f, LineHeight: 28f, Weight: 400),       // 20/28 regular (Text 3 at the call site)
            NumeralLarge: new TextRole(TypeRung.Title, Size: 28f, LineHeight: 36f, Weight: 400),     // 28/36 regular, the artist's Popular
            NowPlayingDisplay: display,                         // 68
            Wordmark: caption,                                  // the title bar's app name: Caption, as WinUI's TitleBar writes it
            LyricsLine: titleLarge,                             // Title Large 40/52
            Heading: subtitle,
            QuickplayHead: subtitle);
    }

    // ═════════════════════════════════════════════ SHAPE, SPACE, MOTION, CASE ═══════════════════════════════════════════════

    // W-Tokens "Radii": 4 controls, rows, small art · 8 overlays, cards, covers, content layer · circle only play/pause, avatars, artists.
    private static ShapeRoles Shape() => new(
        Card: 8f,
        Cover: 8f,
        Control: 4f,
        Overlay: 8f,
        Tile: 8f,
        PinArt: 4f,          // the pane's 24-32 covers are small art
        Avatar: 999f,
        CurrentMark: 1.5f,   // the 3 x 16 selection pill: fully round ends
        ContentSeam: 8f,     // the content layer's top-left corner
        OmniRow: 4f,
        SeekTrack: 2f);      // the slider's 4-px track

    // Sections 24 apart (W-Tokens "24 sections"), 32 on the wide rung; no page cap (owner Q2: none at Huge or 4K, the column is the window's);
    // cards 16 apart; the dock reserve is the 72 player (W-Tokens "player 72"). The shelf, tile, fade and face-pile numbers are the shell's.
    private static SpaceRoles Space() => new(
        SectionGap: 24f, SectionGapWide: 32f, PageMaxW: 3840f,
        ShelfCardMin: 148f, ShelfCardMax: 188f, GridGap: 16f,
        WideTileMin: 330f, WideTileMax: 440f, WideTileAspect: 16f / 9f,
        FadeChip: 16f, FadeShelf: 24f, FadeRail: 36f,
        FacePileFlyoutW: 280f, FacePileFlyoutH: 360f, FacePileListW: 264f, FacePileListH: 344f,
        DockReserve: 72f);

    // WinUI does not grow under the pointer: a control answers with its fill. A press gives a little.
    private static MotionRoles Motion() => new(
        ScaleSubtle: new ScaleTier(1f, 0.98f),
        ScaleStandard: new ScaleTier(1f, 0.97f),
        ScaleEmphatic: new ScaleTier(1f, 0.95f),
        StaggerMs: 40f,
        MastheadStaggerMs: 45f);

    // Sentence case is the Fluent register: every label as written.
    private static CaseRoles Case() => new(TextCase.AsWritten, TextCase.AsWritten, TextCase.AsWritten, TextCase.AsWritten);

    // ═════════════════════════════════════════════ INK AND LAYER ════════════════════════════════════════════════════════════

    // One accent voice, the definition's ramp: the fill for actions and selection, the accent TEXT shade for words and thin glyphs (#99ebff /
    // #003e92 with the theme's blue). The premium line is the success ink.
    private static InkRoles Ink() => new(
        AccentAction: ColorRole.By(static _ => Tok.AccentDefault),
        AccentSelection: ColorRole.By(static _ => Tok.AccentDefault),
        AccentDecor: ColorRole.By(static _ => Tok.AccentTextPrimary),
        PremiumText: ColorRole.By(static _ => Tok.SystemFillSuccess),
        DataDot: ColorScience.DataDotInk);

    private static ColorRole Wash(float darkA, float lightA)
        => ColorRole.Pair(light: WinUiTokens.WashInk(ThemeKind.Light, lightA), dark: WinUiTokens.WashInk(ThemeKind.Dark, darkA));

    private static ColorRole Base => ColorRole.Pair(light: WinUiTokens.LightBase, dark: WinUiTokens.DarkBase);

    private static ColorRole ContentStep => ColorRole.Pair(light: WinUiTokens.LightContent, dark: WinUiTokens.DarkContent);

    // The frame: the title bar, the pane and the player are the bare Mica base (nothing painted: Transparent), the content is ONE layer
    // (LayerFillColorDefault: #4C3A3A3A on dark, #80FFFFFF on light) with the 8-px top-left corner. The opaque stand-ins (a floating pane, the
    // ground under a flattened gate) are the base and the composited layer. Rows have no zebra; the selection is the hover fill plus the pill.
    private static LayerRoles Layer()
    {
        var hover = Wash(WinUiTokens.HoverDarkA, WinUiTokens.HoverLightA);
        var pressed = Wash(WinUiTokens.PressedDarkA, WinUiTokens.PressedLightA);
        var unpainted = ColorRole.Fixed(ColorF.Transparent);
        var layer = ColorRole.Pair(light: ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x80), dark: ColorF.FromRgba(0x3A, 0x3A, 0x3A, 0x4C));
        return new LayerRoles(
            Toolbar: unpainted, Sidebar: unpainted, PlayerBar: unpainted,
            FileArea: layer, Content: layer,
            ContentAlt: ColorRole.Pair(light: ColorF.FromRgba(0xF6, 0xF6, 0xF6, 0x80), dark: ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x08)),
            ShellGround: Base,
            ContentSurface: ContentStep,
            ContentLayer: ColorRole.Pair(
                light: ColorF.FromRgba(0xFF, 0xFF, 0xFF) with { A = WinUiTokens.ContentLayerLightA },
                dark: ColorF.FromRgba(0xFF, 0xFF, 0xFF) with { A = WinUiTokens.ContentLayerDarkA }),
            FloatingChrome: Base,
            FloatingPane: ContentStep,
            RowZebra: unpainted,
            RowHover: hover, RowPressed: pressed, RowHoverZebra: hover, RowPressedZebra: pressed,
            SelectedRest: hover,
            SelectedHover: Wash(WinUiTokens.SelectedHoverDarkA, WinUiTokens.SelectedHoverLightA),
            SelectedPressed: Wash(WinUiTokens.SelectedPressedDarkA, WinUiTokens.SelectedPressedLightA),
            ChromeHover: ColorRole.By(static _ => Tok.FillSubtleSecondary),
            ChromePressed: ColorRole.By(static _ => Tok.FillSubtleTertiary),
            Badge: ColorRole.By(static _ => Tok.AccentDefault),
            // A concert tile is a card: the card fill flattened on the content layer (white 5 % on dark, #fbfbfb on light).
            EventPlate: ColorRole.Pair(light: ColorF.FromRgba(0xFB, 0xFB, 0xFB), dark: ColorF.FromRgba(0x33, 0x33, 0x33)),
            EventHeroPlate: ColorRole.Pair(light: ColorF.FromRgba(0xF6, 0xF6, 0xF6), dark: ColorF.FromRgba(0x2E, 0x2E, 0x2E)),
            ArtPlaceholder: ColorRole.Pair(light: ColorF.FromRgba(0xF2, 0xF2, 0xF2), dark: ColorF.FromRgba(0x2A, 0x2A, 0x2A)));
    }

    // ═════════════════════════════════════════════ ON MEDIA, STAGE, WASH, LYRICS ════════════════════════════════════════════
    //
    // No WinUI board re-tints what paints on artwork: the on-media ladder is theme-invariant by contract, so WinUI publishes the shell's ladder (the
    // same literals and recipes the shell has always drawn). The stage's light arm mirrors onto the light content layer; the card is the in-app
    // acrylic. The wash strengths are the shell's.

    private static ColorF ScrimHoverC => ColorF.FromRgba(0, 0, 0, 190);
    private static ColorF ScrimPressedC => ColorF.FromRgba(0, 0, 0, 220);
    private static ColorF StrokeC => ColorF.FromRgba(255, 255, 255, 58);

    private const float LightHoverDrop = 0.08f, LightPressedDrop = 0.157f;

    private static OnMediaLadder OnMedia() => new(
        ScrimRest: ColorRole.By(static _ => Tok.MediaScrim),
        ScrimHover: ColorRole.Fixed(ScrimHoverC),
        ScrimPressed: ColorRole.Fixed(ScrimPressedC),
        CoverScrim: ColorRole.Fixed(ColorF.FromRgba(0, 0, 0, 110)),
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

    private const float StageScrimBaseA = 0.46f;

    private static bool ArmDark(ThemeKind k) => k == ThemeKind.Dark;

    private static ColorF ArmVeil(ThemeKind k) => ArmDark(k) ? Tok.MediaStage : WinUiTokens.LightContent;

    private static ColorF ArmInk(ThemeKind k) => ArmDark(k) ? Tok.OnMediaPrimary : Tok.MediaStage;

    private static StageArmTable StageArm() => new(
        IsDark: ArmDark,
        Veil: ColorRole.By(ArmVeil),
        Floor: ColorRole.By(ArmVeil),
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
        Stroke: ColorRole.By(static k => ArmDark(k) ? StrokeC : ArmInk(k) with { A = StrokeC.A }),
        ButtonFill: ColorRole.By(static k => ArmDark(k) ? Tok.OnMediaPrimary : ArmInk(k)),
        ButtonFillHover: ColorRole.By(static k => ArmDark(k)
            ? ColorRamp.Darken(Tok.OnMediaPrimary, LightHoverDrop) : ColorRamp.Lighten(ArmInk(k), LightHoverDrop)),
        ButtonFillPressed: ColorRole.By(static k => ArmDark(k)
            ? ColorRamp.Darken(Tok.OnMediaPrimary, LightPressedDrop) : ColorRamp.Lighten(ArmInk(k), LightPressedDrop)),
        ButtonInk: ColorRole.By(static k => ArmDark(k) ? Tok.MediaStage : ArmVeil(k)),
        SkeletonBar: ColorRole.By(static k => ArmInk(k) with { A = 0.12f }),
        Plate: ColorRole.By(static k => ArmInk(k) with { A = 0.045f }),
        PlateHover: ColorRole.By(static k => ArmInk(k) with { A = 0.075f }),
        Card: static k => ArmDark(k)
            ? AcrylicSpec.InAppBase
            : new AcrylicSpec(ColorF.FromRgba(0xF3, 0xF3, 0xF3), 0f, 30f, 0.02f, 0.90f, ColorF.FromRgba(0xEE, 0xEE, 0xEE)),
        AccentFrom: static (k, chrome) => ArmDark(k)
            ? chrome
            : ColorScience.TextInk(chrome, ThemeKind.Light, ColorContrast.Over(ArmVeil(k) with { A = StageScrimBaseA }, Tok.OnMediaPrimary)),
        SelectedTileWash: new ModePair<float>(Light: 0.08f, Dark: 0.12f),
        ScrimBaseA: StageScrimBaseA);

    private const float TintAlphaLight = 0.05f, TintAlphaDark = 0.14f;

    private static ColorF LiftPayload(uint argb) => ColorScience.Lift(ColorScience.ToColor(argb));

    private static AccentLadder.Result BlendLadder(ThemeKind k, Scheme? pageScheme, uint payloadAccent, bool definite)
        => k == ThemeKind.Light
            ? AccentLadder.Resolve(
                new AccentLadder.Input(pageScheme is { } wp ? ColorScience.Lift(ColorScience.Accent(wp)) : null, payloadAccent, definite),
                null, Tok.AccentDefault, LiftPayload)
            : AccentLadder.Resolve(
                new AccentLadder.Input(pageScheme is { } dp ? ColorScience.BackgroundDark(dp) : null, payloadAccent, definite),
                null, ColorScience.BackgroundDark(ColorScience.Neutral), ColorScience.DarkFromPayload);

    private static WashRoles Wash() => new(
        NeutralGround: ColorRole.Pair(light: WinUiTokens.LightBase with { A = 0.03f }, dark: WinUiTokens.DarkBase with { A = 0.03f }),
        HeroAlpha: new ModePair<float>(0.055f, 0.10f),
        ShelfAlpha: new ModePair<float>(0.05f, 0.085f),
        HeroVeilPull: new ModePair<float>(0.16f, 0.24f),
        HeroVeilPeak: new ModePair<float>(0.42f, 0.78f),
        CoverMix: new ModePair<float>(0.10f, 0.18f),
        ConcertMediaMix: new ModePair<float>(0.18f, 0.30f),
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
        BloomOnMedia: ColorRole.By(static k => ArmDark(k) ? ArmInk(k) : ArmVeil(k)),
        BloomScale: new ModePair<float>(0.5f, 1f));
}
