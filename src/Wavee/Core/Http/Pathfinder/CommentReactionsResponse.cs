using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed class CommentReactionsResponse
{
    [JsonPropertyName("data")]
    public CommentReactionsData? Data { get; init; }
}

public sealed class CommentReactionsData
{
    [JsonPropertyName("commentReactions")]
    public List<CommentReactionPage>? CommentReactions { get; init; }
}

public sealed class CommentReactionPage
{
    [JsonPropertyName("commentUri")]
    public string? CommentUri { get; init; }

    [JsonPropertyName("items")]
    public List<CommentReactionItem>? Items { get; init; }

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; init; }

    [JsonPropertyName("reactionCounts")]
    public List<CommentReactionCount>? ReactionCounts { get; init; }
}

public sealed class CommentReactionItem
{
    [JsonPropertyName("author")]
    public EntityCommentAuthorWrapper? Author { get; init; }

    [JsonPropertyName("createDate")]
    public EntityCommentDate? CreateDate { get; init; }

    [JsonPropertyName("reactionUnicode")]
    public string? ReactionUnicode { get; init; }
}

public sealed class CommentReactionCount
{
    [JsonPropertyName("numberOfReactions")]
    public int NumberOfReactions { get; init; }

    [JsonPropertyName("reactionUnicode")]
    public string? ReactionUnicode { get; init; }
}

[JsonSerializable(typeof(CommentReactionsResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class CommentReactionsJsonContext : JsonSerializerContext
{
}
