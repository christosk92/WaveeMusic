using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Storage.Entities;

public sealed record RootlistCacheEntry : ICacheEntry
{
    public required string Uri { get; init; }
    public EntityType EntityType => EntityType.Playlist;
    public byte[]? Revision { get; init; }
    public required string JsonData { get; init; }
    public DateTimeOffset CachedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAccessedAt { get; init; }
}
