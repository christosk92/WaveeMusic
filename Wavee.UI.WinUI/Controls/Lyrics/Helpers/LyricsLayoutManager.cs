// Ported from BetterLyrics by Zhe Fang

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Wavee.UI.WinUI.Controls.Lyrics.Extensions;
using Wavee.UI.WinUI.Controls.Lyrics.Models;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Lyrics.Helpers;

public class LyricsLayoutManager
{
    public static void MeasureAndArrange(
        ICanvasAnimatedControl resourceCreator,
        IList<RenderLyricsLine>? lines,
        LyricsStyleSettings style,
        LyricsEffectSettings effect,
        double canvasWidth,
        double canvasHeight,
        double lyricsWidth,
        double lyricsHeight,
        bool createPhonetic = false,
        bool createTranslated = false)
    {
        if (lines == null || resourceCreator == null) return;

        int originalFontSize, phoneticFontSize, translatedFontSize;

        if (style.IsDynamicLyricsFontSize)
        {
            var metrics = LyricsLayoutHelper.CalculateLayout(canvasWidth, canvasHeight);
            phoneticFontSize = (int)metrics.TransliterationSize;
            originalFontSize = (int)metrics.MainLyricsSize;
            translatedFontSize = (int)metrics.TranslationSize;
        }
        else
        {
            phoneticFontSize = style.PhoneticLyricsFontSize;
            originalFontSize = style.OriginalLyricsFontSize;
            translatedFontSize = style.TranslatedLyricsFontSize;
        }

        var fontWeight = style.LyricsFontWeight;
        double currentY = 0;

        foreach (var line in lines)
        {
            if (line == null) continue;

            double actualWidth = 0;

            line.RecreateTextLayout(
                resourceCreator,
                createPhonetic,
                createTranslated,
                phoneticFontSize, originalFontSize, translatedFontSize,
                fontWeight,
                style.LyricsCJKFontFamily, style.LyricsWesternFontFamily,
                lyricsWidth, lyricsHeight, style.LyricsAlignmentType);

            line.RecreateTextGeometry();
            line.DisposeCaches();

            // Top-left
            line.TopLeftPosition = new Vector2(0, (float)currentY);

            // Tertiary (phonetic) layer
            line.TertiaryPosition = line.TopLeftPosition;
            if (line.TertiaryTextLayout != null)
            {
                currentY += line.TertiaryTextLayout.LayoutBounds.Height;
                currentY += (line.TertiaryTextLayout.LayoutBounds.Height / line.TertiaryTextLayout.LineCount) * 0.1;
                actualWidth = Math.Max(actualWidth, line.TertiaryTextLayout.LayoutBounds.Width);
            }

            // Primary layer
            line.PrimaryPosition = new Vector2(0, (float)currentY);
            if (line.PrimaryTextLayout != null)
            {
                currentY += line.PrimaryTextLayout.LayoutBounds.Height;
                actualWidth = Math.Max(actualWidth, line.PrimaryTextLayout.LayoutBounds.Width);
            }

            // Secondary (translation) layer
            if (line.PrimaryTextLayout != null && line.SecondaryTextLayout != null)
            {
                currentY += (line.SecondaryTextLayout.LayoutBounds.Height / line.SecondaryTextLayout.LineCount) * 0.1;
            }
            line.SecondaryPosition = new Vector2(0, (float)currentY);
            if (line.SecondaryTextLayout != null)
            {
                currentY += line.SecondaryTextLayout.LayoutBounds.Height;
                actualWidth = Math.Max(actualWidth, line.SecondaryTextLayout.LayoutBounds.Width);
            }

            // Bottom-right
            line.BottomRightPosition = new Vector2((float)actualWidth, (float)currentY);

            // Line spacing
            if (line.PrimaryTextLayout != null)
            {
                currentY += (line.PrimaryTextLayout.LayoutBounds.Height / line.PrimaryTextLayout.LineCount) * style.LyricsLineSpacingFactor;
            }

            // Alignment adjustments
            line.TopLeftPosition = style.LyricsAlignmentType switch
            {
                TextAlignmentType.Center => line.TopLeftPosition.AddX((float)((lyricsWidth - actualWidth) / 2)),
                TextAlignmentType.Right => line.TopLeftPosition.AddX((float)(lyricsWidth - actualWidth)),
                _ => line.TopLeftPosition
            };

            line.BottomRightPosition = style.LyricsAlignmentType switch
            {
                TextAlignmentType.Center => line.BottomRightPosition.AddX((float)((lyricsWidth - actualWidth) / 2)),
                TextAlignmentType.Right => line.BottomRightPosition.AddX((float)(lyricsWidth - actualWidth)),
                _ => line.BottomRightPosition
            };

            double centerY = (line.TopLeftPosition.Y + line.BottomRightPosition.Y) / 2;
            line.CenterPosition = style.LyricsAlignmentType switch
            {
                TextAlignmentType.Left => new Vector2(0, (float)centerY),
                TextAlignmentType.Center => new Vector2((float)(lyricsWidth / 2), (float)centerY),
                TextAlignmentType.Right => new Vector2((float)lyricsWidth, (float)centerY),
                _ => line.CenterPosition,
            };

            line.RecreateRenderChars(style.LyricsFontStrokeWidth);
        }
    }

