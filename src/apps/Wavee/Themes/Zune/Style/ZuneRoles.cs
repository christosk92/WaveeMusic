// ── Themes/Zune/Style/ZuneRoles.cs — the roles of the Zune style (themes plan 5.4, 6.3, Z1) ──────────────────────────────────
//
// Every number and colour below is read off the Zune boards (Z-Tokens first, then the surface that draws it): the ground ladder
// #161616 / #111111 / #1b1b1b / #242424 and its light twin #f5f5f5 / #ebebeb / #ffffff / #ffffff, the ink #fff / #a8a8a8 / #7a7a7a
// (light #111 / #4c4c4c / #646464, the boards' #5a5a5a / #8a8a8a darkened so both hold 4.5 : 1), a 5 % ink wash for hover, a 12 % accent wash for selection, the type in Segoe UI Variable Display
// from 20 DIP up at the light weights (200 panorama, 300 titles and headings, 350 pivots at rest) and Segoe UI Variable Text below,
// the square corners (0 everywhere, 2 on covers, full on the round metro buttons and the people), and lowercase for every word the
// theme writes (labels, navigation, headings, commands; never a name or a title someone typed).
//
// A table is a function of three option values: the accent (a swatch pair, or the OS accent), and whether the numerals are big. The
// accent moves only colour roles (ink, layer), so two accents are a COLOUR-ONLY difference and the accent applies live; the numerals
// move the type roles (`TypeRoles.WithBigNumerals`), a non-colour difference applied on the theme transaction. `ZuneStyle` memoizes
// the tables per input so an unchanged pick installs the same instance.
//
// THE PIVOT'S TWO WEIGHTS (ZV hand-off). A `TextRole` has one weight. Zune's pivot is 350 at rest and 600 selected (Z-Tokens: "pivot 28 ·
// rest 350"). The roles say it the way the engine already draws a two-weight pivot: `Pivot` (and `PivotLabel`) is the REST voice,
// 28/36/350, and `FacetTitle` is the same voice as a pivot row with `RestWeight` 350 and `SelectedWeight` 600 (`R.Type.FacetTitleStyle`,
// a `SelectorBarStyle`). A view that draws its own pivot word sets the selected weight from that style
// (`R.Type.Pivot(s) with { Weight = (ushort)R.Type.FacetTitleStyle.SelectedWeight }`); the second row is the same pair one rung down
// (`SubPivot` 14/20/400, `FacetCompact` 400 / 600).
//
// WHAT HAS NO ROLE. The focus ring's 4-DIP corner (Z-Tokens "4 only for the focus ring") is not a shape role: the engine draws the ring
// concentric with the control's own corner. The board's 120 / 64 / 150 panorama ladder by window tier is the structure's (it picks
// `Panorama`, `PanoramaCompact` or `NowPlayingDisplay`); the style publishes the three voices.

using FluentGpu.Foundation;
using ThemeKind = FluentGpu.Dsl.ThemeKind;
using Tok = FluentGpu.Dsl.Tok;

namespace Wavee.Themes.Zune;

/// <summary>The colours the Zune boards name (Z-Tokens), per ground. Plain values: the role table and the definition's token sets are
/// both built from them, so the two can never drift.</summary>
public static class ZuneTokens
{
    /// <summary>The dark ground, the default: the page colour.</summary>
    public static ColorF DarkGround { get; } = ColorF.FromRgba(0x16, 0x16, 0x16);
    /// <summary>The dark well: the play bar and the stage plates.</summary>
    public static ColorF DarkWell { get; } = ColorF.FromRgba(0x11, 0x11, 0x11);
    /// <summary>The dark panel: the inline right panel.</summary>
    public static ColorF DarkPanel { get; } = ColorF.FromRgba(0x1B, 0x1B, 0x1B);
    /// <summary>The dark raised plate: menus, tiles, flyouts, toasts.</summary>
    public static ColorF DarkRaised { get; } = ColorF.FromRgba(0x24, 0x24, 0x24);
    /// <summary>The dark dialog plate (Z-Dialogs draws #1f1f1f, between the panel and the raised plate; the board wins for the surface).</summary>
    public static ColorF DarkDialog { get; } = ColorF.FromRgba(0x1F, 0x1F, 0x1F);
    /// <summary>The dark art placeholder (the boards' empty cover).</summary>
    public static ColorF DarkPlaceholder { get; } = ColorF.FromRgba(0x2A, 0x2A, 0x2A);
    /// <summary>The dark ink.</summary>
    public static ColorF DarkInk { get; } = ColorF.FromRgba(0xFF, 0xFF, 0xFF);
    /// <summary>The dark secondary ink.</summary>
    public static ColorF DarkInkSecondary { get; } = ColorF.FromRgba(0xA8, 0xA8, 0xA8);
    /// <summary>The dark tertiary ink: numbers, pivots at rest.</summary>
    public static ColorF DarkInkTertiary { get; } = ColorF.FromRgba(0x7A, 0x7A, 0x7A);

