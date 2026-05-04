using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// ── Variables ──

public sealed record AlbumMerchVariables
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }
}

// ── Response ──

public sealed record AlbumMerchResponse
{
    [JsonPropertyName("data")]
    public AlbumMerchData? Data { get; init; }
}

public sealed record AlbumMerchData
{
    [JsonPropertyName("albumUnion")]
    public AlbumMerchUnion? AlbumUnion { get; init; }
}

public sealed record AlbumMerchUnion
{
    [JsonPropertyName("merch")]
    public AlbumMerchContainer? Merch { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed record AlbumMerchContainer
{
    [JsonPropertyName("items")]
    public List<AlbumMerchItem>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed record AlbumMerchItem
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("image")]
    public AlbumMerchImage? Image { get; init; }

    [JsonPropertyName("nameV2")]
    public string? NameV2 { get; init; }

    [JsonPropertyName("price")]
    public string? Price { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public sealed record AlbumMerchImage
{
    [JsonPropertyName("sources")]
    public List<AlbumMerchImageSource>? Sources { get; init; }
}

public sealed record AlbumMerchImageSource
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

// ── JSON contexts ──

[JsonSerializable(typeof(AlbumMerchVariables))]
internal partial class AlbumMerchVariablesJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(AlbumMerchResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class AlbumMerchJsonContext : JsonSerializerContext { }
