// ── Shell/Sidebar.Store.V2.cs ──────────────────────────────────────────────────────────────────────────────────────
// the v2 `sidebar-layout.json` shapes, read once by the migration
//
// Role: CORE
// Spec: sidebar-rework-implementation.md §P3.10
// READ-ONLY: the v2 `sidebar-layout.json` shapes, read once by `SidebarMigrationV2` and never written. Delete one
// release after the rework ships.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee;

public sealed class SidebarLayoutDocDto
{
    public int Version { get; set; }                         // REQUIRED. 2 = this schema (v1 upgrades by IDENTITY —
                                                               // see SidebarStoreV2.TryRead). 0/absent is not accepted.
    public long UpdatedAtMs { get; set; }                     // diagnostics only
    public string? AppVersion { get; set; }                   // diagnostics only ("written by")
    public SidebarPinDto[]? Pins { get; set; }
    public SidebarV3Dto? V3 { get; set; }
    public SidebarCuratedDto? Curated { get; set; }

    /// <summary>The shell top bar's customizable shortcut band: one global list shared by all three designs, on the
    /// envelope beside <c>pins</c> (not nested under <c>curated</c>). Item shape is <see cref="SidebarItemDto"/>
    /// verbatim. <c>null</c> ⇒ never customized ⇒ the built-in default; <c>[]</c> ⇒ the user emptied it on purpose —
    /// the two are different documents and both survive the round trip.</summary>
    public SidebarItemDto[]? TopBar { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One shared pin. <c>Kind</c> is the LEGACY form — the pre-unification <c>SidebarPinKind</c> byte as an int,
/// frozen forever in <see cref="SidebarStoreV2.TryLegacyPinKind"/> — kept on write so a build predating the
/// <c>SidebarPinKind</c>/<c>SidebarEntryKind</c> unification does not lose the pin on downgrade. <see cref="EntityKind"/>
/// is the preferred string form and is read first; <c>Kind</c> is consulted only when it is absent. Both are written on
/// every save.</summary>
public sealed class SidebarPinDto
{
    public string? Id { get; set; }                           // REQUIRED — the stable pin id
    public int Kind { get; set; }                              // LEGACY — see SidebarStoreV2.TryLegacyPinKind
    public string? EntityKind { get; set; }                    // "playlist"|"album"|"artist"|"show"|"playlistFolder"|
                                                                // "appRoute"|"track" — see SidebarPinWire.KindName
    public string? Uri { get; set; }
    public string? Name { get; set; }
    public long AddedAtMs { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SidebarV3Dto
{
    public string[]? CustomOrder { get; set; }                // entry ids in user order (playlists filter only)
    public string[]? ExpandedFolders { get; set; }            // folder ids currently expanded
    public SidebarFirstSeenDto[]? FirstSeen { get; set; }     // id → first-projection ms (added-at proxy)

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SidebarCuratedDto
{
    public string? TemplateId { get; set; }                   // the template the layout was seeded from (provenance)
    public SidebarSectionDto[]? Sections { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SidebarSectionDto
{
    public string? Id { get; set; }                           // REQUIRED, unique within the doc ("sec_xxxxxxxx")
    public string? Kind { get; set; }                          // SidebarSectionKind, lower-camel
    public string? Title { get; set; }                         // a user rename; null ⇒ TitleLocKey / the kind default
    public string? TitleLocKey { get; set; }
    public bool? Hidden { get; set; }                          // written only when true (keeps the file small)
    public bool? Collapsed { get; set; }
    public SidebarDisplayDto? Display { get; set; }           // null ⇒ SidebarDisplayOptions.Default
    public SidebarItemDto[]? Items { get; set; }
    public SidebarQueryDto? Query { get; set; }                // EntityList only
    public SidebarSectionDto[]? Children { get; set; }        // CustomGroup only; depth 1
    public SidebarExtensionDto? Extension { get; set; }       // v2: Extension only

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>v2 — the contributed section's ref. <c>Config</c> is carried as raw JSON and never inspected here, so a
/// config member — or a whole config shape — belonging to an extension this build has never heard of survives a
/// load/save round trip byte-for-byte. The only rule applied to it anywhere is the 64 KiB per-section cap
/// (<c>SidebarExtensionRef.MaxConfigBytes</c>), enforced by the reducer and re-checked at save time: over-cap is a
/// fault, never a truncation.</summary>
public sealed class SidebarExtensionDto
{
    public string? ExtensionId { get; set; }                   // REQUIRED ("wavee", "publisher.extension")
    public string? ContributionId { get; set; }                // REQUIRED ("artist.topTracks", "queue")
    public int? SchemaVersion { get; set; }                    // the CONFIG's schema version, declared by the source
    public JsonElement? Config { get; set; }                   // opaque; absent ⇒ {}

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>v2 — an item's persisted action binding. The app's action-id enum is never on the wire: a binding is a
/// namespaced (providerId, actionId) pair the registry resolves, so one written by a newer build, or whose extension is
/// currently missing, round-trips untouched and renders disabled.</summary>
public sealed class SidebarActionDto
{
    public string? ProviderId { get; set; }                    // REQUIRED ("wavee")
    public string? ActionId { get; set; }                      // REQUIRED ("play", "queue.addNext")
    public string? TargetMode { get; set; }                    // "none"|"fixedEntity"|"fixedTrack"|"nowPlaying"|"activeRoute"
    public string? TargetKey { get; set; }                     // the fixed modes' uri
    public JsonElement? Arguments { get; set; }                // opaque, like Config

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Every field nullable: only options that DIFFER from <see cref="SidebarDisplayOptions.Default"/> are
/// written, and reading applies the present fields over the default. Keeps the document small and makes adding a
/// display option non-breaking in both directions.</summary>
public sealed class SidebarDisplayDto
{
    public string? Density { get; set; }                       // "compact"|"cozy"|"comfortable"
    public string? Presentation { get; set; }                  // "list"|"grid"
    public bool? Artwork { get; set; }
    public bool? Subtitles { get; set; }
    public bool? CountBadges { get; set; }
    public bool? CollapsedByDefault { get; set; }
    public bool? ShowInRail { get; set; }
    public int? MaxItems { get; set; }
    public int? GridColumns { get; set; }
    public bool? InlineControls { get; set; }                  // EntityList only
    public bool? PlayButton { get; set; }                       // EntityEmbed only
    public string? Recents { get; set; }                        // JumpBackIn only: "visited"|"played"
    public string? EmptyBehavior { get; set; }                  // "default"|"hideBody"|"compactHint"|"actionCard"

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SidebarItemDto
{
    public string? Id { get; set; }                            // REQUIRED ("itm_xxxxxxxx")
    public string? Target { get; set; }                         // "route"|"entity"|"track"|"action"
    public string? Key { get; set; }                            // route name, or a spotify: uri
    public string? EntityKind { get; set; }                     // "none"|"playlist"|"album"|"artist"|"show"|"playlistFolder"|"track"
    public string? Label { get; set; }                          // SidebarItemSpec.LabelOverride
    public string? Icon { get; set; }                            // SidebarItemSpec.IconOverride (an Icons.* NAME)
    public string? FallbackTitle { get; set; }
    public string? FallbackImageUrl { get; set; }
    public bool? Hidden { get; set; }
    public SidebarActionDto? Action { get; set; }               // v2: set for target "action"

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SidebarQueryDto
{
    public string[]? Kinds { get; set; }                        // ["playlists","albums","artists","shows"]
    public string? Sort { get; set; }                            // "recents"|"recentlyAdded"|"alphabetical"|"creator"|"customOrder"
    public bool? Descending { get; set; }
    public string? Qualifier { get; set; }                       // "any"|"byYou"|"bySpotify"|"mixed"
    public string[]? IncludeUris { get; set; }                  // v2: "only these" allow-set (absent ⇒ no restriction)
    public string[]? ExcludeUris { get; set; }                  // v2: deny-set applied after the allow-set

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

// WriteIndented: the file is user-inspectable and tiny. CamelCase + WhenWritingNull are fixed on the CONTEXT so every
// call site inherits them — there is no loose JsonSerializerOptions anywhere in the sidebar persistence path.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(SidebarLayoutDocDto))]
public sealed partial class SidebarLayoutJsonCtx : JsonSerializerContext { }

/// <summary>The v2 file, read once. Null when it is absent or unreadable (the .bak is tried first) — a migration then
/// simply has nothing to carry.</summary>
public static class SidebarStoreV2
{
    public const string FileName = "sidebar-layout.json";

    public static SidebarLayoutDocDto? TryRead(string profileDir)
    {
        string path = Path.Combine(profileDir, FileName);
        return TryReadAt(path) ?? TryReadAt(path + ".bak");
    }

    static SidebarLayoutDocDto? TryReadAt(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var doc = JsonSerializer.Deserialize(File.ReadAllBytes(path), SidebarLayoutJsonCtx.Default.SidebarLayoutDocDto);
            return doc is { Version: >= 1 and <= 2 } ? doc : null;
        }
        catch (Exception ex) { Log.Warn("sidebar", "migration.v2_unreadable " + ex.GetType().Name); return null; }
    }

    /// <summary>The pre-unification pin kind table (Route=0, Playlist=1, Album=2, Artist=3, Show=4, Folder=5) — frozen.</summary>
    public static bool TryLegacyPinKind(int legacy, out SidebarEntryKind kind)
    {
        kind = legacy switch
        {
            1 => SidebarEntryKind.Playlist, 2 => SidebarEntryKind.Album, 3 => SidebarEntryKind.Artist,
            4 => SidebarEntryKind.Show, 5 => SidebarEntryKind.Folder, _ => SidebarEntryKind.AppRoute,
        };
        return (uint)legacy <= 5;
    }
}