    /// <summary>The light ground.</summary>
    public static ColorF LightGround { get; } = ColorF.FromRgba(0xF5, 0xF5, 0xF5);
    /// <summary>The light well.</summary>
    public static ColorF LightWell { get; } = ColorF.FromRgba(0xEB, 0xEB, 0xEB);
    /// <summary>The light panel.</summary>
    public static ColorF LightPanel { get; } = ColorF.FromRgba(0xFF, 0xFF, 0xFF);
    /// <summary>The light raised plate (Z-Light-PlayBar's menus: white).</summary>
    public static ColorF LightRaised { get; } = ColorF.FromRgba(0xFF, 0xFF, 0xFF);
    /// <summary>The light ink.</summary>
    public static ColorF LightInk { get; } = ColorF.FromRgba(0x11, 0x11, 0x11);
    /// <summary>The light secondary ink: #4c4c4c (the boards' #5a5a5a, a step darker so the tertiary below it can hold 4.5 : 1 on a selected row and
    /// still read as a lighter voice): 7.6 : 1 on the ground.</summary>
    public static ColorF LightInkSecondary { get; } = ColorF.FromRgba(0x4C, 0x4C, 0x4C);
    /// <summary>The light tertiary ink: #646464, not the boards' #8a8a8a (2.9 to 3.2 : 1 on the light ground and well, under the 4.5 : 1 the
    /// 11 to 13 DIP captions, dates and counts it inks need): 5.3 : 1 on the ground, 4.8 : 1 on the well, 4.6 : 1 on a selected row's 8 % grey.</summary>
    public static ColorF LightInkTertiary { get; } = ColorF.FromRgba(0x64, 0x64, 0x64);

    /// <summary>The hover wash: 5 % of the ink (white on dark, black on light; Z-Tokens, Z-Light-Album).</summary>
    public const float HoverA = 0.05f;
    /// <summary>The pressed wash: 8 % of the ink (no board draws a press; one step past hover in both kinds).</summary>
    public const float PressedA = 0.08f;
    /// <summary>The selected wash: 12 % of the accent (Z-Tokens).</summary>
    public const float SelectedA = 0.12f;

    /// <summary>The selected wash on the LIGHT ground: 6 %. At 12 % the accent's wash takes the ground to about #f2dbe7, where the tertiary ink reads
    /// 4.2 : 1 and the accent ink 4.1 : 1; at 6 % both hold 4.5 : 1.</summary>
    public const float SelectedALight = 0.06f;

    /// <summary>The selected wash of <paramref name="kind"/>.</summary>
    public static float SelectedAOf(ThemeKind kind) => kind == ThemeKind.Light ? SelectedALight : SelectedA;
    /// <summary>The hairline, one per surface at most: 8 % white on dark, 10 % black on light (Z-Tokens).</summary>
    public const float HairlineDarkA = 0.08f, HairlineLightA = 0.10f;
    /// <summary>A rest chip and a quiet plate: 6 % of the ink (Z-Tokens' "pop" chip).</summary>
    public const float ChipA = 0.06f;
    /// <summary>A line track (the seek line, a slider rail, a progress track): 12 % of the ink (Z-Part-PlayBar, Z-Light-PlayBar).</summary>
    public const float TrackA = 0.12f;
    /// <summary>A field's rule and the search pill's ring at rest: 18 % of the ink (Z-Part-TitleBar, Z-Dialogs).</summary>
    public const float FieldRuleA = 0.18f;

