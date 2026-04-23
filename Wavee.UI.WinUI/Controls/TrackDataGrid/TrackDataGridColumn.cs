using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Single column's presentation + sort state for <see cref="TrackDataGrid"/>.
/// Header grid and every row's grid both bind their cell widths to the same
/// <see cref="Length"/>, which is the point of having this live on an INPC model
/// rather than separate <see cref="Microsoft.UI.Xaml.Controls.ColumnDefinition"/>s.
/// </summary>
public sealed class TrackDataGridColumn : INotifyPropertyChanged
{
    private GridLength _length = new(1, GridUnitType.Star);
    private GridLength _minLength = new(48);
    private GridLength _maxLength = GridLength.Auto;
    private bool _isVisible = true;
    private TrackDataGridSortDirection? _sortDirection;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Stable identifier, used for persistence and sort-key lookups. XAML type-info
    /// generation forces settable (not <c>init</c>) + non-<c>required</c>; callers in
    /// <see cref="TrackDataGridDefaults"/> still set these on construction via the
    /// object initializer, so behavior is unchanged.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Resource key for the localized header label.</summary>
    public string HeaderResourceKey { get; set; } = string.Empty;

    /// <summary>Cell template applied to both the header cell's content presenter and every row's.</summary>
    public DataTemplate? CellTemplate { get; set; }

    /// <summary>
    /// Sort key passed to the grid's sort handler. <c>null</c> = column is not sortable
    /// and the header suppresses its arrow.
    /// </summary>
    public string? SortKey { get; set; }

    /// <summary>Where cell content aligns horizontally. Defaults to <see cref="HorizontalAlignment.Left"/>.</summary>
    public HorizontalAlignment HorizontalAlignment { get; set; } = HorizontalAlignment.Left;

    /// <summary>
    /// When true, the column picker cannot hide this column. <c>Track</c> uses this —
    /// a row with no title is useless.
    /// </summary>
    public bool IsLocked { get; set; }

    /// <summary>
    /// When true, a resize <c>GridSplitter</c> is inserted after this column (provided
    /// the next column also declares <c>SupportsResize</c>). Widths persist as runtime
    /// edits to <see cref="Length"/>, which propagate to each row's internal column too.
    /// </summary>
    public bool SupportsResize { get; set; }

    /// <summary>
    /// Start-side padding applied to the header cell's content, so the header label
    /// lines up horizontally with the row cell's text. Row cells keep their own margins
    /// (e.g. <c>TrackItem</c>'s 12-px Title margin); the header mirrors that value here.
    /// </summary>
    public Thickness LeftPadding { get; set; } = new(0);

    /// <summary>Current column width, 2-way-bound from header <see cref="ColumnDefinition.Width"/>.</summary>
    public GridLength Length
    {
        get => _length;
        set => SetProperty(ref _length, value);
    }

    public GridLength MinLength
    {
        get => _minLength;
        set => SetProperty(ref _minLength, value);
    }

    public GridLength MaxLength
    {
        get => _maxLength;
        set => SetProperty(ref _maxLength, value);
    }

    /// <summary>Toggled from the column picker. When false, the column is removed from the layout.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>
    /// Which direction this column is currently sorted, or <c>null</c> when another column
    /// owns the sort. <see cref="TrackDataGridColumnHeader"/> binds its visual state to this.
    /// </summary>
    public TrackDataGridSortDirection? SortDirection
    {
        get => _sortDirection;
        set => SetProperty(ref _sortDirection, value);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
