using Wavee.Controls.Lyrics.Models;
using System;

namespace Wavee.Controls.Lyrics.Helper.Lyrics
{
    public static class LyricsLayoutHelper
    {
        // 硬性限制
        private const float BaseMinFontSize = 14f;
        private const float BaseMaxFontSize = 80f;
        private const float TargetMinVisibleLines = 5f;
        private const float WidthPaddingRatio = 0.85f;

        // 比例配置
        private const float RatioSongTitle = 1f;
        private const float RatioArtist = 0.85f;
        private const float RatioAlbum = 0.75f;
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
                SongTitleSize = ApplyRatio(baseSize, RatioSongTitle),
                ArtistNameSize = ApplyRatio(baseSize, RatioArtist),
                AlbumNameSize = ApplyRatio(baseSize, RatioAlbum)
            };
        }

        private static float CalculateBaseFontSize(double width, double height)
        {
            float usableWidth = (float)width * WidthPaddingRatio;

            // 宽度 300~500px 时，除以 14 (字大)
            // 宽度 >1000px 时，除以 30 (字适中，展示更多内容)
            float targetCharsPerLine;
            if (width < 400)
            {
                targetCharsPerLine = 8f;
            }
            else if (width < 500)
            {
                // Smooth transition from sidebar (8) to normal (14)
                float t = (float)(width - 400) / 100f;
                targetCharsPerLine = 8f + 6f * t;
            }
            else if (width > 1000)
            {
                targetCharsPerLine = 30f;
            }
            else
            {
                float t = (float)(width - 500) / 500f;
                targetCharsPerLine = 14f + 16f * t;
            }

            float sizeByWidth = usableWidth / targetCharsPerLine;
            float sizeByHeight = (float)height / TargetMinVisibleLines;

            float targetSize = Math.Min(sizeByWidth, sizeByHeight);

            float currentMinLimit = (width < 500) ? 20f : BaseMinFontSize;

            return Math.Clamp(targetSize, currentMinLimit, BaseMaxFontSize);
        }

        private static float ApplyRatio(float baseSize, float ratio)
        {
            return Math.Max(baseSize * ratio, AbsoluteMinReadableSize);
        }
    }
}
