using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Wavee.Core.Http;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Imaging;

/// <summary>
/// Reusable cross-fading image control with a stable layout footprint.
///
/// <para>
/// Two stacked <see cref="CompositionImage"/> layers live inside a fixed-size
/// host Grid. When <see cref="Source"/> changes the new URL is assigned to
/// the idle layer; once it reports <see cref="CompositionImage.ImageOpened"/>
/// a Composition opacity + scale animation fades the layers in parallel —
/// the old image stays fully painted until the new one is ready. No flicker,
/// no 0×0 reflow.
/// </para>
///
/// <para>
/// Cache and pin/unpin are handled by the inner <see cref="CompositionImage"/>
/// controls; this control just sets <c>ImageUrl</c> and animates opacity.
/// </para>
///
/// <para>
/// Palette-aware placeholder: while the surface loads, the placeholder layer
/// behind both image layers is tinted with the image's extracted dominant
/// color (theme-aware, via <see cref="IColorService"/>). Falls back to
/// <see cref="PlaceholderBrush"/> when extraction fails or the service
/// isn't wired.
/// </para>
/// </summary>
public sealed partial class CrossFadeImage : UserControl
{
    // ── Dependency properties ──

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(string), typeof(CrossFadeImage),
            new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty FallbackSourceProperty =
        DependencyProperty.Register(nameof(FallbackSource), typeof(string), typeof(CrossFadeImage),
            new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty DecodePixelWidthProperty =
        DependencyProperty.Register(nameof(DecodePixelWidth), typeof(int), typeof(CrossFadeImage),
            new PropertyMetadata(256));

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(nameof(Stretch), typeof(Stretch), typeof(CrossFadeImage),
            new PropertyMetadata(Microsoft.UI.Xaml.Media.Stretch.UniformToFill));

    public static readonly DependencyProperty PlaceholderBrushProperty =
        DependencyProperty.Register(nameof(PlaceholderBrush), typeof(Brush), typeof(CrossFadeImage),
            new PropertyMetadata(null));

    public static readonly DependencyProperty FadeDurationMsProperty =
        DependencyProperty.Register(nameof(FadeDurationMs), typeof(int), typeof(CrossFadeImage),
            new PropertyMetadata(240));

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string? FallbackSource
    {
        get => (string?)GetValue(FallbackSourceProperty);
        set => SetValue(FallbackSourceProperty, value);
    }

    public int DecodePixelWidth
    {
        get => (int)GetValue(DecodePixelWidthProperty);
        set => SetValue(DecodePixelWidthProperty, value);
    }

    public new Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public Brush? PlaceholderBrush
    {
        get => (Brush?)GetValue(PlaceholderBrushProperty);
        set => SetValue(PlaceholderBrushProperty, value);
    }

    public int FadeDurationMs
    {
        get => (int)GetValue(FadeDurationMsProperty);
        set => SetValue(FadeDurationMsProperty, value);
    }

    // ── Internal state ──

    private bool _activeIsA;
    private string? _currentHttpsUrl;
    private EventHandler? _pendingOpenedHandler;
    private EventHandler? _pendingFailedHandler;
    private CompositionImage? _pendingOpenedTarget;
    private IColorService? _colorService;

    public CrossFadeImage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        BindLayerCenterPointsToSize();
    }

