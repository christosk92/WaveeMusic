using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Wavee.WinUI.Models.Dto;
using Windows.UI;

namespace Wavee.WinUI.Helpers;

/// <summary>
/// Helper class for working with Spotify extracted colors
/// </summary>
public static class ColorHelper
{
    /// <summary>
    /// Parses a hex color string to a Color
    /// </summary>
    /// <param name="hex">Hex color string (e.g., "#FF5733" or "FF5733")</param>
    /// <returns>Color instance</returns>
    public static Color ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');

        if (hex.Length == 6)
        {
            // RGB format
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            return Color.FromArgb(255, r, g, b);
        }
        else if (hex.Length == 8)
        {
            // ARGB format
            byte a = Convert.ToByte(hex.Substring(0, 2), 16);
            byte r = Convert.ToByte(hex.Substring(2, 2), 16);
            byte g = Convert.ToByte(hex.Substring(4, 2), 16);
            byte b = Convert.ToByte(hex.Substring(6, 2), 16);
            return Color.FromArgb(a, r, g, b);
        }

        // Default to transparent
        return Colors.Transparent;
    }

    /// <summary>
    /// Gets the dominant color from extracted colors
    /// </summary>
    /// <param name="extractedColors">Extracted colors DTO</param>
    /// <param name="preferDark">Whether to prefer dark variant</param>
    /// <returns>Color instance</returns>
    public static Color GetDominantColor(ExtractedColorsDto? extractedColors)
    {
        if (extractedColors == null)
            return Colors.Transparent;

        var colorInfo = extractedColors.ColorDark;

        if (colorInfo == null || string.IsNullOrEmpty(colorInfo.Hex))
            return Colors.Transparent;

        return ParseHexColor(colorInfo.Hex);
    }

    /// <summary>
    /// Creates a SolidColorBrush from extracted colors
    /// </summary>
    /// <param name="extractedColors">Extracted colors DTO</param>
    /// <param name="preferDark">Whether to prefer dark variant</param>
    /// <returns>SolidColorBrush instance</returns>
    public static SolidColorBrush GetBrush(ExtractedColorsDto? extractedColors)
    {
        var color = GetDominantColor(extractedColors);
        var brush = new SolidColorBrush(color);
        System.Diagnostics.Debug.WriteLine($"[ColorHelper] GetBrush: extractedColors={extractedColors != null}, color={color}, brush={brush != null}, brush.Color.A={brush.Color.A}");
        return brush;
    }

    /// <summary>
    /// Desaturates a color by the specified amount and increases brightness for light mode
    /// </summary>
    /// <param name="color">Color to desaturate</param>
    /// <param name="amount">Amount to desaturate (0.0 = no change, 1.0 = grayscale)</param>
    /// <returns>Desaturated color</returns>
    public static Color DesaturateColor(Color color, double amount)
    {
        // Convert RGB to HSV
        RgbToHsv(color, out double h, out double s, out double v);

        // Reduce saturation
        s *= (1 - amount);

        // Increase brightness for light mode (20% brighter)
        v = Math.Min(1.0, v * 1.2);

        // Convert back to RGB
        return HsvToRgb(h, s, v, color.A);
    }

    /// <summary>
    /// Converts RGB color to HSV (Hue, Saturation, Value)
    /// </summary>
    private static void RgbToHsv(Color color, out double h, out double s, out double v)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        // Calculate Hue
        h = 0;
        if (delta > 0)
        {
            if (max == r)
                h = 60 * (((g - b) / delta) % 6);
            else if (max == g)
                h = 60 * (((b - r) / delta) + 2);
            else if (max == b)
                h = 60 * (((r - g) / delta) + 4);
        }
        if (h < 0) h += 360;

        // Calculate Saturation
        s = (max == 0) ? 0 : delta / max;

        // Calculate Value
        v = max;
    }

    /// <summary>
    /// Converts HSV (Hue, Saturation, Value) to RGB color
    /// </summary>
    private static Color HsvToRgb(double h, double s, double v, byte a = 255)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;

        double r, g, b;

        if (h < 60)
            (r, g, b) = (c, x, 0);
        else if (h < 120)
            (r, g, b) = (x, c, 0);
        else if (h < 180)
            (r, g, b) = (0, c, x);
        else if (h < 240)
            (r, g, b) = (0, x, c);
        else if (h < 300)
            (r, g, b) = (x, 0, c);
        else
            (r, g, b) = (c, 0, x);

        return Color.FromArgb(
            a,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255)
        );
    }
}
