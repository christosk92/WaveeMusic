using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.Services.DragDrop.Payloads;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Controls.Reorder;
using Wavee.UI.WinUI.DragDrop;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Sortable / filterable track list. Fetches its column set per <see cref="PageKey"/>
/// from <see cref="TrackDataGridDefaults"/>. Rows are rendered by <see cref="Track.TrackItem"/>
/// (Row mode), which owns all per-row visuals, heart toggle, hover paint, selection pill,
/// tap-to-play (respecting <c>TrackClickBehavior</c>), and context menu — no duplication
/// with <c>TrackListView</c>. This grid adds toolbar chrome (filter / sort / density /
/// details) and a column header row that shares widths with <c>TrackItem</c>'s internal
/// Row grid.
/// </summary>
public sealed partial class TrackDataGrid : UserControl, IDisposable
{
    // Default count when no page binds LoadingRowCount (or binds it as 0). Below
    // the album-length median, so the typical case grows rows downward (additive,
    // calm) rather than collapsing on load.
    private const int DefaultLoadingRowCount = 6;
    // Clamp ceiling so a 200-track playlist doesn't render 200 skeleton rows.
    private const int MaxLoadingRowCount = 20;
    // Rows the ItemsView renders. With IsGrouped=false this is a flat list of
    // ITrackItem; with IsGrouped=true the grid interleaves TrackDataGridGroupRow
    // markers between buckets and the ItemTemplateSelector routes each kind to
    // the right template. Selection / sort / reorder helpers filter
    // `is ITrackItem` so the markers are transparent to those flows.
    private readonly ObservableCollection<object> _visibleRows = new();
    private IReadOnlyList<ITrackItem> _sourceSnapshot = Array.Empty<ITrackItem>();
    private INotifyCollectionChanged? _subscribedSource;
    private ISettingsService? _settingsService;
    private string _filterText = string.Empty;
    private bool _disposed;
    private bool _restoringSelection;
    private readonly HashSet<Track.TrackItem> _itemsViewRows = new();
    private int _projectionDiagnosticGeneration;
    // Centralized LazyTrackItem.PropertyChanged subscription book-keeping. One
    // shared handler (_lazyItemHandler) is attached at most once per source
    // LazyTrackItem; the realized row is looked up via _rowByLazyItem on
    // notification. This replaces the previous per-row subscription model
    // (~50× simultaneous closures) and shrinks the WinRT reference-tracker walk
    // accordingly. Lifecycle: source membership owned by _visibleRows
    // CollectionChanged; row↔item mapping owned by container Loaded/Unloaded.
    private readonly Dictionary<LazyTrackItem, Track.TrackItem> _rowByLazyItem = new();
    private readonly Dictionary<Track.TrackItem, LazyTrackItem> _lazyItemByRow = new();
    private readonly HashSet<LazyTrackItem> _subscribedLazyItems = new();
    private PropertyChangedEventHandler? _lazyItemHandler;
    public event EventHandler<ITrackItem>? RowSelected;
    public event EventHandler? RowsScrollViewChanged;

    public ScrollView? RowsScrollView => _rowsItemsViewScrollView ?? RowsItemsView?.ScrollView;

    // Size-slider stops (matches the XS/S/M/L/XL segmentation in the view flyout).
    // MinHeight floor per row; content (padding + art + text) may still push the
    // row taller on larger steps, and that's intentional.
    private static readonly double[] DensityRowHeights = { 32d, 40d, 48d, 60d, 76d };
    private static readonly Thickness[] DensityRowPaddings =
    {
        new(4, 2, 4, 2),
        new(6, 4, 6, 4),
        new(8, 6, 8, 6),
        new(10, 10, 10, 10),
        new(12, 14, 12, 14),
    };
    private static readonly double[] DensityArtSizes = { 0d, 28d, 34d, 40d, 48d };

    public TrackDataGrid()
    {
        InitializeComponent();
        ApplyGroupHeaderTemplate();
        RowsItemsView.ItemsSource = _visibleRows;
        ApplyLoadingRowCount();
        // Centralized subscription bus (see field comment).
        _lazyItemHandler = OnAnyLazyItemPropertyChanged;
        _visibleRows.CollectionChanged += OnVisibleRowsCollectionChanged;
        RowsItemsView.Loaded += RowsItemsView_Loaded;
        RowsItemsView.Unloaded += RowsItemsView_Unloaded;

        // Set Slider.Value AFTER InitializeComponent so Minimum/Maximum are already in
        // place — attribute-order parsing in XAML was failing to apply Value="2" before
        // the other RangeBase properties settled.
        DensitySlider.Value = 2;

        var defaults = TrackDataGridDefaults.Create(TrackDataGridDefaults.PlaylistPageKey);
        ApplyPersistedColumnWidths(defaults, TrackDataGridDefaults.PlaylistPageKey);
        SetValue(ColumnsProperty, defaults);
        Root.Tag = defaults;
        SubscribeColumns(defaults);
        SyncAddedByColumnVisibility();
        RebuildHeader();
        RebuildSortFlyout();
        ApplyHorizontalRowScroll();
        ApplyVerticalRowScroll();
        WireRowContextMenuHandlers();
    }

    // Sticky-header sync: the HeaderHost Grid lives outside the ItemsView's
    // inner ScrollView so it stays vertically pinned at the top. When the
    // user scrolls horizontally we translate the header to match the
    // ItemsView.ScrollView.HorizontalOffset via ViewChanged.
    private ScrollView? _rowsItemsViewScrollView;

    private void RowsItemsView_Loaded(object sender, RoutedEventArgs e)
    {
        HookRowsItemsViewScrollView();
        ApplyHorizontalRowScroll();
        ApplyVerticalRowScroll();
    }

