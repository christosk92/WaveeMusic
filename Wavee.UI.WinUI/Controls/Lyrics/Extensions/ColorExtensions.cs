// Ported from BetterLyrics by Zhe Fang — stripped SkiaSharp/LiveCharts deps

using Windows.UI;

namespace Wavee.UI.WinUI.Controls.Lyrics.Extensions;

public static class ColorExtensions
{
    public static Color WithAlpha(this Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    public static Color WithOpacity(this Color color, float opacity) =>
        Color.FromArgb((byte)(opacity * 255), color.R, color.G, color.B);
}
