using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// Variables for the similarAlbumsBasedOnThisTrack persisted query — returns
// similar albums (by mood / style) seeded from a track URI. Drives the
// AlbumPage "For this mood" / "Similar albums" shelf. Seed with the album's
// most-played track; fall back to the first track if play counts are unloaded.
public sealed record SimilarAlbumsBasedOnThisTrackVariables(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("limit")] int Limit = 24,
    [property: JsonPropertyName("albumsOnly")] bool AlbumsOnly = true);

[JsonSerializable(typeof(SimilarAlbumsBasedOnThisTrackVariables))]
internal partial class SimilarAlbumsBasedOnThisTrackVariablesJsonContext : JsonSerializerContext
{
}
