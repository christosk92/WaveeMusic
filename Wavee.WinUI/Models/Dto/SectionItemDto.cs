using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// Represents an item within a home feed section
/// </summary>
public sealed class SectionItemDto
{
    [JsonPropertyName("content")]
    public ContentWrapperDto? Content { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

/// <summary>
/// Container for section items
/// </summary>
public sealed class SectionItemsContainerDto
{
    [JsonPropertyName("items")]
    public SectionItemDto[]? Items { get; set; }
}
