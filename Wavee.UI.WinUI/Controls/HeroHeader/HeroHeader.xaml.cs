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
/// gradient mask, overlaid with a dark scrim for text readability, and scales in with
/// a smooth pop-in animation on load.
/// </summary>
public sealed partial class HeroHeader : UserControl
{
    private static readonly TimeSpan ColorTransitionDuration = TimeSpan.FromMilliseconds(420);
    private CompositionSurfaceBrush? _surfaceBrush;
    private CompositionLinearGradientBrush? _colorBlendBrush;
    private CompositionColorGradientStop? _colorBlendMidStop;
    private CompositionColorGradientStop? _colorBlendBottomStop;
    private SpriteVisual? _colorBlendVisual;
    private CompositionLinearGradientBrush? _scrimBrush;
    private CompositionColorGradientStop? _scrimMidStop;
    private CompositionColorGradientStop? _scrimBottomStop;
    private SpriteVisual? _scrimVisual;
    private ContainerVisual? _containerVisual;
    private Compositor? _compositor;
    private Microsoft.UI.Xaml.Media.LoadedImageSurface? _imageSurface;
    private bool _hasAnimated;
    private bool _animateNextImageLoad = true;
    private string? _loadedImageUrl;
    private string? _requestedImageUrl;

    // ── Dependency Properties ──

    public static readonly DependencyProperty ImageUrlProperty =
        DependencyProperty.Register(nameof(ImageUrl), typeof(string), typeof(HeroHeader),
            new PropertyMetadata(null, OnImageUrlChanged));

    public static readonly DependencyProperty ColorHexProperty =
        DependencyProperty.Register(nameof(ColorHex), typeof(string), typeof(HeroHeader),
            new PropertyMetadata(null, OnColorHexChanged));

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

    public string? ImageUrl
    {
        get => (string?)GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    public string? ColorHex
    {
        get => (string?)GetValue(ColorHexProperty);
        set => SetValue(ColorHexProperty, value);
    }

    public object? OverlayContent
    {
        get => GetValue(OverlayContentProperty);
        set => SetValue(OverlayContentProperty, value);
    }

    public double InitialScale
    {
        get => (double)GetValue(InitialScaleProperty);
        set => SetValue(InitialScaleProperty, value);
    }

    public double FinalScale
    {
        get => (double)GetValue(FinalScaleProperty);
        set => SetValue(FinalScaleProperty, value);
    }

    public TimeSpan AnimationDuration
    {
        get => (TimeSpan)GetValue(AnimationDurationProperty);
        set => SetValue(AnimationDurationProperty, value);
    }

    public double FadeStart
    {
        get => (double)GetValue(FadeStartProperty);
        set => SetValue(FadeStartProperty, value);
    }

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
        ApplyColor(ColorHex);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ImageBorder.SizeChanged -= OnImageBorderSizeChanged;

        _imageSurface?.Dispose();
        _imageSurface = null;

        if (_surfaceBrush != null)
        {
            _surfaceBrush.Surface = null;
            _surfaceBrush.Dispose();
            _surfaceBrush = null;
        }

        _colorBlendBrush = null;
        _colorBlendMidStop = null;
        _colorBlendBottomStop = null;
        _colorBlendVisual = null;
        _scrimBrush = null;
        _scrimMidStop = null;
        _scrimBottomStop = null;
        _scrimVisual = null;

        if (_containerVisual != null)
        {
            ElementCompositionPreview.SetElementChildVisual(ImageBorder, null);
            _containerVisual.Dispose();
            _containerVisual = null;
        }

        _compositor = null;
    }

    private void OnImageBorderLoaded(object sender, RoutedEventArgs e)
    {
        SetupComposition();
        LoadImage(ImageUrl);
    }

