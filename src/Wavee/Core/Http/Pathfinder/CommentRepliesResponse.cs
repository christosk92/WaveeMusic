using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed class CommentRepliesResponse
{
    [JsonPropertyName("data")]
    public CommentRepliesData? Data { get; init; }
}

public sealed class CommentRepliesData
{
    [JsonPropertyName("commentReplies")]
    public List<CommentReplyPage>? CommentReplies { get; init; }
}

public sealed class CommentReplyPage
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("items")]
    public List<CommentReplyItem>? Items { get; init; }

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; init; }

    [JsonPropertyName("totalCount")]
    public int? TotalCount { get; init; }
}

public sealed class CommentReplyItem
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("replyString")]
    public string? ReplyString { get; init; }

    [JsonPropertyName("author")]
    public EntityCommentAuthorWrapper? Author { get; init; }

    [JsonPropertyName("createDate")]
    public EntityCommentDate? CreateDate { get; init; }

    [JsonPropertyName("isPendingReview")]
    public bool IsPendingReview { get; init; }

    [JsonPropertyName("isSensitive")]
    public bool IsSensitive { get; init; }

    [JsonPropertyName("reactionsMetadata")]
    public EntityCommentReactionsMetadata? ReactionsMetadata { get; init; }
}

[JsonSerializable(typeof(CommentRepliesResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class CommentRepliesJsonContext : JsonSerializerContext
{
}
