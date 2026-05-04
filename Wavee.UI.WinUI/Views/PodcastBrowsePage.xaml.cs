using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class PodcastBrowsePage : Page, ITabBarItemContent, IDisposable
{
    private const int ShimmerCollapseDelayMs = 250;
    // Card geometry. Fill the content column so the first hero aligns with the
    // page title, while still capping ultra-wide layouts for readability.
    private const double HeroCardWidthRatio = 1.0;
    private const double HeroCardMinWidth = 320;
    // Bumped 760 → 1100 so the hero fills the content column the way the MS
    // Store reference does. Combined with the side-cards repeater being
    // collapsed, the page now has a single dominant hero panel per row.
    private const double HeroCardMaxWidth = 1100;
    private const double HeroCardHeight = 420;
    private const double HeroSideColumnWidth = 380;
    private const double HeroSideColumnSpacing = 12;
    private const double HeroCardCornerRadius = 20;
    private const double HeroShadowBleed = 24;
    // Compact browse header + carousel + pips. Keep this close to the card height
    // so category pages feel like a catalog, not a landing-page hero.
    private const double HeroSurfaceMinHeight = 500;
    private const double HeroSurfaceMaxHeight = 540;
    // Chevrons sit just inside the active card edge (MS Store-style overlap).
    private const double HeroChevronInset = 12;
    // Parallax during a layered transition: active card's artwork shifts
    // opposite to the slide for depth. Bumped from the original scroll-feel
    // values (44 / 0.12) so the shift reads clearly across the brief 280–500ms
    // slide window — was previously too subtle to notice mid-animation.
    private const double HeroArtParallaxStrength = 0.18;
    private const double HeroArtParallaxMaxShiftPx = 80;

    // Layered transition timings.
    private const int HeroSingleStepMs = 500;
    private const int HeroChainStepMs = 280;
    private const int HeroRevertStepMs = 260;
    private const int HeroFrameMs = 16;
    private const double HeroDragCommitDivisor = 4.0;

    private readonly DispatcherTimer _heroTimer = new()
    {
        Interval = TimeSpan.FromSeconds(6)
    };

    private TabItemParameter? _tabItemParameter;
    private bool _isLoaded;
    private bool _isDisposed;
    private bool _isViewModelSubscribed;
    private bool _showingContent;
    private bool _crossfadeScheduled;

    // Layered hero stage state. _heroCards is parallel to ViewModel.HeroShows
    // (one realised card per item). _currentHeroIndex is the index of the active
    // card (the one at TranslateTransform.X=0); other cards sit off-stage.
    private readonly List<FrameworkElement> _heroCards = new();
    private int _currentHeroIndex = -1;
    private CancellationTokenSource? _transitionCts;
    private bool _isTransitioning;
    // While a transition is in progress (animation OR drag), these describe which
    // pair of heroes the page is between, so UpdatePageTintFromState and
    // ApplyTransitionParallax can lerp continuously.
    private int _transitionFromIndex = -1;
    private int _transitionToIndex = -1;
    private int _transitionDirection;
    private double _transitionProgress;

    // Drag/swipe state — driven by Manipulation events on HeroStage.
    private bool _isDragging;
    private FrameworkElement? _dragIncomingCard;
    private int _dragIncomingIndex = -1;
    private int _dragDirection;

    public PodcastBrowseViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => _tabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public PodcastBrowsePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<PodcastBrowseViewModel>();
        InitializeComponent();
        PrepareLoadingVisualState(scrollToTop: false);
        _heroTimer.Tick += HeroTimer_Tick;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var parameter = e.Parameter as ContentNavigationParameter;
        PrepareLoadingVisualState(scrollToTop: true);
        await ViewModel.LoadAsync(parameter);
        ApplyTabParameter();
        UpdateSidebarVisibility();
        UpdateHeroCardMetrics();
        RebuildHeroStage();
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
        UpdateHeroCardMetrics();
        RebuildHeroStage();
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
        _heroTimer.Stop();
        _showingContent = false;
        _crossfadeScheduled = false;

        if (scrollToTop && _isLoaded && MainScroll is not null)
        {
            MainScroll.ScrollTo(
                MainScroll.HorizontalOffset,
                0,
                new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
        }

        if (HeroCarouselShimmer is not null)
        {
            HeroCarouselShimmer.Visibility = Visibility.Visible;
            HeroCarouselShimmer.Opacity = 1;
        }

        if (PodcastBrowseShimmer is not null)
        {
            PodcastBrowseShimmer.Visibility = Visibility.Visible;
            PodcastBrowseShimmer.Opacity = 1;
        }

        if (HeroStage is not null)
            HeroStage.Opacity = 0;
        if (HeroPips is not null)
            HeroPips.Opacity = 0;
        if (PodcastBrowseContent is not null)
            PodcastBrowseContent.Opacity = 0;
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

        // Same deferal principle as ArtistPage: wait for bindings/repeaters to
        // measure before fading away the skeleton, otherwise shelves and hero
        // cards can grow during the animation and visibly shift the page.
        await Task.Yield();
        await Task.Delay(16);

        if (_isDisposed || !_isLoaded || _showingContent || ViewModel.IsLoading)
        {
            _crossfadeScheduled = false;
            return;
        }

        UpdateHeroCardMetrics();
        RebuildHeroStage();
        UpdatePageTintFromState();

        CrossfadeToContent();
    }

    private async void CrossfadeToContent()
    {
        if (_showingContent)
            return;

        _showingContent = true;
        _crossfadeScheduled = false;

        AnimationBuilder.Create()
            .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200),
                layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
            .Start(HeroCarouselShimmer);

        AnimationBuilder.Create()
            .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200),
                layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
            .Start(PodcastBrowseShimmer);

        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(300),
                delay: TimeSpan.FromMilliseconds(100),
                layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
            .Start(HeroStage);

        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(300),
                delay: TimeSpan.FromMilliseconds(100),
                layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
            .Start(HeroPips);

        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(300),
                delay: TimeSpan.FromMilliseconds(100),
                layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
            .Start(PodcastBrowseContent);

        await Task.Delay(ShimmerCollapseDelayMs);
        if (_showingContent)
        {
            HeroCarouselShimmer.Visibility = Visibility.Collapsed;
            PodcastBrowseShimmer.Visibility = Visibility.Collapsed;
        }

        UpdateHeroTimer();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        SubscribeViewModel();
        UpdateHeroTimer();
        UpdateSidebarVisibility();
        UpdateHeroCardMetrics();
        RebuildHeroStage();
        UpdatePageTintFromState();
        TryShowContentNow();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _heroTimer.Stop();
        _transitionCts?.Cancel();
        UnsubscribeViewModel();
    }

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
            case nameof(ViewModel.SelectedHero):
                _ = AnimateToHeroAsync(ViewModel.SelectedHero);
                break;
            case nameof(ViewModel.HasAllPodcastCategories):
            case nameof(ViewModel.ShowSidebar):
            case nameof(ViewModel.ShowSidebarShimmer):
                UpdateSidebarVisibility();
                break;
            case nameof(ViewModel.HasHeroShows):
            case nameof(ViewModel.ShowHeroShows):
                UpdateHeroCardMetrics();
                RebuildHeroStage();
                UpdatePageTintFromState();
                break;
        }
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateHeroCardMetrics();
        // Re-snap card positions to the new stage width whenever we're not
        // mid-transition. Otherwise the running animation keeps driving X.
        if (!_isTransitioning && !_isDragging && _currentHeroIndex >= 0)
            PositionCardsForActive(_currentHeroIndex);
    }

    private void UpdateSidebarVisibility()
    {
        if (CategorySidebar is null)
            return;

        // Sidebar is the Zune-style "all genres" navigation. Visible whenever the
        // master AllPodcastCategories collection has data — on the root AND on every
        // sub-page (the master list is loaded once and reused across navigations).
        // Also visible during the initial load so the shimmer skeleton can render.
        CategorySidebar.Visibility = ViewModel.ShowSidebar || ViewModel.ShowSidebarShimmer
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateHeroCardMetrics()
    {
        var availableWidth = HeroCarouselHost?.ActualWidth > 0
            ? HeroCarouselHost.ActualWidth
            : HeroSurface?.ActualWidth > 52
                ? HeroSurface.ActualWidth - 52
                : ActualWidth;

        if (availableWidth <= 0)
            return;

        var minWidth = Math.Min(HeroCardMinWidth, availableWidth);
        var cardWidth = Math.Round(Math.Clamp(availableWidth * HeroCardWidthRatio, minWidth, HeroCardMaxWidth));
        const double cardHeight = HeroCardHeight;

        foreach (var hero in ViewModel.HeroShows)
        {
            hero.HeroCardWidth = cardWidth;
            hero.HeroCardHeight = cardHeight;
        }

        var carouselHeight = cardHeight + (HeroShadowBleed * 2);
        HeroSurface.Height = Math.Clamp(carouselHeight + 80, HeroSurfaceMinHeight, HeroSurfaceMaxHeight);
        if (HeroCarouselHost is not null)
            HeroCarouselHost.Height = carouselHeight;
        if (HeroCarouselShimmer is not null)
        {
            HeroCarouselShimmer.Width = cardWidth;
            HeroCarouselShimmer.Height = cardHeight;
        }

        // Chevrons sit just inside the active card's edge — overlap pattern from
        // Microsoft Store. The card is centred, so its left edge lives at
        // (availableWidth - cardWidth)/2; the chevron's left edge is one inset
        // further right, putting it inside the card's rounded frame.
        var cardLeftEdge = (availableWidth - cardWidth) / 2.0;
        var inset = Math.Max(HeroChevronInset, cardLeftEdge + HeroChevronInset);
        if (HeroPrevButton is not null)
            HeroPrevButton.Margin = new Thickness(inset, 0, 0, 0);
        if (HeroNextButton is not null)
            HeroNextButton.Margin = new Thickness(0, 0, inset, 0);
    }

    // ─── Layered hero stage ───────────────────────────────────────────────

    private void RebuildHeroStage()
    {
        if (HeroStage is null)
            return;

        // Cancel any in-flight transition before tearing down.
        _transitionCts?.Cancel();
        _isTransitioning = false;
        _isDragging = false;
        _dragIncomingCard = null;
        _dragIncomingIndex = -1;

        HeroStage.Children.Clear();
        _heroCards.Clear();
        _currentHeroIndex = -1;

        var heroes = ViewModel.HeroShows;
        if (heroes.Count == 0)
            return;

        var template = (DataTemplate)Resources["HeroShowTemplate"];
        if (template is null)
            return;

        // Wrap each card in a ContentControl so the framework wires DataContext
        // through to x:Bind in the template the same way ItemsRepeater does.
        // The ContentControl inherits the inner Button's natural size.
        for (var i = 0; i < heroes.Count; i++)
        {
            var card = new ContentControl
            {
                Content = heroes[i],
                ContentTemplate = template,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsTabStop = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TranslateTransform()
            };
            HeroStage.Children.Add(card);
            _heroCards.Add(card);
        }

        var initialIndex = Math.Clamp(ViewModel.SelectedHeroIndex, 0, heroes.Count - 1);
        _currentHeroIndex = initialIndex;
        PositionCardsForActive(initialIndex);
    }

    private void PositionCardsForActive(int activeIndex)
    {
        var stageWidth = GetStageWidth();
        for (var i = 0; i < _heroCards.Count; i++)
        {
            var card = _heroCards[i];
            var transform = EnsureTranslate(card);
            transform.X = i == activeIndex ? 0 : (i < activeIndex ? -stageWidth : stageWidth);
            card.IsHitTestVisible = i == activeIndex;
            // Only the active card renders. Non-active cards live off-stage in the
            // layered model AND would otherwise leak across the sidebar / shelves
            // because nothing clips the carousel host (we removed that clip on
            // purpose so corners survive scroll). Visibility=Collapsed guarantees
            // they don't paint until a transition explicitly shows them.
            card.Visibility = i == activeIndex ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetZIndex(card, i == activeIndex ? 5 : 1);
            ResetCardParallax(card);
        }
    }

    private double GetStageWidth()
    {
        if (HeroStage?.ActualWidth > 0)
            return HeroStage.ActualWidth;
        if (HeroCarouselHost?.ActualWidth > 0)
            return HeroCarouselHost.ActualWidth;
        return HeroCardMaxWidth;
    }

    private static TranslateTransform EnsureTranslate(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform existing)
            return existing;
        var fresh = new TranslateTransform();
        element.RenderTransform = fresh;
        return fresh;
    }

    private async Task AnimateToHeroAsync(PodcastBrowseItemViewModel? hero)
    {
        if (hero is null)
            return;
        var index = ViewModel.HeroShows.IndexOf(hero);
        if (index < 0)
            return;
        await AnimateToIndexAsync(index);
    }

    private async Task AnimateToIndexAsync(int targetIndex)
    {
        if (HeroStage is null || _heroCards.Count == 0)
            return;
        if (targetIndex < 0 || targetIndex >= _heroCards.Count)
            return;
        if (targetIndex == _currentHeroIndex)
            return;

        // Cancel any in-flight transition; start fresh from current state.
        _transitionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _transitionCts = cts;
        var token = cts.Token;
        _isTransitioning = true;

        try
        {
            // Step duration depends on whether this is a single-step (chevron / pip
            // adjacent / drag commit) or a chained jump (pip across multiple cards).
            var initialStepCount = Math.Abs(targetIndex - _currentHeroIndex);
            var durationMs = initialStepCount > 1 ? HeroChainStepMs : HeroSingleStepMs;

            while (_currentHeroIndex != targetIndex && !token.IsCancellationRequested)
            {
                var direction = Math.Sign(targetIndex - _currentHeroIndex);
                var nextIndex = _currentHeroIndex + direction;
                await AnimateStepAsync(_currentHeroIndex, nextIndex, direction, durationMs, token);
                if (token.IsCancellationRequested)
                    break;
                _currentHeroIndex = nextIndex;
            }
        }
        finally
        {
            _isTransitioning = false;
            _transitionFromIndex = -1;
            _transitionToIndex = -1;
            _transitionDirection = 0;
            _transitionProgress = 0;
            if (!token.IsCancellationRequested)
                UpdatePageTintFromState();
        }
    }

    private async Task AnimateStepAsync(int fromIndex, int toIndex, int direction, int durationMs, CancellationToken token)
    {
        var stageWidth = GetStageWidth();
        if (stageWidth <= 0)
            return;

        var fromCard = _heroCards[fromIndex];
        var toCard = _heroCards[toIndex];
        var fromTransform = EnsureTranslate(fromCard);
        var toTransform = EnsureTranslate(toCard);

        // Active card stays anchored at X=0; incoming starts off-stage on the far
        // side and slides over the active. Z-order: incoming above active.
        fromTransform.X = 0;
        toTransform.X = direction * stageWidth;
        // Reveal both cards for the duration of the slide; previous active goes
        // back to Collapsed once the slide settles.
        fromCard.Visibility = Visibility.Visible;
        toCard.Visibility = Visibility.Visible;
        toCard.IsHitTestVisible = false;
        Canvas.SetZIndex(fromCard, 5);
        Canvas.SetZIndex(toCard, 10);

        _transitionFromIndex = fromIndex;
        _transitionToIndex = toIndex;
        _transitionDirection = direction;

        var startMs = Environment.TickCount64;
        while (true)
        {
            if (token.IsCancellationRequested)
                return;
            var elapsed = Environment.TickCount64 - startMs;
            var raw = Math.Clamp(elapsed / (double)durationMs, 0.0, 1.0);
            var eased = EaseOutCubic(raw);
            _transitionProgress = eased;

            toTransform.X = direction * stageWidth * (1 - eased);
            ApplyTransitionParallax(fromCard, toCard, eased, direction);
            UpdatePageTintFromState();

            if (raw >= 1.0)
                break;
            try { await Task.Delay(HeroFrameMs, token); }
            catch (TaskCanceledException) { return; }
        }

        // Settle: previous active is bumped off-stage in the direction of travel
        // and hidden so it doesn't paint over neighbours.
        toTransform.X = 0;
        fromTransform.X = -direction * stageWidth;
        fromCard.IsHitTestVisible = false;
        fromCard.Visibility = Visibility.Collapsed;
        toCard.IsHitTestVisible = true;
        Canvas.SetZIndex(fromCard, 1);
        Canvas.SetZIndex(toCard, 5);
        ResetCardParallax(fromCard);
        ResetCardParallax(toCard);
    }

    // Approximation of cubic-bezier(0.32, 0.72, 0, 1) — close enough for the slide feel.
    private static double EaseOutCubic(double t)
    {
        var clamped = Math.Clamp(t, 0.0, 1.0);
        return 1 - Math.Pow(1 - clamped, 3);
    }

    private static void ResetCardParallax(FrameworkElement card)
    {
        if (FindDescendant<Border>(card, "HeroArtworkHost") is { } art)
            SetBrushTranslateX(art, 0);
    }

    private static void ApplyTransitionParallax(FrameworkElement fromCard, FrameworkElement toCard, double progress, int direction)
    {
        // Active card's artwork shifts opposite to the slide for depth.
        if (FindDescendant<Border>(fromCard, "HeroArtworkHost") is { } fromArt && fromArt.ActualWidth > 0)
        {
            var maxShift = Math.Min(HeroArtParallaxMaxShiftPx, fromArt.ActualWidth * HeroArtParallaxStrength);
            SetBrushTranslateX(fromArt, progress * direction * -maxShift);
        }
        // Incoming card's artwork starts offset (in the slide direction) and settles to 0.
        if (FindDescendant<Border>(toCard, "HeroArtworkHost") is { } toArt && toArt.ActualWidth > 0)
        {
            var maxShift = Math.Min(HeroArtParallaxMaxShiftPx, toArt.ActualWidth * HeroArtParallaxStrength);
            SetBrushTranslateX(toArt, (1 - progress) * direction * maxShift);
        }
    }

    // ─── Drag / Swipe ──────────────────────────────────────────────────────
    //
    // Manipulation events instead of raw PointerPressed/Moved/Released because
    // each card is a Button which captures the pointer on press — that capture
    // routes subsequent PointerMoved/Released exclusively to the Button, never
    // to HeroStage. The manipulation system runs at a higher level and aggregates
    // mouse / trackpad / touch / pen drags into a single Translation stream that
    // reaches HeroStage even with a child holding the pointer.

    private void HeroStage_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        if (_isTransitioning) { e.Complete(); return; }
        if (_heroCards.Count <= 1) { e.Complete(); return; }

        _isDragging = true;
        _dragIncomingCard = null;
        _dragIncomingIndex = -1;
        _dragDirection = 0;
        PauseHeroAutoAdvance();
    }

    private void HeroStage_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (!_isDragging)
            return;
        if (_currentHeroIndex < 0 || _heroCards.Count == 0)
            return;

        var delta = e.Cumulative.Translation.X;
        var stageWidth = GetStageWidth();

        // Drag right (positive delta) reveals the previous card → direction = -1.
        var direction = -Math.Sign(delta);
        if (direction == 0)
            return;

        if (direction != _dragDirection || _dragIncomingCard is null)
        {
            _dragDirection = direction;
            var incomingIndex = _currentHeroIndex + direction;
            if (incomingIndex < 0 || incomingIndex >= _heroCards.Count)
            {
                _dragIncomingCard = null;
                _dragIncomingIndex = -1;
                return;
            }
            _dragIncomingIndex = incomingIndex;
            _dragIncomingCard = _heroCards[incomingIndex];
            _dragIncomingCard.Visibility = Visibility.Visible;
            Canvas.SetZIndex(_dragIncomingCard, 10);
        }

        if (_dragIncomingCard is null)
            return;

        var clampedDelta = Math.Clamp(delta, -stageWidth, stageWidth);
        // Incoming starts at direction * stageWidth (matches AnimateStepAsync) and
        // tracks the drag delta toward 0.
        EnsureTranslate(_dragIncomingCard).X = (direction * stageWidth) + clampedDelta;
        var progress = Math.Clamp(Math.Abs(clampedDelta) / stageWidth, 0.0, 1.0);
        _transitionFromIndex = _currentHeroIndex;
        _transitionToIndex = _dragIncomingIndex;
        _transitionDirection = direction;
        _transitionProgress = progress;
        ApplyTransitionParallax(_heroCards[_currentHeroIndex], _dragIncomingCard, progress, direction);
        UpdatePageTintFromState();
    }

    private async void HeroStage_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        if (!_isDragging)
            return;
        _isDragging = false;
        UpdateHeroTimer();

        if (_dragIncomingCard is null || _dragIncomingIndex < 0)
        {
            if (_currentHeroIndex >= 0)
                PositionCardsForActive(_currentHeroIndex);
            UpdatePageTintFromState();
            return;
        }

        var stageWidth = GetStageWidth();
        var distanceFromStart = Math.Abs(EnsureTranslate(_dragIncomingCard).X - (_dragDirection * stageWidth));
        var threshold = stageWidth / HeroDragCommitDivisor;

        if (distanceFromStart >= threshold)
        {
            var incoming = _dragIncomingCard;
            var incomingIndex = _dragIncomingIndex;
            var direction = _dragDirection;
            _dragIncomingCard = null;
            _dragIncomingIndex = -1;
            await CommitDragAsync(incoming, incomingIndex, direction);
            // Sync the VM. Because _currentHeroIndex already matches incomingIndex,
            // the resulting AnimateToIndexAsync is a no-op.
            ViewModel.SelectHero(incomingIndex);
        }
        else
        {
            await RevertDragAsync(_dragIncomingCard, _dragDirection);
            if (_currentHeroIndex >= 0)
                PositionCardsForActive(_currentHeroIndex);
            UpdatePageTintFromState();
        }
    }

    private async Task CommitDragAsync(FrameworkElement incoming, int incomingIndex, int direction)
    {
        var stageWidth = GetStageWidth();
        var transform = EnsureTranslate(incoming);
        var startX = transform.X;
        var startProgress = _transitionProgress;
        var fromCard = _heroCards[_currentHeroIndex];

        var startMs = Environment.TickCount64;
        while (true)
        {
            var elapsed = Environment.TickCount64 - startMs;
            var raw = Math.Clamp(elapsed / (double)HeroChainStepMs, 0.0, 1.0);
            var eased = EaseOutCubic(raw);
            transform.X = startX + (0 - startX) * eased;
            _transitionProgress = startProgress + ((1.0 - startProgress) * eased);
            ApplyTransitionParallax(fromCard, incoming, _transitionProgress, direction);
            UpdatePageTintFromState();
            if (raw >= 1.0)
                break;
            await Task.Delay(HeroFrameMs);
        }

        transform.X = 0;
        EnsureTranslate(fromCard).X = -direction * stageWidth;
        fromCard.IsHitTestVisible = false;
        fromCard.Visibility = Visibility.Collapsed;
        incoming.IsHitTestVisible = true;
        incoming.Visibility = Visibility.Visible;
        Canvas.SetZIndex(fromCard, 1);
        Canvas.SetZIndex(incoming, 5);
        ResetCardParallax(fromCard);
        ResetCardParallax(incoming);
        _currentHeroIndex = incomingIndex;
        _transitionFromIndex = -1;
        _transitionToIndex = -1;
        _transitionDirection = 0;
        _transitionProgress = 0;
    }

    private async Task RevertDragAsync(FrameworkElement incoming, int direction)
    {
        var stageWidth = GetStageWidth();
        var transform = EnsureTranslate(incoming);
        var startX = transform.X;
        // Send the incoming card back to its starting off-stage position (matches
        // direction * stageWidth from AnimateStepAsync).
        var endX = direction * stageWidth;

        var startMs = Environment.TickCount64;
        while (true)
        {
            var elapsed = Environment.TickCount64 - startMs;
            var raw = Math.Clamp(elapsed / (double)HeroRevertStepMs, 0.0, 1.0);
            var eased = EaseOutCubic(raw);
            transform.X = startX + ((endX - startX) * eased);
            if (raw >= 1.0)
                break;
            await Task.Delay(HeroFrameMs);
        }
        ResetCardParallax(incoming);
        // Send the reverted incoming card back to Collapsed so it stops painting.
        incoming.Visibility = Visibility.Collapsed;
        if (_currentHeroIndex >= 0 && _currentHeroIndex < _heroCards.Count)
            ResetCardParallax(_heroCards[_currentHeroIndex]);
        _transitionFromIndex = -1;
        _transitionToIndex = -1;
        _transitionDirection = 0;
        _transitionProgress = 0;
    }

    private void HeroTimer_Tick(object? sender, object e)
    {
        // Mid-transition / mid-drag ticks are absorbed.
        if (_isTransitioning || _isDragging)
            return;
        ViewModel.SelectNextHero();
    }

    private void UpdateHeroTimer()
    {
        if (_isLoaded && ViewModel.HeroShows.Count > 1)
        {
            if (!_heroTimer.IsEnabled)
                _heroTimer.Start();
        }
        else
        {
            _heroTimer.Stop();
        }
    }

    private void PauseHeroAutoAdvance()
    {
        _heroTimer.Stop();
    }

    private void RestartHeroAutoAdvance()
    {
        if (!_isLoaded || ViewModel.HeroShows.Count <= 1)
            return;
        _heroTimer.Stop();
        _heroTimer.Start();
    }

    private void BrowseItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PodcastBrowseItemViewModel item } fe)
            return;

        // Hero cards are bespoke (not ContentCard) so we hand-prepare the connected
        // animation here. Shelf items go through ContentCard.PrepareConnectedAnimation,
        // which now picks the PodcastArt key for show:/episode: URIs.
        if (item.Kind == PodcastBrowseItemKind.Show)
        {
            if (FindHeroArtwork(fe) is { } artwork)
                ConnectedAnimationHelper.PrepareAnimation(ConnectedAnimationHelper.PodcastArt, artwork);
        }

        ViewModel.OpenItem(item);
    }

    private static FrameworkElement? FindHeroArtwork(DependencyObject root)
    {
        // Both hero templates now use a named Border with ImageBrush background.
        return FindDescendant<Border>(root, "HeroArtworkHost")
            ?? (FrameworkElement?)FindDescendant<Border>(root, "HeroSideArtworkHost");
    }

    private static T? FindDescendant<T>(DependencyObject root, string? name = null)
        where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T element && (name is null || element.Name == name))
                return element;

            if (FindDescendant<T>(child, name) is { } nested)
                return nested;
        }
        return null;
    }

    // Pointer / focus only pause + resume auto-advance — they no longer auto-select
    // the hovered card or auto-scroll the carousel. Selection is user-driven via
    // chevrons, PipsPager, click, or snap-scroll.
    private void HeroCard_PointerEntered(object sender, PointerRoutedEventArgs e)
        => PauseHeroAutoAdvance();

    private void HeroCard_PointerExited(object sender, PointerRoutedEventArgs e)
        => UpdateHeroTimer();

    private void HeroCard_GotFocus(object sender, RoutedEventArgs e)
        => PauseHeroAutoAdvance();

    private void HeroCard_LostFocus(object sender, RoutedEventArgs e)
        => UpdateHeroTimer();

    private async void SectionAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PodcastBrowseSectionViewModel section })
        {
            await ViewModel.ActivateSectionAsync(section);
            UpdateHeroTimer();
        }
    }

    private void BrowseBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        ViewModel.OpenBreadcrumb(args.Index);
    }

    private void HeroRoundedClip_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            ApplyHeroRoundedClip(element);
            element.DispatcherQueue.TryEnqueue(() => ApplyHeroRoundedClip(element));
        }
    }

    private void HeroRoundedClip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element)
            ApplyHeroRoundedClip(element);
    }

    private void HeroRoundedClip_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement element)
            ElementCompositionPreview.GetElementVisual(element).Clip = null;
    }

    private static void ApplyHeroRoundedClip(FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var clip = compositor.CreateRectangleClip();
        clip.Right = (float)element.ActualWidth;
        clip.Bottom = (float)element.ActualHeight;
        var radii = GetClipRadii(element);
        clip.TopLeftRadius = radii.TopLeft;
        clip.TopRightRadius = radii.TopRight;
        clip.BottomLeftRadius = radii.BottomLeft;
        clip.BottomRightRadius = radii.BottomRight;
        visual.Clip = clip;
    }

    private static (Vector2 TopLeft, Vector2 TopRight, Vector2 BottomLeft, Vector2 BottomRight) GetClipRadii(FrameworkElement element)
    {
        if (element is Border border)
            return ToVectors(border.CornerRadius);
        if (element is Control control)
            return ToVectors(control.CornerRadius);

        // For Panel / Grid (no native CornerRadius): inherit the nearest ancestor
        // Border or Control's CornerRadius. The HeroCardContentGrid sits directly
        // inside HeroCardClipRoot Border (CornerRadius=20), and the side card's
        // grid inside HeroSideCardClipRoot (CornerRadius=14).
        var parent = VisualTreeHelper.GetParent(element);
        while (parent is not null)
        {
            if (parent is Border b)
                return ToVectors(b.CornerRadius);
            if (parent is Control c)
                return ToVectors(c.CornerRadius);
            parent = VisualTreeHelper.GetParent(parent);
        }

        var fallbackRadius = new Vector2((float)HeroCardCornerRadius);
        return (fallbackRadius, fallbackRadius, fallbackRadius, fallbackRadius);
    }

    private static (Vector2 TopLeft, Vector2 TopRight, Vector2 BottomLeft, Vector2 BottomRight) ToVectors(CornerRadius radius)
        => (
            new Vector2((float)radius.TopLeft),
            new Vector2((float)radius.TopRight),
            new Vector2((float)radius.BottomLeft),
            new Vector2((float)radius.BottomRight));

    private void HeroPrev_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectPrevHero();
        RestartHeroAutoAdvance();
    }

    private void HeroNext_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectNextHero();
        RestartHeroAutoAdvance();
    }

    private void HeroPips_SelectedIndexChanged(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        // Bound OneWay; the timer-driven push to SelectedPageIndex re-fires this event.
        // Guard against re-entry so we don't bounce the timer reset for our own writes.
        var newIndex = sender.SelectedPageIndex;
        if (newIndex == ViewModel.SelectedHeroIndex)
            return;
        ViewModel.SelectHero(newIndex);
        RestartHeroAutoAdvance();
    }

    // Drives PageTintTopStop.Color from the layered transition state.
    // - At rest: uses the current hero's HeroPrimaryColor.
    // - During a slide / drag: lerps between fromIndex and toIndex by progress
    //   so the page recolors continuously as the incoming card slides in.
    private void UpdatePageTintFromState()
    {
        if (PageTintTopStop is null)
            return;

        var heroes = ViewModel.HeroShows;
        if (heroes.Count == 0)
        {
            PageTintTopStop.Color = Colors.Transparent;
            return;
        }

        if (_transitionFromIndex >= 0 && _transitionToIndex >= 0
            && _transitionFromIndex < heroes.Count && _transitionToIndex < heroes.Count)
        {
            PageTintTopStop.Color = LerpColor(
                heroes[_transitionFromIndex].HeroPrimaryColor,
                heroes[_transitionToIndex].HeroPrimaryColor,
                (float)_transitionProgress);
            return;
        }

        var idx = _currentHeroIndex >= 0
            ? Math.Clamp(_currentHeroIndex, 0, heroes.Count - 1)
            : Math.Clamp(ViewModel.SelectedHeroIndex, 0, heroes.Count - 1);
        PageTintTopStop.Color = heroes[idx].HeroPrimaryColor;
    }

    private static Color LerpColor(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (byte)(a.A + ((b.A - a.A) * t)),
            (byte)(a.R + ((b.R - a.R) * t)),
            (byte)(a.G + ((b.G - a.G) * t)),
            (byte)(a.B + ((b.B - a.B) * t)));
    }

    private static void SetBrushTranslateX(Border artHost, double x)
    {
        if (artHost.Background is not ImageBrush brush)
            return;
        if (brush.Transform is TranslateTransform existing)
        {
            existing.X = x;
            return;
        }
        brush.Transform = new TranslateTransform { X = x };
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _heroTimer.Stop();
        _heroTimer.Tick -= HeroTimer_Tick;
        _transitionCts?.Cancel();
        UnsubscribeViewModel();
        ViewModel.Dispose();
    }
}
