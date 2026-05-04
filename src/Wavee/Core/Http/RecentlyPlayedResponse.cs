using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http;

/// <summary>
/// Response from spclient /recently-played/v3/user/{userId}/recently-played
/// </summary>
public sealed class RecentlyPlayedResponse
{
    [JsonPropertyName("playContexts")]
    public List<RecentlyPlayedContext>? PlayContexts { get; init; }
}

public sealed class RecentlyPlayedContext
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("lastPlayedTime")]
    public long LastPlayedTime { get; init; }

    [JsonPropertyName("lastPlayedTrackUri")]
    public string? LastPlayedTrackUri { get; init; }
}

[JsonSerializable(typeof(RecentlyPlayedResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class RecentlyPlayedJsonContext : JsonSerializerContext;
