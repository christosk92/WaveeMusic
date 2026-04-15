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
using Microsoft.UI.Xaml.Media.Animation;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Windows.ApplicationModel.DataTransfer;
using Wavee.UI.WinUI.Controls.Track;
using System.Windows.Input;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.DragDrop;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels.Contracts;
using Windows.Foundation;
namespace Wavee.UI.WinUI.Controls.TrackList;

/// <summary>
/// A reusable track list control with sorting, selection, sticky headers, and command bar.
/// </summary>
public sealed partial class TrackListView : UserControl
{
    private const int DurationColumnIndex = 7; // Base index of Duration column before custom columns (0=#, 1=Heart, 2=Art, 3=Title, 4=Artist, 5=Album, 6=DateAdded, 7=Duration)
    private ScrollViewer? _scrollViewer;
    private INotifyCollectionChanged? _currentCollection;
    private readonly List<MenuFlyoutItem> _dynamicPlaylistMenuItems = [];
    private readonly List<UIElement> _scrollableCustomHeaderElements = [];
    private readonly List<UIElement> _stickyCustomHeaderElements = [];
    private readonly Dictionary<(Type, string), PropertyInfo?> _propertyCache = [];

    private readonly ThemeColorService? _themeColors;
    private readonly DragStateService? _dragStateService;

    // Fallback command when no ViewModel is set (e.g., AlbumDetailPanel).
    // Raises the TrackClicked event so the parent can handle playback.
    private readonly ICommand _trackClickedCommand;

    public TrackListView()
    {
        InitializeComponent();
        SetValue(CustomColumnsProperty, new List<TrackListColumnDefinition>());
        _trackClickedCommand = new RelayCommand<ITrackItem>(t =>
        {
            if (t != null) TrackClicked?.Invoke(this, t);
        });
        _themeColors = Ioc.Default.GetService<ThemeColorService>();
        _dragStateService = Ioc.Default.GetService<DragStateService>();
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

    public string? PlaceholderColorHex
    {
        get => (string?)GetValue(PlaceholderColorHexProperty);
        set => SetValue(PlaceholderColorHexProperty, value);
    }

    public static readonly DependencyProperty PlaceholderColorHexProperty =
        DependencyProperty.Register(nameof(PlaceholderColorHex), typeof(string), typeof(TrackListView),
            new PropertyMetadata(null, OnPlaceholderColorHexChanged));

    /// <summary>
    /// When true, each realized TrackItem resolves its own per-track placeholder color
    /// via <see cref="Wavee.UI.Services.ITrackColorHintService"/>. Use this for track lists
    /// that span multiple albums (liked songs, playlists, search) where a single
    /// <see cref="PlaceholderColorHex"/> wouldn't be meaningful.
    /// </summary>
    public bool UseImageColorHint
    {
        get => (bool)GetValue(UseImageColorHintProperty);
        set => SetValue(UseImageColorHintProperty, value);
    }

    public static readonly DependencyProperty UseImageColorHintProperty =
        DependencyProperty.Register(nameof(UseImageColorHint), typeof(bool), typeof(TrackListView),
            new PropertyMetadata(false));

    private static void OnPlaceholderColorHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (TrackListView)d;
        var hex = e.NewValue as string;
        if (view.InternalListView?.Items == null) return;
        foreach (var item in view.InternalListView.Items)
        {
            if (view.InternalListView.ContainerFromItem(item) is ListViewItem lvi
                && lvi.ContentTemplateRoot is TrackItem ti)
            {
                ti.PlaceholderColorHex = hex;
            }
        }
    }


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

