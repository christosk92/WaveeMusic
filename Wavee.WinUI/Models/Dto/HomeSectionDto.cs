using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// Represents a section in the Spotify home feed
/// </summary>
public sealed class HomeSectionDto
{
    [JsonPropertyName("data")]
    public SectionDataDto? Data { get; set; }

    [JsonPropertyName("sectionItems")]
    public SectionItemsContainerDto? SectionItems { get; set; }
}

/// <summary>
/// Base class for section data with polymorphic support
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "__typename")]
[JsonDerivedType(typeof(HomeGenericSectionDataDto), "HomeGenericSectionData")]
[JsonDerivedType(typeof(HomeShortsSectionDataDto), "HomeShortsSectionData")]
[JsonDerivedType(typeof(HomeRecentlyPlayedSectionDataDto), "HomeRecentlyPlayedSectionData")]
[JsonDerivedType(typeof(HomeFeedBaselineSectionDataDto), "HomeFeedBaselineSectionData")]
public abstract class SectionDataDto
{
}

/// <summary>
/// Generic section data with title and subtitle
/// </summary>
public sealed class HomeGenericSectionDataDto : SectionDataDto
{
    [JsonPropertyName("title")]
    public GreetingDto? Title { get; set; }

    [JsonPropertyName("subtitle")]
    public GreetingDto? Subtitle { get; set; }
}

/// <summary>
/// Shorts section data (minimal metadata)
/// </summary>
public sealed class HomeShortsSectionDataDto : SectionDataDto
{
}

/// <summary>
/// Recently played section data with title
/// </summary>
public sealed class HomeRecentlyPlayedSectionDataDto : SectionDataDto
{
    [JsonPropertyName("title")]
    public GreetingDto? Title { get; set; }
}

/// <summary>
/// Feed baseline section data with title (e.g., "Made for you")
/// </summary>
public sealed class HomeFeedBaselineSectionDataDto : SectionDataDto
{
    [JsonPropertyName("title")]
    public GreetingDto? Title { get; set; }
}
