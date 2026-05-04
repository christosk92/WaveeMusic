using Wavee.Controls.Lyrics.Constants;
using Wavee.Controls.Lyrics.Enums;
using Wavee.Controls.Lyrics.Helper;
using Microsoft.Graphics.Canvas.Effects;
using System;
using System.Numerics;
using Windows.Foundation;

namespace Wavee.Controls.Lyrics.Models.Lyrics
{
    public class RenderLyricsChar : BaseRenderLyrics
    {
        public Rect LayoutRect { get; private set; }

        public ValueTransition<double> ScaleTransition { get; set; }
        public ValueTransition<double> GlowTransition { get; set; }
        public ValueTransition<double> FloatTransition { get; set; }

        public CropEffect Crop { get; }
        public ColorMatrixEffect GlowWhiten { get; }
        public GaussianBlurEffect Glow { get; }

        public double ProgressPlayed { get; set; } = 0; // 0~1

        public RenderLyricsChar(BaseLyrics lyricsChars, Rect layoutRect) : base(lyricsChars)
        {
            ScaleTransition = new(
                initialValue: 1.0,
                EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine),
                defaultTotalDuration: Time.AnimationDuration.TotalSeconds
            );
            GlowTransition = new(
                initialValue: 0,
                EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine),
                defaultTotalDuration: Time.AnimationDuration.TotalSeconds
            );
            FloatTransition = new(
                initialValue: 0,
                EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine),
                defaultTotalDuration: Time.LongAnimationDuration.TotalSeconds
            );
            LayoutRect = layoutRect;
            Crop = new CropEffect { BorderMode = EffectBorderMode.Hard };
            GlowWhiten = new ColorMatrixEffect
            {
                Source = Crop,
                ColorMatrix = new Matrix5x4
                {
                    // Zero out color-to-color mapping, set RGB offset to white, keep alpha
                    M44 = 1,
                    M51 = 1, M52 = 1, M53 = 1,
                }
            };
            Glow = new GaussianBlurEffect { Source = GlowWhiten, BorderMode = EffectBorderMode.Soft };
        }

        public void Update(TimeSpan elapsedTime)
        {
            ScaleTransition.Update(elapsedTime);
            GlowTransition.Update(elapsedTime);
            FloatTransition.Update(elapsedTime);
        }

        public void DisposeEffetcts()
        {
            Crop?.Dispose();
            GlowWhiten?.Dispose();
            Glow?.Dispose();
        }

    }
}
