using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Models.PodcastBrowse;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class PodcastBrowsePage : UserControl, ITabBarItemContent, IPageHostAware, IDisposable
{
    private TabItemParameter? _tabItemParameter;
    private bool _isDisposed;

    // Shimmer crossfade state — mirrors BrowsePage.xaml.cs.
    private bool _showingContent;
    private bool _isShimmerContentReleased;

    public PodcastBrowseViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => _tabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public PodcastBrowsePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<PodcastBrowseViewModel>();
        InitializeComponent();
    }

    public void OnLeaving()
    {
        Dispose();
    }

    public async void OnEntered(object? parameter, PageHostNavigationMode mode)
    {
        await ViewModel.LoadAsync(parameter as ContentNavigationParameter);
        ApplyTabParameter();
    }

    public void RefreshWithParameter(object? parameter)
    {
        _ = LoadFromParameterAsync(parameter as ContentNavigationParameter);
    }

    private async Task LoadFromParameterAsync(ContentNavigationParameter? parameter)
    {
        await ViewModel.LoadAsync(parameter);
        ApplyTabParameter();
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

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        // Hide content underneath the shimmer until the crossfade reveals it.
        // We use the XAML Opacity DP rather than the composition visual's
        // Opacity here: this page's content tree includes elements (ContentCard
        // with its pointer-enter Scale animation, HeroBandPanel with its halo)
        // that touch UIElement.Scale via composition, and grabbing
        // ElementCompositionPreview.GetElementVisual on ContentContainer locks
        // its visual handle in a way that makes any descendant UIElement.Scale
        // assignment throw "Calling Scale API is not allowed on this object".
        ContentContainer.Opacity = 0;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Fast-path: VM may have finished loading before this page was Loaded;
        // reveal content immediately in that case.
        if (!ViewModel.IsLoading && HasAnyRenderableContent())
            CrossfadeToContent();
    }

    /// <summary>
    /// Any of the VM's content surfaces being non-empty (or an error state)
    /// counts as enough to take down the shimmer. We OR over shelves, hero
    /// slides, and category groups so the shimmer clears as soon as any one
    /// of the three first-paint surfaces lands.
    /// </summary>
    private bool HasAnyRenderableContent()
        => ViewModel.ContentShelves.Count > 0
           || ViewModel.HeroSlides.Count > 0
           || ViewModel.CategoryGroups.Count > 0
           || ViewModel.HasError;

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsLoading) && ViewModel.IsLoading && _showingContent)
            ShowShimmer();

        if (e.PropertyName is nameof(ViewModel.IsLoading)
            or nameof(ViewModel.HasError))
        {
            if (!ViewModel.IsLoading
                && HasAnyRenderableContent()
                && !_showingContent)
                CrossfadeToContent();
        }
    }

    private void ShowShimmer()
    {
        if (_isShimmerContentReleased || ShimmerContainer == null)
            return;

        _showingContent = false;
        ShimmerContainer.Visibility = Visibility.Visible;

        // FrameworkLayer.Xaml so neither side takes a composition visual lease
        // on ContentContainer — descendants (ContentCard hover Scale, etc.)
        // are free to use the composition layer themselves.
        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(200),
                     layer: FrameworkLayer.Xaml)
            .Start(ShimmerContainer);

        AnimationBuilder.Create()
            .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200),
                     layer: FrameworkLayer.Xaml)
            .Start(ContentContainer);
    }

    private void CrossfadeToContent()
    {
        _showingContent = true;
        _ = CrossfadeShimmerOutAsync();

        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(300),
                     delay: TimeSpan.FromMilliseconds(100),
                     layer: FrameworkLayer.Xaml)
            .Start(ContentContainer);
    }

    private async Task CrossfadeShimmerOutAsync()
    {
        if (ShimmerContainer == null) return;

        try
        {
            await AnimationBuilder.Create()
                .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200),
                         layer: FrameworkLayer.Xaml)
                .StartAsync(ShimmerContainer);
        }
        catch
        {
            // Cancelled by re-entry from ShowShimmer; the guard below
            // preserves correctness.
        }

        if (!_showingContent || ShimmerContainer == null) return;

        ShimmerContainer.Visibility = Visibility.Collapsed;
        if (!_isShimmerContentReleased)
        {
            ShimmerContainer.Children.Clear();
            _isShimmerContentReleased = true;
        }
    }

    // ── Navigation handlers ──

    /// <summary>
    /// Breadcrumb click — truncates the VM's crumb stack to this rung and
    /// reloads the right pane. Left rail stays untouched.
    /// </summary>
    private async void HeaderBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is BreadcrumbItem rung)
        {
            await ViewModel.NavigateToBreadcrumbAsync(rung);
            ApplyTabParameter();
        }
    }

    /// <summary>
    /// Category chip click — sets the chip rail's selected highlight and
    /// drills into that category's page. The chip rail itself stays intact;
    /// only the hero + shelves repaint. The pinned "All" chip routes the
    /// same way (its Uri is RootPodcastsUri, drilling back to root).
    /// </summary>
    private async void ChipButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not PodcastBrowseCategoryItem item)
            return;

        ViewModel.MarkCategorySelected(item);
        await ViewModel.DrillToAsync(item.Uri, item.Title);
        ApplyTabParameter();
    }

    /// <summary>
    /// Category-tile tap — discriminator on the URI scheme: show URIs leave
    /// the page entirely (open ShowPage); page/section URIs drill in place,
    /// adding to the breadcrumb trail.
    /// </summary>
    private async void PodcastTile_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not PodcastBrowseTile tile)
            return;

        await NavigateToTileAsync(tile);
    }

    /// <summary>
    /// CTA pill click — same routing rules as a tile tap but with no
    /// secondary data on the source button.
    /// </summary>
    private async void PodcastCta_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not PodcastBrowseTile tile)
            return;

        await NavigateToTileAsync(tile);
    }

    /// <summary>
    /// Infinite-scroll auto-pagination. Fires on every scroll tick of the
    /// page's outer <c>ScrollView</c>; when the viewport is within
    /// <see cref="LoadMoreTriggerBandPx"/> of the bottom AND there's a grid
    /// shelf with more to fetch, kick the VM. Cheap: VM guards on its own
    /// IsLoadingMore so back-to-back scroll ticks don't double-fire.
    /// </summary>
    private const double LoadMoreTriggerBandPx = 600;

    private void ContentScrollViewer_ViewChanged(ScrollView sender, object args)
    {
        if (sender is null) return;
        var distanceFromBottom = sender.ScrollableHeight - sender.VerticalOffset;
        if (distanceFromBottom > LoadMoreTriggerBandPx) return;

        // Find the first paginatable grid shelf with capacity for more.
        for (var i = 0; i < ViewModel.ContentShelves.Count; i++)
        {
            var shelf = ViewModel.ContentShelves[i];
            if (shelf.LayoutKind == PodcastBrowseSectionLayoutKind.Grid
                && shelf.HasMore
                && !shelf.IsLoadingMore)
            {
                _ = ViewModel.LoadMoreSectionAsync(shelf);
                return;
            }
        }
    }

    private async Task NavigateToTileAsync(PodcastBrowseTile tile)
    {
        if (string.IsNullOrEmpty(tile.NavigationUri)) return;

        if (tile.NavigationUri.StartsWith("spotify:show:", StringComparison.Ordinal) ||
            tile.NavigationUri.StartsWith("spotify:episode:", StringComparison.Ordinal))
        {
            // Leave the browse page entirely — the show/episode surfaces own
            // their own back stack.
            NavigationHelpers.OpenShowPage(tile.NavigationUri, tile.Title, tile.Subtitle, tile.ImageUrl);
            return;
        }

        // Drill in place. Page + section URIs both flow through the VM,
        // which discriminates them at fetch time.
        await ViewModel.DrillToAsync(tile.NavigationUri, tile.Title, tile.Subtitle, tile.ImageUrl);
        ApplyTabParameter();
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        ViewModel.Dispose();
    }
}
