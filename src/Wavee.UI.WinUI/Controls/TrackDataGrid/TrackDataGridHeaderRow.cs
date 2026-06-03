namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Marker projected as the FIRST synthetic row inside the grid's
/// <see cref="Microsoft.UI.Xaml.Controls.ItemsView"/> when a consumer opts into a
/// scrolling header via <see cref="TrackDataGridHeaderPlacement.InRowsScroll"/>.
/// Symmetric to <see cref="TrackDataGridFooterRow"/>: it lets a caller-supplied
/// element (e.g. a playlist banner) scroll WITH the virtualized rows instead of
/// sitting in a fixed band above them.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class TrackDataGridHeaderRow
{
    public required object Content { get; init; }
}
