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
public sealed partial class TrackDataGrid : UserControl
{
    private readonly ObservableCollection<ITrackItem> _visibleRows = new();
    private IReadOnlyList<ITrackItem> _sourceSnapshot = Array.Empty<ITrackItem>();
    private INotifyCollectionChanged? _subscribedSource;
    private string _filterText = string.Empty;

    // Size-slider stops (matches the XS/S/M/L/XL segmentation in the view flyout).
    // MinHeight floor per row; content (padding + art + text) may still push the
    // row taller on larger steps, and that's intentional.
    private static readonly double[] DensityRowHeights = { 32d, 40d, 48d, 60d, 76d };

    public TrackDataGrid()
    {
        InitializeComponent();
        RowsList.ItemsSource = _visibleRows;
        RowsList.ContainerContentChanging += RowsList_ContainerContentChanging;
        RowsList.Loaded += RowsList_Loaded;
        RowsList.Unloaded += RowsList_Unloaded;

        // Set Slider.Value AFTER InitializeComponent so Minimum/Maximum are already in
        // place — attribute-order parsing in XAML was failing to apply Value="2" before
        // the other RangeBase properties settled.
        DensitySlider.Value = 2;

        var defaults = TrackDataGridDefaults.Create(TrackDataGridDefaults.PlaylistPageKey);
        SetValue(ColumnsProperty, defaults);
        Root.Tag = defaults;
        SubscribeColumns(defaults);
        RebuildHeader();
        RebuildSortFlyout();
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

    private void RowsList_Loaded(object sender, RoutedEventArgs e)
    {
        HookRowsListScrollViewer();
        ApplyHorizontalRowScroll();
    }

    private void RowsList_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_rowsListScrollViewerWinUi is not null)
        {
            _rowsListScrollViewerWinUi.ViewChanged -= RowsListScrollViewer_ViewChanged;
            _rowsListScrollViewerWinUi = null;
        }
    }

    private void ApplyHorizontalRowScroll()
    {
        if (RowsList is null) return;
        if (AllowHorizontalRowScroll)
        {
            ScrollViewer.SetHorizontalScrollMode(RowsList, ScrollMode.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(RowsList, ScrollBarVisibility.Auto);
        }
        else
        {
            ScrollViewer.SetHorizontalScrollMode(RowsList, ScrollMode.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(RowsList, ScrollBarVisibility.Disabled);
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

    /// <summary>
    /// Per-row setup, split into phases so the first frame only does what's needed
    /// to show the row at all; non-critical updates (column widths, date formatting)
    /// happen in later phases scheduled via <see cref="ContainerContentChangingEventArgs.RegisterUpdateCallback"/>.
    /// Without this, 200-track playlists blocked the UI thread for hundreds of ms on navigation.
    /// TrackItem owns hover, selection, heart toggle, tap (TrackClickBehavior), context menu.
    /// </summary>
    private void RowsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.ItemContainer is not ListViewItem container) return;
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
                item.SetAlternatingBorder(args.ItemIndex % 2 != 0);
                WireContainerToggleHandlers(container);
                args.RegisterUpdateCallback(RowsList_ContainerContentChanging);
                break;

            case 1:
                // Column show/hide flags — batched so the setters trigger one layout pass.
                item.BeginBatchUpdate();
                item.ShowAlbumArt        = ColumnVisible("TrackArt");
                item.ShowArtistColumn    = PageKey != TrackDataGridDefaults.AlbumPageKey;
                item.ShowAlbumColumn     = ColumnVisible("Album");
                item.ShowAddedByColumn   = AddedByVisible && ColumnVisible("AddedBy");
                item.ShowDateAdded       = ColumnVisible("DateAdded");
                item.ShowPlayCount       = ColumnVisible("PlayCount");
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
                if (DateAddedFormatter != null && args.Item != null)
                    item.DateAddedText = DateAddedFormatter(args.Item);
                if (PlayCountFormatter != null && args.Item != null)
                    item.PlayCountText = PlayCountFormatter(args.Item);
                if (AddedByFormatter != null && args.Item != null)
                {
                    var info = AddedByFormatter(args.Item);
                    item.AddedByText = info.Text;
                    item.AddedByAvatarUrl = info.AvatarUrl;
                }
                else
                {
                    item.AddedByText = string.Empty;
                    item.AddedByAvatarUrl = null;
                }
                break;
        }
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

    private double WidthOf(string key, double fallback)
    {
        var col = Columns?.FirstOrDefault(c => c.Key == key);
        if (col is null || !col.IsVisible) return fallback;
        return col.Length.IsAbsolute ? col.Length.Value : fallback;
    }

    /// <summary>Push current column widths from <see cref="Columns"/> onto a single row.</summary>
    private void PushWidthsToRow(Track.TrackItem item)
    {
        item.AlbumColumnWidth     = WidthOf("Album", 180);
        item.AddedByColumnWidth   = WidthOf("AddedBy", 140);
        item.DateAddedColumnWidth = WidthOf("DateAdded", 120);
        item.PlayCountColumnWidth = WidthOf("PlayCount", 100);
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
                PushWidthsToRow(ti);
                ti.EndBatchUpdate();
                walked++;
            }
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
                case "Duration":
                    ti.DurationColumnWidth = WidthOf("Duration", 60);
                    break;
            }
        }
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

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(TrackDataGrid),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
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
            // OnHeaderColumnChanged will run RefreshRowShowFlags as part of its
            // IsVisible-change branch — no need to call it again here.
            return;
        }

        grid.RefreshRowShowFlags();
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

    // ------------------------------------------------------------- change handlers

    private static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TrackDataGrid grid) return;
        if (e.OldValue is TrackDataGridColumns old)
            grid.UnsubscribeColumns(old);
        if (e.NewValue is TrackDataGridColumns fresh)
            grid.SubscribeColumns(fresh);
        grid.Root.Tag = e.NewValue;
        grid.RebuildHeader();
        grid.RebuildSortFlyout();
        grid.ReprojectRows();
    }

    private static void OnPageKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TrackDataGrid grid) return;
        if (e.NewValue is not string key || string.IsNullOrEmpty(key)) return;
        grid.Columns = TrackDataGridDefaults.Create(key);
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
                        capturedCol.Length = slot.Def.Width;
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
        _visibleRows.Clear();
        var source = _sourceSnapshot;
        if (source.Count == 0) return;

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

        foreach (var t in pipeline)
            _visibleRows.Add(t);
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
            var track = RowsList.SelectedItem as ITrackItem ?? _visibleRows.FirstOrDefault();
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
    private void SelectAllItem_Click(object sender, RoutedEventArgs e) => RowsList.SelectAll();

    private void InvertSelectionItem_Click(object sender, RoutedEventArgs e)
    {
        var currentlySelected = new HashSet<object>(RowsList.SelectedItems.Cast<object>());
        RowsList.SelectedItems.Clear();
        foreach (var item in RowsList.Items)
        {
            if (!currentlySelected.Contains(item))
                RowsList.SelectedItems.Add(item);
        }
    }

    private void ClearSelectionItem_Click(object sender, RoutedEventArgs e) => RowsList.SelectedItems.Clear();

    // Tap / DoubleTap are handled inside TrackItem (respecting AppSettings.TrackClickBehavior)
    // and native Extended-mode selection — nothing to wire at this level. Enter/Space on a
    // keyboard-selected row still plays via the handler below, matching TrackListView.

    private void RowsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if ((e.Key == VirtualKey.Enter || e.Key == VirtualKey.Space) &&
            RowsList.SelectedItem is ITrackItem track)
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
}
