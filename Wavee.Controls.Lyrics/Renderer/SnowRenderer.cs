using Wavee.Controls.Lyrics.Shaders;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Numerics;

namespace Wavee.Controls.Lyrics.Renderer
{
    public partial class SnowRenderer : BreathingRendererBase, IBackgroundRenderer
    {
        private PixelShaderEffect<SnowEffect>? _snowEffect;
        private float _timeAccumulator = 0f;

        public bool IsEnabled { get; set; } = false;
        public float Amount { get; set; } = 0.5f;
        public float Speed { get; set; } = 1.0f;

        public void LoadResources()
        {
            Dispose();
            _snowEffect = new PixelShaderEffect<SnowEffect>();
        }

        public void Update(double deltaTime, float bassEnergy, int breathingIntensity)
        {
            if (_snowEffect == null || !IsEnabled) return;
            base.UpdateBreathing(bassEnergy, breathingIntensity);
            _timeAccumulator += (float)deltaTime;
        }

        public void Draw(ICanvasAnimatedControl control, CanvasDrawingSession ds, bool isBreathingEffectEnabled)
        {
            if (_snowEffect == null || !IsEnabled) return;

            float width = control.ConvertDipsToPixels((float)control.Size.Width, CanvasDpiRounding.Round);
            float height = control.ConvertDipsToPixels((float)control.Size.Height, CanvasDpiRounding.Round);

            var center = new Vector2((float)control.Size.Width / 2, (float)control.Size.Height / 2);

            _snowEffect.ConstantBuffer = new SnowEffect(
                _timeAccumulator,
                new float2(width, height),
                Amount, // 0.0 ~ 1.0
                Speed
            );

            ApplyBreathingTransform(ds, center, isBreathingEffectEnabled);
            ds.DrawImage(_snowEffect);
            ResetTransform(ds, isBreathingEffectEnabled);
        }

        // IBackgroundRenderer
        void IBackgroundRenderer.LoadResources(ICanvasResourceCreator creator) => LoadResources();

        void IBackgroundRenderer.Update(in RenderContext ctx)
        {
            var bg = ctx.Settings.LyricsBackgroundSettings;
            IsEnabled = bg.IsSnowFlakeOverlayEnabled;
            Amount = bg.SnowFlakeOverlayAmount / 100f;
            Speed = bg.SnowFlakeOverlaySpeed;
            Update(ctx.Elapsed.TotalSeconds, ctx.BassEnergy, bg.SnowFlakeOverlayBreathingIntensity);
        }

        void IBackgroundRenderer.Draw(CanvasDrawingSession ds, in RenderContext ctx)
        {
            Draw(ctx.Control, ds, ctx.Settings.LyricsBackgroundSettings.IsSnowFlakeOverlayBrethingEffectEnabled);
        }

        public void Dispose()
        {
            _snowEffect?.Dispose();
            _snowEffect = null;
        }
    }
}