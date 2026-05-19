using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Animations;
using CommunityToolkit.WinUI.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.Services.DragDrop.Payloads;
using Wavee.UI.WinUI.Controls.AlbumDetailPanel;
using Wavee.UI.WinUI.Controls.Cards;
using Wavee.UI.WinUI.Controls.HeroHeader;
using Wavee.UI.WinUI.Controls.InPageFilter;
using Wavee.UI.WinUI.DragDrop;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Controls.Track;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;
using Wavee.UI.WinUI.ViewModels.Artist;
using Windows.UI;
using Wavee.Core.Http;
using ColorAnimation = Microsoft.UI.Xaml.Media.Animation.ColorAnimation;

namespace Wavee.UI.WinUI.Views;

/// <summary>
/// Canonical V4A "Magazine Hero" artist detail page, routed through
/// <see cref="NavigationHelpers.OpenArtist"/>.
/// Reuses <see cref="ArtistViewModel"/> and renders a two-column magazine hero,
/// paired Top Tracks / Popular
/// Releases band, on-tour rhythm-break banner, tour+merch + about+stats
/// two-column sections, gallery, related artists, plus the on-device Phi Silica
/// fallback excerpt for artists without a Spotify biography.
///
/// The page leans on Composition for the polish that makes it feel native:
///   * scroll-driven parallax on hero photo + copy column + page tint
///   * one-shot Composition reveal that cascades sections into view
///   * one-shot pulse on the Large Play button on first realization
///   * the existing <see cref="ShyHeaderController"/> / <see cref="TransitionHelper"/>
///     pattern to morph the hero into a pinned compact card on scroll
/// </summary>
public sealed partial class ArtistPage : UserControl, ITabBarItemContent, INavigationCacheMemoryParticipant, IPageHostAware, IDisposable, IRedirectsCtrlFToOmnibar
{
    private const int ReleasePanelResizeDebounceMs = 75;
    private const int ReleasePanelExitMs = 110;
    private const double ReleasePanelExitOffset = -8;

    private readonly ILogger? _logger;
    private readonly IColorService _colorService;
    private ShyHeaderController? _shyHeader;
    private LinearGradientBrush? _pageTintBrush;
    private GradientStop? _pageTintHeroStop;
    private GradientStop? _pageTintFadeStop;
    private bool _parallaxAttached;
    private bool _heroSizeHandlersAttached;
    private bool _heroDragAttached;
    private bool _isDisposed;
    private bool _isNavigatingAway;
    private bool _heroPulseFired;
    private bool _trimmedForNavigationCache;
    private string? _lastRestoredArtistId;
    /// <summary>True once <see cref="ShimmerGate.RunCrossfadeAsync"/> has been
    /// triggered for the current load. Guards the crossfade's continuation
    /// against re-entrant navigation that fires <see cref="ShimmerGate.Reset"/>
    /// while a prior crossfade is still in its 250 ms delay window â€” without
    /// this, the stale crossfade's `Visibility=Collapsed` finalisation could
    /// hide the freshly-realised shimmer skeleton mid-load.</summary>
    private bool _showingContent;

    /// <summary>Shared by the unified body shimmer skeleton (x:Load) and the
    /// real BodyContent (Opacity crossfade). Owned by the page; legacy
    /// ArtistPage uses the same instance pattern.</summary>
    public Wavee.UI.WinUI.Helpers.ShimmerLoadGate ShimmerGate { get; } = new();

    private bool _crossfadeScheduled;
    private int _navigationRevision;
    private int _heroArrangeRefreshRevision;
    private AlbumDetailPanel? _activeDetailPanel;
    private EventHandler? _closeRequestedHandler;
    private ItemsRepeater? _splitRepeaterAfter;
    private StackPanel? _splitParent;
    private ItemsRepeater? _originalRepeater;
    private object? _originalItemsSource;
    private LazyReleaseItem? _expandedItem;
    private int _expandedItemIndex = -1;
    private CancellationTokenSource? _resizeDebounceCts;
    private int _releaseExpansionRevision;

    public ArtistViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public bool ReuseForParameterNavigation => false;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ArtistPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ArtistViewModel>();
        _logger = Ioc.Default.GetService<ILogger<ArtistPage>>();
        _colorService = Ioc.Default.GetRequiredService<IColorService>();
        InitializeComponent();

        ViewModel.ContentChanged += ViewModel_ContentChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        // The page used to switch on a handful of parent VM properties (Artist,
        // BioSummaryText, HasPopularReleases, HasGallery). After the
        // ArtistViewModel decomposition those properties live on the children;
        // hook each child's PropertyChanged into the same dispatcher so the
        // existing per-property logic continues to fire.
        ViewModel.Header.PropertyChanged += ViewModel_ChildPropertyChanged;
        ViewModel.Bio.PropertyChanged += ViewModel_ChildPropertyChanged;
        ViewModel.Discography.PropertyChanged += ViewModel_ChildPropertyChanged;
        ViewModel.Extras.PropertyChanged += ViewModel_ChildPropertyChanged;
        ApplyPageTint();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnPageSizeChanged;
        ActualThemeChanged += (_, _) => ApplyPageTint();
        UpdateResponsivePageChrome();
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Navigation
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public async void OnEntered(object? parameter, PageHostNavigationMode mode)
    {
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.artist.onEntered");
        _isNavigatingAway = false;
        // Restore from the trimmed (hibernated) state before we re-Initialize
        // the VM â€” matches AlbumPage’s ordering so bindings are alive again
        // by the time the new artist’s data starts flowing.
        RestoreFromNavigationCache();

        var nav = parameter as ContentNavigationParameter;
        var uri = nav?.Uri ?? (parameter as string);

        if (!string.IsNullOrEmpty(uri))
        {
            var queryIdx = uri.IndexOf('?', StringComparison.Ordinal);
            if (queryIdx >= 0)
            {
                ViewModel.IsDebugMode = uri[(queryIdx + 1)..].Contains("debug", StringComparison.OrdinalIgnoreCase);
                uri = uri[..queryIdx];
            }
            else
            {
                ViewModel.IsDebugMode = false;
            }
        }

        // Back/Forward to the artist we’re already showing (e.g. Back from
        // “All singles” or any other child page on the same artist):
        // preserve the user’s scroll position and shy-header state. The
        // ScrollTo(0) + shy-header reset block below is what was clobbering
        // it. Hibernate deliberately preserves ViewModel.ArtistId, so this
        // comparison is reliable even on trimmed cache-hits. When we skip
        // the scroll reset we also have no reason to run the shy-header
        // suppression dance (the whole reason that exists is to mask the
        // ScrollTo(0) below).
        var samePageCacheHit = (mode == PageHostNavigationMode.Back || mode == PageHostNavigationMode.Forward)
                               && !string.IsNullOrEmpty(uri)
                               && string.Equals(uri, ViewModel.ArtistId, StringComparison.Ordinal);

        if (!samePageCacheHit)
        {
            // Suppress the shy-header evaluator through the entire navigation
            // reset. Without this, ScrollView.ViewChanged events queued by the
            // ScrollTo(0) below can fire while _isPinned still reads true from
            // the previous artist’s deep scroll â€” the controller then calls
            // _transition.ReverseAsync() to morph the pill back to the hero
            // overlay, the matched-IDs interpolate scale 1â†’hero scale over the
            // 300 ms reverse duration, and the user sees the shy pill visibly
            // inflate to fill the hero band before snapping out. We unsuppress
            // on a dispatcher tick after Stop+Reset have landed.
            if (_shyHeader is not null) _shyHeader.Suppressed = true;
            // Belt-and-braces: even if the TransitionHelper’s internal Reset is
            // laggy, the pill is forced invisible from frame one of the new nav.
            if (ShyHeaderCard is not null) ShyHeaderCard.Visibility = Visibility.Collapsed;
        }

        var navigationRevision = ++_navigationRevision;

        // Cache-hit nav (Back/Forward): content already realised — skip
        // the shimmer reset to avoid flashing skeleton over good pixels.
        if (mode != PageHostNavigationMode.Back && mode != PageHostNavigationMode.Forward)
        {
            _showingContent = false;
            _crossfadeScheduled = false;
            ShimmerGate.Reset(() => ShimmerContainer, () => BodyContent);
        }

        // Yield once between the shimmer flip and the heavy VM-init /
        // Bindings.Update / Attach* work below. Without this the framework
        // runs the whole method synchronously and DWM never gets a paint
        // frame to show the just-armed shimmer OR the page-entrance fade’s
        // first few percent — the user sees the page pop in fully rendered
        // after the UI thread finally returns. Task.Yield posts the
        // continuation to the dispatcher, which forces a render frame in
        // between. The navigation-revision guard makes a follow-on nav
        // (user clicks another artist before this one’s bindings have
        // landed) supersede this in-flight invocation cleanly — the newer
        // OnNavigatedTo runs its own sync sweep and the older one bails.
        await Task.Yield();
        if (_isDisposed || _isNavigatingAway || navigationRevision != _navigationRevision)
            return;

        // Compare against the INCOMING uri, not ViewModel.ArtistId â€” at this
        // point ArtistId still references the previous artist (Initialize
        // hasn’t run yet). This is what was misfiring in
        // RestoreFromNavigationCache and leaving stale bindings on cross-
        // artist navs.
        var artistChanged = !string.Equals(_lastRestoredArtistId, uri, StringComparison.Ordinal);

        if (!string.IsNullOrEmpty(uri))
            ViewModel.Initialize(uri);

        // Re-evaluate every compiled x:Bind on the page so the freshly-reset
        // VM state is picked up. Same-artist returns skip this for perf â€”
        // nothing in the binding graph actually changed in that case.
        if (artistChanged)
        {
            Bindings?.Update();
            _lastRestoredArtistId = uri;
        }

        ApplyPopularReleasesColumnWidth();
        UpdateResponsivePageChrome();
        AttachHeroSizeHandlers();
        AttachScrollParallax();

        if (!samePageCacheHit)
        {
            ForceHeroVisualsVisible();
            ScheduleHeroArrangeRefresh();

            try
            {
                PageScrollView?.ScrollTo(
                    0, 0,
                    new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "ArtistPage scroll-to-top on navigation failed.");
            }

            _shyHeader?.Stop();
            _shyHeader?.Reset();
            ForceHeroVisualsVisible();
            ScheduleHeroArrangeRefresh();

            // Drain the dispatcher AND wait an additional settle window before
            // re-enabling the shy-header evaluator. One TryEnqueue tick wasn’t
            // enough â€” the ScrollViewer continues firing ViewChanged events
            // through several frames after ScrollTo(0), and the user-visible
            // symptom was the shy pill “inflating” into the hero band when the
            // user scrolled up immediately after an artistâ†’artist nav. Chain
            // two dispatcher ticks + a short Task.Delay so the queued events
            // (and any composition reflow) have actually finished before the
            // evaluator wakes up.
            _ = ReleaseShyHeaderSuppressionAsync(navigationRevision);
        }

        ProbeWarmCacheReveal(navigationRevision, uri);
    }