    public static double? CalculateTargetScrollOffset(IList<RenderLyricsLine>? lines, int playingLineIndex)
    {
        if (lines == null || lines.Count == 0) return null;
        var currentLine = lines.ElementAtOrDefault(playingLineIndex);
        if (currentLine?.PrimaryTextLayout == null) return null;
        return -currentLine.CenterPosition.Y;
    }

    public static (int Start, int End) CalculateVisibleRange(
        IList<RenderLyricsLine>? lines,
        double currentScrollOffset,
        double lyricsY,
        double lyricsHeight,
        double canvasHeight,
        double playingLineTopOffsetFactor)
    {
        if (lines == null || lines.Count == 0) return (-1, -1);

        double offset = currentScrollOffset + lyricsY + lyricsHeight * playingLineTopOffsetFactor;

        int start = FindFirstVisibleLine(lines, offset);
        int end = FindLastVisibleLine(lines, offset, canvasHeight);

        if (start != -1 && end == -1)
            end = lines.Count - 1;

        return (start, end);
    }

    public static (int Start, int End) CalculateMaxRange(IList<RenderLyricsLine>? lines)
    {
        if (lines == null || lines.Count == 0) return (-1, -1);
        return (0, lines.Count - 1);
    }

    public static double CalculateActualHeight(IList<RenderLyricsLine>? lines)
    {
        if (lines == null || lines.Count == 0) return 0;
        return lines.Last().BottomRightPosition.Y;
    }

    public static void CalculateLanes(IList<RenderLyricsLine>? lines, int toleranceMs = 50)
    {
        if (lines == null) return;
        var lanesEndMs = new List<int> { 0 };

        foreach (var line in lines)
        {
            var start = line.StartMs;
            var end = line.EndMs;

            int assignedLane = -1;
            for (int i = 0; i < lanesEndMs.Count; i++)
            {
                if (lanesEndMs[i] <= start + toleranceMs)
                {
                    assignedLane = i;
                    break;
                }
            }

            if (assignedLane == -1)
            {
                assignedLane = lanesEndMs.Count;
                lanesEndMs.Add(0);
            }

            lanesEndMs[assignedLane] = end ?? 0;
            line.LaneIndex = assignedLane;
        }
    }

    public static int FindMouseHoverLineIndex(
        IList<RenderLyricsLine>? lines,
        bool isMouseInLyricsArea,
        Point mousePosition,
        double currentScrollOffset,
        double lyricsHeight,
        double playingLineTopOffsetFactor)
    {
        if (!isMouseInLyricsArea || lines == null || lines.Count == 0) return -1;

        double yOffset = currentScrollOffset + lyricsHeight * playingLineTopOffsetFactor;

        int left = 0, right = lines.Count - 1, result = -1;
        while (left <= right)
        {
            int mid = (left + right) / 2;
            var line = lines[mid];
            if (line.PrimaryTextLayout == null) break;
            double lineBottomY = yOffset + line.BottomRightPosition.Y;
            if (lineBottomY >= mousePosition.Y) { result = mid; right = mid - 1; }
            else { left = mid + 1; }
        }

        if (result != -1)
        {
            var line = lines[result];
            double lineTopY = yOffset + line.TopLeftPosition.Y;
            if (mousePosition.X < line.TopLeftPosition.X || mousePosition.X > line.BottomRightPosition.X || mousePosition.Y < lineTopY)
                result = -1;
        }

        return result;
    }

    private static int FindFirstVisibleLine(IList<RenderLyricsLine> lines, double offset)
    {
        int left = 0, right = lines.Count - 1, result = -1;
        while (left <= right)
        {
            int mid = (left + right) / 2;
            var line = lines[mid];
            if (line.PrimaryTextLayout == null) break;
            double value = offset + line.BottomRightPosition.Y;
            if (value >= 0) { result = mid; right = mid - 1; }
            else { left = mid + 1; }
        }
        return result;
    }

    private static int FindLastVisibleLine(IList<RenderLyricsLine> lines, double offset, double canvasHeight)
    {
        int left = 0, right = lines.Count - 1, result = -1;
        while (left <= right)
        {
            int mid = (left + right) / 2;
            var line = lines[mid];
            if (line.PrimaryTextLayout == null) break;
            double value = offset + line.BottomRightPosition.Y;
            if (value >= canvasHeight) { result = mid; right = mid - 1; }
            else { left = mid + 1; }
        }
        return result;
    }
}
