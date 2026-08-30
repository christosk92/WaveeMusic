using FluentGpu.Dsl;

namespace Wavee;

/// <summary>Wavee theme bootstrap helpers (palette id → <see cref="ThemePalette"/>, shared by Program + shell).</summary>
static class WaveeTheme
{
    /// <summary>Palette id → the palette, for the four ids the Settings picker offers.
    ///
    /// <para>This DELEGATES to the engine's own <see cref="Tok.PaletteById"/> rather than restating its arms. The
    /// restatement is what made the Warm preset unreachable: this switch carried "slate"/"neutral"/"accent" but not
    /// "warm", while Settings has always offered a Warm swatch that persists <c>"warm"</c> — so choosing it fell
    /// through the default arm to Neutral, silently, and every value <c>PaletteBuilder.BuildWarmLight</c> composes was
    /// dead code the app could not reach. One resolver means adding a preset to the engine cannot leave the app behind
    /// again; the default arm stays as the answer for an unknown/corrupt persisted id.</para></summary>
    public static ThemePalette ResolvePalette(string id) => Tok.PaletteById(id) ?? Tok.NeutralPalette;

    public static void ApplyPalette(string id, IAppSettings? settings = null)
    {
        Tok.Use(ResolvePalette(id), Tok.Theme);
        settings?.Set(WaveeSettings.PaletteId, id);
    }

    /// <summary>Apply + persist the theme-mode preference (0 System · 1 Light · 2 Dark) — the same resolution Program.cs
    /// runs at startup and WaveeApp runs on a live OS flip. System re-reads the OS theme (and accent) immediately.</summary>
    public static void ApplyThemeMode(int mode, IAppSettings? settings = null)
    {
        var kind = mode switch
        {
            1 => ThemeKind.Light,
            2 => ThemeKind.Dark,
            _ => FluentGpu.FluentApp.SystemUsesLightTheme() ? ThemeKind.Light : ThemeKind.Dark,
        };
        Tok.Use(ResolvePalette(settings?.Get(WaveeSettings.PaletteId) ?? Tok.Palette.Id), kind);
        if (mode == 0)
        {
            // Prefer the exact OS accent ramp (theme-aware fills); else the base accent (SetAccent derives a ramp).
            if (FluentGpu.FluentApp.SystemAccentRamp() is { } ramp) Tok.SetAccent(in ramp);
            else if (FluentGpu.FluentApp.SystemAccent() is { } a) Tok.SetAccent(a);
        }
        settings?.Set(WaveeSettings.ThemeMode, mode);
    }
}
