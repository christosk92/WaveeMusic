using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.Core.Sidebar;

namespace Wavee;

// ── The versioned sidebar-layout document: wire DTOs + the AOT source-generated context (F.3.2) ───────────────────────
// The HistoryJsonCtx / EntityJson precedent: everything persisted here goes through a JsonSerializerContext, never
// reflection. The DTOs are deliberately SEPARATE from the live records (Wavee.Core.Sidebar's SidebarCustomLayout &
// friends): the wire shape is flat, nullable-tolerant and versioned, so a shape change is an UPGRADE, never a silent
// data loss. Nothing in this file throws on an unknown or missing member.
//
// FORWARD COMPATIBILITY is a hard contract (locked decision 8 — preserve, don't destroy). Two mechanisms:
//   1. Section kinds are STRINGS on the wire (never renumbered), and an UNRECOGNIZED kind string round-trips
//      UNTOUCHED — it is preserved as an opaque section blob at its original index and re-emitted on the next save
//      (it renders as nothing). Dropping it would make opening a newer build's document destructive.
//   2. Unknown MEMBERS anywhere in the tree are captured by [JsonExtensionData] and re-attached on write, matched by
//      the owning section/item id. So a field a future build adds to a section a THIS build understands also survives.
//
// LAYOUT V2 (the extension-ready shape) adds three things and changes nothing else — the v1→v2 migration is IDENTITY, so
// an existing document loads unchanged and simply stamps "version": 2 on its next ordinary save:
//   * section `extension: { extensionId, contributionId, schemaVersion, config }` for kind "extension", with `config`
//     carried as RAW JSON (never inspected here — an unknown extension's config is opaque data, and mechanism 1 above
//     already covers a whole section kind this build cannot render);
//   * item `action: { providerId, actionId, targetMode, targetKey, arguments }` for target "action", where targetMode is
//     one of "none"/"fixedEntity"/"fixedTrack"/"nowPlaying"/"activeRoute" and an unknown mode degrades to "none";
//   * query `includeUris` / `excludeUris` — the "only these artists" allow/deny sets.
// The caps (64 KiB per section config, 2 MiB per document) live with their enforcement: SidebarExtensionRef.MaxConfigBytes
// in the reducer, SidebarLayoutStore.MaxDocumentBytes at the write. Over-cap is a save FAULT, never a truncation.
//
// Accessibility deviation from the spec sketch: the DTOs are `public`, not `internal`. `SidebarLayoutStore` is public
// and returns/accepts them (and `SidebarPreferences`' public ctor takes the store), so internal DTOs would be a CS0050
// inconsistent-accessibility error. Nothing outside this assembly consumes them.

public sealed class SidebarLayoutDocDto
{
    public int Version { get; set; }                        // REQUIRED. 2 = this schema (v1 upgrades by IDENTITY — see
                                                            // SidebarLayoutMigrations). 0/absent is NOT accepted (see store).
    public long UpdatedAtMs { get; set; }                   // diagnostics only
    public string? AppVersion { get; set; }                  // diagnostics only ("written by")
    public SidebarPinDto[]? Pins { get; set; }
    public SidebarV3Dto? V3 { get; set; }
    public SidebarCuratedDto? Curated { get; set; }

