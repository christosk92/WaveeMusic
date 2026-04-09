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
    private CompositionSurfaceBrush? _surfaceBrush;
    private ContainerVisual? _containerVisual;
    private Compositor? _compositor;
    private Microsoft.UI.Xaml.Media.LoadedImageSurface? _imageSurface;
    private bool _hasAnimated;

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

    public string? ImageUrl
    {
        get => (string?)GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
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
        fadeMask.ColorStops.Add(_compositor.CreateColorGradientStop((float)FadeStart,
            Windows.UI.Color.FromArgb(255, 255, 255, 255)));
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

        // 2. Dark gradient scrim for text readability (transparent top → semi-opaque black → transparent bottom)
        //    The scrim must fade back to transparent at the same point the image mask does,
        //    otherwise it creates a hard edge instead of blending into the page background.
        var scrimGradient = _compositor.CreateLinearGradientBrush();
        scrimGradient.StartPoint = new Vector2(0.5f, 0f);
        scrimGradient.EndPoint = new Vector2(0.5f, 1f);
        scrimGradient.ColorStops.Add(_compositor.CreateColorGradientStop(0f,
            Windows.UI.Color.FromArgb(0, 0, 0, 0)));
        scrimGradient.ColorStops.Add(_compositor.CreateColorGradientStop(0.4f,
            Windows.UI.Color.FromArgb(0, 0, 0, 0)));
        scrimGradient.ColorStops.Add(_compositor.CreateColorGradientStop((float)FadeStart,
            Windows.UI.Color.FromArgb(180, 0, 0, 0)));
        scrimGradient.ColorStops.Add(_compositor.CreateColorGradientStop((float)FadeEnd,
            Windows.UI.Color.FromArgb(0, 0, 0, 0)));

        var scrimVisual = _compositor.CreateSpriteVisual();
        scrimVisual.Brush = scrimGradient;
        scrimVisual.RelativeSizeAdjustment = Vector2.One;
        _containerVisual.Children.InsertAtTop(scrimVisual);

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

        ImageBorder.SizeChanged -= OnImageBorderSizeChanged;
        ImageBorder.SizeChanged += OnImageBorderSizeChanged;
    }

    private void LoadImage(string? url)
    {
        if (_surfaceBrush == null || _compositor == null) return;

        if (string.IsNullOrEmpty(url))
        {
            _surfaceBrush.Surface = null;
            if (_containerVisual != null) _containerVisual.Opacity = 0f;
            return;
        }

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(url);
        if (string.IsNullOrEmpty(httpsUrl)) return;

        _imageSurface?.Dispose();
        var desiredSize = new Windows.Foundation.Size(
            Math.Max(1, ImageBorder.ActualWidth > 0 ? ImageBorder.ActualWidth : 1200),
            Math.Max(1, ImageBorder.ActualHeight > 0 ? ImageBorder.ActualHeight : 420));
        _imageSurface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromUri(new Uri(httpsUrl), desiredSize);
        _surfaceBrush.Surface = _imageSurface;

        _imageSurface.LoadCompleted += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() => PlayPopInAnimation());
        };
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

    private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HeroHeader header)
            header.LoadImage(e.NewValue as string);
    }
}
