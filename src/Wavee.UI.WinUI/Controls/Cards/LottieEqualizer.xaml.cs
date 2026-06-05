using System;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// "Now playing" equalizer indicator: a few rounded bars whose heights are
/// animated by looping <see cref="Compositor"/> <c>Scale.Y</c> animations that
/// run entirely on the compositor thread.
///
/// <para>
/// This replaces an earlier codegen Lottie source whose multi-layer vector graph
/// dominated idle CPU (rasterising at the display rate) even when viewport-gated.
/// A handful of scalar lerps over solid rectangles is effectively free by
/// comparison. The public API (<see cref="IsActive"/>, <see cref="IconSize"/>,
/// <see cref="EqualizerColor"/>) is unchanged, so consumers are untouched.
/// </para>
///
/// <para>
/// The animation runs only when the control is BOTH active and inside the
/// effective viewport; otherwise the bars hold a short static rest frame with no
/// per-frame cost. As the user scrolls a list, bars that leave the viewport stop
/// animating even though their track is still "playing".
/// </para>
/// </summary>
public sealed partial class LottieEqualizer : UserControl
{
    // Resting (and paused / inactive) bar height as a fraction of full height.
    private const float RestScaleY = 0.40f;
    private const float MinScaleY = 0.22f;

    // Per-bar loop timings — staggered duration / start-delay and a distinct peak
    // height so the three bars read as an equalizer instead of moving in lockstep.
    private static readonly (int DurationMs, int DelayMs, float MaxScaleY)[] BarTimings =
    {
        (300, 0, 1.00f),
        (440, 70, 0.78f),
        (260, 40, 0.92f),
    };

    private readonly SolidColorBrush _barBrush = new(Colors.Transparent);
    private Rectangle[] _bars = Array.Empty<Rectangle>();

    private bool _isLoaded;
    private bool _isInViewport;
    private bool _animating;

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(LottieEqualizer),
            new PropertyMetadata(false, OnIsActiveChanged));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(LottieEqualizer),
            new PropertyMetadata(18.0, OnIconSizeChanged));

    public static readonly DependencyProperty EqualizerColorProperty =
        DependencyProperty.Register(nameof(EqualizerColor), typeof(Color), typeof(LottieEqualizer),
            new PropertyMetadata(Colors.Transparent, OnEqualizerColorChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public Color EqualizerColor
    {
        get => (Color)GetValue(EqualizerColorProperty);
        set => SetValue(EqualizerColorProperty, value);
    }

    public LottieEqualizer()
    {
        InitializeComponent();
        _bars = new[] { Bar0, Bar1, Bar2 };
        foreach (var bar in _bars)
            bar.Fill = _barBrush;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
        ApplyIconSize();
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LottieEqualizer)d).UpdatePlayback();

    private static void OnIconSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LottieEqualizer)d).ApplyIconSize();

    private static void OnEqualizerColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LottieEqualizer)d).ApplyColor();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        // Default _isInViewport to true on load; the first
        // EffectiveViewportChanged fires shortly after with the real state.
        _isInViewport = true;
        PrimeBarsRestState();
        // EffectiveViewportChanged accounts for scroll-viewer clipping. Subscribed
        // on attach (and removed on detach) so the handler doesn't accumulate in
        // the WinRT EventSource table across navigation-cached realizations. Used
        // to stop the bar animations while scrolled off-screen even if IsActive.
        EffectiveViewportChanged += OnEffectiveViewportChanged;
        ApplyColor();
        UpdatePlayback();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        _isLoaded = false;
        StopBars();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
        => ApplyColor();

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        // BringIntoViewDistanceX/Y are 0 when the element is fully inside the
        // viewport, positive when it's offscreen by that many DIPs. Use a small
        // threshold so a 1-pixel scroll glitch doesn't toggle the animation.
        const double OffscreenThresholdPx = 4.0;

        var offscreen = args.BringIntoViewDistanceX > OffscreenThresholdPx
                        || args.BringIntoViewDistanceY > OffscreenThresholdPx;
        var inViewport = !offscreen;

        if (inViewport == _isInViewport) return;
        _isInViewport = inViewport;
        UpdatePlayback();
    }

    private void ApplyIconSize()
    {
        if (BarsViewbox == null) return;

        var size = Math.Max(1, IconSize);
        BarsViewbox.Width = size;
        BarsViewbox.Height = size;
    }

    private void ApplyColor()
    {
        _barBrush.Color = EqualizerColor.A == 0
            ? ResolveThemeColor()
            : EqualizerColor;
    }

    private void UpdatePlayback()
    {
        if (!_isLoaded)
            return;

        // Animate only when active AND visible; otherwise hold the static rest
        // frame (no per-frame composition cost).
        if (IsActive && _isInViewport)
            StartBars();
        else
            StopBars();
    }

    /// <summary>Anchor each bar's scale to its bottom edge and seat it at the rest height.</summary>
    private void PrimeBarsRestState()
    {
        foreach (var bar in _bars)
        {
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            visual.CenterPoint = new Vector3((float)(bar.Width / 2), (float)bar.Height, 0);
            visual.Scale = new Vector3(1f, RestScaleY, 1f);
        }
    }

    private void StartBars()
    {
        if (_animating)
            return;
        _animating = true;

        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        // Smooth ease-in-out so the bounce reads as organic rather than a triangle wave.
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f));

        for (int i = 0; i < _bars.Length; i++)
        {
            var bar = _bars[i];
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            visual.CenterPoint = new Vector3((float)(bar.Width / 2), (float)bar.Height, 0);

            var (durationMs, delayMs, maxScaleY) = BarTimings[i % BarTimings.Length];

            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(0f, MinScaleY, easing);
            anim.InsertKeyFrame(1f, maxScaleY, easing);
            anim.Duration = TimeSpan.FromMilliseconds(durationMs);
            anim.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            anim.IterationBehavior = AnimationIterationBehavior.Forever;
            anim.Direction = AnimationDirection.Alternate;

            visual.StartAnimation("Scale.Y", anim);
        }
    }

    private void StopBars()
    {
        _animating = false;
        foreach (var bar in _bars)
        {
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            visual.StopAnimation("Scale.Y");
            visual.CenterPoint = new Vector3((float)(bar.Width / 2), (float)bar.Height, 0);
            visual.Scale = new Vector3(1f, RestScaleY, 1f);
        }
    }

    private static Color ResolveThemeColor()
    {
        if (TryGetBrushColor("AccentFillColorDefaultBrush", out var color))
            return color;

        if (TryGetBrushColor("AccentTextFillColorPrimaryBrush", out color))
            return color;

        if (TryGetBrushColor("TextFillColorPrimaryBrush", out color))
            return color;

        return Colors.White;
    }

    private static bool TryGetBrushColor(string resourceKey, out Color color)
    {
        color = Colors.White;
        if (Application.Current.Resources.TryGetValue(resourceKey, out var resource) &&
            resource is SolidColorBrush brush)
        {
            color = brush.Color;
            return true;
        }

        return false;
    }
}
