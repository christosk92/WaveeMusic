using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// Response shape for the internalLinkRecommenderTrack persisted query.
// Currently unused — the bespoke video-page "Up next" list it fed was retired
// when VideoPlayerPage collapsed onto the shared ExpandedPlayerView. Kept
// because the URL/params/shape encode reverse-engineered details that would
// be expensive to re-derive.

public sealed class SeoRecommendedTracksResponse
{
    [JsonPropertyName("data")]
    public SeoRecommendedTracksData? Data { get; init; }
}

public sealed class SeoRecommendedTracksData
{
    [JsonPropertyName("seoRecommendedTrack")]
    public SeoRecommendedTrackPage? SeoRecommendedTrack { get; init; }
}

public sealed class SeoRecommendedTrackPage
{
    [JsonPropertyName("items")]
    public List<SeoRecommendedTrackItem>? Items { get; init; }
}

public sealed class SeoRecommendedTrackItem
{
    [JsonPropertyName("data")]
    public SeoRecommendedTrackData? Data { get; init; }
}

public sealed class SeoRecommendedTrackData
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Returned as a string in the JSON payload; may exceed Int32.MaxValue.</summary>
    [JsonPropertyName("playcount")]
    public string? Playcount { get; init; }

    [JsonPropertyName("contentRating")]
    public SeoContentRating? ContentRating { get; init; }

    [JsonPropertyName("duration")]
    public SeoDuration? Duration { get; init; }

    [JsonPropertyName("artists")]
    public SeoArtists? Artists { get; init; }

    [JsonPropertyName("albumOfTrack")]
    public SeoAlbumOfTrack? AlbumOfTrack { get; init; }
}

public sealed class SeoDuration
{
    [JsonPropertyName("totalMilliseconds")]
    public long TotalMilliseconds { get; init; }
}

public sealed class SeoContentRating
{
    /// <summary>"NONE" or "EXPLICIT" — drives the small E badge on the card.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

public sealed class SeoArtists
{
    [JsonPropertyName("items")]
    public List<SeoArtistItem>? Items { get; init; }
}

public sealed class SeoArtistItem
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("profile")]
    public SeoArtistProfile? Profile { get; init; }
}

public sealed class SeoArtistProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class SeoAlbumOfTrack
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("coverArt")]
    public SeoCoverArt? CoverArt { get; init; }
}

public sealed class SeoCoverArt
{
    /// <summary>
    /// Reuses <see cref="ArtistImageSource"/> from <c>ArtistOverviewResponse.cs</c>
    /// — same shape (url + width + height + maxWidth/maxHeight).
    /// </summary>
    [JsonPropertyName("sources")]
    public List<ArtistImageSource>? Sources { get; init; }
}

[JsonSerializable(typeof(SeoRecommendedTracksResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class SeoRecommendedTracksJsonContext : JsonSerializerContext
{
}
