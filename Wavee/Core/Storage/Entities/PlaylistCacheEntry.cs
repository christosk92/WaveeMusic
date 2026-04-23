using Wavee.Core.Playlists;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Storage.Entities;

/// <summary>
/// Cache entry for playlist metadata.
/// </summary>
public sealed record PlaylistCacheEntry : ICacheEntry
{
    /// <inheritdoc />
    public required string Uri { get; init; }

    /// <inheritdoc />
    public EntityType EntityType => EntityType.Playlist;

    /// <summary>
    /// Playlist name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Playlist description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Owner user URI.
    /// </summary>
    public string? OwnerUri { get; init; }

    /// <summary>
    /// Owner username.
    /// </summary>
    public string? OwnerUsername { get; init; }

    /// <summary>
    /// Owner display name.
    /// </summary>
    public string? OwnerName { get; init; }

    /// <summary>
    /// Number of tracks in the playlist.
    /// </summary>
    public int? TrackCount { get; init; }

    /// <summary>
    /// Playlist cover image URL.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Whether the playlist is public.
    /// </summary>
    public bool IsPublic { get; init; }

    /// <summary>
    /// Whether the playlist is collaborative.
    /// </summary>
    public bool IsCollaborative { get; init; }

    /// <summary>
    /// Number of followers.
    /// </summary>
    public int? FollowerCount { get; init; }

    /// <summary>
    /// Opaque playlist revision bytes from the playlist v2 API.
    /// </summary>
    public byte[]? Revision { get; init; }

    /// <summary>
    /// JSON-serialized ordered playlist items.
    /// </summary>
    public string? OrderedItemsJson { get; init; }

    /// <summary>
    /// Whether OrderedItemsJson is a complete snapshot of playlist contents.
    /// </summary>
    public bool HasContentsSnapshot { get; init; }

    /// <summary>
    /// Cached base permission for the current user.
    /// </summary>
    public CachedPlaylistBasePermission BasePermission { get; init; } = CachedPlaylistBasePermission.Viewer;

    /// <summary>
    /// JSON-serialized capability snapshot.
    /// </summary>
    public string? CapabilitiesJson { get; init; }

    /// <summary>
    /// Whether the owner deleted the playlist while the user still has access.
    /// </summary>
    public bool DeletedByOwner { get; init; }

    /// <summary>
    /// Whether abuse reporting is enabled for the playlist.
    /// </summary>
    public bool AbuseReportingEnabled { get; init; }

    /// <inheritdoc />
    public DateTimeOffset CachedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public DateTimeOffset? LastAccessedAt { get; init; }
}
