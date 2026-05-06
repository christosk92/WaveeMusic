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

public sealed partial class AlbumPage : Page, ITabBarItemContent, INavigationCacheMemoryParticipant, IDisposable
{
    private readonly ILogger? _logger;
    private readonly INotificationService? _notificationService;
    private readonly ISettingsService _settings;
    private bool _showingContent;
    private bool _crossfadeScheduled;
    private bool _isNavigatingAway;
    private bool _isDisposed;
    private bool _trimmedForNavigationCache;

    public AlbumViewModel ViewModel { get; }

    public ShimmerLoadGate ShimmerGate { get; } = new();

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public AlbumPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<AlbumViewModel>();
        _logger = Ioc.Default.GetService<ILogger<AlbumPage>>();
        _notificationService = Ioc.Default.GetService<INotificationService>();
        _settings = Ioc.Default.GetRequiredService<ISettingsService>();
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
            string action;
            if (ViewModel.IsLoading) action = "skip-still-loading";
            else if (_showingContent) action = "skip-already-shown";
            else if (_crossfadeScheduled) action = "skip-already-scheduled";
            else action = "schedule";
            _logger?.LogDebug(
                "[xfade][album:{Id}] propchg.isLoading val={Val} showing={Showing} scheduled={Scheduled} action={Action}",
                XfadeLog.Tag(ViewModel.AlbumId), ViewModel.IsLoading, _showingContent, _crossfadeScheduled, action);
            if (action == "schedule")
                ScheduleCrossfade();
        }
        else if (e.PropertyName == nameof(AlbumViewModel.AlternateReleases))
        {
            RebuildOtherVersionsFlyout();
        }
        else if (e.PropertyName == nameof(AlbumViewModel.HeaderArtistLinks))
        {
            RebuildHeaderArtistsText();
        }
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
        _logger?.LogDebug(
            "[xfade][album:{Id}] loaded showing={Showing} scheduled={Scheduled} navAway={NavAway}",
            XfadeLog.Tag(ViewModel.AlbumId), _showingContent, _crossfadeScheduled, _isNavigatingAway);
        // Re-emit the inline header names from the current VM state — covers the
        // warm-cache navigation path where ApplyDetail runs before the page is
        // fully constructed and PropertyChanged finds HeaderArtistsText null.
        RebuildHeaderArtistsText();

    }

    private void AlbumPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _logger?.LogDebug(
            "[xfade][album:{Id}] unloaded showing={Showing} scheduled={Scheduled}",
            XfadeLog.Tag(ViewModel.AlbumId), _showingContent, _crossfadeScheduled);
        // Under NavigationCacheMode=Required the Page is reused across N
        // navigations and the (singleton-via-cache) VM lives with it — keep the
        // constructor's PropertyChanged subscription attached for the page's
        // lifetime. Unhooking here would leave the cached page deaf to the next
        // IsLoading=false transition on cold cache, stuck on shimmer.
        _isNavigatingAway = true;
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

    // ── Crossfade ──
    // Mirrors ProfilePage / ArtistPage: yield twice so XAML has time to
    // propagate bindings + measure the inner panels before the fade starts;
    // collapse shimmer once it has fully faded out so it stops participating
    // in measure/arrange.

    private async void ScheduleCrossfade()
    {
        _logger?.LogDebug(
            "[xfade][album:{Id}] schedule.enter showing={Showing} scheduled={Scheduled} navAway={NavAway} isLoading={IsLoading}",
            XfadeLog.Tag(ViewModel.AlbumId), _showingContent, _crossfadeScheduled, _isNavigatingAway, ViewModel.IsLoading);
        _crossfadeScheduled = true;
        await Task.Yield();
        await Task.Delay(16);
        var bail = _isNavigatingAway || _showingContent;
        _logger?.LogDebug(
            "[xfade][album:{Id}] schedule.gate navAway={NavAway} showing={Showing} action={Action}",
            XfadeLog.Tag(ViewModel.AlbumId), _isNavigatingAway, _showingContent, bail ? "bail" : "run");
        if (bail) return;
        CrossfadeToContent();
    }

    // Warm-cache fallback for the same-id re-navigation path. AlbumViewModel.Initialize
    // (line 538) early-exits when the incoming AlbumId matches the current one, so
    // IsLoading stays at its previous value (false) and ApplyDetailAsync's
    // IsLoading = false write is a no-op that fires no PropertyChanged. Without this
    // helper, the IsLoading-driven ScheduleCrossfade in ViewModel_PropertyChanged
    // never runs and the page paints empty (shimmer composition opacity stuck at 0
    // from the prior fade, content composition opacity at 0 from the reset).
    // Confirmed by [xfade] capture: state.ready freshness=Fresh + two
    // propset.isLoading old=false new=false changed=false events, schedule.enter
    // never observed.
    private void TryShowContentNow()
    {
        if (_showingContent || _crossfadeScheduled || ViewModel.IsLoading || string.IsNullOrEmpty(ViewModel.AlbumName))
            return;
        ScheduleCrossfade();
    }

    private async void CrossfadeToContent()
    {
        if (_showingContent) return;
        _showingContent = true;

        if (ShimmerContainer is not null)
        {
            var shimmerVisualOpacity = ElementCompositionPreview.GetElementVisual(ShimmerContainer).Opacity;
            var contentVisualOpacity = ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity;
            _logger?.LogDebug(
                "[xfade][album:{Id}] xfade.start shimmerXaml={ShimmerXaml} shimmerVisual={ShimmerVisual} contentVisual={ContentVisual} shimmerVisible={ShimmerVisible}",
                XfadeLog.Tag(ViewModel.AlbumId), ShimmerContainer.Opacity, shimmerVisualOpacity, contentVisualOpacity, ShimmerContainer.Visibility);
        }

        await ShimmerGate.RunCrossfadeAsync(ShimmerContainer, ContentContainer,
            continuePredicate: () => _showingContent);
        if (_showingContent)
        {
            _logger?.LogDebug(
                "[xfade][album:{Id}] xfade.shimmerCollapsed contentVisual={ContentVisual}",
                XfadeLog.Tag(ViewModel.AlbumId), ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity);
        }
    }

    private void ResetCrossfadeForNewLoad()
    {
        // Clear the navigation-away gate — Unloaded sets _isNavigatingAway = true
        // and Frame caches the page instance, so on re-attach the gate is stale
        // and ScheduleCrossfade's post-yield guard bails on every load.
        // Confirmed by [xfade] capture: load.enter and reset both reported
        // navAway=true on a same-id Frame.Navigate to a cached AlbumPage.
        _isNavigatingAway = false;
        _showingContent = false;
        _crossfadeScheduled = false;
        ShimmerGate.Reset(() => ShimmerContainer, () => ContentContainer);
        var shimmerVisualOpacity = ElementCompositionPreview.GetElementVisual(ShimmerContainer).Opacity;
        _logger?.LogDebug(
            "[xfade][album:{Id}] reset shimmerXaml={ShimmerXaml} shimmerVisual={ShimmerVisual} navAway={NavAway}",
            XfadeLog.Tag(ViewModel.AlbumId), ShimmerContainer.Opacity, shimmerVisualOpacity, _isNavigatingAway);
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private bool TryHandlePendingAlbumArtConnectedAnimation()
    {
        if (!ConnectedAnimationHelper.HasPendingAnimation(ConnectedAnimationHelper.AlbumArt))
            return false;

        _showingContent = true;
        _crossfadeScheduled = false;
        ShimmerGate.IsLoaded = false;
        if (ShimmerContainer is not null)
        {
            ShimmerContainer.Visibility = Visibility.Collapsed;
            ShimmerContainer.Opacity = 0;
            ElementCompositionPreview.GetElementVisual(ShimmerContainer).Opacity = 0;
        }
        ContentContainer.Opacity = 1;
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 1;

        UpdateLayout();
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
        Bindings?.Update();
        if (string.IsNullOrEmpty(ViewModel.AlbumId))
            return;

        ResetCrossfadeForNewLoad();
        ViewModel.Activate(ViewModel.AlbumId);
        DispatcherQueue.TryEnqueue(TryShowContentNow);
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
            "[xfade][album:{Id}] load.enter showing={Showing} scheduled={Scheduled} navAway={NavAway}",
            XfadeLog.Tag(ViewModel.AlbumId), _showingContent, _crossfadeScheduled, _isNavigatingAway);

        // Reset shimmer/content visual state for the fresh load — mirrors
        // ArtistPage's LoadNewContent reset so the next album fades in
        // cleanly instead of inheriting the previous album's faded-in chrome.
        ResetCrossfadeForNewLoad();

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
                if (!_isNavigatingAway)
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

        await Task.Yield();
        if (_isNavigatingAway) return;
        TryShowContentNow();
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
        if (sender is Border border && e.NewSize.Width > 0)
            border.Height = e.NewSize.Width;
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
}
