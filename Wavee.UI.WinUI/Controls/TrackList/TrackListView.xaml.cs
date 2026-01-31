using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.ViewModels.Contracts;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.TrackList;

/// <summary>
/// A reusable track list control with sorting, selection, sticky headers, and command bar.
/// </summary>
public sealed partial class TrackListView : UserControl
{
    private ScrollViewer? _scrollViewer;
    private INotifyCollectionChanged? _currentCollection;
    private readonly List<MenuFlyoutItem> _dynamicPlaylistMenuItems = [];

    public TrackListView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Clean up ScrollViewer subscription
        if (_scrollViewer != null)
        {
            _scrollViewer.ViewChanged -= OnScrollViewChanged;
            _scrollViewer = null;
        }

        // Clean up collection subscription
        if (_currentCollection != null)
        {
            _currentCollection.CollectionChanged -= OnCollectionChanged;
            _currentCollection = null;
        }

        // Clean up ViewModel PropertyChanged subscription
        if (ViewModel is INotifyPropertyChanged vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    #region Dependency Properties

    /// <summary>
    /// The ViewModel providing sorting, selection, and commands.
    /// </summary>
    public ITrackListViewModel? ViewModel
    {
        get => (ITrackListViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(ITrackListViewModel), typeof(TrackListView),
            new PropertyMetadata(null, OnViewModelChanged));

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackListView control)
        {
            // Unsubscribe from old ViewModel's PropertyChanged
            if (e.OldValue is INotifyPropertyChanged oldVm)
            {
                oldVm.PropertyChanged -= control.OnViewModelPropertyChanged;
            }

            // Subscribe to new ViewModel's PropertyChanged
            if (e.NewValue is INotifyPropertyChanged newVm)
            {
                newVm.PropertyChanged += control.OnViewModelPropertyChanged;
            }

            control.UpdateSelectionCommandBar();
            control.UpdateRemoveButtonLabel();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Force x:Bind to update when sort-related properties change
        if (e.PropertyName is "SortChevronGlyph" or "IsSortingByTitle" or "IsSortingByArtist"
            or "IsSortingByAlbum" or "IsSortingByAddedAt")
        {
            Bindings.Update();
        }
    }

    /// <summary>
    /// The collection of track items to display.
    /// </summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(TrackListView),
            new PropertyMetadata(null, OnItemsSourceChanged));

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackListView control)
        {
            // Unsubscribe from old collection
            if (control._currentCollection != null)
            {
                control._currentCollection.CollectionChanged -= control.OnCollectionChanged;
            }

            control.InternalListView.ItemsSource = e.NewValue as IEnumerable;

            // Subscribe to new collection for dynamic updates
            if (e.NewValue is INotifyCollectionChanged newCollection)
            {
                control._currentCollection = newCollection;
                newCollection.CollectionChanged += control.OnCollectionChanged;
            }
            else
            {
                control._currentCollection = null;
            }

            control.UpdateEmptyState();
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();
    }

    /// <summary>
    /// Whether to show the Date Added column.
    /// </summary>
    public bool ShowDateAddedColumn
    {
        get => (bool)GetValue(ShowDateAddedColumnProperty);
        set => SetValue(ShowDateAddedColumnProperty, value);
    }

    public static readonly DependencyProperty ShowDateAddedColumnProperty =
        DependencyProperty.Register(nameof(ShowDateAddedColumn), typeof(bool), typeof(TrackListView),
            new PropertyMetadata(true));

    /// <summary>
    /// Whether to show the Album column.
    /// </summary>
    public bool ShowAlbumColumn
    {
        get => (bool)GetValue(ShowAlbumColumnProperty);
        set => SetValue(ShowAlbumColumnProperty, value);
    }

    public static readonly DependencyProperty ShowAlbumColumnProperty =
        DependencyProperty.Register(nameof(ShowAlbumColumn), typeof(bool), typeof(TrackListView),
            new PropertyMetadata(true));

    /// <summary>
    /// Whether to show the Artist column.
    /// </summary>
    public bool ShowArtistColumn
    {
        get => (bool)GetValue(ShowArtistColumnProperty);
        set => SetValue(ShowArtistColumnProperty, value);
    }

    public static readonly DependencyProperty ShowArtistColumnProperty =
        DependencyProperty.Register(nameof(ShowArtistColumn), typeof(bool), typeof(TrackListView),
            new PropertyMetadata(true));

    /// <summary>
    /// Whether to show the column headers row (for sorting).
    /// Set to false for compact embedded use.
    /// </summary>
    public bool ShowColumnHeaders
    {
        get => (bool)GetValue(ShowColumnHeadersProperty);
        set => SetValue(ShowColumnHeadersProperty, value);
    }

    public static readonly DependencyProperty ShowColumnHeadersProperty =
        DependencyProperty.Register(nameof(ShowColumnHeaders), typeof(bool), typeof(TrackListView),
            new PropertyMetadata(true));

    /// <summary>
    /// Whether to show the album art column.
    /// Set to false for compact embedded use.
    /// </summary>
    public bool ShowAlbumArtColumn
    {
        get => (bool)GetValue(ShowAlbumArtColumnProperty);
        set => SetValue(ShowAlbumArtColumnProperty, value);
    }

    public static readonly DependencyProperty ShowAlbumArtColumnProperty =
        DependencyProperty.Register(nameof(ShowAlbumArtColumn), typeof(bool), typeof(TrackListView),
            new PropertyMetadata(true));

    /// <summary>
    /// Whether to use compact padding (reduced padding for embedded use).
    /// </summary>
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(TrackListView),
            new PropertyMetadata(false));

    /// <summary>
    /// Page-specific header content (title, icon, stats, play/shuffle buttons).
    /// </summary>
    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public static readonly DependencyProperty HeaderContentProperty =
        DependencyProperty.Register(nameof(HeaderContent), typeof(object), typeof(TrackListView),
            new PropertyMetadata(null));

    /// <summary>
    /// Page-specific empty state content.
    /// </summary>
    public object? EmptyStateContent
    {
        get => GetValue(EmptyStateContentProperty);
        set => SetValue(EmptyStateContentProperty, value);
    }

    public static readonly DependencyProperty EmptyStateContentProperty =
        DependencyProperty.Register(nameof(EmptyStateContent), typeof(object), typeof(TrackListView),
            new PropertyMetadata(null));

    /// <summary>
    /// Label for the remove action button (e.g., "Remove from Liked" or "Remove from playlist").
    /// </summary>
    public string RemoveActionLabel
    {
        get => (string)GetValue(RemoveActionLabelProperty);
        set => SetValue(RemoveActionLabelProperty, value);
    }

    public static readonly DependencyProperty RemoveActionLabelProperty =
        DependencyProperty.Register(nameof(RemoveActionLabel), typeof(string), typeof(TrackListView),
            new PropertyMetadata("Remove", OnRemoveActionLabelChanged));

    private static void OnRemoveActionLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackListView control)
        {
            control.UpdateRemoveButtonLabel();
        }
    }

    /// <summary>
    /// Whether the control is in a loading state.
    /// </summary>
    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(TrackListView),
            new PropertyMetadata(true, OnIsLoadingChanged));

    private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackListView control)
        {
            control.LoadingOverlay.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
            control.UpdateEmptyState();
        }
    }

    /// <summary>
    /// Delegate for getting the formatted date string from a track item.
    /// </summary>
    public Func<object, string>? DateAddedFormatter
    {
        get => (Func<object, string>?)GetValue(DateAddedFormatterProperty);
        set => SetValue(DateAddedFormatterProperty, value);
    }

    public static readonly DependencyProperty DateAddedFormatterProperty =
        DependencyProperty.Register(nameof(DateAddedFormatter), typeof(Func<object, string>), typeof(TrackListView),
            new PropertyMetadata(null));

    #endregion

    #region Events

    /// <summary>
    /// Raised when a track is clicked/double-clicked.
    /// </summary>
    public event EventHandler<ITrackItem>? TrackClicked;

    /// <summary>
    /// Raised when an artist link is clicked.
    /// </summary>
    public event EventHandler<string>? ArtistClicked;

    /// <summary>
    /// Raised when an album link is clicked.
    /// </summary>
    public event EventHandler<string>? AlbumClicked;

    /// <summary>
    /// Raised when "New playlist..." is clicked in the flyout.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? NewPlaylistRequested;

    #endregion

    #region Initialization

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Unsubscribe first if already subscribed (guard against double subscription)
        if (_scrollViewer != null)
        {
            _scrollViewer.ViewChanged -= OnScrollViewChanged;
        }

        // Get the ListView's internal ScrollViewer
        _scrollViewer = FindScrollViewer(InternalListView);
        if (_scrollViewer != null)
        {
            _scrollViewer.ViewChanged += OnScrollViewChanged;
        }

        UpdateRemoveButtonLabel();
        UpdateColumnVisibility();
    }

    private void UpdateColumnVisibility()
    {
        // Set column widths based on Show* properties
        // Column indices: 0=#, 1=Art, 2=Title, 3=Artist, 4=Album, 5=DateAdded, 6=Duration

        // Apply compact padding
        InternalListView.Padding = IsCompact
            ? new Thickness(0)
            : new Thickness(16, 0, 18, 0);

        // Column headers visibility (for compact mode)
        ScrollableColumnHeaders.Visibility = ShowColumnHeaders ? Visibility.Visible : Visibility.Collapsed;
        // Note: StickyColumnHeaders visibility is already managed by scroll logic

        // Album art column
        ArtColumnDef.Width = ShowAlbumArtColumn ? new GridLength(48) : new GridLength(0);
        StickyArtColumnDef.Width = ShowAlbumArtColumn ? new GridLength(48) : new GridLength(0);

        // Scrollable header columns
        ArtistColumnDef.Width = ShowArtistColumn ? new GridLength(180) : new GridLength(0);
        AlbumColumnDef.Width = ShowAlbumColumn ? new GridLength(180) : new GridLength(0);
        AddedColumnDef.Width = ShowDateAddedColumn ? new GridLength(120) : new GridLength(0);

        // Sticky header columns
        StickyArtistColumnDef.Width = ShowArtistColumn ? new GridLength(180) : new GridLength(0);
        StickyAlbumColumnDef.Width = ShowAlbumColumn ? new GridLength(180) : new GridLength(0);
        StickyAddedColumnDef.Width = ShowDateAddedColumn ? new GridLength(120) : new GridLength(0);

        // Header button visibility
        ArtistHeaderScrollable.Visibility = ShowArtistColumn ? Visibility.Visible : Visibility.Collapsed;
        AlbumHeaderScrollable.Visibility = ShowAlbumColumn ? Visibility.Visible : Visibility.Collapsed;
        AddedHeaderScrollable.Visibility = ShowDateAddedColumn ? Visibility.Visible : Visibility.Collapsed;

        StickyArtistHeader.Visibility = ShowArtistColumn ? Visibility.Visible : Visibility.Collapsed;
        StickyAlbumHeader.Visibility = ShowAlbumColumn ? Visibility.Visible : Visibility.Collapsed;
        StickyAddedHeader.Visibility = ShowDateAddedColumn ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv)
                return sv;

            var result = FindScrollViewer(child);
            if (result != null)
                return result;
        }
        return null;
    }

    #endregion

    #region Scroll & Sticky Header

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scrollViewer == null) return;

        // Get the position of the scrollable column headers relative to the control
        var transform = ScrollableColumnHeaders.TransformToVisual(this);
        var position = transform.TransformPoint(new Point(0, 0));

        // Show sticky header when the scrollable one goes above the viewport (position.Y < 0)
        // But only if column headers are enabled
        StickyColumnHeaders.Visibility = ShowColumnHeaders && position.Y < 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    #endregion

    #region Item Container & Row Handling

    private void TrackListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer?.ContentTemplateRoot is Border border)
        {
            // Apply compact styling
            if (IsCompact)
            {
                border.Padding = new Thickness(4, 2, 4, 2);
                border.CornerRadius = new CornerRadius(2);
                border.Background = null; // No card background in compact mode
                CommunityToolkit.WinUI.Effects.SetShadow(border, null);
            }
            else
            {
                // Set alternating row background
                var isEven = args.ItemIndex % 2 == 0;
                border.Padding = new Thickness(8, 8, 8, 8);
                border.CornerRadius = new CornerRadius(6);
                border.Background = isEven
                    ? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
                    : (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
            }

            // Adjust item container margin for compact mode
            if (args.ItemContainer is ListViewItem item)
            {
                item.Margin = IsCompact ? new Thickness(0, 1, 0, 1) : new Thickness(0, 2, 0, 2);
            }

            // Set row index and date added
            if (border.Child is Grid grid)
            {
                // Apply column visibility to row grid
                // Column indices: 0=#, 1=Art, 2=Title, 3=Artist, 4=Album, 5=DateAdded, 6=Duration
                if (grid.ColumnDefinitions.Count >= 7)
                {
                    // Reduce index column width in compact mode (30 vs 40)
                    grid.ColumnDefinitions[0].Width = IsCompact ? new GridLength(30) : new GridLength(40);
                    grid.ColumnDefinitions[1].Width = ShowAlbumArtColumn ? new GridLength(48) : new GridLength(0);
                    grid.ColumnDefinitions[3].Width = ShowArtistColumn ? new GridLength(180) : new GridLength(0);
                    grid.ColumnDefinitions[4].Width = ShowAlbumColumn ? new GridLength(180) : new GridLength(0);
                    grid.ColumnDefinitions[5].Width = ShowDateAddedColumn ? new GridLength(120) : new GridLength(0);
                }

                // Set row index
                var indexGrid = grid.Children.OfType<Grid>().FirstOrDefault();
                if (indexGrid != null)
                {
                    var indexText = indexGrid.Children.OfType<TextBlock>().FirstOrDefault();
                    if (indexText != null)
                    {
                        indexText.Text = (args.ItemIndex + 1).ToString();
                    }
                }

                // Set date added text if formatter is provided
                if (DateAddedFormatter != null && args.Item != null)
                {
                    var dateText = grid.FindName("DateAddedText") as TextBlock;
                    if (dateText != null)
                    {
                        dateText.Text = DateAddedFormatter(args.Item);
                    }
                }
            }
        }
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.Child is Grid grid)
        {
            var indexGrid = grid.Children.OfType<Grid>().FirstOrDefault();
            if (indexGrid != null)
            {
                foreach (var child in indexGrid.Children)
                {
                    if (child is TextBlock)
                        child.Visibility = Visibility.Collapsed;
                    else if (child is Button)
                        child.Visibility = Visibility.Visible;
                }
            }
        }
    }

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.Child is Grid grid)
        {
            var indexGrid = grid.Children.OfType<Grid>().FirstOrDefault();
            if (indexGrid != null)
            {
                foreach (var child in indexGrid.Children)
                {
                    if (child is TextBlock)
                        child.Visibility = Visibility.Visible;
                    else if (child is Button)
                        child.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    #endregion

    #region Click Handlers

    private void TrackItem_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ITrackItem track)
        {
            TrackClicked?.Invoke(this, track);
            ViewModel?.PlayTrackCommand?.Execute(track);
        }
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is ITrackItem track)
        {
            TrackClicked?.Invoke(this, track);
            ViewModel?.PlayTrackCommand?.Execute(track);
        }
    }

    private void ArtistLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton link && link.Tag is string artistId && !string.IsNullOrEmpty(artistId))
        {
            ArtistClicked?.Invoke(this, artistId);
        }
    }

    private void AlbumLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton link && link.Tag is string albumId && !string.IsNullOrEmpty(albumId))
        {
            AlbumClicked?.Invoke(this, albumId);
        }
    }

    #endregion

    #region Selection

    private void TrackListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel == null) return;

        var selectedItems = InternalListView.SelectedItems.Cast<object>().ToList();
        ViewModel.SelectedItems = selectedItems;
        UpdateSelectionCommandBar();
    }

    private void UpdateSelectionCommandBar()
    {
        if (ViewModel == null)
        {
            SelectionCommandBarBorder.Visibility = Visibility.Collapsed;
            return;
        }

        // Only show command bar when 2+ items are selected
        SelectionCommandBarBorder.Visibility = ViewModel.SelectedItems.Count > 1
            ? Visibility.Visible
            : Visibility.Collapsed;

        SelectionCommandBarItem.Header = ViewModel.SelectionHeaderText;
    }

    #endregion

    #region Playlist Flyout

    private void AddToPlaylistFlyout_Opening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout || ViewModel == null) return;

        // Unsubscribe and remove previously added playlist items
        foreach (var item in _dynamicPlaylistMenuItems)
        {
            item.Click -= PlaylistMenuItem_Click;
            flyout.Items.Remove(item);
        }
        _dynamicPlaylistMenuItems.Clear();

        // Show separator and add playlist items if we have playlists
        var playlists = ViewModel.Playlists;
        PlaylistSeparator.Visibility = playlists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var playlist in playlists)
        {
            var menuItem = new MenuFlyoutItem
            {
                Text = playlist.Name,
                Tag = playlist,
                Icon = new FontIcon { Glyph = "\uE8FD" }
            };
            menuItem.Click += PlaylistMenuItem_Click;
            _dynamicPlaylistMenuItems.Add(menuItem);
            flyout.Items.Add(menuItem);
        }
    }

    private void PlaylistMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is PlaylistSummaryDto playlist)
        {
            ViewModel?.AddToPlaylistCommand?.Execute(playlist);
        }
    }

    private void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        var trackIds = ViewModel.SelectedItems
            .OfType<ITrackItem>()
            .Select(t => t.Id)
            .ToList();

        NewPlaylistRequested?.Invoke(this, trackIds);
    }

    #endregion

    #region Helpers

    private void UpdateRemoveButtonLabel()
    {
        RemoveButton.Label = RemoveActionLabel;
    }

    private void UpdateEmptyState()
    {
        var hasItems = ItemsSource != null;

        // Don't show empty state while loading - let loading overlay/shimmer handle it
        var showEmptyState = !hasItems && !IsLoading;

        EmptyStatePresenter.Visibility = showEmptyState ? Visibility.Visible : Visibility.Collapsed;
        InternalListView.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion
}