    /// <summary>
    /// Raised when the internal virtualizing list scrolls. Used by host pages that
    /// need to coordinate companion chrome without taking over scroll ownership.
    /// </summary>
    public event EventHandler<double>? ScrollOffsetChanged;

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

    }

    private void UpdateColumnVisibility()
    {
        // Set column widths based on Show* properties
        // Column indices: 0=#, 1=Heart, 2=Art, 3=Title, 4=Artist, 5=Album, 6=DateAdded, [custom...], Duration

        // Apply compact padding
        InternalListView.Padding = IsCompact
            ? new Thickness(0)
            : new Thickness(16, 0, 18, 0);

        // Column headers visibility (for compact mode)
        ScrollableColumnHeaders.Visibility = ShowColumnHeaders ? Visibility.Visible : Visibility.Collapsed;
        // Note: StickyColumnHeaders visibility is already managed by scroll logic

        // Album art column
        ArtColumnDef.Width = ShowAlbumArtColumn ? new GridLength(42) : new GridLength(0);
        StickyArtColumnDef.Width = ShowAlbumArtColumn ? new GridLength(42) : new GridLength(0);

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

        // Remove previously injected ColumnDefinitions (beyond the original 8: #, Heart, Art, Title, Artist, Album, DateAdded, Duration)
        while (headerGrid.ColumnDefinitions.Count > 8)
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
        ScrollOffsetChanged?.Invoke(this, _scrollViewer.VerticalOffset);

        // Get the position of the scrollable column headers relative to the control
        var transform = ScrollableColumnHeaders.TransformToVisual(this);
        var position = transform.TransformPoint(new Point(0, 0));

        // Show sticky header when the scrollable one goes above the viewport (position.Y < 0)
        // But only if column headers are enabled
        StickyColumnHeaders.Visibility = ShowColumnHeaders && position.Y < 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public void SetItemTransitionsEnabled(bool enabled)
    {
        InternalListView.ItemContainerTransitions = enabled
            ? new TransitionCollection
            {
                new EntranceThemeTransition { IsStaggeringEnabled = true },
                new AddDeleteThemeTransition()
            }
            : null;
    }

    #endregion

    #region Item Container & Row Handling

    private void TrackListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer?.ContentTemplateRoot is TrackItem trackItem)
        {
            // Configure TrackItem with current display settings
            trackItem.ShowAlbumArt = ShowAlbumArtColumn;
            trackItem.ShowArtistColumn = ShowArtistColumn;
            trackItem.ShowAlbumColumn = ShowAlbumColumn;
            trackItem.ShowDateAdded = ShowDateAddedColumn;
            trackItem.IsCompactRow = IsCompact;
            trackItem.RowIndex = args.ItemIndex + 1;
            trackItem.PlaceholderColorHex = PlaceholderColorHex;
            trackItem.UseImageColorHint = UseImageColorHint;

            // Wire up commands from ViewModel, or fall back to raising TrackClicked event
            trackItem.PlayCommand = ViewModel?.PlayTrackCommand ?? _trackClickedCommand;
            trackItem.AddToQueueCommand = ViewModel?.AddSelectedToQueueCommand;
            trackItem.RemoveCommand = ViewModel?.RemoveSelectedCommand;
            trackItem.RemoveCommandLabel = RemoveActionLabel;

            // Date formatter
            if (DateAddedFormatter != null && args.Item != null)
                trackItem.DateAddedText = DateAddedFormatter(args.Item);

            // Handle LazyTrackItem shimmer
            if (args.Item is ViewModels.LazyTrackItem lazy)
            {
                trackItem.IsLoading = !lazy.IsLoaded;

                void OnPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(ViewModels.LazyTrackItem.IsLoaded))
                        DispatcherQueue.TryEnqueue(() => trackItem.IsLoading = !lazy.IsLoaded);
                }

                if (args.ItemContainer.Tag is Action cleanup) cleanup();
                lazy.PropertyChanged += OnPropertyChanged;
                args.ItemContainer.Tag = (Action)(() => lazy.PropertyChanged -= OnPropertyChanged);
            }
            else
            {
                trackItem.IsLoading = false;
            }

            // Alternating row border for visual separation
            // Alternating row styling only for full TrackListViews (with ViewModel), not inline panels
            if (trackItem.Mode == TrackItemDisplayMode.Row)
            {
                if (ViewModel != null)
                    trackItem.SetAlternatingBorder(args.ItemIndex % 2 != 0);
                else
                    trackItem.SetAlternatingBorder(false); // inline panel: all rows transparent
            }

            // Populate custom column values (e.g. Plays)
            if (CustomColumns?.Count > 0 && args.Item != null)
            {
                var values = new string[CustomColumns.Count];
                for (int i = 0; i < CustomColumns.Count; i++)
                    values[i] = GetCustomColumnValue(args.Item, CustomColumns[i]);
                trackItem.SetCustomColumnValues(values, CustomColumns);
            }

            // Adjust item container margin for compact mode
            if (args.ItemContainer is ListViewItem lvItem)
                lvItem.Margin = IsCompact ? new Thickness(0, 1, 0, 1) : new Thickness(0, 2, 0, 2);
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

        _dragStateService?.StartDrag(payload);
    }

    private void InternalListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _dragStateService?.EndDrag();
    }

    #endregion

    // Click handlers, context menu, and navigation links are now handled
    // internally by TrackItem. TrackListView no longer needs them.

    #region Theme Changes

    private void OnThemeColorsChanged()
    {
        // Theme colors changed — refresh all visible TrackItems so playing indicator uses new colors
        DispatcherQueue.TryEnqueue(() =>
        {
            for (int i = 0; i < InternalListView.Items.Count; i++)
            {
                if (InternalListView.ContainerFromIndex(i) is ListViewItem container &&
                    container.ContentTemplateRoot is TrackItem ti)
                {
                    ti.RefreshPlaybackState();
                }
            }
        });
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
