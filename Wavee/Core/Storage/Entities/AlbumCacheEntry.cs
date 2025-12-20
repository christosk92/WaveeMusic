using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Storage.Entities;

/// <summary>
/// Cache entry for album metadata.
/// </summary>
public sealed record AlbumCacheEntry : ICacheEntry
{
    /// <inheritdoc />
    public required string Uri { get; init; }

    /// <inheritdoc />
    public EntityType EntityType => EntityType.Album;

    /// <summary>
    /// Album name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Primary artist name.
    /// </summary>
    public string? ArtistName { get; init; }

    /// <summary>
    /// Primary artist URI.
    /// </summary>
    public string? ArtistUri { get; init; }

    /// <summary>
    /// Number of tracks on the album.
    /// </summary>
    public int? TrackCount { get; init; }

    /// <summary>
    /// Release year.
    /// </summary>
    public int? ReleaseYear { get; init; }

    /// <summary>
    /// Album cover image URL.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Album type (album, single, compilation, etc.).
    /// </summary>
    public string? AlbumType { get; init; }

    /// <summary>
    /// URIs of tracks on this album.
    /// </summary>
    public IReadOnlyList<string>? TrackUris { get; init; }

    /// <inheritdoc />
    public DateTimeOffset CachedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public DateTimeOffset? LastAccessedAt { get; init; }
}
