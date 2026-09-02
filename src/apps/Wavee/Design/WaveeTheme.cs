using FluentGpu.Dsl;

namespace Wavee;

/// <summary>Wavee theme bootstrap helpers, shared by Program + shell.
///
/// <para>Wavee always uses the engine's neutral palette now — the palette picker (Settings + the profile menu) is
/// gone, along with Mica Alt and the two Track-page-layout rows (Workstream B, "Settings regroup + removals"): they
/// were knobs nobody should have had. <see cref="ResolvePalette"/> stays as the ONE place that names the palette, so a
/// future preset re-introduction has exactly one call site to change.</para></summary>
static class WaveeTheme
{
    /// <summary>The palette Wavee renders with. Always <see cref="Tok.NeutralPalette"/> — kept as a method (not an
    /// inlined property read) so every caller reads the same single source instead of restating the constant.</summary>
    public static ThemePalette ResolvePalette() => Tok.NeutralPalette;

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
        Tok.Use(ResolvePalette(), kind);
        if (mode == 0)
        {
            // Prefer the exact OS accent ramp (theme-aware fills); else the base accent (SetAccent derives a ramp).
            if (FluentGpu.FluentApp.SystemAccentRamp() is { } ramp) Tok.SetAccent(in ramp);
            else if (FluentGpu.FluentApp.SystemAccent() is { } a) Tok.SetAccent(a);
        }
        settings?.Set(WaveeSettings.ThemeMode, mode);
    }
}
