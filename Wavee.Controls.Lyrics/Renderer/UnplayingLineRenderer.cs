using Wavee.Controls.Lyrics.Extensions;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Numerics;
using Windows.Foundation;

namespace Wavee.Controls.Lyrics.Renderer
{
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
                DrawPart(ds, textOnlyLayer,
                    line.TertiaryTextLayout,
                    line.TertiaryPosition,
                    strokeWidth,
                    blurAmount,
                    (float)opacity,
                    line.GetTertiaryOverlayEffect(textOnlyLayer));
            }

            if (line.PrimaryTextLayout != null)
            {
                double opacity = Math.Max(line.PlayedPrimaryOpacityTransition.Value, line.UnplayedPrimaryOpacityTransition.Value);
                DrawPart(ds, textOnlyLayer,
                    line.PrimaryTextLayout,
                    line.PrimaryPosition,
                    strokeWidth,
                    blurAmount,
                    (float)opacity,
                    line.GetPrimaryOverlayEffect(textOnlyLayer));
            }

            if (line.SecondaryTextLayout != null)
            {
                var opacity = line.TranslatedOpacityTransition.Value;
                DrawPart(ds, textOnlyLayer,
                    line.SecondaryTextLayout,
                    line.SecondaryPosition,
                    strokeWidth,
                    blurAmount,
                    (float)opacity,
                    line.GetSecondaryOverlayEffect(textOnlyLayer));
            }
        }

        private static void DrawPart(
            CanvasDrawingSession ds,
            ICanvasImage source,
            CanvasTextLayout layout,
            Vector2 position,
            int strokeWidth,
            float blur,
            float opacity,
            OpacityEffect opacityEffect)
        {
            if (float.IsNaN(opacity) || opacity <= 0) return;

            var bounds = layout.LayoutBounds.Extend(strokeWidth / 2f);
            var destRect = new Rect(
                bounds.X + position.X,
                bounds.Y + position.Y,
                bounds.Width,
                bounds.Height
            );

            if (opacityEffect.Source is not GaussianBlurEffect blurEffect || blurEffect.Source is not CropEffect cropEffect) return;

            if (!ReferenceEquals(cropEffect.Source, source))
            {
                cropEffect.Source = source;
            }
            cropEffect.SourceRectangle = destRect;
            blurEffect.BlurAmount = blur;
            opacityEffect.Opacity = opacity;

            ds.DrawImage(opacityEffect);

            //ds.FillRectangle(destRect, Microsoft.UI.Colors.Red.WithAlpha(128));
        }
    }
}
