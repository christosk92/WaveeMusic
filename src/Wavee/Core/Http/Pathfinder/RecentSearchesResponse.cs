using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// ── Root response ──

public sealed class RecentSearchesResponse
{
    [JsonPropertyName("data")]
    public RecentSearchesData? Data { get; init; }
}

public sealed class RecentSearchesData
{
    [JsonPropertyName("recentSearches")]
    public RecentSearchesResult? RecentSearches { get; init; }
}

public sealed class RecentSearchesResult
{
    [JsonPropertyName("recentSearchesItems")]
    public RecentSearchesPage? RecentSearchesItems { get; init; }
}

public sealed class RecentSearchesPage
{
    [JsonPropertyName("items")]
    public List<RecentSearchItem>? Items { get; init; }
}

/// <summary>
/// A polymorphic recent search item. The __typename discriminator tells us what
/// kind of entity it is (Artist, Track, Album, Playlist).
/// The "data" property contains the entity-specific fields as raw JSON.
/// </summary>
public sealed class RecentSearchItem
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }
}
