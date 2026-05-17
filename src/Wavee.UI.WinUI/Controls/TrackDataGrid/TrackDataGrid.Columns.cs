using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Services;
using WaveeGridSplitter = Wavee.UI.WinUI.Controls.GridSplitter;

namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Column-related partial for <see cref="TrackDataGrid"/>:
/// header rebuild, per-column width persistence, sort flyout, and the
/// PropertyChanged → header / row fan-out for column DPs.
///
/// Split out from <c>TrackDataGrid.xaml.cs</c> purely for source-layout —
/// every member here still lives on the same partial class, so cross-column
/// invariants (header slot bookkeeping, sort cycling) keep their O(1)
/// dictionary access against grid-private state.
/// </summary>
public sealed partial class TrackDataGrid
{
    // ----------------------------------------------------------------------
    // Column / sort PropertyChanged plumbing
    // ----------------------------------------------------------------------

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

    // ----------------------------------------------------------------------
    // Header rebuild
    // ----------------------------------------------------------------------

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

    // ----------------------------------------------------------------------
    // Sort flyout
    // ----------------------------------------------------------------------

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

    // ----------------------------------------------------------------------
    // Column width persistence
    // ----------------------------------------------------------------------

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

    // ----------------------------------------------------------------------
    // Column visibility / width lookup helpers (shared with row fan-out)
    // ----------------------------------------------------------------------

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

    /// <summary>
    /// Resolve whether a row should show the artist subline. Single source of
    /// truth so the two call sites (<c>ConfigureItemsViewRow</c>,
    /// <c>RefreshRowShowFlags</c>) stay aligned.
    /// </summary>
    private bool ResolveShowArtistColumn() =>
        ForceShowArtistColumn || PageKey != TrackDataGridDefaults.AlbumPageKey;

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

    // ----------------------------------------------------------------------
    // Sort projection helpers (consumed by ReprojectRows in the main partial)
    // ----------------------------------------------------------------------

    private static object? SortValue(Wavee.UI.Contracts.ITrackItem item, string sortKey) => sortKey switch
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

    // ----------------------------------------------------------------------
    // Sort relay (header click → cycle the column's sort state)
    // ----------------------------------------------------------------------

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

    // ----------------------------------------------------------------------
    // Public column-data refresh entry points
    // ----------------------------------------------------------------------

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
}
