using System;
using System.Collections.Generic;
using System.Numerics;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Controls.TabBar;

namespace Wavee.UI.WinUI.Controls.Imaging;

/// <summary>
/// Composition-backed image control. Hosts a <see cref="SpriteVisual"/>
/// whose brush is the GPU-resident <see cref="CachedImage.Surface"/> from
/// <see cref="ImageCacheService"/>. No <c>BitmapImage</c> in the visual
/// tree â€” the decoded CPU pixel buffer is released after the GPU upload.
///
/// <para>
/// Usage: <c>&lt;imaging:CompositionImage ImageUrl="{x:Bind Url}" DecodePixelSize="200" /&gt;</c>.
/// Set <see cref="IsCircle"/> for circular crops, <see cref="CornerRadius"/>
/// for rounded rectangles. <see cref="PlaceholderBrush"/> renders behind the
/// surface until the load completes.
/// </para>
///
/// <para>
/// Pin / unpin happens on Loaded / Unloaded automatically. <see cref="ImageUrl"/>
/// changes mid-life rebalance: unpin the previous URL, pin the new one,
/// swap the surface brush atomically.
/// </para>
/// </summary>
public sealed partial class CompositionImage : UserControl, INavCacheSurfaceParticipant
{
    // â”€â”€ Dependency Properties â”€â”€

    public static readonly DependencyProperty ImageUrlProperty =
        DependencyProperty.Register(nameof(ImageUrl), typeof(string), typeof(CompositionImage),
            new PropertyMetadata(null, OnImageUrlChanged));

    public static readonly DependencyProperty DecodePixelSizeProperty =
        DependencyProperty.Register(nameof(DecodePixelSize), typeof(int), typeof(CompositionImage),
            new PropertyMetadata(0, OnDecodePixelSizeChanged));

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(nameof(Stretch), typeof(Stretch), typeof(CompositionImage),
            new PropertyMetadata(Microsoft.UI.Xaml.Media.Stretch.UniformToFill, OnStretchChanged));

    public static readonly DependencyProperty IsCircleProperty =
        DependencyProperty.Register(nameof(IsCircle), typeof(bool), typeof(CompositionImage),
            new PropertyMetadata(false, OnClipShapeChanged));

    public static readonly DependencyProperty PlaceholderBrushProperty =
        DependencyProperty.Register(nameof(PlaceholderBrush), typeof(Brush), typeof(CompositionImage),
            new PropertyMetadata(null));

    public static readonly DependencyProperty PlaceholderOpacityProperty =
        DependencyProperty.Register(nameof(PlaceholderOpacity), typeof(double), typeof(CompositionImage),
            new PropertyMetadata(1.0, OnPlaceholderOpacityChanged));

    public static readonly DependencyProperty FadeInDurationMsProperty =
        DependencyProperty.Register(nameof(FadeInDurationMs), typeof(int), typeof(CompositionImage),
            new PropertyMetadata(220));

    public static readonly DependencyProperty IsImageLoadedProperty =
        DependencyProperty.Register(nameof(IsImageLoaded), typeof(bool), typeof(CompositionImage),
            new PropertyMetadata(false));

