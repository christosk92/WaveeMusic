using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

/// <summary>
/// Generic destination for any Spotify <c>spotify:page:</c> URI surfaced by
/// the home / browse-all pipeline. Mirrors HomePage's silhouette — header
/// band + hero carousel + section shelves — but driven by the
/// <c>browsePage</c> persistedQuery instead of the home feed. Composed from
/// the shared <c>HeroBandPanel</c> and <c>SectionShelvesView</c> blocks so
/// future feed-shaped pages reuse the same building bricks.
/// </summary>
public sealed partial class BrowsePage : Page, ITabBarItemContent, IDisposable
{
    private TabItemParameter? _tabItemParameter;
    private bool _isDisposed;

    // Shimmer crossfade state — mirrors HomePage.xaml.cs:33,84.
    // _showingContent flips when the real ContentContainer takes over from
    // the shimmer skeleton; _isShimmerContentReleased prevents re-running the
    // tree-release more than once per page lifetime.
    private bool _showingContent;
    private bool _isShimmerContentReleased;

    public BrowseViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => _tabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public BrowsePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<BrowseViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Dispose();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var parameter = e.Parameter as ContentNavigationParameter;
        await ViewModel.LoadAsync(parameter);
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
            Subtitle = ViewModel.Subtitle
        };

        _tabItemParameter = new TabItemParameter(NavigationPageType.Browse, parameter)
        {
            Title = ViewModel.Title
        };
        ContentChanged?.Invoke(this, _tabItemParameter);
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        // Hide content underneath the shimmer until the crossfade reveals it.
        // Setting via composition visual (not the Opacity DP) so the XAML's
        // declared layout still measures normally.
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 0;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // If the VM already finished loading before this page was Loaded
        // (e.g. fast cache hit), reveal content immediately.
        if (!ViewModel.IsLoading && HasAnyContent())
            CrossfadeToContent();
    }

    private bool HasAnyContent()
        => ViewModel.Sections.Count > 0
           || ViewModel.BrowseGroups.Count > 0
           || ViewModel.BrowseCta is not null;

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsLoading) && ViewModel.IsLoading && _showingContent)
        {
            // Refresh kicked off — bring the shimmer back over the content.
            ShowShimmer();
        }

        if (e.PropertyName is nameof(ViewModel.IsLoading)
            or nameof(ViewModel.Sections)
            or nameof(ViewModel.BrowseGroups)
            or nameof(ViewModel.BrowseCta))
        {
            if (!ViewModel.IsLoading && (HasAnyContent() || ViewModel.HasError) && !_showingContent)
                CrossfadeToContent();
        }
    }

    private void ShowShimmer()
    {
        if (_isShimmerContentReleased || ShimmerContainer == null)
            return;

        _showingContent = false;
        ShimmerContainer.Visibility = Visibility.Visible;

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

        _ = CrossfadeShimmerOutAsync();

        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(300),
                     delay: TimeSpan.FromMilliseconds(100))
            .Start(ContentContainer);
    }

    private async Task CrossfadeShimmerOutAsync()
    {
        if (ShimmerContainer == null) return;

        try
        {
            await AnimationBuilder.Create()
                .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200))
                .StartAsync(ShimmerContainer);
        }
        catch
        {
            // Animation cancelled by a re-entry from ShowShimmer; the guard
            // below preserves correctness.
        }

        if (!_showingContent || ShimmerContainer == null) return;

        ShimmerContainer.Visibility = Visibility.Collapsed;
        if (!_isShimmerContentReleased)
        {
            // Release the placeholder subtree so a cached BrowsePage doesn't
            // hold its shimmer visual tree resident for the rest of the
            // session — same trick HomePage uses.
            ShimmerContainer.Children.Clear();
            _isShimmerContentReleased = true;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        ViewModel.Dispose();
    }
}
