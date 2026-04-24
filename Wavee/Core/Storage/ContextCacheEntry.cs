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
    /// Track list from context (URI + UID + per-track metadata).
    /// The metadata dict carries recommender decorations returned by
    /// <c>/context-resolve/v1/</c> — <c>core:list_uid</c>, <c>item-score</c>,
    /// <c>decision_id</c>, <c>PROBABLY_IN_*</c>, <c>pool-source</c>,
    /// <c>core:added_at</c>, etc. — so a cache hit reproduces the exact
    /// PlayerState payload real Spotify desktop would publish.
    /// </summary>
    public required IReadOnlyList<CachedContextTrack> Tracks { get; init; }

    /// <summary>
    /// Context-level metadata dict returned by <c>/context-resolve/v1/</c>.
    /// Includes <c>format_list_type</c>, <c>tag</c>, <c>request_id</c>,
    /// <c>image_url</c>, <c>header_image_url_desktop</c>,
    /// <c>context_description</c>, <c>session_control_display.displayName.*</c>,
    /// etc. Merged into <c>PlayerState.context_metadata</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ContextMetadata { get; init; }

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
    /// Total number of pages in the context (from <c>Context.Pages.Count</c> on
    /// the context-resolve response). Defaults to 1. Used by auto-pagination and
    /// by the hidden <c>spotify:meta:page:N</c> stub emission.
    /// </summary>
    public int PageCount { get; init; } = 1;

    /// <summary>
    /// Gets whether the cached context is still valid.
    /// </summary>
    public bool IsValid => DateTimeOffset.UtcNow < ExpiresAt;
}

/// <summary>
/// A single track as returned by the context-resolve API — carries the URI,
/// the server-assigned uid (32-char ASCII-hex), and the per-track metadata
/// dict that drives remote "Now Playing" decorations.
/// </summary>
public sealed record CachedContextTrack(
    string Uri,
    string? Uid,
    IReadOnlyDictionary<string, string>? Metadata);
