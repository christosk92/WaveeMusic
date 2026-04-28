using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// ── Root ──

public sealed class HomeResponse
{
    [JsonPropertyName("data")]
    public HomeResponseData? Data { get; set; }

    [JsonIgnore]
    public string? RawJson { get; set; }
}

public sealed class HomeResponseData
{
    [JsonPropertyName("home")]
    public HomePayload? Home { get; set; }
}

public sealed class HomePayload
{
    [JsonPropertyName("greeting")]
    public HomeGreeting? Greeting { get; set; }

    [JsonPropertyName("sectionContainer")]
    public HomeSectionContainer? SectionContainer { get; set; }

    [JsonPropertyName("homeChips")]
    public List<HomeChip>? HomeChips { get; set; }
}

public sealed class HomeGreeting
{
    [JsonPropertyName("transformedLabel")]
    public string? TransformedLabel { get; set; }
}

// ── Sections ──

public sealed class HomeSectionContainer
{
    [JsonPropertyName("sections")]
    public HomeSectionsPage? Sections { get; set; }
}

public sealed class HomeSectionsPage
{
    [JsonPropertyName("items")]
    public List<HomeSectionEntry>? Items { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

public sealed class HomeSectionEntry
{
    [JsonPropertyName("data")]
    public HomeSectionData? Data { get; set; }

    [JsonPropertyName("sectionItems")]
    public HomeSectionItemsPage? SectionItems { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}

/// <summary>
/// Section metadata. The __typename field determines the layout.
/// </summary>
public sealed class HomeSectionData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("title")]
    public HomeLabel? Title { get; set; }

    [JsonPropertyName("subtitle")]
    public HomeLabel? Subtitle { get; set; }

    [JsonPropertyName("headerEntity")]
    public HomeItemContent? HeaderEntity { get; set; }
}

public sealed class HomeLabel
{
    [JsonPropertyName("transformedLabel")]
    public string? TransformedLabel { get; set; }

    [JsonPropertyName("translatedBaseText")]
    public string? TranslatedBaseText { get; set; }
}

// ── Section Items ──

public sealed class HomeSectionItemsPage
{
    [JsonPropertyName("items")]
    public List<HomeSectionItemEntry>? Items { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

public sealed class HomeSectionItemEntry
{
    // V1 format (old API)
    [JsonPropertyName("content")]
    public HomeItemContent? Content { get; set; }

    // V2 format (new entity/trait API)
    [JsonPropertyName("entity")]
    public HomeEntityWrapper? Entity { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    [JsonPropertyName("formatListAttributes")]
    public List<HomeFormatListAttribute>? FormatListAttributes { get; set; }

    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}

// ── V2 Entity/Trait models ──

public sealed class HomeFormatListAttribute
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

public sealed class HomeEntityWrapper
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("_uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("data")]
    public HomeEntityData? Data { get; set; }
}

public sealed class HomeEntityData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("identityTrait")]
    public HomeIdentityTrait? IdentityTrait { get; set; }

    [JsonPropertyName("visualIdentityTrait")]
    public HomeVisualIdentityTrait? VisualIdentityTrait { get; set; }

    [JsonPropertyName("entityTypeTrait")]
    public HomeEntityTypeTrait? EntityTypeTrait { get; set; }

    [JsonPropertyName("consumptionExperienceTrait")]
    public JsonElement? ConsumptionExperienceTrait { get; set; }

    [JsonPropertyName("typedEntity")]
    public HomeTypedEntity? TypedEntity { get; set; }

    [JsonPropertyName("sharingInfo")]
    public JsonElement? SharingInfo { get; set; }

    /// <summary>
    /// V2 episode payload — populated when the wrapper's typename matches
    /// <c>EpisodeOrChapterResponseWrapper</c>. Carries duration, played-state,
    /// release date, mediaTypes, and the nested <c>podcastV2</c> show pointer.
    /// </summary>
    [JsonPropertyName("episode")]
    public HomeEpisodeData? Episode { get; set; }
}

public sealed class HomeTypedEntity
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }
}

public sealed class HomeIdentityTrait
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("contributors")]
    public HomeContributors? Contributors { get; set; }

    [JsonPropertyName("contentHierarchyParent")]
    public JsonElement? ContentHierarchyParent { get; set; }
}

public sealed class HomeContributors
{
    [JsonPropertyName("items")]
    public List<HomeContributorItem>? Items { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

public sealed class HomeContributorItem
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}

public sealed class HomeEntityTypeTrait
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public sealed class HomeVisualIdentityTrait
{
    [JsonPropertyName("squareCoverImage")]
    public HomeSquareCoverImage? SquareCoverImage { get; set; }
}

public sealed class HomeSquareCoverImage
{
    [JsonPropertyName("image")]
    public HomeImageV2Wrapper? Image { get; set; }

    [JsonPropertyName("originalInstances")]
    public List<HomeOriginalInstance>? OriginalInstances { get; set; }
}

public sealed class HomeImageV2Wrapper
{
    [JsonPropertyName("data")]
    public HomeImageV2Data? Data { get; set; }
}

public sealed class HomeImageV2Data
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("sources")]
    public List<HomeImageV2Source>? Sources { get; set; }
}