    private void SetupComposition()
    {
        var visual = ElementCompositionPreview.GetElementVisual(ImageBorder);
        _compositor = visual.Compositor;

        _containerVisual = _compositor.CreateContainerVisual();
        _containerVisual.RelativeSizeAdjustment = Vector2.One;

        // 1. Image layer with gradient fade mask (opaque top → transparent bottom = blends with page)
        var fadeMask = _compositor.CreateLinearGradientBrush();
        fadeMask.StartPoint = new Vector2(0.5f, 0f);
        fadeMask.EndPoint = new Vector2(0.5f, 1f);
        fadeMask.ColorStops.Add(_compositor.CreateColorGradientStop(0f,
            Windows.UI.Color.FromArgb(255, 255, 255, 255)));
        fadeMask.ColorStops.Add(_compositor.CreateColorGradientStop(Math.Max(0f, (float)FadeStart - 0.12f),
            Windows.UI.Color.FromArgb(255, 255, 255, 255)));
        fadeMask.ColorStops.Add(_compositor.CreateColorGradientStop((float)((FadeStart + FadeEnd) * 0.5),
            Windows.UI.Color.FromArgb(214, 255, 255, 255)));
        fadeMask.ColorStops.Add(_compositor.CreateColorGradientStop(Math.Min(1f, (float)FadeEnd - 0.08f),
            Windows.UI.Color.FromArgb(120, 255, 255, 255)));
        fadeMask.ColorStops.Add(_compositor.CreateColorGradientStop((float)FadeEnd,
            Windows.UI.Color.FromArgb(0, 255, 255, 255)));

        _surfaceBrush = _compositor.CreateSurfaceBrush();
        _surfaceBrush.Stretch = CompositionStretch.UniformToFill;
        _surfaceBrush.VerticalAlignmentRatio = 0.5f;

        var maskedBrush = _compositor.CreateMaskBrush();
        maskedBrush.Source = _surfaceBrush;
        maskedBrush.Mask = fadeMask;

        var imageVisual = _compositor.CreateSpriteVisual();
        imageVisual.Brush = maskedBrush;
        imageVisual.RelativeSizeAdjustment = Vector2.One;
        _containerVisual.Children.InsertAtBottom(imageVisual);

        // 2. Color blend that softly accumulates toward the bottom but still shares
        // the same fade mask as the image, so it disappears cleanly into the page.
        _colorBlendBrush = _compositor.CreateLinearGradientBrush();
        _colorBlendBrush.StartPoint = new Vector2(0.5f, 0f);
        _colorBlendBrush.EndPoint = new Vector2(0.5f, 1f);
        _colorBlendBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0f,
            Windows.UI.Color.FromArgb(0, 0, 0, 0)));
        _colorBlendBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.30f,
            Windows.UI.Color.FromArgb(0, 0, 0, 0)));
        _colorBlendMidStop = _compositor.CreateColorGradientStop(0.70f,
            Windows.UI.Color.FromArgb(0, 0, 0, 0));
        _colorBlendBottomStop = _compositor.CreateColorGradientStop(1f,
            Windows.UI.Color.FromArgb(0, 0, 0, 0));
        _colorBlendBrush.ColorStops.Add(_colorBlendMidStop);
        _colorBlendBrush.ColorStops.Add(_colorBlendBottomStop);

        var colorBlendMaskBrush = _compositor.CreateMaskBrush();
        colorBlendMaskBrush.Source = _colorBlendBrush;
        colorBlendMaskBrush.Mask = fadeMask;

        _colorBlendVisual = _compositor.CreateSpriteVisual();
        _colorBlendVisual.Brush = colorBlendMaskBrush;
        _colorBlendVisual.RelativeSizeAdjustment = Vector2.One;
        _colorBlendVisual.Opacity = 0f;
        _containerVisual.Children.InsertAtTop(_colorBlendVisual);

        // 3. Soft top highlight so bright images keep a little shape.
        var highlightGradient = _compositor.CreateLinearGradientBrush();
        highlightGradient.StartPoint = new Vector2(0.5f, 0f);
        highlightGradient.EndPoint = new Vector2(0.5f, 1f);
        highlightGradient.ColorStops.Add(_compositor.CreateColorGradientStop(0f,
            Windows.UI.Color.FromArgb(84, 255, 255, 255)));
        highlightGradient.ColorStops.Add(_compositor.CreateColorGradientStop(0.18f,
            Windows.UI.Color.FromArgb(16, 255, 255, 255)));
        highlightGradient.ColorStops.Add(_compositor.CreateColorGradientStop(0.46f,
            Windows.UI.Color.FromArgb(0, 255, 255, 255)));

        var highlightVisual = _compositor.CreateSpriteVisual();
        highlightVisual.Brush = highlightGradient;
        highlightVisual.RelativeSizeAdjustment = Vector2.One;
        _containerVisual.Children.InsertAtTop(highlightVisual);

        // 4. Dark readability scrim. This now shares the image fade mask too,
        // so the entire hero stack eases out together instead of forming a band.
        _scrimBrush = _compositor.CreateLinearGradientBrush();
        _scrimBrush.StartPoint = new Vector2(0.5f, 0f);
        _scrimBrush.EndPoint = new Vector2(0.5f, 1f);
        _scrimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0f,
            Windows.UI.Color.FromArgb(8, 0, 0, 0)));
        _scrimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.32f,
            Windows.UI.Color.FromArgb(10, 0, 0, 0)));
        _scrimMidStop = _compositor.CreateColorGradientStop(0.68f,
            Windows.UI.Color.FromArgb(44, 0, 0, 0));
        _scrimBottomStop = _compositor.CreateColorGradientStop(1f,
            Windows.UI.Color.FromArgb(110, 0, 0, 0));
        _scrimBrush.ColorStops.Add(_scrimMidStop);
        _scrimBrush.ColorStops.Add(_scrimBottomStop);

        var scrimMaskBrush = _compositor.CreateMaskBrush();
        scrimMaskBrush.Source = _scrimBrush;
        scrimMaskBrush.Mask = fadeMask;

        _scrimVisual = _compositor.CreateSpriteVisual();
        _scrimVisual.Brush = scrimMaskBrush;
        _scrimVisual.RelativeSizeAdjustment = Vector2.One;
        _containerVisual.Children.InsertAtTop(_scrimVisual);

        // On first load: start hidden for pop-in. On re-attach: show immediately.
        if (_hasAnimated)
        {
            _containerVisual.Scale = new Vector3((float)FinalScale);
            _containerVisual.Opacity = 1f;
        }
        else
        {
            _containerVisual.Scale = new Vector3((float)InitialScale);
            _containerVisual.Opacity = 0f;
        }

        _containerVisual.CenterPoint = new Vector3(
            (float)(ImageBorder.ActualWidth / 2),
            (float)(ImageBorder.ActualHeight / 2), 0);

        ElementCompositionPreview.SetElementChildVisual(ImageBorder, _containerVisual);
        ApplyColor(ColorHex, animate: false);

        ImageBorder.SizeChanged -= OnImageBorderSizeChanged;
        ImageBorder.SizeChanged += OnImageBorderSizeChanged;
    }

    private void LoadImage(string? url)
    {
        if (_surfaceBrush == null || _compositor == null) return;

        var normalizedUrl = NormalizeImageUrl(url);
        _requestedImageUrl = normalizedUrl;
        _animateNextImageLoad = !string.Equals(_loadedImageUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(normalizedUrl))
        {
            _loadedImageUrl = null;
            _hasAnimated = false;
            _surfaceBrush.Surface = null;
            if (_containerVisual != null)
            {
                _containerVisual.StopAnimation("Scale");
                _containerVisual.StopAnimation("Opacity");
                _containerVisual.Scale = new Vector3((float)InitialScale);
                _containerVisual.Opacity = 0f;
            }
            return;
        }

        var shouldAnimate = _animateNextImageLoad;
        PrepareContainerForImageLoad(shouldAnimate);

        _imageSurface?.Dispose();
        var desiredSize = new Windows.Foundation.Size(
            Math.Max(1, ImageBorder.ActualWidth > 0 ? ImageBorder.ActualWidth : 1200),
            Math.Max(1, ImageBorder.ActualHeight > 0 ? ImageBorder.ActualHeight : 420));
        _imageSurface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(new Uri(normalizedUrl), desiredSize);
        _surfaceBrush.Surface = _imageSurface;
        _loadedImageUrl = normalizedUrl;

        var surface = _imageSurface;
        surface.LoadCompleted += (_, args) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(surface, _imageSurface)
                    || !string.Equals(normalizedUrl, _requestedImageUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (args.Status == LoadedImageSourceLoadStatus.Success)
                {
                    PlayPopInAnimation();
                }
                else if (_containerVisual != null)
                {
                    _containerVisual.Opacity = 1f;
                    _containerVisual.Scale = new Vector3((float)FinalScale);
                }
            });
        };
    }

    private void PrepareContainerForImageLoad(bool shouldAnimate)
    {
        if (_containerVisual == null)
            return;

        _containerVisual.StopAnimation("Scale");
        _containerVisual.StopAnimation("Opacity");

        if (shouldAnimate)
        {
            _hasAnimated = false;
            _containerVisual.Scale = new Vector3((float)InitialScale);
            _containerVisual.Opacity = 0f;
        }
        else
        {
            _hasAnimated = true;
            _containerVisual.Scale = new Vector3((float)FinalScale);
            _containerVisual.Opacity = 1f;
        }
    }

    private void ApplyColor(string? hex, bool animate = true)
    {
        if (_colorBlendBrush == null
            || _colorBlendMidStop == null
            || _colorBlendBottomStop == null)
        {
            return;
        }

        Windows.UI.Color midColor;
        Windows.UI.Color bottomColor;
        float targetOpacity;

        if (string.IsNullOrWhiteSpace(hex))
        {
            midColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            bottomColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            targetOpacity = 0f;
        }
        else if (TintColorHelper.TryParseHex(hex, out var parsedColor))
        {
            var color = TintColorHelper.BrightenForTint(parsedColor);
            midColor = Windows.UI.Color.FromArgb(60, color.R, color.G, color.B);
            bottomColor = Windows.UI.Color.FromArgb(210, color.R, color.G, color.B);
            targetOpacity = 1f;
        }
        else
        {
            midColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            bottomColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            targetOpacity = 0f;
        }

        if (!animate || _compositor == null)
        {
            _colorBlendMidStop.Color = midColor;
            _colorBlendBottomStop.Color = bottomColor;
            if (_colorBlendVisual != null)
                _colorBlendVisual.Opacity = targetOpacity;
            return;
        }

        AnimateColorStop(_colorBlendMidStop, midColor);
        AnimateColorStop(_colorBlendBottomStop, bottomColor);

        if (_colorBlendVisual != null)
            AnimateScalar(_colorBlendVisual, "Opacity", targetOpacity);
    }

    private void OnImageBorderSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_containerVisual != null)
            _containerVisual.CenterPoint = new Vector3(
                (float)(args.NewSize.Width / 2),
                (float)(args.NewSize.Height / 2), 0);
    }

    private void PlayPopInAnimation()
    {
        if (_containerVisual == null || _compositor == null) return;

        // Skip animation on re-attach — image already visible
        if (_hasAnimated)
        {
            _containerVisual.Opacity = 1f;
            _containerVisual.Scale = new Vector3((float)FinalScale);
            return;
        }
        _hasAnimated = true;

        var duration = AnimationDuration;

        var scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.InsertKeyFrame(0f, new Vector3((float)InitialScale));
        scaleAnim.InsertKeyFrame(1f, new Vector3((float)FinalScale),
            _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        scaleAnim.Duration = duration;

        var opacityAnim = _compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(0f, 0f);
        opacityAnim.InsertKeyFrame(0.4f, 1f,
            _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        opacityAnim.Duration = duration;

        _containerVisual.StartAnimation("Scale", scaleAnim);
        _containerVisual.StartAnimation("Opacity", opacityAnim);
    }

    private void AnimateColorStop(CompositionColorGradientStop stop, Windows.UI.Color target)
    {
        if (_compositor == null)
        {
            stop.Color = target;
            return;
        }

        var animation = _compositor.CreateColorKeyFrameAnimation();
        animation.InsertKeyFrame(1f, target,
            _compositor.CreateCubicBezierEasingFunction(new Vector2(0.18f, 0.84f), new Vector2(0.24f, 1f)));
        animation.Duration = ColorTransitionDuration;
        stop.StartAnimation(nameof(CompositionColorGradientStop.Color), animation);
    }

    private void AnimateScalar(CompositionObject target, string propertyName, float to)
    {
        if (_compositor == null)
        {
            if (target is Visual visual && propertyName == nameof(Visual.Opacity))
                visual.Opacity = to;
            return;
        }

        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1f, to,
            _compositor.CreateCubicBezierEasingFunction(new Vector2(0.18f, 0.84f), new Vector2(0.24f, 1f)));
        animation.Duration = ColorTransitionDuration;
        target.StartAnimation(propertyName, animation);
    }

    private static string? NormalizeImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        return SpotifyImageHelper.ToHttpsUrl(url);
    }

    private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HeroHeader header)
            header.LoadImage(e.NewValue as string);
    }

    private static void OnColorHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HeroHeader header)
            header.ApplyColor(e.NewValue as string);
    }

}