    /// <summary>The ink of <paramref name="kind"/>.</summary>
    public static ColorF InkOf(ThemeKind kind) => kind == ThemeKind.Light ? LightInk : DarkInk;
}

/// <summary>The roles of the Zune style.</summary>
public static class ZuneRoles
{
    /// <summary>The display face: everything 20 DIP and up (Z-Tokens).</summary>
    public const string DisplayFace = "Segoe UI Variable Display";

    /// <summary>The text face chain: everything under 20 DIP, and the definition's body family (the font registry walks it).</summary>
    public const string TextFaceChain = "Segoe UI Variable Text, Segoe UI";

    // The accent-independent families, built once. Declared BEFORE `Table`: static initialisers run in textual order.
    private static readonly TypeRoles s_type = Type();
    private static readonly ShapeRoles s_shape = Shape();
    private static readonly SpaceRoles s_space = Space();
    private static readonly MotionRoles s_motion = Motion();
    private static readonly CaseRoles s_case = Case();
    private static readonly OnMediaLadder s_onMedia = OnMedia();
    private static readonly StageArmTable s_stageArm = StageArm();
    private static readonly WashRoles s_wash = Wash();
    private static readonly LyricsInkRoles s_lyrics = Lyrics();

    /// <summary>Zune's table at its defaults: the pink accent, big numerals on.</summary>
    public static WaveeStyleRoles Table { get; } = For(ZuneAccent.Pink, followsWindows: false, bigNumerals: true);

    /// <summary>The type roles with big numerals on (the table <see cref="TypeRoles.WithBigNumerals"/> starts from).</summary>
    public static TypeRoles TypeTable => s_type;

    /// <summary>The table for an accent and the numerals option. <paramref name="followsWindows"/> reads the live accent tokens (the OS ramp the
    /// definition carries) instead of the pair. Pure: equal inputs build equal tables.</summary>
    public static WaveeStyleRoles For(ZuneAccentPair accent, bool followsWindows, bool bigNumerals) => new(
        Type: s_type.WithBigNumerals(bigNumerals),
        Ink: Ink(accent, followsWindows),
        Layer: Layer(accent, followsWindows),
        Shape: s_shape,
        Space: s_space,
        OnMedia: s_onMedia,
        StageArm: s_stageArm,
        Wash: s_wash,
        Lyrics: s_lyrics,
        Motion: s_motion,
        Case: s_case)
    {
        Controls = ZuneControlRoles.Table,
    };

    // ═════════════════════════════════════════════ TYPE ═════════════════════════════════════════════════════════════════════
    //
    // Sizes / line heights / weights are Z-Tokens' (panorama 120/130·200, hero 64/72·300, title 40·300, pivot 28/36 600·350, heading 26/32·300,
    // group 22·300, numerals 22 (28 on the artist page)·300, sub-pivot 14/20, body 15/20, meta 13/18, micro 11/14, now playing 150·300) and the
    // surfaces' (lyrics 56 semilight, Z-Lyrics; the wordmark 20/28·350, Z-Part-TitleBar). Tracking is the boards' letter-spacing in 1/1000 em
    // (-2 px on 120 = -17, -1 px on 64 = -16, -0.5 px on 40 = -12, -0.3 px on 28 = -11, -0.2 px on 20 = -10). Every role states its size, line
    // height and weight, so the engine ramp only names the rung.

    private static TextRole Display(TypeRung rung, float size, float line, ushort weight, float? tracking = null)
        => new(rung, Face: DisplayFace, Size: size, LineHeight: line, Weight: weight, Tracking: tracking);

    private static TextRole Text(TypeRung rung, float size, float line, ushort weight = 400, bool secondary = false)
        => new(rung, Size: size, LineHeight: line, Weight: weight, Secondary: secondary);

