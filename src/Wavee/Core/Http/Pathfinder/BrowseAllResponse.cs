using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// Variables for the Pathfinder <c>browseAll</c> persistedQuery. Drives the
/// flat top-level Browse All surface (Music / Podcasts / Audiobooks / genres /
/// moods / charts / …).
/// </summary>
public sealed class BrowseAllVariables
{
    [JsonPropertyName("pagePagination")]
    public BrowseAllPagination PagePagination { get; set; } = new() { Offset = 0, Limit = 10 };

    [JsonPropertyName("sectionPagination")]
    public BrowseAllPagination SectionPagination { get; set; } = new() { Offset = 0, Limit = 99 };

    [JsonPropertyName("browseEndUserIntegration")]
    public string BrowseEndUserIntegration { get; set; } = "INTEGRATION_WEB_PLAYER";
}

public sealed class BrowseAllPagination
{
    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }
}

/// <summary>
/// Minimal DTO for the <c>browseAll</c> response. Item-level data is left as
/// <see cref="JsonElement"/> because the wrapper schema differs by typename
/// (BrowseSectionContainerWrapper has the card under <c>data.cardRepresentation</c>;
/// BrowseXlinkResponseWrapper exposes title/backgroundColor at the top level).
/// The parser walks both paths in <c>BrowseAllParser</c> on the WinUI side.
/// </summary>
public sealed class BrowseAllResponse
{
    [JsonPropertyName("data")]
    public BrowseAllResponseData? Data { get; set; }
}

public sealed class BrowseAllResponseData
{
    [JsonPropertyName("browseStart")]
    public BrowseAllStart? BrowseStart { get; set; }
}

public sealed class BrowseAllStart
{
    [JsonPropertyName("sections")]
    public BrowseAllSections? Sections { get; set; }
}

public sealed class BrowseAllSections
{
    [JsonPropertyName("items")]
    public List<BrowseAllSectionContainer>? Items { get; set; }
}

public sealed class BrowseAllSectionContainer
{
    [JsonPropertyName("sectionItems")]
    public BrowseAllSectionItems? SectionItems { get; set; }
}

public sealed class BrowseAllSectionItems
{
    [JsonPropertyName("items")]
    public List<BrowseAllItemEntry>? Items { get; set; }
}

public sealed class BrowseAllItemEntry
{
    [JsonPropertyName("content")]
    public BrowseAllItemContent? Content { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}

public sealed class BrowseAllItemContent
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

[JsonSerializable(typeof(BrowseAllResponse))]
[JsonSerializable(typeof(BrowseAllVariables))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
public partial class BrowseAllJsonContext : JsonSerializerContext { }
