using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// DTO for the queryNpvEpisodeChapters persisted GraphQL query. Mirrors the
// shape Spotify returns under data.episodeUnionV2.displaySegments — only the
// fields the chapter-aware position bar actually consumes are modelled.

public sealed class QueryNpvEpisodeChaptersResponse
{
    [JsonPropertyName("data")]
    public QueryNpvEpisodeChaptersData? Data { get; init; }
}

public sealed class QueryNpvEpisodeChaptersData
{
    [JsonPropertyName("episodeUnionV2")]
    public PathfinderEpisodeChapters? EpisodeUnionV2 { get; init; }
}

public sealed class PathfinderEpisodeChapters
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("displaySegments")]
    public PathfinderDisplaySegmentsContainer? DisplaySegments { get; init; }
}

public sealed class PathfinderDisplaySegmentsContainer
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("displaySegments")]
    public PathfinderDisplaySegmentsPage? DisplaySegments { get; init; }
}

public sealed class PathfinderDisplaySegmentsPage
{
    [JsonPropertyName("items")]
    public List<PathfinderDisplaySegment>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class PathfinderDisplaySegment
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("seekStart")]
    public PathfinderSegmentTime? SeekStart { get; init; }

    [JsonPropertyName("seekStop")]
    public PathfinderSegmentTime? SeekStop { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; init; }
}

public sealed class PathfinderSegmentTime
{
    [JsonPropertyName("milliseconds")]
    public long Milliseconds { get; init; }
}

[JsonSerializable(typeof(QueryNpvEpisodeChaptersResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class QueryNpvEpisodeChaptersJsonContext : JsonSerializerContext
{
}
