using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed record QueryShowMetadataV2Variables(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("includeContentCapabilityTrait")] bool IncludeContentCapabilityTrait = true,
    [property: JsonPropertyName("includeEpisodeContentRatingsV2")] bool IncludeEpisodeContentRatingsV2 = false);

[JsonSerializable(typeof(QueryShowMetadataV2Variables))]
internal partial class QueryShowMetadataV2VariablesJsonContext : JsonSerializerContext
{
}
