using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// ── Root ──

public sealed class HomeResponse
{
    [JsonPropertyName("data")]
    public HomeResponseData? Data { get; set; }
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
}

public sealed class HomeLabel
{
    [JsonPropertyName("transformedLabel")]
    public string? TransformedLabel { get; set; }
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
    [JsonPropertyName("content")]
    public HomeItemContent? Content { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
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
[JsonSerializable(typeof(HomeChip))]
[JsonSerializable(typeof(HomeLabel))]
[JsonSourceGenerationOptions]
public partial class HomeJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(HomeVariables))]
public partial class HomeVariablesJsonContext : JsonSerializerContext;
