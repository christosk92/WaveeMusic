using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// Variables for the <c>queryNpvEpisodeChapters</c> persisted query — fetches
/// the talk-display segments (chapter ranges + titles) Spotify exposes for a
/// podcast episode. Used to render a chapter-aware position bar in playback UI.
/// </summary>
public sealed record QueryNpvEpisodeChaptersVariables(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("offset")] int Offset = 0,
    [property: JsonPropertyName("limit")] int Limit = 50);

[JsonSerializable(typeof(QueryNpvEpisodeChaptersVariables))]
internal partial class QueryNpvEpisodeChaptersVariablesJsonContext : JsonSerializerContext
{
}
