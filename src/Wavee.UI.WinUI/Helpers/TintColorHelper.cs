using System;
using System.Globalization;
using Windows.UI;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Shared helpers for working with Spotify's extracted palette colors. The
/// palette values are tuned for 100% opaque page backgrounds — applied at
/// partial alpha they can collapse to near-black, so we lift them proportionally
/// before using them as tints.
/// </summary>
internal static class TintColorHelper
{
    /// <summary>
    /// Scales the RGB channels so the brightest channel lands at <paramref name="targetMax"/>,
    /// preserving hue and saturation. No-op if the color is already bright enough.
    /// </summary>
    public static Color BrightenForTint(Color color, byte targetMax = 190)
    {
        byte maxChannel = Math.Max(color.R, Math.Max(color.G, color.B));
        if (maxChannel >= targetMax || maxChannel == 0) return color;
        double factor = (double)targetMax / maxChannel;
        return Color.FromArgb(
            color.A,
            (byte)Math.Min(255, Math.Round(color.R * factor)),
            (byte)Math.Min(255, Math.Round(color.G * factor)),
            (byte)Math.Min(255, Math.Round(color.B * factor)));
    }

    /// <summary>
    /// Linear blend between two colors. Alpha is taken from <paramref name="from"/>;
    /// only RGB is interpolated. <paramref name="ratio"/> is clamped to [0, 1] —
    /// 0 returns the source color unchanged, 1 returns the target.
    /// </summary>
    public static Color BlendToward(Color from, Color to, double ratio)
    {
        ratio = Math.Clamp(ratio, 0.0, 1.0);
        return Color.FromArgb(
            from.A,
            (byte)Math.Round(from.R + (to.R - from.R) * ratio),
            (byte)Math.Round(from.G + (to.G - from.G) * ratio),
            (byte)Math.Round(from.B + (to.B - from.B) * ratio));
    }

    /// <summary>
    /// Lift a palette color toward white for Light-mode page washes / hero scrim.
    /// The palette tones Spotify ships are tuned for ~100% opaque dark backgrounds;
    /// dropping them onto a white theme — even at low alpha — leaves a darkened
    /// veil. Pre-blending toward white with <paramref name="whiteWeight"/> retains
    /// ~30% of the palette's hue while letting the page read pastel.
    /// </summary>
    public static Color LightTint(Color paletteColor, double whiteWeight = 0.7) =>
        BlendToward(paletteColor, Color.FromArgb(255, 255, 255, 255), whiteWeight);

    public static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;

        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6)
        {
            if (byte.TryParse(hex[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
                && byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
                && byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                color = Color.FromArgb(255, r, g, b);
                return true;
            }
            return false;
        }

        if (hex.Length == 8)
        {
            if (byte.TryParse(hex[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a)
                && byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
                && byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
                && byte.TryParse(hex[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                color = Color.FromArgb(a, r, g, b);
                return true;
            }
        }
        return false;
    }
}
