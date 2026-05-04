using System.Text.Json.Serialization;

namespace Wavee.Core.Http;

/// <summary>
/// JSON payload returned by Spotify's liked-songs content-filter endpoint.
/// </summary>
public sealed record LikedSongsContentFiltersResponse
{
    [JsonPropertyName("contentFilters")]
    public IReadOnlyList<LikedSongsContentFilter> ContentFilters { get; init; } = Array.Empty<LikedSongsContentFilter>();
}

/// <summary>
/// A single Spotify-provided liked-songs filter chip definition.
/// </summary>
public sealed record LikedSongsContentFilter
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("query")]
    public string Query { get; init; } = "";
}

/// <summary>
/// Result of fetching liked-songs content filters, including conditional GET metadata.
/// </summary>
public sealed record LikedSongsContentFiltersResult
{
    public IReadOnlyList<LikedSongsContentFilter> Filters { get; init; } = Array.Empty<LikedSongsContentFilter>();

    public string? ETag { get; init; }

    public bool IsNotModified { get; init; }
}

[JsonSerializable(typeof(LikedSongsContentFiltersResponse))]
internal partial class LikedSongsContentFiltersJsonContext : JsonSerializerContext;
