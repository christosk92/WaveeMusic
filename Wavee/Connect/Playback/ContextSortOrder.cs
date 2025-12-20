namespace Wavee.Connect.Playback;

/// <summary>
/// Sort order for context tracks (playlists, albums).
/// Values match Spotify's PlaylistQuery.SortBy enum from playlist_query.proto.
/// </summary>
public enum ContextSortOrder
{
    /// <summary>
    /// Original playlist/album order (no sorting).
    /// </summary>
    Default = 0,

    /// <summary>
    /// Sort by album artist name A-Z.
    /// </summary>
    AlbumArtistNameAsc = 1,

    /// <summary>
    /// Sort by album artist name Z-A.
    /// </summary>
    AlbumArtistNameDesc = 2,

    /// <summary>
    /// Sort by track number (ascending).
    /// </summary>
    TrackNumberAsc = 3,

    /// <summary>
    /// Sort by track number (descending).
    /// </summary>
    TrackNumberDesc = 4,

    /// <summary>
    /// Sort by disc number (ascending).
    /// </summary>
    DiscNumberAsc = 5,

    /// <summary>
    /// Sort by disc number (descending).
    /// </summary>
    DiscNumberDesc = 6,

    /// <summary>
    /// Sort by album name A-Z.
    /// </summary>
    AlbumNameAsc = 7,

    /// <summary>
    /// Sort by album name Z-A.
    /// </summary>
    AlbumNameDesc = 8,

    /// <summary>
    /// Sort by artist name A-Z.
    /// </summary>
    ArtistNameAsc = 9,

    /// <summary>
    /// Sort by artist name Z-A.
    /// </summary>
    ArtistNameDesc = 10,

    /// <summary>
    /// Sort by track name A-Z.
    /// </summary>
    NameAsc = 11,

    /// <summary>
    /// Sort by track name Z-A.
    /// </summary>
    NameDesc = 12,

    /// <summary>
    /// Sort by date added to playlist (oldest first).
    /// </summary>
    AddTimeAsc = 13,

    /// <summary>
    /// Sort by date added to playlist (newest first).
    /// </summary>
    AddTimeDesc = 14,

    /// <summary>
    /// Sort by who added the track A-Z.
    /// </summary>
    AddedByAsc = 15,

    /// <summary>
    /// Sort by who added the track Z-A.
    /// </summary>
    AddedByDesc = 16,

    /// <summary>
    /// Sort by duration (shortest first).
    /// </summary>
    DurationAsc = 17,

    /// <summary>
    /// Sort by duration (longest first).
    /// </summary>
    DurationDesc = 18
}
