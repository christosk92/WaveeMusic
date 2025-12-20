using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// Represents an album from the Spotify API
/// </summary>
public sealed class AlbumDto
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("coverArt")]
    public CoverArtDto? CoverArt { get; set; }

    [JsonPropertyName("artists")]
    public ArtistItemsContainerDto? Artists { get; set; }

    [JsonPropertyName("albumType")]
    public string? AlbumType { get; set; }

    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("totalTracks")]
    public int? TotalTracks { get; set; }
}