    private void BindLayerCenterPointsToSize()
    {
        try
        {
            BindCenterPoint(LayerA);
            BindCenterPoint(LayerB);
        }
        catch
        {
            // Composition may be unavailable in design-time; the fallback in
            // AnimateLayerSwap will still give a usable, if uncentered, swap.
        }

        static void BindCenterPoint(UIElement element)
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            var expr = compositor.CreateExpressionAnimation("Vector3(visual.Size.X / 2, visual.Size.Y / 2, 0)");
            expr.SetReferenceParameter("visual", visual);
            visual.StartAnimation("CenterPoint", expr);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var src = GetEffectiveSource();
        if (string.IsNullOrEmpty(src)) return;
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(src) ?? src;
        if (!string.Equals(httpsUrl, _currentHttpsUrl, StringComparison.Ordinal))
        {
            _currentHttpsUrl = null;
            ApplySource(src);
        }
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CrossFadeImage self)
            self.ApplySource(self.GetEffectiveSource());
    }

    private string? GetEffectiveSource()
        => !string.IsNullOrWhiteSpace(Source) ? Source : FallbackSource;

    private void ApplySource(string? rawUri)
    {
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(rawUri) ?? rawUri;

        // Idempotent guard — VM property updates re-fire on every position
        // tick in this app (PlaybackStateService).
        if (string.Equals(httpsUrl, _currentHttpsUrl, StringComparison.Ordinal))
            return;

        DetachPendingHandlers();
        _currentHttpsUrl = httpsUrl;

        if (string.IsNullOrEmpty(httpsUrl))
        {
            FadeOutActive();
            PlaceholderLayer.Background = PlaceholderBrush;
            return;
        }

        // Kick off extracted-color fetch in parallel so the placeholder gets
        // tinted before the bitmap finishes loading.
        _ = LoadPlaceholderColorAsync(httpsUrl);

        var idleLayer = _activeIsA ? LayerB : LayerA;
        idleLayer.DecodePixelSize = DecodePixelWidth;
        idleLayer.ImageUrl = httpsUrl;

        if (idleLayer.IsImageLoaded)
        {
            DispatcherQueue?.TryEnqueue(() => RunFade(httpsUrl));
        }
        else
        {
            EventHandler? openedHandler = null;
            EventHandler? failedHandler = null;
            openedHandler = (_, _) =>
            {
                if (_pendingOpenedHandler != openedHandler) return;
                DetachPendingHandlers();
                RunFade(httpsUrl);
            };
            failedHandler = (_, _) =>
            {
                if (_pendingFailedHandler != failedHandler) return;
                DetachPendingHandlers();
                if (!string.IsNullOrEmpty(FallbackSource)
                    && !string.Equals(Source, FallbackSource, StringComparison.Ordinal))
                {
                    _currentHttpsUrl = null;
                    ApplySource(FallbackSource);
                    return;
                }
                FadeOutActive();
            };
            _pendingOpenedHandler = openedHandler;
            _pendingFailedHandler = failedHandler;
            _pendingOpenedTarget = idleLayer;
            idleLayer.ImageOpened += openedHandler;
            idleLayer.ImageFailed += failedHandler;
        }
    }

    private void RunFade(string httpsUrl)
    {
        // Source may have changed again between ImageOpened firing and this
        // continuation running. Skip stale fades.
        if (!string.Equals(_currentHttpsUrl, httpsUrl, StringComparison.Ordinal))
            return;

        var idleLayer = _activeIsA ? LayerB : LayerA;
        var activeLayer = _activeIsA ? LayerA : LayerB;

        // Pop-in / pop-out feel: new image scales 1.08 → 1.0, old image scales
        // 1.0 → 0.92 as it fades. Matches the now-playing surface swap motion.
        AnimateLayerSwap(idleLayer,   opacityTo: 1f, scaleFrom: 1.08f, scaleTo: 1f);
        AnimateLayerSwap(activeLayer, opacityTo: 0f, scaleFrom: 1f,    scaleTo: 0.92f);
        idleLayer.Opacity = 1;
        activeLayer.Opacity = 0;

        // Swap roles so a subsequent source change targets the other layer.
        // The previous layer's ImageUrl stays set during the fade — clearing
        // it now would yank the old surface mid-animation. The next ApplySource
        // overwrites it.
        _activeIsA = !_activeIsA;
    }

    private void FadeOutActive()
    {
        var activeLayer = _activeIsA ? LayerA : LayerB;
        AnimateOpacity(activeLayer, to: 0f);
    }

    private async Task LoadPlaceholderColorAsync(string httpsUrl)
    {
        try
        {
            _colorService ??= Ioc.Default.GetService<IColorService>();
            if (_colorService == null) return;

            var color = await _colorService.GetColorAsync(httpsUrl).ConfigureAwait(true);

            if (!string.Equals(_currentHttpsUrl, httpsUrl, StringComparison.Ordinal))
                return;
            if (color == null) return;

            var hex = ActualTheme == ElementTheme.Dark
                ? color.DarkHex ?? color.RawHex
                : color.LightHex ?? color.RawHex;

            if (!string.IsNullOrEmpty(hex) && TintColorHelper.TryParseHex(hex, out var parsed))
                PlaceholderLayer.Background = new SolidColorBrush(parsed);
        }
        catch
        {
            // Best-effort.
        }
    }

    private void AnimateOpacity(UIElement target, float to)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(target);
            var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1f, to);
            animation.Duration = TimeSpan.FromMilliseconds(FadeDurationMs);
            visual.StartAnimation("Opacity", animation);
        }
        catch
        {
            target.Opacity = to;
        }
    }

    private void AnimateLayerSwap(UIElement target, float opacityTo, float scaleFrom, float scaleTo)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(target);
            var compositor = visual.Compositor;

            if (target is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                visual.CenterPoint = new System.Numerics.Vector3(
                    (float)(fe.ActualWidth / 2.0),
                    (float)(fe.ActualHeight / 2.0),
                    0f);
            }

            visual.Scale = new System.Numerics.Vector3(scaleFrom, scaleFrom, 1f);

            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(1f, opacityTo);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(FadeDurationMs);

            var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(1f, new System.Numerics.Vector3(scaleTo, scaleTo, 1f));
            scaleAnim.Duration = TimeSpan.FromMilliseconds(FadeDurationMs);

            visual.StartAnimation("Opacity", opacityAnim);
            visual.StartAnimation("Scale", scaleAnim);
        }
        catch
        {
            target.Opacity = opacityTo;
        }
    }

    private void DetachPendingHandlers()
    {
        if (_pendingOpenedTarget is not null)
        {
            if (_pendingOpenedHandler is not null)
                _pendingOpenedTarget.ImageOpened -= _pendingOpenedHandler;
            if (_pendingFailedHandler is not null)
                _pendingOpenedTarget.ImageFailed -= _pendingFailedHandler;
        }
        _pendingOpenedHandler = null;
        _pendingFailedHandler = null;
        _pendingOpenedTarget = null;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachPendingHandlers();
        // Clear both ImageUrls so the inner CompositionImage unpins. The
        // layers will be repinned on the next ApplySource after reload.
        LayerA.ImageUrl = null;
        LayerB.ImageUrl = null;
        PlaceholderLayer.Background = PlaceholderBrush;
        _currentHttpsUrl = null;
    }
}
