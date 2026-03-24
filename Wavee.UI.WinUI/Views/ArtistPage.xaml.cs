using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class ArtistPage : Page, ITabBarItemContent
{
    private readonly ILogger? _logger;
    private bool _showingContent;

    public ArtistViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ArtistPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ArtistViewModel>();
        _logger = Ioc.Default.GetService<ILogger<ArtistPage>>();
        InitializeComponent();

        // Hide content initially — shimmer is visible, content is collapsed
        ContentContainer.Opacity = 0;

        ViewModel.ContentChanged += ViewModel_ContentChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        SizeChanged += OnSizeChanged;
        Unloaded += ArtistPage_Unloaded;
    }

    private void ViewModel_ContentChanged(object? sender, TabItemParameter e)
        => ContentChanged?.Invoke(this, e);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArtistViewModel.IsLoading))
        {
            if (!ViewModel.IsLoading && !_showingContent)
            {
                // Defer slightly to let reactive bindings populate collections
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    CrossfadeToContent);
            }
        }
    }

    private async void CrossfadeToContent()
    {
        if (_showingContent) return;
        _showingContent = true;

        // Start both simultaneously — content fades in AS shimmer fades out
        AnimationBuilder.Create()
            .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(250),
                     layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
            .Start(ShimmerContainer);

        ContentContainer.Opacity = 1;
        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(250),
                     layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
            .Start(ContentContainer);

        // Collapse shimmer after animation completes
        await Task.Delay(300);
        if (_showingContent)
            ShimmerContainer.Visibility = Visibility.Collapsed;
    }

    private void ArtistPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ContentChanged -= ViewModel_ContentChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        SizeChanged -= OnSizeChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Hero height = 45% of page height (min 300)
        if (HeroGrid != null)
            HeroGrid.Height = Math.Max(300, e.NewSize.Height * 0.45);
    }

    private void TopTracksLayout_ColumnCountChanged(object? sender, int columns)
    {
        ViewModel.ColumnCount = columns;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            ConnectedAnimationHelper.TryStartAnimation(ConnectedAnimationHelper.ArtistImage, ArtistImageContainer);

            if (e.Parameter is ContentNavigationParameter nav)
            {
                ViewModel.PrefillFrom(nav);
                ViewModel.Initialize(nav.Uri);
                await ViewModel.LoadCommand.ExecuteAsync(null);
            }
            else if (e.Parameter is string artistId)
            {
                ViewModel.Initialize(artistId);
                await ViewModel.LoadCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unhandled error in ArtistPage OnNavigatedTo");
        }
    }

    private void Release_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ArtistReleaseVm release)
        {
            var param = new ContentNavigationParameter
            {
                Uri = release.Uri ?? release.Id,
                Title = release.Name,
                ImageUrl = release.ImageUrl
            };
            NavigationHelpers.OpenAlbum(param, release.Name ?? "Album", NavigationHelpers.IsCtrlPressed());
        }
    }

    private void RelatedArtist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is RelatedArtistVm artist)
        {
            var param = new ContentNavigationParameter
            {
                Uri = artist.Uri ?? artist.Id ?? "",
                Title = artist.Name,
                ImageUrl = artist.ImageUrl
            };
            NavigationHelpers.OpenArtist(param, artist.Name ?? "Artist", NavigationHelpers.IsCtrlPressed());
        }
    }
}
