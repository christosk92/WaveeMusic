using System;
using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Wavee.UI.WinUI.Controls.MiniVideoPlayer;

/// <summary>
/// Three-layer video host that crossfades album art → blurred poster →
/// live video frame so the user never sees a black-frame moment during
/// audio → video transitions. Opacity transitions are composition implicit
/// animations (one-time install in OnApplyTemplate-equivalent), keyed on
/// the timing the HTML prototype settled on (180 ms art→poster,
/// 220 ms poster→video, both <c>cubic-bezier(0.16, 1, 0.3, 1)</c>).
///
/// The poster blur is a Win2D <see cref="GaussianBlurEffect"/> applied via
/// <see cref="CompositionEffectBrush"/> on a SpriteVisual whose source is a
/// <see cref="LoadedImageSurface"/>. The visual lives as a child of
/// <c>PosterBlurHost</c> through <see cref="ElementCompositionPreview"/>.
/// </summary>
public sealed partial class VideoSurfaceHost : UserControl
{
    // Crossfade durations / easing — locked to the "Soft" preset the user
    // approved in the HTML prototype.
    private static readonly TimeSpan ArtToPosterDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan PosterToVideoDuration = TimeSpan.FromMilliseconds(220);
    private static readonly Vector2 SoftEaseControl1 = new(0.16f, 1f);
    private static readonly Vector2 SoftEaseControl2 = new(0.30f, 1f);

    private const float PosterBlurAmount = 24f;
    private const float PosterScale = 1.08f;

    // Composition pieces owned for the lifetime of the control. Disposed
    // on Unloaded so we don't leak GPU-side resources when the host is
    // re-realized (e.g. user toggles between pages).
    private Compositor? _compositor;
    private SpriteVisual? _posterVisual;
    private LoadedImageSurface? _posterSurface;
    private CompositionSurfaceBrush? _posterSurfaceBrush;
    private CompositionEffectBrush? _posterEffectBrush;

    public VideoSurfaceHost()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    // ── Dependency Properties ────────────────────────────────────────────

    public static readonly DependencyProperty AlbumArtUrlProperty =
        DependencyProperty.Register(
            nameof(AlbumArtUrl), typeof(string), typeof(VideoSurfaceHost),
            new PropertyMetadata(null, OnAlbumArtUrlChanged));

    public string? AlbumArtUrl
    {
        get => (string?)GetValue(AlbumArtUrlProperty);
        set => SetValue(AlbumArtUrlProperty, value);
    }

    public static readonly DependencyProperty PosterUrlProperty =
        DependencyProperty.Register(
            nameof(PosterUrl), typeof(string), typeof(VideoSurfaceHost),
            new PropertyMetadata(null, OnPosterUrlChanged));

    public string? PosterUrl
    {
        get => (string?)GetValue(PosterUrlProperty);
        set => SetValue(PosterUrlProperty, value);
    }

    public static readonly DependencyProperty IsFirstFrameReadyProperty =
        DependencyProperty.Register(
            nameof(IsFirstFrameReady), typeof(bool), typeof(VideoSurfaceHost),
            new PropertyMetadata(false, OnIsFirstFrameReadyChanged));

    public bool IsFirstFrameReady
    {
        get => (bool)GetValue(IsFirstFrameReadyProperty);
        set => SetValue(IsFirstFrameReadyProperty, value);
    }

    public static readonly DependencyProperty HostCornerRadiusProperty =
        DependencyProperty.Register(
            nameof(HostCornerRadius), typeof(CornerRadius), typeof(VideoSurfaceHost),
            new PropertyMetadata(new CornerRadius(0), OnHostCornerRadiusChanged));

    public CornerRadius HostCornerRadius
    {
        get => (CornerRadius)GetValue(HostCornerRadiusProperty);
        set => SetValue(HostCornerRadiusProperty, value);
    }

    /// <summary>The Grid the video element (MediaPlayerElement / WebView2)
    /// should be mounted into by the surface arbiter.</summary>
    public Panel VideoMountPoint => VideoSlot;

    // ── Public API: video element mounting ───────────────────────────────