    private async Task ReleaseShyHeaderSuppressionAsync(int navigationRevision)
    {
        // First yield to let the immediate ScrollTo's first ViewChanged fire.
        await Task.Yield();
        if (_isDisposed || _isNavigatingAway || navigationRevision != _navigationRevision)
            return;
        // Then sleep long enough for the ScrollViewer to settle on the new
        // artist's content. 250ms covers the post-nav layout + composition
        // reflow window comfortably; the user doesn't perceive a delay
        // because the shy pill is invisible during the suppression anyway.
        await Task.Delay(250).ConfigureAwait(true);
        if (_isDisposed || _isNavigatingAway || navigationRevision != _navigationRevision)
            return;
        _shyHeader?.Reset();
        if (_shyHeader is not null) _shyHeader.Suppressed = false;
    }

    public void OnLeaving()
    {
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.artist.onLeaving");
        _isNavigatingAway = true;
        CancelResizeDebounce();
        CollapseExpandedAlbumCore();
        // Trim work is deferred ~1 s by TabBarItem's central scheduler — calling
        // it here would move the cost from pageHostNavigating into onLeaving.
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Loaded / Unloaded
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // Synchronous: just the comp-Opacity zero-snap so there's no first-paint
        // flash of the fully-opaque body between Loaded and the first composition
        // frame. All other setup is deferred (see below) — composition-binding
        // work (ShyHeader, parallax, section-reveal expressions, hero pulse)
        // needs the visual tree fully realised, which under PageHost lands on
        // the next dispatcher tick rather than synchronously inside Loaded.
        if (BodyContent is not null) BodyContent.Opacity = 0;

        // Defer the heavy composition / layout wire-up one dispatcher tick.
        // Same pattern as AlbumPage.AttachParallax — running these inside
        // Loaded while the page is being added to PageHost's internal panel
        // causes a visible first-paint stall and can leave composition
        // expressions bound against an empty ExpressionAnimationSources.
        DispatcherQueue?.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            () =>
            {
                System.Diagnostics.Debug.WriteLine("[artist.loaded] deferred OnLoaded body running");

                _shyHeader = new ShyHeaderController(
                    PageScrollView, HeroGrid, HeroOverlayPanel, ShyHeaderCard,
                    (TransitionHelper)Resources["ArtistShyHeaderTransition"],
                    ShyHeaderFade.ForHeroHeader(HeroGrid),
                    ShyHeaderPinOffset.Below(HeroGrid, 120),
                    canEvaluate: () => !_isNavigatingAway,
                    logger: _logger);
                _shyHeader.Attach();
                _shyHeader.Reset();
                ForceHeroVisualsVisible();

                AttachScrollParallax();
                AttachSectionRevealAnimations();
                FireHeroPlayPulseOnce();
                RebuildBioRuns();
                AttachHeroSizeHandlers();
                AttachHeroDragSource();
                ApplyResponsiveHeroName();
                UpdateResponsivePageChrome();
                ScheduleHeroArrangeRefresh();
                ApplyPopularReleasesColumnWidth();
                TryShowContentNow();
                // Seed the gallery marquee from whatever the VM already has —
                // covers nav-cache restore where HasGallery was raised before
                // this page was realized, so the ItemsSource assignment in
                // ViewModel_PropertyChanged never saw a live GalleryMarquee
                // element.
                RebuildGallerySlides();
            });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachScrollParallax();
        CancelResizeDebounce();
        CollapseExpandedAlbumCore();
        DetachHeroSizeHandlers();
    }

    private void AttachHeroSizeHandlers()
    {
        if (_heroSizeHandlersAttached) return;
        HeroPhotoBorder.SizeChanged += OnHeroPhotoBorderSizeChanged;
        _heroSizeHandlersAttached = true;
    }

    /// <summary>
    /// Makes the hero region a drag source that emits an
    /// <see cref="ArtistDragPayload"/>. The user can drop the artist onto a
    /// playlist row (→ append top tracks), a folder, or the sidebar Pinned
    /// drop zone. <see cref="ManualDragAttachment"/> uses a 6 px movement
    /// threshold so taps on inner buttons (Play / Follow / Spotlight card)
    /// still register as clicks; only an actual drag promotes the gesture.
    /// </summary>
    private void AttachHeroDragSource()
    {
        if (_heroDragAttached) return;
        _heroDragAttached = true;
        ManualDragAttachment.AttachWithPackageWriter(HeroPhotoBorder, BuildHeroDragPayload);
    }

    private IDragPayload? BuildHeroDragPayload()
    {
        var id = ViewModel.ArtistId;
        if (string.IsNullOrWhiteSpace(id)) return null;
        var uri = id.StartsWith("spotify:artist:", StringComparison.Ordinal) ? id : $"spotify:artist:{id}";
        var name = ViewModel.Header.ArtistName ?? string.Empty;
        return new ArtistDragPayload(uri, name);
    }

    private void DetachHeroSizeHandlers()
    {
        if (!_heroSizeHandlersAttached) return;
        HeroPhotoBorder.SizeChanged -= OnHeroPhotoBorderSizeChanged;
        _heroSizeHandlersAttached = false;
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsivePageChrome(e.NewSize.Width);

        if (_activeDetailPanel is null || _expandedItem is null)
            return;

        CancelResizeDebounce();
        _resizeDebounceCts = new CancellationTokenSource();
        _ = RecomputeExpandedPanelAsync(_resizeDebounceCts.Token);
    }

    private async Task RecomputeExpandedPanelAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(ReleasePanelResizeDebounceMs, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_activeDetailPanel is null
            || _expandedItem is null
            || _originalRepeater is null
            || _splitParent is null
            || _originalItemsSource is null)
        {
            return;
        }

        ApplySplitLayout();
    }

    private void CancelResizeDebounce()
    {
        var cts = Interlocked.Exchange(ref _resizeDebounceCts, null);
        if (cts is null) return;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    private void OnHeroPhotoBorderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveHeroName();
    }

    private void ApplyResponsiveHeroName()
    {
        if (HeroNameText is null) return;
        var w = HeroPhotoBorder.ActualWidth;
        if (w <= 0) return;

        // Length-based heuristic. The old implementation ran a 24-iteration
        // measure loop (FontSize → InvalidateMeasure → Measure on every
        // iteration), which was measured at 50-100 ms per Loaded — the single
        // largest cost on the entire ArtistPage entry. The heuristic below
        // sizes from the hero width (~7%) and clamps tighter as the name
        // length grows, with no Measure passes.
        var name = HeroNameText.Text ?? string.Empty;
        var len = name.Length;

        var baseSize = Math.Clamp(w * 0.07, 56.0, 200.0);

        // Empirically-tuned thresholds, calibrated against the bracket the
        // previous binary-search produced on a 1920×1080 viewport for typical
        // artist names. Buckets keep visual scale consistent across the
        // common length ranges; wrap-to-2-lines kicks in only for the genuinely
        // long names that wouldn't fit even at the smallest single-line size.
        double size = len switch
        {
            <= 8  => baseSize,
            <= 16 => baseSize * 0.85,
            <= 24 => baseSize * 0.70,
            <= 36 => baseSize * 0.55,
            _     => baseSize * 0.45,
        };
        size = Math.Max(32.0, size);

        var wrap = len > 36;
        HeroNameText.TextWrapping = wrap ? TextWrapping.WrapWholeWords : TextWrapping.NoWrap;
        HeroNameText.TextTrimming = TextTrimming.None;
        HeroNameText.MaxLines = wrap ? 2 : 1;
        HeroNameText.FontSize = size;
        HeroNameText.LineHeight = size * (wrap ? 0.88 : 0.84);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // ViewModel change tracking
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void ViewModel_ContentChanged(object? sender, TabItemParameter e)
        => ContentChanged?.Invoke(this, e);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ArtistViewModel.IsLoading):
                TryShowContentNow();
                break;
        }
    }

    private void ViewModel_ChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ViewModel.Header))
        {
            switch (e.PropertyName)
            {
                case nameof(ArtistHeaderViewModel.Artist):
                    ApplyPageTint();
                    FireHeroPlayPulseOnce();
                    ApplyResponsiveHeroName();
                    ScheduleHeroArrangeRefresh();
                    RebuildBioRuns();
                    ApplyPopularReleasesColumnWidth();
                    TryShowContentNow();
                    break;
            }
            return;
        }

        if (ReferenceEquals(sender, ViewModel.Bio))
        {
            if (e.PropertyName == nameof(ArtistBioViewModel.BioSummaryText))
                RebuildBioRuns();
            return;
        }

        if (ReferenceEquals(sender, ViewModel.Discography))
        {
            if (e.PropertyName == nameof(ArtistDiscographyViewModel.HasPopularReleases))
            {
                ApplyPopularReleasesColumnWidth();
                TryShowContentNow();
            }
            return;
        }

        if (ReferenceEquals(sender, ViewModel.Extras))
        {
            if (e.PropertyName == nameof(ArtistExtrasViewModel.HasGallery))
                RebuildGallerySlides();
        }
    }

    /// <summary>
    /// Feed <c>ViewModel.GalleryPhotos</c> into the compact marquee strip. The
    /// strip does its own HTTPS normalisation + duplication for the seamless
    /// loop; we just hand it the raw URL list (filtered down to non-empty).
    /// </summary>
    private void RebuildGallerySlides()
    {
        if (GalleryMarquee is null) return;
        var photos = ViewModel.Extras.GalleryPhotos;
        if (photos is null || photos.Count == 0)
        {
            GalleryMarquee.ItemsSource = null;
            return;
        }
        var urls = new System.Collections.Generic.List<string>(photos.Count);
        foreach (var photo in photos)
        {
            if (!string.IsNullOrEmpty(photo)) urls.Add(photo);
        }
        GalleryMarquee.ItemsSource = urls;
    }

    private async void ProbeWarmCacheReveal(int navigationRevision, string? expectedArtistId)
    {
        await Task.Yield();

        if (_isNavigatingAway || navigationRevision != _navigationRevision)
            return;

        if (!string.IsNullOrEmpty(expectedArtistId)
            && !string.Equals(ViewModel.ArtistId, expectedArtistId, StringComparison.Ordinal))
            return;

        TryShowContentNow();
    }

    private void TryShowContentNow()
    {
        if (_showingContent
            || _crossfadeScheduled
            || ViewModel.IsLoading
            || string.IsNullOrEmpty(ViewModel.Header.ArtistName)
            || BodyContent is null)
            return;

        ScheduleCrossfade();
    }

    private async void ScheduleCrossfade()
    {
        _crossfadeScheduled = true;
        await Task.Yield();
        await Task.Delay(16);

        if (_isNavigatingAway || _showingContent || ViewModel.IsLoading)
        {
            _crossfadeScheduled = false;
            return;
        }

        _showingContent = true;
        _crossfadeScheduled = false;
        BodyContent.Opacity = 0;

        _ = ShimmerGate.RunCrossfadeAsync(
            ShimmerContainer,
            BodyContent,
            CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml,
            () => _showingContent);
    }

    /// <summary>Collapse the right column to 0 when there are no popular
    /// releases AND we're not loading. During the initial load we keep the
    /// column open at 1* width so its shimmer can render alongside the
    /// top-tracks grid â€” collapsing it during load would leave a one-sided
    /// skeleton that doesn't predict the final layout.</summary>
    private void ApplyPopularReleasesColumnWidth()
    {
        if (PopularReleasesColumn is null) return;
        bool keepOpen = ViewModel.Discography.HasPopularReleases || ViewModel.IsLoading;
        var compact = GetResponsiveWidth() < 900;

        if (PopularReleasesBorder is not null)
        {
            Grid.SetRow(PopularReleasesBorder, compact ? 1 : 0);
            Grid.SetColumn(PopularReleasesBorder, compact ? 0 : 1);
        }

        if (TopTracksBandGrid is not null)
            TopTracksBandGrid.ColumnSpacing = compact ? 0 : 24;

        PopularReleasesColumn.Width = keepOpen && !compact
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
    }

    private double GetResponsiveWidth()
    {
        if (PageScrollView?.ActualWidth > 0) return PageScrollView.ActualWidth;
        if (ActualWidth > 0) return ActualWidth;
        if (ContentRoot?.ActualWidth > 0) return ContentRoot.ActualWidth;
        return 1200;
    }

    private void UpdateResponsivePageChrome(double? widthOverride = null)
    {
        var width = widthOverride is > 0 ? widthOverride.Value : GetResponsiveWidth();

        if (ContentRoot is not null && width > 0)
            ContentRoot.Width = width;

        var horizontal = width switch
        {
            < 360 => 8,
            < 520 => 12,
            < 720 => 16,
            < 1024 => 24,
            _ => 32
        };

        var bodyTop = width < 520 ? 28 : 40;
        var bodyBottom = width < 520 ? 32 : 40;
        if (BodyContent is not null)
        {
            BodyContent.Padding = new Thickness(horizontal, bodyTop, horizontal, bodyBottom);
            BodyContent.Spacing = width < 520 ? 28 : 40;
        }

        if (HeroOverlayContentGrid is not null)
        {
            var heroHorizontal = width < 520 ? 20 : 32;
            HeroOverlayContentGrid.Padding = new Thickness(heroHorizontal, 34, heroHorizontal, 30);
            HeroOverlayContentGrid.ColumnSpacing = width < 900 ? 0 : 24;
        }

        ApplyPopularReleasesColumnWidth();
    }

    /// <summary>x:Bind helper for the popular-releases Border visibility â€”
    /// visible while loading (so the shimmer renders) and once loaded only
    /// when the artist actually has popular releases.</summary>
    public static Visibility ShowPopularColumnArea(bool hasPopularReleases, bool isLoading)
        => (hasPopularReleases || isLoading) ? Visibility.Visible : Visibility.Collapsed;

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // About-this-artist bio â€” heuristic em-styling
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Rebuilds <see cref="BioExcerptTextBlock"/>'s inline runs, emphasising
    /// the artist's own name + the names of the top 5 top tracks against the
    /// bio text. Spotify ships biographies as plain text, so we infer "what
    /// would be styled" by matching known proper nouns we already have on
    /// the VM. The accent foreground + SemiBold weight is applied to matches;
    /// the rest of the text renders plain.
    /// </summary>
    private void RebuildBioRuns()
    {
        if (BioExcerptTextBlock is null) return;
        BioExcerptTextBlock.Inlines.Clear();

        // The About-excerpt card is gated on the on-device AI summary
        // (HasAboutExcerpt => HasBioSummary), so feed that here exclusively.
        // The full biography lives in the dedicated Biography card below.
        var bio = FlattenBioForExcerpt(ViewModel.Bio.BioSummaryText);
        if (string.IsNullOrEmpty(bio)) return;

        var emphasize = new System.Collections.Generic.List<string>(8);
        if (!string.IsNullOrWhiteSpace(ViewModel.Header.ArtistName))
            emphasize.Add(ViewModel.Header.ArtistName!);

        foreach (var track in ViewModel.TopTracks.Tracks)
        {
            if (track is { IsLoaded: true, Data: Wavee.UI.Contracts.ITrackItem item }
                && !string.IsNullOrWhiteSpace(item.Title))
            {
                emphasize.Add(item.Title!);
                if (emphasize.Count >= 6) break;
            }
        }

        // Longest-first so multi-word matches like "Strategy 2.0" win over "Strategy".
        emphasize.Sort((a, b) => b.Length.CompareTo(a.Length));

        AppendBioRuns(BioExcerptTextBlock, bio, emphasize);
    }

    /// <summary>
    /// Strip HTML tags, decode entities, and collapse whitespace â€” produces a
    /// single flowing paragraph suitable for the about-excerpt TextBlock's
    /// run-tokenizer. Mirrors the primitives <see cref="Wavee.UI.WinUI.Controls.HtmlTextBlock"/>
    /// uses internally so the excerpt and the full-biography card stay in sync.
    /// </summary>
    private static string FlattenBioForExcerpt(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var stripped = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(stripped);
        return System.Text.RegularExpressions.Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static void AppendBioRuns(TextBlock target, string text, System.Collections.Generic.IReadOnlyList<string> emphasize)
    {
        var i = 0;
        while (i < text.Length)
        {
            // Find the next earliest emphasize-match starting at i or later.
            var matchStart = -1;
            string? matchValue = null;
            foreach (var token in emphasize)
            {
                if (token.Length == 0) continue;
                var idx = text.IndexOf(token, i, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (matchStart < 0 || idx < matchStart))
                {
                    matchStart = idx;
                    matchValue = token;
                }
            }

            if (matchStart < 0 || matchValue is null)
            {
                if (i < text.Length)
                    target.Inlines.Add(new Run { Text = text[i..] });
                break;
            }

            if (matchStart > i)
                target.Inlines.Add(new Run { Text = text[i..matchStart] });

            var accentRun = new Run
            {
                Text = text.Substring(matchStart, matchValue.Length),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
            };
            target.Inlines.Add(accentRun);
            i = matchStart + matchValue.Length;
        }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // PageTintFill â€” palette-driven gradient backdrop
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void ApplyPageTint()
    {
        var hex = ViewModel.Header.HeaderHeroColorHex;
        var color = ResolveTintColor(hex);

        if (_pageTintBrush is null)
        {
            _pageTintBrush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
            };
            _pageTintHeroStop = new GradientStop { Color = color, Offset = 0.0 };
            _pageTintFadeStop = new GradientStop { Color = Color.FromArgb(0, color.R, color.G, color.B), Offset = 1.0 };
            _pageTintBrush.GradientStops.Add(_pageTintHeroStop);
            _pageTintBrush.GradientStops.Add(_pageTintFadeStop);
            PageTintFill.Fill = _pageTintBrush;
        }
        else
        {
            AnimateTintStop(_pageTintHeroStop!, color);
            AnimateTintStop(_pageTintFadeStop!, Color.FromArgb(0, color.R, color.G, color.B));
        }
    }

    private static void AnimateTintStop(GradientStop stop, Color target)
    {
        var sb = new Storyboard();
        var anim = new ColorAnimation
        {
            From = stop.Color,
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(420)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(anim, stop);
        Storyboard.SetTargetProperty(anim, nameof(GradientStop.Color));
        sb.Children.Add(anim);
        sb.Begin();
    }

    private Color ResolveTintColor(string? hex)
    {
        // Honour the artist palette when available â€” the BackgroundTinted tier
        // is the same one the production hero scrim uses. Fall back to the
        // HeaderHeroColorHex when palette is null, then to a near-bg neutral
        // when both are missing.
        var palette = ViewModel.Header.Palette;
        if (palette is not null)
        {
            var tier = ActualTheme == ElementTheme.Dark
                ? palette.HigherContrast
                : palette.HighContrast;
            if (tier is not null)
                return Color.FromArgb(0xA0, tier.BackgroundTintedR, tier.BackgroundTintedG, tier.BackgroundTintedB);
        }

        if (!string.IsNullOrEmpty(hex) && hex!.StartsWith('#') && hex.Length == 7)
        {
            try
            {
                var r = Convert.ToByte(hex[1..3], 16);
                var g = Convert.ToByte(hex[3..5], 16);
                var b = Convert.ToByte(hex[5..7], 16);
                return Color.FromArgb(0xA0, r, g, b);
            }
            catch { /* fall through */ }
        }

        return ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(0x55, 0x18, 0x18, 0x1F)
            : Color.FromArgb(0x55, 0xE6, 0xE6, 0xEE);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Scroll-driven parallax
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>Defensive reset of MagazineHero + HeroOverlayPanel composition
    /// state. The section-reveal animation and shy-header morph both manipulate
    /// these visuals' Opacity/Offset; if a prior morph or a re-entrant cache nav
    /// leaves either Visual mid-animation, the new artist's hero can land at
    /// Opacity â‰ˆ 0 with only the page-tint visible behind it. Stop any in-flight
    /// Composition animations and snap properties back to defaults so the hero
    /// is always visible from frame one.</summary>
    private void ForceHeroVisualsVisible()
    {
        TryForceVisible(MagazineHero);
        TryForceVisible(HeroOverlayPanel);
        TryForceVisible(HeroOverlayContentGrid);
        TryForceVisible(HeroCopyPanel);
        TryForceVisible(HeroBadgePanel);
        TryForceVisible(HeroNameText);
        TryForceVisible(HeroBioText);
        TryForceVisible(HeroMetaPanel);
        TryForceVisible(HeroPlayButton);
        TryForceVisible(HeroFollowButton);
        TryForceVisible(SpotlightCard);
    }

    private void ScheduleHeroArrangeRefresh()
    {
        var revision = ++_heroArrangeRefreshRevision;
        _ = RefreshHeroArrangeAsync(revision);
    }

    private async Task RefreshHeroArrangeAsync(int revision)
    {
        await Task.Yield();

        if (_isDisposed || _isNavigatingAway || revision != _heroArrangeRefreshRevision)
            return;

        InvalidateHeroArrange();
        HeroPhotoLayer?.UpdateLayout();
        HeroOverlayPanel?.UpdateLayout();
        HeroOverlayContentGrid?.UpdateLayout();

        await Task.Yield();

        if (_isDisposed || _isNavigatingAway || revision != _heroArrangeRefreshRevision)
            return;

        ApplyResponsiveHeroName();
        ForceHeroVisualsVisible();
    }

    private void InvalidateHeroArrange()
    {
        HeroPhotoLayer?.InvalidateMeasure();
        HeroPhotoLayer?.InvalidateArrange();
        HeroOverlayPanel?.InvalidateMeasure();
        HeroOverlayPanel?.InvalidateArrange();
        HeroOverlayContentGrid?.InvalidateMeasure();
        HeroOverlayContentGrid?.InvalidateArrange();
        HeroCopyPanel?.InvalidateMeasure();
        HeroCopyPanel?.InvalidateArrange();
        SpotlightCard?.InvalidateMeasure();
        SpotlightCard?.InvalidateArrange();
    }

    private static void TryForceVisible(UIElement? element)
    {
        if (element is null) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Offset");
            visual.StopAnimation("Scale");
            visual.Opacity = 1f;
            visual.Scale = System.Numerics.Vector3.One;
            element.Opacity = 1;
            element.Translation = System.Numerics.Vector3.Zero;
            element.Scale = System.Numerics.Vector3.One;
        }
        catch
        {
            // Visual may not be realised yet on early nav â€” safe to ignore.
        }
    }

    private void AttachScrollParallax()
    {
        if (_parallaxAttached) return;
        _parallaxAttached = true;
        PageScrollView.ViewChanged += OnScrollViewChanged;
    }

    private void DetachScrollParallax()
    {
        if (!_parallaxAttached) return;
        PageScrollView.ViewChanged -= OnScrollViewChanged;
        _parallaxAttached = false;
    }

    private void OnScrollViewChanged(ScrollView sender, object args)
    {
        var offset = sender.VerticalOffset;

        // PageTintFill fades out as the user scrolls past the hero region.
        // Denominator tracks the hero's MinHeight (420) so the tint persists
        // across the full hero region.
        if (PageTintFill is { } tint)
        {
            var fade = Math.Clamp(1.0 - (offset / 360.0), 0.0, 1.0);
            tint.Opacity = fade;
        }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Section reveal (Composition keyframes, staggered)
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void AttachSectionRevealAnimations()
    {
        // Hero is the first thing to fade up; remaining sections stagger after it.
        AttachRevealAnimation(MagazineHero, 0);
        var i = 1;
        foreach (var child in BodyContent.Children)
        {
            if (child is not UIElement uie) continue;
            AttachRevealAnimation(uie, i++);
        }
    }

    private static void AttachRevealAnimation(UIElement element, int index)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        var delay = TimeSpan.FromMilliseconds(Math.Min(index * 35, 200));
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1.0f), new Vector2(0.3f, 1.0f));

        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.InsertKeyFrame(0f, new Vector3(0, 18, 0));
        offset.InsertKeyFrame(1f, Vector3.Zero, easing);
        offset.Duration = TimeSpan.FromMilliseconds(340);
        offset.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        offset.DelayTime = delay;

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f, easing);
        opacity.Duration = TimeSpan.FromMilliseconds(320);
        opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        opacity.DelayTime = delay;

        visual.StartAnimation("Offset", offset);
        visual.StartAnimation("Opacity", opacity);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Hero Play one-shot pulse
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void FireHeroPlayPulseOnce()
    {
        if (_heroPulseFired) return;
        if (HeroPlayButton is null) return;
        if (string.IsNullOrEmpty(ViewModel.Header.ArtistName)) return;
        _heroPulseFired = true;

        var visual = ElementCompositionPreview.GetElementVisual(HeroPlayButton);
        visual.CenterPoint = new Vector3((float)HeroPlayButton.ActualWidth / 2f, (float)HeroPlayButton.ActualHeight / 2f, 0f);

        var compositor = visual.Compositor;
        var pulse = compositor.CreateVector3KeyFrameAnimation();
        pulse.InsertKeyFrame(0f, new Vector3(1f, 1f, 0f));
        pulse.InsertKeyFrame(0.5f, new Vector3(1.06f, 1.06f, 0f));
        pulse.InsertKeyFrame(1f, new Vector3(1f, 1f, 0f));
        pulse.Duration = TimeSpan.FromMilliseconds(800);
        visual.StartAnimation("Scale", pulse);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // ElementPrepared handlers â€” bind cards that don't use plain x:Bind
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>The grid layout reports column-count changes whenever its
    /// width crosses a MinItemWidth threshold. Mirroring the legacy page,
    /// we feed that count to the ViewModel so TracksPerPage =
    /// RowsPerPage Ã— ColumnCount stays correct as the viewport resizes.</summary>
    private void TopTracksLayout_ColumnCountChanged(object? sender, int columns)
    {
        ViewModel.TopTracks.ColumnCount = columns;
    }

    private void TopTracksRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not TrackItem row) return;

        // RowIndex is bound via x:Bind to LazyTrackItem.Index (absolute), so
        // page 2 of extended tracks shows ranks 9..16 instead of resetting to
        // 1..8 â€” don't override here.
        // No popularity badge â€” the rank number is enough and the star clashes
        // with the selection pill on row 1. No pre-selection either; let the
        // user pick.
        row.ShowPopularityBadge = false;

        // Alternating row striping (matches TrackDataGrid in playlists) plus a
        // hover tint so the row reads as interactive â€” the raw ItemsRepeater
        // host here doesn't get either by default.
        row.SetAlternatingBorder(args.Index % 2 != 0, useCardRow: false);
        row.RowHoverBackgroundBrush =
            (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
    }

    private void PopularReleasesRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not PopularReleaseRow row) return;
        if (row.DataContext is not LazyReleaseItem lri || lri.Data is not Wavee.UI.WinUI.ViewModels.ArtistReleaseVm vm) return;

        row.Title = vm.Name ?? string.Empty;
        row.CoverImageUrl = vm.ImageUrl;
        row.Rank = args.Index + 1;
        // Push the artist palette through before flipping IsFeatured â€” the
        // featured-row tint derives directly from AccentBrush, so setting it
        // first means the highlight reads as the artist's palette colour
        // rather than the hardcoded teal default for one frame.
        row.AccentBrush = ViewModel.Header.SectionAccentBrush;
        row.AccentForegroundBrush = ViewModel.Header.PaletteAccentPillForegroundBrush;
        row.IsFeatured = args.Index == 0;
        row.Tag = vm.Uri;
        // Self-routing DPs â€” control's internal CardButton_Click navigates
        // via AlbumNavigationHelper (prefetch + connected anim + count
        // prefill) when NavigationUri is set. Subtitle feeds nav prefill.
        row.NavigationUri = vm.Uri;
        row.NavigationTotalTracks = vm.TrackCount;
        row.Subtitle = ViewModel.Header.ArtistName;

        var meta = new System.Collections.Generic.List<string>(3);
        if (vm.Year > 0) meta.Add(vm.Year.ToString());
        if (!string.IsNullOrEmpty(vm.Type)) meta.Add(ToTitleCase(vm.Type));
        if (vm.TrackCount > 0) meta.Add($"{vm.TrackCount} tracks");
        row.Meta = string.Join(" · ", meta);

        Wavee.UI.WinUI.Behaviors.CardHoverScaleBehavior.SetEnable(row, true);
    }

    private void ConcertsRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not TicketStub stub) return;
        if (stub.DataContext is not ConcertVm vm) return;

        // DateFormatted comes in as "MMM d" upper-cased ("MAY 16"). Split it.
        var parts = (vm.DateFormatted ?? string.Empty).Split(' ', 2);
        stub.Month = parts.Length > 0 ? parts[0] : string.Empty;
        stub.Day = parts.Length > 1 ? parts[1] : string.Empty;
        stub.Venue = vm.Venue ?? string.Empty;
        stub.City = vm.City ?? string.Empty;
        stub.IsNearUser = vm.IsNearUser;
        stub.Tag = vm.Uri;

        Wavee.UI.WinUI.Behaviors.CardHoverScaleBehavior.SetEnable(stub, true);
    }

    private static string ToTitleCase(string s)
        => string.IsNullOrEmpty(s)
            ? s
            : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Click handlers
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void SpotlightCard_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SpotlightReleaseUri is { Length: > 0 } uri)
            NavigationHelpers.OpenAlbum(uri, ViewModel.SpotlightReleaseName ?? string.Empty);
    }

    private async void SpotlightPlay_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SpotlightReleaseUri is not { Length: > 0 } uri) return;

        var playback = Ioc.Default.GetService<IPlaybackService>();
        if (playback is null) return;

        var result = await playback.PlayContextAsync(
            uri,
            new PlayContextOptions { PlayOriginFeature = "artist_page" });

        if (!result.IsSuccess)
            _logger?.LogWarning("Spotlight release play failed: {Error}", result.ErrorMessage);
    }

    private void SpotlightSave_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SpotlightReleaseUri is not { Length: > 0 } uri) return;

        var likeService = Ioc.Default.GetService<ITrackLikeService>();
        if (likeService is null) return;

        var isSaved = likeService.IsSaved(SavedItemType.Album, uri);
        likeService.ToggleSave(SavedItemType.Album, uri, isSaved);
    }

    private void SpotlightShare_Click(object sender, RoutedEventArgs e)
    {
        var shareUrl = ToOpenSpotifyUrl(ViewModel.SpotlightReleaseUri);
        if (string.IsNullOrEmpty(shareUrl)) return;

        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(shareUrl);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    private void PopularReleaseRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is PopularReleaseRow row && row.Tag is string uri && !string.IsNullOrEmpty(uri))
            NavigationHelpers.OpenAlbum(uri, row.Title);
    }

    private void ReleaseNavigateCard_Click(object? sender, EventArgs e)
    {
        if (sender is ContentCard card && card.Tag is string uri && !string.IsNullOrEmpty(uri))
            NavigationHelpers.OpenAlbum(uri, card.Title ?? string.Empty);
    }

    private void AlbumsSeeAll_Click(object sender, RoutedEventArgs e)
        => OpenDiscography(ArtistDiscographyGroupKind.Albums);

    private void SinglesSeeAll_Click(object sender, RoutedEventArgs e)
        => OpenDiscography(ArtistDiscographyGroupKind.Singles);

    private void OpenDiscography(ArtistDiscographyGroupKind groupKind)
    {
        var artistId = ViewModel.ArtistId;
        if (string.IsNullOrWhiteSpace(artistId)) return;
        var uri = artistId.StartsWith("spotify:artist:", StringComparison.Ordinal)
            ? artistId
            : $"spotify:artist:{artistId}";
        NavigationHelpers.OpenArtistDiscography(
            uri,
            ViewModel.Header.ArtistName ?? string.Empty,
            groupKind,
            ViewModel.Header.ArtistImageUrl,
            NavigationHelpers.IsCtrlPressed());
    }

    private async void AlbumCard_Click(object? sender, EventArgs e)
    {
        if (sender is not FrameworkElement source) return;
        var expansionRevision = ++_releaseExpansionRevision;

        var repeater = FindParent<ItemsRepeater>(source);
        if (repeater is null || repeater == AppearsOnRepeater)
            return;

        var item = ResolveReleaseItemFromRepeaterClick(source, repeater, out var trueRepeater, out var itemIndex);
        if (item?.IsLoaded != true || item.Data is null)
            return;

        if (ViewModel.Discography.ExpandedAlbum?.Id == item.Id)
        {
            // User clicked the already-expanded album â†’ animate the close.
            await CollapseExpandedAlbumAsync(animate: true);
            return;
        }

        // Switching to a different album: tear the previous expansion down
        // synchronously so the new one appears in the same frame. Awaiting an
        // exit animation here just stacked latency on top of the enter delay.
        if (HasActiveReleaseExpansion())
            CollapseExpandedAlbumCore();

        if (expansionRevision != _releaseExpansionRevision || _isNavigatingAway || XamlRoot is null)
            return;

        if (trueRepeater.Parent is not StackPanel parentPanel)
            return;

        var repeaterIndex = parentPanel.Children.IndexOf(trueRepeater);
        if (repeaterIndex < 0)
            return;

        _originalRepeater = trueRepeater;
        _originalItemsSource = trueRepeater.ItemsSource;
        _splitParent = parentPanel;
        _expandedItem = item;
        _expandedItemIndex = itemIndex;

        _activeDetailPanel = new AlbumDetailPanel
        {
            Album = item.Data,
            Tracks = ViewModel.Discography.ExpandedAlbumTracks
        };
        _closeRequestedHandler = async (_, _) => await CollapseExpandedAlbumAsync(animate: true);
        _activeDetailPanel.CloseRequested += _closeRequestedHandler;

        parentPanel.Children.Insert(repeaterIndex + 1, _activeDetailPanel);
        ApplySplitLayout();

        // Single motion source: scroll the inserted panel into view. Adding a
        // Y-translate enter animation alongside this fought the scroll and
        // produced the "down then up again" stutter; opacity-prep made the
        // panel invisible for the first ~180 ms and read as click latency.
        _activeDetailPanel.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = true,
            VerticalAlignmentRatio = 0.5,
            VerticalOffset = -200
        });

        _ = FetchAlbumColorAsync(item.Data, _activeDetailPanel);
        ViewModel.Discography.ExpandAlbumCommand.Execute(item);
    }

    private LazyReleaseItem? ResolveReleaseItemFromRepeaterClick(
        FrameworkElement source,
        ItemsRepeater repeater,
        out ItemsRepeater trueRepeater,
        out int itemIndex)
    {
        var isSplitRepeaterAfter = _splitRepeaterAfter is not null && ReferenceEquals(repeater, _splitRepeaterAfter);
        var isOriginalSplitRepeater = _originalRepeater is not null && ReferenceEquals(repeater, _originalRepeater);

        trueRepeater = isSplitRepeaterAfter
            ? _originalRepeater ?? repeater
            : repeater;
        itemIndex = -1;

        var templateRoot = FindRepeaterTemplateRoot(source, repeater);
        if (templateRoot is null)
            return null;

        var visibleIndex = repeater.GetElementIndex(templateRoot);
        if (visibleIndex < 0 || repeater.ItemsSource is not IList visibleItems || visibleIndex >= visibleItems.Count)
            return null;

        var item = visibleItems[visibleIndex] as LazyReleaseItem;
        if (item is null)
            return null;

        var fullItems = (isSplitRepeaterAfter || isOriginalSplitRepeater
            ? _originalItemsSource
            : trueRepeater.ItemsSource) as IList;
        itemIndex = fullItems?.IndexOf(item) ?? visibleIndex;
        if (itemIndex < 0)
            itemIndex = visibleIndex;

        return item;
    }

    private static UIElement? FindRepeaterTemplateRoot(DependencyObject source, ItemsRepeater repeater)
    {
        DependencyObject? current = source;
        var parent = VisualTreeHelper.GetParent(current);
        while (parent is not null && parent != repeater)
        {
            current = parent;
            parent = VisualTreeHelper.GetParent(current);
        }

        return parent == repeater ? current as UIElement : null;
    }

    private void ApplySplitLayout()
    {
        if (_originalRepeater is null
            || _splitParent is null
            || _originalItemsSource is not IList allItems
            || _activeDetailPanel is null
            || _expandedItemIndex < 0)
        {
            return;
        }

        var layout = _originalRepeater.Layout as UniformGridLayout;
        var availableWidth = Math.Max(_splitParent.ActualWidth, _originalRepeater.ActualWidth);
        if (availableWidth <= 0)
            availableWidth = BodyContent?.ActualWidth ?? 0;
        if (availableWidth <= 0)
            return;

        var minWidth = layout?.MinItemWidth ?? 160;
        var spacing = layout?.MinColumnSpacing ?? 16;
        var columns = Math.Max(1, (int)Math.Floor((availableWidth + spacing) / (minWidth + spacing)));
        var rowOfItem = _expandedItemIndex / columns;
        var splitAfterIndex = Math.Min((rowOfItem + 1) * columns, allItems.Count);

        var itemsBefore = new System.Collections.Generic.List<object>();
        var itemsAfter = new System.Collections.Generic.List<object>();
        for (var i = 0; i < allItems.Count; i++)
        {
            if (i < splitAfterIndex)
                itemsBefore.Add(allItems[i]!);
            else
                itemsAfter.Add(allItems[i]!);
        }

        _originalRepeater.ItemsSource = itemsBefore;

        if (_splitRepeaterAfter is not null)
        {
            if (itemsAfter.Count > 0)
            {
                _splitRepeaterAfter.ItemsSource = itemsAfter;
            }
            else
            {
                _splitRepeaterAfter.ElementClearing -= DiscographyRepeater_ElementClearing;
                ReleaseImagesInSubtree(_splitRepeaterAfter);
                _splitParent.Children.Remove(_splitRepeaterAfter);
                _splitRepeaterAfter = null;
            }
        }
        else if (itemsAfter.Count > 0)
        {
            _splitRepeaterAfter = new ItemsRepeater
            {
                Layout = new UniformGridLayout
                {
                    MinItemWidth = minWidth,
                    MinItemHeight = layout?.MinItemHeight ?? 220,
                    MinRowSpacing = layout?.MinRowSpacing ?? 20,
                    MinColumnSpacing = spacing,
                    ItemsStretch = UniformGridLayoutItemsStretch.Uniform
                },
                ItemTemplate = _originalRepeater.ItemTemplate,
                ItemsSource = itemsAfter
            };
            _splitRepeaterAfter.ElementClearing += DiscographyRepeater_ElementClearing;

            var panelIndex = _splitParent.Children.IndexOf(_activeDetailPanel);
            if (panelIndex >= 0)
                _splitParent.Children.Insert(panelIndex + 1, _splitRepeaterAfter);
        }

        var columnIndex = _expandedItemIndex % columns;
        var cellWidth = (availableWidth - (columns - 1) * spacing) / columns;
        _activeDetailPanel.NotchOffsetX = columnIndex * (cellWidth + spacing) + cellWidth / 2;
    }

    private bool HasActiveReleaseExpansion()
        => _activeDetailPanel is not null
           || _splitRepeaterAfter is not null
           || _originalRepeater is not null
           || ViewModel.Discography.ExpandedAlbum is not null;

    private async Task CollapseExpandedAlbumAsync(bool animate)
    {
        var panel = _activeDetailPanel;
        if (animate && panel is not null)
            await AnimateReleasePanelOutAsync(panel);

        CollapseExpandedAlbumCore();
    }

    private async Task AnimateReleasePanelOutAsync(FrameworkElement panel)
    {
        try
        {
            await AnimationBuilder.Create()
                .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(ReleasePanelExitMs))
                .Translation(
                    Axis.Y,
                    to: ReleasePanelExitOffset,
                    duration: TimeSpan.FromMilliseconds(ReleasePanelExitMs),
                    easingType: EasingType.Sine,
                    easingMode: EasingMode.EaseIn)
                .StartAsync(panel);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "ArtistPage release panel collapse animation failed.");
        }
    }

    private void CollapseExpandedAlbumCore()
    {
        var hadExpansion = _activeDetailPanel is not null
            || _splitRepeaterAfter is not null
            || _originalRepeater is not null
            || ViewModel.Discography.ExpandedAlbum is not null;

        CancelResizeDebounce();

        if (_activeDetailPanel is not null && _closeRequestedHandler is not null)
            _activeDetailPanel.CloseRequested -= _closeRequestedHandler;
        _closeRequestedHandler = null;

        if (_activeDetailPanel is not null)
        {
            ReleaseImagesInSubtree(_activeDetailPanel);
            _activeDetailPanel.Tracks = null;
            _splitParent?.Children.Remove(_activeDetailPanel);
        }

        if (_splitRepeaterAfter is not null)
        {
            _splitRepeaterAfter.ElementClearing -= DiscographyRepeater_ElementClearing;
            ReleaseImagesInSubtree(_splitRepeaterAfter);
            _splitParent?.Children.Remove(_splitRepeaterAfter);
        }

        if (_originalRepeater is not null)
            _originalRepeater.ItemsSource = _originalItemsSource;

        _activeDetailPanel = null;
        _splitRepeaterAfter = null;
        _splitParent = null;
        _originalRepeater = null;
        _originalItemsSource = null;
        _expandedItem = null;
        _expandedItemIndex = -1;

        if (hadExpansion)
            ViewModel.Discography.CollapseAlbumCommand.Execute(null);
    }

    private async Task FetchAlbumColorAsync(Wavee.UI.WinUI.ViewModels.ArtistReleaseVm album, AlbumDetailPanel panel)
    {
        if (!string.IsNullOrEmpty(album.ColorHex))
        {
            panel.ColorHex = album.ColorHex;
            return;
        }

        var imageUrl = SpotifyImageHelper.ToHttpsUrl(album.ImageUrl);
        if (string.IsNullOrEmpty(imageUrl))
            return;

        try
        {
            var color = await _colorService.GetColorAsync(imageUrl);
            if (color is null || panel.XamlRoot is null)
                return;

            var isDark = ActualTheme == ElementTheme.Dark;
            var hex = isDark
                ? color.DarkHex ?? color.RawHex
                : color.LightHex ?? color.RawHex;

            if (!string.IsNullOrEmpty(hex))
                panel.ColorHex = hex;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch artist release color");
        }
    }

    private void DiscographyRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        => ReleaseImagesInSubtree(args.Element);

    private static void ReleaseImagesInSubtree(DependencyObject? root)
    {
        if (root is null)
            return;

        switch (root)
        {
            case ContentCard card:
                card.ReleaseImage();
                break;
            case Image image:
                image.Source = null;
                image.Opacity = 1;
                image.Visibility = Visibility.Visible;
                break;
            case Microsoft.UI.Xaml.Shapes.Shape { Fill: ImageBrush shapeBrush }:
                shapeBrush.ImageSource = null;
                break;
            case Border { Background: ImageBrush borderBrush }:
                borderBrush.ImageSource = null;
                break;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
            ReleaseImagesInSubtree(VisualTreeHelper.GetChild(root, i));
    }

    private static T? FindParent<T>(DependencyObject child, DependencyObject? stopAt = null)
        where T : DependencyObject
    {
        var current = child;
        var parent = VisualTreeHelper.GetParent(current);
        while (parent is not null && parent != stopAt)
        {
            if (parent is T found)
                return found;

            current = parent;
            parent = VisualTreeHelper.GetParent(current);
        }

        return stopAt is not null && parent == stopAt
            ? current as T
            : null;
    }

    private void MusicVideoCard_Click(object? sender, EventArgs e)
    {
        if (sender is ContentCard { Tag: string trackUri } && !string.IsNullOrEmpty(trackUri))
            _logger?.LogDebug("MusicVideoCard clicked: {Uri}", trackUri);
    }

    private void PlaylistCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MediaCard card && card.Tag is string uri && !string.IsNullOrEmpty(uri))
        {
            NavigationHelpers.OpenPlaylist(
                new ContentNavigationParameter { Uri = uri, Title = card.Title },
                card.Title ?? "Playlist",
                openInNewTab: NavigationHelpers.IsCtrlPressed());
        }
    }

    private void ConcertStub_Click(object sender, RoutedEventArgs e) => OpenConcertFromStub(sender);

    private void ConcertStub_ViewClick(object sender, RoutedEventArgs e) => OpenConcertFromStub(sender);

    private void OpenConcertFromStub(object sender)
    {
        if (sender is not TicketStub stub) return;
        // Tag carries the spotify:concert URI (wired in ConcertsRepeater_ElementPrepared);
        // DataContext is the original ConcertVm, which we use for the tab title.
        if (stub.Tag is not string uri || string.IsNullOrEmpty(uri)) return;
        var vm = stub.DataContext as ConcertVm;
        var title = !string.IsNullOrEmpty(vm?.Venue) ? vm!.Venue! : (vm?.Title ?? "Concert");
        var param = new ContentNavigationParameter { Uri = uri, Title = title };
        NavigationHelpers.OpenConcert(param, title, NavigationHelpers.IsCtrlPressed());
    }

    private void ConcertStub_BuyClick(object sender, RoutedEventArgs e)
    {
        if (sender is TicketStub { Tag: string uri } && !string.IsNullOrEmpty(uri)
            && Uri.TryCreate(uri, UriKind.Absolute, out var u))
            _ = Windows.System.Launcher.LaunchUriAsync(u);
    }

    private void MerchCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MerchCard { Tag: string url } && !string.IsNullOrEmpty(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var u))
            _ = Windows.System.Launcher.LaunchUriAsync(u);
    }

    private void MerchCard_BuyClick(object sender, RoutedEventArgs e) => MerchCard_Click(sender, e);

    // â”€â”€ Gallery marquee / lightbox â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //
    // MarqueeGalleryStrip raises ItemTapped with the original-list index when
    // any tile is clicked (the strip itself handles duplication for the
    // continuous loop and reports the un-duplicated index).
    private void GalleryMarquee_ItemTapped(
        Wavee.UI.WinUI.Controls.Gallery.MarqueeGalleryStrip sender,
        Wavee.UI.WinUI.Controls.Gallery.GalleryItemTappedEventArgs args)
    {
        OpenGalleryLightboxAt(args.Index);
    }

    private void OpenGalleryLightboxAt(int index)
    {
        var photos = ViewModel.Extras.GalleryPhotos;
        if (photos is null || photos.Count == 0) return;
        if (index < 0 || index >= photos.Count) index = 0;
        GalleryFlip.ItemsSource = photos;
        GalleryFlip.SelectedIndex = index;
        GalleryLightbox.Visibility = Visibility.Visible;
        // Focus so the KeyDown handler can receive Esc.
        GalleryLightbox.Focus(FocusState.Programmatic);
    }

    private void CloseGalleryLightbox()
    {
        if (GalleryLightbox is null) return;
        GalleryLightbox.Visibility = Visibility.Collapsed;
        if (GalleryFlip is not null)
        {
            GalleryFlip.SelectedIndex = -1;
            GalleryFlip.ItemsSource = null;
        }
    }

    private void GalleryLightbox_BackdropTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // Only dismiss when the tap landed on the backdrop itself or the FlipView's chrome â€”
        // not on the actual photo. If the user clicks the photo we keep the lightbox open so
        // the FlipView's prev/next gesture still works.
        if (ReferenceEquals(e.OriginalSource, GalleryLightbox))
            CloseGalleryLightbox();
    }

    private void GalleryLightbox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CloseGalleryLightbox();
        }
    }

    private void GalleryLightboxClose_Click(object sender, RoutedEventArgs e) => CloseGalleryLightbox();

    private void RelatedArtist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ArtistCircleCard card && card.Tag is string uri && !string.IsNullOrEmpty(uri))
            NavigationHelpers.OpenArtist(uri, card.DisplayName ?? string.Empty);
    }

    private void TourBanner_Click(object sender, RoutedEventArgs e)
    {
        // Round 4 split the old TourMerchSection grid into two independent
        // rows; scroll to Concerts when it's present, otherwise Merch.
        FrameworkElement? target =
            ConcertsSection is { Visibility: Visibility.Visible } ? ConcertsSection :
            MerchSection is { Visibility: Visibility.Visible } ? MerchSection :
            null;
        if (target is null) return;
        try
        {
            var transform = target.TransformToVisual(ContentRoot);
            var offsetY = transform.TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            PageScrollView.ScrollTo(0, Math.Max(0, offsetY - 16), new ScrollingScrollOptions(ScrollingAnimationMode.Enabled));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "TourBanner scroll-into-view failed.");
        }
    }

    private void ReadFullBioLink_Click(object sender, RoutedEventArgs e)
    {
        if (AboutLinksSection is null) return;
        try
        {
            var transform = AboutLinksSection.TransformToVisual(ContentRoot);
            var offsetY = transform.TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            PageScrollView.ScrollTo(0, Math.Max(0, offsetY - 16), new ScrollingScrollOptions(ScrollingAnimationMode.Enabled));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "ReadFullBio scroll-into-view failed.");
        }
    }

    private void ShareArtist_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ViewModel.ArtistId)) return;
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText($"https://open.spotify.com/artist/{ViewModel.ArtistId.Replace("spotify:artist:", "", StringComparison.Ordinal)}");
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    private void CopyArtistLink_Click(object sender, RoutedEventArgs e) => ShareArtist_Click(sender, e);

    private void OpenInSpotify_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ViewModel.ArtistId)) return;
        var id = ViewModel.ArtistId.Replace("spotify:artist:", "", StringComparison.Ordinal);
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri($"https://open.spotify.com/artist/{id}"));
    }

    private static string? ToOpenSpotifyUrl(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var parts = uri.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0].Equals("spotify", StringComparison.OrdinalIgnoreCase))
            return $"https://open.spotify.com/{parts[1]}/{parts[2]}";

        return null;
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // INavigationCacheMemoryParticipant
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // Micro-step trim — see AlbumPage equivalent for the rationale.
    IEnumerable<Action> INavigationCacheMemoryParticipant.GetTrimMicroSteps()
    {
        if (_trimmedForNavigationCache)
            yield break;

        yield return () =>
        {
            _trimmedForNavigationCache = true;
            _lastRestoredArtistId = ViewModel.ArtistId;
        };
        yield return () => HeroGrid?.ReleaseSurface();
        yield return CloseGalleryLightbox;
        yield return () => ViewModel.Hibernate();
        yield return () => Bindings?.StopTracking();
    }

    public void TrimForNavigationCache()
    {
        if (_trimmedForNavigationCache) return;
        _trimmedForNavigationCache = true;
        _lastRestoredArtistId = ViewModel.ArtistId;
        HeroGrid?.ReleaseSurface();
        CloseGalleryLightbox();
        // NOTE: do NOT DetachScrollParallax here. Under PageHost the page is
        // kept rooted with Visibility=Collapsed on leave (not detached from
        // the visual tree like Frame did) — the composition ExpressionAnimations
        // can safely keep evaluating against the still-rooted ScrollView while
        // the page is hidden. Detaching here would break sticky / parallax on
        // the next cache-hit re-entry because Loaded does NOT re-fire under
        // PageHost (page Visibility flips but it never leaves the tree), so
        // OnLoaded's AttachScrollParallax call never gets a second chance.
        // DetachScrollParallax stays in OnUnloaded + Dispose for true teardown.
        // Hibernate releases the bound discography / related-artist / video /
        // merch collections AND unsubscribes the VM from singleton services.
        // Without this, the cached page's VM stays rooted by
        // _playbackStateService.PropertyChanged forever â€" every cross-type
        // navigation adds a stale ArtistViewModel to the singleton's
        // invocation list, the heap grows linearly, Gen2 GCs lengthen, and
        // clicks freeze for 1â€"2 s.
        ViewModel.Hibernate();
        // Detach compiled x:Bind so VM PropertyChanged firings can't reach
        // the page tree while it sits invisible in the Frame cache.
        Bindings?.StopTracking();
    }

    public void RestoreFromNavigationCache()
    {
        HeroGrid?.RestoreSurface();
        if (!_trimmedForNavigationCache) return;
        _trimmedForNavigationCache = false;

        // Re-attach compiled x:Bind. Trim's Bindings.StopTracking micro-step
        // detached the binding graph and ViewModel.Hibernate null'd
        // Header.Artist.HeaderImageUrl (texture-release path). On same-artist
        // Back returns OnEntered skips Bindings.Update() because
        // artistChanged=false, so without this re-attach the URL push from
        // EnsureHeroUrls (called by ApplyOverviewState's same-artist Ready
        // branch) never reaches the HeroHeader.ImageUrl DP and the hero image
        // stays blank. Deferred to the next dispatcher tick so DWM gets a
        // paint frame between the page reattaching and the synchronous
        // binding sweep â€” mirrors AlbumPage.RestoreFromNavigationCache.
        DispatcherQueue?.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            () =>
            {
                if (_isDisposed) return;
                Bindings?.Update();
            });
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // IDisposable
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        SizeChanged -= OnPageSizeChanged;
        DetachScrollParallax();
        DetachHeroSizeHandlers();
        CancelResizeDebounce();
        CollapseExpandedAlbumCore();
        _shyHeader?.Detach();
        _shyHeader = null;

        ViewModel.ContentChanged -= ViewModel_ContentChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Header.PropertyChanged -= ViewModel_ChildPropertyChanged;
        ViewModel.Bio.PropertyChanged -= ViewModel_ChildPropertyChanged;
        ViewModel.Discography.PropertyChanged -= ViewModel_ChildPropertyChanged;
        ViewModel.Extras.PropertyChanged -= ViewModel_ChildPropertyChanged;

        if (ViewModel is IDisposable d)
            d.Dispose();
    }
}
