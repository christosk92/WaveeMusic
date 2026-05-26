using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using CommunityToolkit.Mvvm.DependencyInjection;
using Windows.Foundation;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Behaviors.Card;
using Wavee.UI.WinUI.Controls.Imaging;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Reusable content card with colored placeholder, fade-in image, title and subtitle.
/// Supports square (playlist/album) and circular (artist) image modes.
///
/// <para>This file owns the "core" surface — construction, Loaded/Unloaded
/// lifecycle, image loading state machine, the hover/press visual chrome, and
/// the helpers that several partials share. The class is split across several
/// partial files for readability:</para>
/// <list type="bullet">
///   <item><c>ContentCard.DependencyProperties.cs</c> — the 23 DPs and their
///   <c>PropertyChanged</c> callbacks.</item>
///   <item><c>ContentCard.Navigation.cs</c> — click routing, navigation,
///   drag-source payload construction, connected-animation prep.</item>
///   <item><c>ContentCard.PlaybackHighlight.cs</c> — IsPlaying / IsContextPaused
///   visual state, <see cref="NowPlayingHighlightService"/> subscription,
///   play-button click and buffering pending state.</item>
/// </list>
///
/// <para>Cross-cutting UI concerns are handled by attached behaviors in
/// <c>Wavee.UI.WinUI.Behaviors.Card</c>: image-retry, passive-mode pointer
/// re-registration with <c>handledEventsToo=true</c>, and effective-viewport
/// prefetch / image-loading gating. The behaviors are wired in XAML and call
/// back into <c>internal</c> entry points on this control.</para>
/// </summary>
public sealed partial class ContentCard : UserControl
{
    // ── Events ───────────────────────────────────────────────────────────────

    public event EventHandler? CardClick;
    public event EventHandler? CardMiddleClick;
    public event EventHandler? CardHover;
    public event EventHandler? PlayRequested;
    public event EventHandler? ExternalActionRequested;
    public event TypedEventHandler<ContentCard, RightTappedRoutedEventArgs>? CardRightTapped;

    // ── Backing state ────────────────────────────────────────────────────────

    private bool _isPointerOver;
    private bool _circleSizeHandlerAttached;

    // Viewport gating. Mirrored from CardEffectiveViewportBehavior so the
    // synchronous DP callbacks (OnImageUrlChanged) and OnLoaded can consult
    // it without taking a dependency on the behavior's per-element state.
    private bool _hasEffectiveViewport;
    private bool _isInsideEffectiveViewport = true;

