using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// Represents an artist from the Spotify API
/// </summary>
public sealed class ArtistDto
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("profile")]
    public ArtistProfileDto? Profile { get; set; }

    [JsonPropertyName("visuals")]
    public ArtistVisualsDto? Visuals { get; set; }

    [JsonPropertyName("genres")]
    public string[]? Genres { get; set; }

    [JsonPropertyName("popularity")]
    public int? Popularity { get; set; }
}

/// <summary>
/// Artist visuals (avatar images)
/// </summary>
public sealed class ArtistVisualsDto
{
    [JsonPropertyName("avatarImage")]
    public CoverArtDto? AvatarImage { get; set; }
}
