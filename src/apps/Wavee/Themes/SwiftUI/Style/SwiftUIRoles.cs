// ── Themes/SwiftUI/Style/SwiftUIRoles.cs — the roles of the SwiftUI spike style (SPIKE, not for release) ─────────────────────────
//
// The spike rides on WinUI's structure, so it starts from WinUI's role table (handed in by the composition: a style may not name another
// theme) and replaces what makes it read as Apple's:
//   · TYPE: the ramp itself is the definition's (`SwiftUIStyle.Ramp`, the macOS 26 / iOS 26 text styles), so every WinUI role that names a
//     rung follows it; the faces are Segoe UI Variable Display / Text, the closest optical-size pair Windows ships to SF Pro Display / Text.
//   · LAYERS: one unified ground (no Mica base, no content card: the macOS window is one surface and the sidebar floats over it), rounded
//     unemphasized selection plates, no zebra;
//   · SHAPE: concentric radii (covers 8, cards and tiles 12, controls 8, overlays 16, capsules full) instead of WinUI's 4 / 8;
//   · MOTION: a press gives a little more (Apple's springy 0.96 on emphatic controls).

using FluentGpu.Foundation;
using ThemeKind = FluentGpu.Dsl.ThemeKind;
using Tok = FluentGpu.Dsl.Tok;

namespace Wavee.Themes.SwiftUI;

/// <summary>The roles of the SwiftUI spike style.</summary>
public static class SwiftUIRoles
{
    /// <summary>The display face (Large Title to Title 3): Segoe UI Variable Display, standing in for SF Pro Display.</summary>
    public const string DisplayFace = "Segoe UI Variable Display";

    /// <summary>The text face chain (Headline and under): Segoe UI Variable Text, standing in for SF Pro Text.</summary>
    public const string TextFaceChain = "Segoe UI Variable Text, Segoe UI";

    /// <summary>The table: <paramref name="baseRoles"/> (WinUI's, from the composition) with SwiftUI's layers, shapes and motion.</summary>
    public static WaveeStyleRoles From(WaveeStyleRoles baseRoles) => baseRoles with
    {
        Type = Type(baseRoles.Type),
        Layer = Layer(),
        Shape = Shape(),
        Motion = Motion(),
    };

    // ═════════════════════════════════════════════ TYPE ═════════════════════════════════════════════════════════════════════
    //
    // The rungs are the definition's ramp. The few roles WinUI pins to explicit sizes are re-pinned to Apple's: the pivot words are a 13/16
    // segmented label (FacetCompact), the first pivot row the Title 2 voice, the numerals Title 3 / Title 1 at regular weight.
    private static TypeRoles Type(TypeRoles b) => b with
    {
        DisplayFace = DisplayFace,
        FacetTitle = new FacetRole(22f, 28f, DisplayFace, 0f, 700, 700, new Edges4(0f, 6f, 0f, 6f), 44f, 20f, 0f),
        FacetCompact = new FacetRole(13f, 16f, null, 0f, 500, 600, new Edges4(12f, 6f, 12f, 6f), 28f, 2f, 0f),
        Numeral = new TextRole(TypeRung.Subtitle, Size: 20f, LineHeight: 25f, Weight: 400),
        NumeralLarge = new TextRole(TypeRung.Title, Size: 26f, LineHeight: 32f, Weight: 400),
        Wordmark = new TextRole(TypeRung.BodyStrong),
    };

    // ═════════════════════════════════════════════ SHAPE, MOTION ════════════════════════════════════════════════════════════

    // Concentric corners: a cover 8 inside a 12 card (8 + the 4 inset), a 16 overlay around 8 rows, capsules for controls that act.
    private static ShapeRoles Shape() => new(
        Card: 12f,
        Cover: 8f,
        Control: 8f,
        Overlay: 16f,
        Tile: 12f,
        PinArt: 6f,
        Avatar: 999f,
        CurrentMark: 1.5f,
        ContentSeam: 0f,
        OmniRow: 8f,
        SeekTrack: 3f);

    private static MotionRoles Motion() => new(
        ScaleSubtle: new ScaleTier(1f, 0.98f),
        ScaleStandard: new ScaleTier(1f, 0.97f),
        ScaleEmphatic: new ScaleTier(1f, 0.96f),
        StaggerMs: 35f,
        MastheadStaggerMs: 40f);

    // ═════════════════════════════════════════════ LAYERS ═══════════════════════════════════════════════════════════════════

    private static ColorRole Wash(float darkA, float lightA)
        => ColorRole.Pair(light: SwiftUITokens.Wash(false, lightA), dark: SwiftUITokens.Wash(true, darkA));

    private static ColorRole Ground => ColorRole.Pair(light: SwiftUITokens.LightGround, dark: SwiftUITokens.DarkGround);

    private static ColorRole Raised => ColorRole.Pair(light: SwiftUITokens.LightRaised, dark: SwiftUITokens.DarkRaised);

    // The window is ONE surface: the toolbar, the content and the shell ground are the same colour, so nothing reads as a card. The sidebar
    // and the player paint nothing here: the SwiftUI frame lays the glass under them.
    private static LayerRoles Layer()
    {
        var hover = Wash(SwiftUITokens.HoverDarkA, SwiftUITokens.HoverLightA);
        var pressed = Wash(SwiftUITokens.PressedDarkA, SwiftUITokens.PressedLightA);
        var selected = Wash(SwiftUITokens.SelectedDarkA, SwiftUITokens.SelectedLightA);
        var unpainted = ColorRole.Fixed(ColorF.Transparent);
        return new LayerRoles(
            Toolbar: unpainted, Sidebar: unpainted, PlayerBar: unpainted,
            FileArea: Ground, Content: Ground,
            ContentAlt: Wash(0.03f, 0.02f),
            ShellGround: Ground,
            ContentSurface: Ground,
            ContentLayer: unpainted,
            FloatingChrome: Raised,
            FloatingPane: Raised,
            RowZebra: unpainted,
            RowHover: hover, RowPressed: pressed, RowHoverZebra: hover, RowPressedZebra: pressed,
            SelectedRest: selected,
            SelectedHover: Wash(SwiftUITokens.SelectedDarkA + 0.03f, SwiftUITokens.SelectedLightA + 0.02f),
            SelectedPressed: Wash(SwiftUITokens.SelectedDarkA + 0.05f, SwiftUITokens.SelectedLightA + 0.04f),
            ChromeHover: hover,
            ChromePressed: pressed,
            Badge: ColorRole.By(static _ => Tok.AccentDefault),
            EventPlate: ColorRole.Pair(light: SwiftUITokens.LightGrouped, dark: SwiftUITokens.DarkRaised),
            EventHeroPlate: ColorRole.Pair(light: SwiftUITokens.LightGrouped, dark: SwiftUITokens.DarkRaised),
            ArtPlaceholder: ColorRole.Pair(light: ColorF.FromRgba(0xE5, 0xE5, 0xEA), dark: ColorF.FromRgba(0x3A, 0x3A, 0x3C)));
    }

    /// <summary>The kind a role pair answers for (light or dark), for the frame's glass.</summary>
    public static bool IsDark => Tok.Theme == ThemeKind.Dark;
}
