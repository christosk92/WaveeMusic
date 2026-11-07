// ── Themes/SwiftUI/Style/SwiftUITokens.cs — the SwiftUI spike's colours and glass recipes (SPIKE, not for release) ─────────────
//
// The values of Apple's current design language (iOS 26 / macOS 26 "Tahoe", Liquid Glass), as far as Windows can draw them:
//   · the macOS 26 dark and light window grounds (#1E1E1E content on dark, #FFFFFF on light) and the system label ladder, the secondary and
//     tertiary inks raised from Apple's 55 % / 25 % (dark) and 50 % / 26 % (light) to pass the inspector's AA gate (Apple's own fail it);
//   · systemBlue (#007AFF light, #0A84FF dark; the accessible variants #0040DD / #409CFF for accent TEXT) as the accent;
//   · the separators (macOS separatorColor: white 10 % on dark, black 10 % on light);
//   · LIQUID GLASS approximated with the engine's per-node acrylic: a blurred, luminosity-blended backdrop under a thin tint, a vertical
//     gradient rim (the specular edge: bright at the top, faint at the bottom) and a soft drop shadow. The engine has no refraction (lensing
//     at the edges), no adaptive light/dark flip of the glass by what is behind it and no vibrancy (ink that blends with the backdrop): those
//     are the spike's engine gaps.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.SwiftUI;

/// <summary>The SwiftUI spike's colours and glass recipes, per kind.</summary>
public static class SwiftUITokens
{
    // ── grounds ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The dark window and content ground (macOS 26 textBackground / Music's content, #1E1E1E).</summary>
    public static ColorF DarkGround { get; } = ColorF.FromRgba(0x1E, 0x1E, 0x1E);
    /// <summary>The dark raised plate (secondarySystemGroupedBackground #1C1C1E lifted to read over #1E1E1E: #2C2C2E, tertiarySystemBackground).</summary>
    public static ColorF DarkRaised { get; } = ColorF.FromRgba(0x2C, 0x2C, 0x2E);
    /// <summary>The dark grouped ground (systemGroupedBackground on macOS dark, between the ground and the raised rows).</summary>
    public static ColorF DarkGrouped { get; } = ColorF.FromRgba(0x18, 0x18, 0x18);
    /// <summary>The light window and content ground (#FFFFFF).</summary>
    public static ColorF LightGround { get; } = ColorF.FromRgba(0xFF, 0xFF, 0xFF);
    /// <summary>The light grouped ground (systemGroupedBackground #F2F2F7).</summary>
    public static ColorF LightGrouped { get; } = ColorF.FromRgba(0xF2, 0xF2, 0xF7);
    /// <summary>The light raised plate (secondarySystemBackground #F2F2F7 is the grouped ground; rows on it are white).</summary>
    public static ColorF LightRaised { get; } = ColorF.FromRgba(0xFF, 0xFF, 0xFF);

    // ── the label ladder (raised for AA where Apple's fails) ─────────────────────────────────────────────────────────────────

    /// <summary>labelColor: white 85 % on dark, black 85 % on light (Apple's).</summary>
    public const float LabelA = 0.85f;
    /// <summary>secondaryLabel: 60 % (Apple: 55 % dark, 50 % light; raised for AA on the small meta lines).</summary>
    public const float SecondaryDarkA = 0.60f, SecondaryLightA = 0.62f;
    /// <summary>tertiaryLabel: 52 % dark, 55 % light (Apple: 25 % / 26 %, which reads 1.9 : 1).</summary>
    public const float TertiaryDarkA = 0.52f, TertiaryLightA = 0.55f;
    /// <summary>quaternaryLabel, the disabled ink: 25 % (Apple: 10 %).</summary>
    public const float DisabledA = 0.28f;

    /// <summary>separatorColor: white 10 % on dark, black 10 % on light.</summary>
    public const float SeparatorA = 0.10f;

    /// <summary>A row's hover fill (macOS unemphasized selection, quaternarySystemFill): 8 % / 5 %.</summary>
    public const float HoverDarkA = 0.08f, HoverLightA = 0.05f;
    /// <summary>A row's pressed fill: 12 % / 8 %.</summary>
    public const float PressedDarkA = 0.12f, PressedLightA = 0.08f;
    /// <summary>A selected row (the sidebar's selection, a rounded plate): 14 % / 9 %.</summary>
    public const float SelectedDarkA = 0.14f, SelectedLightA = 0.09f;

