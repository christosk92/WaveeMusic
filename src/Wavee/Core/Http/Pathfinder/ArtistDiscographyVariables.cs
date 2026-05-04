using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// Variables for the queryArtistDiscography{Albums,Singles,Compilations} GraphQL queries.
/// </summary>
public sealed record ArtistDiscographyVariables
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 20;

    [JsonPropertyName("order")]
    public string Order { get; init; } = "DATE_DESC";
}

/// <summary>
/// Response envelope for paginated discography queries.
/// Reuses ArtistReleaseGroup from ArtistOverviewResponse.
/// </summary>
public sealed class ArtistDiscographyResponse
{
    [JsonPropertyName("data")]
    public ArtistDiscographyData? Data { get; init; }
}

public sealed class ArtistDiscographyData
{
    [JsonPropertyName("artistUnion")]
    public ArtistDiscographyUnion? ArtistUnion { get; init; }
}

public sealed class ArtistDiscographyUnion
{
    [JsonPropertyName("discography")]
    public ArtistDiscographyPayload? Discography { get; init; }
}

public sealed class ArtistDiscographyPayload
{
    [JsonPropertyName("all")]
    public ArtistReleaseGroup? All { get; init; }

    [JsonPropertyName("albums")]
    public ArtistReleaseGroup? Albums { get; init; }

    [JsonPropertyName("singles")]
    public ArtistReleaseGroup? Singles { get; init; }

    [JsonPropertyName("compilations")]
    public ArtistReleaseGroup? Compilations { get; init; }
}

[JsonSerializable(typeof(ArtistDiscographyVariables))]
internal partial class ArtistDiscographyVariablesJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(ArtistDiscographyResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ArtistDiscographyJsonContext : JsonSerializerContext { }
