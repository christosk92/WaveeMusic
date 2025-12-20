using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// Represents an image item with sources and extracted colors
/// </summary>
public sealed class ImageItemDto
{
    [JsonPropertyName("sources")]
    public ImageDto[]? Sources { get; set; }

    [JsonPropertyName("extractedColors")]
    public ExtractedColorsDto? ExtractedColors { get; set; }
}

/// <summary>
/// Container for image items
/// </summary>
public sealed class ImageItemsContainerDto
{
    [JsonPropertyName("items")]
    public ImageItemDto[]? Items { get; set; }
}
