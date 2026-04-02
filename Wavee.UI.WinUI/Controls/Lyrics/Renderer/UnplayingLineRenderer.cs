// Ported from BetterLyrics by Zhe Fang

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Numerics;
using Wavee.UI.WinUI.Controls.Lyrics.Extensions;
using Wavee.UI.WinUI.Controls.Lyrics.Models;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Lyrics.Renderer;

public class UnplayingLineRenderer
{
    public static void Draw(
        CanvasDrawingSession ds,
        ICanvasImage textOnlyLayer,
        int strokeWidth,
        RenderLyricsLine line)
    {
        var blurAmount = (float)line.BlurAmountTransition.Value;

        if (line.TertiaryTextLayout != null)
        {
            var opacity = line.PhoneticOpacityTransition.Value;
            DrawPart(ds, textOnlyLayer, line.TertiaryTextLayout, line.TertiaryPosition, strokeWidth, blurAmount, (float)opacity);
        }

        if (line.PrimaryTextLayout != null)
        {
            double opacity = Math.Max(line.PlayedPrimaryOpacityTransition.Value, line.UnplayedPrimaryOpacityTransition.Value);
            DrawPart(ds, textOnlyLayer, line.PrimaryTextLayout, line.PrimaryPosition, strokeWidth, blurAmount, (float)opacity);
        }

        if (line.SecondaryTextLayout != null)
        {
            var opacity = line.TranslatedOpacityTransition.Value;
            DrawPart(ds, textOnlyLayer, line.SecondaryTextLayout, line.SecondaryPosition, strokeWidth, blurAmount, (float)opacity);
        }
    }

    private static void DrawPart(
        CanvasDrawingSession ds,
        ICanvasImage source,
        CanvasTextLayout layout,
        Vector2 position,
        int strokeWidth,
        float blur,
        float opacity)
    {
        if (float.IsNaN(opacity) || opacity <= 0) return;

        var bounds = layout.LayoutBounds.Extend(strokeWidth / 2f);
        var destRect = new Rect(
            bounds.X + position.X,
            bounds.Y + position.Y,
            bounds.Width,
            bounds.Height);

        using var cropEffect = new CropEffect { Source = source, SourceRectangle = destRect, BorderMode = EffectBorderMode.Hard };
        using var blurEffect = new GaussianBlurEffect { BlurAmount = blur, Source = cropEffect, BorderMode = EffectBorderMode.Soft };
        using var opacityEffect = new OpacityEffect { Source = blurEffect, Opacity = opacity };
        ds.DrawImage(opacityEffect);
    }
}