    public string? ImageUrl
    {
        get => (string?)GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    public int DecodePixelSize
    {
        get => (int)GetValue(DecodePixelSizeProperty);
        set => SetValue(DecodePixelSizeProperty, value);
    }

    public new Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public bool IsCircle
    {
        get => (bool)GetValue(IsCircleProperty);
        set => SetValue(IsCircleProperty, value);
    }

    public Brush? PlaceholderBrush
    {
        get => (Brush?)GetValue(PlaceholderBrushProperty);
        set => SetValue(PlaceholderBrushProperty, value);
    }

    public double PlaceholderOpacity
    {
        get => (double)GetValue(PlaceholderOpacityProperty);
        set => SetValue(PlaceholderOpacityProperty, value);
    }

    public int FadeInDurationMs
    {
        get => (int)GetValue(FadeInDurationMsProperty);
        set => SetValue(FadeInDurationMsProperty, value);
    }

    /// <summary>
    /// True once the underlying surface has reported a successful load. Useful
    /// for x:Bind triggers â€” e.g. collapse a placeholder glyph when this flips
    /// to true. Set by the control; binding is one-way out.
    /// </summary>
    public bool IsImageLoaded
    {
        get => (bool)GetValue(IsImageLoadedProperty);
        private set => SetValue(IsImageLoadedProperty, value);
    }

    // â”€â”€ Events â”€â”€

    public event EventHandler? ImageOpened;
    public event EventHandler? ImageFailed;

    // â”€â”€ State â”€â”€

    private ImageCacheService? _cache;
    private SpriteVisual? _spriteVisual;
    private CompositionSurfaceBrush? _surfaceBrush;
    // Clip resources. The geometry sizes are bound via ExpressionAnimation
    // to the SpriteVisual's Size so they auto-track regardless of when
    // ActualWidth/ActualHeight become valid. Without that, an x:Load-deferred
    // CompositionImage realized after its parent's layout pass would have
    // its clip stuck at 0Ã—0 â€” invisible â€” until the next SizeChanged fired,
    // which doesn't happen reliably in ItemsRepeater virtualization.
    private CompositionEllipseGeometry? _ellipseGeometry;
    private CompositionGeometricClip? _ellipseClip;
    private CompositionRoundedRectangleGeometry? _roundedRectGeometry;
    private CompositionGeometricClip? _roundedRectClip;
    private CachedImage? _currentCachedImage;
    private string? _pinnedUrl;
    private string? _resolvedUrl;
    private int _pinnedDecode;
    private bool _isAttached;
    private bool _initialized;
    private bool _releasedForNavigationCache;
    private EventHandler? _loadCompletedHandler;

    // â”€â”€ Diagnostics â”€â”€
    // Opt-in with WAVEE_IMAGE_DIAGNOSTICS. The call sites pass interpolated
    // strings on hot paths, so compile-time gating avoids hidden allocations.
    private static int s_nextDiagId;
    private readonly int _diagId = System.Threading.Interlocked.Increment(ref s_nextDiagId);

    [System.Diagnostics.Conditional("WAVEE_IMAGE_DIAGNOSTICS")]
    private void DiagLog(string stage, string? extra = null)
    {
        var urlTail = _resolvedUrl is null ? "(null)" :
            _resolvedUrl.Length > 18 ? "…" + _resolvedUrl[^18..] : _resolvedUrl;
        var hasBrush = _surfaceBrush is not null ? "B" : "-";
        var hasVis = _spriteVisual is not null ? "V" : "-";
        var hasCached = _currentCachedImage is not null ? "C" : "-";
        var loaded = _currentCachedImage?.IsLoaded == true ? "L" : (_currentCachedImage?.LoadFailed == true ? "F" : "-");
        var att = _isAttached ? "att" : "det";
        System.Diagnostics.Debug.WriteLine(
            $"[CompImg:{_diagId:D4}|{att}|{hasBrush}{hasVis}{hasCached}{loaded}] {stage} url={urlTail}"
            + (extra is null ? "" : $" {extra}"));
    }

    public CompositionImage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private Visual? _hostVisual;

    private void EnsureCompositionResources()
    {
        if (_initialized) return;
        _initialized = true;

        _cache = Ioc.Default.GetService<ImageCacheService>();

        try
        {
            _hostVisual = ElementCompositionPreview.GetElementVisual(SurfaceHost);
            var compositor = _hostVisual.Compositor;
            _spriteVisual = compositor.CreateSpriteVisual();
            // Auto-track the host's size. Avoids the race where ActualWidth is
            // still 0 on Loaded â€” the visual would otherwise stay 0Ã—0 forever
            // unless a later SizeChanged fired, which doesn't happen for
            // already-sized parents inside ItemsRepeater virtualization.
            _spriteVisual.RelativeSizeAdjustment = Vector2.One;
            _surfaceBrush = compositor.CreateSurfaceBrush();
            _surfaceBrush.Stretch = MapStretch(Stretch);
            _surfaceBrush.HorizontalAlignmentRatio = 0.5f;
            _surfaceBrush.VerticalAlignmentRatio = 0.5f;
            _spriteVisual.Brush = _surfaceBrush;

            // Build BOTH clip shapes once and bind their dimensions to the
            // HOST (parent) visual's Size via expression animation. The
            // SpriteVisual's own Size stays (0,0) because it uses
            // RelativeSizeAdjustment, so we MUST reference the host visual
            // â€” referencing the sprite would clip everything to 0Ã—0.
            CreateClipGeometriesWithExpressions(compositor);
        }
        catch
        {
            // Composition can be unavailable in design-time; the control
            // still renders the placeholder behind nothing.
        }
    }

    private void CreateClipGeometriesWithExpressions(Compositor compositor)
    {
        if (_spriteVisual is null || _hostVisual is null) return;

        // Rounded rectangle clip â€” CompositionGeometricClip wrapping a
        // CompositionRoundedRectangleGeometry whose Size is expression-bound
        // to the HOST visual size (SurfaceHost's element visual, which WinUI
        // auto-sizes from layout). CornerRadius is set per-shape by UpdateClip.
        _roundedRectGeometry = compositor.CreateRoundedRectangleGeometry();

        var rectSizeExpr = compositor.CreateExpressionAnimation("host.Size");
        rectSizeExpr.SetReferenceParameter("host", _hostVisual);
        _roundedRectGeometry.StartAnimation("Size", rectSizeExpr);

        _roundedRectClip = compositor.CreateGeometricClip();
        _roundedRectClip.Geometry = _roundedRectGeometry;

        // Ellipse clip â€” center and radius expression-bound to host.Size/2
        // and min(host.X,host.Y)/2 respectively.
        _ellipseGeometry = compositor.CreateEllipseGeometry();

        var centerExpr = compositor.CreateExpressionAnimation(
            "Vector2(host.Size.X / 2, host.Size.Y / 2)");
        centerExpr.SetReferenceParameter("host", _hostVisual);
        _ellipseGeometry.StartAnimation("Center", centerExpr);

        var radiusExpr = compositor.CreateExpressionAnimation(
            "Vector2(Min(host.Size.X, host.Size.Y) / 2, Min(host.Size.X, host.Size.Y) / 2)");
        radiusExpr.SetReferenceParameter("host", _hostVisual);
        _ellipseGeometry.StartAnimation("Radius", radiusExpr);

        _ellipseClip = compositor.CreateGeometricClip();
        _ellipseClip.Geometry = _ellipseGeometry;
    }

    private void AttachVisualToHost()
    {
        if (_spriteVisual is null || SurfaceHost is null)
        {
            DiagLog("AttachVisualToHost:bail",
                $"visual={(_spriteVisual is null ? "null" : "ok")} host={(SurfaceHost is null ? "null" : "ok")}");
            return;
        }
        try
        {
            ElementCompositionPreview.SetElementChildVisual(SurfaceHost, _spriteVisual);
            UpdateClip();
            DiagLog("AttachVisualToHost:done");
        }
        catch (Exception ex)
        {
            DiagLog("AttachVisualToHost:EX", ex.GetType().Name);
        }
    }

    private void DetachVisualFromHost()
    {
        if (SurfaceHost is null) return;
        try
        {
            ElementCompositionPreview.SetElementChildVisual(SurfaceHost, null);
        }
        catch
        {
            // Composition may be torn down already.
        }
    }

    private static CompositionStretch MapStretch(Stretch s) => s switch
    {
        Microsoft.UI.Xaml.Media.Stretch.None => CompositionStretch.None,
        Microsoft.UI.Xaml.Media.Stretch.Fill => CompositionStretch.Fill,
        Microsoft.UI.Xaml.Media.Stretch.Uniform => CompositionStretch.Uniform,
        _ => CompositionStretch.UniformToFill,
    };

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DiagLog("OnLoaded:enter", $"ImageUrl={ImageUrl ?? "(null)"} decode={DecodePixelSize}");
        _isAttached = true;
        _releasedForNavigationCache = false;
        EnsureCompositionResources();
        ImageLoadingSuspension.Changed += OnSuspensionChanged;

        // Re-attach the SpriteVisual eagerly. Unload/nav-cache release detaches
        // it, and TryLoadCurrent's same-url path can return without doing a
        // fresh attach. SetElementChildVisual is idempotent.
        AttachVisualToHost();

        // Defensive path for any retained cached image whose surface completed
        // before the visual was re-attached.
        if (_currentCachedImage is { IsLoaded: true, Surface: not null } cached
            && _surfaceBrush is not null
            && _surfaceBrush.Surface is null)
        {
            try
            {
                _surfaceBrush.Surface = cached.Surface;
                IsImageLoaded = true;
                FadeOutPlaceholder();
                DiagLog("OnLoaded:reAssignSurfaceFromInFlightLoad");
            }
            catch (Exception ex)
            {
                DiagLog("OnLoaded:reAssignEX", ex.GetType().Name);
            }
        }

        TryLoadCurrent();
        DiagLog("OnLoaded:exit");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DiagLog("OnUnloaded:enter");
        _isAttached = false;
        ImageLoadingSuspension.Changed -= OnSuspensionChanged;

        // Treat Unloaded as out-of-view. Drop our reference to the cached
        // surface and evict the cache entry if no other visible control still
        // pins it. On the next Loaded pass the placeholder paints first and the
        // image reloads through the normal OS/disk-backed image path.
        ReleaseSurfaceReference(resetResolvedUrl: true, evictIfUnpinned: true);

        _releasedForNavigationCache = false;
        DiagLog("OnUnloaded:exit");
    }

