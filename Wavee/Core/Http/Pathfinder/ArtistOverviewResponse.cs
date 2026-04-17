using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// ── Root ──

public sealed class ArtistOverviewResponse
{
    [JsonPropertyName("data")]
    public ArtistOverviewData? Data { get; init; }
}

public sealed class ArtistOverviewData
{
    [JsonPropertyName("artistUnion")]
    public ArtistUnion? ArtistUnion { get; init; }
}

// ── Artist ──

public sealed class ArtistUnion
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("saved")]
    public bool Saved { get; init; }

    [JsonPropertyName("profile")]
    public AoArtistProfile? Profile { get; init; }

    [JsonPropertyName("visuals")]
    public AoArtistVisuals? Visuals { get; init; }

    [JsonPropertyName("headerImage")]
    public ArtistHeaderImage? HeaderImage { get; init; }

    [JsonPropertyName("visualIdentity")]
    public ArtistVisualIdentity? VisualIdentity { get; init; }

    [JsonPropertyName("stats")]
    public ArtistStats? Stats { get; init; }

    [JsonPropertyName("discography")]
    public ArtistDiscography? Discography { get; init; }

    [JsonPropertyName("relatedContent")]
    public ArtistRelatedContent? RelatedContent { get; init; }

    [JsonPropertyName("watchFeedEntrypoint")]
    public ArtistWatchFeedEntrypoint? WatchFeedEntrypoint { get; init; }

    [JsonPropertyName("goods")]
    public ArtistGoods? Goods { get; init; }
}

// ── Profile ──

public sealed class AoArtistProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("biography")]
    public ArtistBiography? Biography { get; init; }

    [JsonPropertyName("verified")]
    public bool Verified { get; init; }

    [JsonPropertyName("externalLinks")]
    public ArtistExternalLinks? ExternalLinks { get; init; }

    [JsonPropertyName("pinnedItem")]
    public ArtistPinnedItem? PinnedItem { get; init; }
}

public sealed class ArtistBiography
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

public sealed class ArtistExternalLinks
{
    [JsonPropertyName("items")]
    public List<ArtistExternalLink>? Items { get; init; }
}

public sealed class ArtistExternalLink
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

// ── Visuals ──

public sealed class AoArtistVisuals
{
    [JsonPropertyName("avatarImage")]
    public ArtistAvatarImage? AvatarImage { get; init; }

    [JsonPropertyName("gallery")]
    public ArtistGallery? Gallery { get; init; }
}

public sealed class ArtistAvatarImage
{
    [JsonPropertyName("sources")]
    public List<ArtistImageSource>? Sources { get; init; }

    [JsonPropertyName("extractedColors")]
    public ArtistExtractedColors? ExtractedColors { get; init; }
}

public sealed class ArtistExtractedColors
{
    [JsonPropertyName("colorRaw")]
    public ArtistColorHex? ColorRaw { get; init; }
}

public sealed class ArtistColorHex
{
    [JsonPropertyName("hex")]
    public string? Hex { get; init; }
}

public sealed class ArtistImageSource
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    [JsonPropertyName("maxWidth")]
    public int? MaxWidth { get; init; }

    [JsonPropertyName("maxHeight")]
    public int? MaxHeight { get; init; }
}

public sealed class ArtistGallery
{
    [JsonPropertyName("items")]
    public List<ArtistGalleryItem>? Items { get; init; }
}

public sealed class ArtistGalleryItem
{
    [JsonPropertyName("sources")]
    public List<ArtistImageSource>? Sources { get; init; }
}

public sealed class ArtistHeaderImage
{
    [JsonPropertyName("data")]
    public ArtistHeaderImageData? Data { get; init; }
}

public sealed class ArtistHeaderImageData
{
    [JsonPropertyName("sources")]
    public List<ArtistImageSource>? Sources { get; init; }
}

// ── Visual Identity (palette extracted by Spotify, present on the artist payload) ──

public sealed class ArtistVisualIdentity
{
    [JsonPropertyName("wideFullBleedImage")]
    public ArtistVisualIdentityImage? WideFullBleedImage { get; init; }
}

public sealed class ArtistVisualIdentityImage
{
    [JsonPropertyName("extractedColorSet")]
    public ArtistExtractedColorSet? ExtractedColorSet { get; init; }
}

public sealed class ArtistExtractedColorSet
{
    [JsonPropertyName("highContrast")]
    public ArtistExtractedColorPalette? HighContrast { get; init; }

