using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// Root response from Pathfinder userTopContent query.
/// </summary>
public sealed class UserTopContentResponse
{
    [JsonPropertyName("data")]
    public UserTopContentData? Data { get; init; }
}

public sealed class UserTopContentData
{
    [JsonPropertyName("me")]
    public UserTopContentMe? Me { get; init; }
}

public sealed class UserTopContentMe
{
    [JsonPropertyName("profile")]
    public UserTopContentProfile? Profile { get; init; }
}

public sealed class UserTopContentProfile
{
    [JsonPropertyName("topArtists")]
    public TopArtistsContainer? TopArtists { get; init; }

    [JsonPropertyName("topTracks")]
    public TopTracksContainer? TopTracks { get; init; }
}

public sealed class TopArtistsContainer
{
    [JsonPropertyName("items")]
    public List<TopArtistItem>? Items { get; init; }
}

public sealed class TopArtistItem
{
    [JsonPropertyName("data")]
    public TopArtistData? Data { get; init; }
}

public sealed class TopArtistData
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("profile")]
    public TopArtistProfile? Profile { get; init; }

    [JsonPropertyName("visuals")]
    public TopArtistVisuals? Visuals { get; init; }
}

public sealed class TopArtistProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class TopArtistVisuals
{
    [JsonPropertyName("avatarImage")]
    public TopArtistAvatarImage? AvatarImage { get; init; }
}

public sealed class TopArtistAvatarImage
{
    [JsonPropertyName("sources")]
    public List<ImageSource>? Sources { get; init; }
}

public sealed class TopTracksContainer
{
    [JsonPropertyName("items")]
    public List<TopTrackItem>? Items { get; init; }
}

public sealed class TopTrackItem
{
    [JsonPropertyName("data")]
    public TopTrackData? Data { get; init; }
}

public sealed class TopTrackData
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("duration")]
    public TopTrackDuration? Duration { get; init; }

    [JsonPropertyName("albumOfTrack")]
    public TopTrackAlbum? AlbumOfTrack { get; init; }

    [JsonPropertyName("artists")]
    public TopTrackArtistList? Artists { get; init; }

    [JsonPropertyName("contentRating")]
    public TopTrackContentRating? ContentRating { get; init; }

    [JsonPropertyName("saved")]
    public bool? Saved { get; init; }

    // --- Display helpers for UI binding (not serialized) ---

    /// <summary>First artist name, or empty string.</summary>
    [JsonIgnore]
    public string DisplayArtistName =>
        Artists?.Items?.FirstOrDefault()?.Profile?.Name ?? string.Empty;

    /// <summary>First album cover-art URL, or null.</summary>
    [JsonIgnore]
    public string? DisplayAlbumArtUrl =>
        AlbumOfTrack?.CoverArt?.Sources?.FirstOrDefault()?.Url;

    /// <summary>Duration formatted as m:ss.</summary>
    [JsonIgnore]
    public string DisplayDuration
    {
        get
        {
            if (Duration?.TotalMilliseconds is not > 0)
                return string.Empty;
            var ts = TimeSpan.FromMilliseconds(Duration.TotalMilliseconds.Value);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }
    }
}

public sealed class TopTrackDuration
{
    [JsonPropertyName("totalMilliseconds")]
    public long? TotalMilliseconds { get; init; }
}

public sealed class TopTrackAlbum
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("coverArt")]
    public TopTrackCoverArt? CoverArt { get; init; }
}

public sealed class TopTrackCoverArt
{
    [JsonPropertyName("sources")]
    public List<ImageSource>? Sources { get; init; }
}

public sealed class TopTrackArtistList
{
    [JsonPropertyName("items")]
    public List<TopTrackArtistItem>? Items { get; init; }
}

public sealed class TopTrackArtistItem
{
    [JsonPropertyName("profile")]
    public TopTrackArtistProfile? Profile { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

public sealed class TopTrackArtistProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class TopTrackContentRating
{
    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

/// <summary>
/// JSON serializer context for UserTopContent response (AOT compatible).
/// </summary>
[JsonSerializable(typeof(UserTopContentResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class UserTopContentJsonContext : JsonSerializerContext
{
}
