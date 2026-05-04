using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed record TrackCreditsVariables(
    [property: JsonPropertyName("trackUri")] string TrackUri,
    [property: JsonPropertyName("contributorsLimit")] int ContributorsLimit = 100,
    [property: JsonPropertyName("contributorsOffset")] int ContributorsOffset = 0);

[JsonSerializable(typeof(TrackCreditsVariables))]
internal partial class TrackCreditsVariablesJsonContext : JsonSerializerContext
{
}
