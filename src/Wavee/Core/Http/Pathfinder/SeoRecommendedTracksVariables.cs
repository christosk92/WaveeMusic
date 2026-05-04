using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// Variables for the <c>internalLinkRecommenderTrack</c> persisted query —
/// returns Spotify's "watch next" track recommendations for a given track.
/// </summary>
public sealed record SeoRecommendedTracksVariables(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("limit")] int Limit = 20);

[JsonSerializable(typeof(SeoRecommendedTracksVariables))]
internal partial class SeoRecommendedTracksVariablesJsonContext : JsonSerializerContext
{
}
