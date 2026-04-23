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

    // Size-slider stops (matches the S/M/L/XL/XS segmentation in the view flyout).
    private static readonly double[] DensityRowHeights = { 36d, 40d, 48d, 56d, 64d };

    public TrackDataGrid()
    {
        InitializeComponent();
        RowsList.ItemsSource = _visibleRows;
        RowsList.ContainerContentChanging += RowsList_ContainerContentChanging;

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
                // Essential + cheap: play command + alternating zebra + density MinHeight.
                if (_preferredRowHeight is double h) container.MinHeight = h;
                item.PlayCommand = PlayCommand;
                item.SetAlternatingBorder(args.ItemIndex % 2 != 0);
                args.RegisterUpdateCallback(RowsList_ContainerContentChanging);
                break;

            case 1:
                // Column show/hide flags — batched so the 4 setters trigger one layout pass.
                item.BeginBatchUpdate();
                item.ShowAlbumArt     = ColumnVisible("TrackArt");
                item.ShowArtistColumn = PageKey != TrackDataGridDefaults.AlbumPageKey;
                item.ShowAlbumColumn  = ColumnVisible("Album");
                item.ShowDateAdded    = ColumnVisible("DateAdded");
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
                // Final trim: date string is consumer-provided; do it last.
                if (DateAddedFormatter != null && args.Item != null)
                    item.DateAddedText = DateAddedFormatter(args.Item);
                break;
        }
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
        item.DateAddedColumnWidth = WidthOf("DateAdded", 120);
        item.DurationColumnWidth  = WidthOf("Duration", 60);
    }

    /// <summary>Re-push column flags + widths onto every materialized TrackItem.</summary>
    private void RefreshRowShowFlags()
    {
        foreach (var item in RowsList.Items)
        {
            if (RowsList.ContainerFromItem(item) is not ListViewItem container) continue;
            if (container.ContentTemplateRoot is not Track.TrackItem ti) continue;
            ti.BeginBatchUpdate();
            ti.ShowAlbumArt    = ColumnVisible("TrackArt");
            ti.ShowAlbumColumn = ColumnVisible("Album");
            ti.ShowDateAdded   = ColumnVisible("DateAdded");
            PushWidthsToRow(ti);
            ti.EndBatchUpdate();
        }
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
                case "DateAdded":
                    ti.DateAddedColumnWidth = WidthOf("DateAdded", 120);
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

    private void DensitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var stop = (int)Math.Clamp(Math.Round(e.NewValue), 0, DensityRowHeights.Length - 1);
        var height = DensityRowHeights[stop];
        // ItemContainerStyle's MinHeight lives on every materialized ListViewItem; tweak
        // it directly so the change applies without a re-template pass.
        foreach (var item in RowsList.Items)
        {
            if (RowsList.ContainerFromItem(item) is ListViewItem container)
                container.MinHeight = height;
        }
        // Preserve for future containers (virtualization materializes on demand).
        _preferredRowHeight = height;
    }

    private double? _preferredRowHeight;

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
