using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// Response shape for the similarAlbumsBasedOnThisTrack persisted query.
// Drives AlbumPage's "For this mood" / "Similar albums" shelf. Each item is
// an Album entity with artists, cover art, release date, playability and a
// type discriminator ("ALBUM" / "SINGLE" / "EP" / "COMPILATION").
// Reuses SeoArtists/SeoArtistItem/SeoArtistProfile/SeoCoverArt from the
// SeoRecommendedTracksResponse types since the JSON shape is identical.

public sealed class SimilarAlbumsBasedOnThisTrackResponse
{
    [JsonPropertyName("data")]
    public SimilarAlbumsData? Data { get; init; }
}

public sealed class SimilarAlbumsData
{
    [JsonPropertyName("seoRecommendedTrackAlbum")]
    public SimilarAlbumsPage? SeoRecommendedTrackAlbum { get; init; }
}

public sealed class SimilarAlbumsPage
{
    [JsonPropertyName("items")]
    public List<SimilarAlbumItem>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class SimilarAlbumItem
{
    [JsonPropertyName("data")]
    public SimilarAlbumData? Data { get; init; }
}

public sealed class SimilarAlbumData
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>"ALBUM", "SINGLE", "EP", or "COMPILATION" — drives the type chip on the tile.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Reuses <see cref="SeoArtists"/> — identical shape.</summary>
    [JsonPropertyName("artists")]
    public SeoArtists? Artists { get; init; }

    /// <summary>Reuses <see cref="SeoCoverArt"/> — identical shape.</summary>
    [JsonPropertyName("coverArt")]
    public SeoCoverArt? CoverArt { get; init; }

    [JsonPropertyName("date")]
    public SimilarAlbumDate? Date { get; init; }

    [JsonPropertyName("playability")]
    public SimilarAlbumPlayability? Playability { get; init; }

    [JsonPropertyName("sharingInfo")]
    public SimilarAlbumSharingInfo? SharingInfo { get; init; }
}

public sealed class SimilarAlbumDate
{
    /// <summary>ISO date string like "2016-09-07T00:00:00Z" or year-only.</summary>
    [JsonPropertyName("isoString")]
    public string? IsoString { get; init; }

    /// <summary>"YEAR" | "MONTH" | "DAY" — granularity of the date.</summary>
    [JsonPropertyName("precision")]
    public string? Precision { get; init; }

    [JsonPropertyName("year")]
    public int Year { get; init; }
}

public sealed class SimilarAlbumPlayability
{
    [JsonPropertyName("playable")]
    public bool Playable { get; init; }
}

public sealed class SimilarAlbumSharingInfo
{
    [JsonPropertyName("shareId")]
    public string? ShareId { get; init; }

    [JsonPropertyName("shareUrl")]
    public string? ShareUrl { get; init; }
}

[JsonSerializable(typeof(SimilarAlbumsBasedOnThisTrackResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class SimilarAlbumsBasedOnThisTrackJsonContext : JsonSerializerContext
{
}
