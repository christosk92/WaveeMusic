using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed record SeoRecommendedEpisodesVariables(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("includeEpisodeContentRatingsV2")] bool IncludeEpisodeContentRatingsV2 = false);

[JsonSerializable(typeof(SeoRecommendedEpisodesVariables))]
internal partial class SeoRecommendedEpisodesVariablesJsonContext : JsonSerializerContext
{
}
