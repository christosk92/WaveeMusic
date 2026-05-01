using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// Response shape for the getTrack persisted query. Trimmed aggressively —
// we only consume the playcount + a few essentials. The full payload also
// returns firstArtist.discography. We keep only topTracks from that section
// because the video page uses it as the only no-extra-request source for
// music-video row playcounts.

public sealed class GetTrackResponse
{
    [JsonPropertyName("data")]
    public GetTrackData? Data { get; init; }
}

public sealed class GetTrackData
{
    [JsonPropertyName("trackUnion")]
    public GetTrackUnion? TrackUnion { get; init; }
}

public sealed class GetTrackUnion
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Returned as a string in the JSON payload; may exceed Int32.MaxValue
    /// (e.g. 2.4B+ for "APT."). Parse via <c>long.TryParse</c>.
    /// </summary>
    [JsonPropertyName("playcount")]
    public string? Playcount { get; init; }

    [JsonPropertyName("contentRating")]
    public GetTrackContentRating? ContentRating { get; init; }

    [JsonPropertyName("duration")]
    public GetTrackDuration? Duration { get; init; }

    [JsonPropertyName("albumOfTrack")]
    public GetTrackAlbum? AlbumOfTrack { get; init; }

    [JsonPropertyName("firstArtist")]
    public GetTrackFirstArtist? FirstArtist { get; init; }
}

public sealed class GetTrackDuration
{
    [JsonPropertyName("totalMilliseconds")]
    public long TotalMilliseconds { get; init; }
}

public sealed class GetTrackContentRating
{
    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

public sealed class GetTrackAlbum
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("coverArt")]
    public GetTrackCoverArt? CoverArt { get; init; }
}

public sealed class GetTrackCoverArt
{
    [JsonPropertyName("sources")]
    public List<ArtistImageSource>? Sources { get; init; }
}

public sealed class GetTrackFirstArtist
{
    [JsonPropertyName("items")]
    public List<GetTrackFirstArtistItem>? Items { get; init; }
}

public sealed class GetTrackFirstArtistItem
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("profile")]
    public GetTrackArtistProfile? Profile { get; init; }

    [JsonPropertyName("discography")]
    public GetTrackArtistDiscography? Discography { get; init; }
}

public sealed class GetTrackArtistProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

// Discography subset — only the top-tracks list, used by the video page to
// enrich its "Music videos" rows with playcount (npv RelatedVideos doesn't
// carry playcount; the artist's top tracks usually do, and same-artist videos
// almost always overlap with same-artist top tracks).
public sealed class GetTrackArtistDiscography
{
    [JsonPropertyName("topTracks")]
    public GetTrackTopTracks? TopTracks { get; init; }
}

public sealed class GetTrackTopTracks
{
    [JsonPropertyName("items")]
    public List<GetTrackTopTrackItem>? Items { get; init; }
}

public sealed class GetTrackTopTrackItem
{
    [JsonPropertyName("track")]
    public GetTrackTopTrackData? Track { get; init; }
}

public sealed class GetTrackTopTrackData
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Returned as a string, may exceed Int32.</summary>
    [JsonPropertyName("playcount")]
    public string? Playcount { get; init; }
}

[JsonSerializable(typeof(GetTrackResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class GetTrackJsonContext : JsonSerializerContext
{
}
