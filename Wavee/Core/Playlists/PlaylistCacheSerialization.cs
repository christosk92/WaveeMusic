using System.Text.Json.Serialization;

namespace Wavee.Core.Playlists;

internal sealed record PersistedRootlistData
{
    public byte[] Revision { get; init; } = [];
    public List<PersistedRootlistEntry> Items { get; init; } = [];
    public Dictionary<string, RootlistDecoration> Decorations { get; init; } = new(StringComparer.Ordinal);
    public DateTimeOffset FetchedAt { get; init; }
}

internal sealed record PersistedPlaylistItems
{
    public List<CachedPlaylistItem> Items { get; init; } = [];
}

internal sealed record PersistedRootlistEntry
{
    public PersistedRootlistEntryKind Kind { get; init; }
    public string? Uri { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
}

internal enum PersistedRootlistEntryKind
{
    Playlist,
    FolderStart,
    FolderEnd
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CachedPlaylistCapabilities))]
[JsonSerializable(typeof(PersistedPlaylistItems))]
[JsonSerializable(typeof(PersistedRootlistData))]
internal partial class PlaylistCacheJsonContext : JsonSerializerContext
{
}
