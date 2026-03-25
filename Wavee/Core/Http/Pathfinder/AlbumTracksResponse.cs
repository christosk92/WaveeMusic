using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// ── Variables ──

public sealed record AlbumTracksVariables
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 300;
}

// ── Response ──

public sealed class AlbumTracksResponse
{
    [JsonPropertyName("data")]
    public AlbumTracksData? Data { get; init; }
}

public sealed class AlbumTracksData
{
    [JsonPropertyName("albumUnion")]
    public AlbumTracksUnion? AlbumUnion { get; init; }
}

public sealed class AlbumTracksUnion
{
    [JsonPropertyName("playability")]
    public ArtistPlayability? Playability { get; init; }

    [JsonPropertyName("tracksV2")]
    public AlbumTracksV2? TracksV2 { get; init; }
}

public sealed class AlbumTracksV2
{
    [JsonPropertyName("items")]
    public List<AlbumTrackItem>? Items { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class AlbumTrackItem
{
    [JsonPropertyName("track")]
    public AlbumTrack? Track { get; init; }

    [JsonPropertyName("uid")]
    public string? Uid { get; init; }
}

public sealed class AlbumTrack
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("duration")]
    public ArtistTrackDuration? Duration { get; init; }

    [JsonPropertyName("playcount")]
    public string? Playcount { get; init; }

    [JsonPropertyName("trackNumber")]
    public int TrackNumber { get; init; }

    [JsonPropertyName("discNumber")]
    public int DiscNumber { get; init; }

    [JsonPropertyName("contentRating")]
    public ArtistContentRating? ContentRating { get; init; }

    [JsonPropertyName("playability")]
    public ArtistPlayability? Playability { get; init; }

    [JsonPropertyName("artists")]
    public ArtistTrackArtists? Artists { get; init; }

    [JsonPropertyName("saved")]
    public bool Saved { get; init; }

    [JsonPropertyName("associationsV3")]
    public AlbumTrackAssociations? AssociationsV3 { get; init; }
}

public sealed class AlbumTrackAssociations
{
    [JsonPropertyName("videoAssociations")]
    public AlbumTrackVideoAssociations? VideoAssociations { get; init; }
}

public sealed class AlbumTrackVideoAssociations
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

// ── JSON contexts ──

[JsonSerializable(typeof(AlbumTracksVariables))]
internal partial class AlbumTracksVariablesJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(AlbumTracksResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class AlbumTracksJsonContext : JsonSerializerContext { }
