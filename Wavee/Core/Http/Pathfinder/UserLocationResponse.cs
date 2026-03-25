using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// ── userLocation ──

public sealed class UserLocationResponse
{
    [JsonPropertyName("data")]
    public UserLocationData? Data { get; init; }
}

public sealed class UserLocationData
{
    [JsonPropertyName("me")]
    public UserLocationMe? Me { get; init; }
}

public sealed class UserLocationMe
{
    [JsonPropertyName("profile")]
    public UserLocationProfile? Profile { get; init; }
}

public sealed class UserLocationProfile
{
    [JsonPropertyName("location")]
    public UserLocationInfo? Location { get; init; }
}

public sealed class UserLocationInfo
{
    [JsonPropertyName("geoHash")]
    public string? GeoHash { get; init; }

    [JsonPropertyName("geonameId")]
    public string? GeonameId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

// ── concertLocationsByLatLon ──

public sealed class ConcertLocationsResponse
{
    [JsonPropertyName("data")]
    public ConcertLocationsData? Data { get; init; }
}

public sealed class ConcertLocationsData
{
    [JsonPropertyName("concertLocations")]
    public ConcertLocationsList? ConcertLocations { get; init; }
}

public sealed class ConcertLocationsList
{
    [JsonPropertyName("items")]
    public List<ConcertLocationItem>? Items { get; init; }
}

public sealed class ConcertLocationItem
{
    [JsonPropertyName("geonameId")]
    public string? GeonameId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("country")]
    public string? Country { get; init; }

    [JsonPropertyName("fullName")]
    public string? FullName { get; init; }

    [JsonPropertyName("geoHash")]
    public string? GeoHash { get; init; }
}

// ── saveLocation ──

public sealed class SaveLocationResponse
{
    [JsonPropertyName("data")]
    public SaveLocationData? Data { get; init; }
}

public sealed class SaveLocationData
{
    [JsonPropertyName("storeUserLocation")]
    public SaveLocationResult? StoreUserLocation { get; init; }
}

public sealed class SaveLocationResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }
}

// ── Source generation ──

[JsonSerializable(typeof(UserLocationResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class UserLocationJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(ConcertLocationsResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ConcertLocationsJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(SaveLocationResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class SaveLocationJsonContext : JsonSerializerContext { }