    // ── INavCacheSurfaceParticipant ──
    //
    // Driven by the NavCacheSurfaces tree-walk when this control's hosting page
    // goes off-screen / comes back. Replaces the reverted indiscriminate
    // surface tree-walk: scoped to dormant pages only, and restore is a
    // deterministic clean reload through the normal placeholder path.

    /// <summary>
    /// Sheds the GPU surface for an off-screen cached page. Unpins the LRU
    /// entry (refcount-safe — a shared image still pinned by another realized
    /// control survives) and resets <c>_resolvedUrl</c> so a later
    /// <see cref="RestoreForNavCache"/> runs the standard fresh-load path.
    /// Unpinning is the whole point: it lets <c>ImageCacheService</c> evict
    /// the surface (the earlier nav-cache release kept the pin and freed no
    /// memory).
    /// </summary>
    public bool ReleaseForNavCache()
    {
        if (_releasedForNavigationCache)
            return false;

        if (_currentCachedImage is null
            && string.IsNullOrEmpty(_pinnedUrl)
            && _surfaceBrush is null
            && _spriteVisual is null)
            return false;

        ReleaseSurfaceReference(resetResolvedUrl: true, evictIfUnpinned: true);
        ReleaseCompositionResources();
        _releasedForNavigationCache = true;
        DiagLog("ReleaseForNavCache");
        return true;
    }

