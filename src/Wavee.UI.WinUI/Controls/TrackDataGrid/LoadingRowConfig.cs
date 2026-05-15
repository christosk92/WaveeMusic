using Microsoft.UI.Xaml;

namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Per-row skeleton placeholder. All rows in <c>LoadingRowsRepeater</c> share
/// the same column geometry (Index, Like, then variable Art / Album / AddedBy
/// / DateAdded / PlayCount / Duration), so the config is built once from
/// <see cref="TrackDataGrid"/>'s current <c>PageKey</c> + <c>Columns</c> +
/// <c>AddedByVisible</c> + <c>ForceShowArtistColumn</c> and replicated
/// <c>LoadingRowCount</c> times.
///
/// Columns that are not in the active set get <see cref="GridLength"/>(0) — the
/// column collapses entirely instead of leaving a blank gap (which previously
/// caused the visible "track rows shift left when content loads" on the album
/// page, where the 42 px art-column gap closed on load).
/// </summary>
public sealed class LoadingRowConfig
{
    public int Index { get; init; }

    public GridLength ArtColumnWidth { get; init; }
    public GridLength AlbumColumnWidth { get; init; }
    public GridLength AddedByColumnWidth { get; init; }
    public GridLength DateAddedColumnWidth { get; init; }
    public GridLength PlayCountColumnWidth { get; init; }
    public GridLength DurationColumnWidth { get; init; }

    public double TitleColumnMaxWidth { get; init; }

    /// <summary>
    /// When true, render the second shimmer bar (artist subline) under the
    /// title. Matches <c>TrackItem.ShowArtistColumn</c> — false on
    /// single-artist album pages, true everywhere else.
    /// </summary>
    public bool ShowArtistSubtitle { get; init; }
}
