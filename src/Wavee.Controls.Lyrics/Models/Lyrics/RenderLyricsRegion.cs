using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using System;

namespace Wavee.Controls.Lyrics.Models.Lyrics
{
    public partial class RenderLyricsRegion : IDisposable
    {
        public CanvasGradientStop[] FillStops { get; } = new CanvasGradientStop[4];
        public CanvasGradientStop[] StrokeStops { get; } = new CanvasGradientStop[4];

        public AlphaMaskEffect FinalFillEffect { get; }
        public AlphaMaskEffect? FinalStrokeEffect { get; }
        public CompositeEffect? CombinedEffect { get; }

        private CanvasCommandList? _fillGradientLayer;
        private CanvasCommandList? _strokeGradientLayer;

        public RenderLyricsRegion(ICanvasImage cachedFill, ICanvasImage? cachedStroke)
        {
            FinalFillEffect = new AlphaMaskEffect { AlphaMask = cachedFill };

            if (cachedStroke != null)
            {
                FinalStrokeEffect = new AlphaMaskEffect { AlphaMask = cachedStroke };
                CombinedEffect = new CompositeEffect
                {
                    Sources = { FinalStrokeEffect, FinalFillEffect },
                    Mode = CanvasComposite.SourceOver
                };
            }
        }

        public CanvasCommandList GetFillGradientLayer(ICanvasResourceCreator creator)
        {
            // CanvasCommandList cannot be reused after being consumed as an image source,
            // so we must create a fresh one each frame.
            _fillGradientLayer?.Dispose();
            _fillGradientLayer = new CanvasCommandList(creator);
            return _fillGradientLayer;
        }

        public CanvasCommandList? GetStrokeGradientLayer(ICanvasResourceCreator creator)
        {
            if (FinalStrokeEffect == null) return null;
            _strokeGradientLayer?.Dispose();
            _strokeGradientLayer = new CanvasCommandList(creator);
            return _strokeGradientLayer;
        }

        public void Dispose()
        {
            _fillGradientLayer?.Dispose();
            _strokeGradientLayer?.Dispose();
            _fillGradientLayer = null;
            _strokeGradientLayer = null;

            FinalFillEffect?.Dispose();
            FinalStrokeEffect?.Dispose();
            CombinedEffect?.Dispose();
        }
    }
}
