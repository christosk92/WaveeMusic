using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// ── Root ──

public sealed class RecentlyPlayedEntitiesResponse
{
    [JsonPropertyName("data")]
    public RecentlyPlayedEntitiesData? Data { get; set; }
}

public sealed class RecentlyPlayedEntitiesData
{
    [JsonPropertyName("lookup")]
    public List<RecentlyPlayedEntityEntry>? Lookup { get; set; }
}

/// <summary>
/// A single entity from the lookup array. The __typename determines the content shape.
/// Possible types: AlbumResponseWrapper, ArtistResponseWrapper, PlaylistResponseWrapper.
/// </summary>
public sealed class RecentlyPlayedEntityEntry
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    /// <summary>
    /// Inner data varies by __typename. Deserialized as JsonElement for polymorphic dispatch.
    /// </summary>
    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

/// <summary>
/// Extension methods for extracting typed data from RecentlyPlayedEntityEntry.
/// Reuses HomeResponse data types since the shapes are identical.
/// </summary>
public static class RecentlyPlayedEntityExtensions
{
    public static HomeArtistData? GetArtistData(this RecentlyPlayedEntityEntry entry)
    {
        if (entry.Data is not { } el || el.ValueKind == JsonValueKind.Null) return null;
        return JsonSerializer.Deserialize(el.GetRawText(), HomeJsonContext.Default.HomeArtistData);
    }

    public static HomePlaylistData? GetPlaylistData(this RecentlyPlayedEntityEntry entry)
    {
        if (entry.Data is not { } el || el.ValueKind == JsonValueKind.Null) return null;
        return JsonSerializer.Deserialize(el.GetRawText(), HomeJsonContext.Default.HomePlaylistData);
    }

    public static HomeAlbumData? GetAlbumData(this RecentlyPlayedEntityEntry entry)
    {
        if (entry.Data is not { } el || el.ValueKind == JsonValueKind.Null) return null;
        return JsonSerializer.Deserialize(el.GetRawText(), HomeJsonContext.Default.HomeAlbumData);
    }
}

// ── Variables ──

public sealed class RecentlyPlayedEntitiesVariables
{
    [JsonPropertyName("uris")]
    public List<string> Uris { get; set; } = [];
}

// ── JSON serialization contexts ──

[JsonSerializable(typeof(RecentlyPlayedEntitiesResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
public partial class RecentlyPlayedEntitiesJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(RecentlyPlayedEntitiesVariables))]
public partial class RecentlyPlayedEntitiesVariablesJsonContext : JsonSerializerContext;
