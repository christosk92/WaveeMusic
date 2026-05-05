using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.WinUI.Controls.Cards;
using Wavee.UI.WinUI.Controls.ShowEpisode;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class ShowPage : Page, ITabBarItemContent, INavigationCacheMemoryParticipant, IDisposable
{
    private const int ShimmerCollapseDelayMs = 250;

    private readonly ILogger? _logger;
    private readonly INotificationService? _notificationService;
    private readonly ISettingsService _settings;
    private bool _showingContent;
    private bool _crossfadeScheduled;
    private bool _isNavigatingAway;
    private bool _isDisposed;

    public ShowViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ShowPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ShowViewModel>();
        _logger = Ioc.Default.GetService<ILogger<ShowPage>>();
        _notificationService = Ioc.Default.GetService<INotificationService>();
        _settings = Ioc.Default.GetRequiredService<ISettingsService>();
        InitializeComponent();

        ViewModel.ContentChanged += ViewModel_ContentChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ActualThemeChanged += OnActualThemeChanged;
        Loaded += ShowPage_Loaded;
        Unloaded += ShowPage_Unloaded;

        // Start the content layer invisible at the composition layer so the
        // shimmer-to-content transition is a true crossfade, matching Album
        // and Artist pages.
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 0;

        ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Cleanup is in TrimForNavigationCache below — TabBarItem invokes that
        // around Frame.Navigate. Under NavigationCacheMode=Required the page
        // survives nav-away in Frame's cache pool; do NOT Dispose the VM here
        // (the cached page would come back bound to a dead VM).
    }

    private bool _trimmedForNavigationCache;

    public void TrimForNavigationCache()
    {
        if (_trimmedForNavigationCache) return;
        _trimmedForNavigationCache = true;
        // Detach compiled x:Bind from VM.PropertyChanged so the BindingsTracking
        // sibling does not keep the cached page wired up while it sits off-screen.
        Bindings?.StopTracking();
    }

    public void RestoreFromNavigationCache()
    {
        if (!_trimmedForNavigationCache) return;
        _trimmedForNavigationCache = false;
        Bindings?.Update();
    }

    private void ViewModel_ContentChanged(object? sender, TabItemParameter e)
        => ContentChanged?.Invoke(this, e);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShowViewModel.IsLoading))
        {
            if (!ViewModel.IsLoading && !_showingContent && !_crossfadeScheduled)
                TryShowContentNow();
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
        => ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);

    private void ShowPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isNavigatingAway = false;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        if (!ViewModel.IsLoading)
            TryShowContentNow();

        TryHandlePendingPodcastArtConnectedAnimation();
    }

    private void ShowPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isNavigatingAway = true;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Loaded -= ShowPage_Loaded;
        Unloaded -= ShowPage_Unloaded;
        ActualThemeChanged -= OnActualThemeChanged;
        ViewModel.ContentChanged -= ViewModel_ContentChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        (ViewModel as IDisposable)?.Dispose();
    }

    // Crossfade — copy of the AlbumPage pattern. Yield twice so XAML measures
    // the freshly bound content tree before the fade starts; collapse shimmer
    // after the fade completes so it stops participating in measure/arrange.
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

        CrossfadeToContent();
    }

    private async void CrossfadeToContent()
    {
        if (_showingContent) return;
        _showingContent = true;
        _crossfadeScheduled = false;

        AnimationBuilder.Create()
            .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200),
                layer: FrameworkLayer.Xaml)
            .Start(ShimmerContainer);

        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(300),
                delay: TimeSpan.FromMilliseconds(100),
                layer: FrameworkLayer.Xaml)
            .Start(ContentContainer);

        await Task.Delay(ShimmerCollapseDelayMs);
        if (_showingContent) ShimmerContainer.Visibility = Visibility.Collapsed;
    }

    private void TryShowContentNow()
    {
        if (_showingContent ||
            _crossfadeScheduled ||
            ViewModel.IsLoading ||
            (string.IsNullOrEmpty(ViewModel.ShowName) && !ViewModel.HasError))
        {
            return;
        }

        ScheduleCrossfade();
    }

    private void SnapCrossfadeToContent()
    {
        _showingContent = true;
        _crossfadeScheduled = false;
        ShimmerContainer.Visibility = Visibility.Collapsed;
        ShimmerContainer.Opacity = 0;
        ContentContainer.Opacity = 1;
        ElementCompositionPreview.GetElementVisual(ShimmerContainer).Opacity = 0;
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 1;
    }

    private void ResetCrossfadeForNewLoad()
    {
        _isNavigatingAway = false;
        _showingContent = false;
        _crossfadeScheduled = false;
        ShimmerContainer.Visibility = Visibility.Visible;
        ShimmerContainer.Opacity = 1;
        ContentContainer.Opacity = 0;
        ElementCompositionPreview.GetElementVisual(ShimmerContainer).Opacity = 1;
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 0;
    }

    // ── Navigation ──────────────────────────────────────────────────────────

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadNewContent(e.Parameter);
    }

    public void RefreshWithParameter(object? parameter)
    {
        // Same-tab navigation between two different shows reuses this Page
        // instance and never fires OnNavigatedTo - TabBarItem.Navigate routes
        // through this method instead. Without it, tapping a different show
        // from a recommendation card silently drops the new parameter.
        LoadNewContent(parameter);
    }

    private async void LoadNewContent(object? parameter)
    {
        ResetCrossfadeForNewLoad();

        ContentNavigationParameter? navigationParameter = null;
        var showUri = parameter switch
        {
            ContentNavigationParameter nav => (navigationParameter = nav).Uri,
            string raw => raw,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(showUri)) return;

        ViewModel.Activate(showUri);
        if (navigationParameter is not null)
            ViewModel.PrefillFrom(navigationParameter);
        RestoreShowPanelWidth(showUri);

        TryHandlePendingPodcastArtConnectedAnimation();

        await Task.Yield();
        if (_isNavigatingAway)
            return;

        TryShowContentNow();
    }

    private bool TryHandlePendingPodcastArtConnectedAnimation()
    {
        if (!ConnectedAnimationHelper.HasPendingAnimation(ConnectedAnimationHelper.PodcastArt) ||
            CoverContainer is null)
        {
            return false;
        }

        SnapCrossfadeToContent();
        UpdateLayout();
        return ConnectedAnimationHelper.TryStartAnimation(
            ConnectedAnimationHelper.PodcastArt,
            CoverContainer);
    }

    // ── Left-panel sizing ───────────────────────────────────────────────────

    private void ShowBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0)
            NavigationHelpers.OpenPodcasts(NavigationHelpers.IsCtrlPressed());
    }

    private void RestoreShowPanelWidth(string showUri)
    {
        const double defaultWidth = 280;
        var key = $"show:{showUri}";

        var width = _settings.Settings.PanelWidths.TryGetValue(key, out var saved)
            ? saved
            : defaultWidth;

        width = Math.Clamp(width, 220, 500);
        LeftPanelColumn.Width = new GridLength(width, GridUnitType.Pixel);
    }

    private void ShowSplitter_ResizeCompleted(object? sender, GridSplitterResizeCompletedEventArgs e)
    {
        var showUri = ViewModel.ShowUri;
        if (string.IsNullOrEmpty(showUri)) return;
        _settings.Update(s => s.PanelWidths[$"show:{showUri}"] = e.NewWidth);
    }

    // Keep the cover square as the splitter resizes the left column.
    private void CoverContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border border && e.NewSize.Width > 0)
            border.Height = e.NewSize.Width;
    }

    // ── Filter / sort dropdowns ─────────────────────────────────────────────

    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            Enum.TryParse<ShowEpisodeFilter>(tag, out var filter))
        {
            ViewModel.Filter = filter;
        }
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            Enum.TryParse<ShowEpisodeSort>(tag, out var sort))
        {
            ViewModel.Sort = sort;
        }
    }

    // ── Episode row events ─────────────────────────────────────────────────
    //
    // Two distinct gestures per row/banner/card:
    //   - PlayRequested: explicit play button → start playback in-place.
    //   - OpenRequested: row body tap        → navigate to EpisodePage with
    //     the parent show pre-filled so the breadcrumb and palette paint
    //     before the network resolves.

    private void EpisodeRow_PlayRequested(object? sender, ShowEpisodeDto e)
        => ViewModel.PlayEpisodeCommand.Execute(e);

    private void ResumeBanner_PlayRequested(object? sender, ShowEpisodeDto e)
        => ViewModel.PlayEpisodeCommand.Execute(e);

    private void UpNextCard_PlayRequested(object? sender, ShowEpisodeDto e)
        => ViewModel.PlayEpisodeCommand.Execute(e);

    private void EpisodeRow_OpenRequested(object? sender, ShowEpisodeDto e) => OpenEpisodeFromShow(e);

    private void ResumeBanner_OpenRequested(object? sender, ShowEpisodeDto e) => OpenEpisodeFromShow(e);

    private void UpNextCard_OpenRequested(object? sender, ShowEpisodeDto e) => OpenEpisodeFromShow(e);

    private void OpenEpisodeFromShow(ShowEpisodeDto? e)
    {
        if (e is null || string.IsNullOrEmpty(e.Uri)) return;
        NavigationHelpers.OpenEpisodePage(
            e.Uri,
            e.Title,
            e.CoverArtUrl ?? ViewModel.CoverArtUrl,
            ViewModel.ShowUri,
            ViewModel.ShowName,
            ViewModel.CoverArtUrl,
            NavigationHelpers.IsCtrlPressed());
    }

    private void EpisodeRow_LikeRequested(object? sender, ShowEpisodeDto e)
    {
        // Episode-level like is owned by ITrackLikeService too — but Spotify
        // saves an episode by URI without distinguishing show/episode, so we
        // currently route through the show-level follow toggle since saving
        // episodes individually is a separate user gesture not covered by this
        // page. Stub for now so the heart click is acknowledged.
        _logger?.LogDebug("Episode like requested but episode-level save isn't wired yet: {Uri}", e?.Uri);
    }

    // ── Recommendations ────────────────────────────────────────────────────

    private void RecommendedShow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is ContentCard card && !string.IsNullOrEmpty(card.NavigationUri))
        {
            var rec = card.Tag as ShowRecommendationDto;
            ViewModel.OpenRecommendationCommand.Execute(rec);
        }
    }

    // ── Share ──────────────────────────────────────────────────────────────

    private void TopicToken_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ShowTopicDto topic || string.IsNullOrWhiteSpace(topic.Title))
            return;

        var parameter = new ContentNavigationParameter
        {
            Uri = string.IsNullOrWhiteSpace(topic.Uri)
                ? $"wavee:podcast-topic:{topic.Title}"
                : topic.Uri!,
            Title = topic.Title,
            Subtitle = "Podcast genre"
        };

        NavigationHelpers.OpenPodcastBrowse(parameter, NavigationHelpers.IsCtrlPressed());
    }

    private void Share_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ViewModel.ShareUrl)) return;
        _notificationService?.Show(
            "Show link copied",
            NotificationSeverity.Success,
            TimeSpan.FromSeconds(3));
    }
}
