using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// GraphQL request for Spotify's Pathfinder API.
/// </summary>
public sealed class PathfinderRequest
{
    [JsonPropertyName("variables")]
    public object? Variables { get; set; }

    [JsonPropertyName("operationName")]
    public required string OperationName { get; init; }

    [JsonPropertyName("extensions")]
    public required QueryExtensions Extensions { get; init; }

    /// <summary>
    /// Creates a search request with the given parameters.
    /// </summary>
    public static PathfinderRequest CreateSearchRequest(
        string searchTerm,
        int limit = 10,
        int offset = 0,
        int numberOfTopResults = 5)
    {
        return new PathfinderRequest
        {
            Variables = new SearchVariables
            {
                Query = searchTerm,
                Limit = limit,
                Offset = offset,
                NumberOfTopResults = numberOfTopResults,
                IncludeAudiobooks = true,
                IncludeArtistHasConcertsField = false,
                IncludePreReleases = true,
                IncludeAuthors = true,
                SectionFilters = ["GENERIC", "VIDEO_CONTENT"]
            },
            OperationName = PathfinderOperations.SearchTopResultsList,
            Extensions = new QueryExtensions
            {
                PersistedQuery = new PersistedQuery
                {
                    Version = 1,
                    Sha256Hash = PathfinderOperations.SearchTopResultsListHash
                }
            }
        };
    }
}

/// <summary>
/// Variables for the search GraphQL query.
/// </summary>
public sealed class SearchVariables
{
    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 10;

    [JsonPropertyName("numberOfTopResults")]
    public int NumberOfTopResults { get; init; } = 5;

    [JsonPropertyName("includeAudiobooks")]
    public bool IncludeAudiobooks { get; init; } = true;

    [JsonPropertyName("includeArtistHasConcertsField")]
    public bool IncludeArtistHasConcertsField { get; init; }

    [JsonPropertyName("includePreReleases")]
    public bool IncludePreReleases { get; init; } = true;

    [JsonPropertyName("includeAuthors")]
    public bool IncludeAuthors { get; init; } = true;

    [JsonPropertyName("sectionFilters")]
    public string[] SectionFilters { get; init; } = ["GENERIC", "VIDEO_CONTENT"];
}

public sealed class FilteredSearchVariables
{
    [JsonPropertyName("searchTerm")]
    public required string SearchTerm { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 30;

    [JsonPropertyName("numberOfTopResults")]
    public int NumberOfTopResults { get; init; } = 20;

    [JsonPropertyName("includeAudiobooks")]
    public bool IncludeAudiobooks { get; init; } = true;

    [JsonPropertyName("includeAuthors")]
    public bool IncludeAuthors { get; init; } = true;

    [JsonPropertyName("includePreReleases")]
    public bool IncludePreReleases { get; init; }
}

/// <summary>
/// Extensions for persisted GraphQL queries.
/// </summary>
public sealed class QueryExtensions
{
    [JsonPropertyName("persistedQuery")]
    public required PersistedQuery PersistedQuery { get; init; }
}

/// <summary>
/// Persisted query reference with hash.
/// </summary>
public sealed class PersistedQuery
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("sha256Hash")]
    public required string Sha256Hash { get; init; }
}

/// <summary>
/// Variables for the recentSearches query.
/// </summary>
public sealed class RecentSearchesVariables
{
    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 50;

    [JsonPropertyName("includeAuthors")]
    public bool IncludeAuthors { get; init; } = true;
}

[JsonSerializable(typeof(RecentSearchesVariables))]
internal partial class RecentSearchesVariablesJsonContext : JsonSerializerContext { }

/// <summary>
/// Variables for the searchSuggestions query.
/// </summary>
public sealed class SearchSuggestionsVariables
{
    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 30;

    [JsonPropertyName("numberOfTopResults")]
    public int NumberOfTopResults { get; init; } = 30;

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("includeAuthors")]
    public bool IncludeAuthors { get; init; } = true;
}

[JsonSerializable(typeof(SearchSuggestionsVariables))]
internal partial class SearchSuggestionsVariablesJsonContext : JsonSerializerContext { }