    /// <summary>
    /// Insert the live video element into <see cref="VideoSlot"/>. Caller
    /// owns the element's lifetime — the host only adopts/discards.
    /// </summary>
    public void MountVideoElement(FrameworkElement element)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        if (element.Parent is Panel p && !ReferenceEquals(p, VideoSlot))
        {
            // Reparent: remove from previous parent first.
            p.Children.Remove(element);
        }
        if (!VideoSlot.Children.Contains(element))
        {
            element.HorizontalAlignment = HorizontalAlignment.Stretch;
            element.VerticalAlignment = VerticalAlignment.Stretch;
            element.IsHitTestVisible = false;
            VideoSlot.Children.Insert(0, element);
        }
        UpdateLayout();
    }

    public void UnmountVideoElement(FrameworkElement element)
    {
        if (element is null) return;
        if (VideoSlot.Children.Contains(element))
            VideoSlot.Children.Remove(element);
    }

    // ── Lifecycle / composition setup ────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureComposition();
        ApplyAlbumArt(AlbumArtUrl);
        ApplyPosterUrl(PosterUrl);
        ApplyFirstFrameState(IsFirstFrameReady, animate: false);
        ApplyHostCornerRadius();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DisposePosterResources();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_posterVisual is null) return;
        _posterVisual.Size = new Vector2((float)Math.Max(0, ActualWidth), (float)Math.Max(0, ActualHeight));
        // Re-center the scale so the slight zoom doesn't drift away from the
        // visual centre on resize.
        _posterVisual.CenterPoint = new Vector3(_posterVisual.Size / 2f, 0);
    }

    private void EnsureComposition()
    {
        if (_compositor is not null) return;

        _compositor = ElementCompositionPreview.GetElementVisual(PosterBlurHost).Compositor;

        // SpriteVisual hosts the blurred poster brush. Initial Size matches
        // the host's current size; later updated on SizeChanged.
        _posterVisual = _compositor.CreateSpriteVisual();
        _posterVisual.Size = new Vector2((float)Math.Max(0, ActualWidth), (float)Math.Max(0, ActualHeight));
        _posterVisual.CenterPoint = new Vector3(_posterVisual.Size / 2f, 0);
        _posterVisual.Scale = new Vector3(PosterScale, PosterScale, 1f);

        // Implicit Opacity animations for art / poster / video layers.
        // Composition handles the curve; XAML opacity flips simply trigger.
        InstallOpacityImplicits(AlbumArtLayer, ArtToPosterDuration);
        InstallOpacityImplicits(PosterBlurHost, ArtToPosterDuration);
        InstallOpacityImplicits(VideoSlot, PosterToVideoDuration);

        ElementCompositionPreview.SetElementChildVisual(PosterBlurHost, _posterVisual);
    }

    private void InstallOpacityImplicits(UIElement element, TimeSpan duration)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertExpressionKeyFrame(
            1f,
            "this.FinalValue",
            compositor.CreateCubicBezierEasingFunction(SoftEaseControl1, SoftEaseControl2));
        opacityAnim.Duration = duration;
        opacityAnim.Target = "Opacity";

        var collection = compositor.CreateImplicitAnimationCollection();
        collection["Opacity"] = opacityAnim;
        visual.ImplicitAnimations = collection;
    }

    // ── Poster URL → CompositionEffectBrush ──────────────────────────────

    private static void OnAlbumArtUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((VideoSurfaceHost)d).ApplyAlbumArt(e.NewValue as string);

    private static void OnPosterUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((VideoSurfaceHost)d).ApplyPosterUrl(e.NewValue as string);

    private static void OnIsFirstFrameReadyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((VideoSurfaceHost)d).ApplyFirstFrameState((bool)e.NewValue, animate: true);

    private static void OnHostCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((VideoSurfaceHost)d).ApplyHostCornerRadius();

    private void ApplyHostCornerRadius()
    {
        OuterFrame.CornerRadius = HostCornerRadius;
    }

    private void ApplyAlbumArt(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            AlbumArtLayer.Source = null;
            return;
        }
        try
        {
            AlbumArtLayer.Source = new BitmapImage(new Uri(url));
        }
        catch
        {
            AlbumArtLayer.Source = null;
        }
    }

    private void ApplyPosterUrl(string? url)
    {
        if (_compositor is null || _posterVisual is null)
        {
            // Composition not ready yet — OnLoaded will re-call us.
            return;
        }

        DisposePosterResources();

        if (string.IsNullOrEmpty(url))
        {
            _posterVisual.Brush = null;
            return;
        }

        try
        {
            _posterSurface = LoadedImageSurface.StartLoadFromUri(new Uri(url));
        }
        catch
        {
            _posterSurface = null;
        }

        if (_posterSurface is null)
        {
            _posterVisual.Brush = null;
            return;
        }

        _posterSurfaceBrush = _compositor.CreateSurfaceBrush(_posterSurface);
        _posterSurfaceBrush.Stretch = CompositionStretch.UniformToFill;

        var blurEffect = new GaussianBlurEffect
        {
            Name = "Blur",
            BlurAmount = PosterBlurAmount,
            BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("Source"),
        };
        var factory = _compositor.CreateEffectFactory(blurEffect);
        _posterEffectBrush = factory.CreateBrush();
        _posterEffectBrush.SetSourceParameter("Source", _posterSurfaceBrush);
        _posterVisual.Brush = _posterEffectBrush;
    }

    private void DisposePosterResources()
    {
        _posterEffectBrush?.Dispose();
        _posterEffectBrush = null;
        _posterSurfaceBrush?.Dispose();
        _posterSurfaceBrush = null;
        _posterSurface?.Dispose();
        _posterSurface = null;
    }

    // ── State driver ─────────────────────────────────────────────────────

    private void ApplyFirstFrameState(bool firstFrameReady, bool animate)
    {
        // Choose layer opacities by state. Composition implicit animations
        // make the actual transitions smooth — we just set the target value.
        //
        // Without composition (first paint / OnLoaded), bypass animation so
        // the initial appearance is correct.
        if (!animate)
        {
            var ready = firstFrameReady;
            AlbumArtLayer.Opacity = ready ? 0 : 1;
            PosterBlurHost.Opacity = ready ? 0 : (PosterUrl is null ? 0 : 1);
            VideoSlot.Opacity = ready ? 1 : 0;
            return;
        }

        if (firstFrameReady)
        {
            VideoSlot.Opacity = 1;
            AlbumArtLayer.Opacity = 0;
            PosterBlurHost.Opacity = 0;
        }
        else
        {
            VideoSlot.Opacity = 0;
            PosterBlurHost.Opacity = PosterUrl is null ? 0 : 1;
            AlbumArtLayer.Opacity = PosterUrl is null ? 1 : 0;
        }
    }
}
