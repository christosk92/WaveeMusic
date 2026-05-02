using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed class SeoRecommendedEpisodesResponse
{
    [JsonPropertyName("data")]
    public SeoRecommendedEpisodesData? Data { get; init; }
}

public sealed class SeoRecommendedEpisodesData
{
    [JsonPropertyName("seoRecommendedEpisode")]
    public SeoRecommendedEpisodePage? SeoRecommendedEpisode { get; init; }
}

public sealed class SeoRecommendedEpisodePage
{
    [JsonPropertyName("items")]
    public List<SeoRecommendedEpisodeItem>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int? TotalCount { get; init; }
}

public sealed class SeoRecommendedEpisodeItem
{
    [JsonPropertyName("data")]
    public PathfinderEpisode? Data { get; init; }
}

[JsonSerializable(typeof(SeoRecommendedEpisodesResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class SeoRecommendedEpisodesJsonContext : JsonSerializerContext
{
}
