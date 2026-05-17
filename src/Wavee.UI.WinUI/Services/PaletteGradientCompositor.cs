using System;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Helpers;
using Windows.UI;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Brushes derived from an <see cref="ArtistPalette"/> for a single theme.
/// Bundled into one descriptor so the caller only has to <c>set</c> four
/// VM properties from a single point and never has to recompute the parts
/// individually. Null fields mean "no palette available — caller should
/// fall back to system accent brushes".
/// </summary>
public sealed record GradientBrushDescriptor(
    Brush? SectionAccentBrush,
    Brush? HeroGradientBrush,
    Brush? AccentPillBrush,
    Brush? AccentPillForegroundBrush);

/// <summary>
/// Pure WinUI-side composition helper: turns an <see cref="ArtistPalette"/>
/// + theme + high-contrast flag into the bundle of accent / hero-gradient /
/// pill brushes that the artist hero binds against. Extracted from
/// <c>ArtistViewModel.ApplyTheme</c> so any other header VM can reuse the
/// same palette-to-brush logic without copy-pasting the alpha cadence /
/// luma-contrast text math. Stateless; safe to use as a DI singleton.
/// </summary>
public sealed class PaletteGradientCompositor
{
    /// <summary>
    /// Compose the four palette-derived brushes for the supplied palette /
    /// theme. Caller resolves <paramref name="isHighContrast"/> from the
    /// page's <c>AccessibilitySettings.HighContrast</c> + decides whether
    /// to drop into the system-accent fallback or not (returned brushes
    /// are null when the palette can't be projected).
    /// </summary>
    public GradientBrushDescriptor Compose(ArtistPalette? palette, bool isDarkTheme, bool isHighContrast)
    {
        // High-contrast theme paths should always fall back to system brushes
        // — the bespoke palette can defeat the user's contrast preference.
        if (palette is null || isHighContrast)
        {
            return BuildSystemFallback();
        }

        var tier = isDarkTheme
            ? (palette.HigherContrast ?? palette.HighContrast)
            : (palette.HighContrast ?? palette.HigherContrast);

        if (tier is null)
        {
            return BuildSystemFallback();
        }

        var bg = Color.FromArgb(255, tier.BackgroundR, tier.BackgroundG, tier.BackgroundB);
        var bgTint = Color.FromArgb(255, tier.BackgroundTintedR, tier.BackgroundTintedG, tier.BackgroundTintedB);

        // Use BackgroundTinted (the artist's actual cover-derived color) for
        // accents instead of TextAccent. TextAccent often resolves to Spotify's
        // brand green (#1DB954) regardless of the cover photo, which made every
        // artist accent look identical (and disconnected from the visual).
        var accentBase = TintColorHelper.BrightenForTint(bgTint, targetMax: 210);

        // Section bar — lifted accent. Drop alpha in Light mode so the bar reads
        // as an accent rather than a stoplight against the lighter page.
        var sectionAccentBrush = new SolidColorBrush(Color.FromArgb(
            (byte)(isDarkTheme ? 255 : 200), accentBase.R, accentBase.G, accentBase.B));

        // Hero scrim — same alpha cadence used by AlbumViewModel/PlaylistViewModel.
        // Light mode blends palette colors toward white and cuts alphas so dark
        // covers don't drag the page dark.
        var heroBg = isDarkTheme ? bg : TintColorHelper.LightTint(bg);
        var heroBgTint = isDarkTheme ? bgTint : TintColorHelper.LightTint(bgTint);
        var (a0, a1, a2, a3) = isDarkTheme ? (240, 176, 80, 0) : (140, 100, 50, 0);
        var heroGrad = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
        };
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a0, heroBgTint.R, heroBgTint.G, heroBgTint.B), Offset = 0.0 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a1, heroBg.R, heroBg.G, heroBg.B), Offset = 0.35 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a2, heroBg.R, heroBg.G, heroBg.B), Offset = 0.65 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a3, heroBg.R, heroBg.G, heroBg.B), Offset = 1.0 });

        // Play button — same lifted accent as the section bar so the page
        // reads as one color identity, with luma-based contrast text.
        var accentPillBrush = new SolidColorBrush(accentBase);
        var accentLuma = (accentBase.R * 299 + accentBase.G * 587 + accentBase.B * 114) / 1000;
        var accentPillForegroundBrush = new SolidColorBrush(
            accentLuma > 160 ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255));

        return new GradientBrushDescriptor(
            SectionAccentBrush: sectionAccentBrush,
            HeroGradientBrush: heroGrad,
            AccentPillBrush: accentPillBrush,
            AccentPillForegroundBrush: accentPillForegroundBrush);
    }

    /// <summary>
    /// Fallback bundle for "no palette" / high-contrast: use the system
    /// accent for play + section accents, null the hero gradient so bound
    /// elements render untinted, and pick the standard primary text-on-accent
    /// brush for foreground.
    /// </summary>
    private static GradientBrushDescriptor BuildSystemFallback()
    {
        // Fall back to system accent when no palette is available so the
        // Play button + section accent still render correctly on cold load.
        var systemAccent = ResolveSystemBrush("AccentFillColorDefaultBrush");
        var systemAccentForeground = ResolveSystemBrush("TextOnAccentFillColorPrimaryBrush");
        return new GradientBrushDescriptor(
            SectionAccentBrush: systemAccent,
            HeroGradientBrush: null,
            AccentPillBrush: systemAccent,
            AccentPillForegroundBrush: systemAccentForeground);
    }

    private static Brush? ResolveSystemBrush(string resourceKey)
    {
        if (Microsoft.UI.Xaml.Application.Current?.Resources is { } res
            && res.TryGetValue(resourceKey, out var value)
            && value is Brush brush)
            return brush;
        return null;
    }
}
