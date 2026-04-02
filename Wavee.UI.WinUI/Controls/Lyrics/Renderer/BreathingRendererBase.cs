// Ported from BetterLyrics by Zhe Fang

using Microsoft.Graphics.Canvas;
using System.Numerics;

namespace Wavee.UI.WinUI.Controls.Lyrics.Renderer;

public abstract class BreathingRendererBase
{
    protected float _currentScale = 1.0f;
    private float _targetScale = 1.0f;

    public virtual void UpdateBreathing(float bassEnergy, int intensity)
    {
        if (intensity <= 0)
        {
            _currentScale = 1.0f;
            return;
        }

        float maxScaleOffset = intensity / 100.0f;
        _targetScale = 1.0f + (bassEnergy * maxScaleOffset);

        if (_targetScale > _currentScale)
            _currentScale += (_targetScale - _currentScale) * 0.2f; // Attack
        else
            _currentScale += (_targetScale - _currentScale) * 0.05f; // Decay
    }

    protected void ApplyBreathingTransform(CanvasDrawingSession ds, Vector2 center, bool isEnabled)
    {
        if (isEnabled && _currentScale > 1.0f)
        {
            ds.Transform = Matrix3x2.CreateScale(_currentScale, center);
        }
    }

    protected static void ResetTransform(CanvasDrawingSession ds, bool isEnabled)
    {
        if (isEnabled)
        {
            ds.Transform = Matrix3x2.Identity;
        }
    }
}
