using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Diagnostics;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class AlbumPage : Page, ITabBarItemContent, INavigationCacheMemoryParticipant, IDisposable, IContentPageHost
{
    private readonly ILogger? _logger;
    private readonly INotificationService? _notificationService;
    private readonly ISettingsService _settings;
    private bool _isDisposed;
    private bool _trimmedForNavigationCache;
    private string? _lastRestoredAlbumId;
    private int _layoutSettlingGeneration;

    public AlbumViewModel ViewModel { get; }

    public ContentPageController PageController { get; }

    public ShimmerLoadGate ShimmerGate => PageController.ShimmerGate;

    /// <summary>
    /// Sibling shimmer gate for the TrackDataGrid footer rail (about-artist card
    /// + Fans-also-like / More-by-Artist shelves). Operates independently from
    /// <see cref="ShimmerGate"/> so the footer can keep its skeleton up until
    /// <see cref="AlbumViewModel.IsContentReady"/> flips true — i.e. after BOTH
    /// header metadata and tracks have hydrated — instead of revealing the
    /// instant the header lands and leaving an unstyled card floating below
    /// still-animating skeleton rows.
    /// </summary>
    public ShimmerLoadGate FooterShimmerGate { get; } = new();

    private bool _footerRevealed;
    private int _footerRevealGeneration;

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    // ── IContentPageHost ─────────────────────────────────────────────────────
    FrameworkElement? IContentPageHost.ShimmerContainer => ShimmerContainer;
    FrameworkElement IContentPageHost.ContentContainer => ContentContainer;
    FrameworkLayer IContentPageHost.CrossfadeLayer => FrameworkLayer.Composition;
    string IContentPageHost.PageIdForLogging => $"album:{XfadeLog.Tag(ViewModel.AlbumId)}";
    bool IContentPageHost.IsLoading => ViewModel.IsLoading;
    bool IContentPageHost.HasContent => !string.IsNullOrEmpty(ViewModel.AlbumName);

    public AlbumPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<AlbumViewModel>();
        _logger = Ioc.Default.GetService<ILogger<AlbumPage>>();
        _notificationService = Ioc.Default.GetService<INotificationService>();
        _settings = Ioc.Default.GetRequiredService<ISettingsService>();
        PageController = new ContentPageController(this, _logger);
        InitializeComponent();

        // PlayCount column formatter — TrackDataGrid's PlayCount column uses this
        // delegate to reach AlbumTrackDto.PlayCountFormatted (TrackItem doesn't know
        // about the album-specific DTO). Same pattern as PlaylistPage.
        TrackGrid.PlayCountFormatter = item =>
            item is ViewModels.LazyTrackItem lazy && lazy.Data is Data.DTOs.AlbumTrackDto dto
                ? dto.PlayCountFormatted
                : "";
        TrackGrid.PopularityBadgeSelector = ViewModel.IsPopularTrack;

        ViewModel.ContentChanged += ViewModel_ContentChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ActualThemeChanged += OnActualThemeChanged;
        Loaded += AlbumPage_Loaded;
        Unloaded += AlbumPage_Unloaded;
        _logger?.LogDebug("[xfade][album:{Id}] ctor.enter", XfadeLog.Tag(ViewModel.AlbumId));

        // Start the content layer invisible at composition level so the
        // shimmer→content transition is a smooth crossfade, not the previous
        // hard cut the BoolToVisibilityConverter produced.
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 0;

        // Other-versions flyout is built dynamically — the data shape (name + year +
        // type) is uniform per album but the count varies.
        // ViewModel_PropertyChanged rebuilds it when AlternateReleases changes.
        RebuildOtherVersionsFlyout();

        // Seed the VM with the current theme so palette brushes are correct as soon
        // as the data lands. ActualThemeChanged keeps them in sync from there.
        ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);
    }

    private void ViewModel_ContentChanged(object? sender, TabItemParameter e)
        => ContentChanged?.Invoke(this, e);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlbumViewModel.IsLoading))
        {
            if (ViewModel.IsLoading)
                PageController.OnIsLoadingChanged();
            else
                _ = ShowContentAfterAlbumLayoutSettlesAsync();
        }
        else if (e.PropertyName == nameof(AlbumViewModel.IsContentReady))
        {
            if (ViewModel.IsContentReady)
                _ = TryRevealFooterAsync();
        }
        else if (e.PropertyName == nameof(AlbumViewModel.AlternateReleases))
            RebuildOtherVersionsFlyout();
        else if (e.PropertyName == nameof(AlbumViewModel.HeaderArtistLinks))
            RebuildHeaderArtistsText();
    }

    /// <summary>
    /// Rebuild the inline content of <c>HeaderArtistsText</c> from the current
    /// <see cref="AlbumViewModel.HeaderArtistLinks"/>. Inline <c>Hyperlink</c>s
    /// per name + <c>Run</c> separators give the names line natural typographic
    /// wrapping (no orphan ", " on the second row), which a horizontal
    /// ItemsControl can't deliver inside the header's narrow Grid column.
    /// </summary>
    private void RebuildHeaderArtistsText()
    {
        if (HeaderArtistsText == null) return;
        HeaderArtistsText.Inlines.Clear();

        var links = ViewModel.HeaderArtistLinks;
        if (links == null || links.Count == 0) return;

        for (var i = 0; i < links.Count; i++)
        {
            if (i > 0)
            {
                HeaderArtistsText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = ", " });
            }
            var entry = links[i];
            var hyperlink = new Microsoft.UI.Xaml.Documents.Hyperlink
            {
                UnderlineStyle = Microsoft.UI.Xaml.Documents.UnderlineStyle.None,
            };
            hyperlink.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = entry.Name });
            var capturedUri = entry.Uri;
            var capturedName = entry.Name;
            hyperlink.Click += (_, _) =>
            {
                if (string.IsNullOrEmpty(capturedUri)) return;
                NavigationHelpers.OpenArtist(capturedUri, capturedName, NavigationHelpers.IsCtrlPressed());
            };
            HeaderArtistsText.Inlines.Add(hyperlink);
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);
    }

    private void AlbumPage_Loaded(object sender, RoutedEventArgs e)
    {
        // Re-emit the inline header names from the current VM state — covers the
        // warm-cache navigation path where ApplyDetail runs before the page is
        // fully constructed and PropertyChanged finds HeaderArtistsText null.
        RebuildHeaderArtistsText();
        AttachParallax();
    }

    private void AlbumPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // Under NavigationCacheMode=Enabled the Page may be reused across N
        // navigations until LRU eviction. Keep the ctor's PropertyChanged
        // subscription attached for the page's lifetime — unhooking here would
        // leave the cached page deaf to the next IsLoading=false transition.
        PageController.IsNavigatingAway = true;
        DetachParallax();
    }

    // ── Sticky left column (composition-thread) ──────────────────────────
    //
    // Single unified page-level ScrollView drives the whole layout (left
    // sidebar + TrackDataGrid + footer rail). The ScrollView translates ALL
    // its content up by `offset`, so to make the left column stay PINNED at
    // its starting position we apply an equal-and-opposite Translation.Y to
    // its composition Visual: `+offset` cancels the scroll exactly.
    //
    // CRITICAL: this runs as an ExpressionAnimation bound to the
    // ScrollView's ExpressionAnimationSources — NOT a UI-thread
    // ViewChanged handler. The event-handler version had a visible
    // "scroll-then-snap-back" flicker because ViewChanged fires AFTER the
    // frame has painted; the compensating translation always landed one
    // frame late. The expression runs on the composition thread in
    // lockstep with the scroll, so the visual is never observably out of
    // position.
    //
    // The MaxLag scalar in the local _stickyProps property set caps the
    // translation when the column is taller than the viewport — after the
    // user has scrolled past the column's own extent, it begins to scroll
    // off naturally so the page doesn't permanently lock the sidebar at the
    // top. Updated via SizeChanged whenever either bounds change.

    private bool _parallaxAttached;
    private Microsoft.UI.Composition.CompositionPropertySet? _stickyProps;
    private Microsoft.UI.Composition.ExpressionAnimation? _stickyAnimation;

    private void AttachParallax()
    {
        if (_parallaxAttached) return;
        if (PageScrollView is null || LeftColumnHost is null) return;
        _parallaxAttached = true;

        var visual = ElementCompositionPreview.GetElementVisual(LeftColumnHost);
        var compositor = visual.Compositor;

        // Animatable Translation requires opt-in; the default Visual surface
        // ignores `StartAnimation("Translation", …)` without this.
        ElementCompositionPreview.SetIsTranslationEnabled(LeftColumnHost, true);

        // Local property set carries the dynamic MaxLag scalar. The
        // expression reads it reactively — pushing a new value via
        // InsertScalar re-evaluates the animation without restart.
        _stickyProps = compositor.CreatePropertySet();
        _stickyProps.InsertScalar("LeftColHeight", (float)LeftColumnHost.ActualHeight);

        // ExpressionAnimationSources on the NEW ScrollView control exposes
        // `Position` (Vector2) — positive Y when scrolled DOWN. That's the
        // raw offset we want to apply as a positive Y translation to pull
        // the column DOWN, countering the scroll's upward push and pinning
        // the column in place. (The OLD ScrollViewer.ManipulationPropertySet
        // used a NEGATIVE `Translation` property — different shape; don't
        // confuse the two.)
        //
        // Clamp(0, MaxLag) floors out at the top (no rubber-band overshoot)
        // and ceilings when the column is taller than the viewport (so it
        // can eventually scroll off).
        // Clamp(Y, 0, max(0, ExtentH - LeftColH)):
        //   • For Y small: T = Y → column pinned at viewport top.
        //   • Once Y exceeds (ExtentH - LeftColH), T plateaus at that cap and
        //     the column drifts up so its BOTTOM reaches viewport bottom at
        //     Y_max. Without the cap, the column would stay pinned even at
        //     max scroll and its bottom (release info / About card) would
        //     stay permanently below the viewport. ExtentH is sourced from
        //     scroll.Extent.Y so the expression re-evaluates reactively when
        //     tracks load and the content extent grows.
        _stickyAnimation = compositor.CreateExpressionAnimation(
            "Vector3(0, Clamp(scroll.Position.Y, 0, Max(scroll.Extent.Y - source.LeftColHeight, 0)), 0)");
        _stickyAnimation.SetReferenceParameter("scroll", PageScrollView.ExpressionAnimationSources);
        _stickyAnimation.SetReferenceParameter("source", _stickyProps);
        visual.StartAnimation("Translation", _stickyAnimation);

        // Keep MaxLag fresh as either side resizes.
        LeftColumnHost.SizeChanged += StickyAnchors_SizeChanged;
        PageScrollView.SizeChanged += StickyAnchors_SizeChanged;
    }

    private void DetachParallax()
    {
        if (!_parallaxAttached) return;
        _parallaxAttached = false;

        if (LeftColumnHost is not null)
            LeftColumnHost.SizeChanged -= StickyAnchors_SizeChanged;
        if (PageScrollView is not null)
            PageScrollView.SizeChanged -= StickyAnchors_SizeChanged;

        if (LeftColumnHost is not null)
        {
            var visual = ElementCompositionPreview.GetElementVisual(LeftColumnHost);
            visual.StopAnimation("Translation");
            // Reset Translation explicitly so the next attach starts at
            // origin — StopAnimation alone leaves the last computed value.
            ElementCompositionPreview.SetIsTranslationEnabled(LeftColumnHost, true);
            visual.Properties.InsertVector3("Translation", System.Numerics.Vector3.Zero);
        }

        _stickyAnimation?.Dispose();
        _stickyAnimation = null;
        _stickyProps?.Dispose();
        _stickyProps = null;
    }

    private void StickyAnchors_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_stickyProps is null || LeftColumnHost is null) return;
        _stickyProps.InsertScalar("LeftColHeight", (float)LeftColumnHost.ActualHeight);
    }

    // Retained for reference / future tuning. Not called by the expression
    // animation anymore — Extent and LeftColHeight are sourced reactively.
    private float ComputeMaxLagFloat()
    {
        if (LeftColumnHost is null || PageScrollView is null) return float.MaxValue / 2f;
        var columnHeight = LeftColumnHost.ActualHeight;
        var viewportHeight = PageScrollView.ActualHeight;
        // Column shorter than viewport: pin indefinitely (huge ceiling, the
        // user can never scroll far enough to hit it).
        if (columnHeight <= 0 || viewportHeight <= 0 || columnHeight <= viewportHeight)
            return float.MaxValue / 2f;
        return (float)(columnHeight - viewportHeight);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Loaded -= AlbumPage_Loaded;
        Unloaded -= AlbumPage_Unloaded;
        ActualThemeChanged -= OnActualThemeChanged;
        ViewModel.ContentChanged -= ViewModel_ContentChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        TrackGrid.Dispose();
        if (OtherVersionsFlyout != null)
            OtherVersionsFlyout.Items.Clear();
        (ViewModel as IDisposable)?.Dispose();
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private bool TryHandlePendingAlbumArtConnectedAnimation()
    {
        if (!ConnectedAnimationHelper.HasPendingAnimation(ConnectedAnimationHelper.AlbumArt))
            return false;

        // Skip the standard crossfade — connected animation paints content directly.
        PageController.MarkContentShownDirectly();

        using (Wavee.UI.WinUI.Services.UiOperationProfiler.Instance?.Profile("page.album.updateLayout"))
        {
            // Element-scoped: see PlaylistPage for the rationale. Lays out
            // the AlbumArtContainer's parent chain only so connected
            // animation can read its destination rect — siblings (track
            // list, more-by-artist, merch shelf) settle one frame later.
            AlbumArtContainer.UpdateLayout();
        }
        var started = ConnectedAnimationHelper.TryStartAnimation(
            ConnectedAnimationHelper.AlbumArt,
            AlbumArtContainer);

        _logger?.LogDebug(
            "[xfade][album:{Id}] connected.albumArt action={Action}",
            XfadeLog.Tag(ViewModel.AlbumId), started ? "started" : "miss");
        return true;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.album.onNavigatedTo");
        var incomingId = e.Parameter is ContentNavigationParameter nav ? nav.Uri
                       : e.Parameter as string;
        var sameId = !string.IsNullOrEmpty(incomingId) && string.Equals(incomingId, ViewModel.AlbumId, StringComparison.Ordinal);
        _logger?.LogDebug(
            "[xfade][album:{Id}] nav.to mode={Mode} incoming={Incoming} sameId={SameId}",
            XfadeLog.Tag(ViewModel.AlbumId), e.NavigationMode, XfadeLog.Tag(incomingId), sameId);
        base.OnNavigatedTo(e);
        LoadNewContent(e.Parameter);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.album.onNavigatedFrom");
        _logger?.LogDebug("[xfade][album:{Id}] nav.from", XfadeLog.Tag(ViewModel.AlbumId));
        base.OnNavigatedFrom(e);
        // Hibernate also releases FilteredTracks / MoreByArtist / AlternateReleases /
        // Merch — the bound collections that pin the most realized item containers
        // while this cached page sits invisible in the Frame cache. Activate's
        // _appliedDetailFor reset (cleared in Hibernate) makes the next subscribe
        // re-apply the warm AlbumStore value.
        TrimForNavigationCache();
    }

    public void TrimForNavigationCache()
    {
        if (_trimmedForNavigationCache)
            return;

        _trimmedForNavigationCache = true;
        _lastRestoredAlbumId = ViewModel.AlbumId;
        ViewModel.Hibernate();
        // Detach compiled x:Bind from VM.PropertyChanged so the BindingsTracking
        // sibling is no longer rooted by the (singleton-store-subscribed) VM —
        // without this the entire page tree is pinned across navigations.
        Bindings?.StopTracking();
    }

    public void RestoreFromNavigationCache()
    {
        if (!_trimmedForNavigationCache)
            return;

        _trimmedForNavigationCache = false;
        // Bindings.Update() re-evaluates every compiled x:Bind on the page. Skip
        // it when the user is returning to the same album they just left —
        // nothing in the binding graph actually changed in that case.
        var sameAlbum = !string.IsNullOrEmpty(_lastRestoredAlbumId)
            && string.Equals(_lastRestoredAlbumId, ViewModel.AlbumId, StringComparison.Ordinal);
        if (!sameAlbum)
        {
            using (Wavee.UI.WinUI.Services.UiOperationProfiler.Instance?.Profile("page.album.bindingsUpdate"))
            {
                Bindings?.Update();
            }
        }
        // ResetForNewLoad + ViewModel.Activate + TryShowContentNow used to run here
        // too — but OnNavigatedTo → LoadNewContent fires next on the same dispatch
        // with the authoritative parameter and does the same Activate. Running both
        // caused two ApplyDetail dispatches against AlbumStore and two waves of
        // TrackItem materialization per navigation.
    }

    // Same-tab navigation between two albums reuses this Page instance and never
    // fires OnNavigatedTo — TabBarItem.Navigate routes through this method instead.
    // Without this override, clicking a different album from the player bar / a
    // shelf / search while AlbumPage is the active tab content silently drops the
    // new parameter.
    public void RefreshWithParameter(object? parameter)
    {
        var incomingId = parameter is ContentNavigationParameter nav ? nav.Uri
                       : parameter as string;
        var sameId = !string.IsNullOrEmpty(incomingId) && string.Equals(incomingId, ViewModel.AlbumId, StringComparison.Ordinal);
        _logger?.LogDebug(
            "[xfade][album:{Id}] refresh incoming={Incoming} sameId={SameId}",
            XfadeLog.Tag(ViewModel.AlbumId), XfadeLog.Tag(incomingId), sameId);
        LoadNewContent(parameter);
    }

    private async void LoadNewContent(object? parameter)
    {
        _trimmedForNavigationCache = false;
        _logger?.LogDebug(
            "[xfade][album:{Id}] load.enter",
            XfadeLog.Tag(ViewModel.AlbumId));

        // Reset shimmer/content visual state for the fresh load.
        PageController.ResetForNewLoad();
        _footerRevealed = false;
        _footerRevealGeneration++;
        FooterShimmerGate.Reset(() => FooterShimmer, () => FooterContent);

        var hasPendingAlbumArtAnimation =
            ConnectedAnimationHelper.HasPendingAnimation(ConnectedAnimationHelper.AlbumArt);

        string? albumId = null;
        ContentNavigationParameter? connectedAnimationNav = null;

        if (parameter is ContentNavigationParameter nav)
        {
            albumId = nav.Uri;
            // Activate first so its new-album clear-down (in Initialize) runs BEFORE
            // PrefillFrom writes the nav values — otherwise the clear would wipe the
            // prefill and the cached page would keep showing the previous album's
            // header until the store push arrived. Same pattern as PlaylistPage.
            if (hasPendingAlbumArtAnimation)
            {
                // Keep the destination cover/title materialized so TryStart can run
                // before the VM seeds placeholders and subscribes to the store.
                connectedAnimationNav = nav;
                ViewModel.PrefillFrom(nav, clearMissing: true);
            }
            else
            {
                ViewModel.Activate(nav.Uri);
                ViewModel.PrefillFrom(nav);
            }
        }
        else if (parameter is string rawId && !string.IsNullOrWhiteSpace(rawId))
        {
            albumId = rawId;
            ViewModel.Activate(rawId);
        }

        if (!string.IsNullOrEmpty(albumId))
            RestoreAlbumPanelWidth(albumId);

        if (hasPendingAlbumArtAnimation && TryHandlePendingAlbumArtConnectedAnimation())
        {
            if (connectedAnimationNav is not null)
            {
                var uri = connectedAnimationNav.Uri;
                await Task.Yield();
                if (!PageController.IsNavigatingAway)
                    ViewModel.Activate(uri, preserveHeaderPrefill: true);
            }

            return;
        }

        // Warm-cache trigger. AlbumStore is a BehaviorSubject — Activate's subscribe
        // queues ApplyDetailState via the dispatcher, which runs after this method
        // returns. After one yield it has landed (AlbumName populated, IsLoading
        // stayed false), so TryShowContentNow can fire ScheduleCrossfade for the
        // same-id case where the IsLoading=false write was a no-op.
        if (connectedAnimationNav is not null)
            ViewModel.Activate(connectedAnimationNav.Uri, preserveHeaderPrefill: true);

        if (await SettleAlbumLayoutAsync())
        {
            PageController.TryShowContentNow();
            // Warm-cache footer trigger. AlbumStore can return Ready immediately
            // for an album the user has visited before — IsLoading / IsLoadingTracks
            // never flip during navigation, so ViewModel_PropertyChanged's
            // IsContentReady branch never fires and the footer would stay in its
            // freshly-Reset shimmer state forever. Kick the reveal here so the
            // same-id / warm-cache cases match the cold-load timing.
            if (ViewModel.IsContentReady)
                _ = TryRevealFooterAsync();
        }
    }

    // ── Transition settling ──────────────────────────────────────────────────

    private async Task ShowContentAfterAlbumLayoutSettlesAsync()
    {
        if (await SettleAlbumLayoutAsync())
            PageController.TryShowContentNow();
        if (ViewModel.IsContentReady)
            _ = TryRevealFooterAsync();
    }

    /// <summary>
    /// Fade the footer shimmer out and the real footer content in, matching the
    /// timing of the main shimmer→content crossfade. Idempotent across repeated
    /// flips of <see cref="AlbumViewModel.IsContentReady"/>, and guards against
    /// nav-away / fresh-load races via a per-call generation counter.
    /// </summary>
    private async Task TryRevealFooterAsync()
    {
        if (_isDisposed || PageController.IsNavigatingAway) return;
        if (_footerRevealed) return;

        var generation = ++_footerRevealGeneration;

        // Two Task.Yield()s + UpdateLayout matches SettleAlbumLayoutAsync's pattern:
        // gives XAML one frame to measure the freshly-bound footer subtree
        // before the composition crossfade starts, so neither shimmer nor real
        // content snaps a layout pass mid-animation.
        await Task.Yield();
        if (_isDisposed || PageController.IsNavigatingAway || generation != _footerRevealGeneration)
            return;

        FooterShimmer?.UpdateLayout();
        FooterContent?.UpdateLayout();
        await Task.Yield();
        if (_isDisposed || PageController.IsNavigatingAway || generation != _footerRevealGeneration)
            return;

        // FooterContent is x:Name'd (not x:Load gated), so it should be realised
        // whenever the page tree is up. Defensive null-check covers the edge
        // case where IsContentReady arrives before the framework has wired the
        // named field on a freshly-constructed page.
        var content = FooterContent;
        if (content is null) return;

        _footerRevealed = true;
        _logger?.LogDebug(
            "[xfade][album:{Id}] footer.xfade.start shimmer={ShimmerKnown}",
            XfadeLog.Tag(ViewModel.AlbumId), FooterShimmer is not null);

        await FooterShimmerGate.RunCrossfadeAsync(
            FooterShimmer, content,
            continuePredicate: () =>
                _footerRevealed &&
                !_isDisposed &&
                !PageController.IsNavigatingAway &&
                generation == _footerRevealGeneration);
    }

    private async Task<bool> SettleAlbumLayoutAsync()
    {
        var generation = ++_layoutSettlingGeneration;
        await Task.Yield();
        if (_isDisposed ||
            PageController.IsNavigatingAway ||
            generation != _layoutSettlingGeneration)
        {
            return false;
        }

        ShimmerContainer?.UpdateLayout();
        AlbumArtContainer?.UpdateLayout();
        TrackGrid?.UpdateLayout();
        ContentContainer?.UpdateLayout();

        await Task.Yield();
        return !_isDisposed &&
               !PageController.IsNavigatingAway &&
               generation == _layoutSettlingGeneration;
    }

    // ── Left-panel sizing ────────────────────────────────────────────────────

    private void RestoreAlbumPanelWidth(string albumId)
    {
        const double defaultWidth = 280;
        var key = $"album:{albumId}";

        var width = _settings.Settings.PanelWidths.TryGetValue(key, out var saved)
            ? saved
            : defaultWidth;

        width = Math.Clamp(width, 200, 500);
        LeftPanelColumn.Width = new GridLength(width, GridUnitType.Pixel);
        // Shimmer cover height is wired via AlbumArtShimmerContainer_SizeChanged
        // (mirrors AlbumArtContainer's Height = ActualWidth so the square stays
        // in sync with the splitter — no manual width-24 fudge needed here).
    }

    private void AlbumSplitter_ResizeCompleted(object? sender, GridSplitterResizeCompletedEventArgs e)
    {
        var albumId = ViewModel.AlbumId;
        if (string.IsNullOrEmpty(albumId)) return;

        _settings.Update(s => s.PanelWidths[$"album:{albumId}"] = e.NewWidth);
    }

    // Keep the cover square as the splitter resizes the left column.
    private void AlbumArtContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border border || e.NewSize.Width <= 0) return;
        var target = e.NewSize.Width;
        // Suppress redundant assignments — every measure pass fires SizeChanged
        // and re-assigning Height re-enters the layout queue, pulsing the cover
        // during the loading→content transition.
        if (Math.Abs(border.Height - target) < 0.5) return;
        border.Height = target;
    }

    // Same square-as-it-grows treatment for the shimmer cover so the loading
    // silhouette matches the real cover 1:1 even when the splitter is dragged
    // mid-load. Without this, dragging the splitter while the shimmer is on
    // screen leaves the shimmer rectangle at its first-paint height while the
    // real cover behind it tracks the new width, and the crossfade reveals a
    // height jump.
    private void AlbumArtShimmerContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement element || e.NewSize.Width <= 0) return;
        var target = e.NewSize.Width;
        if (Math.Abs(element.Height - target) < 0.5) return;
        element.Height = target;
    }

    // ── Other versions flyout ───────────────────────────────────────────────

    private void RebuildOtherVersionsFlyout()
    {
        if (OtherVersionsFlyout == null) return;
        OtherVersionsFlyout.Items.Clear();

        foreach (var release in ViewModel.AlternateReleases)
        {
            if (string.IsNullOrEmpty(release.Uri)) continue;

            var label = string.IsNullOrEmpty(release.Name)
                ? FormatType(release.Type)
                : release.Name;
            if (release.Year > 0)
                label = $"{label} · {release.Year}";

            var item = new MenuFlyoutItem { Text = label, Tag = release };
            item.Click += OtherVersion_Click;
            OtherVersionsFlyout.Items.Add(item);
        }
    }

    private static string FormatType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return "Edition";
        // "ALBUM" → "Album", "EP" stays uppercase per Spotify convention.
        if (type.Equals("EP", StringComparison.OrdinalIgnoreCase)) return "EP";
        return char.ToUpperInvariant(type[0]) + type[1..].ToLowerInvariant();
    }

    private void OtherVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not AlbumAlternateReleaseResult release)
            return;

        var targetUri = release.Uri ?? release.Id;
        if (string.IsNullOrWhiteSpace(targetUri)) return;

        var param = new ContentNavigationParameter
        {
            Uri = targetUri,
            Title = release.Name,
            ImageUrl = release.CoverArtUrl
        };
        OpenAlbumAfterCurrentEvent(param, release.Name ?? "Album", NavigationHelpers.IsCtrlPressed());
    }

    // ── Click handlers ───────────────────────────────────────────────────────

    /// <summary>
    /// Opens the all-artists Flyout attached to the AvatarStack so users can
    /// reach every distinct artist on the album — including track-only featureds
    /// not in the album billing.
    /// </summary>
    private void ArtistsAvatarStack_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            FlyoutBase.ShowAttachedFlyout(fe);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Navigate to the clicked artist from the all-artists flyout list, then
    /// dismiss the flyout so the user lands on ArtistPage cleanly.
    /// </summary>
    private void ArtistsFlyoutList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AlbumArtistResult artist) return;
        var uri = artist.Uri;
        var id = artist.Id;
        var target = !string.IsNullOrEmpty(uri) ? uri
                   : !string.IsNullOrEmpty(id) ? id
                   : null;
        if (string.IsNullOrEmpty(target)) return;

        var openInNewTab = NavigationHelpers.IsCtrlPressed();
        NavigationHelpers.OpenArtist(target, artist.Name ?? "Artist", openInNewTab);
        ArtistsFlyout.Hide();
    }

    private void RelatedAlbum_Click(object sender, EventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        var album = fe.Tag as AlbumRelatedResult ?? fe.DataContext as AlbumRelatedResult;
        if (album != null)
        {
            var targetUri = album.Uri ?? album.Id;
            if (string.IsNullOrWhiteSpace(targetUri)) return;

            var param = new ContentNavigationParameter
            {
                Uri = targetUri,
                Title = album.Name,
                ImageUrl = album.ImageUrl
            };
            OpenAlbumAfterCurrentEvent(param, album.Name ?? "Album", NavigationHelpers.IsCtrlPressed());
            return;
        }

        if (sender is Controls.Cards.ContentCard card && !string.IsNullOrWhiteSpace(card.NavigationUri))
        {
            var param = new ContentNavigationParameter
            {
                Uri = card.NavigationUri,
                Title = card.Title,
                ImageUrl = card.ImageUrl
            };
            OpenAlbumAfterCurrentEvent(param, card.Title ?? "Album", NavigationHelpers.IsCtrlPressed());
        }
    }

    private void OpenAlbumAfterCurrentEvent(ContentNavigationParameter parameter, string title, bool openInNewTab)
    {
        if (!openInNewTab && DispatcherQueue is not null)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isDisposed)
                    NavigationHelpers.OpenAlbum(parameter, title, openInNewTab: false);
            });
            return;
        }

        NavigationHelpers.OpenAlbum(parameter, title, openInNewTab);
    }

    private void MerchItem_Click(object sender, EventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AlbumMerchItemResult merch
            && !string.IsNullOrEmpty(merch.ShopUrl))
        {
            _ = ViewModel.OpenMerchItemCommand.ExecuteAsync(merch.ShopUrl);
        }
    }

    private void Share_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ViewModel.ShareUrl)) return;
        ViewModel.ShareCommand.Execute(null);
        _notificationService?.Show(
            "Album link copied",
            NotificationSeverity.Success,
            TimeSpan.FromSeconds(3));
    }

    private void MusicVideoStrip_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // Start album playback. The PlayerBarViewModel's track-changed
        // auto-switch picks up the new track and routes it to the video
        // surface when the user has the "auto-switch to video" preference
        // enabled. Otherwise the Watch-Video button on the player bar lights
        // up because IsCurrentTrackVideoCapable flips true once the player
        // is positioned on a track with videoAssociations.
        if (string.IsNullOrEmpty(ViewModel.MusicVideoUri)) return;
        ViewModel.PlayAlbumCommand.Execute(null);
        e.Handled = true;
    }

    // ── MusicVideoStrip hover/press affordance ─────────────────────────────
    // Direct property assignment instead of VSM: Border doesn't host VSGs in
    // its visual tree, and ContentControl's default ContentPresenter prevents
    // GoToState's one-level child walk from reaching state groups placed on
    // a nested Grid. Instant property snaps look fine for a card hover (same
    // as Windows Photos cards). The play-button scale uses a Storyboard so
    // it feels mechanical, not jumpy.

    private Microsoft.UI.Xaml.Media.Brush? _mvsNormalBg;
    private Microsoft.UI.Xaml.Media.Brush? _mvsHoverBg;
    private Microsoft.UI.Xaml.Media.Brush? _mvsPressedBg;
    private Microsoft.UI.Xaml.Media.Brush? _mvsNormalBorder;
    private Microsoft.UI.Xaml.Media.Brush? _mvsHoverBorder;

    private void EnsureMusicVideoStripBrushesCached()
    {
        if (_mvsNormalBg is not null) return;
        var res = Application.Current.Resources;
        _mvsNormalBg = (Microsoft.UI.Xaml.Media.Brush)res["CardBackgroundFillColorDefaultBrush"];
        _mvsHoverBg = (Microsoft.UI.Xaml.Media.Brush)res["CardBackgroundFillColorSecondaryBrush"];
        _mvsPressedBg = (Microsoft.UI.Xaml.Media.Brush)res["ControlFillColorTertiaryBrush"];
        _mvsNormalBorder = (Microsoft.UI.Xaml.Media.Brush)res["CardStrokeColorDefaultBrush"];
        _mvsHoverBorder = (Microsoft.UI.Xaml.Media.Brush)res["ControlStrokeColorSecondaryBrush"];
    }

    private void AnimateMusicVideoStripPlayScale(double target)
    {
        if (MusicVideoStripPlayScale is null) return;
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var xAnim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
            }
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(xAnim, MusicVideoStripPlayScale);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(xAnim, "ScaleX");
        var yAnim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
            }
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(yAnim, MusicVideoStripPlayScale);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(yAnim, "ScaleY");
        sb.Children.Add(xAnim);
        sb.Children.Add(yAnim);
        sb.Begin();
    }

    private void MusicVideoStrip_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        EnsureMusicVideoStripBrushesCached();
        MusicVideoStrip.Background = _mvsHoverBg;
        MusicVideoStrip.BorderBrush = _mvsHoverBorder;
        MusicVideoStripDarkenOverlay.Opacity = 0.18;
        AnimateMusicVideoStripPlayScale(1.08);
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
    }

    private void MusicVideoStrip_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        EnsureMusicVideoStripBrushesCached();
        MusicVideoStrip.Background = _mvsNormalBg;
        MusicVideoStrip.BorderBrush = _mvsNormalBorder;
        MusicVideoStripDarkenOverlay.Opacity = 0.32;
        AnimateMusicVideoStripPlayScale(1.0);
        ProtectedCursor = null;
    }

    private void MusicVideoStrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        EnsureMusicVideoStripBrushesCached();
        MusicVideoStrip.Background = _mvsPressedBg;
        MusicVideoStripDarkenOverlay.Opacity = 0.24;
        AnimateMusicVideoStripPlayScale(0.96);
    }

    private void MusicVideoStrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EnsureMusicVideoStripBrushesCached();
        MusicVideoStrip.Background = _mvsHoverBg;
        MusicVideoStripDarkenOverlay.Opacity = 0.18;
        AnimateMusicVideoStripPlayScale(1.08);
    }

    private void ArtistsStackButton_Loaded(object sender, RoutedEventArgs e)
    {
        // Hand-cursor affordance on hover so the "click to expand artists"
        // affordance reads as interactive — without it the chevron alone is
        // easy to miss next to the avatar stack.
        if (sender is ClickableBorder cb) cb.ShowHandCursor();
    }

    private void AddToPlaylistMenuFlyout_Opening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;
        // Rebuild on every open — user playlists may have been created /
        // renamed / deleted while the page sat in the navigation cache.
        flyout.Items.Clear();
        foreach (var playlist in ViewModel.Playlists)
        {
            var captured = playlist;
            var mi = new MenuFlyoutItem
            {
                Text = playlist.Name ?? "Untitled playlist",
                Tag = playlist
            };
            mi.Click += (s, args) =>
            {
                _ = ViewModel.AddAlbumToPlaylistCommand.ExecuteAsync(captured);
                _notificationService?.Show(
                    $"Added to {captured.Name ?? "playlist"}",
                    NotificationSeverity.Success,
                    TimeSpan.FromSeconds(3));
            };
            flyout.Items.Add(mi);
        }
        if (flyout.Items.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "No playlists yet",
                IsEnabled = false
            });
        }
    }

    private void MerchCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AlbumMerchItemResult merch
            && !string.IsNullOrEmpty(merch.ShopUrl))
        {
            _ = ViewModel.OpenMerchItemCommand.ExecuteAsync(merch.ShopUrl);
        }
    }
}
