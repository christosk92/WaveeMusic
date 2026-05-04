using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Storage.Entities;

/// <summary>
/// Cache entry for podcast show metadata.
/// </summary>
public sealed record ShowCacheEntry : ICacheEntry
{
    /// <inheritdoc />
    public required string Uri { get; init; }

    /// <inheritdoc />
    public EntityType EntityType => EntityType.Show;

    /// <summary>
    /// Show name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Publisher name.
    /// </summary>
    public string? Publisher { get; init; }

    /// <summary>
    /// Show description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Number of episodes.
    /// </summary>
    public int? EpisodeCount { get; init; }

    /// <summary>
    /// Show cover image URL.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Whether the show contains explicit content.
    /// </summary>
    public bool IsExplicit { get; init; }

    /// <summary>
    /// Show media type (audio, video, mixed).
    /// </summary>
    public string? MediaType { get; init; }

    /// <inheritdoc />
    public DateTimeOffset CachedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public DateTimeOffset? LastAccessedAt { get; init; }
}
