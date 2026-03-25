using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.Controls.HeroHeader;

/// <summary>
/// A reusable hero header with a background image that fades to transparent via composition
/// gradient mask, and scales in with a smooth pop-in animation on load.
/// </summary>
public sealed partial class HeroHeader : UserControl
{
    private CompositionSurfaceBrush? _surfaceBrush;
    private SpriteVisual? _spriteVisual;
    private Compositor? _compositor;
    private Microsoft.UI.Xaml.Media.LoadedImageSurface? _imageSurface;

    // ── Dependency Properties ──

    public static readonly DependencyProperty ImageUrlProperty =
        DependencyProperty.Register(nameof(ImageUrl), typeof(string), typeof(HeroHeader),
            new PropertyMetadata(null, OnImageUrlChanged));

    public static readonly DependencyProperty OverlayContentProperty =
        DependencyProperty.Register(nameof(OverlayContent), typeof(object), typeof(HeroHeader),
            new PropertyMetadata(null));

    public static readonly DependencyProperty InitialScaleProperty =
        DependencyProperty.Register(nameof(InitialScale), typeof(double), typeof(HeroHeader),
            new PropertyMetadata(1.0));

    public static readonly DependencyProperty FinalScaleProperty =
        DependencyProperty.Register(nameof(FinalScale), typeof(double), typeof(HeroHeader),
            new PropertyMetadata(1.05));

    public static readonly DependencyProperty AnimationDurationProperty =
        DependencyProperty.Register(nameof(AnimationDuration), typeof(TimeSpan), typeof(HeroHeader),
            new PropertyMetadata(TimeSpan.FromMilliseconds(800)));

    public static readonly DependencyProperty FadeStartProperty =
        DependencyProperty.Register(nameof(FadeStart), typeof(double), typeof(HeroHeader),
            new PropertyMetadata(0.55));

    public static readonly DependencyProperty FadeEndProperty =
        DependencyProperty.Register(nameof(FadeEnd), typeof(double), typeof(HeroHeader),
            new PropertyMetadata(0.95));

    /// <summary>Image URL to display as the hero background.</summary>
    public string? ImageUrl
    {
        get => (string?)GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    /// <summary>Content overlaid on the hero (e.g. artist name, buttons).</summary>
    public object? OverlayContent
    {
        get => GetValue(OverlayContentProperty);
        set => SetValue(OverlayContentProperty, value);
    }

    /// <summary>Starting scale of the image (default 1.0).</summary>
    public double InitialScale
    {
        get => (double)GetValue(InitialScaleProperty);
        set => SetValue(InitialScaleProperty, value);
    }

    /// <summary>Final scale after the pop-in animation (default 1.05).</summary>
    public double FinalScale
    {
        get => (double)GetValue(FinalScaleProperty);
        set => SetValue(FinalScaleProperty, value);
    }

    /// <summary>Duration of the scale + opacity pop-in animation.</summary>
    public TimeSpan AnimationDuration
    {
        get => (TimeSpan)GetValue(AnimationDurationProperty);
        set => SetValue(AnimationDurationProperty, value);
    }

    /// <summary>Gradient fade start position (0-1, top to bottom). Default 0.55.</summary>
    public double FadeStart
    {
        get => (double)GetValue(FadeStartProperty);
        set => SetValue(FadeStartProperty, value);
    }

    /// <summary>Gradient fade end position (0-1, fully transparent). Default 0.95.</summary>
    public double FadeEnd
    {
        get => (double)GetValue(FadeEndProperty);
        set => SetValue(FadeEndProperty, value);
    }

    public HeroHeader()
    {
        InitializeComponent();
        ImageBorder.Loaded += OnImageBorderLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnUnloaded;

        _imageSurface?.Dispose();
        _imageSurface = null;

        if (_surfaceBrush != null)
        {
            _surfaceBrush.Surface = null;
            _surfaceBrush.Dispose();
            _surfaceBrush = null;
        }

        if (_spriteVisual != null)
        {
            ElementCompositionPreview.SetElementChildVisual(ImageBorder, null);
            _spriteVisual.Brush?.Dispose();
            _spriteVisual.Dispose();
            _spriteVisual = null;
        }

        _compositor = null;
    }

    private void OnImageBorderLoaded(object sender, RoutedEventArgs e)
    {
        ImageBorder.Loaded -= OnImageBorderLoaded;
        SetupComposition();
        LoadImage(ImageUrl);
    }

    private void SetupComposition()
    {
        var visual = ElementCompositionPreview.GetElementVisual(ImageBorder);
        _compositor = visual.Compositor;

        // Gradient mask: opaque at top → transparent at bottom
        var gradientBrush = _compositor.CreateLinearGradientBrush();
        gradientBrush.StartPoint = new Vector2(0.5f, 0f);
        gradientBrush.EndPoint = new Vector2(0.5f, 1f);
        gradientBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0f,
            Windows.UI.Color.FromArgb(255, 255, 255, 255)));
        gradientBrush.ColorStops.Add(_compositor.CreateColorGradientStop((float)FadeStart,
            Windows.UI.Color.FromArgb(255, 255, 255, 255)));
        gradientBrush.ColorStops.Add(_compositor.CreateColorGradientStop((float)FadeEnd,
            Windows.UI.Color.FromArgb(0, 255, 255, 255)));

