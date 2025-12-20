using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// Represents a podcast show from the Spotify API
/// </summary>
public sealed class ShowDto
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("coverArt")]
    public CoverArtDto? CoverArt { get; set; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    [JsonPropertyName("totalEpisodes")]
    public int? TotalEpisodes { get; set; }
}