    private const double DefaultCardWidth = 160;
    private const double DefaultCardHorizontalPadding = 16;
    private const double CompactCardHorizontalPadding = 0;
    private const double CircleImageInset = 16;
    private const double MinimumImageSide = 60;
    private const int CardImageDecodeSize = 200;
    // 4 px horizontal breathing room so the 1.03x hover-scale doesn't push
    // the image / text past the parent panel's left edge on edge-row cards.
    // Vertical stays tight; only the very small bottom inset keeps title text
    // from sticking to the next row when compact grids stack closely.
    private static readonly Thickness CompactCardPadding = new(4, 0, 4, 2);
    private static readonly Thickness NoBorderThickness = new(0);
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);

    private string? _currentImageCacheUrl;
    private double _defaultContentPanelSpacing;

    private readonly ThemeColorService? _themeColorService;
    private readonly NowPlayingHighlightService? _highlightService;

    /// <summary>Selector for the kind of metadata prefetch the viewport
    /// behavior asks the card to enqueue. Public so the behavior can name it
    /// without taking a dependency on the prefetcher service abstractions.</summary>
    public enum ViewportPrefetchKind
    {
        Album,
        Playlist,
    }

    // ── Construction / lifecycle ─────────────────────────────────────────────

    public ContentCard()
    {
        _themeColorService = Ioc.Default.GetService<ThemeColorService>();
        _highlightService = Ioc.Default.GetService<NowPlayingHighlightService>();
        InitializeComponent();
        CaptureDefaultDensityValues();
        ApplyDensityMode();
        // Cards are always interactive (click navigates or opens). Set the hand cursor
        // once on construction — the system shows it on hover automatically as long as
        // the cursor stays assigned, no per-event toggling needed.
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
        EnsureManualDragAttached();
        ActualThemeChanged += OnActualThemeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // EffectiveViewportChanged is wired by CardEffectiveViewportBehavior
        // (attached in XAML). The behavior subscribes on Loaded / unsubscribes
        // on Unloaded so handlers don't accumulate in the WinRT EventSource
        // table across ItemsRepeater container recycles. The first realize
        // still picks up the right image — OnLoaded calls LoadImage
        // unconditionally before the first viewport event fires.
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Subscribe to the shared NowPlayingHighlightService singleton instead of
        // registering directly with WeakReferenceMessenger. The service listens to
        // NowPlayingChangedMessage once at startup and broadcasts via a plain C# event
        // — avoiding ~310 per-card messenger Register calls during HomePage realization.
        if (_highlightService != null)
        {
            _highlightService.CurrentChanged += OnHighlightServiceChanged;
            // Apply the current snapshot immediately so newly-realized cards reflect playback state.
            var (contextUri, albumUri, playing) = _highlightService.Current;
            ApplyHighlight(contextUri, albumUri, playing);
        }
        ImageLoadingSuspension.Changed += OnImageLoadingSuspensionChanged;

        // Register the image-retry callback with the behavior attached on
        // SquareImage. The behavior owns the "single retry per URL" state; we
        // only own the actual reload entry point.
        if (SquareImage != null)
            CardImageRetryBehavior.AddRetryHandler(SquareImage, OnImageRetryRequested);

        if (!_hasEffectiveViewport || _isInsideEffectiveViewport)
            LoadImage(ImageUrl);
        SyncInitialPlaybackState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ResetInteractionState(updatePlayingState: false);
        StopPendingBeam();

        // Release the final CompositionRectangleClip on SquareImageContainer.
        // UpdateSquareImageClip swaps a fresh clip on every image load and
        // disposes the previous one — but the LAST one stays attached for
        // the card's lifetime. On unload, drop it so the GPU resource is
        // released promptly rather than waiting for full visual teardown.
        //
        // IMPORTANT (project memory `feedback_contentcard_unload_nulls_image`):
        // this cleanup stays on the control, NOT in a behavior. The teardown is
        // deliberate; recycle bugs must be fixed by re-triggering LoadImage on
        // re-realization, not by removing the cleanup.
        if (SquareImageContainer is not null)
        {
            try
            {
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(SquareImageContainer);
                var lingering = visual.Clip;
                visual.Clip = null;
                lingering?.Dispose();
            }
            catch
            {
                // Composition can already be torn down during window close.
            }
        }

        // Unsubscribe from the highlight service — strong event, explicit unsubscribe required.
        if (_highlightService != null)
            _highlightService.CurrentChanged -= OnHighlightServiceChanged;
        ImageLoadingSuspension.Changed -= OnImageLoadingSuspensionChanged;

        if (SquareImage != null)
            CardImageRetryBehavior.RemoveRetryHandler(SquareImage);

        // Clean up SizeChanged subscription to prevent memory leaks
        if (CircleImageContainer != null && _circleSizeHandlerAttached)
        {
            CircleImageContainer.SizeChanged -= OnCircleContainerSizeChanged;
            _circleSizeHandlerAttached = false;
        }

        // Viewport gate is reset by CardEffectiveViewportBehavior via
        // HandleViewportReset(); we just trust that next attach will resample.
        // CompositionImage.OnUnloaded handles its own pin release. No further
        // teardown needed here — the surface stays in the LRU until evicted.
    }

    private double EffectiveCardHorizontalPadding =>
        IsCompact ? CompactCardHorizontalPadding : DefaultCardHorizontalPadding;

    private void CaptureDefaultDensityValues()
    {
        if (ContentPanel != null)
            _defaultContentPanelSpacing = ContentPanel.Spacing;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (IsCategoryTile)
            ApplyCategoryTileBackground();
        else
            ApplyDensityMode();

        if (string.IsNullOrEmpty(PlaceholderColorHex))
            ApplyPlaceholderColor(null);

        UpdateBadgeForeground();
        UpdateSubtitleNavigationVisualState();
        UpdatePlayingState();
    }

    private void ApplyDensityMode()
    {
        if (CardRoot == null || ContentPanel == null)
            return;

        if (IsCompact)
        {
            CardRoot.Padding = CompactCardPadding;
            CardRoot.BorderThickness = NoBorderThickness;
            CardRoot.Background = TransparentBrush;
            CardRoot.BorderBrush = TransparentBrush;
            ContentPanel.Spacing = 5;
        }
        else
        {
            CardRoot.ClearValue(Grid.PaddingProperty);
            CardRoot.ClearValue(Grid.BorderThicknessProperty);
            CardRoot.ClearValue(Grid.BorderBrushProperty);
            CardRoot.ClearValue(Panel.BackgroundProperty);
            ContentPanel.Spacing = _defaultContentPanelSpacing;
        }
    }

    // ── Hooks called by attached behaviors ───────────────────────────────────

    /// <summary>Reset the viewport-gate mirror state. Called by
    /// <see cref="CardEffectiveViewportBehavior"/> on detach so the next attach
    /// re-samples cleanly.</summary>
    internal void HandleViewportReset()
    {
        _hasEffectiveViewport = false;
        _isInsideEffectiveViewport = true;
    }

    /// <summary>Called by <see cref="CardEffectiveViewportBehavior"/> when the
    /// card's viewport intersection changes. Mirrors the state for synchronous
    /// readers (DP callbacks, OnLoaded) and reloads the image if we just
    /// re-entered the viewport and the slot is empty.</summary>
    internal void HandleViewportIntersectionChanged(bool hasViewport, bool isInside)
    {
        _hasEffectiveViewport = hasViewport;
        _isInsideEffectiveViewport = isInside;

        if (!hasViewport || !isInside)
            return;

        // Cheap short-circuit: 99% of fires are scroll noise on already-loaded
        // cards. Only act when the image was nulled (by OnUnloaded) and we
        // have a URL to reload.
        if (string.IsNullOrEmpty(ImageUrl)) return;
        if (HasImage()) return;
        LoadImage(ImageUrl);
    }

    /// <summary>Called by <see cref="CardEffectiveViewportBehavior"/> when a
    /// realized card moves within prefetch range of the viewport. The behavior
    /// guarantees this fires at most once per (realization, kind) pair.</summary>
    internal void HandleViewportPrefetch(string navUri, ViewportPrefetchKind kind)
    {
        switch (kind)
        {
            case ViewportPrefetchKind.Album:
                Ioc.Default.GetService<IAlbumPrefetcher>()?.EnqueueAlbumPrefetch(navUri);
                break;
            case ViewportPrefetchKind.Playlist:
                Ioc.Default.GetService<IPlaylistMetadataPrefetcher>()?.EnqueuePlaylistPrefetch(navUri);
                break;
        }
    }

    /// <summary>Passive-mode pointer handlers — invoked by
    /// <see cref="CardPassivePointerBehavior"/> from <c>AddHandler(handledEventsToo:true)</c>
    /// so hover chrome still runs when a parent selection chrome marks pointer
    /// events as handled.</summary>
    internal void HandlePassivePointerEntered(object sender, PointerRoutedEventArgs e)
        => Card_PointerEntered(sender, e);

    internal void HandlePassivePointerExited(object sender, PointerRoutedEventArgs e)
        => Card_PointerExited(sender, e);

    internal void HandlePassivePointerPressed(object sender, PointerRoutedEventArgs e)
        => Card_PointerPressed(sender, e);

    internal void HandlePassivePointerReleased(object sender, PointerRoutedEventArgs e)
        => Card_PointerReleased(sender, e);

    /// <summary>Retry-load callback registered with
    /// <see cref="CardImageRetryBehavior"/>. Re-runs the regular image-load
    /// pipeline against the failed URL after deferring through the dispatcher.</summary>
    private void OnImageRetryRequested(string failedUrl)
    {
        if (!IsLoaded
            || ImageLoadingSuspension.IsSuspended
            || (_hasEffectiveViewport && !_isInsideEffectiveViewport)
            || HasImage()
            || !string.Equals(_currentImageCacheUrl, failedUrl, StringComparison.Ordinal))
        {
            return;
        }

        LoadImage(ImageUrl);
    }

    // ── Image suspension reload ──────────────────────────────────────────────

    private void OnImageLoadingSuspensionChanged(bool suspended)
    {
        if (suspended || !IsLoaded)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded && !IsImageLoadingSuspended)
                ReloadImageIfNeeded(ignoreViewportGate: true);
        });
    }

    public void ReleaseImage()
    {
        _currentImageCacheUrl = null;

        if (SquareImage != null)
        {
            SquareImage.ImageUrl = null;
            // Reset to invisible — fade-in animation snaps from current
            // opacity, so leaving this at 1 caused a 1 → 0 → 0.85 flash on
            // the next ImageOpened. The XAML default is 0 too.
            SquareImage.Opacity = 0;
        }

        if (CircleImage != null)
            CircleImage.ImageUrl = null;

        if (SquarePlaceholderIcon != null)
            SquarePlaceholderIcon.Visibility = Visibility.Visible;
        if (CirclePlaceholderIcon != null)
            CirclePlaceholderIcon.Visibility = Visibility.Visible;
    }

    public void ReloadImageIfNeeded(bool ignoreViewportGate = false)
    {
        if (!ignoreViewportGate && _hasEffectiveViewport && !_isInsideEffectiveViewport)
            return;
        if (HasImage())
            return;

        if (ignoreViewportGate)
        {
            _hasEffectiveViewport = false;
            _isInsideEffectiveViewport = true;
        }

        LoadImage(ImageUrl);
    }

    private bool HasImage()
        => IsCircularImage
            ? CircleImage?.IsImageLoaded == true
            : SquareImage?.IsImageLoaded == true;

    private bool HasRequestedImage()
        => IsCircularImage
            ? !string.IsNullOrEmpty(CircleImage?.ImageUrl)
            : !string.IsNullOrEmpty(SquareImage?.ImageUrl);

    private bool HasLoadedImageFor(string? url)
        => HasImage() && IsCurrentImageUrl(url);

    private bool IsCurrentImageUrl(string? url)
    {
        var resolved = ResolveCardImageUrl(url);
        return !string.IsNullOrEmpty(resolved)
               && string.Equals(_currentImageCacheUrl, resolved, StringComparison.Ordinal);
    }

    private void LoadImage(string? url)
    {
        // Guard: template may not be applied yet
        if (SquareImage == null) return;
        if (IsImageLoadingSuspended) return;

        var resolvedImageUrl = ResolveCardImageUrl(url);
        if (string.IsNullOrEmpty(resolvedImageUrl))
        {
            ReleaseImage();
            return;
        }

        if (string.Equals(_currentImageCacheUrl, resolvedImageUrl, StringComparison.Ordinal) && HasRequestedImage())
        {
            if (HasImage())
                HidePlaceholderForCurrentMode();
            else
                GetActiveImage()?.RefreshCurrentImage();
            return;
        }

        // Show placeholders — they sit on top of the image via z-order.
        SquarePlaceholderIcon.Visibility = Visibility.Visible;
        if (CirclePlaceholderIcon != null)
            CirclePlaceholderIcon.Visibility = Visibility.Visible;

        var httpsUrl = resolvedImageUrl;
        _currentImageCacheUrl = httpsUrl;

        // Notify the retry behavior of the new URL so its single-shot retry
        // counter resets — every fresh URL gets its own retry budget.
        if (SquareImage != null)
            CardImageRetryBehavior.NotifyLoadStarted(SquareImage, httpsUrl);

        // CompositionImage handles pin/unpin and surface lifetime internally.
        // Setting ImageUrl kicks off the LoadedImageSurface fetch via the
        // shared ImageCacheService.
        if (IsCircularImage)
        {
            EnsureCircleRealized();
            CircleImage!.DecodePixelSize = CardImageDecodeSize;
            CircleImage.ImageUrl = httpsUrl;
            // Clear the square slot so a virtualized recycle doesn't leave the
            // last item's surface holding a pin in this card's other layer.
            SquareImage.ImageUrl = null;
            CirclePlaceholderIcon!.Visibility = CircleImage.IsImageLoaded
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        else
        {
            SquareImage.DecodePixelSize = CardImageDecodeSize;
            SquareImage.ImageUrl = httpsUrl;
            if (CircleImage != null) CircleImage.ImageUrl = null;
            // If the surface is already loaded (cache hit), snap to resting
            // opacity. CompositionImage's ImageOpened event still fires in
            // that case, but we'd otherwise pop from 0 → 0.85 on the next
            // tick which looks like a delayed reveal on a cached hit.
            if (SquareImage.IsImageLoaded)
            {
                SquarePlaceholderIcon.Visibility = Visibility.Collapsed;
                SquareImage.Opacity = 0.85;
            }
        }
    }

    private CompositionImage? GetActiveImage()
        => IsCircularImage ? CircleImage : SquareImage;

    private void HidePlaceholderForCurrentMode()
    {
        if (IsCircularImage)
        {
            if (CirclePlaceholderIcon != null)
                CirclePlaceholderIcon.Visibility = Visibility.Collapsed;
            // The circle path defaults CircleImage.Opacity=0.85 in XAML, so it
            // rarely needs a bump here — but if a previous ReleaseImage zeroed
            // it, restore the resting opacity so the image is actually visible
            // when we collapse the placeholder on top of it. Skip when hover
            // has already raised it to 1.0.
            if (CircleImage != null && CircleImage.Opacity < 0.85)
                CircleImage.Opacity = 0.85;
        }
        else
        {
            if (SquarePlaceholderIcon != null)
                SquarePlaceholderIcon.Visibility = Visibility.Collapsed;
            // SquareImage starts at Opacity=0 in XAML and is normally raised by
            // the fresh-URL cache-hit branch or by SquareImage_ImageOpened. The
            // same-URL early-return path that calls into this helper bypasses
            // both, so restore the resting opacity here to close that gap.
            if (SquareImage != null && SquareImage.Opacity < 0.85)
                SquareImage.Opacity = 0.85;
        }
    }

    private static string? ResolveCardImageUrl(string? url)
    {
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(url);
        if (!string.IsNullOrEmpty(httpsUrl))
            return httpsUrl;

        // Home cards need a cheap preview. Full playlist/sidebar surfaces still
        // use PlaylistMosaicService for composed 2x2 mosaics.
        return SpotifyImageHelper.TryParseMosaicTileUrls(url, out var tileUrls) && tileUrls.Count > 0
            ? tileUrls[0]
            : null;
    }

    private void ApplyPlaceholderColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            SquareImageContainer.ClearValue(Panel.BackgroundProperty);
            return;
        }

        var color = ParseHexColor(hex);
        var brush = new SolidColorBrush(color) { Opacity = 0.3 };
        SquareImageContainer.Background = brush;

        // Only apply to circle placeholder if the circle subtree is realized;
        // EnsureCircleRealized re-applies this color when the subtree loads.
        if (CirclePlaceholder?.Fill is SolidColorBrush)
            CirclePlaceholder.Fill = new SolidColorBrush(color) { Opacity = 0.3 };
    }

    /// <summary>
    /// Flip the card between standard (art-on-top, title-below) and category-tile
    /// (full-bleed colored block, title bottom-left, optional rotated artwork
    /// bottom-right) presentation. Called by the IsCategoryTile DP changed
    /// callback. The overlay subtree is x:Load=false so non-podcast surfaces
    /// never pay the realization cost.
    /// </summary>
    private void ApplyCategoryTileMode()
    {
        if (CardRoot == null) return;

        if (IsCategoryTile)
        {
            // Force the overlay subtree into the visual tree (x:Load=False until
            // first access). The FindName side-effect of an x:Load element only
            // triggers when something asks for it — touching the field is enough.
            this.FindName(nameof(CategoryTileOverlay));

            ContentPanel.Visibility = Visibility.Collapsed;
            CardRoot.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
            CardRoot.Padding = new Microsoft.UI.Xaml.Thickness(12);

            if (CategoryTileOverlay != null)
                CategoryTileOverlay.Visibility = Visibility.Visible;
            if (CategoryTileTitle != null)
                CategoryTileTitle.Text = Title ?? string.Empty;

            ApplyCategoryTileBackground();
            ApplyCategoryTileArt(ImageUrl);
        }
        else
        {
            ContentPanel.Visibility = Visibility.Visible;
            ApplyDensityMode();
            if (CategoryTileOverlay != null)
                CategoryTileOverlay.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Paint <see cref="CardRoot"/> with the per-tile background colour when
    /// in category-tile mode. Falls back to the accent text-fill brush when
    /// PlaceholderColorHex is missing so the card is still visible.
    /// </summary>
    private void ApplyCategoryTileBackground()
    {
        if (CardRoot == null || !IsCategoryTile) return;

        var hex = PlaceholderColorHex;
        if (string.IsNullOrEmpty(hex))
        {
            CardRoot.Background = GetThemeBrush("AccentFillColorDefaultBrush") ?? _themeColorService?.AccentFill;
            return;
        }

        var color = ParseHexColor(hex);
        CardRoot.Background = new SolidColorBrush(color);
    }

    /// <summary>
    /// Push the artwork URL into the rotated bottom-right thumbnail when in
    /// category-tile mode. When the URL is null/empty the slot collapses so
    /// the tile reads as a pure colored block with just the title.
    /// </summary>
    private void ApplyCategoryTileArt(string? url)
    {
        if (CategoryTileArt == null) return;
        if (string.IsNullOrEmpty(url))
        {
            CategoryTileArt.ImageUrl = null;
            CategoryTileArt.Opacity = 0;
            return;
        }
        CategoryTileArt.ImageUrl = url;
        CategoryTileArt.Opacity = 1;
    }

    private void UpdateImageMode()
    {
        if (SquareImageContainer == null) return; // template not applied yet

        if (IsCircularImage)
        {
            EnsureCircleRealized();
            SquareImageContainer.Visibility = Visibility.Collapsed;
            CircleImageContainer!.Visibility = Visibility.Visible;
            // Size will be set dynamically based on card width via SizeChanged
            if (!_circleSizeHandlerAttached)
            {
                CircleImageContainer.SizeChanged += OnCircleContainerSizeChanged;
                _circleSizeHandlerAttached = true;
            }
            StabilizeImageSlotForMeasure(ActualWidth);
        }
        else
        {
            SquareImageContainer.Visibility = Visibility.Visible;
            // Only collapse the circle container if it was actually realized;
            // for square cards the x:Load-deferred subtree simply never exists.
            if (CircleImageContainer != null)
            {
                CircleImageContainer.Visibility = Visibility.Collapsed;
                if (_circleSizeHandlerAttached)
                {
                    CircleImageContainer.SizeChanged -= OnCircleContainerSizeChanged;
                    _circleSizeHandlerAttached = false;
                }
            }
            StabilizeImageSlotForMeasure(ActualWidth);
        }
    }

    private void OnCircleContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Make circle diameter = container width (minus a small margin)
        var size = ImageSize > 0
            ? ImageSize
            : Math.Max(MinimumImageSide, e.NewSize.Width - CircleImageInset);
        SetCircleImageSide(size);
    }

    private void SquareImageContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = ImageSize > 0 ? ImageSize : e.NewSize.Width;
        if (width <= 0) return;

        SetSquareImageSide(width);
    }

    /// <summary>
    /// Height multiplier for the current <see cref="AspectMode"/>:
    /// height = width × ratio. Square = 1, Tall (2:3 portrait) = 1.5,
    /// Wide / Backdrop (16:9 landscape) = 0.5625.
    /// </summary>
    private double AspectHeightRatio() => AspectMode switch
    {
        CardAspectMode.Tall                                  => 1.5,
        CardAspectMode.Wide or CardAspectMode.Backdrop       => 9.0 / 16.0,
        _                                                    => 1.0,
    };

    /// <summary>
    /// Sets the image-host box height from a measured width, honoring the current
    /// <see cref="AspectMode"/>. Name kept for back-compat — historically only
    /// "Square" existed so the parameter was a single side. With aspect modes the
    /// height is derived from the width per ratio.
    /// </summary>
    private void SetSquareImageSide(double width)
    {
        if (SquareImageContainer == null || width <= 0)
            return;

        var height = width * AspectHeightRatio();

        if (double.IsNaN(SquareImageContainer.Height) || Math.Abs(SquareImageContainer.Height - height) > 0.5)
            SquareImageContainer.Height = height;

        UpdateSquareImageClip(width, height);
    }

    private void SetCircleImageSide(double side)
    {
        if (side <= 0 || CirclePlaceholder == null || CircleImage == null)
            return;

        if (CircleImageContainer != null
            && (double.IsNaN(CircleImageContainer.Height) || Math.Abs(CircleImageContainer.Height - side) > 0.5))
            CircleImageContainer.Height = side;

        CirclePlaceholder.Width = side;
        CirclePlaceholder.Height = side;
        CircleImage.Width = side;
        CircleImage.Height = side;
    }

    private void UpdateSquareImageClip(double width, double height)
    {
        // Grid.CornerRadius only clips background paint in WinUI 3. SquareImageContainer
        // is a Grid (not a Border), so its CornerRadius does not clip child UIElements.
        // CompositionRectangleClip set on the outermost visual (GetElementVisual returns the
        // handoff visual for Border but the outermost for Grid/UserControl) clips the image.
        // CreateRectangleClip is used instead of CreateGeometricClip(RoundedRectangleGeometry)
        // — the latter bleeds at sub-pixel edges (see AnimatedHeroBackground.UpdateClip).
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(SquareImageContainer);
        var compositor = visual.Compositor;
        var clip = compositor.CreateRectangleClip();
        clip.Right = (float)width;
        clip.Bottom = (float)height;
        clip.TopLeftRadius = new System.Numerics.Vector2(4f);
        clip.TopRightRadius = new System.Numerics.Vector2(4f);
        clip.BottomLeftRadius = new System.Numerics.Vector2(4f);
        clip.BottomRightRadius = new System.Numerics.Vector2(4f);
        // Assign the new clip BEFORE disposing the old one — disposing
        // an attached clip mid-composition can flash. WinUI keeps a
        // ref-count on attached clips, so disposing after the swap drops
        // the redundant managed wrapper without affecting the visual.
        var oldClip = visual.Clip;
        visual.Clip = clip;
        try { oldClip?.Dispose(); }
        catch { /* idempotent — already disposed by composition shutdown */ }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        StabilizeImageSlotForMeasure(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }

    private void StabilizeImageSlotForMeasure(double availableWidth)
    {
        if (SquareImageContainer == null)
            return;

        var cardWidth = ResolveMeasureWidth(availableWidth);
        var contentWidth = Math.Max(MinimumImageSide, cardWidth - EffectiveCardHorizontalPadding);

        if (IsCircularImage)
        {
            EnsureCircleRealized();
            var side = ImageSize > 0
                ? ImageSize
                : Math.Max(MinimumImageSide, contentWidth - CircleImageInset);
            SetCircleImageSide(side);
        }
        else
        {
            var width = ImageSize > 0 ? ImageSize : contentWidth;
            var height = width * AspectHeightRatio();
            if (double.IsNaN(SquareImageContainer.Height) || Math.Abs(SquareImageContainer.Height - height) > 0.5)
                SquareImageContainer.Height = height;
        }
    }

    private double ResolveMeasureWidth(double availableWidth)
    {
        if (!double.IsNaN(availableWidth) && !double.IsInfinity(availableWidth) && availableWidth > 0)
            return availableWidth;

        if (ActualWidth > 0)
            return ActualWidth;

        return ImageSize > 0 ? ImageSize + EffectiveCardHorizontalPadding : DefaultCardWidth;
    }

    private void SquareImage_ImageOpened(object? sender, EventArgs e)
    {
        SquarePlaceholderIcon.Visibility = Visibility.Collapsed;

        // Fade in using XAML framework layer (not composition — avoids layer multiply bugs).
        // No explicit `from` — let the animation pick up SquareImage's current
        // opacity. End at resting opacity (0.85), not 1.0 — hover handlers
        // manage the 0.85 ↔ 1.0 toggle on their own.
        CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
            .Opacity(to: 0.85,
                     duration: TimeSpan.FromMilliseconds(250),
                     layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
            .Start(SquareImage);
    }

    private void SquareImage_ImageFailed(object? sender, EventArgs e)
    {
        // CompositionImage already invalidated the cache entry before raising
        // ImageFailed. Reset our local visible state and put the placeholder
        // back; CardImageRetryBehavior owns the "retry once" decision and will
        // call back into OnImageRetryRequested if appropriate.
        SquareImage.ImageUrl = null;
        SquarePlaceholderIcon.Visibility = Visibility.Visible;
    }

    private void CircleImage_ImageOpened(object? sender, EventArgs e)
    {
        if (CirclePlaceholderIcon != null)
            CirclePlaceholderIcon.Visibility = Visibility.Collapsed;
    }

    private void CircleImage_ImageFailed(object? sender, EventArgs e)
    {
        if (CircleImage != null)
            CircleImage.ImageUrl = null;
        if (CirclePlaceholderIcon != null)
            CirclePlaceholderIcon.Visibility = Visibility.Visible;
    }

    // ── Hover handling ───────────────────────────────────────────────────────

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        CardHover?.Invoke(this, EventArgs.Empty);

        // Realize the overlay for the current shape before reading the named elements —
        // after x:Load="False" on the overlays, the backing fields start null until FindName.
        EnsurePlayOverlayRealized();
        UpdatePlayingState();

        var overlayBtn = GetActiveOverlayButton();
        if (overlayBtn != null)
        {
            overlayBtn.Visibility = Visibility.Visible;
            // FrameworkLayer.Xaml animates UIElement.Opacity directly. The
            // default Composition layer raced the Visibility=Collapsed→Visible
            // transition on the FIRST hover because the composition visual
            // wasn't ready when the animation started — overlay stayed
            // invisible until the second hover when the visual already
            // existed. Xaml layer works regardless of composition state.
            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Opacity(from: 0, to: 1,
                         duration: TimeSpan.FromMilliseconds(150),
                         layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
                .Start(overlayBtn);
        }

        // SecondaryAction "Open album" button — fades in alongside the play
        // overlay so the discography-card affordance is discoverable only on
        // hover. Gated on SecondaryActionVisible (default false) so cards
        // that don't opt in pay no animation cost. Xaml layer — same reason
        // as the play overlay above.
        if (SecondaryActionVisible && SquareSecondaryActionButton != null)
        {
            SquareSecondaryActionButton.Visibility = Visibility.Visible;
            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Opacity(from: 0, to: 1,
                         duration: TimeSpan.FromMilliseconds(150),
                         layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
                .Start(SquareSecondaryActionButton);
        }

        // Scale up via composition with proper CenterPoint
        if (CardRoot != null)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardRoot);
            visual.CenterPoint = new System.Numerics.Vector3((float)CardRoot.ActualWidth / 2, (float)CardRoot.ActualHeight / 2, 0);

            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Scale(from: System.Numerics.Vector3.One, to: new System.Numerics.Vector3(1.03f), duration: TimeSpan.FromMilliseconds(200))
                .Start(CardRoot);
        }

        // Image opacity: muted at rest (0.85), full on hover. Cheap snap; the
        // 1.03 scale animation above already carries the motion of the pop.
        if (SquareImage != null) SquareImage.Opacity = 1.0;
        if (CircleImage != null) CircleImage.Opacity = 1.0;
    }

    private async void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        var overlayBtn = GetActiveOverlayButton();
        if (overlayBtn != null && !_isPlaybackPending && !IsContextPaused)
        {
            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Opacity(to: 0,
                         duration: TimeSpan.FromMilliseconds(100),
                         layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
                .Start(overlayBtn);

            // Collapse after fade-out to reset for next hover
            await System.Threading.Tasks.Task.Delay(120);
            if (!_isPointerOver && !_isPlaybackPending && !IsContextPaused)
                overlayBtn.Visibility = Visibility.Collapsed;
        }

        // Mirror the secondary "Open album" button fade-out. Same 100 ms
        // fade + 120 ms collapse delay as the play overlay above so both
        // affordances retreat together.
        if (SecondaryActionVisible && SquareSecondaryActionButton != null)
        {
            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Opacity(to: 0,
                         duration: TimeSpan.FromMilliseconds(100),
                         layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
                .Start(SquareSecondaryActionButton);

            await System.Threading.Tasks.Task.Delay(120);
            if (!_isPointerOver)
                SquareSecondaryActionButton.Visibility = Visibility.Collapsed;
        }

        UpdatePlayingState();

        if (CardRoot != null)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardRoot);
            visual.CenterPoint = new System.Numerics.Vector3((float)CardRoot.ActualWidth / 2, (float)CardRoot.ActualHeight / 2, 0);

            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Scale(from: new System.Numerics.Vector3(1.03f), to: System.Numerics.Vector3.One, duration: TimeSpan.FromMilliseconds(200))
                .Start(CardRoot);
        }

        // Restore the muted resting state for the image.
        if (SquareImage != null) SquareImage.Opacity = 0.85;
        if (CircleImage != null) CircleImage.Opacity = 0.85;
    }

    // ── Press animation ──────────────────────────────────────────────────────

    private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (CardRoot == null) return;
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardRoot);
        visual.CenterPoint = new System.Numerics.Vector3((float)CardRoot.ActualWidth / 2, (float)CardRoot.ActualHeight / 2, 0);

        CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
            .Scale(to: new System.Numerics.Vector3(0.96f), duration: TimeSpan.FromMilliseconds(100))
            .Start(CardRoot);
    }

    private void Card_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (CardRoot == null) return;
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardRoot);
        visual.CenterPoint = new System.Numerics.Vector3((float)CardRoot.ActualWidth / 2, (float)CardRoot.ActualHeight / 2, 0);

        // Mouse releases return to hover scale; touch/pen taps have no hover state.
        var targetScale = _isPointerOver
            ? new System.Numerics.Vector3(1.03f)
            : System.Numerics.Vector3.One;
        CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
            .Scale(to: targetScale, duration: TimeSpan.FromMilliseconds(150))
            .Start(CardRoot);
    }

    // ── Shared helpers (used by the partials) ────────────────────────────────

    private void EnsurePlayOverlayRealized()
    {
        if (IsExternal)
        {
            // External cards never need play / now-playing chrome — only the
            // "open in browser" overlay. Square is the only supported shape today.
            if (SquareExternalButton == null)
                this.FindName("SquareExternalButton");
            return;
        }

        if (!ShowPlaybackOverlay)
            return;

        if (IsCircularImage)
            EnsureCircleRealized();
        else if (SquarePlayButton == null)
            this.FindName("SquarePlayButton");
    }

    /// <summary>
    /// Returns the button used for the hover overlay in the card's current mode:
    /// the external (open-in-browser) button when <see cref="IsExternal"/> is true,
    /// otherwise the play button matching the image shape. May return null if the
    /// overlay subtree hasn't been realized yet (call <see cref="EnsurePlayOverlayRealized"/>
    /// first if you intend to act on the result).
    /// </summary>
    private Button? GetActiveOverlayButton()
        => IsExternal
            ? SquareExternalButton
            : !ShowPlaybackOverlay
                ? null
            : (IsCircularImage ? CirclePlayButton : SquarePlayButton);

    private void ResetInteractionState(bool updatePlayingState = true)
    {
        _isPointerOver = false;

        if (CardRoot != null)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardRoot);
            visual.Scale = System.Numerics.Vector3.One;
        }

        if (SquareImage != null) SquareImage.Opacity = 0.85;
        if (CircleImage != null) CircleImage.Opacity = 0.85;

        if (!_isPlaybackPending)
            StopPendingBeam();

        if (!_isPlaybackPending)
        {
            if (SquarePlayButton != null)
            {
                SquarePlayButton.Opacity = 0;
                SquarePlayButton.Visibility = Visibility.Collapsed;
            }
            if (CirclePlayButton != null)
            {
                CirclePlayButton.Opacity = 0;
                CirclePlayButton.Visibility = Visibility.Collapsed;
            }
            if (SquareExternalButton != null)
            {
                SquareExternalButton.Opacity = 0;
                SquareExternalButton.Visibility = Visibility.Collapsed;
            }
        }

        if (updatePlayingState)
            UpdatePlayingState();
    }

    /// <summary>
    /// Realizes the <c>CircleImageContainer</c> subtree on demand. With <c>x:Load="False"</c>
    /// on the grid, all circle-mode named elements (<c>CirclePlaceholder</c>, <c>CirclePlaceholderIcon</c>,
    /// <c>CircleImage</c>, <c>CircleImageBrush</c>, <c>CirclePlayButton</c>, <c>CirclePlayingIndicator</c>, etc.)
    /// start null until <see cref="FrameworkElement.FindName"/> triggers the subtree load.
    /// Idempotent — returns early if the container is already realized.
    /// </summary>
    private void EnsureCircleRealized()
    {
        if (CircleImageContainer != null) return;
        this.FindName("CircleImageContainer");

        // Re-apply DP-sourced state that the DP callbacks skipped while the circle
        // subtree was null. For the common case of a square card (which never
        // realizes this subtree), none of this ever runs.
        if (CirclePlaceholderIcon != null)
        {
            var glyph = GetValue(PlaceholderGlyphProperty) as string ?? "\uE8D6";
            CirclePlaceholderIcon.Glyph = glyph;
        }
        if (CirclePlaceholder != null && GetValue(PlaceholderColorHexProperty) is string hex && !string.IsNullOrEmpty(hex))
        {
            var color = ParseHexColor(hex);
            CirclePlaceholder.Fill = new SolidColorBrush(color) { Opacity = 0.3 };
        }
    }

    private static Windows.UI.Color ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        return hex.Length switch
        {
            6 => Windows.UI.Color.FromArgb(255,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16)),
            8 => Windows.UI.Color.FromArgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16),
                Convert.ToByte(hex[6..8], 16)),
            _ => Windows.UI.Color.FromArgb(255, 128, 128, 128)
        };
    }
}
