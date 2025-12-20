using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// Represents a podcast episode from the Spotify API
/// </summary>
public sealed class EpisodeDto
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("coverArt")]
    public CoverArtDto? CoverArt { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; set; }

    [JsonPropertyName("show")]
    public ShowDto? Show { get; set; }

    [JsonPropertyName("resumePosition")]
    public long? ResumePositionMs { get; set; }
}
