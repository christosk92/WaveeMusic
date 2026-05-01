using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// Variables for the <c>getTrack</c> persisted query — returns the trackUnion
/// with playcount, duration, content rating, album metadata, and the artist
/// discography. Used by the Now Playing video page hero to surface playcount.
/// </summary>
public sealed record GetTrackVariables(
    [property: JsonPropertyName("uri")] string Uri);

[JsonSerializable(typeof(GetTrackVariables))]
internal partial class GetTrackVariablesJsonContext : JsonSerializerContext
{
}
