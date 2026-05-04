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