    /// <summary>
    /// Re-hydrates after <see cref="ReleaseForNavCache"/>. Runs a clean
    /// <see cref="TryLoadCurrent"/> from a fully-reset state: placeholder first,
    /// then a cache/OS-backed reload through the standard first-realization path.
    /// </summary>
    public bool RestoreForNavCache()
    {
        if (!_releasedForNavigationCache)
            return false;

        _releasedForNavigationCache = false;

        // Not realized yet (e.g. ItemsRepeater hasn't materialized this row) —
        // OnLoaded will TryLoadCurrent from the clean state we left behind.
        if (!_isAttached)
            return true;

        DiagLog("RestoreForNavCache");
        TryLoadCurrent();
        return true;
    }

    /// <summary>Rough GPU bytes held — 0 once released. Diagnostics only.</summary>
    public long EstimatedSurfaceBytes
    {
        get
        {
            if (_releasedForNavigationCache || _surfaceBrush?.Surface is null)
                return 0;
            var d = _pinnedDecode > 0 ? _pinnedDecode : 512;
            return (long)d * d * 4;
        }
    }

    private void ReleaseCompositionResources()
    {
        DetachVisualFromHost();

        try
        {
            if (_spriteVisual is not null)
            {
                _spriteVisual.Brush = null;
                _spriteVisual.Clip = null;
            }
        }
        catch
        {
            // Composition can already be torn down during window close.
        }

        if (_surfaceBrush is not null)
        {
            try { _surfaceBrush.Surface = null; }
            catch { }
        }

        TryDispose(_surfaceBrush);
        _surfaceBrush = null;
        TryDispose(_spriteVisual);
        _spriteVisual = null;
        TryDispose(_ellipseClip);
        _ellipseClip = null;
        TryDispose(_ellipseGeometry);
        _ellipseGeometry = null;
        TryDispose(_roundedRectClip);
        _roundedRectClip = null;
        TryDispose(_roundedRectGeometry);
        _roundedRectGeometry = null;

        _hostVisual = null;
        _initialized = false;
    }

