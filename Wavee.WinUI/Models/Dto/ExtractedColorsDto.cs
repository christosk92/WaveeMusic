using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// Represents extracted colors from album/playlist artwork
/// </summary>
public sealed class ExtractedColorsDto
{
    [JsonPropertyName("colorDark")]
    public ColorInfoDto? ColorDark { get; set; }

    [JsonPropertyName("colorLight")]
    public ColorInfoDto? ColorLight { get; set; }

    [JsonPropertyName("colorRaw")]
    public ColorInfoDto? ColorRaw { get; set; }
}

/// <summary>
/// Represents a single color value
/// </summary>
public sealed class ColorInfoDto
{
    [JsonPropertyName("hex")]
    public string Hex { get; set; } = string.Empty;

    [JsonPropertyName("isFallback")]
    public bool IsFallback { get; set; }
}
