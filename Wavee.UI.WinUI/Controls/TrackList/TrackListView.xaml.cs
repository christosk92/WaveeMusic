using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using CommunityToolkit.Mvvm.DependencyInjection;
using Windows.ApplicationModel.DataTransfer;
using Wavee.UI.WinUI.Controls.Track;
using Wavee.UI.WinUI.Controls.Track.Behaviors;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.DragDrop;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels.Contracts;
using Windows.Foundation;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Controls.TrackList;

/// <summary>
/// A reusable track list control with sorting, selection, sticky headers, and command bar.
/// </summary>
public sealed partial class TrackListView : UserControl
{
    private const int DurationColumnIndex = 6; // Base index of Duration column before custom columns
    private ScrollViewer? _scrollViewer;
    private INotifyCollectionChanged? _currentCollection;
    private readonly List<MenuFlyoutItem> _dynamicPlaylistMenuItems = [];
    private readonly List<UIElement> _scrollableCustomHeaderElements = [];
    private readonly List<UIElement> _stickyCustomHeaderElements = [];
    private readonly Dictionary<(Type, string), PropertyInfo?> _propertyCache = [];

    private readonly ThemeColorService? _themeColors;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    public TrackListView()
    {
        InitializeComponent();
        SetValue(CustomColumnsProperty, new List<TrackListColumnDefinition>());
        _themeColors = Ioc.Default.GetService<ThemeColorService>();
        if (_themeColors != null)
            _themeColors.ThemeChanged += OnThemeColorsChanged;
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

        // Clean up playback state subscription
        TrackStateBehavior.PlaybackStateChanged -= OnPlaybackStateChangedFromBehavior;

        // Clean up theme change subscription
        if (_themeColors != null)
        {
            _themeColors.ThemeChanged -= OnThemeColorsChanged;
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

            control.UpdateVisualState();
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateVisualState();
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
    /// Override background for column headers (scrollable + sticky). Set to Transparent to remove acrylic.
    /// </summary>
    public Microsoft.UI.Xaml.Media.Brush? HeaderBackground
    {
        get => (Microsoft.UI.Xaml.Media.Brush?)GetValue(HeaderBackgroundProperty);
        set => SetValue(HeaderBackgroundProperty, value);
    }

    public static readonly DependencyProperty HeaderBackgroundProperty =
        DependencyProperty.Register(nameof(HeaderBackground), typeof(Microsoft.UI.Xaml.Media.Brush), typeof(TrackListView),
            new PropertyMetadata(null, OnHeaderBackgroundChanged));

    private static void OnHeaderBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackListView control && e.NewValue is Microsoft.UI.Xaml.Media.Brush brush)
        {
            control.ScrollableColumnHeaders.Background = brush;
            control.StickyColumnHeaders.Background = brush;
        }
    }

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
    /// Content rendered below the track list (inside the ListView's scrollable area).
    /// Use for related albums, copyright info, etc.
    /// </summary>
    public object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }

    public static readonly DependencyProperty FooterContentProperty =
        DependencyProperty.Register(nameof(FooterContent), typeof(object), typeof(TrackListView),
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
            control.UpdateVisualState();
        }
    }

    /// <summary>
    /// Whether the control is in an error state.
    /// </summary>
    public bool HasError
    {
        get => (bool)GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }

    public static readonly DependencyProperty HasErrorProperty =
        DependencyProperty.Register(nameof(HasError), typeof(bool), typeof(TrackListView),
            new PropertyMetadata(false, OnHasErrorChanged));

    private static void OnHasErrorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackListView control)
        {
            control.UpdateVisualState();
        }
    }

    /// <summary>
    /// Error message to display in the default error state.
    /// </summary>
    public string? ErrorMessage
    {
        get => (string?)GetValue(ErrorMessageProperty);
        set => SetValue(ErrorMessageProperty, value);
    }

    public static readonly DependencyProperty ErrorMessageProperty =
        DependencyProperty.Register(nameof(ErrorMessage), typeof(string), typeof(TrackListView),
            new PropertyMetadata(null));

    /// <summary>
    /// Page-specific error state content (overrides default error panel).
    /// </summary>
    public object? ErrorStateContent
    {
        get => GetValue(ErrorStateContentProperty);
        set => SetValue(ErrorStateContentProperty, value);
    }

    public static readonly DependencyProperty ErrorStateContentProperty =
        DependencyProperty.Register(nameof(ErrorStateContent), typeof(object), typeof(TrackListView),
            new PropertyMetadata(null, OnErrorStateContentChanged));

    private static void OnErrorStateContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackListView control)
        {
            control.UpdateVisualState();
        }
    }

    /// <summary>
    /// Command to execute when the retry button is clicked.
    /// </summary>
    public System.Windows.Input.ICommand? RetryCommand
    {
        get => (System.Windows.Input.ICommand?)GetValue(RetryCommandProperty);
        set => SetValue(RetryCommandProperty, value);
    }

    public static readonly DependencyProperty RetryCommandProperty =
        DependencyProperty.Register(nameof(RetryCommand), typeof(System.Windows.Input.ICommand), typeof(TrackListView),
            new PropertyMetadata(null));

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

    /// <summary>
    /// Custom columns to display between DateAdded and Duration.
    /// Set in XAML or code-behind. Each column uses PropertyName (reflection) or ValueSelector (delegate).
    /// </summary>
    public IList<TrackListColumnDefinition> CustomColumns
    {
        get => (IList<TrackListColumnDefinition>)GetValue(CustomColumnsProperty);
        set => SetValue(CustomColumnsProperty, value);
    }

    public static readonly DependencyProperty CustomColumnsProperty =
        DependencyProperty.Register(nameof(CustomColumns), typeof(IList<TrackListColumnDefinition>), typeof(TrackListView),
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
        ApplyCustomColumns();
        UpdateColumnVisibility();

        // Subscribe to playback state changes via centralized TrackStateBehavior
        TrackStateBehavior.EnsurePlaybackSubscription();
        TrackStateBehavior.PlaybackStateChanged += OnPlaybackStateChangedFromBehavior;

        // Initial refresh of all visible rows
        UpdateAllVisibleRowPlaybackState();
    }

    private void UpdateColumnVisibility()
    {
        // Set column widths based on Show* properties
        // Column indices: 0=#, 1=Art, 2=Title, 3=Artist, 4=Album, 5=DateAdded, [custom...], Duration

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

    /// <summary>
    /// Injects custom column definitions and headers into the scrollable and sticky header grids.
    /// Called once on Loaded. Custom columns are inserted at index 6 (before Duration).
    /// </summary>
    private void ApplyCustomColumns()
    {
        var customCols = CustomColumns;
        var count = customCols?.Count ?? 0;
        if (count == 0) return;

        // Apply to both header grids
        ApplyCustomColumnsToHeaderGrid(ScrollableColumnHeaders, _scrollableCustomHeaderElements, customCols!);
        ApplyCustomColumnsToHeaderGrid(FindStickyHeaderGrid(), _stickyCustomHeaderElements, customCols!);
    }

    private void ApplyCustomColumnsToHeaderGrid(Grid? headerGrid, List<UIElement> trackedElements, IList<TrackListColumnDefinition> columns)
    {
        if (headerGrid == null) return;

        // Clean up previously injected elements
        foreach (var el in trackedElements)
            headerGrid.Children.Remove(el);
        trackedElements.Clear();

        // Remove previously injected ColumnDefinitions (beyond the original 7)
        while (headerGrid.ColumnDefinitions.Count > 7)
            headerGrid.ColumnDefinitions.RemoveAt(DurationColumnIndex);

        // Insert custom ColumnDefinitions at index 6 (pushing Duration right)
        for (int i = 0; i < columns.Count; i++)
        {
            headerGrid.ColumnDefinitions.Insert(DurationColumnIndex + i,
                new ColumnDefinition { Width = columns[i].Width });
        }

        // Move Duration header to new position
        var durationColIndex = DurationColumnIndex + columns.Count;

        // Shift existing elements that were at column >= DurationColumnIndex
        foreach (var child in headerGrid.Children.OfType<FrameworkElement>())
        {
            var col = Grid.GetColumn(child);
            if (col >= DurationColumnIndex && !trackedElements.Contains(child))
            {
                Grid.SetColumn(child, col + columns.Count);
            }
        }

        // Add custom column headers
        for (int i = 0; i < columns.Count; i++)
        {
            var colDef = columns[i];
            var colIndex = DurationColumnIndex + i;

            FrameworkElement headerElement;
            if (colDef.SortKey != null && ViewModel != null)
            {
                var button = new Button
                {
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0, 8, 8, 8),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Command = ViewModel.SortByCommand,
                    CommandParameter = colDef.SortKey,
                    Content = new TextBlock
                    {
                        Text = colDef.Header,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        Foreground = _themeColors?.TextSecondary ?? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    }
                };
                headerElement = button;
            }
            else
            {
                headerElement = new TextBlock
                {
                    Text = colDef.Header,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = _themeColors?.TextSecondary ?? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = colDef.TextAlignment,
                    Margin = new Thickness(0, 8, 0, 8)
                };
            }

            Grid.SetColumn(headerElement, colIndex);
            headerGrid.Children.Add(headerElement);
            trackedElements.Add(headerElement);
        }
    }

    private Grid? FindStickyHeaderGrid()
    {
        // The sticky header Border contains a Grid child
        return StickyColumnHeaders.Child as Grid;
    }

    private int CustomColumnCount => CustomColumns?.Count ?? 0;

    private string GetCustomColumnValue(object item, TrackListColumnDefinition col)
    {
        if (col.ValueSelector != null)
            return col.ValueSelector(item);

        if (col.PropertyName != null)
        {
            var type = item.GetType();
            var key = (type, col.PropertyName);
            if (!_propertyCache.TryGetValue(key, out var prop))
            {
                prop = type.GetProperty(col.PropertyName);
                _propertyCache[key] = prop;
            }
            return prop?.GetValue(item)?.ToString() ?? "";
        }

        return "";
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
                border.Background = null;
                border.BorderThickness = new Thickness(0);
                CommunityToolkit.WinUI.Effects.SetShadow(border, null);
            }
            else
            {
                var isEven = args.ItemIndex % 2 == 0;
                border.Padding = new Thickness(4, 2, 4, 2);
                border.CornerRadius = new CornerRadius(6);
                border.BorderThickness = isEven ? new Thickness(0) : new Thickness(1);
                border.BorderBrush = isEven
                    ? (_themeColors?.TransparentBrush ?? border.BorderBrush)
                    : (_themeColors?.GetBrush("CardStrokeColorDefaultBrush") ?? border.BorderBrush);
                border.Background = isEven
                    ? (_themeColors?.TransparentBrush ?? border.Background)
                    : (_themeColors?.CardBackground ?? border.Background);
            }

            // Adjust item container margin for compact mode
            if (args.ItemContainer is ListViewItem item)
            {
                item.Margin = IsCompact ? new Thickness(0, 1, 0, 1) : new Thickness(0, 2, 0, 2);
            }

            // Handle shimmer visibility for lazy items imperatively
            // (x:Bind in ListView containers can miss PropertyChanged updates)
            if (border.Child is Grid wrapperGrid)
            {
                var shimmerEl = wrapperGrid.FindName("ShimmerOverlay") as UIElement;
                var contentEl = wrapperGrid.FindName("ContentGrid") as UIElement;

                if (args.Item is ViewModels.LazyTrackItem lazy)
                {
                    void UpdateVisibility()
                    {
                        if (shimmerEl != null)
                            shimmerEl.Visibility = lazy.IsLoaded ? Visibility.Collapsed : Visibility.Visible;
                        if (contentEl != null)
                            contentEl.Visibility = lazy.IsLoaded ? Visibility.Visible : Visibility.Collapsed;
                    }

                    UpdateVisibility();

                    // Subscribe to IsLoaded changes for when Populate() is called
                    void OnPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
                    {
                        if (e.PropertyName == nameof(ViewModels.LazyTrackItem.IsLoaded))
                        {
                            DispatcherQueue.TryEnqueue(UpdateVisibility);
                        }
                    }

                    // Clean up previous subscription (container recycling)
                    if (args.ItemContainer.Tag is Action cleanup)
                        cleanup();

                    lazy.PropertyChanged += OnPropertyChanged;
                    args.ItemContainer.Tag = (Action)(() => lazy.PropertyChanged -= OnPropertyChanged);
                }
                else
                {
                    // Non-lazy item — always show content
                    if (shimmerEl != null) shimmerEl.Visibility = Visibility.Collapsed;
                    if (contentEl != null) contentEl.Visibility = Visibility.Visible;
                }
            }

            // Set row index, date added, and custom columns
            var grid = border.Child is Grid w ? w.FindName("ContentGrid") as Grid : null;
            if (grid != null)
            {
                var customCols = CustomColumns;
                var customCount = customCols?.Count ?? 0;

                // Apply column visibility to row grid
                // Base indices: 0=#, 1=Art, 2=Title, 3=Artist, 4=Album, 5=DateAdded, 6=Duration
                if (grid.ColumnDefinitions.Count >= 7)
                {
                    // Reduce index column width in compact mode (30 vs 40)
                    grid.ColumnDefinitions[0].Width = IsCompact ? new GridLength(30) : new GridLength(40);
                    grid.ColumnDefinitions[1].Width = ShowAlbumArtColumn ? new GridLength(48) : new GridLength(0);
                    grid.ColumnDefinitions[3].Width = ShowArtistColumn ? new GridLength(180) : new GridLength(0);
                    grid.ColumnDefinitions[4].Width = ShowAlbumColumn ? new GridLength(180) : new GridLength(0);
                    grid.ColumnDefinitions[5].Width = ShowDateAddedColumn ? new GridLength(120) : new GridLength(0);
                }

                // Handle custom columns: insert ColumnDefinitions and shift Duration
                if (customCount > 0)
                {
                    // Ensure grid has enough columns (7 base + N custom)
                    while (grid.ColumnDefinitions.Count < 7 + customCount)
                        grid.ColumnDefinitions.Insert(DurationColumnIndex, new ColumnDefinition());

                    // Set custom column widths
                    for (int i = 0; i < customCount; i++)
                        grid.ColumnDefinitions[DurationColumnIndex + i].Width = customCols![i].Width;

                    // Move Duration column to the end
                    var durationIndex = DurationColumnIndex + customCount;
                    grid.ColumnDefinitions[durationIndex].Width = new GridLength(60);

                    // Find and shift the Duration TextBlock (last TextBlock with HorizontalAlignment=Right, not DateAddedText)
                    foreach (var child in grid.Children.OfType<TextBlock>())
                    {
                        if (child.Name != "DateAddedText" && child.HorizontalAlignment == HorizontalAlignment.Right
                            && child.Tag is not string)
                        {
                            Grid.SetColumn(child, durationIndex);
                            break;
                        }
                    }

                    // Render custom column cells
                    for (int i = 0; i < customCount; i++)
                    {
                        var colDef = customCols![i];
                        var colIndex = DurationColumnIndex + i;
                        var cellTag = $"_customCol{i}";

                        // Find existing cell or create a new one
                        var cell = grid.Children.OfType<TextBlock>().FirstOrDefault(t => t.Tag as string == cellTag);
                        if (cell == null)
                        {
                            cell = new TextBlock
                            {
                                Tag = cellTag,
                                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                                Foreground = _themeColors?.TextSecondary ?? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                                VerticalAlignment = VerticalAlignment.Center,
                                HorizontalAlignment = colDef.TextAlignment,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                                MaxLines = 1
                            };
                            grid.Children.Add(cell);
                        }

                        Grid.SetColumn(cell, colIndex);
                        cell.Text = args.Item != null ? GetCustomColumnValue(args.Item, colDef) : "";
                    }
                }

                // Set row index — use original index if available, otherwise fall back to position
                var indexGrid = grid.Children.OfType<Grid>().FirstOrDefault();
                if (indexGrid != null)
                {
                    var indexText = indexGrid.Children.OfType<TextBlock>().FirstOrDefault();
                    if (indexText != null)
                    {
                        indexText.Text = (args.Item is ITrackItem trackItem && trackItem.OriginalIndex > 0)
                            ? trackItem.OriginalIndex.ToString()
                            : (args.ItemIndex + 1).ToString();
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

                // Apply now-playing state to this row
                if (args.Item is ITrackItem playbackTrackItem)
                {
                    ApplyPlaybackStateToRow(grid, playbackTrackItem);
                }
            }
        }
    }

    /// <summary>
    /// Gets the ContentGrid from a row Border (handles the wrapper Grid for shimmer overlay).
    /// </summary>
    private static Grid? GetContentGrid(Border border)
    {
        if (border.Child is Grid wrapper)
            return wrapper.FindName("ContentGrid") as Grid ?? wrapper;
        return null;
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && GetContentGrid(border) is Grid grid)
        {
            var indexGrid = grid.Children.OfType<Grid>().FirstOrDefault();
            if (indexGrid != null)
            {
                indexGrid.Tag = true; // Mark as hovered
                if (border.DataContext is ITrackItem trackItem)
                {
                    ApplyPlaybackStateToRow(grid, trackItem);
                }
            }
        }
    }

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && GetContentGrid(border) is Grid grid)
        {
            var indexGrid = grid.Children.OfType<Grid>().FirstOrDefault();
            if (indexGrid != null)
            {
                indexGrid.Tag = false; // Clear hover
                if (border.DataContext is ITrackItem trackItem)
                {
                    ApplyPlaybackStateToRow(grid, trackItem);
                }
            }
        }
    }

    #endregion

    #region Drag Handlers

    private void InternalListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var tracks = e.Items.OfType<ITrackItem>().ToList();
        if (tracks.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        var payload = new TrackDragPayload(tracks);
        e.Data.SetData(payload.DataFormat, payload.SerializedData);
        e.Data.RequestedOperation = DataPackageOperation.Copy;

        Ioc.Default.GetService<DragStateService>()?.StartDrag(payload);
    }

    private void InternalListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        Ioc.Default.GetService<DragStateService>()?.EndDrag();
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
            if (track.Id == TrackStateBehavior.CurrentTrackId)
            {
                // Toggle play/pause for the current track
                var playbackService = Ioc.Default.GetService<IPlaybackStateService>();
                playbackService?.PlayPause();
            }
            else
            {
                TrackClicked?.Invoke(this, track);
                ViewModel?.PlayTrackCommand?.Execute(track);
            }
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

    private void Row_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        if (el.DataContext is not ITrackItem track) return;

        var options = new TrackContextMenuOptions
        {
            PlayCommand = ViewModel?.PlayTrackCommand,
            AddToQueueCommand = ViewModel?.AddSelectedToQueueCommand,
            RemoveCommand = ViewModel?.RemoveSelectedCommand,
            RemoveLabel = RemoveActionLabel
        };

        var menu = TrackContextMenu.Create(track, options);
        menu.ShowAt(el, e.GetPosition(el));
        e.Handled = true;
    }

    #endregion

    #region Now-Playing State

    private void OnPlaybackStateChangedFromBehavior()
    {
        DispatcherQueue.TryEnqueue(UpdateAllVisibleRowPlaybackState);
    }

    private void OnThemeColorsChanged()
    {
        // Theme changed: re-apply all visible row backgrounds + playback states
        DispatcherQueue.TryEnqueue(() =>
        {
            for (int i = 0; i < InternalListView.Items.Count; i++)
            {
                if (InternalListView.ContainerFromIndex(i) is not ListViewItem container) continue;
                if (container.ContentTemplateRoot is Border border && GetContentGrid(border) is Grid grid)
                {
                    // Re-apply alternating row background
                    if (!IsCompact)
                    {
                        var isEven = i % 2 == 0;
                        border.Background = isEven
                            ? (_themeColors?.TransparentBrush)
                            : (_themeColors?.CardBackground);
                        border.BorderBrush = isEven
                            ? (_themeColors?.TransparentBrush)
                            : (_themeColors?.GetBrush("CardStrokeColorDefaultBrush"));
                    }

                    // Re-apply playback state (title color)
                    if (container.Content is ITrackItem trackItem)
                        ApplyPlaybackStateToRow(grid, trackItem);
                }
            }
        });
    }

    private void UpdateAllVisibleRowPlaybackState()
    {
        _logger?.LogDebug("UpdateAllVisibleRowPlaybackState: currentTrackId={CurrentTrackId}, isPlaying={IsPlaying}, itemCount={Count}",
            TrackStateBehavior.CurrentTrackId, TrackStateBehavior.IsCurrentlyPlaying, InternalListView.Items.Count);
        for (int i = 0; i < InternalListView.Items.Count; i++)
        {
            if (InternalListView.ContainerFromIndex(i) is not ListViewItem container) continue;
            if (container.ContentTemplateRoot is Border border && GetContentGrid(border) is Grid grid && container.Content is ITrackItem trackItem)
            {
                ApplyPlaybackStateToRow(grid, trackItem);
            }
        }
    }

    private void ApplyPlaybackStateToRow(Grid grid, ITrackItem trackItem)
    {
        var isThisTrack = trackItem.Id == TrackStateBehavior.CurrentTrackId;
        var isPlaying = isThisTrack && TrackStateBehavior.IsCurrentlyPlaying;
        var isBuffering = trackItem.Id == TrackStateBehavior.BufferingTrackId && TrackStateBehavior.IsCurrentlyBuffering;

        // Column 0: index Grid with RowIndex, NowPlayingIcon, PlayButton, (+ ProgressRing for buffering)
        var indexGrid = grid.Children.OfType<Grid>().FirstOrDefault();
        if (indexGrid != null)
        {
            var rowIndex = indexGrid.Children.OfType<TextBlock>().FirstOrDefault();
            var nowPlayingIcon = indexGrid.Children.OfType<FontIcon>().FirstOrDefault();
            var playButton = indexGrid.Children.OfType<Button>().FirstOrDefault();

            // Find or create ProgressRing for buffering indicator
            var progressRing = indexGrid.Children.OfType<ProgressRing>().FirstOrDefault();
            if (progressRing == null && isBuffering)
            {
                progressRing = new ProgressRing
                {
                    Width = 16, Height = 16,
                    IsActive = true,
                    Foreground = _themeColors?.AccentText
                        ?? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                indexGrid.Children.Add(progressRing);
            }

            bool isHovered = indexGrid.Tag is true;

            if (isBuffering && !isHovered)
            {
                // Buffering: show ProgressRing, hide everything else
                if (rowIndex != null) rowIndex.Visibility = Visibility.Collapsed;
                if (nowPlayingIcon != null) nowPlayingIcon.Visibility = Visibility.Collapsed;
                if (playButton != null) playButton.Visibility = Visibility.Collapsed;
                if (progressRing != null) { progressRing.Visibility = Visibility.Visible; progressRing.IsActive = true; }
            }
            else if (isHovered)
            {
                // Hovered: show play/pause button, hide others
                if (rowIndex != null) rowIndex.Visibility = Visibility.Collapsed;
                if (nowPlayingIcon != null) nowPlayingIcon.Visibility = Visibility.Collapsed;
                if (progressRing != null) progressRing.Visibility = Visibility.Collapsed;
                if (playButton != null)
                {
                    playButton.Visibility = Visibility.Visible;
                    if (playButton.Content is FontIcon btnIcon)
                    {
                        btnIcon.Glyph = isPlaying ? "\uE769" : "\uE768"; // Pause : Play
                    }
                }
            }
            else if (isThisTrack)
            {
                // Not hovered, this is the playing/paused track: show equalizer icon
                if (rowIndex != null) rowIndex.Visibility = Visibility.Collapsed;
                if (progressRing != null) progressRing.Visibility = Visibility.Collapsed;
                if (nowPlayingIcon != null)
                {
                    nowPlayingIcon.Visibility = Visibility.Visible;
                    nowPlayingIcon.Glyph = isPlaying ? "\uE995" : "\uE769"; // Equalizer : Pause
                }
                if (playButton != null) playButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Not hovered, not this track: show row index
                if (rowIndex != null) rowIndex.Visibility = Visibility.Visible;
                if (nowPlayingIcon != null) nowPlayingIcon.Visibility = Visibility.Collapsed;
                if (playButton != null) playButton.Visibility = Visibility.Collapsed;
                if (progressRing != null) progressRing.Visibility = Visibility.Collapsed;
            }
        }

        // Title text accent color for playing/buffering track
        var titleStack = grid.Children.OfType<StackPanel>().FirstOrDefault();
        if (titleStack != null)
        {
            var titleText = titleStack.Children.OfType<TextBlock>().FirstOrDefault();
            if (titleText != null)
            {
                titleText.Foreground = (isThisTrack || isBuffering)
                    ? (_themeColors?.AccentText ?? titleText.Foreground)
                    : (_themeColors?.TextPrimary ?? titleText.Foreground);
            }
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

    private void UpdateVisualState()
    {
        var hasItems = ItemsSource != null;

        if (HasError)
        {
            // Error state: hide everything else, show error
            LoadingOverlay.Visibility = Visibility.Collapsed;
            EmptyStatePresenter.Visibility = Visibility.Collapsed;
            InternalListView.Visibility = Visibility.Collapsed;
            ErrorStateContainer.Visibility = Visibility.Visible;

            // Show custom or default error content
            if (ErrorStateContent != null)
            {
                ErrorStatePresenter.Visibility = Visibility.Visible;
                DefaultErrorState.Visibility = Visibility.Collapsed;
            }
            else
            {
                ErrorStatePresenter.Visibility = Visibility.Collapsed;
                DefaultErrorState.Visibility = Visibility.Visible;
            }
        }
        else if (IsLoading)
        {
            // Loading state
            LoadingOverlay.Visibility = Visibility.Visible;
            EmptyStatePresenter.Visibility = Visibility.Collapsed;
            InternalListView.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            ErrorStateContainer.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Content or empty state
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ErrorStateContainer.Visibility = Visibility.Collapsed;
            EmptyStatePresenter.Visibility = !hasItems ? Visibility.Visible : Visibility.Collapsed;
            InternalListView.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    #endregion
}
