// ── Themes/FluentV8/Style/FluentV8Palette.cs — the Fluent UI React v8 palette (SPIKE) ───────────────────────────────────────────────
//
// The exact @fluentui/react v8 tokens the FluentV8 style is built from:
//   · LIGHT: `DefaultPalette` (packages/theme/src/colors/DefaultPalette.ts): themePrimary #0078d4, themeDarkAlt #106ebe, themeDark #005a9e,
//     themeDarker #004578, themeSecondary #2b88d8, themeTertiary #71afe5, themeLight #c7e0f4, themeLighter #deecf9, themeLighterAlt #eff6fc;
//     neutralDark #201f1e, neutralPrimary #323130, neutralPrimaryAlt #3b3a39, neutralSecondary #605e5c, neutralSecondaryAlt #8a8886,
//     neutralTertiary #a19f9d, neutralTertiaryAlt #c8c6c4, neutralQuaternary #d2d0ce, neutralQuaternaryAlt #e1dfdd, neutralLight #edebe9,
//     neutralLighter #f3f2f1, neutralLighterAlt #faf9f8, white #ffffff.
//   · DARK: `DarkDefaultPalette` (packages/theme-samples/src/DarkCustomizations): themePrimary #2899f5, themeDarkAlt #3aa0f3, themeDark #6cb8f6,
//     themeDarker #82c7ff, themeSecondary #0078d4; black #ffffff ... white #1b1a19 (the neutral ramp inverted: neutralLighter #252423, neutralLight
//     #292827, neutralQuaternaryAlt #323130, neutralQuaternary #3b3a39, neutralPrimary #f3f2f1, neutralSecondary #a19f9d).
//   · EFFECTS (packages/theme/src/effects/FluentDepths.ts): depth4 0 1.6 3.6 .132, depth8 0 3.2 7.2 .132, depth16 0 6.4 14.4 .132,
//     depth64 0 25.6 57.6 .22 (the second, tighter layer of each depth is dropped: the engine's shadow is one band). roundedCorner2 = 2.
//   · FONTS (packages/theme/src/fonts/FluentFonts.ts): 'Segoe UI'; small 12, medium 14, mediumPlus 16, large 18, xLarge 20, xLargePlus 24,
//     xxLarge 28, superLarge 42, mega 68; semibold 600 from xLarge up.

using FluentGpu.Foundation;
using ThemeKind = FluentGpu.Dsl.ThemeKind;

namespace Wavee.Themes.FluentV8;

/// <summary>The Fluent UI React v8 palette slots, per kind.</summary>
public static class FluentV8Palette
{
    private static ColorF Hex(uint rgb) => ColorF.FromRgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ── light (DefaultPalette) ──
    public static ColorF ThemePrimary { get; } = Hex(0x0078D4);
    public static ColorF ThemeDarkAlt { get; } = Hex(0x106EBE);
    public static ColorF ThemeDark { get; } = Hex(0x005A9E);
    public static ColorF ThemeDarker { get; } = Hex(0x004578);
    public static ColorF ThemeSecondary { get; } = Hex(0x2B88D8);
    public static ColorF ThemeTertiary { get; } = Hex(0x71AFE5);
    public static ColorF ThemeLight { get; } = Hex(0xC7E0F4);
    public static ColorF ThemeLighter { get; } = Hex(0xDEECF9);
    public static ColorF ThemeLighterAlt { get; } = Hex(0xEFF6FC);
    public static ColorF NeutralDark { get; } = Hex(0x201F1E);
    public static ColorF NeutralPrimary { get; } = Hex(0x323130);
    public static ColorF NeutralPrimaryAlt { get; } = Hex(0x3B3A39);
    public static ColorF NeutralSecondary { get; } = Hex(0x605E5C);
    public static ColorF NeutralSecondaryAlt { get; } = Hex(0x8A8886);
    public static ColorF NeutralTertiary { get; } = Hex(0xA19F9D);
    public static ColorF NeutralTertiaryAlt { get; } = Hex(0xC8C6C4);
    public static ColorF NeutralQuaternary { get; } = Hex(0xD2D0CE);
    public static ColorF NeutralQuaternaryAlt { get; } = Hex(0xE1DFDD);
    public static ColorF NeutralLight { get; } = Hex(0xEDEBE9);
    public static ColorF NeutralLighter { get; } = Hex(0xF3F2F1);
    public static ColorF NeutralLighterAlt { get; } = Hex(0xFAF9F8);
    public static ColorF White { get; } = Hex(0xFFFFFF);

