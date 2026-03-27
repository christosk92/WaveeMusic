using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.PlayerBar;

public sealed partial class PlayerBar : UserControl
{
    public PlayerBarViewModel ViewModel { get; }

    public PlayerBar()
    {
        ViewModel = Ioc.Default.GetRequiredService<PlayerBarViewModel>();
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Apply initial color if available
        ApplyTintColor(ViewModel.AlbumArtColor);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.AlbumArtColor))
        {
            ApplyTintColor(ViewModel.AlbumArtColor);
        }
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

    private void TrackTitle_Click(object sender, RoutedEventArgs e)
    {
        var albumId = ViewModel.CurrentAlbumId;
        var trackTitle = ViewModel.TrackTitle;
        if (!string.IsNullOrEmpty(albumId))
            NavigationHelpers.OpenAlbum(albumId, trackTitle ?? "Album");
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
