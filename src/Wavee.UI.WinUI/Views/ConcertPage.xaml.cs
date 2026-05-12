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
using Wavee.UI.WinUI.Controls.HeroHeader;
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

    private ShyHeaderController? _shyHeader;

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

        _shyHeader = new ShyHeaderController(
            ContentContainer, StoreHero, HeroOverlayPanel, ShyHeaderCard,
            (TransitionHelper)Resources["ConcertShyHeaderTransition"],
            ShyHeaderFade.ForElementOpacity(FeatureTileRoot),
            ShyHeaderPinOffset.Below(StoreHero, 120),
            canEvaluate: () => !_isNavigatingAway,
            logger: _logger);
        _shyHeader.Attach();
        _shyHeader.Reset();
        _shyHeader.UpdateHeroFade();

        // StoreHero resize during a window drag needs to re-run both the fade
        // (its scale changed) and the pin check (the threshold moved). The
        // controller's scroll handler doesn't fire on size changes — so wire
        // this explicitly.
        StoreHero.SizeChanged += (_, _) =>
        {
            _shyHeader?.UpdateHeroFade();
            _ = _shyHeader?.EvaluateAsync();
        };
    }

    private void ConcertPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isNavigatingAway = true;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ActualThemeChanged -= OnActualThemeChanged;
        _shyHeader?.Dispose();
        _shyHeader = null;
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
