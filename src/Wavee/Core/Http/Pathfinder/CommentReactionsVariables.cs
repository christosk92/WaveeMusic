using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed record CommentReactionsVariables(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("token")] string? Token = null,
    [property: JsonPropertyName("reactionUnicode")] string? ReactionUnicode = null);

[JsonSerializable(typeof(CommentReactionsVariables))]
internal partial class CommentReactionsVariablesJsonContext : JsonSerializerContext
{
}
