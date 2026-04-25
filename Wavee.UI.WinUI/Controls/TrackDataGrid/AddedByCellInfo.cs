namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Per-row "Added by" cell payload returned by <c>TrackDataGrid.AddedByFormatter</c>.
/// Empty <see cref="Text"/> collapses the cell entirely (no chrome, no reserved
/// space) — used for rows the current user added themselves on collaborative
/// playlists, and for every row on non-collab playlists.
/// </summary>
public readonly record struct AddedByCellInfo(string Text, string? AvatarUrl)
{
    public static AddedByCellInfo Empty { get; } = new(string.Empty, null);
}
