// Ported from BetterLyrics by Zhe Fang

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Numerics;
using Wavee.UI.WinUI.Controls.Lyrics.Extensions;
using Wavee.UI.WinUI.Controls.Lyrics.Models;
using Windows.Foundation;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.Lyrics.Renderer;

public class LyricsRenderer : BreathingRendererBase
{
    private Matrix4x4 _threeDimMatrix = Matrix4x4.Identity;

    public void Draw(
        ICanvasAnimatedControl control,
        CanvasDrawingSession ds,
        IList<RenderLyricsLine>? lines,
        int mouseHoverLineIndex,
        bool isMousePressing,
        int startVisibleIndex,
        int endVisibleIndex,
        double lyricsX,
        double lyricsY,
        double lyricsWidth,
        double lyricsHeight,
        double userScrollOffset,
        double lyricsOpacity,
        double playingLineTopOffsetFactor,
        LyricsEffectSettings effectSettings,
        LyricsStyleSettings styleSettings,
        double currentProgressMs)
    {
        if (lyricsOpacity == 0) return;

        if (effectSettings.Is3DLyricsEnabled)
        {
            using var layer = new CanvasCommandList(control);
            using (var layerDs = layer.CreateDrawingSession())
            {
                DrawLyrics(control, layerDs, lines, mouseHoverLineIndex, isMousePressing,
                    startVisibleIndex, endVisibleIndex, lyricsX, lyricsY, lyricsWidth, lyricsHeight,
                    userScrollOffset, playingLineTopOffsetFactor, effectSettings, styleSettings, currentProgressMs);
            }
            ds.DrawImage(new Transform3DEffect { Source = layer, TransformMatrix = _threeDimMatrix });
        }
        else
        {
            DrawLyrics(control, ds, lines, mouseHoverLineIndex, isMousePressing,
                startVisibleIndex, endVisibleIndex, lyricsX, lyricsY, lyricsWidth, lyricsHeight,
                userScrollOffset, playingLineTopOffsetFactor, effectSettings, styleSettings, currentProgressMs);
        }
    }

    private void DrawLyrics(
        ICanvasAnimatedControl control,
        CanvasDrawingSession ds,
        IList<RenderLyricsLine>? lines,
        int mouseHoverLineIndex,
        bool isMousePressing,
        int startVisibleIndex,
        int endVisibleIndex,
        double lyricsX,
        double lyricsY,
        double lyricsWidth,
        double lyricsHeight,
        double userScrollOffset,
        double playingLineTopOffsetFactor,
        LyricsEffectSettings effectSettings,
        LyricsStyleSettings styleSettings,
        double currentProgressMs)
    {
        if (lines == null) return;

        var isBreathingEnabled = effectSettings.IsLyricsBrethingEffectEnabled;
        var rotationX = effectSettings.FanLyricsAngle < 0 ? lyricsWidth : 0;
        rotationX += lyricsWidth / 2 * (effectSettings.FanLyricsAngle < 0 ? 1 : -1);

        var yOffsetBase = userScrollOffset + lyricsY + lyricsHeight * playingLineTopOffsetFactor;

        for (int i = startVisibleIndex; i <= endVisibleIndex; i++)
        {
            if (i < 0 || i >= lines.Count) continue;
            var line = lines[i];

            if (line?.PrimaryTextLayout == null) continue;
            if (line.PrimaryTextLayout.LayoutBounds.Width <= 0) continue;

            double xOffset = lyricsX;
            double yOffset = line.YOffsetTransition.Value + yOffsetBase;

            bool isPlaying = line.GetIsPlaying(currentProgressMs);

            if (isPlaying)
                ApplyBreathingTransform(ds, line.CenterPosition, isBreathingEnabled);

            ds.Transform *= Matrix3x2.CreateScale((float)line.ScaleTransition.Value, line.CenterPosition);

            if (effectSettings.IsFanLyricsEnabled)
            {
                xOffset += Math.Abs(line.AngleTransition.Value) / (Math.PI / 2) * lyricsWidth / 2 * (effectSettings.FanLyricsAngle < 0 ? 1 : -1);
                var rotationY = line.CenterPosition.Y;
                ds.Transform *= Matrix3x2.CreateRotation((float)line.AngleTransition.Value, new Vector2((float)rotationX, rotationY));
            }

            ds.Transform *= Matrix3x2.CreateTranslation((float)xOffset, (float)yOffset);

            line.EnsureCaches(control, styleSettings.LyricsFontStrokeWidth);
            if (line.CachedStroke == null || line.CachedFill == null) continue;
            if (line.UnplayedFillTint == null || line.UnplayedStrokeTint == null || line.UnplayedComposite == null) continue;

            line.UnplayedFillTint.Color = line.UnplayedFillColorTransition.Value;
            line.UnplayedStrokeTint.Color = line.UnplayedStrokeColorTransition.Value;

            if (isPlaying)
            {
                PlayingLineRenderer.Draw(control, ds, styleSettings.LyricsFontStrokeWidth, line.CachedStroke, line.CachedFill, line.UnplayedComposite, line, currentProgressMs, effectSettings);
            }
            else
            {
                UnplayingLineRenderer.Draw(ds, line.UnplayedComposite, styleSettings.LyricsFontStrokeWidth, line);
            }

            if (i == mouseHoverLineIndex)
            {
                byte opacity = isMousePressing ? (byte)32 : (byte)16;
                double scale = isMousePressing ? 1.09 : 1.10;
                var hoverRect = new Windows.Foundation.Rect(
                    new Point(line.TopLeftPosition.X, line.TopLeftPosition.Y),
                    new Point(line.BottomRightPosition.X, line.BottomRightPosition.Y));
                ds.FillRoundedRectangle(hoverRect.Scale(scale), 8, 8, Color.FromArgb(opacity, 255, 255, 255));
            }

            ds.Transform = Matrix3x2.Identity;
        }
    }

    public void Update(float bassEnergy, int breathingIntensity)
    {
        base.UpdateBreathing(bassEnergy, breathingIntensity);
    }
}