    [JsonPropertyName("higherContrast")]
    public ArtistExtractedColorPalette? HigherContrast { get; init; }

    [JsonPropertyName("minContrast")]
    public ArtistExtractedColorPalette? MinContrast { get; init; }
}

public sealed class ArtistExtractedColorPalette
{
    [JsonPropertyName("backgroundBase")]
    public ArtistRgbaColor? BackgroundBase { get; init; }

    [JsonPropertyName("backgroundTintedBase")]
    public ArtistRgbaColor? BackgroundTintedBase { get; init; }

    [JsonPropertyName("textBase")]
    public ArtistRgbaColor? TextBase { get; init; }

    [JsonPropertyName("textBrightAccent")]
    public ArtistRgbaColor? TextBrightAccent { get; init; }

    [JsonPropertyName("textSubdued")]
    public ArtistRgbaColor? TextSubdued { get; init; }
}

public sealed class ArtistRgbaColor
{
    [JsonPropertyName("alpha")] public int Alpha { get; init; }
    [JsonPropertyName("red")]   public int Red   { get; init; }
    [JsonPropertyName("green")] public int Green { get; init; }
    [JsonPropertyName("blue")]  public int Blue  { get; init; }

    public string ToHex() => $"#{Red:X2}{Green:X2}{Blue:X2}";
}

// ── Stats ──

public sealed class ArtistStats
{
    [JsonPropertyName("followers")]
    public long Followers { get; init; }

    [JsonPropertyName("monthlyListeners")]
    public long MonthlyListeners { get; init; }

    [JsonPropertyName("topCities")]
    public ArtistTopCities? TopCities { get; init; }
}

public sealed class ArtistTopCities
{
    [JsonPropertyName("items")]
    public List<ArtistTopCity>? Items { get; init; }
}

public sealed class ArtistTopCity
{
    [JsonPropertyName("city")]
    public string? City { get; init; }

    [JsonPropertyName("country")]
    public string? Country { get; init; }

    [JsonPropertyName("numberOfListeners")]
    public long NumberOfListeners { get; init; }
}

// ── Discography ──

public sealed class ArtistDiscography
{
    [JsonPropertyName("topTracks")]
    public ArtistTopTracks? TopTracks { get; init; }

    [JsonPropertyName("albums")]
    public ArtistReleaseGroup? Albums { get; init; }

    [JsonPropertyName("singles")]
    public ArtistReleaseGroup? Singles { get; init; }

    [JsonPropertyName("compilations")]
    public ArtistReleaseGroup? Compilations { get; init; }

    [JsonPropertyName("popularReleasesAlbums")]
    public ArtistPopularReleases? PopularReleasesAlbums { get; init; }

    [JsonPropertyName("latest")]
    public ArtistRelease? Latest { get; init; }
}

// ── Top Tracks ──

public sealed class ArtistTopTracks
{
    [JsonPropertyName("items")]
    public List<ArtistTopTrackItem>? Items { get; init; }
}

public sealed class ArtistTopTrackItem
{
    [JsonPropertyName("uid")]
    public string? Uid { get; init; }

    [JsonPropertyName("track")]
    public ArtistTrack? Track { get; init; }
}

public sealed class ArtistTrack
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("playcount")]
    public string? Playcount { get; init; }

    [JsonPropertyName("discNumber")]
    public int DiscNumber { get; init; }

    [JsonPropertyName("duration")]
    public ArtistTrackDuration? Duration { get; init; }

    [JsonPropertyName("contentRating")]
    public ArtistContentRating? ContentRating { get; init; }

    [JsonPropertyName("albumOfTrack")]
    public ArtistTrackAlbum? AlbumOfTrack { get; init; }

    [JsonPropertyName("artists")]
    public ArtistTrackArtists? Artists { get; init; }

    [JsonPropertyName("playability")]
    public ArtistPlayability? Playability { get; init; }

    [JsonPropertyName("associationsV3")]
    public ArtistTrackAssociationsInfo? AssociationsV3 { get; init; }

    /// <summary>
    /// True if this entry has a music video (has video associations pointing to the real track).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasVideo => AssociationsV3?.VideoAssociations?.TotalCount >0;
}

