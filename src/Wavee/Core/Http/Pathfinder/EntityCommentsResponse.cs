using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed class EntityCommentsResponse
{
    [JsonPropertyName("data")]
    public EntityCommentsData? Data { get; init; }
}

public sealed class EntityCommentsData
{
    [JsonPropertyName("comments")]
    public List<EntityCommentPage>? Comments { get; init; }
}

public sealed class EntityCommentPage
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("eligibilityStatus")]
    public string? EligibilityStatus { get; init; }

    [JsonPropertyName("entityUri")]
    public string? EntityUri { get; init; }

    [JsonPropertyName("items")]
    public List<EntityCommentItem>? Items { get; init; }

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; init; }

    [JsonPropertyName("totalCount")]
    public int? TotalCount { get; init; }
}

public sealed class EntityCommentItem
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("commentString")]
    public string? CommentString { get; init; }

    [JsonPropertyName("author")]
    public EntityCommentAuthorWrapper? Author { get; init; }

    [JsonPropertyName("createDate")]
    public EntityCommentDate? CreateDate { get; init; }

    [JsonPropertyName("isPinned")]
    public bool IsPinned { get; init; }

    [JsonPropertyName("isSensitive")]
    public bool IsSensitive { get; init; }

    [JsonPropertyName("numberOfRepliesWithThreads")]
    public int NumberOfRepliesWithThreads { get; init; }

    [JsonPropertyName("reactionsMetadata")]
    public EntityCommentReactionsMetadata? ReactionsMetadata { get; init; }

    [JsonPropertyName("topRepliesAuthors")]
    public List<EntityCommentAuthorWrapper>? TopRepliesAuthors { get; init; }
}

public sealed class EntityCommentAuthorWrapper
{
    [JsonPropertyName("data")]
    public EntityCommentAuthor? Data { get; init; }
}

public sealed class EntityCommentAuthor
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("avatar")]
    public EntityCommentAvatar? Avatar { get; init; }
}

public sealed class EntityCommentAvatar
{
    [JsonPropertyName("sources")]
    public List<ArtistImageSource>? Sources { get; init; }
}

public sealed class EntityCommentDate
{
    [JsonPropertyName("isoString")]
    public string? IsoString { get; init; }

    [JsonPropertyName("precision")]
    public string? Precision { get; init; }
}

public sealed class EntityCommentReactionsMetadata
{
    [JsonPropertyName("numberOfReactions")]
    public int NumberOfReactions { get; init; }

    [JsonPropertyName("usersReactionUnicode")]
    public string? UsersReactionUnicode { get; init; }

    [JsonPropertyName("topReactionUnicode")]
    public List<string>? TopReactionUnicode { get; init; }
}

[JsonSerializable(typeof(EntityCommentsResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class EntityCommentsJsonContext : JsonSerializerContext
{
}
