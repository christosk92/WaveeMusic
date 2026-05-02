using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed record CommentRepliesVariables(
    [property: JsonPropertyName("commentUri")] string CommentUri,
    [property: JsonPropertyName("pageToken")] string? PageToken = null);

[JsonSerializable(typeof(CommentRepliesVariables))]
internal partial class CommentRepliesVariablesJsonContext : JsonSerializerContext
{
}
