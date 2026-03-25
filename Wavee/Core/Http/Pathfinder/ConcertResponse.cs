using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed class ConcertDetailResponse
{
    [JsonPropertyName("data")]
    public ConcertDetailResponseData? Data { get; init; }
}

public sealed class ConcertDetailResponseData
{
    [JsonPropertyName("concert")]
    public ConcertDetailData? Concert { get; init; }

    [JsonPropertyName("me")]
    public UserLocationMe? Me { get; init; }
}

public sealed class ConcertDetailData
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("festival")]
    public bool Festival { get; init; }

    [JsonPropertyName("startDateIsoString")]
    public string? StartDateIsoString { get; init; }

    [JsonPropertyName("doorsOpenTimeIsoString")]
    public string? DoorsOpenTimeIsoString { get; init; }

    [JsonPropertyName("ageRestriction")]
    public string? AgeRestriction { get; init; }

    [JsonPropertyName("saved")]
    public bool? Saved { get; init; }

    [JsonPropertyName("location")]
    public ConcertDetailLocation? Location { get; init; }

    [JsonPropertyName("artists")]
    public ConcertDetailArtists? Artists { get; init; }

    [JsonPropertyName("offers")]
    public ConcertDetailOffers? Offers { get; init; }

    [JsonPropertyName("relatedConcerts")]
    public ConcertDetailRelatedConcerts? RelatedConcerts { get; init; }

    [JsonPropertyName("concepts")]
    public ConcertDetailConcepts? Concepts { get; init; }

    [JsonPropertyName("venue")]
    public ConcertVenueRef? Venue { get; init; }
}

public sealed class ConcertDetailLocation
{
    [JsonPropertyName("city")]
    public string? City { get; init; }

    [JsonPropertyName("country")]
    public string? Country { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("region")]
    public string? Region { get; init; }

    [JsonPropertyName("coordinates")]
    public ConcertCoordinates? Coordinates { get; init; }

    [JsonPropertyName("metroAreaLocation")]
    public ConcertMetroArea? MetroAreaLocation { get; init; }
}

public sealed class ConcertCoordinates
{
    [JsonPropertyName("latitude")]
    public double Latitude { get; init; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; init; }
}

public sealed class ConcertMetroArea
{
    [JsonPropertyName("fullName")]
    public string? FullName { get; init; }

    [JsonPropertyName("geonameId")]
    public string? GeonameId { get; init; }
}

// ── Artists ──

public sealed class ConcertDetailArtists
{
    [JsonPropertyName("items")]
    public List<ConcertDetailArtistWrapper>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class ConcertDetailArtistWrapper
{
    [JsonPropertyName("_uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("data")]
    public ConcertDetailArtistData? Data { get; init; }
}

public sealed class ConcertDetailArtistData
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("profile")]
    public ConcertArtistProfile? Profile { get; init; }

    [JsonPropertyName("visuals")]
    public ConcertArtistVisuals? Visuals { get; init; }

    [JsonPropertyName("headerImage")]
    public ArtistHeaderImage? HeaderImage { get; init; }

    [JsonPropertyName("discography")]
    public ConcertArtistDiscography? Discography { get; init; }

    [JsonPropertyName("goods")]
    public ConcertArtistGoods? Goods { get; init; }
}

public sealed class ConcertArtistDiscography
{
    [JsonPropertyName("popularReleasesAlbums")]
    public ConcertPopularReleases? PopularReleasesAlbums { get; init; }
}

public sealed class ConcertPopularReleases
{
    [JsonPropertyName("items")]
    public List<ConcertPopularAlbum>? Items { get; init; }
}

public sealed class ConcertPopularAlbum
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("coverArt")]
    public ArtistCoverArt? CoverArt { get; init; }

    [JsonPropertyName("artists")]
    public ArtistTrackArtists? Artists { get; init; }
}

public sealed class ConcertArtistGoods
{
    [JsonPropertyName("concerts")]
    public ConcertArtistConcertCount? Concerts { get; init; }
}

public sealed class ConcertArtistConcertCount
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class ConcertArtistProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class ConcertArtistVisuals
{
    [JsonPropertyName("avatarImage")]
    public ConcertArtistAvatarImage? AvatarImage { get; init; }
}

public sealed class ConcertArtistAvatarImage
{
    [JsonPropertyName("sources")]
    public List<ArtistImageSource>? Sources { get; init; }
}

// ── Offers ──

public sealed class ConcertDetailOffers
{
    [JsonPropertyName("items")]
    public List<ConcertOffer>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class ConcertOffer
{
    [JsonPropertyName("providerName")]
    public string? ProviderName { get; init; }

    [JsonPropertyName("providerImageUrl")]
    public string? ProviderImageUrl { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("saleType")]
    public string? SaleType { get; init; }

    [JsonPropertyName("minPrice")]
    public string? MinPrice { get; init; }

    [JsonPropertyName("maxPrice")]
    public string? MaxPrice { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }
}

// ── Related Concerts ──

public sealed class ConcertDetailRelatedConcerts
{
    [JsonPropertyName("items")]
    public List<RelatedConcertWrapper>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class RelatedConcertWrapper
{
    [JsonPropertyName("_uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("data")]
    public RelatedConcertData? Data { get; init; }
}

public sealed class RelatedConcertData
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("festival")]
    public bool Festival { get; init; }

    [JsonPropertyName("startDateIsoString")]
    public string? StartDateIsoString { get; init; }

    [JsonPropertyName("location")]
    public ConcertDetailLocation? Location { get; init; }

    [JsonPropertyName("artists")]
    public ConcertDetailArtists? Artists { get; init; }
}

// ── Concepts / Genres ──

public sealed class ConcertDetailConcepts
{
    [JsonPropertyName("items")]
    public List<ConcertConcept>? Items { get; init; }
}

public sealed class ConcertConcept
{
    [JsonPropertyName("data")]
    public ConcertConceptData? Data { get; init; }

    [JsonPropertyName("weight")]
    public double Weight { get; init; }
}

public sealed class ConcertConceptData
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

public sealed class ConcertVenueRef
{
    [JsonPropertyName("data")]
    public ConcertVenueData? Data { get; init; }
}

public sealed class ConcertVenueData
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

// ── Source generation ──

[JsonSerializable(typeof(ConcertDetailResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ConcertDetailJsonContext : JsonSerializerContext { }
