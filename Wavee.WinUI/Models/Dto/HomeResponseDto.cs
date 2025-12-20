using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// Root response wrapper
/// </summary>
public sealed class HomeRootDto
{
    [JsonPropertyName("data")]
    public HomeDataDto? Data { get; set; }
}

/// <summary>
/// Data wrapper with home payload
/// </summary>
public sealed class HomeDataDto
{
    [JsonPropertyName("home")]
    public HomeResponseDto? Home { get; set; }
}

/// <summary>
/// Represents the home feed payload
/// </summary>
public sealed class HomeResponseDto
{
    [JsonPropertyName("greeting")]
    public GreetingDto? Greeting { get; set; }

    [JsonPropertyName("sectionContainer")]
    public SectionContainerDto? SectionContainer { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}
