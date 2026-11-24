// ── Theming/Colours/CustomColours.cs — the user's own colour set for one mode, as one stored line (#184, plan 3.5) ─────────────
//
// `v1;ground=#1e1e2e;surface=#313244;ink=#cdd6f4;muted=#a6adc8;accent=#cba6f7;surface2=#181825` (`ColourKeys.CustomLight` / `.CustomDark`):
// the six seeds the editor offers, `surface2` (the lower plate: the player bar, a pane) optional. Parsed with string splitting and
// `ColorF.TryParseHex`: no JSON, nothing new for the trimmer, and the line is import-ready (a palette file is a file dialog away). A line that
// does not parse, or whose ground does not fit its mode (`ColourSolver.Fits`), reads as "no custom set" and is left in place.

using System.Collections.Immutable;
using System.Text;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming.Colours;

/// <summary>One of the custom set's six editable seeds, in the editor's order.</summary>
public enum CustomSeed : byte
{
    /// <summary>The page.</summary>
    Ground,
    /// <summary>The raised plates.</summary>
    Surface,
    /// <summary>The text.</summary>
    Ink,
    /// <summary>The muted text.</summary>
    Muted,
    /// <summary>The accent.</summary>
    Accent,
    /// <summary>The lower plate (optional; derived when absent).</summary>
    Surface2,
}

/// <summary>The custom set's stored line (see the file header).</summary>
public static class CustomColours
{
    /// <summary>The ids of the two custom sets.</summary>
    public const string LightId = "custom.light", DarkId = "custom.dark";

    private const string Version = "v1";

    /// <summary>The id of <paramref name="kind"/>'s custom set.</summary>
    public static string IdOf(ThemeKind kind) => kind == ThemeKind.Light ? LightId : DarkId;

    /// <summary>The number of seeds the editor shows.</summary>
    public const int SeedCount = 6;

    /// <summary>The seeds of <paramref name="line"/>, or null when it does not parse or its ground does not fit <paramref name="kind"/>.</summary>
    public static ColourSeeds? Parse(string? line, ThemeKind kind)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        string[] parts = line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !string.Equals(parts[0], Version, StringComparison.Ordinal)) return null;
        ColorF? ground = null, surface = null, ink = null, muted = null, accent = null, surface2 = null;
        for (int i = 1; i < parts.Length; i++)
        {
            int eq = parts[i].IndexOf('=');
            if (eq <= 0 || !ColorF.TryParseHex(parts[i][(eq + 1)..], out ColorF c)) return null;
            c = c with { A = 1f };
            switch (parts[i][..eq])
            {
                case "ground": ground = c; break;
                case "surface": surface = c; break;
                case "ink": ink = c; break;
                case "muted": muted = c; break;
                case "accent": accent = c; break;
                case "surface2": surface2 = c; break;
                default: return null;
            }
        }
        if (ground is not { } g || surface is not { } s || ink is not { } t || muted is not { } m || accent is not { } a) return null;
        if (!ColourSolver.Fits(kind, g)) return null;
        return new ColourSeeds(g, s, t, m, a, Well: surface2);
    }

    /// <summary>The stored line of <paramref name="seeds"/> (the optional lower plate only when given).</summary>
    public static string Format(in ColourSeeds seeds)
    {
        var sb = new StringBuilder(Version, 96);
        Add(sb, "ground", seeds.Ground);
        Add(sb, "surface", seeds.Surface);
        Add(sb, "ink", seeds.Ink);
        Add(sb, "muted", seeds.InkMuted);
        Add(sb, "accent", seeds.Accent);
        if (seeds.Well is { } w) Add(sb, "surface2", w);
        return sb.ToString();

        static void Add(StringBuilder sb, string name, ColorF c) => sb.Append(';').Append(name).Append("=#").Append(c.ToHex().ToLowerInvariant());
    }

    /// <summary>The custom set of <paramref name="kind"/> as a spec, or null for a missing or unreadable line.</summary>
    public static ColourSpec? SpecOf(string? line, ThemeKind kind, string name)
        => Parse(line, kind) is { } seeds ? new ColourSpec(IdOf(kind), name, name, kind, seeds, ImmutableArray<NamedAccent>.Empty) : null;

    /// <summary>The value of <paramref name="seed"/> in <paramref name="seeds"/> (the lower plate: its given value, or the solver's).</summary>
    public static ColorF Get(in ColourSeeds seeds, CustomSeed seed, ColourLadder solved) => seed switch
    {
        CustomSeed.Ground => seeds.Ground,
        CustomSeed.Surface => seeds.Surface,
        CustomSeed.Ink => seeds.Ink,
        CustomSeed.Muted => seeds.InkMuted,
        CustomSeed.Accent => seeds.Accent,
        _ => seeds.Well ?? solved.Well,
    };

    /// <summary><paramref name="seeds"/> with <paramref name="seed"/> set to <paramref name="colour"/>.</summary>
    public static ColourSeeds With(in ColourSeeds seeds, CustomSeed seed, ColorF colour)
    {
        colour = colour with { A = 1f };
        return seed switch
        {
            CustomSeed.Ground => seeds with { Ground = colour },
            CustomSeed.Surface => seeds with { Surface = colour },
            CustomSeed.Ink => seeds with { Ink = colour },
            CustomSeed.Muted => seeds with { InkMuted = colour },
            CustomSeed.Accent => seeds with { Accent = colour },
            _ => seeds with { Well = colour },
        };
    }

    /// <summary>The editor's starting seeds from a built-in set: its six editable seeds (the rest stay derived, as in a custom set).</summary>
    public static ColourSeeds From(ColourSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var s = spec.Seeds;
        return new ColourSeeds(s.Ground, s.Surface, s.Ink, s.InkMuted, s.Accent, Well: s.Well);
    }
}
