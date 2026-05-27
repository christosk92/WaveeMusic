using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Controls.InPageFilter;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Controls.Layouts;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Controls.Cards;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.Json;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class HomePage : UserControl, ITabBarItemContent, ITabSleepParticipant, INavigationCacheMemoryParticipant, IPageHostAware, IDisposable, IRedirectsCtrlFToOmnibar
{
    private readonly ILogger? _logger;
    private readonly HomeFeedCache? _cache;
    private bool _isShimmerContentReleased;
    private bool _isDisposed;
    private bool _trimmedForNavigationCache;
    private bool _sectionsDetachedForNavigationCache;
    private bool _isNavigatedAway;
    private bool _postNavigationResumeQueued;
    private HomePageSleepState? _pendingSleepState;

    private const int ScrollRestoreMaxAttempts = 12;
    private const int ScrollRestoreRetryDelayMs = 16;
    // Safety net for ImageLoadingSuspension. If BeginScrollRestore runs but
    // the matching EndScrollRestore never fires (ViewModel stuck in IsLoading,
    // page never receives a fresh sleep-state apply, etc.) the global
    // suspension flag would stay on forever and gate ALL cold image loads.
    // After this timeout we forcibly clear our generation's suspension. The
    // generation check in EndScrollRestore is preserved — if the real End
    // already fired and bumped the generation, this is a no-op.
    private const int ScrollRestoreWatchdogMs = 3000;
    private bool _isRestoringScroll;
    private int _scrollRestoreGeneration;
    private int _layoutRecoveryGeneration;

    // HeroCarousel.CurrentAccent registration token — set in HomePage_Loaded,
    // cleared in HomePage_Unloaded. The carousel publishes a per-frame RGB-lerped
    // colour as the InteractionTracker scrubs between slides; we pipe that into
    // HomeViewModel.UpdatePageBleedFromCarousel so the page-level radial bleed
    // follows the active slide cohesively.
    private long _heroAccentToken;
    private bool _heroWired;

    public HomeViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public HomePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<HomeViewModel>();
        _logger = Ioc.Default.GetService<ILogger<HomePage>>();
        _cache = Ioc.Default.GetService<HomeFeedCache>();
        InitializeComponent();

        // Section template selector is now declared as a XAML resource
        // (HomePage.xaml's HomeSectionTemplateSelector key) and bound into
        // each HomeRegionView via the SectionTemplateSelector DP. The outer
        // RegionsRepeater binds to ViewModel.HeroAdapter.Regions; the inner
        // per-region repeater inside HomeRegionView applies the selector.
        Loaded += HomePage_Loaded;
        Unloaded += HomePage_Unloaded;

        // Seed the VM with the current theme + re-derive on swap. Mirrors
        // PlaylistPage / AlbumPage: ApplyTheme rebuilds the hero backdrop
        // brush against the right palette tier (HigherContrast for dark,
        // HighContrast for light).
        ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);
        ActualThemeChanged += (_, _) => ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);
    }

    private bool _showingContent;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsLoading))
        {
            if (ViewModel.IsLoading && _showingContent)
            {
                // Loading started (e.g. refresh) — show shimmer again
                ShowShimmer();
            }
        }

        if (e.PropertyName is nameof(ViewModel.IsLoading) or nameof(ViewModel.Sections))
        {
            if (!ViewModel.IsLoading && ViewModel.Sections.Count > 0 && !_showingContent)
            {
                CrossfadeToContent();
            }
        }

        if (e.PropertyName == nameof(ViewModel.IsLoading) && !ViewModel.IsLoading)
            TryApplyPendingSleepState();
    }

    private void OnCacheDataRefreshed(HomeFeedSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() => ViewModel.ApplyBackgroundRefresh(snapshot));
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= HomePage_Loaded;

        // Deferred setup — moved from constructor so InitializeComponent returns faster.
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 0;

        WireHeroBand();

        WeakReferenceMessenger.Default.Register<AuthStatusChangedMessage>(this, (r, m) =>
        {
            if (m.Value == AuthStatus.Authenticated)
                DispatcherQueue.TryEnqueue(() => _ = ViewModel.LoadCommand.ExecuteAsync(null));
        });

        if (_cache != null)
            _cache.DataRefreshed += OnCacheDataRefreshed;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        try
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unhandled error in HomePage Loaded handler");
        }
    }

    private void ShowShimmer()
    {
        if (_isShimmerContentReleased || ShimmerContainer?.Content == null)
            return;

        _showingContent = false;
        ShimmerContainer.Visibility = Visibility.Visible;

        // Fade in shimmer, fade out content
        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(200))
            .Start(ShimmerContainer);

        AnimationBuilder.Create()
            .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200))
            .Start(ContentContainer);
    }

    private void CrossfadeToContent()
    {
        _showingContent = true;

        // Fade out shimmer, collapse it immediately on completion so it stops
        // participating in layout — leaving it Visible for 500 ms after the opacity
        // hit zero doubled the measure work on every outer-page scroll and amplified
        // any layout stutter.
        _ = CrossfadeShimmerOutAsync();

        // Fade in content
        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(300),
                     delay: TimeSpan.FromMilliseconds(100))
            .Start(ContentContainer);
    }

    private async Task CrossfadeShimmerOutAsync()
    {
        if (ShimmerContainer == null)
            return;

        try
        {
            await AnimationBuilder.Create()
                .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200))
                .StartAsync(ShimmerContainer);
        }
        catch
        {
            // Animation was cancelled (e.g. ShowShimmer was called again). The
            // guard below preserves correctness.
        }

        if (!_showingContent || ShimmerContainer == null)
            return;

        ShimmerContainer.Visibility = Visibility.Collapsed;
        if (!_isShimmerContentReleased)
        {
            // The first-load skeleton is one of the heaviest retained subtrees on Home.
            // Release it after content has loaded so a cached Home page doesn't keep the
            // entire shimmer visual tree resident for the rest of the session.
            ShimmerContainer.Content = null;
            _isShimmerContentReleased = true;
        }
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        CleanupSubscriptions();
        UnwireHeroBand();
    }

    /// <summary>
    /// Wire the HeroHalo to the HaloBackdrop element + subscribe to the
    /// carousel's per-frame accent. Idempotent — guarded by <c>_heroWired</c>
    /// so re-entry from a nav-cache restore doesn't double-subscribe.
    /// </summary>
    private void WireHeroBand()
    {
        if (_heroWired || HomeHero is null || HaloBackdrop is null) return;
        Klankhuis.Hero.Controls.HeroHalo.SetSource(HaloBackdrop, HomeHero);
        _heroAccentToken = HomeHero.RegisterPropertyChangedCallback(
            Klankhuis.Hero.Controls.HeroCarousel.CurrentAccentProperty, OnHeroAccentChanged);
        _heroWired = true;
        // Seed the page bleed with the initial slide's accent so the first
        // paint already reads cohesively rather than waiting for the first
        // tracker tick.
        ViewModel.UpdatePageBleedFromCarousel(HomeHero.CurrentAccent);
    }

    private void UnwireHeroBand()
    {
        if (!_heroWired) return;
        if (HomeHero is not null && _heroAccentToken != 0)
        {
            HomeHero.UnregisterPropertyChangedCallback(
                Klankhuis.Hero.Controls.HeroCarousel.CurrentAccentProperty, _heroAccentToken);
        }
        if (HaloBackdrop is not null)
            Klankhuis.Hero.Controls.HeroHalo.SetSource(HaloBackdrop, null);
        _heroAccentToken = 0;
        _heroWired = false;
    }

    private void OnHeroAccentChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_isDisposed || HomeHero is null) return;
        ViewModel.UpdatePageBleedFromCarousel(HomeHero.CurrentAccent);
    }

    /// <summary>
    /// Drives the hero-band's responsive states off <see cref="HeroBand"/>'s actual
    /// rendered width rather than the window width. <c>AdaptiveTrigger.MinWindowWidth</c>
    /// reads the *window*, which is wrong when the shell sidebar + Queue panel eat
    /// space from the HomePage area — at a 1600-px window with the Queue open, the
    /// HomePage is only ~900 px wide, but the old wide-state trigger fired anyway and
    /// jammed the side rail next to a too-narrow hero. We now branch on the band's
    /// own width so the layout matches what the user actually sees.
    /// </summary>
    private string? _currentHeroBandState;

    private void HeroBand_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        // Thresholds:
        //   ≥900   WideState         — hero + WideSideRail (320 px) side-by-side
        //   ≥720   StackedMedium     — hero full-row + StackedShortcuts (1 big + 2 stacked)
        //   < 720  StackedNarrow     — hero full-row + StackedShortcuts (Card0 banner + 2 below)
        //
        // Previously the wide threshold was 1100 px — below that the side rail
        // collapsed under the hero and the shortcuts wrapped to a new row.
        // 900 lets the hero shrink to ~560 px (still wide enough for Klankhuis
        // to render 2-3 word title lines without char-by-char wrapping)
        // while the 320 px side rail keeps the shortcut cards alongside.
        string nextState;
        if (width >= 900)
            nextState = "HeroBandWideState";
        else if (width >= 720)
            nextState = "HeroBandStackedMediumState";
        else
            nextState = "HeroBandStackedNarrowState";

        if (nextState == _currentHeroBandState) return;
        _currentHeroBandState = nextState;
        VisualStateManager.GoToState(this, nextState, useTransitions: false);
    }

    // Click router for Klankhuis SideCard (3 in WideSideRail + 3 in StackedShortcuts).
    // Mirrors ContentCard.NavigateToUri's URI-prefix switch so the smaller hero-band
    // cards land users on the same destinations as the equivalent ContentCard would.
    // Tag is x:Bind'd to HeroAdapter.SideCardN.NavigationUri (string).
    private void SideCard_Click(Klankhuis.Hero.Controls.SideCard sender, RoutedEventArgs e)
    {
        var uri = sender.Tag as string;
        if (string.IsNullOrEmpty(uri)) return;

        var parts = uri.Split(':');
        if (parts.Length < 3) return;

        var type = parts[1];
        var openInNewTab = NavigationHelpers.IsCtrlPressed();
        var kind = ClickIntentKindFromUri(uri);
        Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.RecordClickIntent(
            "SideCard." + kind + (openInNewTab ? ".NewTab" : ""));

        var title = sender.Label ?? type;
        var imageUrl = sender.ImageUri?.ToString();
        var param = new Data.Parameters.ContentNavigationParameter
        {
            Uri = uri,
            Title = title,
            Subtitle = sender.Eyebrow,
            ImageUrl = imageUrl
        };

        switch (type)
        {
            case "collection" when uri.Contains("your-episodes", StringComparison.OrdinalIgnoreCase):
                NavigationHelpers.OpenYourEpisodes(openInNewTab);
                break;
            case "collection":
                NavigationHelpers.OpenLikedSongs(openInNewTab);
                break;
            case "artist":
                NavigationHelpers.OpenArtist(param, title, openInNewTab);
                break;
            case "album":
                NavigationHelpers.OpenAlbum(param, title, openInNewTab);
                break;
            case "playlist":
                NavigationHelpers.OpenPlaylist(param, title, openInNewTab);
                break;
            case "user" when uri.Contains(":collection", StringComparison.OrdinalIgnoreCase):
                NavigationHelpers.OpenLikedSongs(openInNewTab);
                break;
            case "user":
                NavigationHelpers.OpenProfile(param, title, openInNewTab);
                break;
            case "page":
            case "section":
            case "genre":
                NavigationHelpers.OpenBrowsePage(param, openInNewTab);
                break;
            case "show":
                NavigationHelpers.OpenShowPage(param, openInNewTab);
                break;
            case "episode":
                NavigationHelpers.OpenEpisodePage(uri, title, imageUrl, openInNewTab: openInNewTab);
                break;
        }
    }

    private static string ClickIntentKindFromUri(string uri)
    {
        var parts = uri.Split(':');
        if (parts.Length < 2) return "Unknown";
        return parts[1] switch
        {
            "album" => "Album",
            "playlist" => "Playlist",
            "artist" => "Artist",
            "show" => "Show",
            "episode" => "Episode",
            "collection" => "Collection",
            "user" => "User",
            "page" or "section" or "genre" => "Browse",
            _ => parts[1]
        };
    }

    public void RefreshWithParameter(object? parameter)
    {
        // HomePage has no parameter — a refresh just reloads the feed if stale
        if (_cache is { IsStale: true })
            _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    public void OnEntered(object? parameter, PageHostNavigationMode mode)
    {
        using (Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.home.onEntered"))
        {
            _isNavigatedAway = false;
            // The deferred trim (scheduled by TabBarItem on the previous leave)
            // is cancelled by TabBarItem itself in ContentHost_Navigated when
            // the user returns to this page — no per-page cancel needed here.
            QueuePostNavigationResume(mode);
        }
    }

    public void OnLeaving()
    {
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.home.onLeaving");
        _isNavigatedAway = true;
        CancelScrollRestore();

        // Stop background feed work immediately; visual-tree teardown is now
        // scheduled centrally by TabBarItem (~1 s after the leave). HomePage's
        // TrimForNavigationCache below is what TabBarItem fires on that timer.
        ViewModel.SuspendBackgroundRefresh();
        // Detach compiled x:Bind from VM.PropertyChanged so the cached page
        // does not keep its bindings live while the user is on another page.
        Bindings?.StopTracking();
    }

    public void TrimForNavigationCache()
    {
        if (_isDisposed || _trimmedForNavigationCache)
            return;

        _trimmedForNavigationCache = true;
        _pendingSleepState = new HomePageSleepState(ContentContainer?.VerticalOffset ?? 0);
        ViewModel.HibernateForNavigation();
        // Clear the carousel-bleed delta-throttle so the next accent applied
        // after RestoreFromNavigationCache always paints, even if it falls
        // within 4/256 of the stale pre-trim value.
        ViewModel.ResetCarouselBleedThrottle();
        DetachSectionsRepeater();
    }

    public void RestoreFromNavigationCache()
    {
        // The deferred-trim timer is owned by TabBarItem and cancelled there
        // when the user re-enters this page — no per-page cancel needed.
        if (!_trimmedForNavigationCache)
            return;

        _trimmedForNavigationCache = false;
        _isNavigatedAway = false;
        BeginScrollRestoreIfNeeded();
        ResetRegionsLayoutCache();
        AttachSectionsRepeater();
        ViewModel.ResumeFromNavigationCache();

        QueueRestoredLayoutRefresh();
        TryApplyPendingSleepState();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        CancelScrollRestore();

        Loaded -= HomePage_Loaded;
        Unloaded -= HomePage_Unloaded;
        CleanupSubscriptions();
        (ViewModel as IDisposable)?.Dispose();
    }

    public object? CaptureSleepState()
        => new HomePageSleepState(ContentContainer?.VerticalOffset ?? 0);

    public void RestoreSleepState(object? state)
    {
        _pendingSleepState = state as HomePageSleepState;
        TryApplyPendingSleepState();
    }

    private void CleanupSubscriptions()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        WeakReferenceMessenger.Default.Unregister<AuthStatusChangedMessage>(this);
        if (_cache != null)
            _cache.DataRefreshed -= OnCacheDataRefreshed;
    }

    private void DetachSectionsRepeater()
    {
        if (_sectionsDetachedForNavigationCache || RegionsRepeater == null)
            return;

        RegionsRepeater.ItemsSource = null;
        ResetRegionsLayoutCache();
        _sectionsDetachedForNavigationCache = true;
    }

    private void AttachSectionsRepeater()
    {
        if (!_sectionsDetachedForNavigationCache || RegionsRepeater == null)
            return;

        ResetRegionsLayoutCache();
        RegionsRepeater.ItemsSource = ViewModel.HeroAdapter.Regions;
        _sectionsDetachedForNavigationCache = false;
    }

    private void ResetRegionsLayoutCache()
    {
        if (RegionsRepeater?.Layout is SectionStackLayout layout)
            layout.ResetCache();

        RegionsRepeater?.InvalidateMeasure();
        ContentContainer?.InvalidateMeasure();
    }

    private void QueuePostNavigationResume(PageHostNavigationMode mode)
    {
        if (_postNavigationResumeQueued)
            return;

        var restoreFromTrim = _trimmedForNavigationCache;
        var resumeFromCache = mode == PageHostNavigationMode.New && !restoreFromTrim;
        _postNavigationResumeQueued = true;

        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _postNavigationResumeQueued = false;
                if (_isDisposed || _isNavigatedAway)
                    return;

                using (Wavee.UI.WinUI.Services.UiOperationProfiler.Instance?.Profile("page.home.postNavigationResume"))
                {
                    // Re-attach compiled x:Bind after navigation has returned.
                    // Existing values stay painted while tracking is restored.
                    using (Wavee.UI.WinUI.Services.UiOperationProfiler.Instance?.Profile("page.home.bindingsUpdate"))
                    {
                        Bindings?.Update();
                    }

                    if (restoreFromTrim)
                    {
                        RestoreFromNavigationCache();
                    }
                    else if (resumeFromCache)
                    {
                        // Rehydrate rebuilds Sections + Chips from the cached
                        // home-feed response. Keep it outside the nav stage so
                        // PageHost can complete the transition first.
                        ViewModel.ResumeFromNavigationCache();
                    }
                }

                _ = ViewModel.RefreshLocalSectionAsync();
            }))
        {
            _postNavigationResumeQueued = false;
        }
    }

    private void QueueRestoredLayoutRefresh()
    {
        if (_isDisposed)
            return;

        var generation = ++_layoutRecoveryGeneration;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_isDisposed || _isNavigatedAway || generation != _layoutRecoveryGeneration)
                return;

            ResetRegionsLayoutCache();
            RegionsRepeater?.InvalidateMeasure();
            ContentContainer?.InvalidateMeasure();
        });
    }

    private void TryApplyPendingSleepState()
    {
        if (_pendingSleepState == null || ViewModel.IsLoading || ContentContainer == null)
            return;

        var state = _pendingSleepState;
        _pendingSleepState = null;

        if (state.VerticalOffset <= 0)
        {
            EndScrollRestore(_scrollRestoreGeneration);
            return;
        }

        BeginScrollRestore();
        var generation = _scrollRestoreGeneration;
        _ = RestoreScrollOffsetAsync(state.VerticalOffset, generation);
    }

    private void BeginScrollRestoreIfNeeded()
    {
        if (_pendingSleepState is { VerticalOffset: > 0 })
            BeginScrollRestore();
    }

    private void BeginScrollRestore()
    {
        _scrollRestoreGeneration++;
        _isRestoringScroll = true;
        ContentCard.IsImageLoadingSuspended = true;

        // Watchdog — see ScrollRestoreWatchdogMs comment. If neither the
        // normal RestoreScrollOffsetAsync completion nor an OnNavigatedFrom /
        // Dispose path reaches EndScrollRestore in time, force-clear here.
        var generation = _scrollRestoreGeneration;
        _ = WatchdogClearSuspensionAsync(generation);
    }

    private async Task WatchdogClearSuspensionAsync(int generation)
    {
        try
        {
            await Task.Delay(ScrollRestoreWatchdogMs).ConfigureAwait(true);
        }
        catch { return; }
        if (_isDisposed) return;
        EndScrollRestore(generation);
    }

    private void CancelScrollRestore()
    {
        _scrollRestoreGeneration++;
        _isRestoringScroll = false;
        ContentCard.IsImageLoadingSuspended = false;
    }

    private async Task RestoreScrollOffsetAsync(double offset, int generation)
    {
        for (var attempt = 0; attempt < ScrollRestoreMaxAttempts; attempt++)
        {
            await Task.Yield();
            if (attempt > 0)
                await Task.Delay(ScrollRestoreRetryDelayMs);

            if (_isDisposed || _isNavigatedAway || generation != _scrollRestoreGeneration || ContentContainer == null)
                return;

            var maxOffset = Math.Max(0, ContentContainer.ExtentHeight - ContentContainer.ViewportHeight);
            if (maxOffset <= 0 && attempt + 1 < ScrollRestoreMaxAttempts)
                continue;

            var target = Math.Clamp(offset, 0, maxOffset);
            ContentContainer.ScrollToImmediate(0, target);

            // EffectiveViewport propagation to RegionsRepeater needs a layout
            // cycle after ScrollToImmediate. Invalidate and let XAML process it
            // naturally; forcing UpdateLayout here made home resume block the
            // UI thread for hundreds of milliseconds on large feeds.
            RegionsRepeater?.InvalidateMeasure();
            ContentContainer?.InvalidateMeasure();

            QueueRestoredLayoutRefresh();
            await Task.Yield();
            await Task.Delay(ScrollRestoreRetryDelayMs);
            EndScrollRestore(generation);
            return;
        }

        EndScrollRestore(generation);
    }

    private void EndScrollRestore(int generation)
    {
        if (generation != _scrollRestoreGeneration)
            return;

        _isRestoringScroll = false;
        ContentCard.IsImageLoadingSuspended = false;
    }

    // ── Card click handlers (used by both ContentCard and baseline buttons) ──

    private void ContentCard_Click(object sender, EventArgs e)
    {
        if (sender is ContentCard { DataContext: HomeSectionItem item })
            HomeViewModel.NavigateToItem(item, NavigationHelpers.IsCtrlPressed());
    }

    private void ContentCard_MiddleClick(object sender, EventArgs e)
    {
        if (sender is ContentCard { DataContext: HomeSectionItem item })
            HomeViewModel.NavigateToItem(item, openInNewTab: true);
    }

    private void ContentCard_RightTapped(ContentCard sender, RightTappedRoutedEventArgs e)
    {
        if (sender.DataContext is not HomeSectionItem item) return;

        var items = Controls.ContextMenu.Builders.CardContextMenuBuilder.BuildForUri(
            uri: item.Uri ?? string.Empty,
            title: item.Title ?? string.Empty,
            imageUrl: item.ImageUrl,
            openAction: openInNewTab => HomeViewModel.NavigateToItem(item, openInNewTab));
        Controls.ContextMenu.ContextMenuHost.Show(sender, items, e.GetPosition(sender));
    }

    // Baseline section still uses buttons directly
    private void GenericItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: HomeSectionItem item })
            HomeViewModel.NavigateToItem(item, NavigationHelpers.IsCtrlPressed());
    }

    private void GenericItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed
            && sender is Button { DataContext: HomeSectionItem item })
            HomeViewModel.NavigateToItem(item, openInNewTab: true);
    }

    private void HomeSectionViewAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string uri || string.IsNullOrEmpty(uri))
            return;

        // The local library is the only "View all" destination today. Future
        // sections (e.g. genre browse pages) can dispatch on URI prefix here.
        if (uri == "wavee:local:library" ||
            uri.StartsWith("wavee:local:", StringComparison.Ordinal))
        {
            Wavee.UI.WinUI.Helpers.Navigation.NavigationHelpers.OpenLocalLibrary();
        }
    }

    private async void HomeSectionDebugButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            await ShowHomeDebugTextDialog(
                "Home Section Debug",
                "The debug button did not have a HomeSection attached.");
            return;
        }

        var section = element.Tag as HomeSection ?? element.DataContext as HomeSection;
        if (section == null)
        {
            await ShowHomeDebugTextDialog(
                "Home Section Debug",
                "The debug button did not have a HomeSection attached.");
            return;
        }

        try
        {
            await ShowHomeSectionDebugDialog(section);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomeSectionDebug] Failed to show dialog: {ex}");
            await ShowHomeDebugTextDialog("Home Section Debug Error", ex.ToString());
        }
    }


    private async Task ShowHomeSectionDebugDialog(HomeSection section)
    {
        var pivot = new Pivot
        {
            MaxWidth = 860
        };

        pivot.Items.Add(new PivotItem
        {
            Header = "Raw Spotify",
            Content = CreateJsonDebugViewer(BuildRawSectionDebugJson(section))
        });

        pivot.Items.Add(new PivotItem
        {
            Header = "ViewModel",
            Content = CreateJsonDebugViewer(BuildViewModelDebugJson(section))
        });

        var dialog = new ContentDialog
        {
            Title = $"Home Section Debug: {section.Title ?? section.SectionUri}",
            Content = pivot,
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
            MaxWidth = 900
        };

        await dialog.ShowAsync();
    }

    private static ScrollViewer CreateJsonDebugViewer(string json)
    {
        return new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = json,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas"),
                FontSize = 11,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.NoWrap
            },
            MaxHeight = 520,
            Padding = new Thickness(12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private async Task ShowHomeDebugTextDialog(string title, string text)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = text,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas"),
                    FontSize = 11,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap
                },
                MaxHeight = 500
            },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    private static string BuildRawSectionDebugJson(HomeSection section)
    {
        if (string.IsNullOrWhiteSpace(section.RawSpotifyJson))
        {
            var payload = new HomeDebugMissingSectionPayload(
                "No raw Spotify section JSON is attached to this rendered section.",
                section.Title,
                section.SectionUri,
                section.SectionType.ToString(),
                section.Items.Count);
            return JsonSerializer.Serialize(payload, WaveeUiWinUiJsonContext.Default.HomeDebugMissingSectionPayload);
        }

        return PrettyPrintJson(section.RawSpotifyJson);
    }

    private static string BuildViewModelDebugJson(HomeSection section)
    {
        var viewModel = new HomeDebugSectionViewModel(
            section.Title,
            section.Subtitle,
            section.SectionType.ToString(),
            section.SectionUri,
            section.HeaderEntityName,
            section.HeaderEntityImageUrl,
            section.HeaderEntityUri,
            section.Items.Count,
            section.Items.Select(static (item, index) => new HomeDebugSectionItem(
                index,
                item.Uri,
                item.Title,
                item.Subtitle,
                item.ImageUrl,
                item.ContentType.ToString(),
                item.ColorHex,
                item.PlaceholderGlyph,
                item.IsBaselineLoading,
                item.HasBaselinePreview,
                item.HeroImageUrl,
                item.HeroColorHex,
                item.CanvasUrl,
                item.CanvasThumbnailUrl,
                item.AudioPreviewUrl,
                item.BaselineGroupTitle,
                item.PreviewTracks.Select(static t => new HomeDebugSectionPreviewTrack(
                    t.Uri, t.Name, t.CoverArtUrl, t.ColorHex, t.CanvasUrl, t.CanvasThumbnailUrl, t.AudioPreviewUrl)).ToArray()))
                .ToArray());

        return JsonSerializer.Serialize(viewModel, WaveeUiWinUiJsonContext.Default.HomeDebugSectionViewModel);
    }

    private static string PrettyPrintJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, WaveeUiWinUiJsonContext.Default.JsonElement);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    // ── Customize flyout handlers ──

    private void SectionTitle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string uri }) return;

        // Close the flyout
       // CustomizeFlyout.Hide();

        // TODO(region-redesign): the outer repeater is now RegionsRepeater
        // bound to HeroAdapter.Regions, not Sections directly. Scroll-to-section
        // by URI now needs to walk regions → sections → resolve the section's
        // visual element. The Customize flyout that called this is currently
        // unwired (no XAML reference), so this method is dead code; left as
        // a no-op until the flyout is reintroduced.
        var sectionIndex = -1;
        for (int i = 0; i < ViewModel.Sections.Count; i++)
        {
            if (ViewModel.Sections[i].SectionUri == uri)
            {
                sectionIndex = i;
                break;
            }
        }

        if (sectionIndex < 0) return;

        // Get the element from the ItemsRepeater and scroll to it
        var element = RegionsRepeater.TryGetElement(sectionIndex);
        if (element is FrameworkElement fe)
        {
            // Scroll into view
            fe.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = true,
                VerticalAlignmentRatio = 0.0 // Align to top
            });

            // Brief highlight animation — flash the background
            HighlightSection(fe);
        }
    }

    private const int HighlightBlinkDelayMs = 120;

    private static async void HighlightSection(FrameworkElement element)
    {
        // Store original opacity, flash it
        var original = element.Opacity;
        for (int i = 0; i < 3; i++)
        {
            element.Opacity = 0.5;
            await Task.Delay(HighlightBlinkDelayMs);
            element.Opacity = original;
            await Task.Delay(HighlightBlinkDelayMs);
        }
    }

    private void VisibilityCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string uri)
            ViewModel.SetSectionVisibility(uri, cb.IsChecked == true);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string uri })
            ViewModel.MoveSectionUpCommand.Execute(uri);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string uri })
            ViewModel.MoveSectionDownCommand.Execute(uri);
    }

    // ── Chip click handler ──

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        var chip = (sender as FrameworkElement)?.Tag as HomeChipViewModel;
        if (chip != null)
            _ = ViewModel.SelectChipCommand.ExecuteAsync(chip);
    }

    private sealed record HomePageSleepState(double VerticalOffset);
}