    private void RowsItemsView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_rowsItemsViewScrollView is not null)
        {
            _rowsItemsViewScrollView.ViewChanged -= RowsItemsViewScrollView_ViewChanged;
            _rowsItemsViewScrollView = null;
            RowsScrollViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyHorizontalRowScroll()
    {
        if (RowsItemsView?.ScrollView is not { } itemsScrollView) return;

        if (AllowHorizontalRowScroll)
        {
            itemsScrollView.HorizontalScrollMode = ScrollingScrollMode.Auto;
            itemsScrollView.HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Auto;
        }
        else
        {
            itemsScrollView.HorizontalScrollMode = ScrollingScrollMode.Disabled;
            itemsScrollView.HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
        }
    }

    private void HookRowsItemsViewScrollView()
    {
        if (_rowsItemsViewScrollView is not null) return;

        var scrollView = RowsItemsView.ScrollView;
        if (scrollView is null) return;

        _rowsItemsViewScrollView = scrollView;
        scrollView.ViewChanged += RowsItemsViewScrollView_ViewChanged;
        HeaderScrollTransform.X = -scrollView.HorizontalOffset;
        RowsScrollViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RowsItemsViewScrollView_ViewChanged(ScrollView sender, object args)
    {
        HeaderScrollTransform.X = -sender.HorizontalOffset;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent is not null)
        {
            if (parent is T match)
                return match;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    // ── Centralized LazyTrackItem subscription bus ─────────────────────────
    // Source-membership lifecycle: subscribe/unsubscribe per item from
    // _visibleRows.CollectionChanged. Row↔item mapping lifecycle: maintained
    // by the two row paths below.

    private void OnVisibleRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is not null)
                    foreach (var x in e.NewItems) if (x is LazyTrackItem lazy) SubscribeLazyItem(lazy);
                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is not null)
                    foreach (var x in e.OldItems) if (x is LazyTrackItem lazy) UnsubscribeLazyItem(lazy);
                break;
            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems is not null)
                    foreach (var x in e.OldItems) if (x is LazyTrackItem lazy) UnsubscribeLazyItem(lazy);
                if (e.NewItems is not null)
                    foreach (var x in e.NewItems) if (x is LazyTrackItem lazy) SubscribeLazyItem(lazy);
                break;
            case NotifyCollectionChangedAction.Reset:
                // ReplaceWith fires a single Reset; reconcile by diffing the
                // current source against what we think we're subscribed to.
                ResyncLazyItemSubscriptions();
                break;
        }
    }

    private void SubscribeLazyItem(LazyTrackItem lazy)
    {
        if (_subscribedLazyItems.Add(lazy) && _lazyItemHandler is not null)
            lazy.PropertyChanged += _lazyItemHandler;
    }

    private void UnsubscribeLazyItem(LazyTrackItem lazy)
    {
        if (_subscribedLazyItems.Remove(lazy) && _lazyItemHandler is not null)
            lazy.PropertyChanged -= _lazyItemHandler;
    }

    private void ResyncLazyItemSubscriptions()
    {
        var current = new HashSet<LazyTrackItem>();
        foreach (var item in _visibleRows)
            if (item is LazyTrackItem lazy) current.Add(lazy);

        // Drop any subscription whose item is no longer in the source.
        if (_subscribedLazyItems.Count > 0)
        {
            var toDrop = new List<LazyTrackItem>();
            foreach (var lazy in _subscribedLazyItems)
                if (!current.Contains(lazy)) toDrop.Add(lazy);
            foreach (var lazy in toDrop) UnsubscribeLazyItem(lazy);
        }

        foreach (var lazy in current) SubscribeLazyItem(lazy);
    }

    private void OnAnyLazyItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LazyTrackItem lazy) return;
        if (e.PropertyName is not (nameof(LazyTrackItem.IsLoaded)
            or nameof(LazyTrackItem.Data)
            or nameof(ITrackItem.AddedAtFormatted)
            or nameof(ITrackItem.PlayCountFormatted))) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_rowByLazyItem.TryGetValue(lazy, out var row)) return;
            if (!ReferenceEquals(row.Track, lazy)) return;
            row.IsLoading = !lazy.IsLoaded;
            if (lazy.IsLoaded)
                ApplyFormattedCells(row, lazy);
        });
    }

    private void RegisterRowForLazyItem(Track.TrackItem row, LazyTrackItem? lazy)
    {
        UnregisterRow(row);
        if (lazy is null) return;
        _rowByLazyItem[lazy] = row;
        _lazyItemByRow[row] = lazy;
    }

    private void UnregisterRow(Track.TrackItem row)
    {
        if (!_lazyItemByRow.Remove(row, out var lazy)) return;
        if (_rowByLazyItem.TryGetValue(lazy, out var mapped) && ReferenceEquals(mapped, row))
            _rowByLazyItem.Remove(lazy);
    }

    private readonly HashSet<Track.TrackItem> _itemsViewRowsWithManualDrag = new();

    private void RowsItemsViewTrackItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Track.TrackItem row)
            return;

        _itemsViewRows.Add(row);
        row.TrackChanged -= RowsItemsViewTrackItem_TrackChanged;
        row.TrackChanged += RowsItemsViewTrackItem_TrackChanged;

        // Selection-mode wiring. SupportsSelectionMode lights up the row's
        // "Select" context-menu entry; IsSelectionMode catches rows realized
        // mid-session up to the grid's current mode.
        row.SupportsSelectionMode = true;
        row.IsSelectionMode = _isSelectionMode;
        row.SelectionToggleRequested -= OnRowSelectionToggleRequested;
        row.SelectionToggleRequested += OnRowSelectionToggleRequested;
        row.EnterSelectionRequested -= OnRowEnterSelectionRequested;
        row.EnterSelectionRequested += OnRowEnterSelectionRequested;

        var sourceItem = row.Track;
        var index = sourceItem is null ? -1 : _visibleRows.IndexOf(sourceItem);
        ConfigureItemsViewRow(row, sourceItem, index);
        RegisterRowForLazyItem(row, sourceItem as LazyTrackItem);
        ApplyItemsViewContainerDensity(row);
        row.IsSelected = sourceItem is not null && RowsItemsView.SelectedItems.Contains(sourceItem);

        // Attach manual drag once per realized row. WinUI 3's ItemContainer/
        // CanDrag pipeline doesn't fire DragStarting when an inner control
        // captures pointer for selection; the helper drives StartDragAsync
        // from a movement threshold instead.
        if (_itemsViewRowsWithManualDrag.Add(row))
        {
            ManualDragAttachment.AttachWithPackageWriter(row, () =>
            {
                var t = row.Track;
                if (t is null) return null;
                var selected = RowsItemsView.SelectedItems?.OfType<ITrackItem>().ToList()
                                ?? new List<ITrackItem>();
                IReadOnlyList<ITrackItem> tracks = selected.Contains(t) && selected.Count > 0
                    ? selected
                    : new[] { t };
                var uris = tracks.Select(x => x.Id).ToArray();
                var sourceIndex = _visibleRows.IndexOf(tracks[0]);
                return new TrackDragPayload(
                    uris,
                    sourceContextUri: ContextUri,
                    sourceStartIndex: sourceIndex >= 0 ? sourceIndex : null);
            });
        }
    }

    private void RowsItemsViewTrackItem_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Track.TrackItem row)
            return;

        _itemsViewRows.Remove(row);
        // Intentionally leave the row in _itemsViewRowsWithManualDrag so a
        // recycle (Unload → Load on the same row instance) doesn't double-
        // register the manual-drag pointer handlers.
        row.TrackChanged -= RowsItemsViewTrackItem_TrackChanged;
        row.SelectionToggleRequested -= OnRowSelectionToggleRequested;
        row.EnterSelectionRequested -= OnRowEnterSelectionRequested;
        UnregisterRow(row);
    }

    private void RowsItemsViewTrackItem_TrackChanged(object? sender, EventArgs e)
    {
        if (sender is not Track.TrackItem row || !_itemsViewRows.Contains(row))
            return;

        var sourceItem = row.Track;
        var index = sourceItem is null ? -1 : _visibleRows.IndexOf(sourceItem);
        ConfigureItemsViewRow(row, sourceItem, index);
        RegisterRowForLazyItem(row, sourceItem as LazyTrackItem);
        ApplyItemsViewContainerDensity(row);
        row.IsSelected = sourceItem is not null && RowsItemsView.SelectedItems.Contains(sourceItem);
    }

    private void ConfigureItemsViewRow(Track.TrackItem row, object? sourceItem, int itemIndex)
    {
        row.PlayCommand = PlayCommand;
        row.RowDensity = _preferredDensity;
        row.ShowPopularityBadge = ShouldShowPopularityBadge(sourceItem);
        row.SetAlternatingBorder(IsAlternateRow(sourceItem, itemIndex), UseCardRows);
        row.IsLoading = sourceItem is ITrackItem { IsLoaded: false };

        row.BeginBatchUpdate();
        row.ShowAlbumArt = ColumnVisible("TrackArt");
        row.ShowArtistColumn = ResolveShowArtistColumn();
        row.ShowAlbumColumn = ColumnVisible("Album");
        row.ShowAddedByColumn = AddedByVisible && ColumnVisible("AddedBy");
        row.ShowDateAdded = ColumnVisible("DateAdded");
        row.ShowPlayCount = ColumnVisible("PlayCount");
        row.ShowProgress = ShouldShowInlineProgress();
        PushWidthsToRow(row);
        row.EndBatchUpdate();

        ApplyFormattedCells(row, sourceItem);
    }

    private void ApplyItemsViewContainerDensity(Track.TrackItem row)
    {
        if (FindParent<ItemContainer>(row) is not { } container)
            return;

        container.MinHeight = _preferredRowHeight ?? DensityRowHeights[_preferredDensity];
        container.Margin = _preferredDensity == 0 ? new Thickness(0) : new Thickness(0, 2, 0, 2);
    }

    private bool ShouldShowPopularityBadge(object? row)
    {
        if (row is null || PopularityBadgeSelector is null)
            return false;

        try { return PopularityBadgeSelector(row); }
        catch { return false; }
    }

    private void RefreshPopularityBadges()
    {
        foreach (var row in _itemsViewRows.ToArray())
            row.ShowPopularityBadge = ShouldShowPopularityBadge(row.Track);
    }

    private void ApplyFormattedCells(Track.TrackItem item, object? row)
    {
        if (row is null) return;

        if (DateAddedFormatter != null)
            item.DateAddedText = DateAddedFormatter(row);
        if (PlayCountFormatter != null)
            item.PlayCountText = PlayCountFormatter(row);
        if (AddedByFormatter != null)
        {
            var info = AddedByFormatter(row);
            item.AddedByText = info.Text;
            item.AddedByAvatarUrl = info.AvatarUrl;
        }
        else
        {
            item.AddedByText = string.Empty;
            item.AddedByAvatarUrl = null;
        }

        item.ShowPopularityBadge = ShouldShowPopularityBadge(row);
    }

    // ------------------------------------------------------------------ DPs

    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(nameof(Columns), typeof(TrackDataGridColumns), typeof(TrackDataGrid),
            new PropertyMetadata(null, OnColumnsChanged));

    public TrackDataGridColumns? Columns
    {
        get => (TrackDataGridColumns?)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public static readonly DependencyProperty PageKeyProperty =
        DependencyProperty.Register(nameof(PageKey), typeof(string), typeof(TrackDataGrid),
            new PropertyMetadata(TrackDataGridDefaults.PlaylistPageKey, OnPageKeyChanged));

    public string PageKey
    {
        get => (string)GetValue(PageKeyProperty);
        set => SetValue(PageKeyProperty, value);
    }

    /// <summary>
    /// Force the per-row artist subline to render even when the page-key default
    /// hides it. Album pages default to <c>false</c> because most albums are
    /// single-artist and the artist is implied by the page; soundtracks /
    /// compilations / collaborations override this to <c>true</c> so the row
    /// surfaces the per-track contributor.
    /// </summary>
    public static readonly DependencyProperty ForceShowArtistColumnProperty =
        DependencyProperty.Register(
            nameof(ForceShowArtistColumn),
            typeof(bool),
            typeof(TrackDataGrid),
            new PropertyMetadata(false, OnForceShowArtistColumnChanged));

    public bool ForceShowArtistColumn
    {
        get => (bool)GetValue(ForceShowArtistColumnProperty);
        set => SetValue(ForceShowArtistColumnProperty, value);
    }

    private static void OnForceShowArtistColumnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (TrackDataGrid)d;
        grid.RefreshRowShowFlags();
        grid.ApplyLoadingRowCount();
    }

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(TrackDataGrid),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(TrackDataGrid),
            new PropertyMetadata(false, OnIsLoadingChanged));

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackDataGrid grid)
            grid.ApplyLoadingRowsVisibility();
    }

    // Skeleton row count. When 0 (binding source unset or absent), falls back to
    // DefaultLoadingRowCount. Clamped at MaxLoadingRowCount to avoid pathological
    // renders for large playlists. Album and playlist pages bind this to their
    // ViewModel.TotalTracks, which is seeded by nav prefill and finalised by
    // ApplyDetailAsync — so the skeleton renders the right number of rows
    // before tracks materialise instead of always showing 10.
    public static readonly DependencyProperty LoadingRowCountProperty =
        DependencyProperty.Register(nameof(LoadingRowCount), typeof(int), typeof(TrackDataGrid),
            new PropertyMetadata(0, OnLoadingRowCountChanged));

    public int LoadingRowCount
    {
        get => (int)GetValue(LoadingRowCountProperty);
        set => SetValue(LoadingRowCountProperty, value);
    }

    private static void OnLoadingRowCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackDataGrid grid)
            grid.ApplyLoadingRowCount();
    }

    private void LoadingRowsRepeater_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyLoadingRowCount();
        ApplyLoadingRowsVisibility();
    }

    private void ApplyLoadingRowCount()
    {
        if (LoadingRowsRepeater is null)
            return;

        var requested = LoadingRowCount;
        var effective = requested <= 0
            ? DefaultLoadingRowCount
            : Math.Min(requested, MaxLoadingRowCount);

        // Each row shares the same column geometry, but we materialise a fresh
        // LoadingRowConfig per row so the ItemsRepeater can DataTemplate-bind.
        // Counts are small (≤20), so allocating the list every rebuild is fine.
        var template = BuildLoadingRowConfigTemplate();
        var items = new LoadingRowConfig[effective];
        for (var i = 0; i < effective; i++)
        {
            items[i] = new LoadingRowConfig
            {
                Index = i,
                RowPadding = template.RowPadding,
                RowMargin = template.RowMargin,
                RowMinHeight = template.RowMinHeight,
                ShowRowChrome = UseCardRows || i % 2 != 0,
                ShowIndexCell = template.ShowIndexCell,
                ShowLikeCell = template.ShowLikeCell,
                ShowArtCell = template.ShowArtCell,
                ShowAlbumCell = template.ShowAlbumCell,
                ShowAddedByCell = template.ShowAddedByCell,
                ShowDateAddedCell = template.ShowDateAddedCell,
                ShowPlayCountCell = template.ShowPlayCountCell,
                ShowDurationCell = template.ShowDurationCell,
                ArtColumnWidth = template.ArtColumnWidth,
                AlbumColumnWidth = template.AlbumColumnWidth,
                AddedByColumnWidth = template.AddedByColumnWidth,
                DateAddedColumnWidth = template.DateAddedColumnWidth,
                PlayCountColumnWidth = template.PlayCountColumnWidth,
                DurationColumnWidth = template.DurationColumnWidth,
                ContentMinWidth = template.ContentMinWidth,
                TitleColumnMaxWidth = template.TitleColumnMaxWidth,
                ShowArtistSubtitle = template.ShowArtistSubtitle,
            };
        }
        LoadingRowsRepeater.ItemsSource = items;
    }

    /// <summary>
    /// Derive the skeleton column geometry from the current page state.
    /// Mirrors the real-row column visibility model in
    /// <see cref="ConfigureItemsViewRow"/> so the skeleton row matches what
    /// the real row will paint into the same column slots — no horizontal
    /// shift when content loads.
    /// </summary>
    private LoadingRowConfig BuildLoadingRowConfigTemplate()
    {
        static bool IsVisible(TrackDataGridColumn? col)
            => col is { IsVisible: true };

        static GridLength WidthOrZero(TrackDataGridColumn? col)
            => IsVisible(col) ? col.Length : new GridLength(0);

        static double PixelWidthOrZero(TrackDataGridColumn? col)
        {
            if (!IsVisible(col))
                return 0;

            if (col.Length.IsAbsolute)
                return col.Length.Value;

            return col.MinLength.IsAbsolute ? col.MinLength.Value : 0;
        }

        var indexCol = Columns?.FirstOrDefault(c => c.Key == "Index");
        var likeCol = Columns?.FirstOrDefault(c => c.Key == "Like");
        var artCol = Columns?.FirstOrDefault(c => c.Key == "TrackArt");
        var albumCol = Columns?.FirstOrDefault(c => c.Key == "Album");
        var addedByCol = Columns?.FirstOrDefault(c => c.Key == "AddedBy");
        var dateAddedCol = Columns?.FirstOrDefault(c => c.Key == "DateAdded");
        var playCountCol = Columns?.FirstOrDefault(c => c.Key == "PlayCount");
        var durationCol = Columns?.FirstOrDefault(c => c.Key == "Duration");
        var titleCol = Columns?.FirstOrDefault(c => c.Key == "Track");
        var density = Math.Clamp(_preferredDensity, 0, DensityRowPaddings.Length - 1);
        var artSize = DensityArtSizes[density];
        var showArt = IsVisible(artCol) && artSize > 0;
        var artWidth = showArt ? new GridLength(artSize + 8) : new GridLength(0);

        // AddedBy is special: the column may be present in the set but hidden
        // by the page-level AddedByVisible toggle (non-collab playlists).
        // Mirrored from ConfigureItemsViewRow's ShowAddedByColumn calculation.
        var showAddedBy = AddedByVisible && IsVisible(addedByCol);
        var addedByWidth = showAddedBy
            ? addedByCol.Length
            : new GridLength(0);

        // Title MaxWidth defaults to 640 (playlist) but album pages use null
        // (unbounded) so Plays/Duration pin right. Mirror that here: when the
        // active title column's MaxLength is Auto, treat it as "no cap".
        var titleMax = titleCol?.MaxLength is { GridUnitType: GridUnitType.Pixel } px
            ? px.Value
            : double.PositiveInfinity;
        var titleSkeletonWidth = double.IsFinite(titleMax)
            ? titleMax
            : titleCol?.MinLength is { GridUnitType: GridUnitType.Pixel } minTitle
                ? minTitle.Value
                : 120;

        var contentMinWidth =
            PixelWidthOrZero(indexCol) +
            PixelWidthOrZero(likeCol) +
            (showArt ? artWidth.Value : 0) +
            titleSkeletonWidth +
            PixelWidthOrZero(albumCol) +
            (showAddedBy ? PixelWidthOrZero(addedByCol) : 0) +
            PixelWidthOrZero(dateAddedCol) +
            PixelWidthOrZero(playCountCol) +
            PixelWidthOrZero(durationCol);

        return new LoadingRowConfig
        {
            RowPadding = DensityRowPaddings[density],
            RowMargin = density == 0 ? new Thickness(0) : new Thickness(0, 2, 0, 2),
            RowMinHeight = _preferredRowHeight ?? DensityRowHeights[density],
            ShowIndexCell = IsVisible(indexCol),
            ShowLikeCell = IsVisible(likeCol),
            ShowArtCell = showArt,
            ShowAlbumCell = IsVisible(albumCol),
            ShowAddedByCell = showAddedBy,
            ShowDateAddedCell = IsVisible(dateAddedCol),
            ShowPlayCountCell = IsVisible(playCountCol),
            ShowDurationCell = IsVisible(durationCol),
            ArtColumnWidth = artWidth,
            AlbumColumnWidth = WidthOrZero(albumCol),
            AddedByColumnWidth = addedByWidth,
            DateAddedColumnWidth = WidthOrZero(dateAddedCol),
            PlayCountColumnWidth = WidthOrZero(playCountCol),
            DurationColumnWidth = WidthOrZero(durationCol),
            ContentMinWidth = contentMinWidth,
            TitleColumnMaxWidth = titleMax,
            ShowArtistSubtitle = ResolveShowArtistColumn() && density > 0 && !ShouldShowInlineProgress(),
        };
    }

    public static readonly DependencyProperty IsGroupedProperty =
        DependencyProperty.Register(nameof(IsGrouped), typeof(bool), typeof(TrackDataGrid),
            new PropertyMetadata(false, OnGroupingChanged));

    public bool IsGrouped
    {
        get => (bool)GetValue(IsGroupedProperty);
        set => SetValue(IsGroupedProperty, value);
    }

    public static readonly DependencyProperty GroupKeySelectorProperty =
        DependencyProperty.Register(nameof(GroupKeySelector), typeof(Func<ITrackItem, object?>), typeof(TrackDataGrid),
            new PropertyMetadata(null, OnGroupingChanged));

    public Func<ITrackItem, object?>? GroupKeySelector
    {
        get => (Func<ITrackItem, object?>?)GetValue(GroupKeySelectorProperty);
        set => SetValue(GroupKeySelectorProperty, value);
    }

    public static readonly DependencyProperty GroupHeaderSelectorProperty =
        DependencyProperty.Register(nameof(GroupHeaderSelector), typeof(Func<ITrackItem, object>), typeof(TrackDataGrid),
            new PropertyMetadata(null, OnGroupingChanged));

    public Func<ITrackItem, object>? GroupHeaderSelector
    {
        get => (Func<ITrackItem, object>?)GetValue(GroupHeaderSelectorProperty);
        set => SetValue(GroupHeaderSelectorProperty, value);
    }

    public static readonly DependencyProperty GroupCountFormatterProperty =
        DependencyProperty.Register(nameof(GroupCountFormatter), typeof(Func<int, string>), typeof(TrackDataGrid),
            new PropertyMetadata(null, OnGroupingChanged));

    public Func<int, string>? GroupCountFormatter
    {
        get => (Func<int, string>?)GetValue(GroupCountFormatterProperty);
        set => SetValue(GroupCountFormatterProperty, value);
    }

    public static readonly DependencyProperty GroupHeaderTemplateProperty =
        DependencyProperty.Register(nameof(GroupHeaderTemplate), typeof(DataTemplate), typeof(TrackDataGrid),
            new PropertyMetadata(null, OnGroupHeaderTemplateChanged));

    public DataTemplate? GroupHeaderTemplate
    {
        get => (DataTemplate?)GetValue(GroupHeaderTemplateProperty);
        set => SetValue(GroupHeaderTemplateProperty, value);
    }

    public static readonly DependencyProperty AreStickyGroupHeadersEnabledProperty =
        DependencyProperty.Register(nameof(AreStickyGroupHeadersEnabled), typeof(bool), typeof(TrackDataGrid),
            new PropertyMetadata(false));

    public bool AreStickyGroupHeadersEnabled
    {
        get => (bool)GetValue(AreStickyGroupHeadersEnabledProperty);
        set => SetValue(AreStickyGroupHeadersEnabledProperty, value);
    }

    private static void OnGroupHeaderTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackDataGrid grid)
            grid.ApplyGroupHeaderTemplate();
    }

    public static readonly DependencyProperty PlayCommandProperty =
        DependencyProperty.Register(nameof(PlayCommand), typeof(ICommand), typeof(TrackDataGrid),
            new PropertyMetadata(null));

    /// <summary>Invoked with the clicked <see cref="ITrackItem"/> for single-tap playback.</summary>
    public ICommand? PlayCommand
    {
        get => (ICommand?)GetValue(PlayCommandProperty);
        set => SetValue(PlayCommandProperty, value);
    }

    public static readonly DependencyProperty SelectionChangedCommandProperty =
        DependencyProperty.Register(nameof(SelectionChangedCommand), typeof(ICommand), typeof(TrackDataGrid),
            new PropertyMetadata(null));

    /// <summary>Invoked with the selected row when the internal track list selection changes.</summary>
    public ICommand? SelectionChangedCommand
    {
        get => (ICommand?)GetValue(SelectionChangedCommandProperty);
        set => SetValue(SelectionChangedCommandProperty, value);
    }

    public static readonly DependencyProperty UseCardRowsProperty =
        DependencyProperty.Register(nameof(UseCardRows), typeof(bool), typeof(TrackDataGrid),
            new PropertyMetadata(false, OnUseCardRowsChanged));

    public bool UseCardRows
    {
        get => (bool)GetValue(UseCardRowsProperty);
        set => SetValue(UseCardRowsProperty, value);
    }

    private static void OnUseCardRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackDataGrid grid)
        {
            grid.RefreshRowCardStyles();
            grid.ApplyLoadingRowCount();
        }
    }

    public void ClearSelection()
    {
        RowsItemsView.DeselectAll();
    }

    private void RefreshRowCardStyles()
    {
        foreach (var row in _itemsViewRows.ToArray())
        {
            var index = row.Track is null ? -1 : _visibleRows.IndexOf(row.Track);
            row.SetAlternatingBorder(IsAlternateRow(row.Track, index), UseCardRows);
        }
    }

    private static bool IsAlternateRow(object? row, int itemIndex)
    {
        if (itemIndex >= 0)
            return itemIndex % 2 != 0;

        return row is ITrackItem { OriginalIndex: > 0 } track && track.OriginalIndex % 2 == 0;
    }

    public static readonly DependencyProperty AddedByVisibleProperty =
        DependencyProperty.Register(nameof(AddedByVisible), typeof(bool), typeof(TrackDataGrid),
            new PropertyMetadata(false, OnAddedByVisibleChanged));

    private static void OnAddedByVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (TrackDataGrid)d;
        var newVisible = (bool)e.NewValue;
        System.Diagnostics.Debug.WriteLine($"[addedby-grid] AddedByVisible: {e.OldValue} -> {e.NewValue}");

        // Toggle the AddedBy column's IsVisible so the HEADER also collapses,
        // not just the per-row cells. The column's PropertyChanged flows into
        // OnHeaderColumnChanged → RebuildHeader + RefreshRowShowFlags, so
        // header chrome and row chrome stay in sync. Without this the row
        // cells correctly went to width=0 but the column header label kept
        // rendering at full width.
        var addedByCol = grid.Columns?.FirstOrDefault(c => c.Key == "AddedBy");
        if (addedByCol != null && addedByCol.IsVisible != newVisible)
        {
            addedByCol.IsVisible = newVisible;
            grid.ApplyLoadingRowCount();
            // OnHeaderColumnChanged will run RefreshRowShowFlags as part of its
            // IsVisible-change branch — no need to call it again here.
            return;
        }

        grid.RefreshRowShowFlags();
        grid.ApplyLoadingRowCount();
    }

    /// <summary>
    /// When true, each row's "Added by" cell is shown (gated by per-row content
    /// from <see cref="AddedByFormatter"/>). PlaylistPage flips this to true on
    /// collaborative playlists and false otherwise.
    /// </summary>
    public bool AddedByVisible
    {
        get => (bool)GetValue(AddedByVisibleProperty);
        set => SetValue(AddedByVisibleProperty, value);
    }

    public static readonly DependencyProperty AddedByFormatterProperty =
        DependencyProperty.Register(nameof(AddedByFormatter), typeof(System.Func<object, AddedByCellInfo>), typeof(TrackDataGrid),
            new PropertyMetadata(null));

    /// <summary>
    /// Per-row formatter: returns the display text + avatar URL for the AddedBy
    /// cell. Returning empty <c>Text</c> collapses the row's cell. Mirrors the
    /// existing <see cref="DateAddedFormatter"/> pattern so the grid stays
    /// agnostic of <c>PlaylistTrackDto</c>.
    /// </summary>
    public System.Func<object, AddedByCellInfo>? AddedByFormatter
    {
        get => (System.Func<object, AddedByCellInfo>?)GetValue(AddedByFormatterProperty);
        set => SetValue(AddedByFormatterProperty, value);
    }

    public static readonly DependencyProperty FooterContentProperty =
        DependencyProperty.Register(nameof(FooterContent), typeof(object), typeof(TrackDataGrid),
            new PropertyMetadata(null, OnFooterContentChanged));

    public object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }

    private static void OnFooterContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackDataGrid grid)
            grid.ApplyFooterPlacement();
    }

    public static readonly DependencyProperty FooterPlacementProperty =
        DependencyProperty.Register(nameof(FooterPlacement), typeof(TrackDataGridFooterPlacement), typeof(TrackDataGrid),
            new PropertyMetadata(TrackDataGridFooterPlacement.BelowRows, OnFooterPlacementChanged));

    public TrackDataGridFooterPlacement FooterPlacement
    {
        get => (TrackDataGridFooterPlacement)GetValue(FooterPlacementProperty);
        set => SetValue(FooterPlacementProperty, value);
    }

    private static void OnFooterPlacementChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackDataGrid grid)
            grid.ApplyFooterPlacement();
    }

    private void ApplyFooterPlacement()
    {
        var footer = FooterContent;
        var usePinnedFooter = FooterPlacement == TrackDataGridFooterPlacement.BelowRows;

        if (FooterPresenter is not null)
        {
            FooterPresenter.Content = null;
            FooterPresenter.Visibility = Visibility.Collapsed;
        }

        if (!_disposed)
            ReprojectRows();

        if (FooterPresenter is not null && usePinnedFooter && footer is not null)
        {
            FooterPresenter.Content = footer;
            FooterPresenter.Visibility = Visibility.Visible;
        }
    }

    public static readonly DependencyProperty AllowHorizontalRowScrollProperty =
        DependencyProperty.Register(nameof(AllowHorizontalRowScroll), typeof(bool), typeof(TrackDataGrid),
            new PropertyMetadata(true, OnAllowHorizontalRowScrollChanged));

    /// <summary>
    /// When <c>true</c> (default) the internal ListView permits horizontal scrolling
    /// so wide row sets (many custom columns) remain usable. Set <c>false</c> on
    /// pages that host a horizontally-scrollable widget in <see cref="FooterContent"/>
    /// (e.g. a shelf) — otherwise that widget's content extent propagates upward
    /// and adds a page-level horizontal scrollbar.
    /// </summary>
    public bool AllowHorizontalRowScroll
    {
        get => (bool)GetValue(AllowHorizontalRowScrollProperty);
        set => SetValue(AllowHorizontalRowScrollProperty, value);
    }

    private static void OnAllowHorizontalRowScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackDataGrid self)
            self.ApplyHorizontalRowScroll();
    }

    public static readonly DependencyProperty IsParentScrollingProperty =
        DependencyProperty.Register(nameof(IsParentScrolling), typeof(bool), typeof(TrackDataGrid),
            new PropertyMetadata(false, OnIsParentScrollingChanged));

    /// <summary>
    /// When <c>true</c>, the internal ListView / ItemsView vertical scroll is
    /// disabled so the grid renders all its rows at natural height and the
    /// containing page's scroll viewer drives the whole layout. Default
    /// <c>false</c> keeps normal in-grid vertical scrolling.
    ///
    /// <para>
    /// Tradeoff: in this mode all rows render up-front (no virtualization),
    /// because the inner panel measures against the parent's infinite vertical
    /// extent rather than a constrained viewport. Acceptable for typical album
    /// sizes (≤30 tracks). Revisit if a 100+-track surface needs it.
    /// </para>
    /// </summary>
    public bool IsParentScrolling
    {
        get => (bool)GetValue(IsParentScrollingProperty);
        set => SetValue(IsParentScrollingProperty, value);
    }

    private static void OnIsParentScrollingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackDataGrid self)
            self.ApplyVerticalRowScroll();
    }

    private void ApplyVerticalRowScroll()
    {
        if (RowsItemsView?.ScrollView is not { } itemsScrollView) return;

        if (IsParentScrolling)
        {
            itemsScrollView.VerticalScrollMode = ScrollingScrollMode.Disabled;
            itemsScrollView.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
        }
        else
        {
            itemsScrollView.VerticalScrollMode = ScrollingScrollMode.Auto;
            itemsScrollView.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
        }
    }

    public void ScrollRowsToTop()
    {
        RowsItemsView?.ScrollView?.ScrollTo(
            0, 0,
            new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
    }

    public static readonly DependencyProperty DateAddedFormatterProperty =
        DependencyProperty.Register(nameof(DateAddedFormatter), typeof(Func<object, string>), typeof(TrackDataGrid),
            new PropertyMetadata(null));

    /// <summary>
    /// Per-row formatter for the Date-Added column. Consumers (PlaylistPage etc.) set
    /// this so TrackItem's <c>DateAddedText</c> renders a friendly string — TrackItem
    /// doesn't know how to reach <c>PlaylistTrackDto.AddedAtFormatted</c> on its own.
    /// Mirrors <c>TrackListView.DateAddedFormatter</c>.
    /// </summary>
    public Func<object, string>? DateAddedFormatter
    {
        get => (Func<object, string>?)GetValue(DateAddedFormatterProperty);
        set => SetValue(DateAddedFormatterProperty, value);
    }

    public static readonly DependencyProperty PlayCountFormatterProperty =
        DependencyProperty.Register(nameof(PlayCountFormatter), typeof(Func<object, string>), typeof(TrackDataGrid),
            new PropertyMetadata(null));

    /// <summary>
    /// Per-row formatter for the Play-Count column. AlbumPage sets this to reach
    /// <c>AlbumTrackDto.PlayCountFormatted</c>; TrackItem doesn't know that type.
    /// Same pattern as <see cref="DateAddedFormatter"/>.
    /// </summary>
    public Func<object, string>? PlayCountFormatter
    {
        get => (Func<object, string>?)GetValue(PlayCountFormatterProperty);
        set => SetValue(PlayCountFormatterProperty, value);
    }

    public static readonly DependencyProperty PopularityBadgeSelectorProperty =
        DependencyProperty.Register(nameof(PopularityBadgeSelector), typeof(Func<object, bool>), typeof(TrackDataGrid),
            new PropertyMetadata(null, OnPopularityBadgeSelectorChanged));

    /// <summary>
    /// Optional per-row selector for a small leading "popular" badge in
    /// <see cref="Track.TrackItem"/> row mode. AlbumPage uses play-count ranking;
    /// other pages can opt in with their own scoring without TrackItem knowing
    /// page-specific DTOs.
    /// </summary>
    public Func<object, bool>? PopularityBadgeSelector
    {
        get => (Func<object, bool>?)GetValue(PopularityBadgeSelectorProperty);
        set => SetValue(PopularityBadgeSelectorProperty, value);
    }

    private static void OnPopularityBadgeSelectorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackDataGrid grid)
            grid.RefreshPopularityBadges();
    }

    public static readonly DependencyProperty ShowToolbarProperty =
        DependencyProperty.Register(nameof(ShowToolbar), typeof(bool), typeof(TrackDataGrid),
            new PropertyMetadata(true, OnShowToolbarChanged));

    /// <summary>
    /// When <c>true</c> (default), the grid renders the filter/selection/sort/view/details
    /// toolbar row above the header. Album pages flip this off because the toolbar
    /// controls are redundant there — play/shuffle live in the hero, selection is
    /// less relevant for a single-album track list.
    /// </summary>
    public bool ShowToolbar
    {
        get => (bool)GetValue(ShowToolbarProperty);
        set => SetValue(ShowToolbarProperty, value);
    }

    private static void OnShowToolbarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TrackDataGrid grid) return;
        var visible = (bool)e.NewValue;
        grid.ToolbarHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            // Also ensure the filter row stays collapsed when the toolbar is hidden,
            // since its toggle lives in the toolbar.
            grid.FilterToggle.IsChecked = false;
            grid.FilterHost.Visibility = Visibility.Collapsed;
        }
    }

    public static readonly DependencyProperty ToolbarLeftContentProperty =
        DependencyProperty.Register(nameof(ToolbarLeftContent), typeof(object), typeof(TrackDataGrid),
            new PropertyMetadata(null));

    /// <summary>
    /// Content rendered on the left side of the toolbar row, opposite the built-in
    /// filter/selection/sort/view/details icons. Pages slot page-specific affordances
    /// here (e.g. Play/Shuffle + stats on Liked Songs). Unset → empty left column, and
    /// the right-aligned icon cluster occupies the full row as before.
    /// </summary>
    public object? ToolbarLeftContent
    {
        get => GetValue(ToolbarLeftContentProperty);
        set => SetValue(ToolbarLeftContentProperty, value);
    }

    public static readonly DependencyProperty FilterBarContentProperty =
        DependencyProperty.Register(nameof(FilterBarContent), typeof(object), typeof(TrackDataGrid),
            new PropertyMetadata(null));

    /// <summary>
    /// Optional page-specific controls rendered inside the expanded filter row,
    /// beside the text filter box.
    /// </summary>
    public object? FilterBarContent
    {
        get => GetValue(FilterBarContentProperty);
        set => SetValue(FilterBarContentProperty, value);
    }

    // ------------------------------------------------------------- source / projection

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TrackDataGrid grid) return;
        if (grid._subscribedSource is not null)
            grid._subscribedSource.CollectionChanged -= grid.OnSourceCollectionChanged;
        grid._subscribedSource = null;
        if (grid._disposed || App.IsHostShuttingDown)
            return;

        if (e.NewValue is INotifyCollectionChanged notifying)
        {
            grid._subscribedSource = notifying;
            notifying.CollectionChanged += grid.OnSourceCollectionChanged;
        }
        grid.RefreshSnapshot(e.NewValue as IEnumerable);
        grid.ReprojectRows();
    }

    private static void OnGroupingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackDataGrid grid)
            grid.ReprojectRows();
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed || App.IsHostShuttingDown)
            return;

        RefreshSnapshot(sender as IEnumerable ?? _subscribedSource as IEnumerable);
        ReprojectRows();
    }

    private void RefreshSnapshot(IEnumerable? source)
    {
        _sourceSnapshot = source is IEnumerable<ITrackItem> typed
            ? typed.ToArray()
            : source?.Cast<ITrackItem>().ToArray() ?? Array.Empty<ITrackItem>();
    }

    // -------------------------------------------------------- filter + sort projection

    private void ReprojectRows()
    {
        using var _ = UiOperationProfiler.Instance?.Profile("trackGrid.reprojectRows");
        var selectedKeys = CaptureSelectedTrackKeys();
        var source = _sourceSnapshot;
        if (source.Count == 0)
        {
            if (_visibleRows.Count > 0)
                _visibleRows.Clear();
            ApplyLoadingRowsVisibility();
            QueueProjectionDiagnostic(0, 0);
            return;
        }

        var sortColumn = Columns?.SortColumn;
        var hasFilter = !string.IsNullOrWhiteSpace(_filterText);
        var hasSort = sortColumn is { SortKey: not null, SortDirection: not null };
        List<object> rows;
        if (!hasFilter && !hasSort)
        {
            rows = BuildFlatRowsWithHeaders(source);
        }
        else
        {
            IEnumerable<ITrackItem> pipeline = source;

            if (hasFilter)
            {
                var q = _filterText;
                pipeline = pipeline.Where(t =>
                    t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    t.ArtistName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    t.AlbumName.Contains(q, StringComparison.OrdinalIgnoreCase));
            }

            if (sortColumn is { SortKey: { } sortKey, SortDirection: { } direction })
            {
                pipeline = direction == TrackDataGridSortDirection.Ascending
                    ? pipeline.OrderBy(t => SortValue(t, sortKey), Comparer<object?>.Create(CompareObjects))
                    : pipeline.OrderByDescending(t => SortValue(t, sortKey), Comparer<object?>.Create(CompareObjects));
            }

            rows = BuildFlatRowsWithHeaders(pipeline.ToList());
        }

        AppendFooterRow(rows);
        _visibleRows.ReplaceWith(rows);
        RestoreSelectionByKeys(selectedKeys);
        ApplyLoadingRowsVisibility();
        QueueProjectionDiagnostic(source.Count, rows.Count);
    }

    private void QueueProjectionDiagnostic(int sourceCount, int visibleCount)
    {
        var generation = ++_projectionDiagnosticGeneration;
        if (DispatcherQueue is null)
            return;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_disposed || generation != _projectionDiagnosticGeneration)
                return;

            System.Diagnostics.Debug.WriteLine(
                $"[track-grid] page={PageKey} source={sourceCount} visibleRows={visibleCount} realizedRows={_itemsViewRows.Count} parentScroll={IsParentScrolling}");
        });
    }

    private void AppendFooterRow(List<object> rows)
    {
        if (FooterPlacement != TrackDataGridFooterPlacement.InRowsScroll || FooterContent is null)
            return;

        rows.Add(new TrackDataGridFooterRow { Content = FooterContent });
    }

    private void ApplyLoadingRowsVisibility()
    {
        if (LoadingRowsRepeater is null)
            return;

        LoadingRowsRepeater.Visibility = IsLoading && _sourceSnapshot.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Push the consumer-supplied <c>GroupHeaderTemplate</c> into the
    /// <see cref="ItemTemplateSelector"/> so the ItemsView renders it for any
    /// <see cref="TrackDataGridGroupRow"/> marker the projection produces.
    /// Called from the ctor (after <c>InitializeComponent</c>) and from the
    /// <c>GroupHeaderTemplate</c> DP change callback.
    /// </summary>
    private void ApplyGroupHeaderTemplate()
    {
        if (ItemTemplateSelector is null) return;
        ItemTemplateSelector.GroupHeaderTemplate = GroupHeaderTemplate;
    }

    /// <summary>
    /// Produces the flat row list ItemsView consumes. When the grid is grouped
    /// (<see cref="IsGrouped"/>=true with a <see cref="GroupKeySelector"/>),
    /// emits a <see cref="TrackDataGridGroupRow"/> marker immediately before
    /// each bucket's first track. Markers are non-selectable in the XAML and
    /// filtered out of every <c>ITrackItem</c>-shaped helper, so sort /
    /// selection / drag-reorder / context-menu code is transparent to them.
    /// </summary>
    private List<object> BuildFlatRowsWithHeaders(IReadOnlyList<ITrackItem> tracks)
    {
        if (!IsGrouped || GroupKeySelector is null || tracks.Count == 0)
            return new List<object>(tracks);

        var keySelector = GroupKeySelector;
        var headerSelector = GroupHeaderSelector;
        var countFormatter = GroupCountFormatter;

        var flat = new List<object>(tracks.Count + 4);
        string? currentKey = null;
        var bucket = new List<ITrackItem>(tracks.Count);
        ITrackItem? bucketFirst = null;

        void Flush()
        {
            if (bucketFirst is null || bucket.Count == 0) return;
            var header = headerSelector?.Invoke(bucketFirst) ?? currentKey ?? string.Empty;
            var countText = countFormatter?.Invoke(bucket.Count)
                ?? (bucket.Count == 1 ? "1 item" : $"{bucket.Count:N0} items");
            // Header row marker; the ItemTemplateSelector routes it to
            // GroupHeaderTemplate.
            flat.Add(new TrackDataGridGroupRow
            {
                Header = header,
                Count = bucket.Count,
                CountText = countText,
            });
            flat.AddRange(bucket);
            bucket.Clear();
            bucketFirst = null;
        }

        for (var i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            var key = keySelector(t)?.ToString() ?? string.Empty;
            if (!string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                currentKey = key;
            }
            bucket.Add(t);
            bucketFirst ??= t;
        }
        Flush();
        return flat;
    }

    // ------------------------------------------------------------- toolbar handlers

    private void FilterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var isOn = FilterToggle.IsChecked == true;
        FilterHost.Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;
        if (isOn)
        {
            FilterBox.Focus(FocusState.Programmatic);
        }
        else
        {
            FilterBox.Text = string.Empty;
            _filterText = string.Empty;
            ReprojectRows();
        }
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterText = FilterBox.Text;
        ReprojectRows();
    }

    /// <summary>
    /// Clears the filter text + closes the filter bar. Exposed for consumers that
    /// want to reset view-state when their bound data-context changes — e.g. a
    /// cached page navigating to a different entity where the previous query no
    /// longer makes sense.
    /// </summary>
    public void ResetFilter()
    {
        if (_filterText.Length == 0
            && FilterBox.Text.Length == 0
            && FilterToggle.IsChecked != true)
            return;

        _filterText = string.Empty;
        FilterBox.Text = string.Empty;
        FilterToggle.IsChecked = false;
        FilterHost.Visibility = Visibility.Collapsed;
        ReprojectRows();
    }

    private void DensitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var stop = (int)Math.Clamp(Math.Round(e.NewValue), 0, DensityRowHeights.Length - 1);
        var height = DensityRowHeights[stop];
        var outerMargin = stop == 0 ? new Thickness(0) : new Thickness(0, 2, 0, 2);

        // Push RowDensity into each realized TrackItem so padding /
        // album-art / subline adjust without a re-template pass.
        foreach (var row in _itemsViewRows.ToArray())
        {
            row.RowDensity = stop;
            ApplyItemsViewContainerDensity(row);
        }

        // Preserve for future containers (virtualization materializes on demand).
        _preferredRowHeight = height;
        _preferredDensity = stop;
        ApplyLoadingRowCount();
    }

    private double? _preferredRowHeight;
    private int _preferredDensity = 2;

    private void DetailsToggle_Click(object sender, RoutedEventArgs e)
    {
        var shell = Ioc.Default.GetService<ShellViewModel>();
        if (shell is null) return;

        if (DetailsToggle.IsChecked == true)
        {
            var track = SelectedRowItem() as ITrackItem ?? _visibleRows.OfType<ITrackItem>().FirstOrDefault();
            if (track is null)
            {
                DetailsToggle.IsChecked = false;
                return;
            }
            shell.ShowTrackDetails(track);
        }
        else if (shell.RightPanelMode == RightPanelMode.TrackDetails)
        {
            shell.IsRightPanelOpen = false;
            shell.SelectedTrackForDetails = null;
        }
    }

    public void SelectAllRows()
    {
        RowsItemsView.SelectAll();
        // Group-header markers (multi-disc albums) are selectable ItemsView
        // entries but aren't tracks — drop them so the selection, the floating
        // bar's count, and any bulk action stay tracks-only.
        for (var i = 0; i < _visibleRows.Count; i++)
        {
            if (_visibleRows[i] is not ITrackItem)
                RowsItemsView.Deselect(i);
        }
        SyncItemsViewRowSelectionState();
    }

    private void RowsItemsView_SelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        SyncItemsViewRowSelectionState();
        // Keep the floating selection bar's count / visibility in sync — fires
        // for user changes AND programmatic restore (RestoreSelectionByKeys).
        RaiseSelectionModeStateChanged();
        if (_restoringSelection)
            return;
        if (DeselectSelectedNonTrackRows())
            return;
        if (_isSelectionMode)
            return;

        var selected = SelectedRowItem();
        if (selected is not ITrackItem track)
            return;

        RowSelected?.Invoke(this, track);

        if (SelectionChangedCommand?.CanExecute(track) == true)
            SelectionChangedCommand.Execute(track);
    }

    private bool DeselectSelectedNonTrackRows()
    {
        var selectedItems = RowsItemsView.SelectedItems.Cast<object>().ToArray();
        if (selectedItems.Length == 0)
            return false;

        var removedAny = false;
        var hasSelectedTrack = false;

        foreach (var item in selectedItems)
        {
            if (item is ITrackItem)
            {
                hasSelectedTrack = true;
                continue;
            }

            var index = _visibleRows.IndexOf(item);
            if (index < 0)
                continue;

            RowsItemsView.Deselect(index);
            removedAny = true;
        }

        return removedAny && !hasSelectedTrack;
    }

    private object? SelectedRowItem() => RowsItemsView.SelectedItem;

    private HashSet<string> CaptureSelectedTrackKeys()
    {
        return RowsItemsView.SelectedItems
            .OfType<ITrackItem>()
            .Select(TrackSelectionKey)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void RestoreSelectionByKeys(IReadOnlySet<string> selectedKeys)
    {
        if (selectedKeys.Count == 0 || _visibleRows.Count == 0)
            return;

        _restoringSelection = true;
        try
        {
            RowsItemsView.DeselectAll();
            for (var i = 0; i < _visibleRows.Count; i++)
            {
                if (_visibleRows[i] is not ITrackItem track) continue;
                var key = TrackSelectionKey(track);
                if (!string.IsNullOrEmpty(key) && selectedKeys.Contains(key))
                    RowsItemsView.Select(i);
            }
        }
        finally
        {
            _restoringSelection = false;
        }

        SyncItemsViewRowSelectionState();
    }

    private static string? TrackSelectionKey(ITrackItem item)
        => !string.IsNullOrWhiteSpace(item.Uri)
            ? item.Uri
            : !string.IsNullOrWhiteSpace(item.Id)
                ? item.Id
                : null;

    private void SyncItemsViewRowSelectionState()
    {
        var selected = new HashSet<object>(RowsItemsView.SelectedItems.Cast<object>());
        foreach (var row in _itemsViewRows.ToArray())
            row.IsSelected = row.Track is not null && selected.Contains(row.Track);
    }

    // Tap / DoubleTap are handled inside TrackItem (respecting AppSettings.TrackClickBehavior)
    // and native Extended-mode selection — nothing to wire at this level. Enter/Space on a
    // keyboard-selected row still plays via the handler below, matching TrackListView.

    private void OnRowsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Esc leaves selection mode (and clears the selection).
        if (e.Key == VirtualKey.Escape && _isSelectionMode)
        {
            ExitSelectionMode();
            e.Handled = true;
            return;
        }

        // Ctrl+A is handled by OnProcessKeyboardAccelerators so it works
        // regardless of which sub-element holds focus without WinUI exposing a
        // hover tooltip for a real KeyboardAccelerator.

        if ((e.Key == VirtualKey.Enter || e.Key == VirtualKey.Space) &&
            SelectedRowItem() is ITrackItem track)
        {
            PlayCommand?.Execute(track);
            e.Handled = true;
        }
    }

    protected override void OnProcessKeyboardAccelerators(ProcessKeyboardAcceleratorEventArgs args)
    {
        if (args.Modifiers == VirtualKeyModifiers.Control
            && args.Key == VirtualKey.A
            && TryHandleSelectAllShortcut())
        {
            args.Handled = true;
            return;
        }

        base.OnProcessKeyboardAccelerators(args);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _isSelectionMode = false;

        if (_rowsItemsViewScrollView is not null)
        {
            _rowsItemsViewScrollView.ViewChanged -= RowsItemsViewScrollView_ViewChanged;
            _rowsItemsViewScrollView = null;
            RowsScrollViewChanged?.Invoke(this, EventArgs.Empty);
        }

        UnwireRowContextMenuHandlers();

        RowsItemsView.SelectionChanged -= RowsItemsView_SelectionChanged;
        RowsItemsView.Loaded -= RowsItemsView_Loaded;
        RowsItemsView.Unloaded -= RowsItemsView_Unloaded;

        _itemsViewRows.Clear();

        // Tear down the centralized LazyTrackItem subscription bus.
        _visibleRows.CollectionChanged -= OnVisibleRowsCollectionChanged;
        if (_lazyItemHandler is not null)
        {
            foreach (var lazy in _subscribedLazyItems)
                lazy.PropertyChanged -= _lazyItemHandler;
        }
        _subscribedLazyItems.Clear();
        _rowByLazyItem.Clear();
        _lazyItemByRow.Clear();
        _lazyItemHandler = null;

        if (_subscribedSource is not null)
            _subscribedSource.CollectionChanged -= OnSourceCollectionChanged;
        _subscribedSource = null;

        if (Columns is not null)
            UnsubscribeColumns(Columns);
        foreach (var col in _headerSlots.Keys)
            col.PropertyChanged -= OnHeaderColumnChanged;
        _headerSlots.Clear();

        if (SortBySubItem is not null)
        {
            foreach (var item in SortBySubItem.Items.OfType<RadioMenuFlyoutItem>())
                item.Click -= SortByItem_Click;
            SortBySubItem.Items.Clear();
        }

        HeaderHost.Children.Clear();
        HeaderHost.ColumnDefinitions.Clear();
        FooterPresenter.Content = null;
        RowsItemsView.DeselectAll();
        RowsItemsView.ItemsSource = null;
        if (LoadingRowsRepeater != null)
            LoadingRowsRepeater.ItemsSource = null;
        _visibleRows.Clear();
        _sourceSnapshot = Array.Empty<ITrackItem>();

        ItemsSource = null;
        PlayCommand = null;
        SelectionChangedCommand = null;
        DateAddedFormatter = null;
        PlayCountFormatter = null;
        PopularityBadgeSelector = null;
        AddedByFormatter = null;
        GroupKeySelector = null;
        GroupHeaderSelector = null;
        GroupCountFormatter = null;
        GroupHeaderTemplate = null;
        FooterContent = null;
        ToolbarLeftContent = null;
        FilterBarContent = null;
        DataContext = null;
    }

    // ── Drag & drop ──

    /// <summary>
    /// Spotify URI of the context this grid represents (e.g. <c>spotify:playlist:xxx</c>).
    /// When a drop on this grid carries a <c>TrackDragPayload</c> whose
    /// <c>SourceContextUri</c> equals <see cref="ContextUri"/>, the drop is
    /// interpreted as an intra-list reorder and surfaced via
    /// <see cref="TracksReorderRequested"/>.
    /// </summary>
    public string? ContextUri
    {
        get => (string?)GetValue(ContextUriProperty);
        set => SetValue(ContextUriProperty, value);
    }

    public static readonly DependencyProperty ContextUriProperty =
        DependencyProperty.Register(nameof(ContextUri), typeof(string), typeof(TrackDataGrid),
            new PropertyMetadata(null));

    /// <summary>
    /// When true, this grid accepts intra-list reorder drops. Owners-only
    /// playlists bind this to <c>true</c>; everything else stays false.
    /// </summary>
    public bool CanReorder
    {
        get => (bool)GetValue(CanReorderProperty);
        set => SetValue(CanReorderProperty, value);
    }

    public static readonly DependencyProperty CanReorderProperty =
        DependencyProperty.Register(nameof(CanReorder), typeof(bool), typeof(TrackDataGrid),
            new PropertyMetadata(false));

    /// <summary>
    /// Raised when the user drops a contiguous block back into this same grid.
    /// Carries (sourceStartIndex, length, targetIndex) in <c>_visibleRows</c>
    /// coordinates; the page persists the new order through its ViewModel.
    /// </summary>
    public event Action<int, int, int>? TracksReorderRequested;

    private DragStateService? _dragState;

    private DragStateService? ResolveDragState() =>
        _dragState ??= Ioc.Default.GetService<DragStateService>();

    // RowsItemsView drag-source wiring lives on the inner TrackItem (per row)
    // via ManualDragAttachment in RowsItemsViewTrackItem_Loaded. ItemContainer's
    // CanDrag pipeline gets swallowed by its selection pointer handling, so the
    // framework-driven DragStarting event was never firing for that path.

    private ReorderDropIndicator? _dropIndicator;
    private ReorderDropIndicator DropIndicator => _dropIndicator ??= new ReorderDropIndicator(DropIndicatorOverlay);

    private void RowsItemsViewHost_DragOver(object sender, DragEventArgs e)
    {
        if (!CanReorder) { _dropIndicator?.Hide(); return; }
        if (ResolveDragState()?.CurrentPayload is not TrackDragPayload p
            || string.IsNullOrEmpty(ContextUri)
            || !string.Equals(p.SourceContextUri, ContextUri, StringComparison.Ordinal))
        {
            _dropIndicator?.Hide();
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = p.ItemCount == 1 ? "Move 1 track" : $"Move {p.ItemCount} tracks";

        var rows = BuildRowBounds();
        var slot = ReorderDropIndicator.ResolveSlotIndex(
            e.GetPosition(RowsHostCell).Y, rows, _visibleRows.Count);
        DropIndicator.Show(slot, rows, _visibleRows.Count, RowsHostCell.ActualWidth);
    }

    private void RowsItemsViewHost_DragLeave(object sender, DragEventArgs e) => _dropIndicator?.Hide();

    private void RowsItemsViewHost_Drop(object sender, DragEventArgs e)
    {
        _dropIndicator?.Hide();
        if (!TryReadIntraListReorder(e, out var fromIndex, out var length))
            return;
        var rows = BuildRowBounds();
        var slot = ReorderDropIndicator.ResolveSlotIndex(
            e.GetPosition(RowsHostCell).Y, rows, _visibleRows.Count);
        if (slot < 0) return;
        TracksReorderRequested?.Invoke(fromIndex, length, slot);
    }

    /// <summary>
    /// Walks the realized rows into <see cref="ReorderDropIndicator.RowBounds"/>
    /// in <c>RowsHostCell</c> coordinate space. ItemsView's row containers can't
    /// be reached by a downward VisualTreeHelper walk, so the tracked-rows set
    /// (<see cref="_itemsViewRows"/>) is the source; the bounds anchor is each
    /// row's ItemContainer (full row incl. its margin) when reachable.
    /// </summary>
    private List<ReorderDropIndicator.RowBounds> BuildRowBounds()
    {
        var rows = new List<ReorderDropIndicator.RowBounds>(_itemsViewRows.Count);
        foreach (var row in _itemsViewRows)
        {
            var item = row.Track;
            if (item is null) continue;
            var modelIndex = _visibleRows.IndexOf(item);
            if (modelIndex < 0) continue;

            FrameworkElement boundsSource = FindParent<ItemContainer>(row) ?? (FrameworkElement)row;
            double top;
            try
            {
                top = boundsSource.TransformToVisual(RowsHostCell)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            }
            catch { continue; }

            rows.Add(new ReorderDropIndicator.RowBounds(boundsSource, top, boundsSource.ActualHeight, modelIndex));
        }
        return rows;
    }

    private void WriteTrackPayload(DataPackage data, IReadOnlyList<ITrackItem> tracks, int sourceStartIndex)
    {
        var uris = tracks.Select(t => t.Id).ToArray();
        var payload = new TrackDragPayload(
            uris,
            sourceContextUri: ContextUri,
            sourceStartIndex: sourceStartIndex >= 0 ? sourceStartIndex : null);
        DragPackageWriter.Write(data, payload);
        ResolveDragState()?.StartDrag(payload);
    }

    private bool TryReadIntraListReorder(DragEventArgs e, out int fromIndex, out int length)
    {
        fromIndex = -1;
        length = 0;
        if (!CanReorder) return false;
        if (ResolveDragState()?.CurrentPayload is not TrackDragPayload p) return false;
        if (string.IsNullOrEmpty(ContextUri)
            || !string.Equals(p.SourceContextUri, ContextUri, StringComparison.Ordinal))
            return false;
        if (p.SourceStartIndex is not int from || p.ItemCount <= 0) return false;
        fromIndex = from;
        length = p.ItemCount;
        return true;
    }

}