public sealed class ArtistTrackAssociationsInfo
{
    [JsonPropertyName("videoAssociations")]
    public ArtistVideoAssociationsInfo? VideoAssociations { get; init; }

}
public sealed class ArtistVideoAssociationsInfo
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}
public sealed class ArtistAudioAssociations
{
    [JsonPropertyName("items")]
    public List<ArtistAudioAssociationItem>? Items { get; init; }
}

public sealed class ArtistAudioAssociationItem
{
    [JsonPropertyName("trackAudio")]
    public ArtistTrackAudioRef? TrackAudio { get; init; }
}

public sealed class ArtistTrackAudioRef
{
    [JsonPropertyName("_uri")]
    public string? Uri { get; init; }
}

public sealed class ArtistTrackDuration
{
    [JsonPropertyName("totalMilliseconds")]
    public long TotalMilliseconds { get; init; }
}

public sealed class ArtistContentRating
{
    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

public sealed class ArtistTrackAlbum
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("coverArt")]
    public ArtistCoverArt? CoverArt { get; init; }
}

public sealed class ArtistCoverArt
{
    [JsonPropertyName("sources")]
    public List<ArtistImageSource>? Sources { get; init; }
}

public sealed class ArtistTrackArtists
{
    [JsonPropertyName("items")]
    public List<ArtistTrackArtistItem>? Items { get; init; }
}

public sealed class ArtistTrackArtistItem
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("profile")]
    public ArtistTrackArtistProfile? Profile { get; init; }
}

public sealed class ArtistTrackArtistProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class ArtistPlayability
{
    [JsonPropertyName("playable")]
    public bool Playable { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

// ── Release Groups (Albums, Singles, Compilations) ──

public sealed class ArtistReleaseGroup
{
    [JsonPropertyName("items")]
    public List<ArtistReleaseGroupItem>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class ArtistReleaseGroupItem
{
    [JsonPropertyName("releases")]
    public ArtistReleases? Releases { get; init; }
}

public sealed class ArtistReleases
{
    [JsonPropertyName("items")]
    public List<ArtistRelease>? Items { get; init; }
}

public sealed class ArtistPopularReleases
{
    [JsonPropertyName("items")]
    public List<ArtistRelease>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class ArtistRelease
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("date")]
    public ArtistReleaseDate? Date { get; init; }

    [JsonPropertyName("coverArt")]
    public ArtistCoverArt? CoverArt { get; init; }

    [JsonPropertyName("tracks")]
    public ArtistReleaseTracks? Tracks { get; init; }

    [JsonPropertyName("playability")]
    public ArtistPlayability? Playability { get; init; }

    [JsonPropertyName("sharingInfo")]
    public ArtistSharingInfo? SharingInfo { get; init; }
}

public sealed class ArtistReleaseDate
{
    [JsonPropertyName("day")]
    public int? Day { get; init; }

    [JsonPropertyName("month")]
    public int? Month { get; init; }

    [JsonPropertyName("year")]
    public int Year { get; init; }

    [JsonPropertyName("precision")]
    public string? Precision { get; init; }
}

public sealed class ArtistReleaseTracks
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class ArtistSharingInfo
{
    [JsonPropertyName("shareId")]
    public string? ShareId { get; init; }

    [JsonPropertyName("shareUrl")]
    public string? ShareUrl { get; init; }
}

// ── Related Content ──

public sealed class ArtistRelatedContent
{
    [JsonPropertyName("relatedArtists")]
    public ArtistRelatedArtists? RelatedArtists { get; init; }
}

public sealed class ArtistRelatedArtists
{
    [JsonPropertyName("items")]
    public List<ArtistRelatedArtistItem>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class ArtistRelatedArtistItem
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("profile")]
    public ArtistTrackArtistProfile? Profile { get; init; }

    [JsonPropertyName("visuals")]
    public AoArtistVisuals? Visuals { get; init; }
}

// ── Pinned Item ──

public sealed class ArtistPinnedItem
{
    [JsonPropertyName("comment")]
    public string? Comment { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("backgroundImageV2")]
    public ArtistPinnedItemImage? BackgroundImageV2 { get; init; }

    [JsonPropertyName("thumbnailImage")]
    public ArtistPinnedItemImage? ThumbnailImage { get; init; }

