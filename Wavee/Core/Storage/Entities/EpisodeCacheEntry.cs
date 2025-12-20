using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Storage.Entities;

/// <summary>
/// Cache entry for podcast episode metadata.
/// </summary>
public sealed record EpisodeCacheEntry : ICacheEntry
{
    /// <inheritdoc />
    public required string Uri { get; init; }

    /// <inheritdoc />
    public EntityType EntityType => EntityType.Episode;

    /// <summary>
    /// Episode name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Parent show URI.
    /// </summary>
    public string? ShowUri { get; init; }

    /// <summary>
    /// Parent show name.
    /// </summary>
    public string? ShowName { get; init; }

    /// <summary>
    /// Episode description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Duration in milliseconds.
    /// </summary>
    public int? DurationMs { get; init; }

    /// <summary>
    /// Release date.
    /// </summary>
    public DateTimeOffset? ReleaseDate { get; init; }

    /// <summary>
    /// Episode cover image URL.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Whether the episode is playable.
    /// </summary>
    public bool IsPlayable { get; init; } = true;

    /// <summary>
    /// Whether the episode contains explicit content.
    /// </summary>
    public bool IsExplicit { get; init; }

    /// <inheritdoc />
    public DateTimeOffset CachedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public DateTimeOffset? LastAccessedAt { get; init; }
}
