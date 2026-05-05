using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class ConcertPage : Page, ITabBarItemContent
{
    private readonly ILogger? _logger;
    private bool _showingContent;
    private bool _isNavigatingAway;

    // Shy-header morph state. Mirrors ArtistPage: one running transition at a time;
    // scroll events arriving mid-flight queue a re-check via _shyHeaderRecheckPending.
    private TransitionHelper? _shyHeaderTransition;
    private bool _isShyHeaderPinned;
    private bool _isShyHeaderTransitionRunning;
    private bool _shyHeaderRecheckPending;

    public ConcertViewModel ViewModel { get; }
    public TabItemParameter? TabItemParameter => null;
    public event EventHandler<TabItemParameter>? ContentChanged;

    public ConcertPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ConcertViewModel>();
        _logger = Ioc.Default.GetService<ILogger<ConcertPage>>();
        InitializeComponent();
        ContentContainer.Opacity = 0;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ActualThemeChanged += OnActualThemeChanged;
        Loaded += ConcertPage_Loaded;
        Unloaded += ConcertPage_Unloaded;

        // Seed the VM with the current theme so the palette brushes are correct as soon
        // as the data lands. If the theme flips while we're alive, OnActualThemeChanged
        // will tell the VM to rebuild.
        ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);
    }

    private void ConcertPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ConcertPage_Loaded;
        _isNavigatingAway = false;

        ContentContainer.ViewChanged += ContentContainer_ViewChanged;
        StoreHero.SizeChanged += (_, _) =>
        {
            UpdateHeroScrollFade();
            _ = EvaluateShyHeaderAsync();
        };

        EnsureShyHeaderTransition();
        ResetShyHeaderState();
        UpdateHeroScrollFade();
    }

    private void ConcertPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isNavigatingAway = true;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ActualThemeChanged -= OnActualThemeChanged;
        ContentContainer.ViewChanged -= ContentContainer_ViewChanged;
        ResetShyHeaderState();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Detach compiled x:Bind from VM.PropertyChanged so the BindingsTracking
        // sibling does not pin this page across navigations. NavCacheMode is
        // Disabled — page is destroyed on nav-away, no Update() partner needed.
        Bindings?.StopTracking();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);
    }

    // ── Shy-header morph ───────────────────────────────────────────────────────

    private void EnsureShyHeaderTransition()
    {
        if (_shyHeaderTransition != null)
            return;

        // XAML resources can't ElementName-bind, so Source/Target are wired here.
        if (Resources.TryGetValue("ConcertShyHeaderTransition", out var resource)
            && resource is TransitionHelper helper)
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
        _shyHeaderTransition?.Reset(toInitialState: true);
        if (FeatureTileRoot != null) FeatureTileRoot.Opacity = 1.0;
    }

    private void ContentContainer_ViewChanged(ScrollView sender, object args)
    {
        UpdateHeroScrollFade();
        _ = EvaluateShyHeaderAsync();
    }

    /// <summary>
    /// Continuously fades the feature tile as the user scrolls through the hero —
    /// 1.0 at the top, 0.0 by the time the hero has scrolled fully out of view.
    /// Matches ArtistPage's HeroHeader.ScrollFadeProgress behaviour, but we drive
    /// Opacity directly since StoreHero isn't a HeroHeader.
    /// </summary>
    private void UpdateHeroScrollFade()
    {
        if (FeatureTileRoot == null || StoreHero == null) return;
        var heroH = StoreHero.ActualHeight;
        if (heroH <= 0)
        {
            FeatureTileRoot.Opacity = 1.0;
            return;
        }
        var progress = Math.Clamp(ContentContainer.VerticalOffset / heroH, 0.0, 1.0);
        FeatureTileRoot.Opacity = 1.0 - progress;
    }

    private async Task EvaluateShyHeaderAsync()
    {
        if (_shyHeaderTransition == null || HeroOverlayPanel == null || ShyHeaderCard == null || StoreHero == null)
            return;

        if (_isShyHeaderTransitionRunning)
        {
            // Coalesce: re-check once the in-flight transition lands.
            _shyHeaderRecheckPending = true;
            return;
        }

        while (true)
        {
            if (_isNavigatingAway || !StoreHero.IsLoaded || !ShyHeaderHost.IsLoaded)
                return;

            double pinOffset = Math.Max(0, StoreHero.ActualHeight - 120);
            bool shouldPin = ContentContainer.VerticalOffset >= pinOffset;

            if (shouldPin == _isShyHeaderPinned)
                return;

            _isShyHeaderTransitionRunning = true;
            _shyHeaderRecheckPending = false;

            try
            {
                // Opacity is driven by UpdateHeroScrollFade per-scroll-tick (continuous
                // fade like ArtistPage). Here we only run the matched-id morph.
                if (shouldPin)
                    await _shyHeaderTransition.StartAsync();
                else
                    await _shyHeaderTransition.ReverseAsync();

                _isShyHeaderPinned = shouldPin;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Shy header transition skipped.");
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

    private void ShyHeaderTickets_Click(object sender, RoutedEventArgs e)
    {
        var url = ViewModel.Offers.FirstOrDefault()?.Url;
        if (!string.IsNullOrEmpty(url))
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));
    }

    // ── ViewModel / navigation ─────────────────────────────────────────────────

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConcertViewModel.HasSupportingArtists))
        {
            // Grow the supporting column only when there are actually tiles to show;
            // otherwise the feature tile gets the whole width (single-artist hero).
            SupportingColumn.Width = ViewModel.HasSupportingArtists
                ? new GridLength(380)
                : new GridLength(0);
        }

        if (e.PropertyName == nameof(ConcertViewModel.IsLoading) && !ViewModel.IsLoading && !_showingContent)
        {
            _showingContent = true;

            AnimationBuilder.Create()
                .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(150),
                         layer: FrameworkLayer.Xaml)
                .Start(ShimmerContainer);

            ContentContainer.Opacity = 1;
            AnimationBuilder.Create()
                .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(200),
                         layer: FrameworkLayer.Xaml)
                .Start(ContentContainer);

            _ = Task.Delay(160).ContinueWith(_ =>
                DispatcherQueue.TryEnqueue(() => ShimmerContainer.Visibility = Visibility.Collapsed));
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = LoadParameterAsync(e.Parameter);
    }

    // Same-tab navigation between two concerts reuses this Page instance and
    // never fires OnNavigatedTo — TabBarItem.Navigate routes through this method
    // instead. Without this override, clicking a different concert from a shelf
    // / search while ConcertPage is the active tab content silently drops the
    // new parameter.
    public void RefreshWithParameter(object? parameter) => _ = LoadParameterAsync(parameter);

    private async Task LoadParameterAsync(object? parameter)
    {
        _isNavigatingAway = false;

        try
        {
            if (parameter is ContentNavigationParameter nav)
            {
                ViewModel.Title = nav.Title;
                await ViewModel.LoadCommand.ExecuteAsync(nav.Uri);
            }
            else if (parameter is string uri)
            {
                await ViewModel.LoadCommand.ExecuteAsync(uri);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConcertPage] LoadParameter failed: {ex}");
        }
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        _isNavigatingAway = true;
        base.OnNavigatingFrom(e);
    }

    // ── Click handlers ─────────────────────────────────────────────────────────

    private void Artist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string uri && !string.IsNullOrEmpty(uri))
        {
            var param = new ContentNavigationParameter { Uri = uri };
            NavigationHelpers.OpenArtist(param, "", NavigationHelpers.IsCtrlPressed());
        }
    }

    private void Offer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url && !string.IsNullOrEmpty(url))
        {
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
    }

    private void RelatedConcert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string uri && !string.IsNullOrEmpty(uri))
        {
            var title = (btn.DataContext as ConcertRelatedVm)?.Title;
            var param = new ContentNavigationParameter { Uri = uri, Title = title };
            NavigationHelpers.OpenConcert(param, title ?? "Concert", NavigationHelpers.IsCtrlPressed());
        }
    }

    private void LocationButton_LocationChanged(object? sender, string city)
    {
        ViewModel.UserLocationName = city;
    }
}