    private static void TryDispose(CompositionObject? obj)
    {
        try { obj?.Dispose(); }
        catch
        {
            // Best effort during window close / composition device teardown.
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // No-op. The SpriteVisual size auto-tracks via RelativeSizeAdjustment,
        // and the clip geometry dimensions are expression-bound to the visual
        // size. The only size-independent attribute is CornerRadius, which is
        // refreshed by UpdateClip from AttachVisualToHost / OnClipShapeChanged.
    }

    private void UpdateClip()
    {
        if (_spriteVisual is null) return;

        // Pick the clip shape. Sizes are already auto-bound via expression
        // animations set up in EnsureCompositionResources, so this method
        // just needs to attach the right clip and refresh per-shape attributes
        // (corner radii) that aren't size-dependent.

        if (IsCircle)
        {
            if (_ellipseClip is not null
                && !ReferenceEquals(_spriteVisual.Clip, _ellipseClip))
            {
                _spriteVisual.Clip = _ellipseClip;
            }
            return;
        }

        var corners = CornerRadius;
        if (corners.TopLeft <= 0 && corners.TopRight <= 0 &&
            corners.BottomLeft <= 0 && corners.BottomRight <= 0)
        {
            if (_spriteVisual.Clip is not null)
                _spriteVisual.Clip = null;
            return;
        }

        if (_roundedRectGeometry is not null && _roundedRectClip is not null)
        {
            // CompositionRoundedRectangleGeometry has a single uniform
            // CornerRadius. Use the max of the four corners â€” this control's
            // consumers all set uniform CornerRadius today.
            var maxRadius = (float)Math.Max(
                Math.Max(corners.TopLeft, corners.TopRight),
                Math.Max(corners.BottomLeft, corners.BottomRight));
            _roundedRectGeometry.CornerRadius = new Vector2(maxRadius);
            if (!ReferenceEquals(_spriteVisual.Clip, _roundedRectClip))
                _spriteVisual.Clip = _roundedRectClip;
        }
    }

    private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((CompositionImage)d).TryLoadCurrent();
    }

