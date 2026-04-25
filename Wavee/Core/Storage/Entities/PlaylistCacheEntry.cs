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
    /// Wide editorial hero image URL (from the playlist's
    /// <c>header_image_url_desktop</c> format attribute). Persisted alongside
    /// <see cref="ImageUrl"/> so the banner survives cache eviction and app
    /// restarts — without this, the square cover shows instead of the banner.
    /// </summary>
    public string? HeaderImageUrl { get; init; }

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
    /// JSON-serialized playlist-level format attributes
    /// (<c>Dictionary&lt;string,string&gt;</c>). Preserves recommender /
    /// editorial fields — <c>format</c>, <c>request_id</c>, <c>tag</c>,
    /// <c>source-loader</c>, <c>image_url</c>,
    /// <c>session_control_display.displayName.*</c>, etc. — so the published
    /// <c>PlayerState.context_metadata</c> is identical after a cache reload.
    /// </summary>
    public string? FormatAttributesJson { get; init; }

    /// <summary>
    /// JSON-serialized list of fully-formed session-control signal identifiers
    /// from <c>SelectedListContent.Contents.AvailableSignals</c> — e.g.
    /// <c>session_control_display$&lt;group&gt;$&lt;option&gt;</c> entries and
    /// specials like <c>session-control-reset</c>. Persisted so the chips on
    /// an editorial playlist are clickable immediately on cache reload, rather
    /// than waiting for the background refresh to land.
    /// </summary>
    public string? AvailableSignalsJson { get; init; }

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
