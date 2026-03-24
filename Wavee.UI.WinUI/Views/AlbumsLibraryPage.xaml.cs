using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class AlbumsLibraryPage : Page
{
    public AlbumsLibraryViewModel ViewModel { get; }

    public AlbumsLibraryPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<AlbumsLibraryViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation error: {ex}");
        }
    }

    private void LibraryGrid_ItemDoubleTapped(object? sender, object? e)
    {
        if (e is not LibraryAlbumDto album) return;
        var param = new ContentNavigationParameter
        {
            Uri = album.Id,
            Title = album.Name,
            Subtitle = album.ArtistName,
            ImageUrl = album.ImageUrl
        };
        NavigationHelpers.OpenAlbum(param, album.Name, NavigationHelpers.IsCtrlPressed());
    }
}
