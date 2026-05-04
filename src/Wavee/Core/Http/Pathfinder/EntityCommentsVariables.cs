using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed record EntityCommentsVariables(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("token")] string? Token = null);

[JsonSerializable(typeof(EntityCommentsVariables))]
internal partial class EntityCommentsVariablesJsonContext : JsonSerializerContext
{
}
