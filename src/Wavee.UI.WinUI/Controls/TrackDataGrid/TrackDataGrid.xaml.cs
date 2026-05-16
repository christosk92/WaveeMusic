using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using Windows.System;
using WaveeGridSplitter = Wavee.UI.WinUI.Controls.GridSplitter;

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
    private readonly ObservableCollection<ITrackItem> _visibleRows = new();
    private readonly ObservableCollection<TrackDataGridGroup> _visibleGroups = new();
    private readonly CollectionViewSource _groupedRowsViewSource = new() { IsSourceGrouped = true };
    private IReadOnlyList<ITrackItem> _sourceSnapshot = Array.Empty<ITrackItem>();
    private INotifyCollectionChanged? _subscribedSource;
    private ISettingsService? _settingsService;
    private string _filterText = string.Empty;
    private bool _disposed;
    private bool _restoringSelection;
    private readonly HashSet<Track.TrackItem> _itemsViewRows = new();
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

    // Size-slider stops (matches the XS/S/M/L/XL segmentation in the view flyout).
    // MinHeight floor per row; content (padding + art + text) may still push the
    // row taller on larger steps, and that's intentional.
    private static readonly double[] DensityRowHeights = { 32d, 40d, 48d, 60d, 76d };

    public TrackDataGrid()
    {
        InitializeComponent();
        _groupedRowsViewSource.ItemsPath = new PropertyPath(nameof(TrackDataGridGroup.Items));
        ApplyGroupHeaderTemplate();
        RowsList.ItemsSource = _visibleRows;
        RowsItemsView.ItemsSource = _visibleRows;
        ApplyLoadingRowCount();
        // Centralized subscription bus (see field comment).
        _lazyItemHandler = OnAnyLazyItemPropertyChanged;
        _visibleRows.CollectionChanged += OnVisibleRowsCollectionChanged;
        RowsList.ContainerContentChanging += RowsList_ContainerContentChanging;
        RowsList.SelectionChanged += RowsList_SelectionChanged;
        RowsList.Loaded += RowsList_Loaded;
        RowsList.Unloaded += RowsList_Unloaded;
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
        ApplyRowsPresenterMode();
    }

    // Sticky-header sync: the HeaderHost Grid lives outside the ListView's
    // internal ScrollViewer so it stays vertically pinned at the top. When the
    // user scrolls the rows horizontally (via the ListView's own Scroll*
    // attached props configured in XAML), we translate the header to match the
    // ListView's HorizontalOffset. Pattern documented by Microsoft as the safe
    // alternative to wrapping ListView in an outer ScrollViewer, which has
    // known virtualization + input-routing bugs in WinUI 3 (microsoft-ui-xaml
    // issue #10172).
    private ScrollViewer? _rowsListScrollViewerWinUi;
    private ScrollView? _rowsItemsViewScrollView;

    private void RowsList_Loaded(object sender, RoutedEventArgs e)
    {
        HookRowsListScrollViewer();
        ApplyHorizontalRowScroll();
        ApplyVerticalRowScroll();
    }

    private void RowsList_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_rowsListScrollViewerWinUi is not null)
        {
            _rowsListScrollViewerWinUi.ViewChanged -= RowsListScrollViewer_ViewChanged;
            _rowsListScrollViewerWinUi = null;
        }
    }

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
        }
    }

    private void ApplyHorizontalRowScroll()
    {
        if (RowsList is null) return;
        if (AllowHorizontalRowScroll)
        {
            ScrollViewer.SetHorizontalScrollMode(RowsList, ScrollMode.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(RowsList, ScrollBarVisibility.Auto);
            if (RowsItemsView.ScrollView is { } itemsScrollView)
            {
                itemsScrollView.HorizontalScrollMode = ScrollingScrollMode.Auto;
                itemsScrollView.HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Auto;
            }
        }
        else
        {
            ScrollViewer.SetHorizontalScrollMode(RowsList, ScrollMode.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(RowsList, ScrollBarVisibility.Disabled);
            if (RowsItemsView.ScrollView is { } itemsScrollView)
            {
                itemsScrollView.HorizontalScrollMode = ScrollingScrollMode.Disabled;
                itemsScrollView.HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
            }
        }
    }

    private void HookRowsListScrollViewer()
    {
        if (_rowsListScrollViewerWinUi is not null) return;

        // Walk the ListView's visual tree to find its template ScrollViewer.
        // The template exposes it as part name "ScrollViewer" on ListViewBase
        // in WinUI 3.
        var sv = FindDescendant<ScrollViewer>(RowsList);
        if (sv is null) return;

        _rowsListScrollViewerWinUi = sv;
        sv.ViewChanged += RowsListScrollViewer_ViewChanged;
        // Apply initial offset (in case the ListView scrolled before Loaded fired).
        HeaderScrollTransform.X = -sv.HorizontalOffset;
    }

    private void RowsListScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is ScrollViewer sv)
            HeaderScrollTransform.X = -sv.HorizontalOffset;
    }

    private void HookRowsItemsViewScrollView()
    {
        if (_rowsItemsViewScrollView is not null) return;

        var scrollView = RowsItemsView.ScrollView;
        if (scrollView is null) return;

        _rowsItemsViewScrollView = scrollView;
        scrollView.ViewChanged += RowsItemsViewScrollView_ViewChanged;
        HeaderScrollTransform.X = -scrollView.HorizontalOffset;
    }

    private void RowsItemsViewScrollView_ViewChanged(ScrollView sender, object args)
    {
        HeaderScrollTransform.X = -sender.HorizontalOffset;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            var found = FindDescendant<T>(child);
            if (found is not null) return found;
        }
        return null;
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

    /// <summary>
    /// Per-row setup, split into phases so the first frame only does what's needed
    /// to show the row at all; non-critical updates (column widths, date formatting)
    /// happen in later phases scheduled via <see cref="ContainerContentChangingEventArgs.RegisterUpdateCallback"/>.
    /// Without this, 200-track playlists blocked the UI thread for hundreds of ms on navigation.
    /// TrackItem owns hover, selection, heart toggle, tap (TrackClickBehavior), context menu.
    /// </summary>
    private void RowsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container) return;
        if (args.InRecycleQueue)
        {
            if (container.ContentTemplateRoot is Track.TrackItem recycledRow)
                UnregisterRow(recycledRow);
            return;
        }
        if (container.ContentTemplateRoot is not Track.TrackItem item) return;

        args.Handled = true;

        switch (args.Phase)
        {
            case 0:
                // Essential + cheap: play command + alternating zebra + density.
                if (_preferredRowHeight is double h) container.MinHeight = h;
                container.Margin = _preferredDensity == 0 ? new Thickness(0) : new Thickness(0, 2, 0, 2);
                item.RowDensity = _preferredDensity;
                item.PlayCommand = PlayCommand;
                item.ShowPopularityBadge = ShouldShowPopularityBadge(args.Item);
                item.SetAlternatingBorder(IsAlternateRow(args.Item, args.ItemIndex), UseCardRows);
                item.IsSelected = container.IsSelected;
                item.IsLoading = args.Item is ITrackItem { IsLoaded: false };
                RegisterRowForLazyItem(item, args.Item as LazyTrackItem);
                WireContainerToggleHandlers(container);
                args.RegisterUpdateCallback(RowsList_ContainerContentChanging);
                break;

            case 1:
                // Column show/hide flags — batched so the setters trigger one layout pass.
                item.BeginBatchUpdate();
                item.ShowAlbumArt        = ColumnVisible("TrackArt");
                item.ShowArtistColumn    = ResolveShowArtistColumn();
                item.ShowAlbumColumn     = ColumnVisible("Album");
                item.ShowAddedByColumn   = AddedByVisible && ColumnVisible("AddedBy");
                item.ShowDateAdded       = ColumnVisible("DateAdded");
                item.ShowPlayCount       = ColumnVisible("PlayCount");
                item.ShowProgress        = ShouldShowInlineProgress();
                item.EndBatchUpdate();
                args.RegisterUpdateCallback(RowsList_ContainerContentChanging);
                break;

            case 2:
                // Resizable column widths — also batched.
                item.BeginBatchUpdate();
                PushWidthsToRow(item);
                item.EndBatchUpdate();
                args.RegisterUpdateCallback(RowsList_ContainerContentChanging);
                break;

            case 3:
                // Final trim: formatted strings are consumer-provided; do them last.
                ApplyFormattedCells(item, args.Item);
                break;
        }
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

    private void RowsItemsViewTrackItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Track.TrackItem row)
            return;

        _itemsViewRows.Add(row);
        row.TrackChanged -= RowsItemsViewTrackItem_TrackChanged;
        row.TrackChanged += RowsItemsViewTrackItem_TrackChanged;
        var sourceItem = row.Track;
        var index = sourceItem is null ? -1 : _visibleRows.IndexOf(sourceItem);
        ConfigureItemsViewRow(row, sourceItem, index);
        RegisterRowForLazyItem(row, sourceItem as LazyTrackItem);
        ApplyItemsViewContainerDensity(row);
        row.IsSelected = sourceItem is not null && RowsItemsView.SelectedItems.Contains(sourceItem);
    }

    private void RowsItemsViewTrackItem_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Track.TrackItem row)
            return;

        _itemsViewRows.Remove(row);
        row.TrackChanged -= RowsItemsViewTrackItem_TrackChanged;
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
        foreach (var item in RowsList.Items)
        {
            if (RowsList.ContainerFromItem(item) is not ListViewItem container) continue;
            if (container.ContentTemplateRoot is not Track.TrackItem row) continue;
            row.ShowPopularityBadge = ShouldShowPopularityBadge(item);
        }

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

    // Plain click on an already-selected row must deselect it. The native ListView
    // (SelectionMode=Extended) replaces rather than toggles on plain click, so we
    // intercept PointerPressed to remember "pressed while already selected" and
    // Tapped to complete the toggle. Ctrl/Shift taps keep native behavior.
    private ListViewItem? _pressedWhileSelected;
    private PointerEventHandler? _containerPointerPressedHandler;
    private TappedEventHandler? _containerTappedHandler;

    private void WireContainerToggleHandlers(ListViewItem container)
    {
        _containerPointerPressedHandler ??= Container_PointerPressed;
        _containerTappedHandler ??= Container_Tapped;
        // handledEventsToo=true: ListViewItemPresenter marks PointerPressed/Tapped
        // handled during its internal selection path; we still need to run toggle logic.
        container.RemoveHandler(UIElement.PointerPressedEvent, _containerPointerPressedHandler);
        container.AddHandler(UIElement.PointerPressedEvent, _containerPointerPressedHandler, true);
        container.RemoveHandler(UIElement.TappedEvent, _containerTappedHandler);
        container.AddHandler(UIElement.TappedEvent, _containerTappedHandler, true);
    }

    private void Container_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewItem lvi) return;

        var (ctrl, shift) = GetCtrlShiftState();
        if (ctrl || shift || !lvi.IsSelected || IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            _pressedWhileSelected = null;
            return;
        }

        _pressedWhileSelected = lvi;
    }

    private void Container_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not ListViewItem lvi) return;
        if (_pressedWhileSelected != lvi) return;
        _pressedWhileSelected = null;

        var (ctrl, shift) = GetCtrlShiftState();
        if (ctrl || shift) return;

        var item = lvi.Content ?? lvi.DataContext;
        if (item != null && RowsList.SelectedItems.Contains(item))
            RowsList.SelectedItems.Remove(item);
        lvi.IsSelected = false;
        if (lvi.ContentTemplateRoot is Track.TrackItem ti)
            ti.IsSelected = false;
    }

    // CoreWindow.GetForCurrentThread() returns null on WinUI 3 threads that don't have a
    // CoreWindow attached (some dispatcher contexts hit this); dereferencing it crashed
    // the app. Use Microsoft.UI.Input.InputKeyboardSource — the WinUI 3 native key-state
    // API — and treat any failure as "no modifier held".
    private static (bool ctrl, bool shift) GetCtrlShiftState()
    {
        try
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            return (
                (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down,
                (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down);
        }
        catch
        {
            return (false, false);
        }
    }

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is ButtonBase or HyperlinkButton)
                return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private bool ColumnVisible(string key) =>
        Columns?.Any(c => c.Key == key && c.IsVisible) ?? false;

    private bool ShouldShowInlineProgress() =>
        PageKey == TrackDataGridDefaults.PodcastPageKey || ColumnVisible("Progress");

    private double WidthOf(string key, double fallback)
    {
        var col = Columns?.FirstOrDefault(c => c.Key == key);
        if (col is null || !col.IsVisible) return fallback;
        return col.Length.IsAbsolute ? col.Length.Value : fallback;
    }

    private double MaxWidthOf(string key, double fallback)
    {
        var col = Columns?.FirstOrDefault(c => c.Key == key);
        if (col is null || !col.IsVisible) return fallback;
        return col.MaxLength.IsAuto ? double.PositiveInfinity : col.MaxLength.Value;
    }

    /// <summary>Push current column widths from <see cref="Columns"/> onto a single row.</summary>
    private void PushWidthsToRow(Track.TrackItem item)
    {
        item.TitleColumnMaxWidth = MaxWidthOf("Track", 640);
        item.AlbumColumnWidth     = WidthOf("Album", 180);
        item.AddedByColumnWidth   = WidthOf("AddedBy", 140);
        item.DateAddedColumnWidth = WidthOf("DateAdded", 120);
        item.PlayCountColumnWidth = WidthOf("PlayCount", 100);
        item.ProgressColumnWidth  = WidthOf("Progress", 150);
        item.DurationColumnWidth  = WidthOf("Duration", 60);
    }

    /// <summary>Re-push column flags + widths onto every materialized TrackItem.</summary>
    private void RefreshRowShowFlags()
    {
        var walked = 0;
        var addedByShow = AddedByVisible && ColumnVisible("AddedBy");

        // Walk the panel children directly. ContainerFromItem returns null in
        // the brief window between an items-source change and the container
        // generator wiring up the new items — leaving in-flight TrackItems
        // (already materialised but freshly rebound to the new playlist's
        // rows) stuck with the previous playlist's column flags. The panel
        // children are the actual containers regardless of items state.
        if (RowsList.ItemsPanelRoot is not null)
        {
            foreach (var child in RowsList.ItemsPanelRoot.Children)
            {
                if (child is not ListViewItem container) continue;
                if (container.ContentTemplateRoot is not Track.TrackItem ti) continue;
                ti.BeginBatchUpdate();
                ti.ShowAlbumArt        = ColumnVisible("TrackArt");
                ti.ShowAlbumColumn     = ColumnVisible("Album");
                ti.ShowAddedByColumn   = addedByShow;
                ti.ShowDateAdded       = ColumnVisible("DateAdded");
                ti.ShowPlayCount       = ColumnVisible("PlayCount");
                ti.ShowProgress        = ShouldShowInlineProgress();
                PushWidthsToRow(ti);
                ti.EndBatchUpdate();
                walked++;
            }
        }
        foreach (var ti in _itemsViewRows.ToArray())
        {
            ti.BeginBatchUpdate();
            ti.ShowAlbumArt = ColumnVisible("TrackArt");
            ti.ShowAlbumColumn = ColumnVisible("Album");
            ti.ShowArtistColumn = ResolveShowArtistColumn();
            ti.ShowAddedByColumn = addedByShow;
            ti.ShowDateAdded = ColumnVisible("DateAdded");
            ti.ShowPlayCount = ColumnVisible("PlayCount");
            ti.ShowProgress = ShouldShowInlineProgress();
            PushWidthsToRow(ti);
            ti.EndBatchUpdate();
            walked++;
        }
        System.Diagnostics.Debug.WriteLine($"[addedby-grid] RefreshRowShowFlags: walked={walked} addedByShow={addedByShow} (AddedByVisible={AddedByVisible} colVisible={ColumnVisible("AddedBy")})");
    }

    /// <summary>
    /// Invoked after a splitter resize completes — only the <paramref name="changed"/>
    /// column's width has moved, so no need to touch the other DPs on every row.
    /// </summary>
    private void PushSingleColumnWidth(TrackDataGridColumn changed)
    {
        foreach (var item in RowsList.Items)
        {
            if (RowsList.ContainerFromItem(item) is not ListViewItem container) continue;
            if (container.ContentTemplateRoot is not Track.TrackItem ti) continue;
            PushSingleColumnWidthToRow(ti, changed);
        }
        foreach (var ti in _itemsViewRows.ToArray())
            PushSingleColumnWidthToRow(ti, changed);
    }

    private void PushSingleColumnWidthToRow(Track.TrackItem ti, TrackDataGridColumn changed)
    {
        switch (changed.Key)
        {
            case "Album":
                ti.AlbumColumnWidth = WidthOf("Album", 180);
                break;
            case "AddedBy":
                ti.AddedByColumnWidth = WidthOf("AddedBy", 140);
                break;
            case "DateAdded":
                ti.DateAddedColumnWidth = WidthOf("DateAdded", 120);
                break;
            case "PlayCount":
                ti.PlayCountColumnWidth = WidthOf("PlayCount", 100);
                break;
            case "Progress":
                ti.ProgressColumnWidth = WidthOf("Progress", 150);
                break;
            case "Duration":
                ti.DurationColumnWidth = WidthOf("Duration", 60);
                break;
        }
    }

    private void ApplyPersistedColumnWidths(TrackDataGridColumns columns, string pageKey)
    {
        if (TryGetSettings()?.Settings.ColumnWidths is not { } allWidths)
            return;

        if (!allWidths.TryGetValue(pageKey, out var pageWidths))
            return;

        foreach (var column in columns)
        {
            if (!column.SupportsResize || string.IsNullOrWhiteSpace(column.Key))
                continue;

            if (!pageWidths.TryGetValue(column.Key, out var width) || width <= 0)
                continue;

            column.Length = new GridLength(CoerceColumnWidth(column, width), GridUnitType.Pixel);
        }
    }

    private void PersistColumnWidth(TrackDataGridColumn column)
    {
        if (!column.SupportsResize || string.IsNullOrWhiteSpace(column.Key) || column.Length.Value <= 0)
            return;

        var pageKey = string.IsNullOrWhiteSpace(PageKey)
            ? TrackDataGridDefaults.PlaylistPageKey
            : PageKey;
        var key = column.Key;
        var width = Math.Round(CoerceColumnWidth(column, column.Length.Value));

        TryGetSettings()?.Update(settings =>
        {
            if (!settings.ColumnWidths.TryGetValue(pageKey, out var pageWidths))
            {
                pageWidths = new Dictionary<string, double>(StringComparer.Ordinal);
                settings.ColumnWidths[pageKey] = pageWidths;
            }

            pageWidths[key] = width;
        });
    }

    private ISettingsService? TryGetSettings()
    {
        if (_settingsService is not null)
            return _settingsService;

        try
        {
            return _settingsService = Ioc.Default.GetService<ISettingsService>();
        }
        catch
        {
            return null;
        }
    }

    private static double CoerceColumnWidth(TrackDataGridColumn column, double width)
    {
        var min = column.MinLength.IsAuto ? 0 : column.MinLength.Value;
        var max = column.MaxLength.IsAuto ? double.PositiveInfinity : column.MaxLength.Value;
        return Math.Clamp(width, min, max);
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

    /// <summary>
    /// Resolve whether a row should show the artist subline. Single source of
    /// truth so the four call sites (<c>RowsList_ContainerContentChanging</c>,
    /// <c>ConfigureItemsViewRow</c>, <c>RefreshRowShowFlags</c>) stay aligned.
    /// </summary>
    private bool ResolveShowArtistColumn() =>
        ForceShowArtistColumn || PageKey != TrackDataGridDefaults.AlbumPageKey;

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
                ArtColumnWidth = template.ArtColumnWidth,
                AlbumColumnWidth = template.AlbumColumnWidth,
                AddedByColumnWidth = template.AddedByColumnWidth,
                DateAddedColumnWidth = template.DateAddedColumnWidth,
                PlayCountColumnWidth = template.PlayCountColumnWidth,
                DurationColumnWidth = template.DurationColumnWidth,
                TitleColumnMaxWidth = template.TitleColumnMaxWidth,
                ShowArtistSubtitle = template.ShowArtistSubtitle,
            };
        }
        LoadingRowsRepeater.ItemsSource = items;
    }

    /// <summary>
    /// Derive the skeleton column geometry from the current page state. Mirrors
    /// the real-row column visibility model in
    /// <see cref="RowsList_ContainerContentChanging"/> so the skeleton row
    /// matches what the real row will paint into the same column slots — no
    /// horizontal shift when content loads.
    /// </summary>
    private LoadingRowConfig BuildLoadingRowConfigTemplate()
    {
        static GridLength WidthOrZero(TrackDataGridColumn? col)
            => col is null ? new GridLength(0) : col.Length;

        var artCol = Columns?.FirstOrDefault(c => c.Key == "TrackArt");
        var albumCol = Columns?.FirstOrDefault(c => c.Key == "Album");
        var addedByCol = Columns?.FirstOrDefault(c => c.Key == "AddedBy");
        var dateAddedCol = Columns?.FirstOrDefault(c => c.Key == "DateAdded");
        var playCountCol = Columns?.FirstOrDefault(c => c.Key == "PlayCount");
        var durationCol = Columns?.FirstOrDefault(c => c.Key == "Duration");
        var titleCol = Columns?.FirstOrDefault(c => c.Key == "Track");

        // AddedBy is special: the column may be present in the set but hidden
        // by the page-level AddedByVisible toggle (non-collab playlists). Mirror
        // RowsList_ContainerContentChanging:279.
        var addedByWidth = AddedByVisible && addedByCol is not null
            ? addedByCol.Length
            : new GridLength(0);

        // Title MaxWidth defaults to 640 (playlist) but album pages use null
        // (unbounded) so Plays/Duration pin right. Mirror that here: when the
        // active title column's MaxLength is Auto, treat it as "no cap".
        var titleMax = titleCol?.MaxLength is { GridUnitType: GridUnitType.Pixel } px
            ? px.Value
            : double.PositiveInfinity;

        return new LoadingRowConfig
        {
            ArtColumnWidth = WidthOrZero(artCol),
            AlbumColumnWidth = WidthOrZero(albumCol),
            AddedByColumnWidth = addedByWidth,
            DateAddedColumnWidth = WidthOrZero(dateAddedCol),
            PlayCountColumnWidth = WidthOrZero(playCountCol),
            DurationColumnWidth = WidthOrZero(durationCol),
            TitleColumnMaxWidth = titleMax,
            ShowArtistSubtitle = ResolveShowArtistColumn(),
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
            grid.RefreshRowCardStyles();
    }

    public static readonly DependencyProperty UseItemsViewRowsProperty =
        DependencyProperty.Register(nameof(UseItemsViewRows), typeof(bool), typeof(TrackDataGrid),
            new PropertyMetadata(false, OnUseItemsViewRowsChanged));

    public bool UseItemsViewRows
    {
        get => (bool)GetValue(UseItemsViewRowsProperty);
        set => SetValue(UseItemsViewRowsProperty, value);
    }

    private static void OnUseItemsViewRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackDataGrid grid)
            grid.ApplyRowsPresenterMode();
    }

    private void ApplyRowsPresenterMode()
    {
        if (RowsList is null || RowsItemsView is null) return;

        RowsList.Visibility = UseItemsViewRows ? Visibility.Collapsed : Visibility.Visible;
        // Toggle the WRAPPER Grid so the ItemsView's footer ContentPresenter
        // (RowsItemsViewHost row 1) also hides in ListView mode. Inner
        // ItemsView stays at default Visibility=Visible inside the wrapper.
        if (RowsItemsViewHost is not null)
            RowsItemsViewHost.Visibility = UseItemsViewRows ? Visibility.Visible : Visibility.Collapsed;
        ApplyHorizontalRowScroll();
        ApplyVerticalRowScroll();
        if (UseItemsViewRows)
            HookRowsItemsViewScrollView();
        else
            HookRowsListScrollViewer();
    }

    public void ClearSelection()
    {
        if (UseItemsViewRows)
            RowsItemsView.DeselectAll();
        else
            RowsList.SelectedItems.Clear();
    }

    private void RefreshRowCardStyles()
    {
        foreach (var item in RowsList.Items)
        {
            if (RowsList.ContainerFromItem(item) is not ListViewItem container)
                continue;
            if (container.ContentTemplateRoot is not Track.TrackItem row)
                continue;

            var index = RowsList.IndexFromContainer(container);
            row.SetAlternatingBorder(IsAlternateRow(item, index), UseCardRows);
        }

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

    /// <summary>
    /// Re-invoke <see cref="AddedByFormatter"/> for every materialized row and
    /// push the result onto the row's <c>TrackItem</c>. Use after the consumer
    /// (e.g. PlaylistViewModel) has resolved usernames + avatars in the
    /// background and needs the cells to refresh without a full grid rebuild.
    /// Cheap — only walks already-realized containers.
    /// </summary>
    public void RefreshAddedByCells()
    {
        if (AddedByFormatter is null)
        {
            System.Diagnostics.Debug.WriteLine("[addedby] RefreshAddedByCells: formatter null, no-op");
            return;
        }
        var walked = 0;
        var refreshed = 0;
        foreach (var item in RowsList.Items)
        {
            walked++;
            if (item is null) continue;
            if (RowsList.ContainerFromItem(item) is not ListViewItem container) continue;
            if (container.ContentTemplateRoot is not Track.TrackItem ti) continue;
            var info = AddedByFormatter(item);
            ti.AddedByText = info.Text;
            ti.AddedByAvatarUrl = info.AvatarUrl;
            refreshed++;
        }
        foreach (var ti in _itemsViewRows.ToArray())
        {
            walked++;
            if (ti.Track is null) continue;
            var info = AddedByFormatter(ti.Track);
            ti.AddedByText = info.Text;
            ti.AddedByAvatarUrl = info.AvatarUrl;
            refreshed++;
        }
        System.Diagnostics.Debug.WriteLine($"[addedby] RefreshAddedByCells: walked={walked} refreshed={refreshed}");
    }

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
            new PropertyMetadata(null));

    public object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
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
    /// containing page's scroll viewer drives the whole layout. Used by
    /// AlbumPage to merge the left sidebar + the track table into one
    /// unified scroller. Default <c>false</c> — other consumers (PlaylistPage,
    /// etc.) keep their normal in-grid vertical scroll.
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
        if (RowsList is null) return;
        if (IsParentScrolling)
        {
            ScrollViewer.SetVerticalScrollMode(RowsList, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(RowsList, ScrollBarVisibility.Disabled);
            if (RowsItemsView.ScrollView is { } itemsScrollView)
            {
                itemsScrollView.VerticalScrollMode = ScrollingScrollMode.Disabled;
                itemsScrollView.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
            }
        }
        else
        {
            ScrollViewer.SetVerticalScrollMode(RowsList, ScrollMode.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(RowsList, ScrollBarVisibility.Auto);
            if (RowsItemsView.ScrollView is { } itemsScrollView)
            {
                itemsScrollView.VerticalScrollMode = ScrollingScrollMode.Auto;
                itemsScrollView.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto;
            }
        }
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

    // ------------------------------------------------------------- change handlers

    private static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TrackDataGrid grid) return;
        if (e.OldValue is TrackDataGridColumns old)
            grid.UnsubscribeColumns(old);
        if (e.NewValue is TrackDataGridColumns fresh)
        {
            grid.SubscribeColumns(fresh);
            grid.SyncAddedByColumnVisibility();
        }
        grid.Root.Tag = e.NewValue;
        grid.RebuildHeader();
        grid.RebuildSortFlyout();
        grid.ReprojectRows();
        grid.ApplyLoadingRowCount();
    }

    private void SyncAddedByColumnVisibility()
    {
        var addedByCol = Columns?.FirstOrDefault(c => c.Key == "AddedBy");
        if (addedByCol is null || addedByCol.IsVisible == AddedByVisible)
            return;

        addedByCol.IsVisible = AddedByVisible;
    }

    private static void OnPageKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TrackDataGrid grid) return;
        if (e.NewValue is not string key || string.IsNullOrEmpty(key)) return;
        var columns = TrackDataGridDefaults.Create(key);
        grid.ApplyPersistedColumnWidths(columns, key);
        grid.Columns = columns;
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TrackDataGrid grid) return;
        if (grid._subscribedSource is not null)
            grid._subscribedSource.CollectionChanged -= grid.OnSourceCollectionChanged;
        grid._subscribedSource = null;

        if (e.NewValue is INotifyCollectionChanged notifying)
        {
            grid._subscribedSource = notifying;
            notifying.CollectionChanged += grid.OnSourceCollectionChanged;
        }
        grid.RefreshSnapshot();
        grid.ReprojectRows();
    }

    private static void OnGroupingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackDataGrid grid)
            grid.ReprojectRows();
    }

    private void SubscribeColumns(TrackDataGridColumns columns)
    {
        columns.SortChanged += OnSortChanged;
        columns.CollectionChanged += OnColumnsCollectionChanged;
    }

    private void UnsubscribeColumns(TrackDataGridColumns columns)
    {
        columns.SortChanged -= OnSortChanged;
        columns.CollectionChanged -= OnColumnsCollectionChanged;
    }

    private void OnColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildHeader();
        RebuildSortFlyout();
    }

    private void OnSortChanged(object? sender, EventArgs e)
    {
        SyncSortDirectionFlyoutState();
        ReprojectRows();
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshSnapshot();
        ReprojectRows();
    }

    private void RefreshSnapshot()
    {
        _sourceSnapshot = ItemsSource is IEnumerable<ITrackItem> typed
            ? typed.ToArray()
            : ItemsSource?.Cast<ITrackItem>().ToArray() ?? Array.Empty<ITrackItem>();
    }

    // ----------------------------------------------------------- header rebuild

    private readonly Dictionary<TrackDataGridColumn, (ColumnDefinition Def, TrackDataGridColumnHeader Header)> _headerSlots = new();

    private void RebuildHeader()
    {
        foreach (var col in _headerSlots.Keys)
            col.PropertyChanged -= OnHeaderColumnChanged;
        _headerSlots.Clear();

        HeaderHost.Children.Clear();
        HeaderHost.ColumnDefinitions.Clear();
        if (Columns is null) return;

        for (var i = 0; i < Columns.Count; i++)
        {
            var col = Columns[i];
            // Hidden columns still occupy a slot (so subsequent Grid.SetColumn indices
            // stay stable) but with zero width — mirroring how TrackItem collapses its
            // own row columns in parallel.
            var width = col.IsVisible
                ? col.Length
                : new GridLength(0);
            var def = new ColumnDefinition
            {
                Width = width,
                MinWidth = col.IsVisible ? col.MinLength.Value : 0,
                MaxWidth = !col.IsVisible
                    ? 0
                    : (col.MaxLength.IsAuto ? double.PositiveInfinity : col.MaxLength.Value),
            };
            HeaderHost.ColumnDefinitions.Add(def);

            var header = new TrackDataGridColumnHeader
            {
                Header = ResolveHeader(col.HeaderResourceKey),
                CanBeSorted = col.SortKey is not null,
                ColumnSortOption = col.SortDirection,
                Command = new SortRelay(this),
                CommandParameter = col,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = col.HorizontalAlignment,
                LabelPadding = col.LeftPadding,
            };
            Grid.SetColumn(header, i);
            if (col.IsVisible)
                HeaderHost.Children.Add(header);

            _headerSlots[col] = (def, header);
            col.PropertyChanged += OnHeaderColumnChanged;

            // A GridSplitter sits at the boundary between this column and the next
            // when both declare SupportsResize. Placed in the *next* grid column with
            // Left alignment so the splitter visual straddles the column edge without
            // overlapping either header's content.
            if (col.IsVisible && col.SupportsResize
                && i + 1 < Columns.Count
                && Columns[i + 1].SupportsResize
                && Columns[i + 1].IsVisible)
            {
                var splitter = new WaveeGridSplitter
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Width = 4,
                };
                var capturedCol = col;
                splitter.ResizeCompleted += (_, _) =>
                {
                    if (_headerSlots.TryGetValue(capturedCol, out var slot))
                    {
                        capturedCol.Length = slot.Def.Width;
                        PersistColumnWidth(capturedCol);
                    }
                };
                Grid.SetColumn(splitter, i + 1);
                HeaderHost.Children.Add(splitter);
            }
        }
    }

    private void OnHeaderColumnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TrackDataGridColumn col) return;
        if (!_headerSlots.TryGetValue(col, out var slot)) return;

        if (e.PropertyName == nameof(TrackDataGridColumn.IsVisible))
        {
            RebuildHeader();
            RefreshRowShowFlags();
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(TrackDataGridColumn.Length):
                slot.Def.Width = col.Length;
                PushSingleColumnWidth(col);
                break;
            case nameof(TrackDataGridColumn.MinLength):
                slot.Def.MinWidth = col.MinLength.Value;
                break;
            case nameof(TrackDataGridColumn.MaxLength):
                slot.Def.MaxWidth = col.MaxLength.IsAuto ? double.PositiveInfinity : col.MaxLength.Value;
                break;
            case nameof(TrackDataGridColumn.SortDirection):
                slot.Header.ColumnSortOption = col.SortDirection;
                break;
        }
    }

    private static string ResolveHeader(string key) =>
        // AppLocalization.GetString echoes the key back on a resource miss — that's
        // fine for real keys but confuses intentionally-empty headers (e.g. the
        // Like column's 44-px unlabeled header). Short-circuit empties here.
        string.IsNullOrEmpty(key) ? string.Empty : AppLocalization.GetString(key);

    // -------------------------------------------------------------- sort flyout

    private void RebuildSortFlyout()
    {
        if (SortBySubItem is null) return;
        SortBySubItem.Items.Clear();
        if (Columns is null) return;

        foreach (var col in Columns.Where(c => c.SortKey is not null))
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = ResolveHeader(col.HeaderResourceKey),
                GroupName = "TrackGridSortBy",
                Tag = col,
                IsChecked = ReferenceEquals(Columns.SortColumn, col),
            };
            item.Click += SortByItem_Click;
            SortBySubItem.Items.Add(item);
        }

        SyncSortDirectionFlyoutState();
    }

    private void SortByItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: TrackDataGridColumn col } || Columns is null)
            return;

        // Treat "pick a sort column" as "sort ascending" initially; the direction toggle
        // handles flipping. Matches the Files-app "Sort by > Name → Ascending/Descending" flow.
        var direction = Columns.SortColumn is null ? TrackDataGridSortDirection.Ascending : SelectedSortDirection();
        Columns.ApplySort(col, direction);
        SyncSortByFlyoutState();
    }

    private void SortDirectionItem_Click(object sender, RoutedEventArgs e)
    {
        if (Columns?.SortColumn is null) return;
        var direction = SelectedSortDirection();
        Columns.ApplySort(Columns.SortColumn, direction);
    }

    private TrackDataGridSortDirection SelectedSortDirection()
    {
        return SortDescendingItem?.IsChecked == true
            ? TrackDataGridSortDirection.Descending
            : TrackDataGridSortDirection.Ascending;
    }

    private void SyncSortDirectionFlyoutState()
    {
        if (SortAscendingItem is null || SortDescendingItem is null) return;
        var direction = Columns?.SortColumn?.SortDirection ?? TrackDataGridSortDirection.Ascending;
        SortAscendingItem.IsChecked = direction == TrackDataGridSortDirection.Ascending;
        SortDescendingItem.IsChecked = direction == TrackDataGridSortDirection.Descending;
    }

    private void SyncSortByFlyoutState()
    {
        if (SortBySubItem is null) return;
        foreach (var item in SortBySubItem.Items.OfType<RadioMenuFlyoutItem>())
            item.IsChecked = ReferenceEquals(item.Tag, Columns?.SortColumn);
    }

    // -------------------------------------------------------- filter + sort projection

    private void ReprojectRows()
    {
        var selectedKeys = CaptureSelectedTrackKeys();
        var source = _sourceSnapshot;
        if (source.Count == 0)
        {
            if (_visibleRows.Count > 0)
                _visibleRows.Clear();
            if (_visibleGroups.Count > 0)
                _visibleGroups.Clear();
            ApplyRowsItemsSource();
            ApplyLoadingRowsVisibility();
            return;
        }

        IEnumerable<ITrackItem> pipeline = source;

        if (!string.IsNullOrWhiteSpace(_filterText))
        {
            var q = _filterText;
            pipeline = pipeline.Where(t =>
                t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                t.ArtistName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                t.AlbumName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        if (Columns?.SortColumn is { SortKey: { } sortKey, SortDirection: { } direction })
        {
            pipeline = direction == TrackDataGridSortDirection.Ascending
                ? pipeline.OrderBy(t => SortValue(t, sortKey), Comparer<object?>.Create(CompareObjects))
                : pipeline.OrderByDescending(t => SortValue(t, sortKey), Comparer<object?>.Create(CompareObjects));
        }

        var rows = pipeline.ToList();
        _visibleRows.ReplaceWith(rows);
        RebuildGroups(rows);
        ApplyRowsItemsSource();
        RestoreSelectionByKeys(selectedKeys);
        ApplyLoadingRowsVisibility();
    }

    private void ApplyLoadingRowsVisibility()
    {
        if (LoadingRowsRepeater is null)
            return;

        LoadingRowsRepeater.Visibility = IsLoading && _sourceSnapshot.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyRowsItemsSource()
    {
        if (IsGrouped && GroupKeySelector is not null)
        {
            if (!ReferenceEquals(_groupedRowsViewSource.Source, _visibleGroups))
                _groupedRowsViewSource.Source = _visibleGroups;
            if (!ReferenceEquals(RowsList.ItemsSource, _groupedRowsViewSource.View))
                RowsList.ItemsSource = _groupedRowsViewSource.View;
            if (!ReferenceEquals(RowsItemsView.ItemsSource, _visibleRows))
                RowsItemsView.ItemsSource = _visibleRows;
            return;
        }

        if (!ReferenceEquals(RowsList.ItemsSource, _visibleRows))
            RowsList.ItemsSource = _visibleRows;
        if (!ReferenceEquals(RowsItemsView.ItemsSource, _visibleRows))
            RowsItemsView.ItemsSource = _visibleRows;
    }

    private void ApplyGroupHeaderTemplate()
    {
        if (RowsList?.GroupStyle is null || RowsList.GroupStyle.Count == 0)
            return;

        RowsList.GroupStyle[0].HeaderTemplate = GroupHeaderTemplate;
    }

    private void RebuildGroups(IReadOnlyList<ITrackItem> rows)
    {
        if (_visibleGroups.Count > 0)
            _visibleGroups.Clear();

        if (!IsGrouped || GroupKeySelector is null)
            return;

        foreach (var group in rows.GroupBy(
                     item => GroupKeySelector(item)?.ToString() ?? string.Empty,
                     StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToList();
            var header = GroupHeaderSelector?.Invoke(items[0]) ?? group.Key;
            var countText = GroupCountFormatter?.Invoke(items.Count) ?? (items.Count == 1 ? "1 item" : $"{items.Count:N0} items");
            _visibleGroups.Add(new TrackDataGridGroup(group.Key, header, items, countText));
        }
    }

    private static object? SortValue(ITrackItem item, string sortKey) => sortKey switch
    {
        "title" => item.Title,
        "artist" => item.ArtistName,
        "album" => item.AlbumName,
        "duration" => item.Duration.Ticks,
        "added" => ReflectNullableDateTime(item, "AddedAt"),
        "playcount" => ReflectNullableLong(item, "PlayCount"),
        _ => null,
    };

    private static DateTime? ReflectNullableDateTime(object item, string property)
    {
        var prop = item.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        return prop?.GetValue(item) as DateTime?;
    }

    private static long? ReflectNullableLong(object item, string property)
    {
        var prop = item.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        var value = prop?.GetValue(item);
        return value switch
        {
            long l => l,
            int i => i,
            _ => null,
        };
    }

    private static int CompareObjects(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        if (a is IComparable cmp) return cmp.CompareTo(b);
        return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
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

        // ItemContainerStyle's MinHeight lives on every materialized ListViewItem;
        // tweak directly so the change applies without a re-template pass. Also push
        // RowDensity into each TrackItem so padding / album-art / subline adjust.
        foreach (var item in RowsList.Items)
        {
            if (RowsList.ContainerFromItem(item) is not ListViewItem container) continue;
            container.MinHeight = height;
            container.Margin = outerMargin;
            if (container.ContentTemplateRoot is Track.TrackItem ti)
                ti.RowDensity = stop;
        }
        foreach (var row in _itemsViewRows.ToArray())
        {
            row.RowDensity = stop;
            ApplyItemsViewContainerDensity(row);
        }

        // Preserve for future containers (virtualization materializes on demand).
        _preferredRowHeight = height;
        _preferredDensity = stop;
    }

    private double? _preferredRowHeight;
    private int _preferredDensity = 2;

    private void DetailsToggle_Click(object sender, RoutedEventArgs e)
    {
        var shell = Ioc.Default.GetService<ShellViewModel>();
        if (shell is null) return;

        if (DetailsToggle.IsChecked == true)
        {
            var track = SelectedRowItem() as ITrackItem ?? _visibleRows.FirstOrDefault();
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

    // Selection menu — Select all / Invert / Clear.
    private void SelectAllItem_Click(object sender, RoutedEventArgs e)
        => SelectAllRows();

    private void SelectAllRows()
    {
        if (UseItemsViewRows)
        {
            RowsItemsView.SelectAll();
            SyncItemsViewRowSelectionState();
        }
        else
        {
            RowsList.SelectAll();
            SyncListViewRowSelectionState();
        }
    }

    private void InvertSelectionItem_Click(object sender, RoutedEventArgs e)
    {
        if (UseItemsViewRows)
        {
            RowsItemsView.InvertSelection();
            return;
        }

        var currentlySelected = new HashSet<object>(RowsList.SelectedItems.Cast<object>());
        RowsList.SelectedItems.Clear();
        foreach (var item in RowsList.Items)
        {
            if (!currentlySelected.Contains(item))
                RowsList.SelectedItems.Add(item);
        }
        SyncListViewRowSelectionState();
    }

    private void ClearSelectionItem_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private void RowsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncListViewRowSelectionState();
        if (_restoringSelection)
            return;

        var selected = SelectedRowItem();
        if (selected is ITrackItem track)
            RowSelected?.Invoke(this, track);

        if (selected is not null && SelectionChangedCommand?.CanExecute(selected) == true)
            SelectionChangedCommand.Execute(selected);
    }

    private void RowsItemsView_SelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        SyncItemsViewRowSelectionState();
        if (_restoringSelection)
            return;

        var selected = SelectedRowItem();
        if (selected is ITrackItem track)
            RowSelected?.Invoke(this, track);

        if (selected is not null && SelectionChangedCommand?.CanExecute(selected) == true)
            SelectionChangedCommand.Execute(selected);
    }

    private object? SelectedRowItem()
        => UseItemsViewRows ? RowsItemsView.SelectedItem : RowsList.SelectedItem;

    private HashSet<string> CaptureSelectedTrackKeys()
    {
        var selected = UseItemsViewRows
            ? RowsItemsView.SelectedItems.Cast<object>()
            : RowsList.SelectedItems.Cast<object>();

        return selected
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
            if (UseItemsViewRows)
            {
                RowsItemsView.DeselectAll();
                for (var i = 0; i < _visibleRows.Count; i++)
                {
                    var key = TrackSelectionKey(_visibleRows[i]);
                    if (!string.IsNullOrEmpty(key) && selectedKeys.Contains(key))
                        RowsItemsView.Select(i);
                }
            }
            else
            {
                RowsList.SelectedItems.Clear();
                foreach (var row in _visibleRows)
                {
                    var key = TrackSelectionKey(row);
                    if (!string.IsNullOrEmpty(key) && selectedKeys.Contains(key))
                        RowsList.SelectedItems.Add(row);
                }
            }
        }
        finally
        {
            _restoringSelection = false;
        }

        SyncItemsViewRowSelectionState();
        SyncListViewRowSelectionState();
    }

    private static string? TrackSelectionKey(ITrackItem item)
        => !string.IsNullOrWhiteSpace(item.Uri)
            ? item.Uri
            : !string.IsNullOrWhiteSpace(item.Id)
                ? item.Id
                : null;

    private void SyncItemsViewRowSelectionState()
    {
        if (!UseItemsViewRows)
            return;

        var selected = new HashSet<object>(RowsItemsView.SelectedItems.Cast<object>());
        foreach (var row in _itemsViewRows.ToArray())
            row.IsSelected = row.Track is not null && selected.Contains(row.Track);
    }

    private void SyncListViewRowSelectionState()
    {
        if (UseItemsViewRows || RowsList.ItemsPanelRoot is null)
            return;

        foreach (var child in RowsList.ItemsPanelRoot.Children)
        {
            if (child is ListViewItem container && container.ContentTemplateRoot is Track.TrackItem row)
                row.IsSelected = container.IsSelected;
        }
    }

    // Tap / DoubleTap are handled inside TrackItem (respecting AppSettings.TrackClickBehavior)
    // and native Extended-mode selection — nothing to wire at this level. Enter/Space on a
    // keyboard-selected row still plays via the handler below, matching TrackListView.

    private void RowsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.A && GetCtrlShiftState().ctrl)
        {
            SelectAllRows();
            e.Handled = true;
            return;
        }

        if ((e.Key == VirtualKey.Enter || e.Key == VirtualKey.Space) &&
            SelectedRowItem() is ITrackItem track)
        {
            PlayCommand?.Execute(track);
            e.Handled = true;
        }
    }

    // -------------------------------------------------------------- sort relay

    private sealed class SortRelay : ICommand
    {
        private readonly TrackDataGrid _owner;
        public SortRelay(TrackDataGrid owner) => _owner = owner;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => parameter is TrackDataGridColumn { SortKey: not null };
        public void Execute(object? parameter)
        {
            if (parameter is TrackDataGridColumn col)
                _owner.Columns?.CycleSort(col);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_rowsListScrollViewerWinUi is not null)
        {
            _rowsListScrollViewerWinUi.ViewChanged -= RowsListScrollViewer_ViewChanged;
            _rowsListScrollViewerWinUi = null;
        }
        if (_rowsItemsViewScrollView is not null)
        {
            _rowsItemsViewScrollView.ViewChanged -= RowsItemsViewScrollView_ViewChanged;
            _rowsItemsViewScrollView = null;
        }

        RowsList.ContainerContentChanging -= RowsList_ContainerContentChanging;
        RowsList.SelectionChanged -= RowsList_SelectionChanged;
        RowsList.Loaded -= RowsList_Loaded;
        RowsList.Unloaded -= RowsList_Unloaded;
        RowsItemsView.SelectionChanged -= RowsItemsView_SelectionChanged;
        RowsItemsView.Loaded -= RowsItemsView_Loaded;
        RowsItemsView.Unloaded -= RowsItemsView_Unloaded;

        foreach (var item in RowsList.Items)
        {
            if (RowsList.ContainerFromItem(item) is not ListViewItem container)
                continue;
            if (_containerPointerPressedHandler is not null)
                container.RemoveHandler(UIElement.PointerPressedEvent, _containerPointerPressedHandler);
            if (_containerTappedHandler is not null)
                container.RemoveHandler(UIElement.TappedEvent, _containerTappedHandler);
        }
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
        RowsList.SelectedItems.Clear();
        RowsList.ItemsSource = null;
        RowsItemsView.DeselectAll();
        RowsItemsView.ItemsSource = null;
        LoadingRowsRepeater.ItemsSource = null;
        _visibleRows.Clear();
        _sourceSnapshot = Array.Empty<ITrackItem>();
        _pressedWhileSelected = null;

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
}

public sealed class TrackDataGridGroup
{
    public TrackDataGridGroup(string key, object header, IReadOnlyList<ITrackItem> items, string countText)
    {
        Key = key;
        Header = header;
        Items = items;
        CountText = countText;
    }

    public string Key { get; }
    public object Header { get; }
    public IReadOnlyList<ITrackItem> Items { get; }
    public int Count => Items.Count;
    public string CountText { get; }
}
