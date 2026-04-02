// Ported from BetterLyrics by Zhe Fang

using System;

namespace Wavee.UI.WinUI.Controls.Lyrics.Helpers;

public struct LyricsLayoutMetrics
{
    public float MainLyricsSize;
    public float TranslationSize;
    public float TransliterationSize;
}

public static class LyricsLayoutHelper
{
    private const float BaseMinFontSize = 14f;
    private const float BaseMaxFontSize = 80f;
    private const float TargetMinVisibleLines = 5f;
    private const float WidthPaddingRatio = 0.85f;
    private const float RatioTranslation = 0.7f;
    private const float RatioTransliteration = 0.55f;
    private const float AbsoluteMinReadableSize = 10f;

    public static LyricsLayoutMetrics CalculateLayout(double width, double height)
    {
        float baseSize = CalculateBaseFontSize(width, height);
        return new LyricsLayoutMetrics
        {
            MainLyricsSize = baseSize,
            TranslationSize = ApplyRatio(baseSize, RatioTranslation),
            TransliterationSize = ApplyRatio(baseSize, RatioTransliteration),
        };
    }

    private static float CalculateBaseFontSize(double width, double height)
    {
        float usableWidth = (float)width * WidthPaddingRatio;

        float targetCharsPerLine;
        if (width < 500)
            targetCharsPerLine = 14f;
        else if (width > 1000)
            targetCharsPerLine = 30f;
        else
        {
            float t = (float)(width - 500) / 500f;
            targetCharsPerLine = 14f + 16f * t;
        }

        float sizeByWidth = usableWidth / targetCharsPerLine;
        float sizeByHeight = (float)height / TargetMinVisibleLines;
        float targetSize = Math.Min(sizeByWidth, sizeByHeight);
        float currentMinLimit = (width < 400) ? 16f : BaseMinFontSize;
        return Math.Clamp(targetSize, currentMinLimit, BaseMaxFontSize);
    }

    private static float ApplyRatio(float baseSize, float ratio) =>
        Math.Max(baseSize * ratio, AbsoluteMinReadableSize);
}
