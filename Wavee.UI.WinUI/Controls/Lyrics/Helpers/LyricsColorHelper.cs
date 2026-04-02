// Ported from BetterLyrics by Zhe Fang — stripped P/Invoke and SkiaSharp deps

using Windows.UI;

namespace Wavee.UI.WinUI.Controls.Lyrics.Helpers;

public static class LyricsColorHelper
{
    public static Color GetInterpolatedColor(double progress, Color startColor, Color targetColor)
    {
        byte Lerp(byte a, byte b) => (byte)(a + (progress * (b - a)));
        return Color.FromArgb(
            Lerp(startColor.A, targetColor.A),
            Lerp(startColor.R, targetColor.R),
            Lerp(startColor.G, targetColor.G),
            Lerp(startColor.B, targetColor.B));
    }
}
