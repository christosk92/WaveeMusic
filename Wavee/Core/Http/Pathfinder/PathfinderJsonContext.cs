using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// JSON serializer context for Pathfinder API (AOT compatible).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PathfinderRequest))]
[JsonSerializable(typeof(PathfinderSearchResponse))]
[JsonSerializable(typeof(SearchVariables))]
[JsonSerializable(typeof(QueryExtensions))]
[JsonSerializable(typeof(PersistedQuery))]
internal partial class PathfinderJsonContext : JsonSerializerContext
{
}
