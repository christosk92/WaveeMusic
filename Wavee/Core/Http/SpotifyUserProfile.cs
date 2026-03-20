using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http;

/// <summary>
/// JSON serialization context for Spotify user profile types (AOT compatible).
/// </summary>
[JsonSerializable(typeof(SpotifyUserProfile))]
[JsonSerializable(typeof(SpotifyFollowingResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class SpotifyUserProfileJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Response from spclient /user-profile-view/v3/profile/{username}/following
/// </summary>
public sealed record SpotifyFollowingResponse
{
    [JsonPropertyName("profiles")]
    public IReadOnlyList<SpotifyProfileArtist>? Profiles { get; init; }
}

/// <summary>
/// User profile from spclient /user-profile-view/v3/profile/{username}.
/// Also handles public Web API /v1/me response shape.
/// </summary>
public sealed record SpotifyUserProfile
{
    // === spclient profile-view fields ===

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("following_count")]
    public int? FollowingCount { get; init; }

    [JsonPropertyName("recently_played_artists")]
    public IReadOnlyList<SpotifyProfileArtist>? RecentlyPlayedArtists { get; init; }

    [JsonPropertyName("public_playlists")]
    public IReadOnlyList<SpotifyProfilePlaylist>? PublicPlaylists { get; init; }

    [JsonPropertyName("total_public_playlists_count")]
    public int? TotalPublicPlaylistsCount { get; init; }

    [JsonPropertyName("is_current_user")]
    public bool? IsCurrentUser { get; init; }

    [JsonPropertyName("has_spotify_name")]
    public bool? HasSpotifyName { get; init; }

    [JsonPropertyName("has_spotify_image")]
    public bool? HasSpotifyImage { get; init; }

    [JsonPropertyName("color")]
    public int? Color { get; init; }

    [JsonPropertyName("allow_follows")]
    public bool? AllowFollows { get; init; }

    [JsonPropertyName("show_follows")]
    public bool? ShowFollows { get; init; }

    // === Public Web API /v1/me fields ===

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("country")]
    public string? Country { get; init; }

    [JsonPropertyName("product")]
    public string? Product { get; init; }

    [JsonPropertyName("images")]
    public IReadOnlyList<SpotifyImage>? Images { get; init; }

    // === Computed helpers ===

    /// <summary>
    /// Gets the best available display name from either response format.
    /// spclient uses "name", Web API uses "display_name".
    /// </summary>
    [JsonIgnore]
    public string? EffectiveDisplayName => Name ?? DisplayName;

    /// <summary>
    /// Gets the best available profile image URL from either response format.
    /// spclient uses "image_url", Web API uses "images[0].url".
    /// </summary>
    [JsonIgnore]
    public string? EffectiveImageUrl => ImageUrl ?? (Images?.Count > 0 ? Images[0].Url : null);
}

/// <summary>
/// Artist in the user profile's recently played list.
/// </summary>
public sealed record SpotifyProfileArtist
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("followers_count")]
    public int? FollowersCount { get; init; }

    [JsonPropertyName("is_following")]
    public bool? IsFollowing { get; init; }
}

/// <summary>
/// Playlist in the user profile's public playlists list.
/// </summary>
public sealed record SpotifyProfilePlaylist
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("owner_name")]
    public string? OwnerName { get; init; }

    [JsonPropertyName("owner_uri")]
    public string? OwnerUri { get; init; }

    [JsonPropertyName("is_following")]
    public bool? IsFollowing { get; init; }
}

/// <summary>
/// Spotify image with optional dimensions (from public Web API).
/// </summary>
public sealed record SpotifyImage
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }
}
