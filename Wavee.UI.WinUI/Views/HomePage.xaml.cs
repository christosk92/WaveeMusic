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
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Controls.Cards;
using Wavee.UI.WinUI.Helpers.Navigation;
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

    // Shy header — pinned compact card morphs in via TransitionHelper once the
    // user scrolls past most of the in-flow hero. Mirrors ArtistPage exactly:
    // single helper instance, single pin-state bool, run-guard + recheck flag
    // for ViewChanged event coalescing.
    // Pin once almost the full hero has scrolled out of view (residual = the
    // px of hero still on-screen at the moment of pin). Small residual = pin
    // happens late, which feels right because the HeroSpacer wrapper now
    // reserves the layout — there's no reason to pin early to "save space".
    private const double ShyHeaderHeroResidualPx = 20;
    private const double ShyHeaderHysteresisPx = 16;
    private const double ShyHeaderHeroFallbackPx = 140;
    private const int NavigationCacheTrimDelaySeconds = 45;
    private CommunityToolkit.WinUI.TransitionHelper? _shyHeaderTransition;
    private bool _isShyHeaderPinned;
    private bool _isShyHeaderTransitionRunning;
    private bool _shyHeaderRecheckPending;
    // Captured max-height of the in-flow hero. HeroOverlayPanel IS the
    // TransitionHelper Source, so its ActualHeight collapses to 0 once pinned
    // (SourceToggleMethod=ByVisibility). Reading the live value would make
    // pinOffset drop to 0 → unpin can never trigger. Only ever grow this value.
    private double _heroMeasuredHeightPx;

    public HomeViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public HomePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<HomeViewModel>();
        _logger = Ioc.Default.GetService<ILogger<HomePage>>();
        _cache = Ioc.Default.GetService<HomeFeedCache>();
        InitializeComponent();

        // Set the section template selector here (was deferred to HomePage_Loaded
        // for startup perf) — Sections can be populated before Loaded fires
        // (cached-feed path), and a null ItemTemplate at that moment causes
        // ItemsRepeater to fall back to rendering each item as
        // "Wavee.UI.WinUI.ViewModels.HomeSection" via ToString. Setting it now
        // costs nothing because the template construction is just dictionary
        // lookups against the page's already-loaded Resources.
        SectionsRepeater.ItemTemplate = new HomeSectionTemplateSelector
        {
            ShortsTemplate = (DataTemplate)Resources["ShortsSectionTemplate"],
            GenericTemplate = (DataTemplate)Resources["GenericSectionTemplate"],
            BaselineTemplate = (DataTemplate)Resources["BaselineSectionTemplate"]
        };

        Loaded += HomePage_Loaded;
        Unloaded += HomePage_Unloaded;

        // Seed the VM with the current theme + re-derive on swap. Mirrors
        // PlaylistPage / AlbumPage: ApplyTheme rebuilds the hero backdrop
        // brush against the right palette tier (HigherContrast for dark,
        // HighContrast for light).
        ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);
        ActualThemeChanged += (_, _) => ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);
    }

    private void FeaturedItem_Click(object sender, RoutedEventArgs e)
    {
        var item = ViewModel.FeaturedItem;
        if (item is null) return;
        var openInNewTab = NavigationHelpers.IsCtrlPressed();
        HomeViewModel.NavigateToItem(item, openInNewTab);
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
        // (SectionsRepeater.ItemTemplate is set in the constructor — see comment there.)
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 0;

        // Shy header wiring. ContentContainer is the page's vertical scroller —
        // ViewChanged drives the pin evaluation. EnsureShyHeaderTransition wires
        // the Source/Target on the Resource-declared TransitionHelper (XAML
        // resources can't ElementName-bind, hence the code-behind hookup).
        ContentContainer.ViewChanged += ContentContainer_ViewChanged;
        HeroOverlayPanel.SizeChanged += HeroOverlayPanel_SizeChanged;

        // Loaded fires AFTER initial layout — by now HeroOverlayPanel has been
        // measured at least once and the SizeChanged we just subscribed to has
        // already missed its first event. Sync the cached height + spacer
        // MinHeight here so the layout-reservation isn't 0 at first pin.
        if (HeroOverlayPanel.ActualHeight > _heroMeasuredHeightPx)
        {
            _heroMeasuredHeightPx = HeroOverlayPanel.ActualHeight;
            HeroSpacer.MinHeight = _heroMeasuredHeightPx;
        }

        ResetShyHeaderState();
        UpdatePageBleedOpacity();

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
    }

    public void RefreshWithParameter(object? parameter)
    {
        // HomePage has no parameter — a refresh just reloads the feed if stale
        if (_cache is { IsStale: true })
            _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isNavigatedAway = false;
        CancelNavigationCacheTrim();

        if (_trimmedForNavigationCache)
        {
            RestoreFromNavigationCache();
            return;
        }

        // Rehydrate rebuilds Sections + Chips from the cached home-feed
        // response — paired with HibernateForNavigation on OnNavigatedFrom.
        // Cheap (no network); avoids holding the parsed tree while away.
        ViewModel.ResumeFromNavigationCache();
        await ViewModel.RefreshLocalSectionAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _isNavigatedAway = true;

        // Stop background feed work immediately, but delay tearing down the
        // visual tree. Quick page hops can then reuse the navigation-cached
        // Home surface instead of re-instantiating every visible shelf.
        ViewModel.SuspendBackgroundRefresh();
        ScheduleNavigationCacheTrim();
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
        if (_isDisposed || !_isNavigatedAway || _trimmedForNavigationCache)
            return;

        TrimForNavigationCache();
    }

    public void TrimForNavigationCache()
    {
        if (_trimmedForNavigationCache)
            return;

        _trimmedForNavigationCache = true;
        _pendingSleepState = new HomePageSleepState(ContentContainer?.VerticalOffset ?? 0);
        ViewModel.HibernateForNavigation();
        ResetShyHeaderState();
        DetachSectionsRepeater();
    }

    public void RestoreFromNavigationCache()
    {
        if (!_trimmedForNavigationCache)
            return;

        _trimmedForNavigationCache = false;
        AttachSectionsRepeater();
        ViewModel.ResumeFromNavigationCache();
        TryApplyPendingSleepState();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

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
        if (ContentContainer != null)
            ContentContainer.ViewChanged -= ContentContainer_ViewChanged;
        if (HeroOverlayPanel != null)
            HeroOverlayPanel.SizeChanged -= HeroOverlayPanel_SizeChanged;
    }

    private void DetachSectionsRepeater()
    {
        if (_sectionsDetachedForNavigationCache || SectionsRepeater == null)
            return;

        SectionsRepeater.ItemsSource = null;
        _sectionsDetachedForNavigationCache = true;
    }

    private void AttachSectionsRepeater()
    {
        if (!_sectionsDetachedForNavigationCache || SectionsRepeater == null)
            return;

        SectionsRepeater.ItemsSource = ViewModel.Sections;
        _sectionsDetachedForNavigationCache = false;
    }

    // ── Shy header ──────────────────────────────────────────────────────────
    // Mirrors ArtistPage.xaml.cs structure: single helper instance, single
    // pin-state bool, async coalescing loop for ViewChanged events. Threshold
    // uses a cached max-ever HeroOverlayPanel height (NOT the live ActualHeight
    // — see _heroMeasuredHeightPx field comment) plus 16 px of hysteresis to
    // absorb the StackPanel-reflow ViewChanged echo when the hero collapses.

    private void EnsureShyHeaderTransition()
    {
        if (_shyHeaderTransition != null)
            return;

        if (ShyHeaderCard == null)
            return;

        if (Resources.TryGetValue("HomeShyHeaderTransition", out var resource)
            && resource is CommunityToolkit.WinUI.TransitionHelper helper)
        {
            helper.Source = HeroOverlayPanel;
            helper.Target = ShyHeaderCard;
            _shyHeaderTransition = helper;
        }
    }

    private void ResetShyHeaderState()
    {
        _isShyHeaderPinned = false;
        _isShyHeaderTransitionRunning = false;
        _shyHeaderRecheckPending = false;
        _heroMeasuredHeightPx = 0;
        _shyHeaderTransition?.Reset(toInitialState: true);
    }

    private bool EnsureShyHeaderRealized()
    {
        if (ShyHeaderHost != null && ShyHeaderCard != null)
        {
            EnsureShyHeaderTransition();
            return _shyHeaderTransition != null;
        }

        _ = FindName(nameof(ShyHeaderHost));
        EnsureShyHeaderTransition();
        return ShyHeaderHost != null && ShyHeaderCard != null && _shyHeaderTransition != null;
    }

    private void HeroOverlayPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Only ever grow. The TransitionHelper collapses HeroOverlayPanel when
        // pinning (ByVisibility) → ActualHeight=0 fires here. Ignore that drop.
        if (e.NewSize.Height > _heroMeasuredHeightPx)
        {
            _heroMeasuredHeightPx = e.NewSize.Height;
            // Pin the spacer to the measured height so the StackPanel layout
            // stays put when HeroOverlayPanel collapses — prevents the visual
            // jump that "pushes content up" the moment the shy header pins.
            if (HeroSpacer != null)
                HeroSpacer.MinHeight = _heroMeasuredHeightPx;
        }
    }

    private void ContentContainer_ViewChanged(ScrollView sender, object args)
    {
        UpdatePageBleedOpacity();
        _ = EvaluateShyHeaderAsync();
    }

    private void UpdatePageBleedOpacity()
    {
        if (PageBleedHost == null || ContentContainer == null) return;

        // Fade the top-left page bleed as the user scrolls past the hero.
        // Fully visible at offset 0, fully invisible by the time the hero
        // is mostly out of view. Uses the same cached measured height the
        // shy-header pin logic uses, with a 60 px head-start so the bleed
        // is already nearly gone by the time pin triggers.
        double heroH = _heroMeasuredHeightPx > 0 ? _heroMeasuredHeightPx : ShyHeaderHeroFallbackPx;
        double fadeDistance = Math.Max(80, heroH - 40);
        double progress = Math.Clamp(ContentContainer.VerticalOffset / fadeDistance, 0.0, 1.0);
        PageBleedHost.Opacity = 1.0 - progress;
    }

    private async Task EvaluateShyHeaderAsync()
    {
        if (HeroOverlayPanel == null || ContentContainer == null)
            return;

        if (_isShyHeaderTransitionRunning)
        {
            // Coalesce: re-check once the in-flight transition lands so a
            // burst of ViewChanged events doesn't stack animations.
            _shyHeaderRecheckPending = true;
            return;
        }

        while (true)
        {
            if (_isDisposed || !HeroOverlayPanel.IsLoaded)
                return;

            // Use the cached max-ever-measured hero height — see field comment
            // for why HeroOverlayPanel.ActualHeight isn't safe to read here.
            double heroH = _heroMeasuredHeightPx > 0 ? _heroMeasuredHeightPx : ShyHeaderHeroFallbackPx;
            double pinOffset = Math.Max(0, heroH - ShyHeaderHeroResidualPx);
            double unpinOffset = Math.Max(0, pinOffset - ShyHeaderHysteresisPx);

            // Hysteresis: once pinned, the user has to scroll back past the
            // lower threshold to unpin. Absorbs the secondary ViewChanged echo
            // that fires when the in-flow hero collapses.
            double activeThreshold = _isShyHeaderPinned ? unpinOffset : pinOffset;
            bool shouldPin = ContentContainer.VerticalOffset >= activeThreshold;

            if (shouldPin == _isShyHeaderPinned)
                return;

            if (!EnsureShyHeaderRealized() || _shyHeaderTransition == null)
                return;

            _isShyHeaderTransitionRunning = true;
            _shyHeaderRecheckPending = false;

            try
            {
                if (shouldPin)
                    await _shyHeaderTransition.StartAsync();
                else
                    await _shyHeaderTransition.ReverseAsync();

                _isShyHeaderPinned = shouldPin;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Home shy-header transition skipped.");
                return;
            }
            finally
            {
                _isShyHeaderTransitionRunning = false;
            }

            // Loop only if a scroll event arrived during the transition.
            if (!_shyHeaderRecheckPending)
                return;
        }
    }

    private void TryApplyPendingSleepState()
    {
        if (_pendingSleepState == null || ViewModel.IsLoading || ContentContainer == null)
            return;

        var state = _pendingSleepState;
        _pendingSleepState = null;

        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => ContentContainer.ScrollTo(0, state.VerticalOffset));
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

        // Find the section index in the displayed Sections collection
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
        var element = SectionsRepeater.TryGetElement(sectionIndex);
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