    private static void OnDecodePixelSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Decode-size change re-keys the cache; treat as a full reload.
        ((CompositionImage)d).TryLoadCurrent();
    }

    private static void OnStretchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (CompositionImage)d;
        if (self._surfaceBrush is not null)
            self._surfaceBrush.Stretch = MapStretch((Stretch)e.NewValue);
    }

    private static void OnClipShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((CompositionImage)d).UpdateClip();
    }

    private static void OnPlaceholderOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (CompositionImage)d;
        if (self.PlaceholderHost is not null)
            self.PlaceholderHost.Opacity = (double)e.NewValue;
    }

    private void OnSuspensionChanged(bool suspended)
    {
        if (!_isAttached || suspended) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isAttached && !ImageLoadingSuspension.IsSuspended)
                TryLoadCurrent();
        });
    }

    public void RefreshCurrentImage()
    {
        TryLoadCurrent(forceReapply: true);
    }

    private void TryLoadCurrent(bool forceReapply = false)
    {
        DiagLog("TryLoad:enter", $"ImageUrl={ImageUrl ?? "(null)"}");
        if (!_isAttached) { DiagLog("TryLoad:bail:notAttached"); return; }
        _releasedForNavigationCache = false;

        var url = SpotifyImageHelper.ToHttpsUrl(ImageUrl);
        if (string.IsNullOrEmpty(url))
        {
            DiagLog("TryLoad:noUrl:clear");
            ReleasePin(evictIfUnpinned: true);
            ClearVisualsForBlank();
            _resolvedUrl = null;
            return;
        }

        var decode = DecodePixelSize;

        // Already painting this exact (url, decode) â†’ no work.
        if (string.Equals(_resolvedUrl, url, StringComparison.Ordinal)
            && _pinnedDecode == decode
            && _currentCachedImage is not null)
        {
            DiagLog("TryLoad:bail:sameUrl");
            EnsureCompositionResources();
            AttachVisualToHost();

            if (_currentCachedImage is { IsLoaded: true, Surface: not null } cachedx
                && _surfaceBrush is not null
                && (forceReapply || _surfaceBrush.Surface is null || !IsImageLoaded))
            {
                try
                {
                    _surfaceBrush.Surface = cachedx.Surface;
                    IsImageLoaded = true;
                    FadeOutPlaceholder();
                }
                catch
                {
                    // Best effort. The next URL change or load completion will retry.
                }
            }
            return;
        }

        EnsureCompositionResources();
        AttachVisualToHost();
        if (_cache is null || _surfaceBrush is null)
        {
            DiagLog("TryLoad:bail:noBrushOrCache",
                $"cache={(_cache is null ? "null" : "ok")} brush={(_surfaceBrush is null ? "null" : "ok")}");
            return;
        }

        // FAST-PATH â€” peek without kicking off a network load. If the cache
        // already has a decoded surface, swap it onto the brush atomically
        // (single compositor frame, no placeholder flash) and BYPASS the
        // image-loading suspension gate. Suspension only exists to throttle
        // cold network fetches during heavy transition animations; there is
        // no reason to delay rendering an already-decoded GPU surface.
        var peek = _cache.TryGet(url, decode);
        if (peek is { IsLoaded: true, Surface: not null })
        {
            DiagLog("TryLoad:peekHit:fastPath");
            ReleasePin(evictIfUnpinned: true);
            _cache.Pin(url, decode);
            _resolvedUrl = url;
            _pinnedUrl = url;
            _pinnedDecode = decode;
            _currentCachedImage = peek;
            _surfaceBrush.Surface = peek.Surface;
            OnCachedLoaded(success: true);
            return;
        }

        // True cold load â€” respect suspension. OnSuspensionChanged will
        // re-call TryLoadCurrent when the gate lifts.
        if (ImageLoadingSuspension.IsSuspended)
        {
            DiagLog("TryLoad:bail:suspended");
            return;
        }

        var cached = _cache.GetOrCreate(url, decode, pin: true);
        if (cached is null) { DiagLog("TryLoad:bail:cacheReturnedNull"); return; }

        // Drop the OLD pin only after we've successfully pinned the NEW one.
        // ReleasePin doesn't touch any visuals.
        ReleasePin(evictIfUnpinned: true);
        _resolvedUrl = url;
        _pinnedUrl = url;
        _pinnedDecode = decode;
        _currentCachedImage = cached;

        if (cached.IsLoaded)
        {
            // Raced with another control that just finished loading â€” atomic swap.
            _surfaceBrush.Surface = cached.Surface;
            DiagLog("TryLoad:cacheHit:surfaceAssigned");
            OnCachedLoaded(success: true);
            return;
        }
        if (cached.LoadFailed)
        {
            DiagLog("TryLoad:cachePrevFailed");
            OnCachedLoaded(success: false);
            return;
        }

        // Genuine cold load â€” clear the surface so the placeholder shows
        // during the wait, then subscribe.
        try { _surfaceBrush.Surface = null; } catch { }
        ResetPlaceholderOpacity();

        DiagLog("TryLoad:subscribeLoadCompleted");
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (handler is not null)
            {
                try { cached.LoadCompleted -= handler; }
                catch { }
                if (ReferenceEquals(_loadCompletedHandler, handler))
                    _loadCompletedHandler = null;
            }

            var ranOnUI = DispatcherQueue?.TryEnqueue(() =>
            {
                DiagLog("LoadCompleted:dispatched",
                    $"sameRef={ReferenceEquals(_currentCachedImage, cached)} cachedLoaded={cached.IsLoaded} cachedFailed={cached.LoadFailed}");
                if (!ReferenceEquals(_currentCachedImage, cached))
                {
                    DiagLog("LoadCompleted:bail:differentCached");
                    return;
                }
                if (cached.IsLoaded && _surfaceBrush is not null)
                {
                    _surfaceBrush.Surface = cached.Surface;
                    DiagLog("LoadCompleted:surfaceAssigned");
                }
                else if (cached.IsLoaded)
                {
                    DiagLog("LoadCompleted:LOADED_BUT_NO_BRUSH");
                }
                OnCachedLoaded(success: cached.IsLoaded);
            });
            if (ranOnUI != true) DiagLog("LoadCompleted:enqueueFailed",
                $"dq={(DispatcherQueue is null ? "null" : "ok")}");
        };
        _loadCompletedHandler = handler;
        cached.AddLoadCompletedHandler(handler);
    }

    private void OnCachedLoaded(bool success)
    {
        DiagLog("OnCachedLoaded", success ? "success" : "FAIL");
        if (success)
        {
            IsImageLoaded = true;
            FadeOutPlaceholder();
            try { ImageOpened?.Invoke(this, EventArgs.Empty); } catch { }
        }
        else
        {
            IsImageLoaded = false;
            var url = _resolvedUrl;
            var decode = _pinnedDecode;
            ReleasePin();
            ClearVisualsForBlank();
            _resolvedUrl = null;
            _cache?.Invalidate(url, decode);
            try { ImageFailed?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    private void FadeOutPlaceholder()
    {
        if (PlaceholderHost is null) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(PlaceholderHost);
            var anim = visual.Compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(1f, 0f);
            anim.Duration = TimeSpan.FromMilliseconds(Math.Max(1, FadeInDurationMs));
            visual.StartAnimation("Opacity", anim);
        }
        catch
        {
            PlaceholderHost.Opacity = 0;
        }
    }

    /// <summary>
    /// Drops the cache pin and unsubscribes the LoadCompleted handler. When
    /// <paramref name="evictIfUnpinned"/> is true, the shared cache disposes
    /// the entry immediately if this was the last visible pin. Does NOT touch
    /// the brush surface or the placeholder; those decisions belong to the
    /// caller. Used from:
    /// <list type="bullet">
    /// <item>OnUnloaded/nav-cache release - shed off-screen surfaces.</item>
    /// <item>TryLoadCurrent's URL-change paths - atomic surface swap, no clear.</item>
    /// <item>OnCachedLoaded failure - paired with ClearVisualsForBlank.</item>
    /// </list>
    /// </summary>
    private void ReleasePin(bool evictIfUnpinned = false)
    {
        if (_currentCachedImage is not null && _loadCompletedHandler is not null)
        {
            try { _currentCachedImage.LoadCompleted -= _loadCompletedHandler; }
            catch { }
        }
        _loadCompletedHandler = null;
        _currentCachedImage = null;

        if (!string.IsNullOrEmpty(_pinnedUrl))
        {
            try { _cache?.Unpin(_pinnedUrl, _pinnedDecode, evictIfUnpinned); } catch { }
            _pinnedUrl = null;
        }
        _pinnedDecode = 0;
        IsImageLoaded = false;
    }

    private void ReleaseSurfaceReference(bool resetResolvedUrl, bool evictIfUnpinned = false)
    {
        if (_surfaceBrush is not null)
        {
            try { _surfaceBrush.Surface = null; } catch { }
        }

        DetachVisualFromHost();
        ResetPlaceholderOpacity();
        ReleasePin(evictIfUnpinned);
        if (resetResolvedUrl)
            _resolvedUrl = null;
    }

    /// <summary>
    /// Clears the brush surface and resets PlaceholderHost.Opacity so the
    /// placeholder shows. Use when the control should be visually blank
    /// (URL null or load-failure). Unload uses ReleaseSurfaceReference so it
    /// can also reset the resolved URL and cache pin.
    /// </summary>
    private void ClearVisualsForBlank()
    {
        if (_surfaceBrush is not null)
        {
            try { _surfaceBrush.Surface = null; } catch { }
        }
        ResetPlaceholderOpacity();
    }

    private void ResetPlaceholderOpacity()
    {
        if (PlaceholderHost is null) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(PlaceholderHost);
            visual.StopAnimation("Opacity");
        }
        catch { }
        PlaceholderHost.Opacity = PlaceholderOpacity;
    }
}
