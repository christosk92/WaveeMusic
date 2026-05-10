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
        int limit = 50,
        int offset = 0,
        int numberOfTopResults = 50)
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
                IncludeEpisodeContentRatingsV2 = false,
                IsPrefix = null,
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

    /// <summary>
    /// Creates a per-chip "searchPlaylists" request — fired when the user selects
    /// the Playlists chip on the Search page and paginates within that section.
    /// Mirrors the desktop 1.2.89.539 capture exactly.
    /// </summary>
    public static PathfinderRequest CreateSearchPlaylistsRequest(
        string searchTerm,
        int limit = 30,
        int offset = 0,
        int numberOfTopResults = 20)
    {
        return new PathfinderRequest
        {
            Variables = new FilteredSearchVariables
            {
                SearchTerm = searchTerm,
                Limit = limit,
                Offset = offset,
                NumberOfTopResults = numberOfTopResults,
                IncludeAudiobooks = true,
                IncludeAuthors = true,
                IncludePreReleases = false,
                IncludeEpisodeContentRatingsV2 = false
            },
            OperationName = PathfinderOperations.SearchPlaylists,
            Extensions = new QueryExtensions
            {
                PersistedQuery = new PersistedQuery
                {
                    Version = 1,
                    Sha256Hash = PathfinderOperations.SearchPlaylistsHash
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
    public int Limit { get; init; } = 50;

    [JsonPropertyName("numberOfTopResults")]
    public int NumberOfTopResults { get; init; } = 50;

    [JsonPropertyName("includeArtistHasConcertsField")]
    public bool IncludeArtistHasConcertsField { get; init; }

    [JsonPropertyName("includeAudiobooks")]
    public bool IncludeAudiobooks { get; init; } = true;

    [JsonPropertyName("includeAuthors")]
    public bool IncludeAuthors { get; init; } = true;

    [JsonPropertyName("includePreReleases")]
    public bool IncludePreReleases { get; init; } = true;

    [JsonPropertyName("includeEpisodeContentRatingsV2")]
    public bool IncludeEpisodeContentRatingsV2 { get; init; }

    // Source-gen context sets DefaultIgnoreCondition = WhenWritingNull globally;
    // override here so null serializes as "isPrefix": null to match the desktop wire format.
    [JsonPropertyName("isPrefix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? IsPrefix { get; init; }

    [JsonPropertyName("sectionFilters")]
    public string[] SectionFilters { get; init; } = ["GENERIC", "VIDEO_CONTENT"];
}

/// <summary>
/// Variables for the searchFullEpisodes chip query — uses a slimmer payload than the
/// other per-chip ops (no numberOfTopResults / includeAudiobooks / includeAuthors /
/// includePreReleases). Mirrors the desktop 1.2.89.539 capture exactly.
/// </summary>
public sealed class EpisodeSearchVariables
{
    [JsonPropertyName("searchTerm")]
    public required string SearchTerm { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 30;

    [JsonPropertyName("includeEpisodeContentRatingsV2")]
    public bool IncludeEpisodeContentRatingsV2 { get; init; }
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

    [JsonPropertyName("includeEpisodeContentRatingsV2")]
    public bool IncludeEpisodeContentRatingsV2 { get; init; }
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

    [JsonPropertyName("includeEpisodeContentRatingsV2")]
    public bool IncludeEpisodeContentRatingsV2 { get; init; }
}

[JsonSerializable(typeof(SearchSuggestionsVariables))]
internal partial class SearchSuggestionsVariablesJsonContext : JsonSerializerContext { }
