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
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Controls.Layouts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Controls.Cards;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class HomePage : Page, ITabBarItemContent, ITabSleepParticipant, INavigationCacheMemoryParticipant, IDisposable
{
    private readonly ILogger? _logger;
    private readonly HomeFeedCache? _cache;
    private bool _isShimmerContentReleased;
    private bool _isDisposed;
    private bool _trimmedForNavigationCache;
    private bool _sectionsDetachedForNavigationCache;
    private bool _isNavigatedAway;
    private DispatcherQueueTimer? _navigationTrimTimer;
    private HomePageSleepState? _pendingSleepState;

    private const int NavigationCacheTrimDelaySeconds = 45;
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
        //   ≥1100  WideState         — hero + WideSideRail (320 px) side-by-side
        //   ≥720   StackedMedium     — hero full-row + StackedShortcuts (1 big + 2 stacked)
        //   < 720  StackedNarrow     — hero full-row + StackedShortcuts (Card0 banner + 2 below)
        string nextState;
        if (width >= 1100)
            nextState = "HeroBandWideState";
        else if (width >= 720)
            nextState = "HeroBandStackedMediumState";
        else
            nextState = "HeroBandStackedNarrowState";

        if (nextState == _currentHeroBandState) return;
        _currentHeroBandState = nextState;
        VisualStateManager.GoToState(this, nextState, useTransitions: false);
    }

    public void RefreshWithParameter(object? parameter)
    {
        // HomePage has no parameter — a refresh just reloads the feed if stale
        if (_cache is { IsStale: true })
            _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        // Bracket only the synchronous prefix — the using-scope disposes BEFORE
        // the first await so the per-nav stage time excludes async work that
        // runs after Frame.Navigate has returned.
        using (Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.home.onNavigatedTo"))
        {
            base.OnNavigatedTo(e);
            _isNavigatedAway = false;
            CancelNavigationCacheTrim();
            // Re-attach compiled x:Bind to VM.PropertyChanged before any rehydrate
            // path runs. Idempotent; safe on first entry too.
            using (Wavee.UI.WinUI.Services.UiOperationProfiler.Instance?.Profile("page.home.bindingsUpdate"))
            {
                Bindings?.Update();
            }

            var restoredFromTrim = _trimmedForNavigationCache;
            if (restoredFromTrim)
            {
                RestoreFromNavigationCache();
            }

            // Rehydrate rebuilds Sections + Chips from the cached home-feed
            // response — paired with HibernateForNavigation on OnNavigatedFrom.
            // Cheap (no network); avoids holding the parsed tree while away.
            if (!restoredFromTrim)
                ViewModel.ResumeFromNavigationCache();
        }
        await ViewModel.RefreshLocalSectionAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.home.onNavigatedFrom");
        base.OnNavigatedFrom(e);
        _isNavigatedAway = true;
        CancelScrollRestore();

        // Stop background feed work immediately, but delay tearing down the
        // visual tree. Quick page hops can then reuse the navigation-cached
        // Home surface instead of re-instantiating every visible shelf.
        ViewModel.SuspendBackgroundRefresh();
        ScheduleNavigationCacheTrim();
        // Detach compiled x:Bind from VM.PropertyChanged so the cached page
        // does not keep its bindings live while the user is on another page.
        Bindings?.StopTracking();
    }

    private void ScheduleNavigationCacheTrim()
    {
        if (_isDisposed || _trimmedForNavigationCache)
            return;

        var timer = _navigationTrimTimer;
        if (timer is null)
        {
            timer = DispatcherQueue.CreateTimer();
            timer.IsRepeating = false;
            timer.Tick += NavigationTrimTimer_Tick;
            _navigationTrimTimer = timer;
        }

        timer.Stop();
        timer.Interval = TimeSpan.FromSeconds(NavigationCacheTrimDelaySeconds);
        timer.Start();
    }

    private void CancelNavigationCacheTrim()
    {
        _navigationTrimTimer?.Stop();
    }

    private void NavigationTrimTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_isDisposed || _trimmedForNavigationCache)
            return;

        TrimForNavigationCacheNow();
    }

    public void TrimForNavigationCache()
    {
        ScheduleNavigationCacheTrim();
    }

    private void TrimForNavigationCacheNow()
    {
        if (_trimmedForNavigationCache)
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
        CancelNavigationCacheTrim();
        if (!_trimmedForNavigationCache)
            return;

        _trimmedForNavigationCache = false;
        _isNavigatedAway = false;
        BeginScrollRestoreIfNeeded();
        ResetRegionsLayoutCache();
        AttachSectionsRepeater();
        ViewModel.ResumeFromNavigationCache();

        // Force a synchronous measure+arrange right after attach so:
        //   (a) ScrollViewer.ExtentHeight reflects the real content height
        //       before TryApplyPendingSleepState's ScrollToImmediate runs,
        //       avoiding the empty-extent retry loop in RestoreScrollOffsetAsync.
        //   (b) RegionsRepeater realizes containers for the current viewport
        //       (scroll = 0 at this point) so the upper sections paint.
        // Without this the cached page can render with the upper region slots
        // devirtualized until the user nudges the scrollbar.
        RegionsRepeater?.UpdateLayout();
        ContentContainer?.UpdateLayout();

        TryApplyPendingSleepState();
        QueueRestoredLayoutRefresh();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        CancelScrollRestore();

        if (_navigationTrimTimer is not null)
        {
            _navigationTrimTimer.Stop();
            _navigationTrimTimer.Tick -= NavigationTrimTimer_Tick;
            _navigationTrimTimer = null;
        }

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
        // The synchronous UpdateLayout in RestoreFromNavigationCache replaces
        // the queued refresh; keeping both was a redundant double pass.
    }

    private void ResetRegionsLayoutCache()
    {
        if (RegionsRepeater?.Layout is SectionStackLayout layout)
            layout.ResetCache();

        RegionsRepeater?.InvalidateMeasure();
        ContentContainer?.InvalidateMeasure();
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
            RegionsRepeater?.UpdateLayout();
            ContentContainer?.UpdateLayout();
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

            // EffectiveViewport propagation to RegionsRepeater normally needs
            // a layout cycle after ScrollToImmediate. Force it synchronously
            // so SectionStackLayout's MeasureOverride re-runs with the new
            // RealizationRect and realizes containers for the restored
            // viewport position before paint — otherwise the user sees blank
            // section slots until they nudge the scrollbar.
            RegionsRepeater?.InvalidateMeasure();
            ContentContainer?.UpdateLayout();

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

        var items = Controls.ContextMenu.Builders.CardContextMenuBuilder.Build(new Controls.ContextMenu.Builders.CardMenuContext
        {
            Uri = item.Uri ?? string.Empty,
            Title = item.Title ?? string.Empty,
            Subtitle = item.Subtitle,
            ImageUrl = item.ImageUrl,
            OpenAction = openInNewTab => HomeViewModel.NavigateToItem(item, openInNewTab)
        });
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

    private static readonly JsonSerializerOptions HomeDebugJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

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
            return JsonSerializer.Serialize(new
            {
                message = "No raw Spotify section JSON is attached to this rendered section.",
                title = section.Title,
                sectionUri = section.SectionUri,
                sectionType = section.SectionType.ToString(),
                itemCount = section.Items.Count
            }, HomeDebugJsonOptions);
        }

        return PrettyPrintJson(section.RawSpotifyJson);
    }

    private static string BuildViewModelDebugJson(HomeSection section)
    {
        var viewModel = new
        {
            title = section.Title,
            subtitle = section.Subtitle,
            sectionType = section.SectionType.ToString(),
            sectionUri = section.SectionUri,
            headerEntityName = section.HeaderEntityName,
            headerEntityImageUrl = section.HeaderEntityImageUrl,
            headerEntityUri = section.HeaderEntityUri,
            itemCount = section.Items.Count,
            items = section.Items.Select((item, index) => new
            {
                index,
                uri = item.Uri,
                title = item.Title,
                subtitle = item.Subtitle,
                imageUrl = item.ImageUrl,
                contentType = item.ContentType.ToString(),
                colorHex = item.ColorHex,
                placeholderGlyph = item.PlaceholderGlyph,
                isBaselineLoading = item.IsBaselineLoading,
                hasBaselinePreview = item.HasBaselinePreview,
                heroImageUrl = item.HeroImageUrl,
                heroColorHex = item.HeroColorHex,
                canvasUrl = item.CanvasUrl,
                canvasThumbnailUrl = item.CanvasThumbnailUrl,
                audioPreviewUrl = item.AudioPreviewUrl,
                baselineGroupTitle = item.BaselineGroupTitle,
                previewTracks = item.PreviewTracks.Select(track => new
                {
                    uri = track.Uri,
                    name = track.Name,
                    coverArtUrl = track.CoverArtUrl,
                    colorHex = track.ColorHex,
                    canvasUrl = track.CanvasUrl,
                    canvasThumbnailUrl = track.CanvasThumbnailUrl,
                    audioPreviewUrl = track.AudioPreviewUrl
                }).ToList()
            }).ToList()
        };

        return JsonSerializer.Serialize(viewModel, HomeDebugJsonOptions);
    }

    private static string PrettyPrintJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, HomeDebugJsonOptions);
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
public sealed class HomeSectionTemplateSelector : DataTemplateSelector
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
public sealed class HomeItemTemplateSelector : DataTemplateSelector
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