    private static TypeRoles Type()
    {
        var panorama = Display(TypeRung.Display, 120f, 130f, 200, -17f);
        var panoramaCompact = Display(TypeRung.Display, 64f, 72f, 200, -16f);
        var hero = Display(TypeRung.Display, 64f, 72f, 300, -16f);
        var title = Display(TypeRung.TitleLarge, 40f, 48f, 300, -12f);
        var heading = Display(TypeRung.Title, 26f, 32f, 300);
        var group = Display(TypeRung.Subtitle, 22f, 28f, 300);
        var pivot = Display(TypeRung.Title, 28f, 36f, 350, -11f);
        var quote = Display(TypeRung.Title, 28f, 36f, 300, -11f);
        var body = Text(TypeRung.Body, 15f, 20f);
        var meta = Text(TypeRung.Caption, 13f, 18f);
        var micro = Text(TypeRung.Caption, 11f, 14f);
        return new TypeRoles(
            DisplayFace: DisplayFace,
            TrackTitle: body,                                                   // row titles, 15/20
            CardTitle: body,
            TrackMeta: Text(TypeRung.Caption, 13f, 18f, secondary: true),        // meta 13/18, secondary ink
            Eyebrow: micro,                                                     // the small label ("pinned"), 11/14, lowercased by the case roles
            MicroMeta: micro,
            DenseMeta: meta,
            DenseTitle: Text(TypeRung.Body, 14f, 18f),
            SheetTitle: group,                                                  // a sheet or card heading: the group voice, 22/28/300
            RailHeader: heading,                                                // a section heading, 26/32/300
            ModuleHeader: heading,
            PageHero: title,                                                    // a page title that is not a panorama, 40/48/300
            DetailHero: hero,                                                   // liked songs, playlist, profile names, 64/72/300
            SurfaceDisplay: panorama,                                           // a place's name is the panorama
            HeroTitle: title,                                                   // the daylist card's title, 40
            ArtistDisplay: panorama,                                            // the artist's name as a panorama word, 120/130/200
            ArtistTitle: hero,
            ArtistCompactTitle: title,
            NowPlayingTitle: heading,                                           // the now-playing card's title, 26/32/300
            PickQuote: quote,
            FoldTitle: quote,
            PivotLabel: pivot,
            NpvLyric: group,                                                    // the panel's lyric lines, 22/28/300 (Z-Part-Panel)
            StatHero: Display(TypeRung.TitleLarge, 40f, 48f, 200, -12f),
            // The band's row 1 (Z-Part-Band, Z-Theme-Options): 28/36 display words in a 44 row (4 + 36 + 4), 24 apart, 350 at rest and 600
            // selected, no pill; row 2 one rung down: 14/20 text words in a 32 row, 20 apart, 400 / 600. Both start on the page's x.
            FacetTitle: new FacetRole(28f, 36f, DisplayFace, -11f, 350, 600, new Edges4(0f, 4f, 0f, 4f), 44f, 24f, 0f),
            FacetCompact: new FacetRole(14f, 20f, null, 0f, 400, 600, new Edges4(0f, 6f, 0f, 6f), 32f, 20f, 0f),
            Panorama: panorama,
            PanoramaCompact: panoramaCompact,                                   // 64·200 under the compact width (Z-Sizes-Breakpoints)
            Hero: hero,
            Pivot: pivot,                                                       // the REST voice; selected is FacetTitle.SelectedWeight (600)
            SubPivot: Text(TypeRung.Body, 14f, 20f),                            // rest 400; selected is FacetCompact.SelectedWeight (600)
            Numeral: Display(TypeRung.Subtitle, 22f, 28f, 300),                 // track numbers 22·300 (tertiary ink at the call site)
            NumeralLarge: Display(TypeRung.Title, 28f, 36f, 300),               // 28 on the artist page
            NowPlayingDisplay: Display(TypeRung.Display, 150f, 150f, 300),      // the full-screen title, 150/150·300
            Wordmark: Display(TypeRung.Subtitle, 20f, 28f, 350, -10f),          // "wavee" in the title bar
            LyricsLine: Display(TypeRung.Display, 56f, 64f, 350),               // the full-size lyrics line, 56 semilight
            Heading: heading,
            QuickplayHead: group);                                              // a quickplay group's head, 22/28/300
    }

    // ═════════════════════════════════════════════ SHAPE, SPACE, MOTION, CASE ═══════════════════════════════════════════════

