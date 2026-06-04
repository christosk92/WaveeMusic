using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.SidebarPlayer;

/// <summary>
/// Picture-in-picture mini player hosted by <see cref="Floating.PlayerFloatingWindow"/>
/// when the float is in compact form factor. Pure composition over the shared
/// <see cref="PlayerBarViewModel"/> singleton — no independent state of its own.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class CompactPlayerView : UserControl
{
    private readonly PlayerBarViewModel _viewModel;

    public CompactPlayerView()
    {
        _viewModel = Ioc.Default.GetRequiredService<PlayerBarViewModel>();
        InitializeComponent();
    }

    public PlayerBarViewModel ViewModel => _viewModel;

    // Seek wiring mirrors PlayerBar: pause the GPU animation source on drag so
    // AudioHost position echoes don't fight the user's scrub, then commit + re-anchor.
    private void ProgressBar_SeekStarted(object sender, EventArgs e) => _viewModel.StartSeeking();

    private void ProgressBar_SeekCommitted(object sender, double positionMs) => _viewModel.CommitSeekFromBar(positionMs);

    private void AlbumArt_Tapped(object sender, TappedRoutedEventArgs e) => OpenAlbum();

    private void TrackTitle_Click(object sender, RoutedEventArgs e) => OpenAlbum();

    private void OpenAlbum()
    {
        var albumId = _viewModel.CurrentAlbumId;
        if (string.IsNullOrEmpty(albumId)) return;

        var param = new Data.Parameters.ContentNavigationParameter
        {
            Uri = albumId,
            Title = _viewModel.TrackTitle ?? "Album",
            ImageUrl = _viewModel.AlbumArt
        };
        NavigationHelpers.OpenAlbum(param, param.Title);
    }
}
