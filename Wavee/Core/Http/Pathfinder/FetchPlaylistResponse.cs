using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// Variables for the <c>fetchPlaylist</c> persisted query.
/// We only ever ask for the visual-identity colour palette — items are loaded
/// via the existing playlist diff path — so <c>limit=0</c> is intentional
/// (avoids dragging the full track list back over the wire just for the
/// extracted colours).
/// </summary>
public sealed class FetchPlaylistVariables
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; } = 0;

    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 0;

    [JsonPropertyName("enableWatchFeedEntrypoint")]
    public bool EnableWatchFeedEntrypoint { get; init; } = false;

    [JsonPropertyName("includeEpisodeContentRatingsV2")]
    public bool IncludeEpisodeContentRatingsV2 { get; init; } = false;
}

/// <summary>
/// Response shape for the <c>fetchPlaylist</c> persisted query. Only fields
/// we actually consume (<c>visualIdentity.squareCoverImage.extractedColorSet</c>)
/// are bound — everything else is left out so the JSON deserializer can
/// short-circuit. Reuses the existing <see cref="ArtistExtractedColorSet"/>
/// types from the artist response since the GraphQL shape is identical.
/// </summary>
public sealed class FetchPlaylistResponse
{
    [JsonPropertyName("data")]
    public FetchPlaylistData? Data { get; init; }
}

public sealed class FetchPlaylistData
{
    [JsonPropertyName("playlistV2")]
    public FetchPlaylistEntity? PlaylistV2 { get; init; }
}

public sealed class FetchPlaylistEntity
{
    [JsonPropertyName("visualIdentity")]
    public FetchPlaylistVisualIdentity? VisualIdentity { get; init; }
}

public sealed class FetchPlaylistVisualIdentity
{
    [JsonPropertyName("squareCoverImage")]
    public FetchPlaylistVisualIdentityImage? SquareCoverImage { get; init; }
}

public sealed class FetchPlaylistVisualIdentityImage
{
    [JsonPropertyName("extractedColorSet")]
    public ArtistExtractedColorSet? ExtractedColorSet { get; init; }
}

// ── JSON contexts ──

[JsonSerializable(typeof(FetchPlaylistVariables))]
internal partial class FetchPlaylistVariablesJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(FetchPlaylistResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class FetchPlaylistJsonContext : JsonSerializerContext { }