        // Surface brush for the image
        _surfaceBrush = _compositor.CreateSurfaceBrush();
        _surfaceBrush.Stretch = CompositionStretch.UniformToFill;
        _surfaceBrush.VerticalAlignmentRatio = 0.5f;

        // Mask brush: image × gradient
        var maskBrush = _compositor.CreateMaskBrush();
        maskBrush.Source = _surfaceBrush;
        maskBrush.Mask = gradientBrush;

        // Sprite visual to render the masked image
        _spriteVisual = _compositor.CreateSpriteVisual();
        _spriteVisual.Brush = maskBrush;
        _spriteVisual.RelativeSizeAdjustment = Vector2.One;

        // Start with initial scale, centered
        _spriteVisual.Scale = new Vector3((float)InitialScale);
        _spriteVisual.CenterPoint = new Vector3(
            (float)(ImageBorder.ActualWidth / 2),
            (float)(ImageBorder.ActualHeight / 2), 0);
        _spriteVisual.Opacity = 0f;

        ElementCompositionPreview.SetElementChildVisual(ImageBorder, _spriteVisual);

        // Update center point on resize
        ImageBorder.SizeChanged += (_, args) =>
        {
            if (_spriteVisual != null)
                _spriteVisual.CenterPoint = new Vector3(
                    (float)(args.NewSize.Width / 2),
                    (float)(args.NewSize.Height / 2), 0);
        };
    }

    private void LoadImage(string? url)
    {
        if (_surfaceBrush == null || _compositor == null) return;

        if (string.IsNullOrEmpty(url))
        {
            _surfaceBrush.Surface = null;
            if (_spriteVisual != null) _spriteVisual.Opacity = 0f;
            return;
        }

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(url);
        if (string.IsNullOrEmpty(httpsUrl)) return;

        var surface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(new Uri(httpsUrl));
        _surfaceBrush.Surface = surface;

        // Animate: pop-in from center with scale + opacity
        surface.LoadCompleted += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() => PlayPopInAnimation());
        };
    }

    private void PlayPopInAnimation()
    {
        if (_spriteVisual == null || _compositor == null) return;

        var duration = AnimationDuration;

        // Scale: InitialScale → FinalScale with decelerate easing
        var scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.InsertKeyFrame(0f, new Vector3((float)InitialScale));
        scaleAnim.InsertKeyFrame(1f, new Vector3((float)FinalScale),
            _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        scaleAnim.Duration = duration;

        // Opacity: 0 → 1 (fast, front-loaded)
        var opacityAnim = _compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(0f, 0f);
        opacityAnim.InsertKeyFrame(0.4f, 1f,
            _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        opacityAnim.Duration = duration;

        _spriteVisual.StartAnimation("Scale", scaleAnim);
        _spriteVisual.StartAnimation("Opacity", opacityAnim);
    }

    private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HeroHeader header)
            header.LoadImage(e.NewValue as string);
    }
}
