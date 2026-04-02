using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.PlayerBar;

public sealed partial class PlayerBar : UserControl
{
    public PlayerBarViewModel ViewModel { get; }

    private readonly Data.Contracts.ITrackLikeService? _likeService;

    public PlayerBar()
    {
        ViewModel = Ioc.Default.GetRequiredService<PlayerBarViewModel>();
        _likeService = Ioc.Default.GetService<Data.Contracts.ITrackLikeService>();
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        PlayerHeartButton.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(OnPlayerHeartClicked);

        // Subscribe to save state changes for reactive heart updates
        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;
        Unloaded += (_, _) =>
        {
            if (_likeService != null) _likeService.SaveStateChanged -= OnSaveStateChanged;
        };

        // Apply initial color if available
        ApplyTintColor(ViewModel.AlbumArtColor);
    }

    private void OnSaveStateChanged()
    {
        DispatcherQueue?.TryEnqueue(UpdatePlayerHeartState);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.AlbumArtColor))
        {
            ApplyTintColor(ViewModel.AlbumArtColor);
        }
        else if (e.PropertyName == nameof(PlayerBarViewModel.HasTrack))
        {
            UpdatePlayerHeartState();
        }
    }

    private string? GetCurrentTrackId()
    {
        var service = Ioc.Default.GetService<IPlaybackStateService>();
        return service?.CurrentTrackId;
    }

    private void UpdatePlayerHeartState()
    {
        var trackId = GetCurrentTrackId();
        PlayerHeartButton.IsLiked = !string.IsNullOrEmpty(trackId)
            && _likeService?.IsSaved(Data.Contracts.SavedItemType.Track, trackId) == true;
    }

    private void OnPlayerHeartClicked()
    {
        var trackId = GetCurrentTrackId();
        if (string.IsNullOrEmpty(trackId) || _likeService == null) return;

        var uri = $"spotify:track:{trackId}";
        var isLiked = PlayerHeartButton.IsLiked;
        _likeService.ToggleSave(Data.Contracts.SavedItemType.Track, uri, isLiked);
        PlayerHeartButton.IsLiked = !isLiked;
    }

    private void ApplyTintColor(string? hexColor)
    {
        if (string.IsNullOrEmpty(hexColor) || PlayerBarTintBrush == null) return;

        try
        {
            var hex = hexColor.TrimStart('#');
            if (hex.Length == 6)
            {
                var r = Convert.ToByte(hex[..2], 16);
                var g = Convert.ToByte(hex[2..4], 16);
                var b = Convert.ToByte(hex[4..6], 16);
                PlayerBarTintBrush.TintColor = Windows.UI.Color.FromArgb(255, r, g, b);
            }
        }
        catch
        {
            // Keep current tint on parse failure
        }
    }

    private void AlbumArt_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        ViewModel.ToggleAlbumArtExpandedCommand.Execute(null);
    }

    private void TrackTitle_Click(object sender, RoutedEventArgs e) => NavigateToAlbum();
    private void TrackTitle_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => NavigateToAlbum();

    private void NavigateToAlbum()
    {
        var albumId = ViewModel.CurrentAlbumId;
        if (string.IsNullOrEmpty(albumId)) return;
        var param = new Data.Parameters.ContentNavigationParameter
        {
            Uri = albumId,
            Title = ViewModel.TrackTitle ?? "Album",
            ImageUrl = ViewModel.AlbumArt
        };
        NavigationHelpers.OpenAlbum(param, param.Title);
    }

    private void ArtistName_Click(object sender, RoutedEventArgs e)
    {
        var artistId = ViewModel.CurrentArtistId;
        var artistName = ViewModel.ArtistName;
        if (!string.IsNullOrEmpty(artistId))
            NavigationHelpers.OpenArtist(artistId, artistName ?? "Artist");
    }

    private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.StartSeeking();
    }

    private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.EndSeeking();
    }

    private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.EndSeeking();
    }
}
