using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.WinUI.Controls.Ai;
using Wavee.UI.WinUI.Controls.InPageFilter;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.WinUI.Controls.Common;
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.Controls.ContextMenu.Builders;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Diagnostics;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class AlbumPage : UserControl, ITabBarItemContent, IPageHostAware, IHibernatingPage, IDisposable, IContentPageHost, IInPageFilterable
{
    // ── IInPageFilterable ───────────────────────────────────────────────
    string IInPageFilterable.FilterQuery
    {
        get => ViewModel?.SearchQuery ?? string.Empty;
        set { if (ViewModel is { } vm) vm.SearchQuery = value ?? string.Empty; }
    }
    string IInPageFilterable.FilterPlaceholder => "Filter tracks…";
    bool IInPageFilterable.CanFilter => ViewModel is not null;

    private readonly ILogger? _logger;
    private readonly INotificationService? _notificationService;
    private readonly ISettingsService _settings;
    private bool _isDisposed;
    private int _layoutSettlingGeneration;
    private int _scrollResetGeneration;

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
            item is ViewModels.LazyTrackItem lazy && lazy.Data is Wavee.UI.Models.AlbumTrackDto dto
                ? dto.PlayCountFormatted
                : "";
        TrackGrid.PopularityBadgeSelector = ViewModel.IsPopularTrack;

        // Floating multi-select command bar — observes TrackGrid's selection.
        // Albums deliberately don't wire MultiSelectRemoveCommand, so the bar's
        // Remove action stays hidden here.
        SelectionBar.Attach(TrackGrid);

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
        else if (e.PropertyName == nameof(AlbumViewModel.AlternateReleases)
                 || e.PropertyName == nameof(AlbumViewModel.HasAlternateReleases))
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

    }

    private void AlbumPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // Under NavigationCacheMode=Enabled the Page may be reused across N
        // navigations until LRU eviction. Keep the ctor's PropertyChanged
        // subscription attached for the page's lifetime — unhooking here would
        // leave the cached page deaf to the next IsLoading=false transition.
        PageController.IsNavigatingAway = true;
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
        SelectionBar.Detach();
        TrackGrid.Dispose();
        if (OtherVersionsFlyout != null)
            OtherVersionsFlyout.Items.Clear();
        (ViewModel as IDisposable)?.Dispose();
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    public void OnEntered(object? parameter, PageHostNavigationMode mode)
    {
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.album.onEntered");
        var incomingNav = parameter as ContentNavigationParameter;
        var incomingId = incomingNav?.Uri ?? parameter as string;
        var sameId = !string.IsNullOrEmpty(incomingId) && string.Equals(incomingId, ViewModel.AlbumId, StringComparison.Ordinal);
        _logger?.LogDebug(
            "[xfade][album:{Id}] nav.to mode={Mode} incoming={Incoming} sameId={SameId}",
            XfadeLog.Tag(ViewModel.AlbumId), mode, XfadeLog.Tag(incomingId), sameId);
        LoadNewContent(parameter, mode);
    }

    public void OnLeaving()
    {
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.album.onLeaving");
        _logger?.LogDebug("[xfade][album:{Id}] nav.from", XfadeLog.Tag(ViewModel.AlbumId));
        // Off-screen quiet-down is driven by PageHost's hot-window residency tiering
        // (see IHibernatingPage / PageHost.ApplyResidencyTiers): the 2 most-recent
        // collapsed pages stay live for instant back/forward, anything older is
        // hibernated. Nothing to do synchronously on leave.
    }

    // ── IHibernatingPage ───────────────────────────────────────────────────────

    public void Hibernate()
    {
        _logger?.LogDebug("[xfade][album:{Id}] hibernate", XfadeLog.Tag(ViewModel.AlbumId));
        ViewModel.Hibernate();
        // Detach the compiled x:Bind store from VM.PropertyChanged so the idle
        // off-screen page stops re-evaluating bindings (incl. theme / scalar props
        // the VM still raises). Rehydrate() re-evaluates + re-tracks.
        Bindings?.StopTracking();
    }

    public void Rehydrate()
    {
        // Called by PageHost just before OnEntered on a previously-idled page.
        // Re-evaluate + resume x:Bind tracking, then the normal OnEntered →
        // LoadNewContent → ViewModel.Activate path re-subscribes to AlbumStore
        // (replays + repopulates the grid; the track skeleton shows meanwhile
        // because Hibernate left IsLoadingTracks true).
        _logger?.LogDebug("[xfade][album:{Id}] rehydrate", XfadeLog.Tag(ViewModel.AlbumId));
        Bindings?.Update();
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
        LoadNewContent(parameter, PageHostNavigationMode.Refresh);
    }

    private async void LoadNewContent(object? parameter, PageHostNavigationMode mode = PageHostNavigationMode.New)
    {
        _logger?.LogDebug(
            "[xfade][album:{Id}] load.enter",
            XfadeLog.Tag(ViewModel.AlbumId));

        PageController.IsNavigatingAway = false;

        // Reveal mode:
        //  • Back/Forward — keep already-rendered pixels and scroll state.
        //  • Same-tab refresh with content already showing + usable prefill —
        //    warm content cross-fade (no skeleton flashed over structurally-
        //    identical pixels; see ContentPageController.CrossfadeContentSwap).
        //  • New / cold — full shimmer-gated reveal.
        var navPrefill = parameter as ContentNavigationParameter;
        var useWarmSwap =
            mode == PageHostNavigationMode.Refresh &&
            PageController.IsShowingContent &&
            navPrefill is not null &&
            (!string.IsNullOrEmpty(navPrefill.Title) || !string.IsNullOrEmpty(navPrefill.ImageUrl));

        if (mode != PageHostNavigationMode.Back && mode != PageHostNavigationMode.Forward)
        {
            if (useWarmSwap)
            {
                PageController.CrossfadeContentSwap();
                ResetScrollPositionForNavigation();
            }
            else
            {
                PageController.ResetForNewLoad();
                ResetScrollPositionForNavigation();
                _footerRevealed = false;
                _footerRevealGeneration++;
                // FooterContent stays visible; per-section SectionStaggerEntrance owns
                // the reveal. Re-arm the (collapsed) legacy shimmer gate only — don't
                // zero FooterContent's opacity (that would flatten the staggered sections).
                FooterShimmerGate.Reset(() => null, () => null, FrameworkLayer.Xaml);
            }
        }

        string? albumId = null;

        if (parameter is ContentNavigationParameter nav)
        {
            albumId = nav.Uri;
            // Activate first so its new-album clear-down (in Initialize) runs BEFORE
            // PrefillFrom writes the nav values — otherwise the clear would wipe the
            // prefill and the cached page would keep showing the previous album's
            // header until the store push arrived. Same pattern as PlaylistPage.
            ViewModel.Activate(nav.Uri);
            // clearMissing: true → if nav lacks ImageUrl/Subtitle/TotalTracks
            // those fields go null/empty rather than keeping the previous
            // album's values. Prevents stale-cover-with-new-tracks bleed-through
            // when navigating between two albums whose source cards don't
            // carry every prefill field.
            ViewModel.PrefillFrom(nav, clearMissing: true);
        }
        else if (parameter is string rawId && !string.IsNullOrWhiteSpace(rawId))
        {
            albumId = rawId;
            ViewModel.Activate(rawId);
        }

        if (!string.IsNullOrEmpty(albumId))
            RestoreAlbumPanelWidth(albumId);

        // Warm-cache trigger. AlbumStore is a BehaviorSubject — Activate's subscribe
        // queues ApplyDetailState via the dispatcher, which runs after this method
        // returns. After one yield it has landed (AlbumName populated, IsLoading
        // stayed false), so TryShowContentNow can fire ScheduleCrossfade for the
        // same-id case where the IsLoading=false write was a no-op. On a warm swap
        // CrossfadeContentSwap already marked content shown, so TryShowContentNow
        // early-returns and only the footer reveal runs.
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

    private void ResetScrollPositionForNavigation()
    {
        var generation = ++_scrollResetGeneration;
        _ = ResetScrollPositionAfterLayoutAsync(generation);
    }

    private async Task ResetScrollPositionAfterLayoutAsync(int generation)
    {
        await Task.Yield();
        if (_isDisposed || PageController.IsNavigatingAway || generation != _scrollResetGeneration)
            return;

        TryScrollToTop("dispatcher");

        await Task.Yield();
        if (_isDisposed || PageController.IsNavigatingAway || generation != _scrollResetGeneration)
            return;

        TryScrollToTop("layout");
    }

    private void TryScrollToTop(string reason)
    {
        try
        {
            LeftColumnScrollView?.ScrollTo(
                0, 0,
                new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
            TrackGrid?.ScrollRowsToTop();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "AlbumPage scroll-to-top during {Reason} failed.", reason);
        }
    }

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

        // Let XAML measure the freshly-bound footer subtree on a natural frame
        // before the composition crossfade starts. Avoid forcing UpdateLayout:
        // this footer can contain shelves and image hosts, so a sync layout
        // walk shows up as a navigation stall.
        await Task.Yield();
        if (_isDisposed || PageController.IsNavigatingAway || generation != _footerRevealGeneration)
            return;

        FooterShimmer?.InvalidateMeasure();
        FooterContent?.InvalidateMeasure();
        await Task.Delay(16).ConfigureAwait(true);
        if (_isDisposed || PageController.IsNavigatingAway || generation != _footerRevealGeneration)
            return;

        // FooterContent is x:Name'd (not x:Load gated), so it should be realised
        // whenever the page tree is up. Defensive null-check covers the edge
        // case where IsContentReady arrives before the framework has wired the
        // named field on a freshly-constructed page.
        if (FooterContent is null) return;

        _footerRevealed = true;
        _logger?.LogDebug(
            "[xfade][album:{Id}] footer.reveal — per-section staggered entrance owns the reveal",
            XfadeLog.Tag(ViewModel.AlbumId));

        // FooterContent stays visible; SectionStaggerEntrance fades each section in
        // as it scrolls into view, so there is no block crossfade to flatten the
        // stagger. Release the (collapsed) legacy shimmer subtree after a beat so its
        // x:Load peers free.
        await Task.Delay(250).ConfigureAwait(true);
        if (_footerRevealed && !_isDisposed && !PageController.IsNavigatingAway &&
            generation == _footerRevealGeneration)
        {
            FooterShimmerGate.IsLoaded = false;
        }
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

        ShimmerContainer?.InvalidateMeasure();
        AlbumArtContainer?.InvalidateMeasure();
        TrackGrid?.InvalidateMeasure();
        ContentContainer?.InvalidateMeasure();

        await Task.Delay(16).ConfigureAwait(true);
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

    // ── Cover viewer (click cover → zoomable overlay with Save as…) ──
    private static readonly Microsoft.UI.Input.InputCursor s_coverHandCursor =
        Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);

    private async void AlbumArtContainer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        await ImageZoomDialog.ShowAsync(XamlRoot, ViewModel.AlbumImageUrl, ViewModel.AlbumName, ViewModel.AlbumName);
    }

    private void AlbumArtContainer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement el) el.ChangeCursor(s_coverHandCursor);
        if (AlbumArtHoverOverlay is not null)
            AnimationBuilder.Create()
                .Opacity(to: 1, duration: TimeSpan.FromMilliseconds(140))
                .Start(AlbumArtHoverOverlay);
    }

    private void AlbumArtContainer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement el) el.ChangeCursor(null);
        if (AlbumArtHoverOverlay is not null)
            AnimationBuilder.Create()
                .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(140))
                .Start(AlbumArtHoverOverlay);
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

    private async void AddToPlaylistPillButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var mediator = Ioc.Default.GetService<IPlaylistDragDropMediator>();
        var albumId = ViewModel.AlbumId;
        if (mediator is null || string.IsNullOrEmpty(albumId)) return;

        // Same folder-aware menu the row / card / playlist-hero menus use: nests
        // folders, owned playlists only, "Create new playlist". The album's track
        // URIs resolve lazily through the shared mediator (album → track URIs).
        var albumUri = albumId.StartsWith("spotify:album:", StringComparison.Ordinal)
            ? albumId
            : $"spotify:album:{albumId}";
        var loader = AddToPlaylistSubmenuBuilder.Loader(
            sourceLabel: ViewModel.AlbumName,
            trackUrisLoader: ct => mediator.GetAlbumTrackUrisAsync(albumUri, ct));
        var items = await loader();
        ContextMenuHost.Show(fe, items);
    }

    private void MerchCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AlbumMerchItemResult merch
            && !string.IsNullOrEmpty(merch.ShopUrl))
        {
            _ = ViewModel.OpenMerchItemCommand.ExecuteAsync(merch.ShopUrl);
        }
    }

    // ── "About this album" AI text-card linkifier ──────────────────────────
    // Post-process the streamed Phi Silica paragraph: scan for any title from
    // the album's loaded tracklist and replace those plain runs with clickable
    // Hyperlink runs that start playback. Mirrors ArtistPage's bio linkifier
    // but scopes its token set to the current album's tracks only.

    private void OnAlbumBioRevealCompleted(object sender, RevealCompletedEventArgs e)
    {
        if (sender is not AiTextCard card)
            return;

        card.BodyInlines.Clear();
        var text = (e.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
            return;

        AppendAlbumBioRuns(card.BodyInlines, text, BuildAlbumBioTokens());
    }

    private IReadOnlyList<AlbumBioInlineToken> BuildAlbumBioTokens()
    {
        var tokens = new List<AlbumBioInlineToken>(32);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lazy in ViewModel.FilteredTracks)
        {
            if (lazy is { IsLoaded: true, Data: AlbumTrackDto t } && !string.IsNullOrWhiteSpace(t.Title))
            {
                var title = t.Title!.Trim();
                if (title.Length < 2 || !seen.Add(title))
                    continue;

                tokens.Add(new AlbumBioInlineToken(title, t.Uri));
                if (tokens.Count >= 50)
                    break;
            }
        }

        // Longest-first so multi-word titles like "Track Two" win over "Track".
        tokens.Sort((a, b) => b.Text.Length.CompareTo(a.Text.Length));
        return tokens;
    }

    private void AppendAlbumBioRuns(InlineCollection target, string text, IReadOnlyList<AlbumBioInlineToken> tokens)
    {
        var i = 0;
        while (i < text.Length)
        {
            var matchStart = -1;
            AlbumBioInlineToken? matchValue = null;
            foreach (var token in tokens)
            {
                if (token.Text.Length == 0) continue;
                var idx = text.IndexOf(token.Text, i, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (matchStart < 0 || idx < matchStart))
                {
                    matchStart = idx;
                    matchValue = token;
                }
            }

            if (matchStart < 0 || matchValue is null)
            {
                if (i < text.Length)
                    target.Add(new Run { Text = text[i..] });
                break;
            }

            if (matchStart > i)
                target.Add(new Run { Text = text[i..matchStart] });

            var matchedText = text.Substring(matchStart, matchValue.Text.Length);
            target.Add(CreateAlbumBioInline(matchValue, matchedText));
            i = matchStart + matchValue.Text.Length;
        }
    }

    private Inline CreateAlbumBioInline(AlbumBioInlineToken token, string text)
    {
        var accentBrush = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        if (string.IsNullOrWhiteSpace(token.Uri))
        {
            return new Run
            {
                Text = text,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = accentBrush,
            };
        }

        var link = new Hyperlink
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = accentBrush,
        };
        link.Inlines.Add(new Run { Text = text });
        link.Click += async (_, _) => await PlayAlbumBioTrackAsync(token);
        return link;
    }

    private async Task PlayAlbumBioTrackAsync(AlbumBioInlineToken token)
    {
        if (string.IsNullOrWhiteSpace(token.Uri))
            return;

        var playback = Ioc.Default.GetService<IPlaybackService>();
        if (playback is null)
            return;

        var albumId = ViewModel.AlbumId;
        var albumName = ViewModel.AlbumName;
        var albumImage = ViewModel.AlbumImageUrl;

        var result = !string.IsNullOrWhiteSpace(albumId)
            ? await playback.PlayTrackInContextAsync(
                token.Uri!,
                albumId!,
                new PlayContextOptions { PlayOriginFeature = "album_ai_bio" })
            : await playback.PlayTracksAsync(
                [token.Uri!],
                context: new PlaybackContextInfo
                {
                    ContextUri = token.Uri!,
                    Type = PlaybackContextType.Album,
                    Name = albumName ?? "Album AI",
                    ImageUrl = albumImage,
                });

        if (!result.IsSuccess)
            _logger?.LogWarning("Album AI bio track play failed: {Error}", result.ErrorMessage);
    }

    private sealed record AlbumBioInlineToken(string Text, string? Uri);
}