    // ── systemBlue ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>systemBlue on light (#007AFF) and on dark (#0A84FF).</summary>
    public static ColorF BlueLight { get; } = ColorF.FromRgba(0x00, 0x7A, 0xFF);
    /// <summary>systemBlue on dark.</summary>
    public static ColorF BlueDark { get; } = ColorF.FromRgba(0x0A, 0x84, 0xFF);

    /// <summary>The engine's accent ramp from systemBlue: <c>Tok.AccentDefault</c> is Light2 on dark (#0A84FF) and Dark1 on light (#007AFF); the
    /// accent TEXT is Light3 on dark (#409CFF, the accessible dark variant) and Dark2 on light (#0040DD, the accessible light variant).</summary>
    public static AccentRamp Accent { get; } = new(
        Base: ColorF.FromRgba(0x00, 0x7A, 0xFF),
        Light1: ColorF.FromRgba(0x1F, 0x8B, 0xFF), Light2: ColorF.FromRgba(0x0A, 0x84, 0xFF), Light3: ColorF.FromRgba(0x40, 0x9C, 0xFF),
        Dark1: ColorF.FromRgba(0x00, 0x6E, 0xE6), Dark2: ColorF.FromRgba(0x00, 0x40, 0xDD), Dark3: ColorF.FromRgba(0x00, 0x2A, 0x99));

    // ── Liquid Glass (approximated) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The glass blur (Gaussian sigma, DIP). Apple's regular glass reads like a ~20-30 pt blur; 24 keeps the art's colour fields
    /// legible as colour without detail.</summary>
    public const float GlassBlur = 24f;

    /// <summary>The regular glass on dark: a #2A2A2C tint at 30 %, the backdrop's lightness pulled 55 % toward it (the rest passes: the glass stays
    /// see-through), a 1.5 % grain; the fallback (transparency off) is the opaque #2A2A2C.</summary>
    public static AcrylicSpec GlassDark { get; } = new(ColorF.FromRgba(0x2A, 0x2A, 0x2C), 0.30f, GlassBlur, 0.015f, 0.55f, ColorF.FromRgba(0x2A, 0x2A, 0x2C));

    /// <summary>The regular glass on light: a #FAFAFC tint at 35 %, lightness 65 % toward it, the opaque #F5F5F7 fallback.</summary>
    public static AcrylicSpec GlassLight { get; } = new(ColorF.FromRgba(0xFA, 0xFA, 0xFC), 0.35f, GlassBlur, 0.015f, 0.65f, ColorF.FromRgba(0xF5, 0xF5, 0xF7));

    /// <summary>The glass of the active kind.</summary>
    public static AcrylicSpec Glass => (Tok.Theme == ThemeKind.Dark) ? GlassDark : GlassLight;

    /// <summary>The glass plate's resting wash over the blur: #26262A at 55 % on dark, #F0F0F4 at 78 % on light (a grey glass, so the light sidebar reads against the white window). Apple keeps text on glass legible
    /// with vibrancy (ink that adapts to the blurred backdrop); the engine has none, so the wash carries the contrast (4.5 : 1 for the
    /// secondary ink over bright art) and the glass reads a little more frosted than Apple's.</summary>
    public static ColorF GlassWash => (Tok.Theme == ThemeKind.Dark) ? ColorF.FromRgba(0x26, 0x26, 0x2A, 0x8C) : ColorF.FromRgba(0xF0, 0xF0, 0xF4, 0xC7);

    /// <summary>The specular rim: a vertical gradient, white 22 % at the top to white 6 % at the foot on dark, white 90 % to black 6 % on light.</summary>
    public static GradientSpec GlassRim => (Tok.Theme == ThemeKind.Dark)
        ? GradientSpec.Vertical(ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x38), ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x10))
        : GradientSpec.Vertical(ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xE6), ColorF.FromRgba(0x00, 0x00, 0x00, 0x10));

    /// <summary>The floating glass shadow: 24 blur, 8 down, black 35 % on dark and 12 % on light.</summary>
    public static ShadowSpec GlassShadow => new(24f, 8f, 0f, (Tok.Theme == ThemeKind.Dark) ? ColorF.FromRgba(0, 0, 0, 0x59) : ColorF.FromRgba(0, 0, 0, 0x1F));

    /// <summary>The ink a state wash is made of: white on dark, black on light.</summary>
    public static ColorF Wash(bool dark, float alpha)
        => dark ? ColorF.FromRgba(0xFF, 0xFF, 0xFF) with { A = alpha } : ColorF.FromRgba(0, 0, 0) with { A = alpha };
}