/// <summary>
/// Selects the appropriate DataTemplate for each home section type.
/// </summary>
public sealed partial class HomeSectionTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ShortsTemplate { get; set; }
    public DataTemplate? GenericTemplate { get; set; }
    public DataTemplate? BaselineTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        if (item is HomeSection section)
        {
            return section.SectionType switch
            {
                HomeSectionType.Shorts => ShortsTemplate ?? GenericTemplate!,
                HomeSectionType.Baseline => BaselineTemplate ?? GenericTemplate!,
                _ => GenericTemplate!
            };
        }
        return GenericTemplate!;
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}

/// <summary>
/// Selects per-item card template based on content type (artist = circle, everything else = square).
/// </summary>
public sealed partial class HomeItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ArtistTemplate { get; set; }
    public DataTemplate? DefaultTemplate { get; set; }

    /// <summary>
    /// Liked Songs (Recents-saved variant): chosen when the item is
    /// <c>spotify:collection:tracks</c> AND <see cref="HomeSectionItem.IsRecentlySaved"/>
    /// is true. Falls through to <see cref="DefaultTemplate"/> for the legacy
    /// Liked-Songs-as-played case (no group_metadata payload).
    /// </summary>
    public DataTemplate? LikedSongsRecentTemplate { get; set; }

    /// <summary>
    /// Episode template — chosen when <see cref="HomeSectionItem.ContentType"/>
    /// is <see cref="HomeContentType.Episode"/>. Falls through to
    /// <see cref="DefaultTemplate"/> when null so a missing wire doesn't blank
    /// out the section.
    /// </summary>
    public DataTemplate? EpisodeTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        if (item is HomeSectionItem hsi)
        {
            if (hsi.IsRecentlySaved
                && hsi.Uri != null
                && hsi.Uri.Contains(":collection", System.StringComparison.OrdinalIgnoreCase)
                && LikedSongsRecentTemplate != null)
                return LikedSongsRecentTemplate;
            else if (hsi.ContentType == HomeContentType.Episode && EpisodeTemplate != null)
                return EpisodeTemplate;
            else if (hsi.ContentType == HomeContentType.Artist)
                return ArtistTemplate ?? DefaultTemplate!;
            else
                return DefaultTemplate!;
        }

        return DefaultTemplate!;
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
