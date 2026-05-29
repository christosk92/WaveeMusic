using System;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.Reorder;

/// <summary>
/// react-beautiful-dnd-style edge auto-scroll. Call <see cref="Update"/> each
/// pointer move with the pointer's Y inside the viewport; it drives a per-frame
/// scroll via the supplied callbacks. Quadratic ramp: begins at 25% from an edge,
/// reaches max at 5%, capped at 28&#160;px/frame.
/// </summary>
public sealed class ReorderAutoScroller
{
    private const double StartFraction = 0.25;
    private const double MaxFraction = 0.05;
    private const double MaxPixelsPerFrame = 28.0;

    private readonly Action<double> _scrollBy;
    private readonly Action<double> _onScrolled;
    private bool _running;
    private double _velocity; // signed px/frame

    /// <param name="scrollBy">Applies a signed pixel scroll to the host.</param>
    /// <param name="onScrolled">Invoked after each applied scroll with the delta, so the
    /// controller can re-project the (stationary) pointer into the now-shifted content space.</param>
    public ReorderAutoScroller(Action<double> scrollBy, Action<double> onScrolled)
    {
        _scrollBy = scrollBy;
        _onScrolled = onScrolled;
    }

    public void Update(double pointerYInViewport, double viewportHeight)
    {
        if (viewportHeight <= 0) { Stop(); return; }

        var startBand = viewportHeight * StartFraction;
        var maxBand = viewportHeight * MaxFraction;
        var span = Math.Max(1, startBand - maxBand);
        double v = 0;

        if (pointerYInViewport < startBand)
        {
            var p = Clamp01((startBand - pointerYInViewport) / span);
            v = -p * p * MaxPixelsPerFrame;
        }
        else if (pointerYInViewport > viewportHeight - startBand)
        {
            var p = Clamp01((pointerYInViewport - (viewportHeight - startBand)) / span);
            v = p * p * MaxPixelsPerFrame;
        }

        _velocity = v;
        if (v != 0) Start(); else Stop();
    }

    public void Stop()
    {
        _velocity = 0;
        if (_running)
        {
            CompositionTarget.Rendering -= OnRendering;
            _running = false;
        }
    }

    private void Start()
    {
        if (_running) return;
        CompositionTarget.Rendering += OnRendering;
        _running = true;
    }

    private void OnRendering(object? sender, object e)
    {
        if (_velocity == 0) { Stop(); return; }
        _scrollBy(_velocity);
        _onScrolled(_velocity);
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
