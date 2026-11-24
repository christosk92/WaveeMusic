// ── Theming/Colours/ColourCatalog.Data.cs — the built-in colour sets, as data ───────────────────────────────────────────────
//
// GENERATED VALUES, NOT TYPED ONES. The Catppuccin rows were written from the official palette file (catppuccin/palette
// `palette.json` v1.8.0, MIT): base → ground, surface0 → raised plate, text → ink, subtext0 → muted ink, overlay1 → faint ink,
// mantle → well, crust → deep band, mauve → the default accent, red / yellow / green → critical / caution / success, and the fourteen
// named accents in the family's own order. One exception, by design: Latte's raised plate is its base, not surface0. Latte's surface0
// (#ccd0da) is DARKER than its base, and the floors (tertiary text at 4.5 : 1 on every plate it sits on) then left the three ink voices
// under 0.02 of lightness apart, the secondary darker than the text itself; on the base they keep three distinct voices. Update the values
// from that file, never by hand. The names are the projects' own proper nouns
// (not localized); THIRD-PARTY-NOTICES.txt carries the attribution.
//
// Wavee's own pair (plan D6): Ink is the OLED set #196 asked for (a black ground, panels just off black, a saturated blue), Paper its
// warm-white day twin with the same blue.

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming.Colours;

public static partial class ColourCatalog
{
    private static ImmutableArray<ColourSpec> Build() => ImmutableArray.Create(
        WaveePaper(), CatppuccinLatte(),
        WaveeInk(), CatppuccinFrappe(), CatppuccinMacchiato(), CatppuccinMocha());

    private static ColorF Rgb(byte r, byte g, byte b) => ColorF.FromRgba(r, g, b);

