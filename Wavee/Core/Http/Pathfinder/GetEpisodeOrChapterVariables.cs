using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed record GetEpisodeOrChapterVariables(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("includeEpisodeContentRatingsV2")] bool IncludeEpisodeContentRatingsV2 = false);

[JsonSerializable(typeof(GetEpisodeOrChapterVariables))]
internal partial class GetEpisodeOrChapterVariablesJsonContext : JsonSerializerContext
{
}
