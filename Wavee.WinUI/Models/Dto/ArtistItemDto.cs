using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// Represents an artist reference in an album or track
/// </summary>
public sealed class ArtistItemDto
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("profile")]
    public ArtistProfileDto? Profile { get; set; }
}

/// <summary>
/// Container for artist items
/// </summary>
public sealed class ArtistItemsContainerDto
{
    [JsonPropertyName("items")]
    public ArtistItemDto[]? Items { get; set; }
}

/// <summary>
/// Artist profile information
/// </summary>
public sealed class ArtistProfileDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