    // ── dark (DarkDefaultPalette) ──
    public static ColorF DarkThemePrimary { get; } = Hex(0x2899F5);
    public static ColorF DarkThemeDarkAlt { get; } = Hex(0x3AA0F3);
    public static ColorF DarkThemeDark { get; } = Hex(0x6CB8F6);
    public static ColorF DarkThemeDarker { get; } = Hex(0x82C7FF);
    public static ColorF DarkThemeLight { get; } = Hex(0x004C87);
    public static ColorF DarkThemeLighter { get; } = Hex(0x043862);
    public static ColorF DarkNeutralDark { get; } = Hex(0xFAF9F8);
    public static ColorF DarkNeutralPrimary { get; } = Hex(0xF3F2F1);
    public static ColorF DarkNeutralPrimaryAlt { get; } = Hex(0xC8C6C4);
    public static ColorF DarkNeutralSecondary { get; } = Hex(0xA19F9D);
    public static ColorF DarkNeutralSecondaryAlt { get; } = Hex(0x979693);
    public static ColorF DarkNeutralTertiary { get; } = Hex(0x797775);
    public static ColorF DarkNeutralTertiaryAlt { get; } = Hex(0x484644);
    public static ColorF DarkNeutralQuaternary { get; } = Hex(0x3B3A39);
    public static ColorF DarkNeutralQuaternaryAlt { get; } = Hex(0x323130);
    public static ColorF DarkNeutralLight { get; } = Hex(0x292827);
    public static ColorF DarkNeutralLighter { get; } = Hex(0x252423);
    public static ColorF DarkNeutralLighterAlt { get; } = Hex(0x201F1E);
    public static ColorF DarkWhite { get; } = Hex(0x1B1A19);

    // ── the semantic slots the style paints with (v8 semanticColors), per kind ──

    /// <summary>bodyBackground: the page (white / #1b1a19).</summary>
    public static ColorF Body(ThemeKind k) => k == ThemeKind.Light ? White : DarkWhite;
    /// <summary>The frame around the page (title bar, Nav, player): neutralLighter.</summary>
    public static ColorF Frame(ThemeKind k) => k == ThemeKind.Light ? NeutralLighterAlt : DarkNeutralLighterAlt;
    /// <summary>bodyBackgroundHovered / a DetailsRow under the pointer: neutralLighter.</summary>
    public static ColorF Hovered(ThemeKind k) => k == ThemeKind.Light ? NeutralLighter : DarkNeutralLighter;
    /// <summary>bodyBackgroundChecked / a selected DetailsRow: neutralLight.</summary>
    public static ColorF Checked(ThemeKind k) => k == ThemeKind.Light ? NeutralLight : DarkNeutralLight;
    /// <summary>A selected DetailsRow under the pointer: neutralQuaternaryAlt.</summary>
    public static ColorF CheckedHovered(ThemeKind k) => k == ThemeKind.Light ? NeutralQuaternaryAlt : DarkNeutralQuaternaryAlt;
    /// <summary>bodyDivider: neutralLight.</summary>
    public static ColorF Divider(ThemeKind k) => k == ThemeKind.Light ? NeutralLight : DarkNeutralLight;
    /// <summary>buttonBorder: neutralSecondaryAlt.</summary>
    public static ColorF ButtonBorder(ThemeKind k) => k == ThemeKind.Light ? NeutralSecondaryAlt : DarkNeutralSecondaryAlt;
    /// <summary>inputBorder: neutralSecondary.</summary>
    public static ColorF InputBorder(ThemeKind k) => k == ThemeKind.Light ? NeutralSecondary : DarkNeutralSecondary;
    /// <summary>primaryButtonBackground (themePrimary), its hover (themeDarkAlt) and pressed (themeDark).</summary>
    public static ColorF Primary(ThemeKind k) => k == ThemeKind.Light ? ThemePrimary : DarkThemePrimary;
    public static ColorF PrimaryHovered(ThemeKind k) => k == ThemeKind.Light ? ThemeDarkAlt : DarkThemeDarkAlt;
    public static ColorF PrimaryPressed(ThemeKind k) => k == ThemeKind.Light ? ThemeDark : DarkThemeDark;
}
