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

    [JsonPropertyName("stats")]
    public ArtistStats? Stats { get; init; }

    [JsonPropertyName("discography")]
    public ArtistDiscography? Discography { get; init; }

    [JsonPropertyName("relatedContent")]
    public ArtistRelatedContent? RelatedContent { get; init; }
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

// ── JSON Source Generation ──

[JsonSerializable(typeof(ArtistOverviewResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ArtistOverviewJsonContext : JsonSerializerContext
{
}
