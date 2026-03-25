using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

/// <summary>Empty variables for queries like userLocation that take no parameters.</summary>
public sealed class EmptyVariables { }

/// <summary>Variables for concertLocationsByLatLon query.</summary>
public sealed class ConcertLocationsByLatLonVariables
{
    [JsonPropertyName("lat")]
    public double Lat { get; init; }

    [JsonPropertyName("lon")]
    public double Lon { get; init; }
}

/// <summary>Variables for saveLocation query.</summary>
public sealed class SaveLocationVariables
{
    [JsonPropertyName("geonameId")]
    public string GeonameId { get; init; } = "";
}

/// <summary>Variables for searchConcertLocations query.</summary>
public sealed class SearchConcertLocationsVariables
{
    [JsonPropertyName("query")]
    public string Query { get; init; } = "";
}

// ── Source generation ──

[JsonSerializable(typeof(EmptyVariables))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class EmptyVariablesJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(ConcertLocationsByLatLonVariables))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ConcertLocationsByLatLonVariablesJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(SaveLocationVariables))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class SaveLocationVariablesJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(SearchConcertLocationsVariables))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class SearchConcertLocationsVariablesJsonContext : JsonSerializerContext { }

/// <summary>Variables for concert detail query.</summary>
public sealed class ConcertVariables
{
    [JsonPropertyName("uri")]
    public string Uri { get; init; } = "";

    [JsonPropertyName("authenticated")]
    public bool Authenticated { get; init; } = true;
}

[JsonSerializable(typeof(ConcertVariables))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ConcertVariablesJsonContext : JsonSerializerContext { }