    // ── Wavee ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static ColourSpec WaveeInk() => new("wavee.ink", "Wavee", "Ink", ThemeKind.Dark,
        new ColourSeeds(Ground: Rgb(0x00, 0x00, 0x00), Surface: Rgb(0x1A, 0x1A, 0x22), Ink: Rgb(0xF2, 0xF4, 0xF8),
            InkMuted: Rgb(0xA9, 0xAE, 0xBB), Accent: Rgb(0x3D, 0x7B, 0xFF), Well: Rgb(0x0B, 0x0B, 0x10), Deep: Rgb(0x00, 0x00, 0x00),
            InkFaint: Rgb(0x83, 0x88, 0x96)),
        ImmutableArray<NamedAccent>.Empty);

    private static ColourSpec WaveePaper() => new("wavee.paper", "Wavee", "Paper", ThemeKind.Light,
        new ColourSeeds(Ground: Rgb(0xFA, 0xF7, 0xF2), Surface: Rgb(0xFF, 0xFF, 0xFF), Ink: Rgb(0x1F, 0x1B, 0x16),
            InkMuted: Rgb(0x57, 0x50, 0x47), Accent: Rgb(0x2F, 0x5B, 0xD8), Well: Rgb(0xF1, 0xEC, 0xE4), Deep: Rgb(0xE8, 0xE2, 0xD8),
            InkFaint: Rgb(0x6E, 0x66, 0x5B)),
        ImmutableArray<NamedAccent>.Empty);

    // ── Catppuccin (generated, see the header) ──────────────────────────────────────────────────────────────────────────────

    private static ImmutableArray<NamedAccent> CatppuccinAccents(
        ColorF rosewater, ColorF flamingo, ColorF pink, ColorF mauve, ColorF red, ColorF maroon, ColorF peach,
        ColorF yellow, ColorF green, ColorF teal, ColorF sky, ColorF sapphire, ColorF blue, ColorF lavender) => ImmutableArray.Create(
        new NamedAccent("rosewater", "Rosewater", rosewater), new NamedAccent("flamingo", "Flamingo", flamingo),
        new NamedAccent("pink", "Pink", pink), new NamedAccent("mauve", "Mauve", mauve), new NamedAccent("red", "Red", red),
        new NamedAccent("maroon", "Maroon", maroon), new NamedAccent("peach", "Peach", peach), new NamedAccent("yellow", "Yellow", yellow),
        new NamedAccent("green", "Green", green), new NamedAccent("teal", "Teal", teal), new NamedAccent("sky", "Sky", sky),
        new NamedAccent("sapphire", "Sapphire", sapphire), new NamedAccent("blue", "Blue", blue), new NamedAccent("lavender", "Lavender", lavender));

    private static ColourSpec CatppuccinLatte() => new("catppuccin.latte", "Catppuccin", "Latte", ThemeKind.Light,
        new ColourSeeds(Ground: Rgb(0xEF, 0xF1, 0xF5), Surface: Rgb(0xEF, 0xF1, 0xF5), Ink: Rgb(0x4C, 0x4F, 0x69),
            InkMuted: Rgb(0x6C, 0x6F, 0x85), Accent: Rgb(0x88, 0x39, 0xEF), Well: Rgb(0xE6, 0xE9, 0xEF), Deep: Rgb(0xDC, 0xE0, 0xE8),
            InkFaint: Rgb(0x8C, 0x8F, 0xA1), Critical: Rgb(0xD2, 0x0F, 0x39), Caution: Rgb(0xDF, 0x8E, 0x1D), Success: Rgb(0x40, 0xA0, 0x2B)),
        CatppuccinAccents(
            Rgb(0xDC, 0x8A, 0x78), Rgb(0xDD, 0x78, 0x78), Rgb(0xEA, 0x76, 0xCB), Rgb(0x88, 0x39, 0xEF), Rgb(0xD2, 0x0F, 0x39), Rgb(0xE6, 0x45, 0x53), Rgb(0xFE, 0x64, 0x0B),
            Rgb(0xDF, 0x8E, 0x1D), Rgb(0x40, 0xA0, 0x2B), Rgb(0x17, 0x92, 0x99), Rgb(0x04, 0xA5, 0xE5), Rgb(0x20, 0x9F, 0xB5), Rgb(0x1E, 0x66, 0xF5), Rgb(0x72, 0x87, 0xFD)),
        DefaultAccent: "mauve");

    private static ColourSpec CatppuccinFrappe() => new("catppuccin.frappe", "Catppuccin", "Frappé", ThemeKind.Dark,
        new ColourSeeds(Ground: Rgb(0x30, 0x34, 0x46), Surface: Rgb(0x41, 0x45, 0x59), Ink: Rgb(0xC6, 0xD0, 0xF5),
            InkMuted: Rgb(0xA5, 0xAD, 0xCE), Accent: Rgb(0xCA, 0x9E, 0xE6), Well: Rgb(0x29, 0x2C, 0x3C), Deep: Rgb(0x23, 0x26, 0x34),
            InkFaint: Rgb(0x83, 0x8B, 0xA7), Critical: Rgb(0xE7, 0x82, 0x84), Caution: Rgb(0xE5, 0xC8, 0x90), Success: Rgb(0xA6, 0xD1, 0x89)),
        CatppuccinAccents(
            Rgb(0xF2, 0xD5, 0xCF), Rgb(0xEE, 0xBE, 0xBE), Rgb(0xF4, 0xB8, 0xE4), Rgb(0xCA, 0x9E, 0xE6), Rgb(0xE7, 0x82, 0x84), Rgb(0xEA, 0x99, 0x9C), Rgb(0xEF, 0x9F, 0x76),
            Rgb(0xE5, 0xC8, 0x90), Rgb(0xA6, 0xD1, 0x89), Rgb(0x81, 0xC8, 0xBE), Rgb(0x99, 0xD1, 0xDB), Rgb(0x85, 0xC1, 0xDC), Rgb(0x8C, 0xAA, 0xEE), Rgb(0xBA, 0xBB, 0xF1)),
        DefaultAccent: "mauve");

    private static ColourSpec CatppuccinMacchiato() => new("catppuccin.macchiato", "Catppuccin", "Macchiato", ThemeKind.Dark,
        new ColourSeeds(Ground: Rgb(0x24, 0x27, 0x3A), Surface: Rgb(0x36, 0x3A, 0x4F), Ink: Rgb(0xCA, 0xD3, 0xF5),
            InkMuted: Rgb(0xA5, 0xAD, 0xCB), Accent: Rgb(0xC6, 0xA0, 0xF6), Well: Rgb(0x1E, 0x20, 0x30), Deep: Rgb(0x18, 0x19, 0x26),
            InkFaint: Rgb(0x80, 0x87, 0xA2), Critical: Rgb(0xED, 0x87, 0x96), Caution: Rgb(0xEE, 0xD4, 0x9F), Success: Rgb(0xA6, 0xDA, 0x95)),
        CatppuccinAccents(
            Rgb(0xF4, 0xDB, 0xD6), Rgb(0xF0, 0xC6, 0xC6), Rgb(0xF5, 0xBD, 0xE6), Rgb(0xC6, 0xA0, 0xF6), Rgb(0xED, 0x87, 0x96), Rgb(0xEE, 0x99, 0xA0), Rgb(0xF5, 0xA9, 0x7F),
            Rgb(0xEE, 0xD4, 0x9F), Rgb(0xA6, 0xDA, 0x95), Rgb(0x8B, 0xD5, 0xCA), Rgb(0x91, 0xD7, 0xE3), Rgb(0x7D, 0xC4, 0xE4), Rgb(0x8A, 0xAD, 0xF4), Rgb(0xB7, 0xBD, 0xF8)),
        DefaultAccent: "mauve");

    private static ColourSpec CatppuccinMocha() => new("catppuccin.mocha", "Catppuccin", "Mocha", ThemeKind.Dark,
        new ColourSeeds(Ground: Rgb(0x1E, 0x1E, 0x2E), Surface: Rgb(0x31, 0x32, 0x44), Ink: Rgb(0xCD, 0xD6, 0xF4),
            InkMuted: Rgb(0xA6, 0xAD, 0xC8), Accent: Rgb(0xCB, 0xA6, 0xF7), Well: Rgb(0x18, 0x18, 0x25), Deep: Rgb(0x11, 0x11, 0x1B),
            InkFaint: Rgb(0x7F, 0x84, 0x9C), Critical: Rgb(0xF3, 0x8B, 0xA8), Caution: Rgb(0xF9, 0xE2, 0xAF), Success: Rgb(0xA6, 0xE3, 0xA1)),
        CatppuccinAccents(
            Rgb(0xF5, 0xE0, 0xDC), Rgb(0xF2, 0xCD, 0xCD), Rgb(0xF5, 0xC2, 0xE7), Rgb(0xCB, 0xA6, 0xF7), Rgb(0xF3, 0x8B, 0xA8), Rgb(0xEB, 0xA0, 0xAC), Rgb(0xFA, 0xB3, 0x87),
            Rgb(0xF9, 0xE2, 0xAF), Rgb(0xA6, 0xE3, 0xA1), Rgb(0x94, 0xE2, 0xD5), Rgb(0x89, 0xDC, 0xEB), Rgb(0x74, 0xC7, 0xEC), Rgb(0x89, 0xB4, 0xFA), Rgb(0xB4, 0xBE, 0xFE)),
        DefaultAccent: "mauve");
}
