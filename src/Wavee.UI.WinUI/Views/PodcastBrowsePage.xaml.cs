using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Wavee.Controls.HeroCarousel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class PodcastBrowsePage : Page, ITabBarItemContent, IDisposable
{
    private const int ShimmerCollapseDelayMs = 250;

    private TabItemParameter? _tabItemParameter;
    private bool _isLoaded;
    private bool _isDisposed;
    private bool _isViewModelSubscribed;
    private bool _showingContent;
    private bool _crossfadeScheduled;
    private bool _heroEventsHooked;

    public PodcastBrowseViewModel ViewModel { get; }

    public ShimmerLoadGate ShimmerGate { get; } = new();

    public TabItemParameter? TabItemParameter => _tabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public PodcastBrowsePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<PodcastBrowseViewModel>();
        InitializeComponent();
        PrepareLoadingVisualState(scrollToTop: false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Bindings?.StopTracking();
        Dispose();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var parameter = e.Parameter as ContentNavigationParameter;
        PrepareLoadingVisualState(scrollToTop: true);
        await ViewModel.LoadAsync(parameter);
        ApplyTabParameter();
        UpdateSidebarVisibility();
        SyncHeroSlides();
        TryShowContentNow();
    }

    public void RefreshWithParameter(object? parameter)
    {
        _ = LoadFromParameterAsync(parameter as ContentNavigationParameter);
    }

    private async Task LoadFromParameterAsync(ContentNavigationParameter? parameter)
    {
        PrepareLoadingVisualState(scrollToTop: true);
        await ViewModel.LoadAsync(parameter);
        ApplyTabParameter();
        UpdateSidebarVisibility();
        SyncHeroSlides();
        TryShowContentNow();
    }

    private void ApplyTabParameter()
    {
        var parameter = new ContentNavigationParameter
        {
            Uri = ViewModel.CurrentUri,
            Title = ViewModel.Title,
            Subtitle = ViewModel.Subtitle,
            ImageUrl = ViewModel.SelectedHeroImageUrl
        };

        _tabItemParameter = new TabItemParameter(NavigationPageType.PodcastBrowse, parameter)
        {
            Title = ViewModel.Title
        };
        ContentChanged?.Invoke(this, _tabItemParameter);
    }

    private void PrepareLoadingVisualState(bool scrollToTop)
    {
        _showingContent = false;
        _crossfadeScheduled = false;

        if (scrollToTop && _isLoaded && MainScroll is not null)
        {
            MainScroll.ScrollTo(
                MainScroll.HorizontalOffset,
                0,
                new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
        }

        ShimmerGate.Reset(() => PodcastBrowseShimmer, () => PodcastBrowseContent);
    }

    private void TryShowContentNow()
    {
        if (_showingContent || _crossfadeScheduled || ViewModel.IsLoading || !_isLoaded)
            return;

        ScheduleCrossfade();
    }

    private async void ScheduleCrossfade()
    {
        _crossfadeScheduled = true;

        await Task.Yield();
        await Task.Delay(16);

        if (_isDisposed || !_isLoaded || _showingContent || ViewModel.IsLoading)
        {
            _crossfadeScheduled = false;
            return;
        }

        SyncHeroSlides();
        CrossfadeToContent();
    }

    private void CrossfadeToContent()
    {
        if (_showingContent)
            return;

        _showingContent = true;
        _crossfadeScheduled = false;

        _ = ShimmerGate.RunCrossfadeAsync(PodcastBrowseShimmer, PodcastBrowseContent, FrameworkLayer.Xaml,
            () => _showingContent);
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        SubscribeViewModel();
        HookHeroEvents();
        UpdateSidebarVisibility();
        SyncHeroSlides();
        TryShowContentNow();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        UnhookHeroEvents();
        UnsubscribeViewModel();
    }

    private void HookHeroEvents()
    {
        if (_heroEventsHooked || HeroCarousel is null)
            return;
        HeroCarousel.CtaClicked += OnHeroCtaClicked;
        ViewModel.HeroColorsResolved += OnHeroColorsResolved;
        _heroEventsHooked = true;
    }

    private void UnhookHeroEvents()
    {
        if (!_heroEventsHooked)
            return;
        if (HeroCarousel is not null)
            HeroCarousel.CtaClicked -= OnHeroCtaClicked;
        ViewModel.HeroColorsResolved -= OnHeroColorsResolved;
        _heroEventsHooked = false;
    }

    private void OnHeroColorsResolved(object? sender, EventArgs e) => SyncHeroSlides();

    private void SubscribeViewModel()
    {
        if (_isViewModelSubscribed)
            return;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _isViewModelSubscribed = true;
    }

    private void UnsubscribeViewModel()
    {
        if (!_isViewModelSubscribed)
            return;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _isViewModelSubscribed = false;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.IsLoading):
                UpdateSidebarVisibility();
                if (!ViewModel.IsLoading)
                    TryShowContentNow();
                break;
            case nameof(ViewModel.HasAllPodcastCategories):
            case nameof(ViewModel.ShowSidebar):
            case nameof(ViewModel.ShowSidebarShimmer):
                UpdateSidebarVisibility();
                break;
            case nameof(ViewModel.HasHeroShows):
            case nameof(ViewModel.ShowHeroShows):
                SyncHeroSlides();
                break;
        }
    }

    private void UpdateSidebarVisibility()
    {
        if (CategorySidebar is null)
            return;

        CategorySidebar.Visibility = ViewModel.ShowSidebar || ViewModel.ShowSidebarShimmer
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ─── Hero carousel wiring ──────────────────────────────────────────────

    private void SyncHeroSlides()
    {
        if (HeroCarousel is null)
            return;
        // Build a fresh list and assign in one shot. The control rebuilds once on
        // ItemsSource change; per-item Slides.Add would re-rebuild for each entry.
        var slides = new List<HeroCarouselSlide>(ViewModel.HeroShows.Count);
        foreach (var vm in ViewModel.HeroShows)
            slides.Add(vm.ToHeroSlide());
        HeroCarousel.ItemsSource = slides;
    }

    private void OnHeroCtaClicked(object? sender, int index)
    {
        if (index >= 0 && index < ViewModel.HeroShows.Count)
            ViewModel.OpenItem(ViewModel.HeroShows[index]);
    }

    // ─── Sidebar / shelf interactions ──────────────────────────────────────

    private void BrowseItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PodcastBrowseItemViewModel item })
            return;
        ViewModel.OpenItem(item);
    }

    private async void SectionAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PodcastBrowseSectionViewModel section })
            await ViewModel.ActivateSectionAsync(section);
    }

    private void BrowseBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        ViewModel.OpenBreadcrumb(args.Index);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        UnhookHeroEvents();
        UnsubscribeViewModel();
        ViewModel.Dispose();
    }
}
