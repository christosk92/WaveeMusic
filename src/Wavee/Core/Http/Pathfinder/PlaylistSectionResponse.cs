using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// Variables for the <c>playlistSection</c> persisted query — the "You might
/// also like" related-playlist rail. The <c>sectionUri</c> is a fixed section
/// id (<see cref="PathfinderOperations.PlaylistSectionUri"/>); the server
/// resolves the section's contents relative to <c>playlistUri</c>.
/// </summary>
public sealed class PlaylistSectionVariables
{
    [JsonPropertyName("sectionUri")]
    public required string SectionUri { get; init; }

    [JsonPropertyName("playlistUri")]
    public required string PlaylistUri { get; init; }
}

/// <summary>
/// Response shape for the <c>playlistSection</c> persisted query. The payload
/// reuses the home-feed section shape (<c>homeSections.sections[].sectionItems
/// .items[].content.data</c> → <see cref="HomePlaylistData"/>), so the inner
/// section/item/content types are reused verbatim from <c>HomeResponse.cs</c>
/// and only the root wrappers are declared here.
/// </summary>
public sealed class PlaylistSectionResponse
{
    [JsonPropertyName("data")]
    public PlaylistSectionData? Data { get; set; }
}

public sealed class PlaylistSectionData
{
    [JsonPropertyName("homeSections")]
    public PlaylistSectionCollection? HomeSections { get; set; }
}

public sealed class PlaylistSectionCollection
{
    [JsonPropertyName("sections")]
    public List<HomeSectionEntry>? Sections { get; set; }
}

// ── JSON contexts ──

[JsonSerializable(typeof(PlaylistSectionVariables))]
internal partial class PlaylistSectionVariablesJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(PlaylistSectionResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class PlaylistSectionJsonContext : JsonSerializerContext { }
