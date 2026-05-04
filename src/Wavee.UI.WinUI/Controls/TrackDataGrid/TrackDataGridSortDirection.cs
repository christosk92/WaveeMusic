namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Sort direction for a <see cref="TrackDataGridColumn"/>. Distinct from
/// <c>Microsoft.UI.Xaml.Data.SortDirection</c> because the framework enum lives
/// on <c>CollectionViewSource</c> pipelines we don't use here.
/// </summary>
public enum TrackDataGridSortDirection
{
    Ascending,
    Descending
}
