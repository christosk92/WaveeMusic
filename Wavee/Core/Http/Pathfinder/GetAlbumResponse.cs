using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// ── Variables ──

public sealed record GetAlbumVariables
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("locale")]
    public string Locale { get; init; } = "";

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 50;
}

// ── Response ──

public sealed class GetAlbumResponse
{
    [JsonPropertyName("data")]
    public GetAlbumData? Data { get; init; }
}

public sealed class GetAlbumData
{
    [JsonPropertyName("albumUnion")]
    public GetAlbumUnion? AlbumUnion { get; init; }
}

public sealed class GetAlbumUnion
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("saved")]
    public bool Saved { get; init; }

    [JsonPropertyName("isPreRelease")]
    public bool IsPreRelease { get; init; }

    [JsonPropertyName("preReleaseEndDateTime")]
    public string? PreReleaseEndDateTime { get; init; }

    [JsonPropertyName("courtesyLine")]
    public string? CourtesyLine { get; init; }

    [JsonPropertyName("releases")]
    public AlbumReleasesList? Releases { get; init; }

    [JsonPropertyName("visualIdentity")]
    public AlbumVisualIdentity? VisualIdentity { get; init; }

    [JsonPropertyName("date")]
    public AlbumDate? Date { get; init; }

    [JsonPropertyName("playability")]
    public ArtistPlayability? Playability { get; init; }

    [JsonPropertyName("copyright")]
    public AlbumCopyrightList? Copyright { get; init; }

    [JsonPropertyName("coverArt")]
    public AlbumCoverArt? CoverArt { get; init; }

    [JsonPropertyName("artists")]
    public AlbumArtistList? Artists { get; init; }

    [JsonPropertyName("discs")]
    public AlbumDiscList? Discs { get; init; }

    [JsonPropertyName("tracksV2")]
    public AlbumTracksV2? TracksV2 { get; init; }

    [JsonPropertyName("moreAlbumsByArtist")]
    public AlbumMoreByArtist? MoreAlbumsByArtist { get; init; }

    [JsonPropertyName("sharingInfo")]
    public AlbumSharingInfo? SharingInfo { get; init; }
}

// ── Album metadata types ──

public sealed class AlbumDate
{
    [JsonPropertyName("isoString")]
    public string? IsoString { get; init; }

    [JsonPropertyName("precision")]
    public string? Precision { get; init; }
}

public sealed class AlbumCopyrightList
{
    [JsonPropertyName("items")]
    public List<AlbumCopyrightItem>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class AlbumCopyrightItem
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

public sealed class AlbumCoverArt
{
    [JsonPropertyName("extractedColors")]
    public AlbumExtractedColors? ExtractedColors { get; init; }

    [JsonPropertyName("sources")]
    public List<ArtistImageSource>? Sources { get; init; }
}

public sealed class AlbumExtractedColors
{
    [JsonPropertyName("colorDark")]
    public ExtractedColorHex? ColorDark { get; init; }

    [JsonPropertyName("colorLight")]
    public ExtractedColorHex? ColorLight { get; init; }

    [JsonPropertyName("colorRaw")]
    public ExtractedColorHex? ColorRaw { get; init; }
}

public sealed class ExtractedColorHex
{
    [JsonPropertyName("hex")]
    public string? Hex { get; init; }
}

public sealed class AlbumArtistList
{
    [JsonPropertyName("items")]
    public List<AlbumArtistItem>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class AlbumArtistItem
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("profile")]
    public AoArtistProfile? Profile { get; init; }

    [JsonPropertyName("visuals")]
    public AoArtistVisuals? Visuals { get; init; }

    [JsonPropertyName("sharingInfo")]
    public AlbumSharingInfo? SharingInfo { get; init; }
}

public sealed class AlbumDiscList
{
    [JsonPropertyName("items")]
    public List<AlbumDiscItem>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class AlbumDiscItem
{
    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("tracks")]
    public AlbumDiscTracks? Tracks { get; init; }
}

public sealed class AlbumDiscTracks
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class AlbumMoreByArtist
{
    [JsonPropertyName("items")]
    public List<AlbumMoreByArtistItem>? Items { get; init; }
}

public sealed class AlbumMoreByArtistItem
{
    [JsonPropertyName("discography")]
    public AlbumMoreByArtistDiscography? Discography { get; init; }
}

public sealed class AlbumMoreByArtistDiscography
{
    [JsonPropertyName("popularReleasesAlbums")]
    public AlbumMoreByArtistReleases? PopularReleasesAlbums { get; init; }
}

public sealed class AlbumMoreByArtistReleases
{
    [JsonPropertyName("items")]
    public List<AlbumMoreByArtistRelease>? Items { get; init; }
}

public sealed class AlbumMoreByArtistRelease
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("date")]
    public AlbumMoreByArtistDate? Date { get; init; }

    [JsonPropertyName("coverArt")]
    public AlbumMoreByArtistCoverArt? CoverArt { get; init; }

    [JsonPropertyName("playability")]
    public ArtistPlayability? Playability { get; init; }

    [JsonPropertyName("sharingInfo")]
    public AlbumSharingInfo? SharingInfo { get; init; }
}

public sealed class AlbumMoreByArtistDate
{
    [JsonPropertyName("year")]
    public int Year { get; init; }
}

public sealed class AlbumMoreByArtistCoverArt
{
    [JsonPropertyName("sources")]
    public List<ArtistImageSource>? Sources { get; init; }
}

public sealed class AlbumSharingInfo
{
    [JsonPropertyName("shareId")]
    public string? ShareId { get; init; }

    [JsonPropertyName("shareUrl")]
    public string? ShareUrl { get; init; }
}

// ── Alternate releases (deluxe / remaster / anniversary editions of THIS album) ──

public sealed class AlbumReleasesList
{
    [JsonPropertyName("items")]
    public List<AlbumReleaseItem>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class AlbumReleaseItem
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("date")]
    public AlbumMoreByArtistDate? Date { get; init; }

    [JsonPropertyName("coverArt")]
    public AlbumMoreByArtistCoverArt? CoverArt { get; init; }
}

// ── Visual identity (Spotify's pre-extracted color palette for the cover) ──

public sealed class AlbumVisualIdentity
{
    [JsonPropertyName("squareCoverImage")]
    public AlbumVisualIdentityImage? SquareCoverImage { get; init; }
}

public sealed class AlbumVisualIdentityImage
{
    [JsonPropertyName("extractedColorSet")]
    public ArtistExtractedColorSet? ExtractedColorSet { get; init; }
}

// ── JSON contexts ──

[JsonSerializable(typeof(GetAlbumVariables))]
internal partial class GetAlbumVariablesJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(GetAlbumResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class GetAlbumJsonContext : JsonSerializerContext { }