    [JsonPropertyName("itemV2")]
    public ArtistPinnedItemWrapper? ItemV2 { get; init; }
}

public sealed class ArtistPinnedItemImage
{
    [JsonPropertyName("data")]
    public ArtistPinnedItemImageData? Data { get; init; }
}

public sealed class ArtistPinnedItemImageData
{
    [JsonPropertyName("sources")]
    public List<ArtistImageSource>? Sources { get; init; }
}

public sealed class ArtistPinnedItemWrapper
{
    [JsonPropertyName("data")]
    public ArtistPinnedItemData? Data { get; init; }
}

public sealed class ArtistPinnedItemData
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("coverArt")]
    public ArtistCoverArt? CoverArt { get; init; }
}

// ── Watch Feed Entrypoint ──

public sealed class ArtistWatchFeedEntrypoint
{
    [JsonPropertyName("entrypointUri")]
    public string? EntrypointUri { get; init; }

    [JsonPropertyName("thumbnailImage")]
    public ArtistWatchFeedThumbnail? ThumbnailImage { get; init; }

    [JsonPropertyName("video")]
    public ArtistWatchFeedVideo? Video { get; init; }
}

public sealed class ArtistWatchFeedThumbnail
{
    [JsonPropertyName("data")]
    public ArtistWatchFeedThumbnailData? Data { get; init; }
}

public sealed class ArtistWatchFeedThumbnailData
{
    [JsonPropertyName("sources")]
    public List<ArtistImageSource>? Sources { get; init; }
}

public sealed class ArtistWatchFeedVideo
{
    [JsonPropertyName("fileId")]
    public string? FileId { get; init; }

    [JsonPropertyName("startTime")]
    public int StartTime { get; init; }

    [JsonPropertyName("endTime")]
    public int EndTime { get; init; }

    [JsonPropertyName("videoType")]
    public string? VideoType { get; init; }
}

// ── Goods (Concerts, Merch) ──

public sealed class ArtistGoods
{
    [JsonPropertyName("concerts")]
    public ArtistConcerts? Concerts { get; init; }
}

public sealed class ArtistConcerts
{
    [JsonPropertyName("items")]
    public List<ArtistConcertItem>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class ArtistConcertItem
{
    [JsonPropertyName("data")]
    public ArtistConcertData? Data { get; init; }
}

public sealed class ArtistConcertData
{
    [JsonPropertyName("festival")]
    public bool Festival { get; init; }

    [JsonPropertyName("location")]
    public ArtistConcertLocation? Location { get; init; }

    [JsonPropertyName("startDateIsoString")]
    public string? StartDateIsoString { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

public sealed class ArtistConcertLocation
{
    [JsonPropertyName("city")]
    public string? City { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

// ── JSON Source Generation ──

[JsonSerializable(typeof(ArtistOverviewResponse))]
[JsonSerializable(typeof(ArtistOverviewData))]
[JsonSerializable(typeof(ArtistUnion))]
[JsonSerializable(typeof(AoArtistProfile))]
[JsonSerializable(typeof(AoArtistVisuals))]
[JsonSerializable(typeof(ArtistHeaderImage))]
[JsonSerializable(typeof(ArtistVisualIdentity))]
[JsonSerializable(typeof(ArtistVisualIdentityImage))]
[JsonSerializable(typeof(ArtistExtractedColorSet))]
[JsonSerializable(typeof(ArtistExtractedColorPalette))]
[JsonSerializable(typeof(ArtistRgbaColor))]
[JsonSerializable(typeof(ArtistStats))]
[JsonSerializable(typeof(ArtistDiscography))]
[JsonSerializable(typeof(ArtistRelatedContent))]
[JsonSerializable(typeof(ArtistWatchFeedEntrypoint))]
[JsonSerializable(typeof(ArtistWatchFeedVideo))]
[JsonSerializable(typeof(ArtistWatchFeedThumbnail))]
[JsonSerializable(typeof(ArtistWatchFeedThumbnailData))]
[JsonSerializable(typeof(ArtistPinnedItem))]
[JsonSerializable(typeof(ArtistPinnedItemWrapper))]
[JsonSerializable(typeof(ArtistPinnedItemData))]
[JsonSerializable(typeof(ArtistPinnedItemImage))]
[JsonSerializable(typeof(ArtistPinnedItemImageData))]
[JsonSerializable(typeof(ArtistGoods))]
[JsonSerializable(typeof(ArtistConcerts))]
[JsonSerializable(typeof(ArtistConcertItem))]
[JsonSerializable(typeof(ArtistConcertData))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ArtistOverviewJsonContext : JsonSerializerContext
{
}
