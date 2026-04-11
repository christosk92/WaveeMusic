using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class SearchPage : Page
{
    private readonly Services.ThemeColorService? _themeColors;

    public SearchViewModel ViewModel { get; }

    public SearchPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<SearchViewModel>();
        _themeColors = Ioc.Default.GetService<Services.ThemeColorService>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is string query && !string.IsNullOrWhiteSpace(query))
        {
            _ = ViewModel.LoadAsync(query);
        }
    }

    private void FilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            ViewModel.SelectedFilter = tag switch
            {
                "Songs" => SearchFilterType.Songs,
                "Artists" => SearchFilterType.Artists,
                "Albums" => SearchFilterType.Albums,
                "Playlists" => SearchFilterType.Playlists,
                _ => SearchFilterType.All
            };
        }
    }

    private void TopResult_Tapped(object sender, TappedRoutedEventArgs e)
    {
        ExecuteResult(ViewModel.TopResult);
    }

    private void ResultRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is SearchResultItem item)
            ExecuteResult(item);
    }

    private void ExecuteResult(SearchResultItem? item)
    {
        if (item == null)
            return;

        switch (item.Type)
        {
            case SearchResultType.Track:
                var adapted = ViewModel.AdaptedTracks.FirstOrDefault(t => t.Uri == item.Uri)
                              ?? new SearchTrackAdapter(item);
                ViewModel.PlayTrackCommand.Execute(adapted);
                break;
            case SearchResultType.Artist:
                NavigationHelpers.OpenArtist(item.Uri, item.Name);
                break;
            case SearchResultType.Album:
                NavigationHelpers.OpenAlbum(item.Uri, item.Name);
                break;
            case SearchResultType.Playlist:
                NavigationHelpers.OpenPlaylist(item.Uri, item.Name);
                break;
        }
    }

    private void InteractiveCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = _themeColors?.CardBackgroundSecondary ??
                                (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
        }
    }

    private void InteractiveCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = ReferenceEquals(border, TopResultCard)
                ? _themeColors?.CardBackgroundSecondary ??
                  (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }
}
