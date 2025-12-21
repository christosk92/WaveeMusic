using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Storage;

/// <summary>
/// Cache entry for resolved context (playlist/album track list).
/// Has TTL because playlists can be modified by users.
/// </summary>
public sealed record ContextCacheEntry : ICacheEntry
{
    /// <summary>
    /// Context URI (e.g., "spotify:playlist:xxx").
    /// </summary>
    public required string Uri { get; init; }

    /// <summary>
    /// Entity type derived from URI.
    /// </summary>
    public EntityType EntityType => EntityTypeExtensions.ParseFromUri(Uri);

    /// <summary>
    /// When this entry was cached.
    /// </summary>
    public DateTimeOffset CachedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When this entry was last accessed (for LRU tracking).
    /// </summary>
    public DateTimeOffset? LastAccessedAt { get; init; }

    /// <summary>
    /// TTL-based expiry time.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Track list from context (URI + UID pairs).
    /// </summary>
    public required IReadOnlyList<(string Uri, string? Uid)> Tracks { get; init; }

    /// <summary>
    /// Next page URL for lazy loading.
    /// </summary>
    public string? NextPageUrl { get; init; }

    /// <summary>
    /// Total track count from context metadata.
    /// </summary>
    public int? TotalCount { get; init; }

    /// <summary>
    /// Whether this is an infinite context (radio/station).
    /// </summary>
    public bool IsInfinite { get; init; }

    /// <summary>
    /// Gets whether the cached context is still valid.
    /// </summary>
    public bool IsValid => DateTimeOffset.UtcNow < ExpiresAt;
}