    /// <summary>The shell TOP BAR's customizable shortcut band (additive, still v2 — an absent member is simply "never
    /// customized"). It sits on the ENVELOPE, beside <c>pins</c>, not inside <c>curated</c>: the band is ONE GLOBAL list
    /// shared by all three sidebar designs, exactly like the pin list, and nesting it under the curated payload would
    /// imply it belongs to that one design's document.
    /// <para>Item shape is <see cref="SidebarItemDto"/> verbatim — the same targets, the same icon/label overrides and the
    /// same v2 <c>action</c> binding a section item carries — so nothing about resolution or rendering forks.</para>
    /// <para><c>null</c> ⇒ absent ⇒ the built-in default (Home). <c>[]</c> ⇒ the user emptied the band ON PURPOSE and it
    /// renders nothing; the two are different documents and both survive the round trip.</para></summary>
    public SidebarItemDto[]? TopBar { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One shared pin. <c>Kind</c> is the LEGACY form — the pre-unification <c>SidebarPinKind</c> byte as an int,
/// frozen forever in <see cref="SidebarLayoutWire.TryLegacyPinKind"/> — kept on WRITE so a build that predates the
/// <c>SidebarPinKind</c>/<c>SidebarEntryKind</c> unification (2026-08-19) does not lose the pin on downgrade.
/// <see cref="EntityKind"/> is the PREFERRED string form and is read first; <c>Kind</c> is only consulted when
/// <see cref="EntityKind"/> is absent (every profile written before this change). Both are written on every save —
/// the sidebar wire's preserve-don't-destroy rule for an additive field.</summary>
public sealed class SidebarPinDto
{
    public string? Id { get; set; }                          // REQUIRED — the stable pin id (F.5.4)
    public int Kind { get; set; }                             // LEGACY — see SidebarLayoutWire.TryLegacyPinKind
    public string? EntityKind { get; set; }                   // "playlist" | "album" | "artist" | "show" |
                                                               // "playlistFolder" | "appRoute" | "track" — see
                                                               // SidebarLayoutWire.PinKindName / TryParsePinKind
    public string? Uri { get; set; }
    public string? Name { get; set; }
    public long AddedAtMs { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SidebarV3Dto
{
    public string[]? CustomOrder { get; set; }               // entry ids in user order (playlists filter only)
    public string[]? ExpandedFolders { get; set; }           // folder ids currently expanded
    public SidebarFirstSeenDto[]? FirstSeen { get; set; }    // id → first-projection ms (the playlist added-at proxy, F.7.5)

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public readonly record struct SidebarFirstSeenDto(string Id, long Ms);

public sealed class SidebarCuratedDto
{
    public string? TemplateId { get; set; }                  // the template the layout was seeded from (provenance)
    public SidebarSectionDto[]? Sections { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SidebarSectionDto
{
    public string? Id { get; set; }                          // REQUIRED, unique within the doc ("sec_xxxxxxxx")
    public string? Kind { get; set; }                        // SidebarSectionKind, lower-camel (see SidebarLayoutWire)
    public string? Title { get; set; }                       // a USER rename; null ⇒ TitleLocKey / the kind default
    public string? TitleLocKey { get; set; }
    public bool? Hidden { get; set; }                        // written only when true (keeps the file small)
    public bool? Collapsed { get; set; }
    public SidebarDisplayDto? Display { get; set; }          // null ⇒ SidebarDisplayOptions.Default
    public SidebarItemDto[]? Items { get; set; }
    public SidebarQueryDto? Query { get; set; }              // EntityList only
    public SidebarSectionDto[]? Children { get; set; }       // CustomGroup only; depth 1
    public SidebarExtensionDto? Extension { get; set; }      // v2: Extension only

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>v2 — the contributed section's ref. <c>config</c> is carried as RAW JSON (a <see cref="JsonElement"/>): this
/// layer never inspects it, so a config member (or a whole config shape) belonging to an extension this build has never
/// heard of survives a load/save round trip byte-for-byte in meaning. The only rule applied to it anywhere is the 64 KiB
/// per-section cap (<see cref="SidebarExtensionRef.MaxConfigBytes"/>), enforced by the reducer and re-checked at save
/// time — over-cap is a FAULT, never a truncation.</summary>
public sealed class SidebarExtensionDto
{
    public string? ExtensionId { get; set; }                 // REQUIRED ("wavee", "publisher.extension")
    public string? ContributionId { get; set; }              // REQUIRED ("artist.topTracks", "queue")
    public int? SchemaVersion { get; set; }                  // the CONFIG's schema version, declared by the source
    public JsonElement? Config { get; set; }                 // opaque; absent ⇒ {}

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>v2 — an item's persisted action binding. The app's <c>ActionId</c> enum is NEVER on the wire: a binding is a
/// namespaced (providerId, actionId) pair the registry resolves, so a binding written by a newer build, or one whose
/// extension is currently missing, round-trips untouched and simply renders disabled.</summary>
public sealed class SidebarActionDto
{
    public string? ProviderId { get; set; }                  // REQUIRED ("wavee")
    public string? ActionId { get; set; }                    // REQUIRED ("play", "queue.addNext")
    public string? TargetMode { get; set; }                  // "none" | "fixedEntity" | "fixedTrack" | "nowPlaying" | "activeRoute"
    public string? TargetKey { get; set; }                   // the fixed modes' uri
    public JsonElement? Arguments { get; set; }              // opaque, like Config

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Every field nullable: only the options that DIFFER from <see cref="SidebarDisplayOptions.Default"/> are
/// written, and reading applies the present fields over the default. That keeps the document small and makes adding a
/// display option a non-breaking change in both directions.</summary>
public sealed class SidebarDisplayDto
{
    public string? Density { get; set; }                     // "compact" | "cozy" | "comfortable"
    public string? Presentation { get; set; }                // "list" | "grid"
    public bool? Artwork { get; set; }
    public bool? Subtitles { get; set; }
    public bool? CountBadges { get; set; }
    public bool? CollapsedByDefault { get; set; }
    public bool? ShowInRail { get; set; }
    public int? MaxItems { get; set; }
    public int? GridColumns { get; set; }
    public bool? InlineControls { get; set; }                 // EntityList only (§C1.8.6)
    public bool? PlayButton { get; set; }                     // EntityEmbed only (§C1.8.2)
    public string? Recents { get; set; }                      // JumpBackIn only (§C1.8.1): "visited" | "played"
    public string? EmptyBehavior { get; set; }                // "default" | "hideBody" | "compactHint" | "actionCard"

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SidebarItemDto
{
    public string? Id { get; set; }                          // REQUIRED ("itm_xxxxxxxx")
    public string? Target { get; set; }                      // "route" | "entity" | "track"
    public string? Key { get; set; }                         // route name, or a spotify: uri
    public string? EntityKind { get; set; }                  // "none" | "playlist" | "album" | "artist" | "show" | "playlistFolder" | "track"
    public string? Label { get; set; }                       // SidebarItemSpec.LabelOverride
    public string? Icon { get; set; }                        // SidebarItemSpec.IconOverride (an Icons.* NAME)
    public string? FallbackTitle { get; set; }
    public string? FallbackImageUrl { get; set; }
    public bool? Hidden { get; set; }
    public SidebarActionDto? Action { get; set; }            // v2: set for target "action"

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SidebarQueryDto
{
    public string[]? Kinds { get; set; }                     // ["playlists","albums","artists","shows"]
    public string? Sort { get; set; }                        // "recents" | "recentlyAdded" | "alphabetical" | "creator" | "customOrder"
    public bool? Descending { get; set; }
    public string? Qualifier { get; set; }                   // "any" | "byYou" | "bySpotify" | "mixed"
    public string[]? IncludeUris { get; set; }               // v2: "only these" allow-set (absent ⇒ no restriction)
    public string[]? ExcludeUris { get; set; }               // v2: deny-set applied after the allow-set

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

// WriteIndented: the file is user-inspectable and tiny. CamelCase + WhenWritingNull are the fixed serializer options
// from F.3.2.1 — declared on the CONTEXT so every call site inherits them (there is no loose JsonSerializerOptions).
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(SidebarLayoutDocDto))]
public sealed partial class SidebarLayoutJsonCtx : JsonSerializerContext { }

// ── model ⇄ wire ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The opaque forward-compatibility carry produced by <see cref="SidebarLayoutWire.ReadCurated"/>. It holds the
/// RAW section DTOs of a loaded document so that (a) sections whose <c>kind</c> string this build does not recognize are
/// re-emitted untouched at their original index, and (b) unknown MEMBERS on sections/items/display/query this build does
/// understand are re-attached on write. Hand the instance you got from <c>ReadCurated</c> back to
/// <see cref="SidebarLayoutWire.WriteCurated"/> on every snapshot; pass <see cref="Empty"/> for a fresh layout.</summary>
public sealed class SidebarWireCarry
{
    /// <summary>The carry for a document that was never loaded from disk (a template build / a fresh install).</summary>
    public static readonly SidebarWireCarry Empty = new();

    internal readonly Dictionary<string, SidebarSectionDto> Raw = new(StringComparer.Ordinal);
    internal readonly List<KeyValuePair<int, SidebarSectionDto>> Unknown = new();
    /// <summary>Raw top-bar tiles by item id, so an unknown MEMBER on a tile written by a newer build survives the model
    /// hop the same way a section item's does.</summary>
    internal readonly Dictionary<string, SidebarItemDto> RawTopBar = new(StringComparer.Ordinal);

    /// <summary>Unknown members on the CURATED payload object itself (not on a section). Captured/re-attached by
    /// <see cref="SidebarLayoutWire.ReadCurated"/>/<see cref="SidebarLayoutWire.WriteCurated"/>, because
    /// <c>WriteCurated</c> builds a FRESH <see cref="SidebarCuratedDto"/> and would otherwise drop them — which is exactly
    /// how an older build would have destroyed a newer build's additive payload field.</summary>
    internal Dictionary<string, JsonElement>? CuratedExtra;

    /// <summary>Unknown members on the document ENVELOPE (siblings of <c>pins</c>/<c>v3</c>/<c>curated</c>/<c>topBar</c>).
    /// Threaded by <c>SidebarPreferences</c>, which likewise rebuilds the envelope from scratch on every snapshot.</summary>
    internal Dictionary<string, JsonElement>? DocExtra;

    /// <summary>How many sections of a kind this build does not understand are being preserved.</summary>
    public int UnknownSectionCount => Unknown.Count;
    public bool IsEmpty => Raw.Count == 0 && Unknown.Count == 0 && RawTopBar.Count == 0
                        && CuratedExtra is null && DocExtra is null;

    /// <summary>Record the envelope's unknown members (the load path's one call). Null-tolerant.</summary>
    public void CaptureDoc(SidebarLayoutDocDto? doc) => DocExtra = doc?.Extra;

    /// <summary>Re-attach the envelope's unknown members onto a freshly built snapshot (the save path's one call).</summary>
    public void ReattachDoc(SidebarLayoutDocDto? doc)
    {
        if (doc is null) return;
        doc.Extra ??= DocExtra;
    }
}

/// <summary>The result of reading the curated payload: the typed layout plus the opaque carry that makes the next write
/// lossless.</summary>
public readonly record struct SidebarCuratedRead(SidebarCustomLayout Layout, SidebarWireCarry Carry);

/// <summary>The one translation layer between the persisted DTOs and Wavee.Core.Sidebar's live records. Every enum has
/// an explicit STRING form here (values are persisted — never rename one), and every unknown string degrades to the
/// nearest safe default rather than throwing.</summary>
public static class SidebarLayoutWire
{
    // ── section kind ──────────────────────────────────────────────────────────────────────────────────────────────────
    public static string KindName(SidebarSectionKind k) => k switch
    {
        SidebarSectionKind.Pinned => "pinned",
        SidebarSectionKind.JumpBackIn => "jumpBackIn",
        SidebarSectionKind.CollectionShortcuts => "collectionShortcuts",
        SidebarSectionKind.PlaylistTree => "playlistTree",
        SidebarSectionKind.EntityList => "entityList",
        SidebarSectionKind.StaticLinks => "staticLinks",
        SidebarSectionKind.CustomGroup => "customGroup",
        SidebarSectionKind.Header => "header",
        SidebarSectionKind.Divider => "divider",
        SidebarSectionKind.EntityEmbed => "entityEmbed",
        SidebarSectionKind.NewReleases => "newReleases",
        SidebarSectionKind.Concerts => "concerts",
        SidebarSectionKind.Extension => "extension",
        _ => "divider",   // unreachable for a known enum value; never emit an empty kind
    };

    /// <summary>Parse a wire kind. Returns false for a kind THIS build does not know — the caller preserves the raw
    /// section blob instead of dropping it (the binding unknown-kind ruling in the spec's synthesis notes).</summary>
    public static bool TryParseKind(string? s, out SidebarSectionKind kind)
    {
        switch (s)
        {
            case "pinned": kind = SidebarSectionKind.Pinned; return true;
            case "jumpBackIn": kind = SidebarSectionKind.JumpBackIn; return true;
            case "collectionShortcuts": kind = SidebarSectionKind.CollectionShortcuts; return true;
            case "playlistTree": kind = SidebarSectionKind.PlaylistTree; return true;
            case "entityList": kind = SidebarSectionKind.EntityList; return true;
            case "staticLinks": kind = SidebarSectionKind.StaticLinks; return true;
            case "customGroup": kind = SidebarSectionKind.CustomGroup; return true;
            case "header": kind = SidebarSectionKind.Header; return true;
            case "divider": kind = SidebarSectionKind.Divider; return true;
            case "entityEmbed": kind = SidebarSectionKind.EntityEmbed; return true;
            case "newReleases": kind = SidebarSectionKind.NewReleases; return true;
            case "concerts": kind = SidebarSectionKind.Concerts; return true;
            case "extension": kind = SidebarSectionKind.Extension; return true;
            default: kind = SidebarSectionKind.Divider; return false;
        }
    }

    // ── item target / entity kind ─────────────────────────────────────────────────────────────────────────────────────
    public static string TargetName(SidebarItemTarget t) => t switch
    {
        SidebarItemTarget.Entity => "entity",
        SidebarItemTarget.Track => "track",
        SidebarItemTarget.Action => "action",
        _ => "route",
    };

    public static SidebarItemTarget ParseTarget(string? s) => s switch
    {
        "entity" => SidebarItemTarget.Entity,
        "track" => SidebarItemTarget.Track,
        "action" => SidebarItemTarget.Action,
        _ => SidebarItemTarget.Route,
    };

    /// <summary>v2 — the action-binding target mode. An UNKNOWN mode string (a newer build's, or a typo in a hand-edited
    /// file) degrades to <see cref="SidebarActionTargetMode.None"/> without throwing: the row then renders
    /// visible-but-disabled instead of taking the wrong target.</summary>
    public static string TargetModeName(SidebarActionTargetMode m) => m switch
    {
        SidebarActionTargetMode.FixedEntity => "fixedEntity",
        SidebarActionTargetMode.FixedTrack => "fixedTrack",
        SidebarActionTargetMode.NowPlaying => "nowPlaying",
        SidebarActionTargetMode.ActiveRoute => "activeRoute",
        _ => "none",
    };

    public static SidebarActionTargetMode ParseTargetMode(string? s) => s switch
    {
        "fixedEntity" => SidebarActionTargetMode.FixedEntity,
        "fixedTrack" => SidebarActionTargetMode.FixedTrack,
        "nowPlaying" => SidebarActionTargetMode.NowPlaying,
        "activeRoute" => SidebarActionTargetMode.ActiveRoute,
        _ => SidebarActionTargetMode.None,
    };

    public static string EntityKindName(SidebarEntityKind k) => k switch
    {
        SidebarEntityKind.Playlist => "playlist",
        SidebarEntityKind.Album => "album",
        SidebarEntityKind.Artist => "artist",
        SidebarEntityKind.Show => "show",
        SidebarEntityKind.PlaylistFolder => "playlistFolder",
        SidebarEntityKind.Track => "track",
        _ => "none",
    };

    public static SidebarEntityKind ParseEntityKind(string? s) => s switch
    {
        "playlist" => SidebarEntityKind.Playlist,
        "album" => SidebarEntityKind.Album,
        "artist" => SidebarEntityKind.Artist,
        "show" => SidebarEntityKind.Show,
        "playlistFolder" => SidebarEntityKind.PlaylistFolder,
        "track" => SidebarEntityKind.Track,
        _ => SidebarEntityKind.None,
    };

    // ── pin kind (SidebarPin.Kind, a SidebarEntryKind — F.5.4, folded from the deleted SidebarPinKind 2026-08-19) ──────
    //
    // NOT the same enum as SidebarEntityKind above (that one is a curated section ITEM's target-kind restriction and is
    // untouched by this unification) — but the two are close enough in meaning that the wire reuses the same lower-camel
    // spellings for the five kinds they share ("playlist"/"album"/"artist"/"show"/"playlistFolder"), plus "appRoute" for
    // the bare-route family only a PIN (never a section item) can address, and "track" purely for degrade-explicitly
    // symmetry (see IsPinnable — writing it is unreachable).

    /// <summary>The preferred WIRE STRING for <see cref="SidebarPinDto.EntityKind"/>. Every <see cref="SidebarEntryKind"/>
    /// member has an explicit arm here, even <see cref="SidebarEntryKind.Track"/> (never actually written —
    /// <c>SidebarPinId.IsPinnable</c> refuses a track pin at creation): a future member LEFT OFF this switch falls
    /// through to the same default as an unknown prefix, but that miss is still caught — its round trip through
    /// <see cref="TryParsePinKind"/> back to a string would come back as <c>"appRoute"</c>, not its own name, which is
    /// exactly what the exhaustiveness test asserts on.</summary>
    public static string PinKindName(SidebarEntryKind k) => k switch
    {
        SidebarEntryKind.Playlist => "playlist",
        SidebarEntryKind.Album => "album",
        SidebarEntryKind.Artist => "artist",
        SidebarEntryKind.Show => "show",
        SidebarEntryKind.Folder => "playlistFolder",
        SidebarEntryKind.Track => "track",
        _ => "appRoute",   // AppRoute — no known-prefix pin id, matching SidebarPinId.KindOf's own default
    };

    /// <summary>Parse <see cref="SidebarPinDto.EntityKind"/>. False for an unrecognized string — the caller's job is to
    /// preserve-or-drop (there is no in-place default here to hide a future kind behind).</summary>
    public static bool TryParsePinKind(string? s, out SidebarEntryKind kind)
    {
        switch (s)
        {
            case "playlist": kind = SidebarEntryKind.Playlist; return true;
            case "album": kind = SidebarEntryKind.Album; return true;
            case "artist": kind = SidebarEntryKind.Artist; return true;
            case "show": kind = SidebarEntryKind.Show; return true;
            case "playlistFolder": kind = SidebarEntryKind.Folder; return true;
            case "appRoute": kind = SidebarEntryKind.AppRoute; return true;
            case "track": kind = SidebarEntryKind.Track; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>FROZEN LEGACY TABLE. The exact byte numbering of the deleted <c>SidebarPinKind</c> enum
    /// (<c>Route=0, Playlist=1, Album=2, Artist=3, Show=4, Folder=5</c>) — the ONLY place that numbering may still
    /// exist in the tree. Never edit these values: their sole job is decoding a <see cref="SidebarPinDto.Kind"/> int
    /// written by a build that predates the SidebarPinKind/SidebarEntryKind unification.</summary>
    static readonly SidebarEntryKind[] LegacyPinKindTable =
    [
        SidebarEntryKind.AppRoute,   // 0 = the old SidebarPinKind.Route
        SidebarEntryKind.Playlist,   // 1
        SidebarEntryKind.Album,      // 2
        SidebarEntryKind.Artist,     // 3
        SidebarEntryKind.Show,       // 4
        SidebarEntryKind.Folder,     // 5
    ];

    /// <summary>Decode a legacy <see cref="SidebarPinDto.Kind"/> int. False for a value outside 0..5 — the caller's job
    /// is to preserve-or-drop, exactly like <see cref="TryParsePinKind"/>.</summary>
    public static bool TryLegacyPinKind(int legacy, out SidebarEntryKind kind)
    {
        if ((uint)legacy < (uint)LegacyPinKindTable.Length) { kind = LegacyPinKindTable[legacy]; return true; }
        kind = default;
        return false;
    }

    /// <summary>The inverse of <see cref="LegacyPinKindTable"/> — written alongside <see cref="PinKindName"/> so a
    /// downgraded build (which only reads the legacy int) still sees the pin. There is no legacy slot for
    /// <see cref="SidebarEntryKind.Track"/> (a track could never have been pinned under the old scheme either); it
    /// writes the old Route slot only because some int must be written, never because a track pin can exist to reach
    /// this arm.</summary>
    public static int LegacyPinKindInt(SidebarEntryKind kind) => kind switch
    {
        SidebarEntryKind.Playlist => 1,
        SidebarEntryKind.Album => 2,
        SidebarEntryKind.Artist => 3,
        SidebarEntryKind.Show => 4,
        SidebarEntryKind.Folder => 5,
        _ => 0,   // AppRoute, and the unreachable Track
    };

    // ── display / query scalars ───────────────────────────────────────────────────────────────────────────────────────
    public static string DensityName(SidebarDensity d) => d switch
    {
        SidebarDensity.Compact => "compact",
        SidebarDensity.Comfortable => "comfortable",
        _ => "cozy",
    };

    public static SidebarDensity ParseDensity(string? s) => s switch
    {
        "compact" => SidebarDensity.Compact,
        "comfortable" => SidebarDensity.Comfortable,
        _ => SidebarDensity.Cozy,
    };

    public static string PresentationName(SidebarPresentation p) => p == SidebarPresentation.Grid ? "grid" : "list";
    public static SidebarPresentation ParsePresentation(string? s) => s == "grid" ? SidebarPresentation.Grid : SidebarPresentation.List;

    public static string RecentsName(SidebarRecentsSource r) => r == SidebarRecentsSource.Played ? "played" : "visited";
    public static SidebarRecentsSource ParseRecents(string? s) => s == "played" ? SidebarRecentsSource.Played : SidebarRecentsSource.Visited;

    public static string EmptyBehaviorName(SidebarEmptyBehavior behavior) => behavior switch
    {
        SidebarEmptyBehavior.HideBody => "hideBody",
        SidebarEmptyBehavior.CompactHint => "compactHint",
        SidebarEmptyBehavior.ActionCard => "actionCard",
        _ => "default",
    };

    public static SidebarEmptyBehavior ParseEmptyBehavior(string? value) => value switch
    {
        "hideBody" => SidebarEmptyBehavior.HideBody,
        "compactHint" => SidebarEmptyBehavior.CompactHint,
        "actionCard" => SidebarEmptyBehavior.ActionCard,
        _ => SidebarEmptyBehavior.Default,
    };

    public static string SortName(SidebarSortMode s) => s switch
    {
        SidebarSortMode.RecentlyAdded => "recentlyAdded",
        SidebarSortMode.Alphabetical => "alphabetical",
        SidebarSortMode.Creator => "creator",
        SidebarSortMode.CustomOrder => "customOrder",
        _ => "recents",
    };

    public static SidebarSortMode ParseSort(string? s) => s switch
    {
        "recentlyAdded" => SidebarSortMode.RecentlyAdded,
        "alphabetical" => SidebarSortMode.Alphabetical,
        "creator" => SidebarSortMode.Creator,
        "customOrder" => SidebarSortMode.CustomOrder,
        _ => SidebarSortMode.Recents,
    };

    public static string QualifierName(SidebarPlaylistQualifier q) => q switch
    {
        SidebarPlaylistQualifier.ByYou => "byYou",
        SidebarPlaylistQualifier.BySpotify => "bySpotify",
        SidebarPlaylistQualifier.Mixed => "mixed",
        _ => "any",
    };

    public static SidebarPlaylistQualifier ParseQualifier(string? s) => s switch
    {
        "byYou" => SidebarPlaylistQualifier.ByYou,
        "bySpotify" => SidebarPlaylistQualifier.BySpotify,
        "mixed" => SidebarPlaylistQualifier.Mixed,
        _ => SidebarPlaylistQualifier.Any,
    };

    /// <summary>The entity-kind FLAG set as a stable string array (a flag set is never a number on the wire: adding a
    /// kind must not renumber the others).</summary>
    public static string[] KindsNames(SidebarEntityKinds k)
    {
        var list = new List<string>(4);
        if ((k & SidebarEntityKinds.Playlists) != 0) list.Add("playlists");
        if ((k & SidebarEntityKinds.Albums) != 0) list.Add("albums");
        if ((k & SidebarEntityKinds.Artists) != 0) list.Add("artists");
        if ((k & SidebarEntityKinds.Shows) != 0) list.Add("shows");
        return list.ToArray();
    }

    public static SidebarEntityKinds ParseKinds(string[]? names)
    {
        if (names is null) return SidebarEntityKinds.All;
        var k = SidebarEntityKinds.None;
        for (int i = 0; i < names.Length; i++)
            k |= names[i] switch
            {
                "playlists" => SidebarEntityKinds.Playlists,
                "albums" => SidebarEntityKinds.Albums,
                "artists" => SidebarEntityKinds.Artists,
                "shows" => SidebarEntityKinds.Shows,
                _ => SidebarEntityKinds.None,   // a kind this build doesn't know — ignored for the QUERY, preserved in Extra
            };
        return k;
    }

    // ── curated payload: read ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Project the persisted curated payload onto the live model. Never throws: a missing payload yields the
    /// Blank layout, and any section whose kind string is unrecognized is moved into the returned carry rather than
    /// dropped.</summary>
    public static SidebarCuratedRead ReadCurated(SidebarCuratedDto? dto)
    {
        var carry = new SidebarWireCarry();
        if (dto is null) return new SidebarCuratedRead(SidebarCustomLayout.Empty, carry);
        carry.CuratedExtra = dto.Extra;

        var sections = new List<SidebarSectionSpec>(dto.Sections?.Length ?? 0);
        var raw = dto.Sections;
        if (raw is not null)
            for (int i = 0; i < raw.Length; i++)
            {
                var s = raw[i];
                if (s is null) continue;
                if (!TryParseKind(s.Kind, out var kind))
                {
                    carry.Unknown.Add(new KeyValuePair<int, SidebarSectionDto>(i, s));
                    continue;
                }
                sections.Add(ReadSection(s, kind, carry, depth: 0));
            }

        string templateId = string.IsNullOrEmpty(dto.TemplateId) ? SidebarTemplates.Curated : dto.TemplateId!;
        return new SidebarCuratedRead(new SidebarCustomLayout(templateId, sections), carry);
    }

    static SidebarSectionSpec ReadSection(SidebarSectionDto s, SidebarSectionKind kind, SidebarWireCarry carry, int depth)
    {
        string id = string.IsNullOrEmpty(s.Id) ? NewId("sec_") : s.Id!;
        if (!carry.Raw.ContainsKey(id)) carry.Raw[id] = s;

        List<SidebarItemSpec>? items = null;
        if (s.Items is { Length: > 0 } rawItems)
        {
            items = new List<SidebarItemSpec>(rawItems.Length);
            for (int i = 0; i < rawItems.Length; i++)
                if (rawItems[i] is { } it) items.Add(ReadItem(it));
        }

        List<SidebarSectionSpec>? children = null;
        if (depth == 0 && s.Children is { Length: > 0 } rawKids)
        {
            children = new List<SidebarSectionSpec>(rawKids.Length);
            for (int i = 0; i < rawKids.Length; i++)
            {
                var c = rawKids[i];
                if (c is null) continue;
                // A child of an unknown kind cannot be represented one level down and would be lost; keep it as a
                // top-level opaque blob instead (still never dropped) — the depth-1 model has nowhere else to put it.
                if (!TryParseKind(c.Kind, out var ck)) { carry.Unknown.Add(new KeyValuePair<int, SidebarSectionDto>(int.MaxValue, c)); continue; }
                children.Add(ReadSection(c, ck, carry, depth: 1));
            }
        }

        return new SidebarSectionSpec(id, kind)
        {
            Title = string.IsNullOrEmpty(s.Title) ? null : s.Title,
            TitleLocKey = string.IsNullOrEmpty(s.TitleLocKey) ? null : s.TitleLocKey,
            Hidden = s.Hidden ?? false,
            Collapsed = s.Collapsed ?? false,
            Display = ReadDisplay(s.Display),
            Items = items,
            Query = ReadQuery(s.Query),
            Children = children,
            Extension = ReadExtension(s.Extension),
        };
    }

    static SidebarItemSpec ReadItem(SidebarItemDto it) =>
        new(string.IsNullOrEmpty(it.Id) ? NewId("itm_") : it.Id!,
            ParseTarget(it.Target),
            it.Key ?? "")
        {
            EntityKind = ParseEntityKind(it.EntityKind),
            LabelOverride = string.IsNullOrEmpty(it.Label) ? null : it.Label,
            IconOverride = string.IsNullOrEmpty(it.Icon) ? null : it.Icon,
            FallbackTitle = string.IsNullOrEmpty(it.FallbackTitle) ? null : it.FallbackTitle,
            FallbackImageUrl = string.IsNullOrEmpty(it.FallbackImageUrl) ? null : it.FallbackImageUrl,
            Hidden = it.Hidden ?? false,
            Action = ReadAction(it.Action),
        };

    /// <summary>v2 — the contribution ref. A payload with no usable ids yields null (the section then renders the
    /// "Manage extension" placeholder and is never auto-removed); the config is taken verbatim, defaulting to {}.</summary>
    static SidebarExtensionRef? ReadExtension(SidebarExtensionDto? x)
    {
        if (x is null) return null;
        string ext = x.ExtensionId ?? "";
        string contribution = x.ContributionId ?? "";
        if (ext.Length == 0 && contribution.Length == 0) return null;
        var config = x.Config is { } raw ? SidebarJson.Own(raw) : SidebarJson.EmptyObject;
        return new SidebarExtensionRef(ext, contribution, x.SchemaVersion ?? 1, config);
    }

    /// <summary>v2 — the action binding. An id-less payload yields null (nothing to invoke); an unknown target-mode
    /// string degrades to None. Never throws.</summary>
    static SidebarActionBinding? ReadAction(SidebarActionDto? a)
    {
        if (a is null) return null;
        string provider = a.ProviderId ?? "";
        string action = a.ActionId ?? "";
        if (provider.Length == 0 || action.Length == 0) return null;
        return new SidebarActionBinding(provider, action, ParseTargetMode(a.TargetMode),
            string.IsNullOrEmpty(a.TargetKey) ? null : a.TargetKey, SidebarJson.Own(a.Arguments));
    }

    /// <summary>Apply the PRESENT display fields over <see cref="SidebarDisplayOptions.Default"/>. Returns null when the
    /// payload carried no options at all, so <c>SidebarSectionSpec.Display == null</c> keeps its "== Default" meaning.</summary>
    static SidebarDisplayOptions? ReadDisplay(SidebarDisplayDto? d)
    {
        if (d is null) return null;
        var o = SidebarDisplayOptions.Default;
        if (d.Density is not null) o = o with { Density = ParseDensity(d.Density) };
        if (d.Presentation is not null) o = o with { Presentation = ParsePresentation(d.Presentation) };
        if (d.Artwork is { } artwork) o = o with { Artwork = artwork };
        if (d.Subtitles is { } subtitles) o = o with { Subtitles = subtitles };
        if (d.CountBadges is { } counts) o = o with { CountBadges = counts };
        if (d.CollapsedByDefault is { } cbd) o = o with { CollapsedByDefault = cbd };
        if (d.ShowInRail is { } rail) o = o with { ShowInRail = rail };
        if (d.MaxItems is { } max) o = o with { MaxItems = max };
        if (d.GridColumns is { } cols) o = o with { GridColumns = cols };
        if (d.InlineControls is { } inline) o = o with { InlineControls = inline };
        if (d.PlayButton is { } play) o = o with { PlayButton = play };
        if (d.Recents is not null) o = o with { Recents = ParseRecents(d.Recents) };
        if (d.EmptyBehavior is not null) o = o with { EmptyBehavior = ParseEmptyBehavior(d.EmptyBehavior) };
        return o;
    }

    static SidebarEntityQuery? ReadQuery(SidebarQueryDto? q)
    {
        if (q is null) return null;
        var v = SidebarEntityQuery.Default;
        if (q.Kinds is not null) v = v with { Kinds = ParseKinds(q.Kinds) };
        if (q.Sort is not null) v = v with { Sort = ParseSort(q.Sort) };
        if (q.Descending is { } desc) v = v with { Descending = desc };
        if (q.Qualifier is not null) v = v with { Qualifier = ParseQualifier(q.Qualifier) };
        // v2 uri sets: `[]` on the wire reads back as null (the model's "no restriction"), so an empty set can never be
        // mistaken for "include nothing".
        var include = UriSet(q.IncludeUris);
        var exclude = UriSet(q.ExcludeUris);
        if (include is not null || exclude is not null)
            v = v with { IncludeUris = include, ExcludeUris = exclude };
        return v;
    }

    /// <summary>Drop null/blank entries; an empty result is null. Order and duplicates are the reducer's business
    /// (<c>SidebarLayoutReducer.NormalizeUris</c>) — reading stays faithful to the file.</summary>
    static IReadOnlyList<string>? UriSet(string[]? uris)
    {
        if (uris is null || uris.Length == 0) return null;
        var list = new List<string>(uris.Length);
        for (int i = 0; i < uris.Length; i++)
            if (!string.IsNullOrEmpty(uris[i])) list.Add(uris[i]);
        return list.Count == 0 ? null : list;
    }

    // ── curated payload: write ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Serialize the live layout back onto the wire, re-attaching everything <paramref name="carry"/> preserved:
    /// unknown members (matched by section/item id) and unknown-kind sections (re-inserted at their recorded index).</summary>
    public static SidebarCuratedDto WriteCurated(SidebarCustomLayout layout, SidebarWireCarry? carry)
    {
        carry ??= SidebarWireCarry.Empty;
        var list = new List<SidebarSectionDto>(layout.Sections.Count + carry.Unknown.Count);
        for (int i = 0; i < layout.Sections.Count; i++) list.Add(WriteSection(layout.Sections[i], carry, depth: 0));

        // Re-insert the opaque sections at their original indices (ascending, clamped) so a newer build's layout keeps
        // its authored order when this build saves over it.
        if (carry.Unknown.Count > 0)
        {
            var pending = new List<KeyValuePair<int, SidebarSectionDto>>(carry.Unknown);
            pending.Sort(static (a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < pending.Count; i++)
            {
                int at = pending[i].Key;
                if (at < 0) at = 0;
                if (at > list.Count) at = list.Count;
                list.Insert(at, pending[i].Value);
            }
        }

        return new SidebarCuratedDto
        {
            TemplateId = layout.TemplateId,
            Sections = list.ToArray(),
            Extra = carry.CuratedExtra,
        };
    }

    // ── the shell TOP BAR band (envelope-level, one global list) ──────────────────────────────────────────────────────

    /// <summary>Project the persisted band onto the model. <c>null</c> (member absent) stays null — "never customized",
    /// which the model resolves to <c>SidebarCustomLayout.DefaultTopBar</c>. An EMPTY array stays an EMPTY list, because
    /// "the user removed every shortcut" is a real state and must not silently restore Home.</summary>
    public static IReadOnlyList<SidebarItemSpec>? ReadTopBar(SidebarItemDto[]? dto, SidebarWireCarry? carry = null)
    {
        if (dto is null) return null;
        var items = new List<SidebarItemSpec>(dto.Length);
        for (int i = 0; i < dto.Length; i++)
        {
            if (dto[i] is not { } raw) continue;
            var item = ReadItem(raw);
            items.Add(item);
            if (carry is not null && !carry.RawTopBar.ContainsKey(item.Id)) carry.RawTopBar[item.Id] = raw;
        }
        return items;
    }

    /// <summary>Serialize the band back. <c>null</c> ⇒ null (the member is omitted by <c>WhenWritingNull</c>); an empty list
    /// ⇒ <c>[]</c>, which MUST be written so the emptied state survives.</summary>
    public static SidebarItemDto[]? WriteTopBar(IReadOnlyList<SidebarItemSpec>? band, SidebarWireCarry? carry = null)
    {
        if (band is null) return null;
        var arr = new SidebarItemDto[band.Count];
        for (int i = 0; i < arr.Length; i++)
        {
            arr[i] = WriteItem(band[i]);
            if (carry is not null && carry.RawTopBar.TryGetValue(band[i].Id, out var raw))
            {
                arr[i].Extra ??= raw.Extra;
                if (arr[i].Action is { } a && raw.Action is { } ra) a.Extra ??= ra.Extra;
            }
        }
        return arr;
    }

    static SidebarSectionDto WriteSection(SidebarSectionSpec s, SidebarWireCarry carry, int depth)
    {
        SidebarItemDto[]? items = null;
        if (s.ItemList.Count > 0)
        {
            items = new SidebarItemDto[s.ItemList.Count];
            for (int i = 0; i < items.Length; i++) items[i] = WriteItem(s.ItemList[i]);
        }

        SidebarSectionDto[]? children = null;
        if (depth == 0 && s.ChildList.Count > 0)
        {
            children = new SidebarSectionDto[s.ChildList.Count];
            for (int i = 0; i < children.Length; i++) children[i] = WriteSection(s.ChildList[i], carry, depth: 1);
        }

        var dto = new SidebarSectionDto
        {
            Id = s.Id,
            Kind = KindName(s.Kind),
            Title = s.Title,
            TitleLocKey = s.TitleLocKey,
            Hidden = s.Hidden ? true : null,
            Collapsed = s.Collapsed ? true : null,
            Display = WriteDisplay(s.Display),
            Items = items,
            Query = WriteQuery(s.Query),
            Children = children,
            Extension = WriteExtension(s.Extension),
        };

        if (carry.Raw.TryGetValue(s.Id, out var raw)) Reattach(dto, raw);
        return dto;
    }

    static void Reattach(SidebarSectionDto dto, SidebarSectionDto raw)
    {
        dto.Extra ??= raw.Extra;
        if (dto.Display is { } d && raw.Display is { } rd) d.Extra ??= rd.Extra;
        if (dto.Query is { } q && raw.Query is { } rq) q.Extra ??= rq.Extra;
        // v2: an unknown member on the extension payload itself (a future ref field, not a config member — the config is
        // opaque and travels whole) survives the model hop the same way.
        if (dto.Extension is { } x && raw.Extension is { } rx) x.Extra ??= rx.Extra;
        if (dto.Items is { } items && raw.Items is { } rawItems)
            for (int i = 0; i < items.Length; i++)
                for (int j = 0; j < rawItems.Length; j++)
                    if (rawItems[j] is { } ri && string.Equals(items[i].Id, ri.Id, StringComparison.Ordinal))
                    {
                        items[i].Extra ??= ri.Extra;
                        if (items[i].Action is { } a && ri.Action is { } ra) a.Extra ??= ra.Extra;
                        break;
                    }
    }

    static SidebarItemDto WriteItem(SidebarItemSpec it) => new()
    {
        Id = it.Id,
        Target = TargetName(it.Target),
        Key = it.Key,
        EntityKind = it.EntityKind == SidebarEntityKind.None ? null : EntityKindName(it.EntityKind),
        Label = it.LabelOverride,
        Icon = it.IconOverride,
        FallbackTitle = it.FallbackTitle,
        FallbackImageUrl = it.FallbackImageUrl,
        Hidden = it.Hidden ? true : null,
        Action = WriteAction(it.Action),
    };

    /// <summary>v2 — always writes both ids and the schema version (a ref with a missing id is what "unbound" looks like
    /// on the wire, and losing it would silently delete the section's identity). The config is emitted verbatim; an
    /// empty <c>{}</c> is still written, because "the section has a config object" is itself information.</summary>
    static SidebarExtensionDto? WriteExtension(SidebarExtensionRef? x) => x is null ? null : new SidebarExtensionDto
    {
        ExtensionId = x.ExtensionId,
        ContributionId = x.ContributionId,
        SchemaVersion = x.SchemaVersion,
        Config = x.Config.ValueKind == JsonValueKind.Undefined ? SidebarJson.EmptyObject : x.Config,
    };

    static SidebarActionDto? WriteAction(SidebarActionBinding? a) => a is null ? null : new SidebarActionDto
    {
        ProviderId = a.ProviderId,
        ActionId = a.ActionId,
        TargetMode = TargetModeName(a.TargetMode),
        TargetKey = a.TargetKey,
        Arguments = a.Arguments,
    };

    /// <summary>Write only the fields that DIFFER from the default (F.3.2.1's "keeps JSON small"), so the round trip is
    /// exact but a default-valued section costs no bytes. Returns null when nothing differs.</summary>
    static SidebarDisplayDto? WriteDisplay(SidebarDisplayOptions? o)
    {
        if (o is null) return null;
        var def = SidebarDisplayOptions.Default;
        var d = new SidebarDisplayDto();
        bool any = false;
        if (o.Density != def.Density) { d.Density = DensityName(o.Density); any = true; }
        if (o.Presentation != def.Presentation) { d.Presentation = PresentationName(o.Presentation); any = true; }
        if (o.Artwork != def.Artwork) { d.Artwork = o.Artwork; any = true; }
        if (o.Subtitles != def.Subtitles) { d.Subtitles = o.Subtitles; any = true; }
        if (o.CountBadges != def.CountBadges) { d.CountBadges = o.CountBadges; any = true; }
        if (o.CollapsedByDefault != def.CollapsedByDefault) { d.CollapsedByDefault = o.CollapsedByDefault; any = true; }
        if (o.ShowInRail != def.ShowInRail) { d.ShowInRail = o.ShowInRail; any = true; }
        if (o.MaxItems != def.MaxItems) { d.MaxItems = o.MaxItems; any = true; }
        if (o.GridColumns != def.GridColumns) { d.GridColumns = o.GridColumns; any = true; }
        if (o.InlineControls != def.InlineControls) { d.InlineControls = o.InlineControls; any = true; }
        if (o.PlayButton != def.PlayButton) { d.PlayButton = o.PlayButton; any = true; }
        if (o.Recents != def.Recents) { d.Recents = RecentsName(o.Recents); any = true; }
        if (o.EmptyBehavior != def.EmptyBehavior) { d.EmptyBehavior = EmptyBehaviorName(o.EmptyBehavior); any = true; }
        // An all-default Display still has to survive as NON-null (the section explicitly carried options), so emit the
        // one cheapest discriminating field rather than collapsing it to null and changing the model on the way back.
        if (!any) d.Density = DensityName(def.Density);
        return d;
    }

    static SidebarQueryDto? WriteQuery(SidebarEntityQuery? q)
    {
        if (q is null) return null;
        var def = SidebarEntityQuery.Default;
        var d = new SidebarQueryDto();
        bool any = false;
        if (q.Kinds != def.Kinds) { d.Kinds = KindsNames(q.Kinds); any = true; }
        if (q.Sort != def.Sort) { d.Sort = SortName(q.Sort); any = true; }
        if (q.Descending != def.Descending) { d.Descending = q.Descending; any = true; }
        if (q.Qualifier != def.Qualifier) { d.Qualifier = QualifierName(q.Qualifier); any = true; }
        if (q.IncludeUris is { Count: > 0 } include) { d.IncludeUris = ToArray(include); any = true; }
        if (q.ExcludeUris is { Count: > 0 } exclude) { d.ExcludeUris = ToArray(exclude); any = true; }
        if (!any) d.Sort = SortName(def.Sort);
        return d;
    }

    static string[] ToArray(IReadOnlyList<string> uris)
    {
        var arr = new string[uris.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = uris[i];
        return arr;
    }

    static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("n")[..8];
}
