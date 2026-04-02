using System;
using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Controls.Track;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class SearchPage : Page
{
    private bool _isNarrowLayout;

    public SearchViewModel ViewModel { get; }

    public SearchPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<SearchViewModel>();
        InitializeComponent();
        Unloaded += (_, _) => (ViewModel as IDisposable)?.Dispose();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is string query && !string.IsNullOrWhiteSpace(query))
        {
            await ViewModel.LoadAsync(query);
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

    private void TracksRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is TrackItem trackItem)
        {
            trackItem.PlayCommand = ViewModel.PlayTrackCommand;
        }
    }

    private void TopResult_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var topResult = ViewModel.TopResult;
        if (topResult == null) return;

        switch (topResult.Type)
        {
            case SearchResultType.Track:
                var adapted = ViewModel.AdaptedTracks.FirstOrDefault(t => t.Uri == topResult.Uri)
                              ?? new SearchTrackAdapter(topResult);
                ViewModel.PlayTrackCommand.Execute(adapted);
                break;
            case SearchResultType.Artist:
                NavigationHelpers.OpenArtist(topResult.Uri, topResult.Name);
                break;
            case SearchResultType.Album:
                NavigationHelpers.OpenAlbum(topResult.Uri, topResult.Name);
                break;
            case SearchResultType.Playlist:
                NavigationHelpers.OpenPlaylist(topResult.Uri, topResult.Name);
                break;
        }
    }

    private void TopResult_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            var tc = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<Services.ThemeColorService>();
            border.Background = tc?.CardBackgroundSecondary ??
                                (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
        }
    }

    private void TopResult_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            var tc = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<Services.ThemeColorService>();
            border.Background = tc?.CardBackground ?? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        }
    }

    private void RootPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var isNarrow = e.NewSize.Width < 600;
        if (isNarrow == _isNarrowLayout) return;
        _isNarrowLayout = isNarrow;

        if (isNarrow)
        {
            // Stack vertically: single column, two rows
            TopResultCol.Width = new GridLength(1, GridUnitType.Star);
            SongsCol.Width = new GridLength(0);
            SongsRow.Height = GridLength.Auto;
            TopResultGrid.ColumnSpacing = 0;
            TopResultGrid.RowSpacing = 24;
            Grid.SetRow(SongsPanel, 1);
            Grid.SetColumn(SongsPanel, 0);

            ShimmerTopResultCol.Width = new GridLength(1, GridUnitType.Star);
            ShimmerSongsCol.Width = new GridLength(0);
            ShimmerSongsRow.Height = GridLength.Auto;
            ShimmerGrid.ColumnSpacing = 0;
            ShimmerGrid.RowSpacing = 24;
            Grid.SetRow(ShimmerSongsPanel, 1);
            Grid.SetColumn(ShimmerSongsPanel, 0);
        }
        else
        {
            // Side by side: two columns, single row
            TopResultCol.Width = new GridLength(2, GridUnitType.Star);
            SongsCol.Width = new GridLength(3, GridUnitType.Star);
            SongsRow.Height = new GridLength(0);
            TopResultGrid.ColumnSpacing = 24;
            TopResultGrid.RowSpacing = 0;
            Grid.SetRow(SongsPanel, 0);
            Grid.SetColumn(SongsPanel, 1);

            ShimmerTopResultCol.Width = new GridLength(2, GridUnitType.Star);
            ShimmerSongsCol.Width = new GridLength(3, GridUnitType.Star);
            ShimmerSongsRow.Height = new GridLength(0);
            ShimmerGrid.ColumnSpacing = 24;
            ShimmerGrid.RowSpacing = 0;
            Grid.SetRow(ShimmerSongsPanel, 0);
            Grid.SetColumn(ShimmerSongsPanel, 1);
        }
    }
}
