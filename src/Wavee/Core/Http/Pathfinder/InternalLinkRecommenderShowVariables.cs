using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed record InternalLinkRecommenderShowVariables(
    [property: JsonPropertyName("uri")] string Uri);

[JsonSerializable(typeof(InternalLinkRecommenderShowVariables))]
internal partial class InternalLinkRecommenderShowVariablesJsonContext : JsonSerializerContext
{
}
