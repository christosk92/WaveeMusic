using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.Core.Http;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Imaging;

/// <summary>
/// Reusable cross-fading image control with a stable layout footprint.
///
/// <para>
/// Two stacked <see cref="Image"/> layers live inside a fixed-size host Grid.
/// When <see cref="Source"/> changes, the new bitmap is loaded into the idle
/// layer; once it reports <c>PixelWidth &gt; 0</c> (cache hit) or
/// <c>ImageOpened</c> fires (cold load), a Composition opacity animation
/// fades the layers in parallel — the old image stays fully painted until
/// the new one is ready. No flicker, no 0×0 reflow.
/// </para>
///
/// <para>
/// Cache-aware via <see cref="ImageCacheService"/>: cache-hit transitions are
/// near-instant. At any moment we hold at most one pin — each new source
/// unpins the previous URL atomically, which avoids leaks under rapid swaps.
/// Mirrors the pin-balance pattern in <c>TrackItem.xaml.cs</c>.
/// </para>
///
/// <para>
/// Palette-aware placeholder: while the bitmap loads, the placeholder layer
/// is tinted with the image's extracted dominant color (theme-aware, via
/// <see cref="IColorService"/>). The tint sits behind the image layers, so
/// it's only visible until the image fades in — no visual cost when the
/// bitmap was already cached. Falls back to <see cref="PlaceholderBrush"/>
/// when extraction fails or the service isn't wired.
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
            new PropertyMetadata(Stretch.UniformToFill));

    public static readonly DependencyProperty PlaceholderBrushProperty =
        DependencyProperty.Register(nameof(PlaceholderBrush), typeof(Brush), typeof(CrossFadeImage),
            new PropertyMetadata(null));

    public static readonly DependencyProperty FadeDurationMsProperty =
        DependencyProperty.Register(nameof(FadeDurationMs), typeof(int), typeof(CrossFadeImage),
            new PropertyMetadata(240));

    // CornerRadius is inherited from Control — no extra DP needed.

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

    public Stretch Stretch
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
    private string? _pinnedHttpsUrl;
    private int _pinnedDecode;
    private RoutedEventHandler? _pendingOpenedHandler;
    private Image? _pendingOpenedTarget;
    private DispatcherTimer? _fadeCleanupTimer;
    private Image? _fadeCleanupLayer;
    private ImageCacheService? _cache;
    private IColorService? _colorService;

    public CrossFadeImage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        BindLayerCenterPointsToSize();
    }

    /// <summary>
    /// Pin each layer's Composition <c>CenterPoint</c> to half its own size via
    /// an <c>ExpressionAnimation</c>. This way the pop-in/pop-out scale always
    /// pivots around the image center, even on the first swap before layout
    /// has settled (when <c>ActualWidth</c> would still be 0).
    /// </summary>
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
        // Re-apply Source on (re)load. Two scenarios this covers:
        //   1. The DP was assigned while the control was outside the visual
        //      tree (or its parent was Collapsed). OnSourceChanged ran but
        //      the fade animation either silently failed or completed against
        //      an unrendered tree, leaving the layers at Opacity=0.
        //   2. The control was unloaded — OnUnloaded clears layer Sources and
        //      _currentHttpsUrl. On reload the Source DP value is unchanged, so
        //      OnSourceChanged does NOT refire, and the layers stay empty.
        // Both reduce to: if Source is non-null and our internal cache thinks
        // we haven't applied it (or the layers are blank), apply it now.
        var src = GetEffectiveSource();
        if (string.IsNullOrEmpty(src)) return;
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(src) ?? src;
        if (!string.Equals(httpsUrl, _currentHttpsUrl, StringComparison.Ordinal)
            || (LayerA.Source is null && LayerB.Source is null))
        {
            // Force a fresh ApplySource by clearing the dedup key.
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

        // Idempotent: bound VM properties re-fire on every position tick in
        // this app (PlaybackStateService). Without this guard the control
        // would thrash through fades for no reason.
        if (string.Equals(httpsUrl, _currentHttpsUrl, StringComparison.Ordinal))
            return;

        // Tear down any pending cold-load handler from the previous swap.
        DetachPendingOpenedHandler();
        StopFadeCleanupTimer(clearPendingLayer: false);

        // Pin/unpin model: at any moment we hold at most one pin. Each new
        // source unpins the previous URL (regardless of whether its fade
        // completed) and pins the new one. This avoids leaks under rapid
        // source changes — the alternative of unpinning in the post-fade
        // timer leaks pins when a second source arrives mid-fade.
        UnpinPrevious();
        _currentHttpsUrl = httpsUrl;

        if (string.IsNullOrEmpty(httpsUrl))
        {
            // Empty source: fade the active layer to 0, restore the user's
            // placeholder brush (if any) since we have no extracted color
            // to drive the placeholder anymore.
            FadeOutActive();
            PlaceholderLayer.Background = PlaceholderBrush;
            return;
        }

        // Kick off extracted-color fetch in parallel with bitmap load so the
        // placeholder layer behind the image is tinted with the album palette
        // while the bitmap is still resolving. Cache hits inside IColorService
        // make this near-free for already-seen tracks.
        _ = LoadPlaceholderColorAsync(httpsUrl);

        var bitmap = ResolveBitmap(httpsUrl);
        if (bitmap == null)
        {
            FadeOutActive();
            return;
        }

        var idleLayer = _activeIsA ? LayerB : LayerA;
        idleLayer.ImageFailed -= OnLayerImageFailed;
        idleLayer.Source = bitmap;
        idleLayer.ImageFailed += OnLayerImageFailed;

        _cache?.Pin(httpsUrl, DecodePixelWidth);
        _pinnedHttpsUrl = httpsUrl;
        _pinnedDecode = DecodePixelWidth;

        if (bitmap.PixelWidth > 0)
        {
            // Cache hit — schedule on next dispatcher tick so the layer
            // assignment has a chance to compose into the visual tree before
            // we start animating opacity.
            DispatcherQueue?.TryEnqueue(() => RunFade(httpsUrl));
        }
        else
        {
            // Cold load — fade once the bitmap actually paints. Keep a
            // reference to the handler so we can detach it if a new URL
            // arrives before this one resolves.
            RoutedEventHandler? handler = null;
            handler = (_, _) =>
            {
                if (_pendingOpenedHandler == handler)
                {
                    DetachPendingOpenedHandler();
                    RunFade(httpsUrl);
                }
            };
            _pendingOpenedHandler = handler;
            _pendingOpenedTarget = idleLayer;
            idleLayer.ImageOpened += handler;
        }
    }

    private BitmapImage? ResolveBitmap(string httpsUrl)
    {
        try
        {
            _cache ??= Ioc.Default.GetService<ImageCacheService>();
            if (_cache != null)
                return _cache.GetOrCreate(httpsUrl, DecodePixelWidth);

            // Design-time / Ioc not wired: still produce something usable.
            var fallback = new BitmapImage(new Uri(httpsUrl));
            if (DecodePixelWidth > 0)
                fallback.DecodePixelWidth = DecodePixelWidth;
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    private void RunFade(string httpsUrl)
    {
        // The Source property may have changed again between the cold-load
        // ImageOpened firing and this dispatcher continuation running. Skip
        // the fade if the URL we were going to swap to is no longer current.
        if (!string.Equals(_currentHttpsUrl, httpsUrl, StringComparison.Ordinal))
            return;

        var idleLayer = _activeIsA ? LayerB : LayerA;
        var activeLayer = _activeIsA ? LayerA : LayerB;

        // Pop-in/pop-out feel: the new image scales down from slightly larger
        // (1.08) to its rest size, the outgoing image scales down to 0.92 as
        // it fades. Read together it looks like the new artwork "lands" while
        // the old one recedes — matches the swap motion in Apple Music's
        // now-playing surface.
        AnimateLayerSwap(idleLayer,   opacityTo: 1f, scaleFrom: 1.08f, scaleTo: 1f);
        AnimateLayerSwap(activeLayer, opacityTo: 0f, scaleFrom: 1f,    scaleTo: 0.92f);
        idleLayer.Opacity = 1;
        activeLayer.Opacity = 0;

        // Swap roles immediately so a subsequent source change targets the
        // other layer. The previous layer's Source is left in place during
        // the fade — clearing it now would yank the old image mid-animation.
        _activeIsA = !_activeIsA;

        // Clear the now-hidden layer's Source after the fade settles so it
        // can drop its decoded bitmap reference. Keep one owned timer instead
        // of creating a new closure per artwork change; rapid skips otherwise
        // keep several delayed cleanup closures alive at once.
        ScheduleFadeCleanup(activeLayer);
    }

    private void ScheduleFadeCleanup(Image fadedLayer)
    {
        StopFadeCleanupTimer(clearPendingLayer: false);

        _fadeCleanupLayer = fadedLayer;
        _fadeCleanupTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FadeDurationMs + 40)
        };
        _fadeCleanupTimer.Tick += OnFadeCleanupTimerTick;
        _fadeCleanupTimer.Start();
    }

    private void OnFadeCleanupTimerTick(object? sender, object e)
    {
        StopFadeCleanupTimer(clearPendingLayer: true);
    }

    private void StopFadeCleanupTimer(bool clearPendingLayer)
    {
        if (_fadeCleanupTimer != null)
        {
            _fadeCleanupTimer.Stop();
            _fadeCleanupTimer.Tick -= OnFadeCleanupTimerTick;
            _fadeCleanupTimer = null;
        }

        if (clearPendingLayer && _fadeCleanupLayer != null && _fadeCleanupLayer.Opacity == 0)
        {
            _fadeCleanupLayer.Source = null;
            ResetLayerScale(_fadeCleanupLayer);
        }

        _fadeCleanupLayer = null;
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

            // Stale-result guard: a newer source may have replaced this URL
            // while the color fetch was in flight.
            if (!string.Equals(_currentHttpsUrl, httpsUrl, StringComparison.Ordinal))
                return;
            if (color == null) return;

            // Theme-aware: matches the pattern used by ArtistPage when picking
            // panel colors. Falls back to RawHex if the theme-specific variant
            // isn't supplied by the API for this image.
            var hex = ActualTheme == ElementTheme.Dark
                ? color.DarkHex ?? color.RawHex
                : color.LightHex ?? color.RawHex;

            if (!string.IsNullOrEmpty(hex) && TintColorHelper.TryParseHex(hex, out var parsed))
                PlaceholderLayer.Background = new SolidColorBrush(parsed);
        }
        catch
        {
            // Best-effort — if the service throws or the URL is unparseable,
            // leave the placeholder at whatever PlaceholderBrush gave us.
        }
    }

    private void AnimateOpacity(UIElement target, float to)
    {
        // Hand-rolled Composition animation rather than DoubleAnimation /
        // CommunityToolkit AnimationBuilder — same reasoning as
        // ImageFallbackBehavior.AnimateFadeIn: the AnimationBuilder path
        // calls SetIsTranslationEnabled which fail-fasts dcompi.dll on
        // ARM64 / WinAppSDK 2.0-preview2 when the compositor is busy.
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
            // Fall back to a hard switch — better than throwing.
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

    private static void ResetLayerScale(UIElement target)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(target);
            visual.Scale = System.Numerics.Vector3.One;
        }
        catch
        {
            // Best-effort — next swap will reset CenterPoint regardless.
        }
    }

    private void DetachPendingOpenedHandler()
    {
        if (_pendingOpenedHandler != null && _pendingOpenedTarget != null)
            _pendingOpenedTarget.ImageOpened -= _pendingOpenedHandler;
        _pendingOpenedHandler = null;
        _pendingOpenedTarget = null;
    }

    private void OnLayerImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image)
            image.ImageFailed -= OnLayerImageFailed;

        _cache?.Invalidate(_currentHttpsUrl, DecodePixelWidth);

        if (string.Equals(_currentHttpsUrl, SpotifyImageHelper.ToHttpsUrl(Source) ?? Source, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(FallbackSource)
            && !string.Equals(Source, FallbackSource, StringComparison.Ordinal))
        {
            _currentHttpsUrl = null;
            ApplySource(FallbackSource);
            return;
        }

        FadeOutActive();
    }

    private void UnpinPrevious()
    {
        if (!string.IsNullOrEmpty(_pinnedHttpsUrl))
        {
            _cache?.Unpin(_pinnedHttpsUrl, _pinnedDecode);
            _pinnedHttpsUrl = null;
            _pinnedDecode = 0;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachPendingOpenedHandler();
        StopFadeCleanupTimer(clearPendingLayer: false);
        UnpinPrevious();
        LayerA.Source = null;
        LayerB.Source = null;
        LayerA.ImageFailed -= OnLayerImageFailed;
        LayerB.ImageFailed -= OnLayerImageFailed;
        PlaceholderLayer.Background = PlaceholderBrush;
        _currentHttpsUrl = null;
    }
}
