using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed class InternalLinkRecommenderShowResponse
{
    [JsonPropertyName("data")]
    public InternalLinkRecommenderShowData? Data { get; init; }
}

public sealed class InternalLinkRecommenderShowData
{
    [JsonPropertyName("seoRecommendedPodcast")]
    public InternalLinkRecommenderShowPage? SeoRecommendedPodcast { get; init; }
}

public sealed class InternalLinkRecommenderShowPage
{
    [JsonPropertyName("items")]
    public List<InternalLinkRecommenderShowItem>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int? TotalCount { get; init; }
}

public sealed class InternalLinkRecommenderShowItem
{
    [JsonPropertyName("data")]
    public InternalLinkRecommenderShowRef? Data { get; init; }
}

public sealed class InternalLinkRecommenderShowRef
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; init; }

    [JsonPropertyName("publisher")]
    public PathfinderShowPublisher? Publisher { get; init; }

    [JsonPropertyName("coverArt")]
    public PathfinderShowCoverArt? CoverArt { get; init; }
}

[JsonSerializable(typeof(InternalLinkRecommenderShowResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class InternalLinkRecommenderShowJsonContext : JsonSerializerContext
{
}
