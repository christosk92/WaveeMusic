using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// Variables for the userTopContent GraphQL query.
/// </summary>
public sealed class UserTopContentVariables
{
    [JsonPropertyName("includeTopArtists")]
    public bool IncludeTopArtists { get; init; }

    [JsonPropertyName("topArtistsInput")]
    public TopContentInput? TopArtistsInput { get; init; }

    [JsonPropertyName("includeTopTracks")]
    public bool IncludeTopTracks { get; init; }

    [JsonPropertyName("topTracksInput")]
    public TopContentInput? TopTracksInput { get; init; }
}

/// <summary>
/// Input parameters for top content queries (artists or tracks).
/// </summary>
public sealed class TopContentInput
{
    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("sortBy")]
    public string? SortBy { get; init; }

    [JsonPropertyName("timeRange")]
    public string? TimeRange { get; init; }
}

/// <summary>
/// JSON serializer context for Pathfinder variable types (AOT compatible).
/// </summary>
[JsonSerializable(typeof(UserTopContentVariables))]
[JsonSerializable(typeof(TopContentInput))]
[JsonSerializable(typeof(ExtractedColorsVariables))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class PathfinderVariablesJsonContext : JsonSerializerContext
{
}
