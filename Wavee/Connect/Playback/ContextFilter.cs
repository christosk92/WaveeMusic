namespace Wavee.Connect.Playback;

/// <summary>
/// Filter options for context tracks.
/// Values match Spotify's PlaylistQuery.BoolPredicate enum from playlist_query.proto.
/// </summary>
[Flags]
public enum ContextFilter
{
    /// <summary>
    /// No filtering (show all tracks).
    /// </summary>
    None = 0,

    /// <summary>
    /// Only show playable/available tracks.
    /// </summary>
    Available = 1 << 0,

    /// <summary>
    /// Only show tracks available offline.
    /// </summary>
    AvailableOffline = 1 << 1,

    /// <summary>
    /// Exclude tracks from banned artists.
    /// </summary>
    ArtistNotBanned = 1 << 2,

    /// <summary>
    /// Exclude banned tracks.
    /// </summary>
    NotBanned = 1 << 3,

    /// <summary>
    /// Exclude explicit content.
    /// </summary>
    NotExplicit = 1 << 4,

    /// <summary>
    /// Exclude podcast episodes.
    /// </summary>
    NotEpisode = 1 << 5,

    /// <summary>
    /// Exclude recommended tracks (in enhanced playlists).
    /// </summary>
    NotRecommendation = 1 << 6,

    /// <summary>
    /// Only show unplayed episodes.
    /// </summary>
    Unplayed = 1 << 7,

    /// <summary>
    /// Only show episodes in progress.
    /// </summary>
    InProgress = 1 << 8,

    /// <summary>
    /// Only show episodes not fully played.
    /// </summary>
    NotFullyPlayed = 1 << 9
}
