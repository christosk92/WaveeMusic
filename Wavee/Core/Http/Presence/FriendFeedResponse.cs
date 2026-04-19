using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Presence;

/// <summary>
/// Response from spclient /presence-view/v2/init-friend-feed/{connectionId}.
/// Contains the latest listening activity for each friend (one entry per friend).
/// </summary>
public sealed class FriendFeedResponse
{
    [JsonPropertyName("friends")]
    public List<FriendFeedEntry>? Friends { get; init; }
}

public sealed class FriendFeedEntry
{
    /// <summary>Activity timestamp in Unix milliseconds.</summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    [JsonPropertyName("user")]
    public FriendUser? User { get; init; }

    [JsonPropertyName("track")]
    public FriendTrack? Track { get; init; }
}

public sealed class FriendUser
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; init; }
}

public sealed class FriendTrack
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("album")]
    public FriendAlbum? Album { get; init; }

    [JsonPropertyName("artist")]
    public FriendArtist? Artist { get; init; }

    [JsonPropertyName("context")]
    public FriendContext? Context { get; init; }
}

public sealed class FriendAlbum
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class FriendArtist
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class FriendContext
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("index")]
    public int? Index { get; init; }
}

[JsonSerializable(typeof(FriendFeedResponse))]
[JsonSerializable(typeof(FriendFeedEntry))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class FriendFeedJsonContext : JsonSerializerContext;
