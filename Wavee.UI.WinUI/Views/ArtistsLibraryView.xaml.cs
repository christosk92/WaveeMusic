using System;
using System.Collections;
using System.ComponentModel;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class ArtistsLibraryView : UserControl
{
    public ArtistsLibraryViewModel ViewModel { get; }

    public ArtistsLibraryView(ArtistsLibraryViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // Subscribed once for the lifetime of this long-lived UserControl.
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        Loaded += OnLoaded;

        // Load is idempotent (guarded in the VM); called once on first creation.
        _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Sync selection when loaded into the visual tree
        SyncSelectionToItemsView();

        // Initialize tracks panel state
        UpdateTracksPanelVisibility(ViewModel.IsTracksPanelVisible, animate: false);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsTracksPanelVisible))
        {
            UpdateTracksPanelVisibility(ViewModel.IsTracksPanelVisible, animate: true);
        }
    }

    private void UpdateTracksPanelVisibility(bool isVisible, bool animate)
    {
        if (TracksPanelBorder == null || TracksPanelColumn == null) return;

        // Use star sizing when visible so panel fills available space (respecting MinWidth on Border)
        var visibleWidth = new GridLength(1, GridUnitType.Star);
        var hiddenWidth = new GridLength(0);

        if (!animate)
        {
            TracksPanelColumn.Width = isVisible ? visibleWidth : hiddenWidth;
            TracksPanelBorder.Opacity = isVisible ? 1 : 0;
            return;
        }

        if (isVisible)
        {
            // Expand column first, then animate content in
            TracksPanelColumn.Width = visibleWidth;

            AnimationBuilder.Create()
                .Translation(Axis.X, from: 100, to: 0, duration: TimeSpan.FromMilliseconds(300))
                .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(300))
                .Start(TracksPanelBorder);
        }
        else
        {
            // Animate content out, then collapse column
            AnimationBuilder.Create()
                .Translation(Axis.X, from: 0, to: 100, duration: TimeSpan.FromMilliseconds(200))
                .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200))
                .Start(TracksPanelBorder, () =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        TracksPanelColumn.Width = hiddenWidth;
                    });
                });
        }
    }

    private void ArtistsView_SelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem != ViewModel.SelectedArtist)
        {
            ViewModel.SelectedArtist = sender.SelectedItem as Data.DTOs.LibraryArtistDto;
        }
    }

    private void ArtistsView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedArtist is not { } artist) return;

        // Prepare connected animation from the detail panel artist image
        Helpers.ConnectedAnimationHelper.PrepareAnimation(
            Helpers.ConnectedAnimationHelper.ArtistImage, DetailArtistImageContainer);

        var param = new ContentNavigationParameter
        {
            Uri = artist.Id,
            Title = artist.Name,
            ImageUrl = artist.ImageUrl
        };
        NavigationHelpers.OpenArtist(param, artist.Name, NavigationHelpers.IsCtrlPressed());
    }

    private void ViewArtistButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedArtist is not { } artist) return;

        Helpers.ConnectedAnimationHelper.PrepareAnimation(
            Helpers.ConnectedAnimationHelper.ArtistImage, DetailArtistImageContainer);

        var param = new ContentNavigationParameter
        {
            Uri = artist.Id,
            Title = artist.Name,
            ImageUrl = artist.ImageUrl
        };
        NavigationHelpers.OpenArtist(param, artist.Name, NavigationHelpers.IsCtrlPressed());
    }

    private void ViewAlbumButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAlbumForTracks == null) return;
        var album = ViewModel.SelectedAlbumForTracks.Album;

        Helpers.ConnectedAnimationHelper.PrepareAnimation(
            Helpers.ConnectedAnimationHelper.AlbumArt, DetailAlbumImageContainer);

        var param = new ContentNavigationParameter
        {
            Uri = album.Id,
            Title = album.Name,
            ImageUrl = album.ImageUrl
        };
        NavigationHelpers.OpenAlbum(param, album.Name, NavigationHelpers.IsCtrlPressed());
    }

    private void SyncSelectionToItemsView()
    {
        if (ArtistsView is null) return;

        if (ViewModel.SelectedArtist is null)
        {
            ArtistsView.DeselectAll();
        }
        else if (ViewModel.FilteredArtists is IList list)
        {
            var index = list.IndexOf(ViewModel.SelectedArtist);
            if (index >= 0 && ArtistsView.SelectedItem != ViewModel.SelectedArtist)
            {
                ArtistsView.Select(index);
            }
        }
    }
}
