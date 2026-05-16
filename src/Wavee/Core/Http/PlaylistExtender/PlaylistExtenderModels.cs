using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.PlaylistExtender;

// Wire shape for Spotify's playlistextender/extendp endpoint — the "Enhance" /
// "Recommended Songs" recommender. Request body fields are deliberately
// camel-cased except for the two acronyms (`playlistURI`, `trackSkipIDs`)
// which Spotify ships uppercase. Source-gen camel-case policy + explicit
// JsonPropertyName attrs cover both.

internal sealed class ExtendPlaylistRequest
{
    [JsonPropertyName("playlistURI")]
    public required string PlaylistUri { get; init; }

    [JsonPropertyName("trackSkipIDs")]
    public required IReadOnlyList<string> TrackSkipIds { get; init; }

    [JsonPropertyName("numResults")]
    public int NumResults { get; init; } = 20;
}

public sealed class ExtendPlaylistResponse
{
    [JsonPropertyName("recommendedTracks")]
    public List<RecommendedExtendedTrack>? RecommendedTracks { get; init; }

    [JsonPropertyName("tracks")]
    public List<ExtendedTrack>? Tracks { get; init; }
}

public sealed class RecommendedExtendedTrack
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("originalId")]
    public string? OriginalId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("artists")]
    public List<ExtendedArtist>? Artists { get; init; }

    [JsonPropertyName("album")]
    public RecommendedExtendedAlbum? Album { get; init; }

    /// <summary>Track duration in milliseconds.</summary>
    [JsonPropertyName("duration")]
    public long Duration { get; init; }
}

public sealed class RecommendedExtendedAlbum
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("largeImageUrl")]
    public string? LargeImageUrl { get; init; }
}

public sealed class ExtendedTrack
{
    [JsonPropertyName("trackUri")]
    public string? TrackUri { get; init; }

    [JsonPropertyName("trackMetadata")]
    public ExtendedTrackMetadata? Metadata { get; init; }
}

public sealed class ExtendedTrackMetadata
{
    // Spotify's wire shape has flipped historically between `name` and
    // `trackName` on this endpoint. Tolerate both: the service-layer mapper
    // falls back from one to the other.
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("trackName")]
    public string? TrackName { get; init; }

    [JsonPropertyName("albumName")]
    public string? AlbumName { get; init; }

    [JsonPropertyName("trackImageUri")]
    public string? TrackImageUri { get; init; }

    /// <summary>Track duration in milliseconds.</summary>
    [JsonPropertyName("duration")]
    public long Duration { get; init; }

    [JsonPropertyName("artistList")]
    public List<ExtendedArtist>? ArtistList { get; init; }
}

public sealed class ExtendedArtist
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ExtendPlaylistRequest))]
[JsonSerializable(typeof(ExtendPlaylistResponse))]
internal partial class PlaylistExtenderJsonContext : JsonSerializerContext;
