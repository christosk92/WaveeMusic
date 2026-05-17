using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Services;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.RightPanel;

/// <summary>
/// Pure colour-math helpers used by the right-panel chrome. Extracted from
/// <see cref="RightPanelView"/> so the parent control composes only — every
/// function here is referentially transparent: the same inputs always produce
/// the same <see cref="Color"/> / <see cref="LyricsPalette"/>.
/// </summary>
/// <remarks>
/// Why static: the inputs are values, not services. The functions read theme
/// resources from <see cref="Application.Current"/>, but otherwise carry no
/// state — they don't observe ViewModels, don't hold timers, don't allocate
/// composition objects. Keeps unit-test-style reasoning trivial.
/// </remarks>
internal static class RightPanelThemeResolver
{
    /// <summary>
    /// Resolve a colour from the application's theme dictionaries by key. Walks
    /// the active <see cref="ElementTheme"/> first, then "Default", then any
    /// merged dictionaries. Returns <paramref name="fallback"/> on miss.
    /// </summary>
    public static Color ResolveThemeColor(ElementTheme actualTheme, string resourceKey, Color fallback)
    {
        var themeKey = actualTheme switch
        {
            ElementTheme.Light => "Light",
            ElementTheme.Dark => "Dark",
            _ => "Default"
        };

        if (TryResolveColorFromResources(Application.Current.Resources, resourceKey, themeKey, out var color))
            return color;

        return fallback;
    }

    /// <summary>
    /// The right panel's primary tint colour — extracted from the current album
    /// art when available, else theme-accent, else a neutral fallback.
    /// </summary>
    public static Color GetBackgroundTintColor(
        ElementTheme actualTheme,
        ThemeColorService? themeColors,
        ExtractedColor? extractedColor)
    {
        var albumHex = GetBackgroundTintHex(actualTheme, extractedColor);
        if (TryParseHexColor(albumHex, out var albumColor))
            return albumColor;

        if (themeColors?.AccentFill is SolidColorBrush accentBrush)
            return accentBrush.Color;

        return actualTheme == ElementTheme.Light
            ? Color.FromArgb(255, 110, 132, 148)
            : Color.FromArgb(255, 84, 116, 140);
    }

    /// <summary>
    /// Effective surface colour used by the panel chrome — preferring the
    /// secondary card brush so blends sit on the same plane as embedded cards.
    /// </summary>
    public static Color GetPanelSurfaceColor(ElementTheme actualTheme, ThemeColorService? themeColors)
    {
        if (themeColors?.CardBackgroundSecondary is SolidColorBrush secondaryBrush)
            return secondaryBrush.Color;

        if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorSecondaryBrush", out var brushObj)
            && brushObj is SolidColorBrush themeBrush)
        {
            return themeBrush.Color;
        }

