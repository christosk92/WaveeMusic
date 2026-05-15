using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class AlbumsLibraryView : UserControl, IDisposable
{
    private const double NarrowLayoutBreakpoint = 650;
    private bool _hasInitializedLayoutMode;
    private bool _disposed;

    public int[] NarrowShimmerPlaceholders { get; } = [1, 2, 3, 4, 5, 6];
    public AlbumsLibraryViewModel ViewModel { get; }

    public AlbumsLibraryView(AlbumsLibraryViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;

        // Load is idempotent (guarded in the VM); called once on first creation.
        _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayoutMode(preserveContext: false);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayoutMode(preserveContext: _hasInitializedLayoutMode);
    }

    private void UpdateLayoutMode(bool preserveContext)
    {
        var isNarrow = ActualWidth <= NarrowLayoutBreakpoint;
        ViewModel.SetNarrowLayout(isNarrow, preserveContext);
        _hasInitializedLayoutMode = true;
    }

    private void LibraryGrid_ItemDoubleTapped(object? sender, object? e)
    {
        if (e is not LibraryAlbumDto album) return;
        var param = new ContentNavigationParameter
        {
            Uri = album.Id,
            Title = album.Name,
            Subtitle = album.ArtistName,
            ImageUrl = album.ImageUrl,
            TotalTracks = album.TrackCount > 0 ? album.TrackCount : null
        };
        NavigationHelpers.OpenAlbum(param, album.Name, NavigationHelpers.IsCtrlPressed());
    }

    private void NarrowAlbumsView_SelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is LibraryAlbumDto album)
        {
            ViewModel.ShowSelectedAlbumDetails(album);
        }
    }

    private void NarrowAlbumCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is LibraryAlbumDto album)
        {
            ViewModel.ShowSelectedAlbumDetails(album);
        }
    }

    private void BreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0)
        {
            ViewModel.ShowAlbumsRoot();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Loaded -= OnLoaded;
        SizeChanged -= OnSizeChanged;
    }
}
