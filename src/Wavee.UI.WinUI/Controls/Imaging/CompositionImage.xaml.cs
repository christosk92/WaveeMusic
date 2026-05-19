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
public sealed partial class CompositionImage : UserControl
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

        // Re-attach the SpriteVisual eagerly. If LoadCompleted fired while we
        // were unloaded (subscription kept alive across OnUnloaded — see the
        // comment there), the handler will have assigned cached.Surface to
        // _surfaceBrush but the visual is still detached. TryLoadCurrent's
        // same-url bail-out below short-circuits before AttachVisualToHost,
        // so without this explicit attach the cached surface never paints
        // and the row stays on placeholder. SetElementChildVisual is
        // idempotent — duplicate logs are accepted as the cost of
        // correctness.
        AttachVisualToHost();

        // If the LoadCompleted handler ran during unload and the cached
        // image is now loaded, refresh the brush.Surface (OnUnloaded
        // cleared it to surface-null for the placeholder pass-through).
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

        // Drop the cache pin (release memory pressure on the LRU) and detach
        // the SpriteVisual so the placeholder shows through while we're not
        // visible. But DO NOT unsubscribe from CachedImage.LoadCompleted and
        // DO NOT clear _currentCachedImage / _loadCompletedHandler.
        //
        // Why: WinRT's LoadedImageSurface.LoadCompleted is a ONE-SHOT event.
        // When a TrackItem row is recycled while its image is still loading
        // (very common for artist top-tracks under nav-cache trim + extended-
        // tracks fetch), if we unsubscribe in OnUnloaded the in-flight load
        // completes with zero subscribers — surface is loaded in the cache,
        // but our handler never assigns it to _surfaceBrush. When the row
        // re-loads, TryLoadCurrent's same-url bail-out short-circuits and
        // the visual stays blank forever. Keeping the subscription alive
        // means the handler runs on completion, assigns the surface to the
        // brush, and the next OnLoaded sees the surface ready.
        if (!string.IsNullOrEmpty(_pinnedUrl))
        {
            try { _cache?.Unpin(_pinnedUrl, _pinnedDecode); } catch { }
        }
        _pinnedUrl = null;
        _pinnedDecode = 0;

        if (_surfaceBrush is not null)
        {
            try { _surfaceBrush.Surface = null; } catch { }
        }
        DetachVisualFromHost();
        ResetPlaceholderOpacity();
        IsImageLoaded = false;

        _releasedForNavigationCache = false;
        DiagLog("OnUnloaded:exit");
    }

    public bool ReleaseForNavigationCache()
    {
        if (_releasedForNavigationCache)
            return false;

        if (_currentCachedImage is null
            && string.IsNullOrEmpty(_pinnedUrl)
            && _surfaceBrush?.Surface is null)
            return false;

        // Light release: drop the brush surface and detach the sprite so the
        // image stops painting while the page sits hidden, but KEEP the LRU
        // pin (_pinnedUrl), the cached-entry reference (_currentCachedImage),
        // the LoadCompleted subscription, and the _resolvedUrl marker. The
        // earlier implementation went through ReleaseSurfaceReference, which
        // unpinned the LRU entry — under memory pressure the cache would
        // evict it, and on the return tree-walk TryLoadCurrent's peek would
        // miss and fall into the cold-load path that nulls the surface and
        // waits on a network fetch. With the pin preserved the entry stays
        // resident and RestoreAfterNavigationCache becomes a cheap atomic
        // re-attach. Trade-off: a few MB of GPU memory held per cached page,
        // which is the explicit point of the nav-cache (snappy back/forward).
        if (_surfaceBrush is not null)
        {
            try { _surfaceBrush.Surface = null; } catch { }
        }
        DetachVisualFromHost();
        ResetPlaceholderOpacity();
        IsImageLoaded = false;

        _releasedForNavigationCache = true;
        DiagLog("ReleaseForNavigationCache");
        return true;
    }

    public bool RestoreAfterNavigationCache()
    {
        if (!_releasedForNavigationCache)
            return false;

        if (!_isAttached)
        {
            // Item hasn't realized yet (e.g. ItemsRepeater hasn't materialized
            // this row). Leave the flag set so OnLoaded picks it up — clearing
            // it now would leave the row stuck on placeholder until the next
            // URL change or scroll re-realization.
            return false;
        }

        _releasedForNavigationCache = false;

        // Fast path: cache pin survived, surface still in memory. Atomic
        // brush re-assign + sprite re-attach — no peek, no cold load, no
        // race window for OnUnloaded to clobber the freshly-loaded surface.
        if (_currentCachedImage is { IsLoaded: true, Surface: not null } cached
            && _surfaceBrush is not null
            && _surfaceBrush.Surface is null
            && !string.IsNullOrEmpty(_resolvedUrl))
        {
            try
            {
                EnsureCompositionResources();
                AttachVisualToHost();
                _surfaceBrush.Surface = cached.Surface;
                IsImageLoaded = true;
                FadeOutPlaceholder();
                return true;
            }
            catch
            {
                // Fall through to the standard load path.
            }
        }

        TryLoadCurrent();
        return true;
    }

    public static int ReleaseSurfacesForNavigationCache(DependencyObject? root)
        => VisitCompositionImages(root, static image => image.ReleaseForNavigationCache());

    public static int RestoreSurfacesAfterNavigationCache(DependencyObject? root)
        => VisitCompositionImages(root, static image => image.RestoreAfterNavigationCache());

    private static int VisitCompositionImages(DependencyObject? root, Func<CompositionImage, bool> action)
    {
        if (root is null)
            return 0;

        var count = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is CompositionImage image && action(image))
                count++;

            int childCount;
            try
            {
                childCount = VisualTreeHelper.GetChildrenCount(current);
            }
            catch
            {
                continue;
            }

            for (var i = childCount - 1; i >= 0; i--)
            {
                try
                {
                    stack.Push(VisualTreeHelper.GetChild(current, i));
                }
                catch
                {
                    // Visual tree can mutate during page trim; skip that branch.
                }
            }
        }

        return count;
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
            ReleasePin();
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
            ReleasePin();
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
        ReleasePin();
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
        _loadCompletedHandler = (_, _) =>
        {
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
        cached.AddLoadCompletedHandler(_loadCompletedHandler);
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
    /// Drops the cache pin and unsubscribes the LoadCompleted handler. Does
    /// NOT touch the brush surface or the placeholder â€” those decisions
    /// belong to the caller. Used from:
    /// <list type="bullet">
    /// <item>OnUnloaded â€” keep visuals intact across nav-cache trim/restore.</item>
    /// <item>TryLoadCurrent's URL-change paths â€” atomic surface swap, no clear.</item>
    /// <item>OnCachedLoaded failure â€” paired with ClearVisualsForBlank.</item>
    /// </list>
    /// </summary>
    private void ReleasePin()
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
            try { _cache?.Unpin(_pinnedUrl, _pinnedDecode); } catch { }
            _pinnedUrl = null;
        }
        _pinnedDecode = 0;
        IsImageLoaded = false;
    }

    private void ReleaseSurfaceReference(bool resetResolvedUrl)
    {
        ReleasePin();
        if (resetResolvedUrl)
            _resolvedUrl = null;

        if (_surfaceBrush is not null)
        {
            try { _surfaceBrush.Surface = null; } catch { }
        }

        DetachVisualFromHost();
        ResetPlaceholderOpacity();
    }

    /// <summary>
    /// Clears the brush surface and resets PlaceholderHost.Opacity so the
    /// placeholder shows. Use ONLY when the control should be visually blank
    /// (URLâ†’null or load-failure) â€” never on Unloaded, since nav-cache
    /// trim/restore relies on the visual staying painted.
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
