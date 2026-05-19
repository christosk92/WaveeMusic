using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.InPageFilter;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

/// <summary>
/// Display item bound to the page's <see cref="Microsoft.UI.Xaml.Controls.BreadcrumbBar"/>.
/// <see cref="ArtistUri"/> is non-null only for clickable crumbs (the parent
/// artist); the trailing crumb (current page) leaves it null so the click
/// handler short-circuits.
/// </summary>
public sealed record DiscographyBreadcrumbItem(string Label, string? ArtistUri);

/// <summary>
/// "See all albums / singles" destination page. Hosts a single full grid of
/// <see cref="ViewModels.LazyReleaseItem"/>s for one
/// <see cref="ArtistDiscographyGroupKind"/>. The page-VM paginates the
/// requested group via <see cref="Wavee.UI.Contracts.IArtistService.GetDiscographyPageAsync"/>;
/// each Pathfinder page is cached one layer down (HotCache), so the
/// second visit to the same group hydrates instantly.
/// </summary>
public sealed partial class ArtistDiscographyPage : UserControl, ITabBarItemContent, INavigationCacheMemoryParticipant, IPageHostAware, IRedirectsCtrlFToOmnibar
{
    private readonly ILogger? _logger;
    private bool _trimmedForNavigationCache;
    private TabItemParameter? _tabItemParameter;

    public ArtistDiscographyPageViewModel ViewModel { get; }

    public ArtistDiscographyPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ArtistDiscographyPageViewModel>();
        _logger = Ioc.Default.GetService<ILogger<ArtistDiscographyPage>>();
        InitializeComponent();
    }

    public TabItemParameter? TabItemParameter => _tabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public bool ReuseForParameterNavigation => true;

    public void OnEntered(object? parameter, PageHostNavigationMode mode)
    {
        _trimmedForNavigationCache = false;
        LoadParameter(parameter);
    }

    public void OnLeaving() { }

    /// <summary>
    /// Same-tab cross-discography nav (Album → Single switch on the same
    /// artist, or jumping to a different artist's discography via deep link)
    /// reuses this page instance and routes through here instead of firing
    /// <see cref="OnNavigatedTo"/>. Falls through to the same LoadParameter
    /// path so the breadcrumb + tab title re-seat correctly.
    /// </summary>
    public void RefreshWithParameter(object? parameter) => LoadParameter(parameter);

    private void LoadParameter(object? parameter)
    {
        if (parameter is not ArtistDiscographyNavigationParameter param)
        {
            _logger?.LogWarning("ArtistDiscographyPage navigated without an ArtistDiscographyNavigationParameter (got {Type}).",
                parameter?.GetType().FullName ?? "<null>");
            return;
        }

        ViewModel.Initialize(param);
        RefreshBreadcrumb();
        RefreshTabItemParameter(param);
    }

    private void RefreshBreadcrumb()
    {
        var artistName = string.IsNullOrWhiteSpace(ViewModel.ArtistName) ? "Artist" : ViewModel.ArtistName!;
        DiscographyBreadcrumb.ItemsSource = new[]
        {
            new DiscographyBreadcrumbItem(artistName, ViewModel.ArtistUri),
            new DiscographyBreadcrumbItem(ViewModel.GroupLabel, null),
        };
    }

    private void RefreshTabItemParameter(ArtistDiscographyNavigationParameter param)
    {
        var artistName = string.IsNullOrWhiteSpace(param.ArtistName) ? "Artist" : param.ArtistName;
        var title = $"{artistName} – {ViewModel.GroupLabel}";
        _tabItemParameter = new TabItemParameter
        {
            InitialPageType = typeof(ArtistDiscographyPage),
            NavigationParameter = param,
            Title = title,
            PageType = NavigationPageType.ArtistDiscography,
        };
        ContentChanged?.Invoke(this, _tabItemParameter);
    }

    private void DiscographyBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        // The trailing crumb (current page) carries a null ArtistUri so a
        // click on it is a no-op — matching BreadcrumbBar convention.
        if (args.Item is not DiscographyBreadcrumbItem item) return;
        if (string.IsNullOrWhiteSpace(item.ArtistUri)) return;

        Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.RecordClickIntent("Breadcrumb.Artist");

        // Route through NavigationHelpers so the Artist tab title / icon
        // get re-seated correctly (rather than relying on PageHost.GoBack, which
        // would also work for the same-tab case but doesn't handle out-of-
        // band entries like tab-restore).
        NavigationHelpers.OpenArtist(item.ArtistUri, item.Label);
    }

    public void TrimForNavigationCache()
    {
        if (_trimmedForNavigationCache) return;
        _trimmedForNavigationCache = true;
        // Detach compiled x:Bind from VM PropertyChanged. The underlying
        // LazyReleaseItem instances live on the shared ArtistViewModel and
        // stay alive there — only this page's binding graph goes idle.
        Bindings?.StopTracking();
    }

    public void RestoreFromNavigationCache()
    {
        if (!_trimmedForNavigationCache) return;
        _trimmedForNavigationCache = false;
        // Defer the binding sweep to the next dispatcher tick so DWM gets a
        // paint frame between the page reattaching and the synchronous
        // Bindings.Update sweep — matches AlbumPage / ShowPage / EpisodePage.
        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            Bindings?.Update();
        });
    }
}
