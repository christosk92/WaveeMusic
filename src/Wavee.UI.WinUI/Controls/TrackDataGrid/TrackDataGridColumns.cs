using System;
using System.Collections.ObjectModel;

namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Owns the ordered set of <see cref="TrackDataGridColumn"/>s and the active sort
/// assignment. Mutating the active sort fires <see cref="SortChanged"/>, which
/// <see cref="TrackDataGrid"/> listens to for rebuilding its filtered-and-sorted
/// row projection.
/// </summary>
public sealed class TrackDataGridColumns : ObservableCollection<TrackDataGridColumn>
{
    /// <summary>Column currently driving the sort, or <c>null</c> for unsorted.</summary>
    public TrackDataGridColumn? SortColumn { get; private set; }

    public event EventHandler? SortChanged;

    /// <summary>
    /// Click on a column header: if that column wasn't sorted, switch to it Ascending;
    /// if it was already Ascending, flip to Descending; if Descending, unsort.
    /// </summary>
    public void CycleSort(TrackDataGridColumn column)
    {
        if (column.SortKey is null) return;

        TrackDataGridSortDirection? next = column.SortDirection switch
        {
            null => TrackDataGridSortDirection.Ascending,
            TrackDataGridSortDirection.Ascending => TrackDataGridSortDirection.Descending,
            TrackDataGridSortDirection.Descending => null,
            _ => null
        };

        ApplySort(next is null ? null : column, next);
    }

    /// <summary>
    /// Set the sort to a specific column + direction (or clear it with <c>null</c>s),
    /// handling the cross-column "previous sort column loses its arrow" bookkeeping.
    /// </summary>
    public void ApplySort(TrackDataGridColumn? column, TrackDataGridSortDirection? direction)
    {
        if (SortColumn is not null && !ReferenceEquals(SortColumn, column))
            SortColumn.SortDirection = null;

        if (column is not null)
            column.SortDirection = direction;

        SortColumn = direction is null ? null : column;
        SortChanged?.Invoke(this, EventArgs.Empty);
    }
}