        return actualTheme == ElementTheme.Light
            ? Color.FromArgb(255, 245, 245, 245)
            : Color.FromArgb(255, 30, 30, 30);
    }

    /// <summary>
    /// Pick the extracted-colour variant that contrasts against the active
    /// theme. Returns <c>null</c> when no extraction has happened yet.
    /// </summary>
    public static string? GetBackgroundTintHex(ElementTheme actualTheme, ExtractedColor? extractedColor)
    {
        if (extractedColor != null)
        {
            var isLightTheme = actualTheme == ElementTheme.Light;
            return isLightTheme
                ? extractedColor.DarkHex ?? extractedColor.RawHex
                : extractedColor.LightHex ?? extractedColor.RawHex;
        }

        return null;
    }

    /// <summary>
    /// Compose an opaque RGB by alpha-blending an overlay colour over a base.
    /// <paramref name="overlayWeight"/> is the overlay's contribution in [0,1].
    /// </summary>
    public static Color BlendColors(Color baseColor, Color overlayColor, float overlayWeight)
    {
        overlayWeight = Math.Clamp(overlayWeight, 0f, 1f);
        var baseWeight = 1f - overlayWeight;

        return Color.FromArgb(
            255,
            (byte)Math.Clamp((baseColor.R * baseWeight) + (overlayColor.R * overlayWeight), 0, 255),
            (byte)Math.Clamp((baseColor.G * baseWeight) + (overlayColor.G * overlayWeight), 0, 255),
            (byte)Math.Clamp((baseColor.B * baseWeight) + (overlayColor.B * overlayWeight), 0, 255));
    }

    /// <summary>
    /// Multiply each colour channel by <c>1 - amount</c>. Keeps alpha at 255.
    /// </summary>
    public static Color Darken(Color color, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        var scale = 1f - amount;
        return Color.FromArgb(
            255,
            (byte)Math.Clamp(color.R * scale, 0, 255),
            (byte)Math.Clamp(color.G * scale, 0, 255),
            (byte)Math.Clamp(color.B * scale, 0, 255));
    }

    /// <summary>Return <paramref name="color"/> with a new alpha channel.</summary>
    public static Color WithAlpha(Color color, byte alpha)
    {
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    /// <summary>
    /// Parse <c>#RRGGBB</c> or <c>#AARRGGBB</c> (with or without leading hash).
    /// Returns <c>true</c> on success.
    /// </summary>
    public static bool TryParseHexColor(string? hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        var normalized = hex.Trim().TrimStart('#');
        if (normalized.Length == 6
            && byte.TryParse(normalized[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(normalized[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(normalized[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            color = Color.FromArgb(255, r, g, b);
            return true;
        }

        if (normalized.Length == 8
            && byte.TryParse(normalized[..2], System.Globalization.NumberStyles.HexNumber, null, out var a)
            && byte.TryParse(normalized[2..4], System.Globalization.NumberStyles.HexNumber, null, out var r8)
            && byte.TryParse(normalized[4..6], System.Globalization.NumberStyles.HexNumber, null, out var g8)
            && byte.TryParse(normalized[6..8], System.Globalization.NumberStyles.HexNumber, null, out var b8))
        {
            color = Color.FromArgb(a, r8, g8, b8);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Effective panel-background colour used to fade the bottom of the tab
    /// content into the panel chrome. Composites the optional card colour over
    /// the OS theme base so the gradient terminal matches what the user sees.
    /// </summary>
    public static Color ResolveTabFadeTargetColor(
        ElementTheme actualTheme,
        bool isEmbeddedChromeTransparent,
        string? embeddedHostTintHex,
        ThemeColorService? themeColors,
        ExtractedColor? extractedColor)
    {
        if (isEmbeddedChromeTransparent)
            return ResolveEmbeddedTabFadeTargetColor(actualTheme, embeddedHostTintHex, themeColors, extractedColor);

        // Mirror UpdateCanvasClearColor() so the fade's terminal colour matches the
        // panel's effective background in both themes.
        var cardColor = (themeColors?.CardBackground as SolidColorBrush)?.Color;

        Color baseColor;
        if (Application.Current.Resources.TryGetValue("SolidBackgroundFillColorBase", out var baseObj)
            && baseObj is Color resolved)
        {
            baseColor = resolved;
        }
        else
        {
            baseColor = actualTheme == ElementTheme.Light
                ? Color.FromArgb(255, 243, 243, 243)
                : Color.FromArgb(255, 32, 32, 32);
        }

        if (cardColor is { } card && card.A > 0)
        {
            float a = card.A / 255f;
            return Color.FromArgb(255,
                (byte)(card.R * a + baseColor.R * (1 - a)),
                (byte)(card.G * a + baseColor.G * (1 - a)),
                (byte)(card.B * a + baseColor.B * (1 - a)));
        }

        return baseColor;
    }

    /// <summary>
    /// Tab-fade target colour for embedded hosts — biases toward the host's
    /// tint so the fade blends into the host palette instead of the standalone
    /// panel background.
    /// </summary>
    public static Color ResolveEmbeddedTabFadeTargetColor(
        ElementTheme actualTheme,
        string? embeddedHostTintHex,
        ThemeColorService? themeColors,
        ExtractedColor? extractedColor)
    {
        if (TryParseHexColor(embeddedHostTintHex, out var hostTint))
        {
            if (actualTheme == ElementTheme.Dark)
            {
                return Color.FromArgb(
                    255,
                    (byte)(hostTint.R * 0.30f),
                    (byte)(hostTint.G * 0.30f),
                    (byte)(hostTint.B * 0.30f));
            }

            const float blend = 0.72f;
            return Color.FromArgb(
                255,
                (byte)(hostTint.R * (1 - blend) + 255 * blend),
                (byte)(hostTint.G * (1 - blend) + 255 * blend),
                (byte)(hostTint.B * (1 - blend) + 255 * blend));
        }

        var tintColor = GetBackgroundTintColor(actualTheme, themeColors, extractedColor);
        return actualTheme == ElementTheme.Light
            ? BlendColors(Color.FromArgb(255, 244, 246, 248), tintColor, 0.28f)
            : Darken(tintColor, 0.64f);
    }

    /// <summary>
    /// Compose the swap-chain-friendly clear colour used by
    /// <c>NowPlayingCanvas</c>. SwapChainPanels can't blend with XAML content,
    /// so we composite the semi-transparent card brush onto an opaque base.
    /// </summary>
    public static Color ComputeCanvasClearColor(
        ElementTheme actualTheme,
        bool isEmbeddedChromeTransparent,
        ThemeColorService? themeColors)
    {
        if (isEmbeddedChromeTransparent)
            return Colors.Transparent;

        var cardColor = (themeColors?.CardBackground as SolidColorBrush)?.Color
                        ?? Colors.Transparent;

        Color baseColor;
        if (Application.Current.Resources.TryGetValue("SolidBackgroundFillColorBase", out var baseObj)
            && baseObj is Color resolved)
        {
            baseColor = resolved;
        }
        else
        {
            baseColor = actualTheme == ElementTheme.Light
                ? Color.FromArgb(255, 243, 243, 243)
                : Color.FromArgb(255, 32, 32, 32);
        }

        float a = cardColor.A / 255f;
        return Color.FromArgb(174,
            (byte)(cardColor.R * a + baseColor.R * (1 - a)),
            (byte)(cardColor.G * a + baseColor.G * (1 - a)),
            (byte)(cardColor.B * a + baseColor.B * (1 - a)));
    }

    private static bool TryResolveColorFromResources(
        ResourceDictionary resources,
        string resourceKey,
        string themeKey,
        out Color color)
    {
        if (TryResolveColorFromDictionary(resources, resourceKey, out color))
            return true;

        if (resources.ThemeDictionaries.TryGetValue(themeKey, out var themed)
            && themed is ResourceDictionary themedDict
            && TryResolveColorFromDictionary(themedDict, resourceKey, out color))
        {
            return true;
        }

        if (themeKey != "Default"
            && resources.ThemeDictionaries.TryGetValue("Default", out var fallbackThemed)
            && fallbackThemed is ResourceDictionary fallbackDict
            && TryResolveColorFromDictionary(fallbackDict, resourceKey, out color))
        {
            return true;
        }

        foreach (var merged in resources.MergedDictionaries)
        {
            if (TryResolveColorFromResources(merged, resourceKey, themeKey, out color))
                return true;
        }

        color = Colors.Transparent;
        return false;
    }

    private static bool TryResolveColorFromDictionary(
        ResourceDictionary dictionary,
        string resourceKey,
        out Color color)
    {
        if (dictionary.TryGetValue(resourceKey, out var value))
        {
            switch (value)
            {
                case Color c:
                    color = c;
                    return true;
                case SolidColorBrush brush:
                    color = brush.Color;
                    return true;
            }
        }

        color = Colors.Transparent;
        return false;
    }
}
