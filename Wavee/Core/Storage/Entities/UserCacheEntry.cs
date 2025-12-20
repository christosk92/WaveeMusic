using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Storage.Entities;

/// <summary>
/// Cache entry for user profile metadata.
/// </summary>
public sealed record UserCacheEntry : ICacheEntry
{
    /// <inheritdoc />
    public required string Uri { get; init; }

    /// <inheritdoc />
    public EntityType EntityType => EntityType.User;

    /// <summary>
    /// User display name.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Profile image URL.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Number of followers.
    /// </summary>
    public int? FollowerCount { get; init; }

    /// <summary>
    /// Number of public playlists.
    /// </summary>
    public int? PublicPlaylistCount { get; init; }

    /// <inheritdoc />
    public DateTimeOffset CachedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public DateTimeOffset? LastAccessedAt { get; init; }
}
