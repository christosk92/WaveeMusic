using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Controls.InPageFilter;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class AlbumsLibraryView : UserControl, IDisposable, IInPageFilterable
{
    // ── IInPageFilterable ───────────────────────────────────────────────
    string IInPageFilterable.FilterQuery
    {
        get => ViewModel?.SearchQuery ?? string.Empty;
        set { if (ViewModel is { } vm) vm.SearchQuery = value ?? string.Empty; }
    }
    string IInPageFilterable.FilterPlaceholder => AppLocalization.GetString("Filter_AlbumsPlaceholder");
    bool IInPageFilterable.CanFilter => ViewModel is not null;

    private const double NarrowLayoutBreakpoint = 650;
    private bool _hasInitializedLayoutMode;
    private bool _disposed;
    private bool _suppressSelectorEvents;

    public int[] NarrowShimmerPlaceholders { get; } = [1, 2, 3, 4, 5, 6];
    public AlbumsLibraryViewModel ViewModel { get; }

    public AlbumsLibraryView(AlbumsLibraryViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Load is idempotent (guarded in the VM); called once on first creation.
        _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayoutMode(preserveContext: false);
        SyncSourceSelectorFromViewModel();
        SyncDetailModeSelectorFromViewModel();
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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AlbumsLibraryViewModel.SourceMode):
                SyncSourceSelectorFromViewModel();
                break;
            case nameof(AlbumsLibraryViewModel.LikedAlbumDetailMode):
                SyncDetailModeSelectorFromViewModel();
                break;
        }
    }

    private void SyncSourceSelectorFromViewModel()
    {
        if (SourceSelector == null) return;
        var index = ViewModel.SourceMode == LibrarySource.FromLikedSongs ? 1 : 0;
        if (SourceSelector.SelectedIndex != index)
        {
            _suppressSelectorEvents = true;
            try { SourceSelector.SelectedIndex = index; }
            finally { _suppressSelectorEvents = false; }
        }
    }

    private void SyncDetailModeSelectorFromViewModel()
    {
        var index = ViewModel.LikedAlbumDetailMode == LikedAlbumDetailMode.FullAlbum ? 1 : 0;
        _suppressSelectorEvents = true;
        try
        {
            if (DetailModeSelector != null && DetailModeSelector.SelectedIndex != index)
                DetailModeSelector.SelectedIndex = index;
            if (NarrowDetailModeSelector != null && NarrowDetailModeSelector.SelectedIndex != index)
                NarrowDetailModeSelector.SelectedIndex = index;
        }
        finally { _suppressSelectorEvents = false; }
    }

    private void SourceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectorEvents) return;
        if (sender is not Selector { SelectedItem: FrameworkElement fe }) return;
        if (fe.Tag is not string tag) return;

        var newMode = string.Equals(tag, nameof(LibrarySource.FromLikedSongs), StringComparison.OrdinalIgnoreCase)
            ? LibrarySource.FromLikedSongs
            : LibrarySource.Saved;
        if (ViewModel.SourceMode != newMode)
            ViewModel.SourceMode = newMode;
    }

    private void DetailModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectorEvents) return;
        if (sender is not Selector { SelectedItem: FrameworkElement fe }) return;
        if (fe.Tag is not string tag) return;

        var newMode = string.Equals(tag, nameof(LikedAlbumDetailMode.FullAlbum), StringComparison.OrdinalIgnoreCase)
            ? LikedAlbumDetailMode.FullAlbum
            : LikedAlbumDetailMode.Liked;
        if (ViewModel.LikedAlbumDetailMode != newMode)
            ViewModel.LikedAlbumDetailMode = newMode;
    }

    private void SavedLibraryGrid_ItemDoubleTapped(object? sender, object? e)
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

    private void LikedLibraryGrid_ItemDoubleTapped(object? sender, object? e)
    {
        if (e is not LikedAlbumDto album) return;
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
            ViewModel.ShowSelectedAlbumDetails(album);
    }

    private void NarrowAlbumCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is LibraryAlbumDto album)
            ViewModel.ShowSelectedAlbumDetails(album);
    }

    private void NarrowLikedAlbumsView_SelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is LikedAlbumDto album)
            ViewModel.ShowSelectedLikedAlbumDetails(album);
    }

    private void NarrowLikedAlbumCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is LikedAlbumDto album)
            ViewModel.ShowSelectedLikedAlbumDetails(album);
    }

    private void BreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0)
            ViewModel.ShowAlbumsRoot();
    }

    /// <summary>
    /// Right-click on a directly hearted album opens a small album-level menu.
    /// This toggles the album save state only; it does not touch individual
    /// liked tracks that may also belong to the album.
    /// </summary>
    private void SavedAlbumCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (element.DataContext is not LibraryAlbumDto album) return;

        var flyout = new MenuFlyout();
        var item = new MenuFlyoutItem
        {
            Text = "Unheart album",
            Icon = new FontIcon { Glyph = Wavee.UI.WinUI.Styles.FluentGlyphs.HeartFilled }
        };
        item.Click += (_, _) => ViewModel.UnheartAlbum(album);
        flyout.Items.Add(item);

        flyout.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    /// <summary>
    /// Right-click on a From-Liked-Songs card opens a flyout offering to
    /// unlike every track of the album. The actual unliking is gated by a
    /// confirm dialog because it's destructive and can't be undone in one
    /// click.
    /// </summary>
    private void LikedCardContainer_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (element.DataContext is not LikedAlbumDto album) return;

        var flyout = new MenuFlyout();
        var item = new MenuFlyoutItem
        {
            Text = $"Unlike all songs from this album ({album.LikedSongCount})",
            Icon = new FontIcon { Glyph = Wavee.UI.WinUI.Styles.FluentGlyphs.HeartFilled }
        };
        item.Click += async (_, _) => await ConfirmAndUnlikeAllAsync(album);
        flyout.Items.Add(item);

        flyout.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private async System.Threading.Tasks.Task ConfirmAndUnlikeAllAsync(LikedAlbumDto album)
    {
        var dialog = new ContentDialog
        {
            Title = AppLocalization.GetString("Library_UnlikeAllFromAlbumTitle"),
            Content = album.LikedSongCount == 1
                ? AppLocalization.Format("Library_UnlikeAllFromAlbum_OneContent", album.Name)
                : AppLocalization.Format("Library_UnlikeAllFromAlbum_ManyContent", album.LikedSongCount, album.Name),
            PrimaryButtonText = AppLocalization.GetString("Dialog_UnlikeAll"),
            CloseButtonText = AppLocalization.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            ViewModel.UnlikeAllSongsFromLikedAlbum(album);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Loaded -= OnLoaded;
        SizeChanged -= OnSizeChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
