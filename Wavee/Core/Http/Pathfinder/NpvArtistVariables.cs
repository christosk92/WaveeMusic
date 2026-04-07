using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed record NpvArtistVariables(
    [property: JsonPropertyName("artistUri")] string ArtistUri,
    [property: JsonPropertyName("trackUri")] string TrackUri,
    [property: JsonPropertyName("contributorsLimit")] int ContributorsLimit = 10,
    [property: JsonPropertyName("contributorsOffset")] int ContributorsOffset = 0,
    [property: JsonPropertyName("enableRelatedVideos")] bool EnableRelatedVideos = true,
    [property: JsonPropertyName("enableRelatedAudioTracks")] bool EnableRelatedAudioTracks = true);

[JsonSerializable(typeof(NpvArtistVariables))]
internal partial class NpvArtistVariablesJsonContext : JsonSerializerContext
{
}
