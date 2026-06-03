namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Controls how <see cref="TrackDataGrid.HeaderContent"/> is hosted.
/// <see cref="None"/> (default) projects nothing — the grid keeps its classic
/// pinned chrome with no leading header row. <see cref="InRowsScroll"/> projects
/// the header as a synthetic first row INSIDE the ItemsView so it scrolls away
/// with the rows (and the grid's toolbar/column-header become a sticky overlay
/// that rides just below the header until it reaches the top).
/// </summary>
public enum TrackDataGridHeaderPlacement
{
    None,
    InRowsScroll,
}