    // 0 for every rectangle (tiles, plates, menus, dialogs, inputs) · 2 for covers · full for the round metro buttons and the people (Z-Tokens).
    // A band pin's art is the one 4 (Z-Theme-Options draws the pins at 32 with a 4 corner); the playing dot is round.
    private static ShapeRoles Shape() => new(
        Card: 0f, Cover: 2f, Control: 0f, Overlay: 0f, Tile: 0f, PinArt: 4f, Avatar: 999f,
        CurrentMark: 999f, ContentSeam: 0f, OmniRow: 0f, SeekTrack: 0f);

    // Section gap 32 (48 the wide rung, the page gutter's step), grids 16 apart with cards 150 to 220 (Z-Sizes-Breakpoints: "4 columns
    // (min 150)", "auto-fill min 200 · 4k min 220"), pages capped at 2400 (P5 decision 2, owner 6.2), the bar reserve 72 = seek 2 + transport 70.
    // The edge fades, the wide tile and the face-pile flyout have no Zune board: the values are the shell's.
    private static SpaceRoles Space() => new(
        SectionGap: 32f, SectionGapWide: 48f, PageMaxW: 2400f,
        ShelfCardMin: 150f, ShelfCardMax: 220f, GridGap: 16f,
        WideTileMin: 330f, WideTileMax: 440f, WideTileAspect: 16f / 9f,
        FadeChip: 16f, FadeShelf: 24f, FadeRail: 36f,
        FacePileFlyoutW: 280f, FacePileFlyoutH: 360f, FacePileListW: 264f, FacePileListH: 344f,
        DockReserve: 72f);

    // Metro is still under the pointer: a plate answers with its wash, not with growth. Only a press gives (a 2-4 % sink), and the round
    // media affordances keep a small hover lift. The stagger rungs have no Zune board and keep the shell's.
    private static MotionRoles Motion() => new(
        ScaleSubtle: new ScaleTier(1f, 0.98f),
        ScaleStandard: new ScaleTier(1f, 0.97f),
        ScaleEmphatic: new ScaleTier(1.04f, 0.94f),
        StaggerMs: 40f,
        MastheadStaggerMs: 45f);

    // "lowercase for every label the theme writes, never for names and titles people typed" (Z-Tokens).
    private static CaseRoles Case() => new(TextCase.Lower, TextCase.Lower, TextCase.Lower, TextCase.Lower);

    // ═════════════════════════════════════════════ INK AND LAYER (the accent's two families) ════════════════════════════════

    private static ColorRole AccentRole(ZuneAccentPair accent, bool followsWindows)
        => followsWindows ? ColorRole.By(static _ => Tok.AccentDefault) : ColorRole.Pair(light: accent.Light, dark: accent.Dark);

    // Accent as INK: accent-coloured words and thin glyphs. The light arm is the darker LightInk (4.5 : 1 on the light plates), the dark arm the
    // swatch itself; the OS accent reads the ramp's text shade, which `ZuneAccent.RampOf` makes the same ink.
    private static ColorRole AccentInkRole(ZuneAccentPair accent, bool followsWindows)
        => followsWindows ? ColorRole.By(static _ => Tok.AccentTextPrimary) : ColorRole.Pair(light: accent.LightInk, dark: accent.Dark);

    private static ColorRole AccentWash(ZuneAccentPair accent, bool followsWindows)
        => followsWindows
            ? ColorRole.By(static k => Tok.AccentDefault with { A = ZuneTokens.SelectedAOf(k) })
            : ColorRole.Pair(light: accent.Light with { A = ZuneTokens.SelectedALight }, dark: accent.Dark with { A = ZuneTokens.SelectedA });

    // Play, the playing row, selection, the premium line and the notification dot are all the one accent (Z-Tokens, Z-Overlays): Zune
    // has one accent voice. As a FILL (the play disc, a selected chip, the progress line, an accent ring: Action and Selection) it is the
    // board's swatch; as INK (a word, a thin glyph: Decor and PremiumText) it is the swatch on the dark ground and the darker ink on the light
    // one, because the board's light swatch reads 3.3 to 4.2 : 1 as text.
    private static InkRoles Ink(ZuneAccentPair accent, bool followsWindows)
    {
        var fill = AccentRole(accent, followsWindows);
        var ink = AccentInkRole(accent, followsWindows);
        return new InkRoles(AccentAction: fill, AccentSelection: fill, AccentDecor: ink, PremiumText: ink, DataDot: ColorScience.DataDotInk);
    }

