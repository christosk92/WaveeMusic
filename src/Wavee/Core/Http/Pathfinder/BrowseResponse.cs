using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed class BrowsePageVariables
{
    public BrowsePagination PagePagination { get; init; } = new();
    public BrowsePagination SectionPagination { get; init; } = new();
    public string Uri { get; init; } = "";
    public string BrowseEndUserIntegration { get; init; } = "INTEGRATION_WEB_PLAYER";
    public bool IncludeEpisodeContentRatingsV2 { get; init; }
}

public sealed class BrowseSectionVariables
{
    public BrowsePagination Pagination { get; init; } = new();
    public string Uri { get; init; } = "";
    public string BrowseEndUserIntegration { get; init; } = "INTEGRATION_WEB_PLAYER";
    public bool IncludeEpisodeContentRatingsV2 { get; init; }
}

public sealed class BrowsePagination
{
    public int Offset { get; init; }
    public int Limit { get; init; }
}

public sealed class BrowsePageResponse
{
    [JsonPropertyName("data")]
    public BrowsePageResponseData? Data { get; init; }
}

public sealed class BrowsePageResponseData
{
    [JsonPropertyName("browse")]
    public PathfinderBrowseContainer? Browse { get; init; }
}

public sealed class BrowseSectionResponse
{
    [JsonPropertyName("data")]
    public BrowseSectionResponseData? Data { get; init; }
}

public sealed class BrowseSectionResponseData
{
    [JsonPropertyName("browseSection")]
    public PathfinderBrowseSection? BrowseSection { get; init; }
}

public sealed class PathfinderBrowseContainer
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("header")]
    public PathfinderBrowseHeader? Header { get; init; }

    [JsonPropertyName("sections")]
    public PathfinderBrowseSectionsPage? Sections { get; init; }

    [JsonPropertyName("data")]
    public PathfinderBrowseContainerData? Data { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

public sealed class PathfinderBrowseHeader
{
    [JsonPropertyName("color")]
    public PathfinderBrowseColor? Color { get; init; }

    [JsonPropertyName("subtitle")]
    public PathfinderBrowseLabel? Subtitle { get; init; }

    [JsonPropertyName("title")]
    public PathfinderBrowseLabel? Title { get; init; }
}

public sealed class PathfinderBrowseContainerData
{
    [JsonPropertyName("cardRepresentation")]
    public PathfinderBrowseCardRepresentation? CardRepresentation { get; init; }
}

public sealed class PathfinderBrowseSectionsPage
{
    [JsonPropertyName("items")]
    public List<PathfinderBrowseSection>? Items { get; init; }

    [JsonPropertyName("pagingInfo")]
    public PathfinderBrowsePagingInfo? PagingInfo { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class PathfinderBrowseSection
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("data")]
    public PathfinderBrowseSectionData? Data { get; init; }

    [JsonPropertyName("sectionItems")]
    public PathfinderBrowseSectionItemsPage? SectionItems { get; init; }

    [JsonPropertyName("targetLocation")]
    public string? TargetLocation { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

public sealed class PathfinderBrowseSectionData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("subtitle")]
    public PathfinderBrowseLabel? Subtitle { get; init; }

    [JsonPropertyName("title")]
    public PathfinderBrowseLabel? Title { get; init; }
}

public sealed class PathfinderBrowseSectionItemsPage
{
    [JsonPropertyName("items")]
    public List<PathfinderBrowseSectionItem>? Items { get; init; }

    [JsonPropertyName("pagingInfo")]
    public PathfinderBrowsePagingInfo? PagingInfo { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class PathfinderBrowseSectionItem
{
    [JsonPropertyName("content")]
    public PathfinderBrowseContentWrapper? Content { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

public sealed class PathfinderBrowseContentWrapper
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("data")]
    public PathfinderBrowseContentData? Data { get; init; }
}

public sealed class PathfinderBrowseContentData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("coverArt")]
    public PathfinderBrowseCoverArt? CoverArt { get; init; }

    [JsonPropertyName("data")]
    public PathfinderBrowseContainerData? Data { get; init; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("publisher")]
    public PathfinderBrowsePublisher? Publisher { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    // ── Playlist / album fields (for rendering shelves like Home does) ──

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Playlist images. Album cover lives on <see cref="CoverArt"/>; playlists use this nested shape.</summary>
    [JsonPropertyName("images")]
    public PathfinderBrowseImageList? Images { get; init; }

    /// <summary>Playlist owner. Drives the "by Spotify" subtitle on playlist cards.</summary>
    [JsonPropertyName("ownerV2")]
    public PathfinderBrowseOwnerWrapper? OwnerV2 { get; init; }

    /// <summary>Album artists. Drives the artist-name subtitle on album cards.</summary>
    [JsonPropertyName("artists")]
    public PathfinderBrowseArtistList? Artists { get; init; }
}

public sealed class PathfinderBrowseImageList
{
    [JsonPropertyName("items")]
    public List<PathfinderBrowseImageEntry>? Items { get; init; }
}

public sealed class PathfinderBrowseImageEntry
{
    [JsonPropertyName("sources")]
    public List<PathfinderBrowseImageSource>? Sources { get; init; }
}

public sealed class PathfinderBrowseOwnerWrapper
{
    [JsonPropertyName("data")]
    public PathfinderBrowseOwner? Data { get; init; }
}

public sealed class PathfinderBrowseOwner
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class PathfinderBrowseArtistList
{
    [JsonPropertyName("items")]
    public List<PathfinderBrowseArtist>? Items { get; init; }
}

public sealed class PathfinderBrowseArtist
{
    [JsonPropertyName("profile")]
    public PathfinderBrowseArtistProfile? Profile { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

public sealed class PathfinderBrowseArtistProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class PathfinderBrowseCardRepresentation
{
    [JsonPropertyName("artwork")]
    public PathfinderBrowseCoverArt? Artwork { get; init; }

    [JsonPropertyName("backgroundColor")]
    public PathfinderBrowseColor? BackgroundColor { get; init; }

    [JsonPropertyName("title")]
    public PathfinderBrowseLabel? Title { get; init; }
}

public sealed class PathfinderBrowseCoverArt
{
    [JsonPropertyName("sources")]
    public List<PathfinderBrowseImageSource>? Sources { get; init; }
}

public sealed class PathfinderBrowseImageSource
{
    [JsonPropertyName("height")]
    public int? Height { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }
}

public sealed class PathfinderBrowsePublisher
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class PathfinderBrowseLabel
{
    [JsonPropertyName("transformedLabel")]
    public string? TransformedLabel { get; init; }

    [JsonPropertyName("translatedBaseText")]
    public string? TranslatedBaseText { get; init; }
}

public sealed class PathfinderBrowseColor
{
    [JsonPropertyName("hex")]
    public string? Hex { get; init; }
}

public sealed class PathfinderBrowsePagingInfo
{
    [JsonPropertyName("nextOffset")]
    public int? NextOffset { get; init; }
}

[JsonSerializable(typeof(BrowsePageResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class BrowsePageJsonContext : JsonSerializerContext
{
}

[JsonSerializable(typeof(BrowseSectionResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class BrowseSectionJsonContext : JsonSerializerContext
{
}

[JsonSerializable(typeof(BrowsePageVariables))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class BrowsePageVariablesJsonContext : JsonSerializerContext
{
}

[JsonSerializable(typeof(BrowseSectionVariables))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class BrowseSectionVariablesJsonContext : JsonSerializerContext
{
}
