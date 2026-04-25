using Microsoft.UI.Xaml;

namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Built-in column sets per page type. The column list drives the grid's header +
/// filter + sort infrastructure AND (via <c>ContainerContentChanging</c>) the
/// visibility flags on each <c>TrackItem</c> row. Column widths mirror
/// <c>TrackItem</c>'s internal Row-mode grid so header cells align with row cells.
/// </summary>
public static class TrackDataGridDefaults
{
    public const string PlaylistPageKey = "playlist";
    public const string AlbumPageKey = "album";
    public const string LikedPageKey = "liked";

    public static TrackDataGridColumns Create(string pageKey)
    {
        return pageKey switch
        {
            AlbumPageKey => BuildAlbumColumns(),
            LikedPageKey => BuildLikedColumns(),
            _ => BuildPlaylistColumns(),
        };
    }

    // Playlist: 8-column layout that exactly matches TrackItem Row-mode's internal
    // RowContentGrid. Artist is rendered as a subline under the Title cell (driven
    // from TrackDataGrid based on PageKey), not as a standalone header column.
    // The "Added by" column is registered here so the header always paints; the
    // row cell is hidden via TrackItem.ShowAddedByColumn on non-collab playlists,
    // and the page's AddedByVisible flag drives that show/hide.
    private static TrackDataGridColumns BuildPlaylistColumns() =>
    [
        Index(),
        Like(),
        TrackArt(),
        Title(),
        Album(),
        AddedBy(),
        DateAdded(),
        Duration(),
    ];

    private static TrackDataGridColumns BuildAlbumColumns() =>
    [
        Index(),
        Like(),
        Title(),
        PlayCount(),
        Duration(),
    ];

    // Index + Like must lead the column set: TrackItem (Row mode) ALWAYS renders
    // those two columns at positions 0 and 1, so omitting them from the header
    // shifts every header cell ~72 px left of its row content (visible as
    // "Track" landing on the album-art column instead of the title column).
    private static TrackDataGridColumns BuildLikedColumns() =>
    [
        Index(),
        Like(),
        TrackArt(),
        Title(),
        Album(),
        DateAdded(),
        Duration(),
    ];

    // ---- column factories (widths mirror TrackItem.xaml Row mode) ------------

    private static TrackDataGridColumn Index() => new()
    {
        Key = "Index",
        HeaderResourceKey = "TrackGrid_Column_Index",
        Length = new GridLength(40),
        MinLength = new GridLength(40),
        MaxLength = new GridLength(40),
        HorizontalAlignment = HorizontalAlignment.Center,
        IsLocked = true,
    };

    private static TrackDataGridColumn Like() => new()
    {
        Key = "Like",
        // Unlabeled narrow column — matches Spotify/Apple Music heart slot.
        HeaderResourceKey = string.Empty,
        Length = new GridLength(32),
        MinLength = new GridLength(32),
        MaxLength = new GridLength(32),
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    // Album-art thumbnail column. TrackItem renders the art; the header is blank.
    private static TrackDataGridColumn TrackArt() => new()
    {
        Key = "TrackArt",
        HeaderResourceKey = string.Empty,
        Length = new GridLength(42),
        MinLength = new GridLength(42),
        MaxLength = new GridLength(42),
    };

    private static TrackDataGridColumn Title() => new()
    {
        Key = "Track",
        HeaderResourceKey = "TrackGrid_Column_Track",
        SortKey = "title",
        Length = new GridLength(1, GridUnitType.Star),
        MinLength = new GridLength(120),
        // Cap Title width so the column stays consistent across playlists and
        // doesn't stretch to fill the whole window on wide layouts. Spotify-
        // style: Title gets ~half the available space, trailing columns stay
        // aligned against the right edge.
        MaxLength = new GridLength(640),
        IsLocked = true,
        // Match TrackItem's RowTitle StackPanel Margin="12,0,8,0" so header text
        // aligns with title text.
        LeftPadding = new Thickness(12, 0, 8, 0),
    };

    private static TrackDataGridColumn Album() => new()
    {
        Key = "Album",
        HeaderResourceKey = "TrackGrid_Column_Album",
        SortKey = "album",
        Length = new GridLength(180),
        MinLength = new GridLength(96),
        MaxLength = new GridLength(400),
        SupportsResize = true,
    };

    private static TrackDataGridColumn DateAdded() => new()
    {
        Key = "DateAdded",
        HeaderResourceKey = "TrackGrid_Column_DateAdded",
        SortKey = "added",
        Length = new GridLength(120),
        MinLength = new GridLength(72),
        MaxLength = new GridLength(200),
        SupportsResize = true,
    };

    private static TrackDataGridColumn AddedBy() => new()
    {
        Key = "AddedBy",
        HeaderResourceKey = "TrackGrid_Column_AddedBy",
        Length = new GridLength(140),
        MinLength = new GridLength(96),
        MaxLength = new GridLength(220),
        SupportsResize = true,
    };

    private static TrackDataGridColumn PlayCount() => new()
    {
        Key = "PlayCount",
        HeaderResourceKey = "TrackGrid_Column_PlayCount",
        SortKey = "playcount",
        Length = new GridLength(100),
        MinLength = new GridLength(72),
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    private static TrackDataGridColumn Duration() => new()
    {
        Key = "Duration",
        HeaderResourceKey = "TrackGrid_Column_Duration",
        SortKey = "duration",
        Length = new GridLength(60),
        MinLength = new GridLength(56),
        MaxLength = new GridLength(120),
        HorizontalAlignment = HorizontalAlignment.Right,
        SupportsResize = true,
    };
}