    private static ColorRole Ground => ColorRole.Pair(light: ZuneTokens.LightGround, dark: ZuneTokens.DarkGround);
    private static ColorRole Well => ColorRole.Pair(light: ZuneTokens.LightWell, dark: ZuneTokens.DarkWell);
    private static ColorRole Panel => ColorRole.Pair(light: ZuneTokens.LightPanel, dark: ZuneTokens.DarkPanel);
    private static ColorRole Raised => ColorRole.Pair(light: ZuneTokens.LightRaised, dark: ZuneTokens.DarkRaised);

    private static ColorF InkAt(ThemeKind kind, float a) => ZuneTokens.InkOf(kind) with { A = a };

    private static ColorRole InkWash(float a) => ColorRole.Pair(light: InkAt(ThemeKind.Light, a), dark: InkAt(ThemeKind.Dark, a));

    private static ColorRole Over(ColorRole top, ColorRole under) => ColorRole.Pair(
        light: ColorContrast.Over(top.Resolve(ThemeKind.Light), under.Resolve(ThemeKind.Light)),
        dark: ColorContrast.Over(top.Resolve(ThemeKind.Dark), under.Resolve(ThemeKind.Dark)));

    // Flat and opaque: every band of the frame is the ground (no material rungs, no content step), the bar is the well, a floating pane
    // is the panel and floating chrome the raised plate. Rows carry no zebra; hover is the 5 % ink wash, selection the 12 % accent wash.
    private static LayerRoles Layer(ZuneAccentPair accent, bool followsWindows)
    {
        var hover = InkWash(ZuneTokens.HoverA);
        var pressed = InkWash(ZuneTokens.PressedA);
        var selected = AccentWash(accent, followsWindows);
        return new LayerRoles(
            Toolbar: Ground, Sidebar: Ground, PlayerBar: Well, FileArea: Ground, Content: Ground, ContentAlt: Well,
            ShellGround: Ground, ContentSurface: Ground, ContentLayer: Ground, FloatingChrome: Raised, FloatingPane: Panel,
            RowZebra: ColorRole.Fixed(ColorF.Transparent),
            RowHover: hover, RowPressed: pressed, RowHoverZebra: hover, RowPressedZebra: pressed,
            SelectedRest: selected,
            SelectedHover: followsWindows
                ? ColorRole.By(static k => ColorContrast.Over(InkAt(k, ZuneTokens.HoverA), Tok.AccentDefault with { A = ZuneTokens.SelectedAOf(k) }))
                : Over(hover, selected),
            SelectedPressed: followsWindows
                ? ColorRole.By(static k => ColorContrast.Over(InkAt(k, ZuneTokens.PressedA), Tok.AccentDefault with { A = ZuneTokens.SelectedAOf(k) }))
                : Over(pressed, selected),
            ChromeHover: hover, ChromePressed: pressed,
            Badge: AccentRole(accent, followsWindows),
            EventPlate: Panel,
            EventHeroPlate: Well,
            ArtPlaceholder: ColorRole.Pair(light: ZuneTokens.LightWell, dark: ZuneTokens.DarkPlaceholder));
    }

    // ═════════════════════════════════════════════ ON MEDIA, STAGE, WASH, LYRICS ════════════════════════════════════════════
    //
    // No Zune board re-tints what paints on artwork: the on-media ladder is theme-invariant by contract, so Zune publishes the shell's ladder
    // (the same literals and recipes as the shell has always drawn). The stage's light arm mirrors onto Zune's light ground (#f5f5f5), the
    // card is the in-app base. The wash strengths are the shell's until the material lane (Z2b) tunes the artwork wash against the boards.

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

    private static ColorF ArmVeil(ThemeKind k) => ArmDark(k) ? Tok.MediaStage : ZuneTokens.LightGround;

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
        SelectedTileWash: new ModePair<float>(Light: 0.08f, Dark: ZuneTokens.SelectedA),
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
        NeutralGround: ColorRole.Pair(light: ZuneTokens.LightGround with { A = 0.03f }, dark: ZuneTokens.DarkGround with { A = 0.03f }),
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
