using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class AlbumsLibraryView : UserControl
{
    public AlbumsLibraryViewModel ViewModel { get; }

    public AlbumsLibraryView(AlbumsLibraryViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // Load is idempotent (guarded in the VM); called once on first creation.
        _ = ViewModel.LoadCommand.ExecuteAsync(null);
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
