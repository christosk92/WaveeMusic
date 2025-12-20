using System.Text.Json.Serialization;

namespace Wavee.WinUI.Models.Dto;

/// <summary>
/// JSON serializer context for Native AOT compatibility.
/// Registers all DTO types used for deserializing Spotify home feed data.
/// </summary>
// Root and wrapper types
[JsonSerializable(typeof(HomeRootDto))]
[JsonSerializable(typeof(HomeDataDto))]
[JsonSerializable(typeof(HomeResponseDto))]
[JsonSerializable(typeof(GreetingDto))]
[JsonSerializable(typeof(SectionContainerDto))]
[JsonSerializable(typeof(SectionsDto))]
[JsonSerializable(typeof(HomeSectionDto))]
[JsonSerializable(typeof(SectionDataDto))]
[JsonSerializable(typeof(HomeGenericSectionDataDto))]
[JsonSerializable(typeof(HomeShortsSectionDataDto))]
[JsonSerializable(typeof(HomeRecentlyPlayedSectionDataDto))]
[JsonSerializable(typeof(HomeFeedBaselineSectionDataDto))]
[JsonSerializable(typeof(SectionItemsContainerDto))]
[JsonSerializable(typeof(SectionItemDto))]

// Content wrappers
[JsonSerializable(typeof(ContentWrapperDto))]
[JsonSerializable(typeof(PlaylistResponseWrapperDto))]
[JsonSerializable(typeof(AlbumResponseWrapperDto))]
[JsonSerializable(typeof(ArtistResponseWrapperDto))]
[JsonSerializable(typeof(ShowResponseWrapperDto))]
[JsonSerializable(typeof(UnknownTypeWrapperDto))]

// Content types
[JsonSerializable(typeof(AlbumDto))]
[JsonSerializable(typeof(PlaylistDto))]
[JsonSerializable(typeof(ArtistDto))]
[JsonSerializable(typeof(ShowDto))]
[JsonSerializable(typeof(EpisodeDto))]

// Supporting types
[JsonSerializable(typeof(ImageDto))]
[JsonSerializable(typeof(ImageItemDto))]
[JsonSerializable(typeof(ImageItemsContainerDto))]
[JsonSerializable(typeof(CoverArtDto))]
[JsonSerializable(typeof(ExtractedColorsDto))]
[JsonSerializable(typeof(ColorInfoDto))]
[JsonSerializable(typeof(ArtistItemDto))]
[JsonSerializable(typeof(ArtistItemsContainerDto))]
[JsonSerializable(typeof(ArtistProfileDto))]
[JsonSerializable(typeof(ArtistVisualsDto))]
[JsonSerializable(typeof(OwnerV2Dto))]
[JsonSerializable(typeof(UserDto))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class HomeJsonSerializerContext : JsonSerializerContext
{
}
