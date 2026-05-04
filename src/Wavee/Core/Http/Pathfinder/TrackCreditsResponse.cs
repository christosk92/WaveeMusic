using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// ── Root ──

public sealed class TrackCreditsResponse
{
    [JsonPropertyName("data")]
    public TrackCreditsData? Data { get; init; }
}

public sealed class TrackCreditsData
{
    [JsonPropertyName("trackUnion")]
    public TrackCreditsTrackUnion? TrackUnion { get; init; }
}

public sealed class TrackCreditsTrackUnion
{
    [JsonPropertyName("__typename")]
    public string? Typename { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("artists")]
    public TrackCreditsArtists? Artists { get; init; }

    [JsonPropertyName("contributors")]
    public NpvContributorsContainer? Contributors { get; init; }

    /// <summary>
    /// Reuses NpvCreditsTrait from NpvArtistResponse — same shape.
    /// </summary>
    [JsonPropertyName("creditsTrait")]
    public NpvCreditsTrait? CreditsTrait { get; init; }
}

public sealed class TrackCreditsArtists
{
    [JsonPropertyName("items")]
    public List<TrackCreditsArtistItem>? Items { get; init; }
}

public sealed class TrackCreditsArtistItem
{
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

// ── JSON Source Generation ──

[JsonSerializable(typeof(TrackCreditsResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class TrackCreditsJsonContext : JsonSerializerContext
{
}
