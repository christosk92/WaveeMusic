using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Storage.Entities;

/// <summary>
/// Cache entry for artist metadata.
/// </summary>
public sealed record ArtistCacheEntry : ICacheEntry
{
    /// <inheritdoc />
    public required string Uri { get; init; }

    /// <inheritdoc />
    public EntityType EntityType => EntityType.Artist;

    /// <summary>
    /// Artist name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Number of followers.
    /// </summary>
    public int? FollowerCount { get; init; }

    /// <summary>
    /// Artist genres.
    /// </summary>
    public IReadOnlyList<string>? Genres { get; init; }

    /// <summary>
    /// Artist profile image URL.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// URIs of top tracks.
    /// </summary>
    public IReadOnlyList<string>? TopTrackUris { get; init; }

    /// <summary>
    /// URIs of albums.
    /// </summary>
    public IReadOnlyList<string>? AlbumUris { get; init; }

    /// <inheritdoc />
    public DateTimeOffset CachedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public DateTimeOffset? LastAccessedAt { get; init; }
}
