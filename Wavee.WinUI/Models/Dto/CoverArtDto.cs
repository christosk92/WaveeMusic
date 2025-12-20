using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// Represents cover art with images and extracted colors
/// </summary>
public sealed class CoverArtDto
{
    [JsonPropertyName("sources")]
    public ImageDto[]? Sources { get; set; }

    [JsonPropertyName("extractedColors")]
    public ExtractedColorsDto? ExtractedColors { get; set; }
}
