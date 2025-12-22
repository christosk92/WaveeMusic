namespace Wavee.Core.Library.Spotify;

/// <summary>
/// Represents a Spotify playlist (owned or followed by the user).
/// </summary>
public sealed record SpotifyPlaylist
{
    /// <summary>
    /// Playlist URI (spotify:playlist:xxx).
    /// </summary>
    public required string Uri { get; init; }

    /// <summary>
    /// Playlist name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Owner's user ID.
    /// </summary>
    public string? OwnerId { get; init; }

    /// <summary>
    /// Owner's display name.
    /// </summary>
    public string? OwnerName { get; init; }

    /// <summary>
    /// Playlist description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// URL to playlist cover image.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Number of tracks in the playlist.
    /// </summary>
    public int TrackCount { get; init; }

    /// <summary>
    /// Whether the playlist is public.
    /// </summary>
    public bool IsPublic { get; init; }

    /// <summary>
    /// Whether the playlist is collaborative.
    /// </summary>
    public bool IsCollaborative { get; init; }

    /// <summary>
    /// Whether the current user owns this playlist.
    /// </summary>
    public bool IsOwned { get; init; }

    /// <summary>
    /// When this playlist was last synced (Unix timestamp).
    /// </summary>
    public long SyncedAt { get; init; }

    /// <summary>
    /// Revision for incremental sync (null if never synced).
    /// </summary>
    public string? Revision { get; init; }

    /// <summary>
    /// Creates a SpotifyPlaylist with current sync timestamp.
    /// </summary>
    public static SpotifyPlaylist Create(
        string uri,
        string name,
        string? ownerId = null,
        string? ownerName = null,
        string? description = null,
        string? imageUrl = null,
        int trackCount = 0,
        bool isPublic = false,
        bool isCollaborative = false,
        bool isOwned = false,
        string? revision = null)
    {
        return new SpotifyPlaylist
        {
            Uri = uri,
            Name = name,
            OwnerId = ownerId,
            OwnerName = ownerName,
            Description = description,
            ImageUrl = imageUrl,
            TrackCount = trackCount,
            IsPublic = isPublic,
            IsCollaborative = isCollaborative,
            IsOwned = isOwned,
            SyncedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Revision = revision
        };
    }
}
