using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace Wavee.UI.WinUI.Helpers.Composition;

/// <summary>
/// Attached behaviour that replaces a hardcoded gradient overlay on a fade
/// `Border` with a composition-driven backdrop sample. The Border becomes a
/// rectangle that displays a real-time sample of whatever's directly behind
/// it (cards on the left side, page palette wash where there are no cards
/// on the right side), masked by a horizontal alpha gradient so the left
/// edge is transparent and the right edge fades up to a subtle visibility.
///
/// <para>
/// The previous static <c>ShelfFadeRightBrush</c> approach painted an opaque
/// `#FF202020` slab on the right edge, which clashed with the album / artist
/// palette-tinted page backdrop. This composition approach inherently matches
/// whatever's drawn underneath because the source IS the backdrop — no
/// hardcoded colour, no page-specific brush plumbing.
/// </para>
///
/// <para>
/// Usage:
/// <code>
///   xmlns:cfx="using:Wavee.UI.WinUI.Helpers.Composition"
///   &lt;Border cfx:ShelfFadeMask.FadeRight="True" .../&gt;
/// </code>
/// The Border should remain hit-test invisible (it's a visual cue, not an
/// interactive surface) and its <c>Width</c> stays whatever the consuming
/// page sets — the mask scales to the Border's measured size.
/// </para>
/// </summary>
public static class ShelfFadeMask
{
    public static readonly DependencyProperty FadeRightProperty =
        DependencyProperty.RegisterAttached(
            "FadeRight",
            typeof(bool),
            typeof(ShelfFadeMask),
            new PropertyMetadata(false, OnFadeRightChanged));

    public static bool GetFadeRight(DependencyObject d) => (bool)d.GetValue(FadeRightProperty);
    public static void SetFadeRight(DependencyObject d, bool value) => d.SetValue(FadeRightProperty, value);

    // Per-element state. Keyed off the element via an attached property so
    // multiple consumers don't share a static dictionary (each Border gets
    // its own SpriteVisual / Compositor reference).
    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "_FadeRightState",
            typeof(FadeRightState),
            typeof(ShelfFadeMask),
            new PropertyMetadata(null));

    private static void OnFadeRightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;

        var existing = (FadeRightState?)border.GetValue(StateProperty);
        if (e.NewValue is true)
        {
            if (existing is not null) return;
            var state = new FadeRightState(border);
            border.SetValue(StateProperty, state);
            state.Attach();
        }
        else
        {
            existing?.Detach();
            border.SetValue(StateProperty, null);
        }
    }

    /// <summary>
    /// Per-Border composition state. Holds the SpriteVisual + the mask
    /// gradient brush so SizeChanged can re-size everything without
    /// rebuilding the visual tree.
    /// </summary>
    private sealed class FadeRightState
    {
        private readonly Border _border;
        private SpriteVisual? _sprite;
        private CompositionLinearGradientBrush? _maskGradient;

        public FadeRightState(Border border) => _border = border;

        public void Attach()
        {
            _border.Loaded += OnLoaded;
            _border.Unloaded += OnUnloaded;
            _border.SizeChanged += OnSizeChanged;
            if (_border.IsLoaded) BuildVisual();
        }

        public void Detach()
        {
            _border.Loaded -= OnLoaded;
            _border.Unloaded -= OnUnloaded;
            _border.SizeChanged -= OnSizeChanged;
            ElementCompositionPreview.SetElementChildVisual(_border, null);
            _sprite?.Dispose();
            _sprite = null;
            _maskGradient?.Dispose();
            _maskGradient = null;
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => BuildVisual();

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // Don't tear down — Unloaded fires on Frame navigation away but
            // the page (and this Border) often reattach when the user
            // navigates back. The Detach path runs only when FadeRight is
            // explicitly toggled off.
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_sprite is null) return;
            _sprite.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
        }

        private void BuildVisual()
        {
            if (_sprite is not null) return;
            var visual = ElementCompositionPreview.GetElementVisual(_border);
            var compositor = visual.Compositor;

            // Backdrop brush samples whatever's rendered directly behind this
            // visual — for a fade Border positioned over the right edge of a
            // shelf, that's a mix of the rightmost cards (left part of the
            // Border) and the page palette / Mica (right part, beyond the
            // cards). Painting the raw sample back onto the same pixels would
            // be a no-op (identical bytes layered on top of themselves), so we
            // run it through a Gaussian blur first via Win2D — the painted
            // result is visibly softer than the unblurred content underneath,
            // which produces the "frosted right edge" effect that picks up the
            // page colours without painting a hardcoded slab.
            var backdrop = compositor.CreateBackdropBrush();

            using var blurEffect = new GaussianBlurEffect
            {
                Name = "Blur",
                BlurAmount = 16f,
                BorderMode = EffectBorderMode.Hard,
                Source = new CompositionEffectSourceParameter("Backdrop"),
            };
            var effectFactory = compositor.CreateEffectFactory(blurEffect);
            var blurredBackdrop = effectFactory.CreateBrush();
            blurredBackdrop.SetSourceParameter("Backdrop", backdrop);

            // Linear alpha gradient — transparent on left, modest opacity on
            // right. The colour channel of the mask is ignored; only alpha
            // weights the source brush. 4-stop curve mirrors the previous
            // ShelfFadeRightBrush so the visual rhythm stays consistent with
            // any surfaces that still rely on a static gradient.
            _maskGradient = compositor.CreateLinearGradientBrush();
            _maskGradient.StartPoint = new Vector2(0f, 0.5f);
            _maskGradient.EndPoint = new Vector2(1f, 0.5f);
            _maskGradient.ColorStops.Add(compositor.CreateColorGradientStop(0f,    Color.FromArgb(0,   255, 255, 255)));
            _maskGradient.ColorStops.Add(compositor.CreateColorGradientStop(0.35f, Color.FromArgb(64,  255, 255, 255)));
            _maskGradient.ColorStops.Add(compositor.CreateColorGradientStop(0.72f, Color.FromArgb(180, 255, 255, 255)));
            _maskGradient.ColorStops.Add(compositor.CreateColorGradientStop(1f,    Color.FromArgb(230, 255, 255, 255)));

            var maskBrush = compositor.CreateMaskBrush();
            maskBrush.Source = blurredBackdrop;
            maskBrush.Mask = _maskGradient;

            _sprite = compositor.CreateSpriteVisual();
            _sprite.Brush = maskBrush;
            _sprite.Size = new Vector2((float)_border.ActualWidth, (float)_border.ActualHeight);

            ElementCompositionPreview.SetElementChildVisual(_border, _sprite);
        }
    }
}
