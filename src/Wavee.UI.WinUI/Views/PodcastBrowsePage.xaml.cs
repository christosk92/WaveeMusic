using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class PodcastBrowsePage : UserControl, ITabBarItemContent, IPageHostAware, IDisposable
{
    private TabItemParameter? _tabItemParameter;
    private bool _isDisposed;

    // Shimmer crossfade state — mirrors BrowsePage.xaml.cs.
    // _showingContent flips when the real ContentContainer takes over from the
    // shimmer skeleton; _isShimmerContentReleased prevents re-running the
    // tree-release more than once per page lifetime.
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
        // Composition visual (not the Opacity DP) so the XAML layout still
        // measures normally during the load.
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 0;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Fast cache hit — VM may have finished loading before this page was
        // Loaded; reveal content immediately in that case.
        if (!ViewModel.IsLoading && (ViewModel.Sections.Count > 0 || ViewModel.HasError))
            CrossfadeToContent();
    }

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
            or nameof(ViewModel.HasError))
        {
            if (!ViewModel.IsLoading
                && (ViewModel.Sections.Count > 0 || ViewModel.HasError)
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
            // Animation cancelled by re-entry from ShowShimmer; the guard below
            // preserves correctness.
        }

        if (!_showingContent || ShimmerContainer == null) return;

        ShimmerContainer.Visibility = Visibility.Collapsed;
        if (!_isShimmerContentReleased)
        {
            // Release the placeholder subtree so a cached PodcastBrowsePage
            // doesn't hold its shimmer visual tree resident for the rest of the
            // session — same trick HomePage / BrowsePage use.
            ShimmerContainer.Children.Clear();
            _isShimmerContentReleased = true;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        ViewModel.Dispose();
    }
}