public sealed class HomeImageV2Source
{
    [JsonPropertyName("imageFormat")]
    public string? ImageFormat { get; set; }

    [JsonPropertyName("maxHeight")]
    public int? MaxHeight { get; set; }

    [JsonPropertyName("maxWidth")]
    public int? MaxWidth { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class HomeOriginalInstance
{
    [JsonPropertyName("flatFile")]
    public HomeFlatFile? FlatFile { get; set; }

    [JsonPropertyName("size")]
    public string? Size { get; set; }
}

public sealed class HomeFlatFile
{
    [JsonPropertyName("cdnUrl")]
    public string? CdnUrl { get; set; }
}

/// <summary>
/// Polymorphic content wrapper. The __typename determines the actual content type.
/// The "data" field contains the typed payload (artist, playlist, album, etc.).
/// </summary>
public sealed class HomeItemContent
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    /// <summary>
    /// The inner data payload. Deserialized as JsonElement because it varies by __typename.
    /// Use the helper methods to extract typed data.
    /// </summary>
    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

// ── Extracted typed data helpers ──

/// <summary>
/// Provides extraction methods for the polymorphic HomeItemContent.Data field.
/// </summary>
public static class HomeItemContentExtensions
{
    public static HomeArtistData? GetArtistData(this HomeItemContent content)
    {
        if (content.Data is not { } el || el.ValueKind == JsonValueKind.Null) return null;
        return JsonSerializer.Deserialize(el, HomeJsonContext.Default.HomeArtistData);
    }

    public static HomePlaylistData? GetPlaylistData(this HomeItemContent content)
    {
        if (content.Data is not { } el || el.ValueKind == JsonValueKind.Null) return null;
        return JsonSerializer.Deserialize(el, HomeJsonContext.Default.HomePlaylistData);
    }

    public static HomeAlbumData? GetAlbumData(this HomeItemContent content)
    {
        if (content.Data is not { } el || el.ValueKind == JsonValueKind.Null) return null;
        return JsonSerializer.Deserialize(el, HomeJsonContext.Default.HomeAlbumData);
    }

    public static HomePodcastData? GetPodcastData(this HomeItemContent content)
    {
        if (content.Data is not { } el || el.ValueKind == JsonValueKind.Null) return null;
        return JsonSerializer.Deserialize(el, HomeJsonContext.Default.HomePodcastData);
    }

    public static HomeEpisodeData? GetEpisodeData(this HomeItemContent content)
    {
        if (content.Data is not { } el || el.ValueKind == JsonValueKind.Null) return null;
        return JsonSerializer.Deserialize(el, HomeJsonContext.Default.HomeEpisodeData);
    }
}

// ── Artist ──

public sealed class HomeArtistData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("profile")]
    public HomeArtistProfile? Profile { get; set; }

    [JsonPropertyName("visuals")]
    public HomeArtistVisuals? Visuals { get; set; }
}

public sealed class HomeArtistProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class HomeArtistVisuals
{
    [JsonPropertyName("avatarImage")]
    public HomeImageContainer? AvatarImage { get; set; }
}

// ── Playlist ──

public sealed class HomePlaylistData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("images")]
    public HomeImagesContainer? Images { get; set; }

    [JsonPropertyName("ownerV2")]
    public HomeOwnerV2? OwnerV2 { get; set; }
}

public sealed class HomeOwnerV2
{
    [JsonPropertyName("data")]
    public HomeOwnerData? Data { get; set; }
}

public sealed class HomeOwnerData
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

// ── Album ──

public sealed class HomeAlbumData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("artists")]
    public HomeAlbumArtists? Artists { get; set; }

    [JsonPropertyName("coverArt")]
    public HomeImageContainer? CoverArt { get; set; }
}

public sealed class HomeAlbumArtists
{
    [JsonPropertyName("items")]
    public List<HomeAlbumArtistItem>? Items { get; set; }
}

public sealed class HomeAlbumArtistItem
{
    [JsonPropertyName("profile")]
    public HomeArtistProfile? Profile { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}

// ── Podcast ──

public sealed class HomePodcastData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("coverArt")]
    public HomeImageContainer? CoverArt { get; set; }

    [JsonPropertyName("publisher")]
    public HomePodcastPublisher? Publisher { get; set; }
}

public sealed class HomePodcastPublisher
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

// ── Episode (V2 + V1 both reuse this shape) ──

public sealed class HomeEpisodeData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("coverArt")]
    public HomeImageContainer? CoverArt { get; set; }

    [JsonPropertyName("duration")]
    public HomeDurationMs? Duration { get; set; }

    [JsonPropertyName("playedState")]
    public HomePlayedState? PlayedState { get; set; }

    [JsonPropertyName("releaseDate")]
    public HomeIsoDate? ReleaseDate { get; set; }

    [JsonPropertyName("podcastV2")]
    public HomePodcastWrapper? PodcastV2 { get; set; }

    /// <summary>e.g. "PODCAST_EPISODE".</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>e.g. ["VIDEO", "AUDIO"]. Drives the small video glyph on the card.</summary>
    [JsonPropertyName("mediaTypes")]
    public List<string>? MediaTypes { get; set; }
}

