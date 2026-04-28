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
            new PropertyMetadata(180));

    // CornerRadius is inherited from Control — no extra DP needed.

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
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
    private ImageCacheService? _cache;
    private IColorService? _colorService;

    public CrossFadeImage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CrossFadeImage self)
            self.ApplySource(e.NewValue as string);
    }

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
        idleLayer.Source = bitmap;

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

        AnimateOpacity(idleLayer, to: 1f);
        AnimateOpacity(activeLayer, to: 0f);

        // Swap roles immediately so a subsequent source change targets the
        // other layer. The previous layer's Source is left in place during
        // the fade — clearing it now would yank the old image mid-animation.
        _activeIsA = !_activeIsA;

        // Clear the now-hidden layer's Source after the fade settles so it
        // can drop its decoded bitmap reference. If a fresh source comes in
        // and re-promotes this layer to active before the timer fires, the
        // Opacity check below will skip the clear.
        var fadedLayer = activeLayer;
        var timer = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FadeDurationMs + 40)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (fadedLayer.Opacity == 0)
                fadedLayer.Source = null;
        };
        timer.Start();
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

    private void DetachPendingOpenedHandler()
    {
        if (_pendingOpenedHandler != null && _pendingOpenedTarget != null)
            _pendingOpenedTarget.ImageOpened -= _pendingOpenedHandler;
        _pendingOpenedHandler = null;
        _pendingOpenedTarget = null;
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
        UnpinPrevious();
        LayerA.Source = null;
        LayerB.Source = null;
        PlaceholderLayer.Background = PlaceholderBrush;
        _currentHttpsUrl = null;
    }
}
