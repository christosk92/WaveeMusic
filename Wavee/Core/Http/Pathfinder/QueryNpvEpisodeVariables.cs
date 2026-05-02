using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed record QueryNpvEpisodeVariables(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("numberOfChapters")] int NumberOfChapters = 10,
    [property: JsonPropertyName("includeEpisodeContentRatingsV2")] bool IncludeEpisodeContentRatingsV2 = false);

[JsonSerializable(typeof(QueryNpvEpisodeVariables))]
internal partial class QueryNpvEpisodeVariablesJsonContext : JsonSerializerContext
{
}