public sealed class HomeDurationMs
{
    [JsonPropertyName("totalMilliseconds")]
    public long TotalMilliseconds { get; set; }
}

public sealed class HomePlayedState
{
    [JsonPropertyName("playPositionMilliseconds")]
    public long PlayPositionMilliseconds { get; set; }

    /// <summary>One of "NOT_STARTED", "IN_PROGRESS", "COMPLETED".</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }
}

public sealed class HomeIsoDate
{
    [JsonPropertyName("isoString")]
    public string? IsoString { get; set; }
}

public sealed class HomePodcastWrapper
{
    [JsonPropertyName("data")]
    public HomeShowData? Data { get; set; }
}

public sealed class HomeShowData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("coverArt")]
    public HomeImageContainer? CoverArt { get; set; }

    [JsonPropertyName("publisher")]
    public HomePodcastPublisher? Publisher { get; set; }
}

// ── Home Chips ──

public sealed class HomeChip
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("label")]
    public HomeLabel? Label { get; set; }

    [JsonPropertyName("subChips")]
    public List<HomeChip>? SubChips { get; set; }
}

// ── Shared image types ──

public sealed class HomeImagesContainer
{
    [JsonPropertyName("items")]
    public List<HomeImageItem>? Items { get; set; }
}

public sealed class HomeImageItem
{
    [JsonPropertyName("extractedColors")]
    public HomeExtractedColors? ExtractedColors { get; set; }

    [JsonPropertyName("sources")]
    public List<HomeImageSource>? Sources { get; set; }
}

public sealed class HomeImageContainer
{
    [JsonPropertyName("extractedColors")]
    public HomeExtractedColors? ExtractedColors { get; set; }

    [JsonPropertyName("sources")]
    public List<HomeImageSource>? Sources { get; set; }
}

public sealed class HomeExtractedColors
{
    [JsonPropertyName("colorDark")]
    public HomeColorValue? ColorDark { get; set; }
}

public sealed class HomeColorValue
{
    [JsonPropertyName("hex")]
    public string? Hex { get; set; }

    [JsonPropertyName("isFallback")]
    public bool IsFallback { get; set; }
}

public sealed class HomeImageSource
{
    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }
}

// ── JSON serialization context ──

[JsonSerializable(typeof(HomeResponse))]
// V1 typed data
[JsonSerializable(typeof(HomeArtistData))]
[JsonSerializable(typeof(HomePlaylistData))]
[JsonSerializable(typeof(HomeAlbumData))]
[JsonSerializable(typeof(HomePodcastData))]
[JsonSerializable(typeof(HomeImagesContainer))]
[JsonSerializable(typeof(HomeImageItem))]
[JsonSerializable(typeof(HomeImageContainer))]
[JsonSerializable(typeof(HomeImageSource))]
[JsonSerializable(typeof(HomeExtractedColors))]
[JsonSerializable(typeof(HomeColorValue))]
[JsonSerializable(typeof(HomeOwnerV2))]
[JsonSerializable(typeof(HomeOwnerData))]
[JsonSerializable(typeof(HomeArtistProfile))]
[JsonSerializable(typeof(HomeArtistVisuals))]
[JsonSerializable(typeof(HomeAlbumArtists))]
[JsonSerializable(typeof(HomeAlbumArtistItem))]
[JsonSerializable(typeof(HomePodcastPublisher))]
[JsonSerializable(typeof(HomeEpisodeData))]
[JsonSerializable(typeof(HomeDurationMs))]
[JsonSerializable(typeof(HomePlayedState))]
[JsonSerializable(typeof(HomeIsoDate))]
[JsonSerializable(typeof(HomePodcastWrapper))]
[JsonSerializable(typeof(HomeShowData))]
[JsonSerializable(typeof(HomeChip))]
[JsonSerializable(typeof(HomeLabel))]
// V2 entity/trait models
[JsonSerializable(typeof(HomeEntityWrapper))]
[JsonSerializable(typeof(HomeEntityData))]
[JsonSerializable(typeof(HomeIdentityTrait))]
[JsonSerializable(typeof(HomeVisualIdentityTrait))]
[JsonSerializable(typeof(HomeEntityTypeTrait))]
[JsonSerializable(typeof(HomeSquareCoverImage))]
[JsonSerializable(typeof(HomeImageV2Wrapper))]
[JsonSerializable(typeof(HomeImageV2Data))]
[JsonSerializable(typeof(HomeImageV2Source))]
[JsonSerializable(typeof(HomeOriginalInstance))]
[JsonSerializable(typeof(HomeFlatFile))]
[JsonSerializable(typeof(HomeContributors))]
[JsonSerializable(typeof(HomeContributorItem))]
[JsonSerializable(typeof(HomeFormatListAttribute))]
[JsonSerializable(typeof(HomeTypedEntity))]
[JsonSourceGenerationOptions]
public partial class HomeJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(HomeVariables))]
public partial class HomeVariablesJsonContext : JsonSerializerContext;
