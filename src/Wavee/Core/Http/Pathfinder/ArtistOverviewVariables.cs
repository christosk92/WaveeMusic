using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed record ArtistOverviewVariables(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("locale")] string Locale = "",
    [property: JsonPropertyName("preReleaseV2")] bool PreReleaseV2 = false);

[JsonSerializable(typeof(ArtistOverviewVariables))]
internal partial class ArtistOverviewVariablesJsonContext : JsonSerializerContext
{
}
