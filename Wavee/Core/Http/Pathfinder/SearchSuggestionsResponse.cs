using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

// ── Root response ──

public sealed class SearchSuggestionsResponse
{
    [JsonPropertyName("data")]
    public SearchSuggestionsData? Data { get; init; }
}

public sealed class SearchSuggestionsData
{
    [JsonPropertyName("searchV2")]
    public SearchSuggestionsV2? SearchV2 { get; init; }
}

public sealed class SearchSuggestionsV2
{
    [JsonPropertyName("topResultsV2")]
    public SuggestionTopResults? TopResultsV2 { get; init; }
}

public sealed class SuggestionTopResults
{
    [JsonPropertyName("itemsV2")]
    public List<SuggestionHit>? ItemsV2 { get; init; }
}

/// <summary>
/// A suggestion hit wrapping a polymorphic item.
/// The item's __typename tells us the type (SearchAutoCompleteEntity, ArtistResponseWrapper, etc.).
/// </summary>
public sealed class SuggestionHit
{
    [JsonPropertyName("item")]
    public SuggestionItem? Item { get; init; }
}

public sealed class SuggestionItem
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    /// <summary>
    /// The entity data. Structure varies by __typename.
    /// Parsed manually in the service layer based on TypeName.
    /// </summary>
    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }
}
