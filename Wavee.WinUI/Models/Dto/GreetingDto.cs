using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// Represents the greeting in the home feed
/// </summary>
public sealed class GreetingDto
{
    [JsonPropertyName("transformedLabel")]
    public string? TransformedLabel { get; set; }

    [JsonPropertyName("translatedBaseText")]
    public string? TranslatedBaseText { get; set; }
}
