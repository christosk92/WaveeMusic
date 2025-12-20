using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// Container for home feed sections
/// </summary>
public sealed class SectionContainerDto
{
    [JsonPropertyName("sections")]
    public SectionsDto? Sections { get; set; }
}

/// <summary>
/// Sections wrapper with items array
/// </summary>
public sealed class SectionsDto
{
    [JsonPropertyName("items")]
    public HomeSectionDto[]? Items { get; set; }
}
