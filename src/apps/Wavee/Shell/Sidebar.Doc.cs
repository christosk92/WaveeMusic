// ── Shell/Sidebar.Doc.cs ───────────────────────────────────────────────────────────────────────────────────────────
// the sidebar-layout.json document + reducer + wire + migrations + defaults
//
// Role: CORE
// Owner: J
// Wave: 4
// Budget: 4000 lines
// Spec: ch 25 §9
//
// The user's sidebar, as data. Four concerns, one file, because they are one concern wearing four hats: the document
// (`SidebarCustomLayout` and the section/item/query/display shapes under it), the ONE pure reducer every Curated
// mutation goes through, the `sidebar-layout.json` wire that reads and writes it, and the version ladder that carries
// an old file forward. Nothing here opens a stream — `Sidebar.Host.cs` owns the disk, the debounce and the .bak.
//
// THREE RULES THIS FILE EXISTS TO KEEP.
//
// 1. **The on-disk format is 0.2.9's, byte for byte.** An existing `%LOCALAPPDATA%\Wavee\sidebar-layout.json` must
//    load into this build unchanged. Every `[JsonPropertyName]`, every default, every version constant is frozen.
//    Persisted enums are APPEND-ONLY and never renumbered.
// 2. **Nothing vanishes.** An unrecognised section kind round-trips untouched at its original index (it renders as
//    nothing, is still listed under Hidden sections, and is still counted by the budget), and unknown MEMBERS anywhere
//    survive through `[JsonExtensionData]` re-attached by owning id (`SidebarWireCarry`). Preserve, never destroy.
// 3. **A rejected command changes nothing.** `SidebarLayoutReducer.Apply` returns `Changed = false` plus one of the 13
//    `SidebarRejectReason`s, and `Sidebar.Dispatch` (SHELL) then pushes no undo entry, bumps no version and writes no
//    file. That is what makes the customizer's controls snap back honestly instead of lying about what was saved.
//
// NativeAOT: source-generated JSON only (`SidebarLayoutJsonCtx`). No reflection-based serialization anywhere.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee;

// ── 0. the document's own entry points ───────────────────────────────────────────────────────────────────────────────
//
// `Sidebar.Doc.cs` is CORE: the document, the reducer, the wire and the migrations, and nothing that touches a disk.
// The three names below are the only ones a caller outside this file needs — everything else in the file is the shape
// they speak. The FILE I/O lives in `Sidebar.Host.cs`; this half never opens a stream.

public static partial class Sidebar
{
    /// <summary>The user's layout document on disk. It is USER DATA: never deleted on a version bump, never rewritten
    /// when it cannot be parsed (see <see cref="SidebarLayoutStore"/>'s corrupt policy).</summary>
    public const string LayoutFileName = "sidebar-layout.json";

    /// <summary>The one pure fold. `Sidebar.Dispatch` (SHELL) is what the app calls — it wraps this with the undo
    /// push, the version bump and the autosave, and returns early when <c>Changed</c> is false.</summary>
    public static SidebarCommandResult Reduce(SidebarCustomLayout layout, SidebarCommand command,
                                              IReadOnlySet<string>? pinnedKeys = null)
        => SidebarLayoutReducer.Apply(layout, command, pinnedKeys);

    /// <summary>The fresh-install AND corrupt-fallback document (Wavee Curated).</summary>
    public static SidebarCustomLayout DefaultLayout() => SidebarLayoutDefaults.CuratedLayout();
}
// ── SIDEBAR LAYOUT DOCUMENT MODEL ──────────────────────────────────────────────────────────────────────────────
//
// The Mode C ("Wavee Curated") custom-sidebar PAYLOAD MODEL — the document the customizer edits, the reducer
// rewrites, the row planner projects and the versioned sidebar-layout.json carries. Framework-neutral (no engine
// type in any shape — glyph *names* live here, glyph *codepoints* app-side) so Wavee.Tests can exercise it directly.
//
// NO POLYMORPHIC JSON. A section is a single closed record discriminated by a Kind byte with per-kind-optional
// fields — never an abstract hierarchy with [JsonDerivedType]. The document must survive AOT source-gen
// serialization with zero reflection risk, and an unknown (future-build) section kind or member must round-trip
// untouched rather than being dropped. Commands (the reducer's command hierarchy) are in-memory only and never
// serialized — this file is the wire shape only.

/// <summary>What a section IS. Persisted — append only, never renumber. An unknown future value must round-trip
/// untouched and render as a skipped section.</summary>
public enum SidebarSectionKind : byte
{
    Pinned              = 0,  // the shared, unlimited pin store, in pin order
    JumpBackIn          = 1,  // recency from history (visited) or the play log (played), deduped by uri
    CollectionShortcuts = 2,  // fixed app destinations: liked / albums / artists / podcasts / local
    PlaylistTree        = 3,  // the folder-aware rootlist tree (recursive PlaylistFolder)
    EntityList          = 4,  // a dynamic query over the library projection (the V3 projection)
    StaticLinks         = 5,  // hand-picked app routes (home / search / history / settings / api-console)
    CustomGroup         = 6,  // a user-named group of hand-picked entities/routes/tracks; ONE level of nesting
    Header              = 7,  // a text-only group label (no rows)
    Divider             = 8,  // a 1px rule + 8-DIP lead-in
    EntityEmbed         = 9,  // ONE spotlighted entity (playlist/album/artist/show) as a hero card with play
    NewReleases         = 10, // top-N new releases from followed artists
    Concerts            = 11, // top-N upcoming concerts near the user
    Extension           = 12, // LAYOUT V2: a CONTRIBUTED section — rows come from the resolved data source, never
                              // a hand-authored Items list; first-party dynamic feeds use ExtensionId "wavee".
}

public enum SidebarDensity : byte { Compact = 0, Cozy = 1, Comfortable = 2 }

public enum SidebarPresentation : byte { List = 0, Grid = 1 }

/// <summary>What an item points at. Route/Entity NAVIGATE on click; Track PLAYS on click; Action INVOKES its
/// <see cref="SidebarItemSpec.Action"/> binding (LAYOUT V2). The pin store still excludes tracks (locked decision 4).</summary>
public enum SidebarItemTarget : byte { Route = 0, Entity = 1, Track = 2, Action = 3 }

/// <summary>How a bound action gets its target at invoke time. Fixed* reads <see cref="SidebarActionBinding.TargetKey"/>;
/// NowPlaying/ActiveRoute read live app state; None is context-free. Persisted as a STRING on the wire — append
/// only, never renumber.</summary>
public enum SidebarActionTargetMode : byte { None, FixedEntity, FixedTrack, NowPlaying, ActiveRoute }

/// <summary>Null-safe <see cref="JsonElement"/> plumbing for the v2 payload records. A <c>default(JsonElement)</c>
/// has <see cref="JsonValueKind.Undefined"/> and THROWS from <c>GetRawText()</c>/<c>Clone()</c>, so every path
/// through the model goes through here. Nothing in this class throws.</summary>
public static class SidebarJson
{
    /// <summary>A detached, reusable <c>{}</c> — the config an extension ref gets when the wire carried none.</summary>
    public static JsonElement EmptyObject { get; } = Detach("{}");

    /// <summary>Parse into an element that owns its own backing document (survives the parse buffer).</summary>
    public static JsonElement Detach(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>Self-contained copy; an Undefined element normalizes to <see cref="EmptyObject"/>.</summary>
    public static JsonElement Own(JsonElement e)
        => e.ValueKind == JsonValueKind.Undefined ? EmptyObject : e.Clone();

    /// <summary>Self-contained copy of an optional element; Undefined normalizes to null (absent, not empty).</summary>
    public static JsonElement? Own(JsonElement? e)
        => e is { } v && v.ValueKind != JsonValueKind.Undefined ? v.Clone() : null;

    /// <summary>The element re-emitted COMPACTLY — the canonical identity of a config/arguments payload ("" for
    /// Undefined). <c>GetRawText()</c> is deliberately NOT used: it returns the ORIGINAL source span, so a config
    /// read back out of the indented on-disk document would never string-compare equal to the one that wrote it.
    /// Re-emitting through a writer is a fixed point across further write/parse cycles. Property ORDER is
    /// significant — a reorder is a real change to the document.</summary>
    public static string Canonical(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Undefined) return "";
        var buffer = Write(e);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string? Canonical(JsonElement? e) => e is { } v ? Canonical(v) : null;

    /// <summary>UTF-8 byte length of the compact serialized form — what the 64 KiB config cap counts (the on-disk
    /// document is indented and therefore larger; the cap is on the PAYLOAD, measured the same way everywhere).</summary>
    public static int ByteCount(JsonElement e)
        => e.ValueKind == JsonValueKind.Undefined ? 0 : Write(e).WrittenCount;

    public static int ByteCount(JsonElement? e) => e is { } v ? ByteCount(v) : 0;

    public static bool Same(JsonElement a, JsonElement b)
        => a.ValueKind == b.ValueKind && string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);

    public static bool Same(JsonElement? a, JsonElement? b)
        => string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);

    static System.Buffers.ArrayBufferWriter<byte> Write(JsonElement e)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer)) e.WriteTo(writer);
        return buffer;
    }
}

/// <summary>LAYOUT V2 — which extension contribution a <see cref="SidebarSectionKind.Extension"/> section renders,
/// plus the contribution's own opaque configuration. <paramref name="SchemaVersion"/> is the CONFIG's schema
/// version as declared by the contributing source, so a source can migrate its own config without a document
/// migration. <paramref name="Config"/> is never interpreted here: an unknown extension id or config member
/// round-trips untouched, and runtime extension state/secrets NEVER enter this record.</summary>
public sealed record SidebarExtensionRef(string ExtensionId, string ContributionId, int SchemaVersion, JsonElement Config)
{
    /// <summary>The per-section configuration cap. Enforced by the reducer (SetExtensionConfig ⇒ ConfigTooLarge)
    /// and re-checked at save time against a hand-edited document.</summary>
    public const int MaxConfigBytes = 64 * 1024;

    /// <summary>A ref with an empty <c>{}</c> config — what the palette adds before the inspector edits anything.</summary>
    public static SidebarExtensionRef For(string extensionId, string contributionId, int schemaVersion = 1)
        => new(extensionId, contributionId, schemaVersion, SidebarJson.EmptyObject);

    public int ConfigByteCount => SidebarJson.ByteCount(Config);

    /// <summary>False for a ref that cannot address a contribution (either id blank) — the reducer rejects those.</summary>
    public bool IsWellFormed => !string.IsNullOrEmpty(ExtensionId) && !string.IsNullOrEmpty(ContributionId);

    /// <summary>The registry lookup key ("wavee/artist.topTracks").</summary>
    public string ContributionKey => ExtensionId + "/" + ContributionId;

    // JsonElement has NO content equality (a synthesized record comparison hits ValueType.Equals — the backing
    // document by reference), so a config round-tripped through JSON would never equal the one it came from. Both
    // members are hand-declared and compare CANONICAL JSON instead.
    public bool Equals(SidebarExtensionRef? other)
        => other is not null &&
           string.Equals(ExtensionId, other.ExtensionId, StringComparison.Ordinal) &&
           string.Equals(ContributionId, other.ContributionId, StringComparison.Ordinal) &&
           SchemaVersion == other.SchemaVersion &&
           SidebarJson.Same(Config, other.Config);

    // The config is deliberately NOT hashed: equality REQUIRES both ids and the schema version to match, so this
    // stays a valid hash without re-serializing a payload on every lookup.
    public override int GetHashCode() => HashCode.Combine(
        ExtensionId is null ? 0 : StringComparer.Ordinal.GetHashCode(ExtensionId),
        ContributionId is null ? 0 : StringComparer.Ordinal.GetHashCode(ContributionId),
        SchemaVersion);
}

/// <summary>LAYOUT V2 — a persisted action binding for a <see cref="SidebarItemTarget.Action"/> item.
/// <paramref name="ProviderId"/>/<paramref name="ActionId"/> are the registry's namespaced stable key (first-party
/// is literally "wavee", e.g. wavee + play). The layout NEVER stores an ActionId enum value, so a binding written
/// by a newer build or a currently-missing extension round-trips untouched and renders disabled.</summary>
public sealed record SidebarActionBinding(string ProviderId, string ActionId, SidebarActionTargetMode TargetMode,
    string? TargetKey, JsonElement? Arguments)
{
    /// <summary>A context-free binding ("Shuffle everything") — no target, no arguments.</summary>
    public static SidebarActionBinding Simple(string providerId, string actionId)
        => new(providerId, actionId, SidebarActionTargetMode.None, null, null);

    /// <summary>An entity/track-scoped binding ("Play THIS playlist").</summary>
    public static SidebarActionBinding Fixed(string providerId, string actionId, string targetKey, bool track = false)
        => new(providerId, actionId,
            track ? SidebarActionTargetMode.FixedTrack : SidebarActionTargetMode.FixedEntity, targetKey, null);

    /// <summary>The registry lookup key ("wavee.play").</summary>
    public string ActionKey => ProviderId + "." + ActionId;

    /// <summary>Fixed modes carry their target IN the document; the live modes resolve it at invoke time.</summary>
    public bool RequiresTargetKey
        => TargetMode is SidebarActionTargetMode.FixedEntity or SidebarActionTargetMode.FixedTrack;

    /// <summary>False for a binding that cannot address an action (either id blank).</summary>
    public bool IsWellFormed => !string.IsNullOrEmpty(ProviderId) && !string.IsNullOrEmpty(ActionId);

    /// <summary>A fixed binding whose target key went missing renders VISIBLE-BUT-DISABLED with a reason — never
    /// silently dropped.</summary>
    public bool IsResolvable => IsWellFormed && (!RequiresTargetKey || !string.IsNullOrEmpty(TargetKey));

    public int ArgumentsByteCount => SidebarJson.ByteCount(Arguments);

    // Same JsonElement reasoning as SidebarExtensionRef.
    public bool Equals(SidebarActionBinding? other)
        => other is not null &&
           string.Equals(ProviderId, other.ProviderId, StringComparison.Ordinal) &&
           string.Equals(ActionId, other.ActionId, StringComparison.Ordinal) &&
           TargetMode == other.TargetMode &&
           string.Equals(TargetKey, other.TargetKey, StringComparison.Ordinal) &&
           SidebarJson.Same(Arguments, other.Arguments);

    // Arguments are not hashed, for the same reason as SidebarExtensionRef.Config.
    public override int GetHashCode() => HashCode.Combine(
        ProviderId is null ? 0 : StringComparer.Ordinal.GetHashCode(ProviderId),
        ActionId is null ? 0 : StringComparer.Ordinal.GetHashCode(ActionId),
        (byte)TargetMode,
        TargetKey is null ? 0 : StringComparer.Ordinal.GetHashCode(TargetKey));
}

/// <summary>The document's persisted binding → owner I's resolution vocabulary (<c>Platform/Actions.cs</c>). The
/// sidebar resolves and executes every bound row through <see cref="ActionBinding"/> — this is the ONE conversion, so
/// the wire record (kept for its byte-identical JSON round trip and its tests) never becomes a second resolution type.
/// The mode enums share their persisted byte values (None, FixedEntity, FixedTrack, NowPlaying, ActiveRoute).</summary>
public static class SidebarActionBindings
{
    public static ActionBinding ToActionBinding(this SidebarActionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        string? arguments = binding.Arguments is { } args && args.ValueKind != JsonValueKind.Undefined
            ? args.GetRawText()
            : null;
        return new ActionBinding(binding.ProviderId ?? "", binding.ActionId ?? "",
            (ActionTargetMode)(byte)binding.TargetMode, binding.TargetKey, arguments);
    }
}

/// <summary>The entity family of an item — needed to render a correct placeholder row (and pick the right art
/// shape: circular for Artist, rounded-square otherwise) BEFORE the entity resolves.</summary>
public enum SidebarEntityKind : byte
{
    None = 0, Playlist = 1, Album = 2, Artist = 3, Show = 4, PlaylistFolder = 5, Track = 6,
}

[Flags]
public enum SidebarEntityKinds : byte
{
    None = 0, Playlists = 1, Albums = 2, Artists = 4, Shows = 8, All = Playlists | Albums | Artists | Shows,
}

/// <summary>Shared with Mode B: the same five sort modes the V3 list offers. CustomOrder is only meaningful when
/// Kinds == Playlists (locked decision 10).</summary>
public enum SidebarSortMode : byte { Recents = 0, RecentlyAdded = 1, Alphabetical = 2, Creator = 3, CustomOrder = 4 }

/// <summary>Playlist qualifier chips. Any = no qualifier filter.</summary>
public enum SidebarPlaylistQualifier : byte { Any = 0, ByYou = 1, BySpotify = 2, Mixed = 3 }

/// <summary>Where a JumpBackIn section's recency comes from: Visited = navigation history (today's behaviour);
/// Played = the local play log (actual playback).</summary>
public enum SidebarRecentsSource : byte { Visited = 0, Played = 1 }

/// <summary>How an authored section occupies the pane when its live source contributes no rows. Default resolves
/// through <see cref="SidebarSectionKinds.EmptyBehaviorFor"/> so existing documents inherit the product default
/// without a migration. Persisted — append only.</summary>
public enum SidebarEmptyBehavior : byte
{
    Default = 0,
    HideBody = 1,
    CompactHint = 2,
    ActionCard = 3,
}

/// <summary>The property-panel edit surface for display options: one command carries (field, int value), so the
/// panel needs no per-field command type. Bools encode 0/1.</summary>
public enum SidebarDisplayField : byte
{
    Density = 0, Presentation = 1, Artwork = 2, Subtitles = 3, CountBadges = 4,
    CollapsedByDefault = 5, ShowInRail = 6, MaxItems = 7, GridColumns = 8,
    InlineControls = 9,   // EntityList only: a compact filter/sort row rendered atop the section
    PlayButton = 10,      // EntityEmbed only: the hover/focus play affordance on the hero card
    RecentsSource = 11,   // JumpBackIn only: Visited | Played
    EmptyBehavior = 12,   // content sections: Default | HideBody | CompactHint | ActionCard
}

/// <summary>Per-section presentation. Every field is user-editable from the property panel; every field has a
/// default that makes a freshly-added section immediately usable.</summary>
public sealed record SidebarDisplayOptions(
    SidebarDensity Density = SidebarDensity.Cozy,
    SidebarPresentation Presentation = SidebarPresentation.List,
    bool Artwork = true,
    bool Subtitles = true,
    bool CountBadges = false,
    bool CollapsedByDefault = false,
    bool ShowInRail = true,
    int MaxItems = 0,      // 0 = unbounded; clamped to [0, 500] by the reducer
    int GridColumns = 2,   // clamped to [2, 4]; ignored when Presentation == List
    bool InlineControls = false,                                // EntityList only
    bool PlayButton = true,                                     // EntityEmbed only
    SidebarRecentsSource Recents = SidebarRecentsSource.Visited, // JumpBackIn only
    SidebarEmptyBehavior EmptyBehavior = SidebarEmptyBehavior.Default)
{
    public static readonly SidebarDisplayOptions Default = new();

    /// <summary>Icon-only rows for <see cref="SidebarSectionKind.CollectionShortcuts"/>: no artwork, quiet count
    /// badge ON.
    /// <para>Subtitles is false, not true: neither CollectionShortcuts nor StaticLinks exposes the Subtitles field
    /// and a route item never supplies a subtitle string, so the old Subtitles:true preset only ever inflated the
    /// height ladder without ever painting a line — Cozy without a subtitle is the honest 40-DIP row.</para></summary>
    public static readonly SidebarDisplayOptions Shortcuts =
        new(Density: SidebarDensity.Cozy, Artwork: false, Subtitles: false, CountBadges: true);

    /// <summary>The same icon-only rows for <see cref="SidebarSectionKind.StaticLinks"/> — with counts OFF.
    /// DEFECT 8: StaticLinks used to share <see cref="Shortcuts"/> verbatim, seeding CountBadges = true on a field
    /// the property panel never showed and the reducer never accepted an edit for — a default the user could
    /// neither see nor undo, yet the renderer honoured it. One preset per kind is the fix.</summary>
    public static readonly SidebarDisplayOptions Links = Shortcuts with { CountBadges = false };

    /// <summary>Artwork rows with a creator/type subtitle (PlaylistTree / EntityList / Pinned).</summary>
    public static readonly SidebarDisplayOptions Entities = new();
}

/// <summary>One hand-placed row. Display-only overrides NEVER mutate the entity: LabelOverride is a local alias,
/// not a rename.
/// <para>MISSING-ENTITY RETENTION: FallbackTitle/FallbackImageUrl are stamped every time the item successfully
/// resolves against the live projection. When the entity later disappears the row still renders with its
/// last-known title + art, dimmed, with an "Unavailable" affordance — never auto-removed; only an explicit
/// RemoveItem command deletes it.</para></summary>
public sealed record SidebarItemSpec(
    string Id,                        // "itm_" + 8 lowercase hex; stable for the item's life; never reused
    SidebarItemTarget Target,
    string Key,                       // Route -> a route name; Entity/Track -> a spotify: uri
    SidebarEntityKind EntityKind = SidebarEntityKind.None,
    string? LabelOverride = null,      // trimmed; "" is normalized to null by the reducer
    string? IconOverride = null,       // an icon NAME, validated against SidebarIconNames.Allowed
    string? FallbackTitle = null,
    string? FallbackImageUrl = null,
    bool Hidden = false,
    SidebarActionBinding? Action = null)   // LAYOUT V2: set for Target == Action; null for a navigate/play item
{
    /// <summary>An action row whose binding is present and addressable — only whether the invoke path can run, not
    /// whether the row renders.</summary>
    public bool HasRunnableAction => Target == SidebarItemTarget.Action && Action is { IsResolvable: true };
}

/// <summary>A dynamic section: "the library, filtered and sorted" — the same shape Mode B's filter/sort bar
/// produces. Qualifier and Sort == CustomOrder apply only when Kinds == Playlists (the reducer rewrites an
/// illegal combination rather than rejecting the edit).
/// <para>LAYOUT V2 adds IncludeUris/ExcludeUris: an allow/deny set over entity uris ("only these artists" is a
/// QUERY, not a hand list). IncludeUris restricts to exactly those uris; ExcludeUris then drops uris from what
/// remains. Both normalize to null when empty.</para></summary>
public sealed record SidebarEntityQuery(
    SidebarEntityKinds Kinds = SidebarEntityKinds.All,
    SidebarSortMode Sort = SidebarSortMode.Recents,
    bool Descending = true,
    SidebarPlaylistQualifier Qualifier = SidebarPlaylistQualifier.Any,
    IReadOnlyList<string>? IncludeUris = null,
    IReadOnlyList<string>? ExcludeUris = null)
{
    public static readonly SidebarEntityQuery Default = new();

    public static readonly SidebarEntityQuery PlaylistsAlphabetical =
        new(SidebarEntityKinds.Playlists, SidebarSortMode.Alphabetical, Descending: false);

    /// <summary>The effective query for a PlaylistTree whose persisted Query is null: playlist leaves in the
    /// rootlist's authored order. Null stays the compact wire representation; this value lets reducers/comparers/
    /// editors reason about that meaning without substituting <see cref="Default"/> (Recents).</summary>
    public static readonly SidebarEntityQuery PlaylistTreeSourceOrder =
        new(SidebarEntityKinds.Playlists, SidebarSortMode.CustomOrder, Descending: false);

    public IReadOnlyList<string> IncludeList => IncludeUris ?? Array.Empty<string>();
    public IReadOnlyList<string> ExcludeList => ExcludeUris ?? Array.Empty<string>();

    /// <summary>True when the query restricts to an explicit uri set ("only these artists").</summary>
    public bool HasIncludeSet => IncludeUris is { Count: > 0 };
    public bool HasExcludeSet => ExcludeUris is { Count: > 0 };

    /// <summary>Ordinal, order-sensitive list comparison — the identity the two uri sets use for equality.</summary>
    public static bool SameUris(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        int ac = a?.Count ?? 0, bc = b?.Count ?? 0;
        if (ac != bc) return false;
        for (int i = 0; i < ac; i++)
            if (!string.Equals(a![i], b![i], StringComparison.Ordinal)) return false;
        return true;
    }

    // The two uri sets are IReadOnlyList members, which a synthesized record Equals compares by REFERENCE, so a
    // query round-tripped through JSON would never equal the one it came from. Both are hand-declared.
    public bool Equals(SidebarEntityQuery? other)
        => other is not null && Kinds == other.Kinds && Sort == other.Sort && Descending == other.Descending &&
           Qualifier == other.Qualifier &&
           SameUris(IncludeUris, other.IncludeUris) && SameUris(ExcludeUris, other.ExcludeUris);

    // Counts only, deliberately: the hash must never distinguish two values Equals calls equal, and stays O(1).
    public override int GetHashCode() => HashCode.Combine(
        (byte)Kinds, (byte)Sort, Descending, (byte)Qualifier, IncludeUris?.Count ?? 0, ExcludeUris?.Count ?? 0);
}

public sealed record SidebarSectionSpec(
    string Id,                                       // "sec_" + 8 lowercase hex; never reused within a document
    SidebarSectionKind Kind,
    string? Title = null,                            // a USER title (rename); null = use TitleLocKey/kind default
    string? TitleLocKey = null,                      // template-authored titles; RenameSection sets Title and clears this
    bool Hidden = false,                             // authored "off" — contributes no rows and no rail tiles
    bool Collapsed = false,                          // the LIVE collapse state (persisted with the doc)
    SidebarDisplayOptions? Display = null,           // null == SidebarDisplayOptions.Default (keeps JSON small)
    IReadOnlyList<SidebarItemSpec>? Items = null,    // Pinned overrides / StaticLinks / CustomGroup members
    SidebarEntityQuery? Query = null,                // EntityList / PlaylistTree; null tree query = rootlist order
    IReadOnlyList<SidebarSectionSpec>? Children = null, // CustomGroup only; depth 1 — a child may not have Children
    SidebarExtensionRef? Extension = null)           // LAYOUT V2: Extension only — which contribution renders here
{
    public SidebarDisplayOptions Opts => Display ?? SidebarDisplayOptions.Default;
    public IReadOnlyList<SidebarItemSpec> ItemList => Items ?? Array.Empty<SidebarItemSpec>();
    public IReadOnlyList<SidebarSectionSpec> ChildList => Children ?? Array.Empty<SidebarSectionSpec>();

    /// <summary>True for a kind this build does not know — it renders as nothing and round-trips untouched.</summary>
    public bool IsUnknownKind => !SidebarSectionKinds.IsKnown(Kind);

    /// <summary>A contributed section (LAYOUT V2). Its rows come from the resolved data source, not ItemList.</summary>
    public bool IsExtension => Kind == SidebarSectionKind.Extension;

    /// <summary>An Extension section whose ref is missing or unaddressable — a hand-edited or half-migrated
    /// document. It keeps its spec and renders the "Manage extension" placeholder row; never auto-removed.</summary>
    public bool IsUnboundExtension => IsExtension && Extension is not { IsWellFormed: true };
}

/// <summary>The payload the Foundation document envelope carries for Mode C.
/// <para><paramref name="TopBar"/> is the shell top bar's customizable shortcut band — ONE global list, part of
/// this record so undo and <see cref="SidebarLayoutCompare"/> both see it. Null means "never customized" (resolves
/// to <see cref="DefaultTopBar"/> via <see cref="EffectiveTopBar"/>); an EMPTY list means the band was emptied on
/// purpose. Templates preserve it (ApplyTemplate/ResetLayout).</para></summary>
public sealed record SidebarCustomLayout(
    string TemplateId,                               // the template this layout was seeded from
    IReadOnlyList<SidebarSectionSpec> Sections,
    IReadOnlyList<SidebarItemSpec>? TopBar = null)   // null == never customized == DefaultTopBar; [] == emptied
{
    public static readonly SidebarCustomLayout Empty = new(SidebarTemplates.Blank, Array.Empty<SidebarSectionSpec>());

    /// <summary>The built-in top-bar band: Home — what the shell hard-coded before the band became customizable.
    /// FIXED item id (never minted) so the band is stable across reads.
    /// <para>Issue #85 (H4): the five library destinations were briefly seeded here too, but this is the ONE
    /// global band shared by all three designs — that duplicated Classic's own library section and saturated the
    /// top-bar cap. Library V3 renders those destinations from its own compact strip instead.</para></summary>
    public static readonly IReadOnlyList<SidebarItemSpec> DefaultTopBar = new SidebarItemSpec[]
    {
        new(SidebarIds.TopBarHomeItem, SidebarItemTarget.Route, "home", IconOverride: "Home"),
    };

    /// <summary>What the shell actually renders: the authored band, or the built-in default when never customized.
    /// The ONE place the null ⇒ default rule lives.</summary>
    public IReadOnlyList<SidebarItemSpec> EffectiveTopBar => TopBar ?? DefaultTopBar;

    /// <summary>Top-level + child sections. The reducer's section cap counts this.</summary>
    public int SectionCount
    {
        get
        {
            int n = Sections.Count;
            for (int i = 0; i < Sections.Count; i++) n += Sections[i].ChildList.Count;
            return n;
        }
    }

    /// <summary>Depth-2 search by section id.</summary>
    public SidebarSectionSpec? Find(string sectionId)
    {
        for (int i = 0; i < Sections.Count; i++)
        {
            var s = Sections[i];
            if (string.Equals(s.Id, sectionId, StringComparison.Ordinal)) return s;
            var kids = s.ChildList;
            for (int j = 0; j < kids.Count; j++)
                if (string.Equals(kids[j].Id, sectionId, StringComparison.Ordinal)) return kids[j];
        }
        return null;
    }

    /// <summary>Locates a section: parent == null means top-level at Index; Index == -1 means not found.</summary>
    public (SidebarSectionSpec? Parent, int Index) Locate(string id)
    {
        for (int i = 0; i < Sections.Count; i++)
        {
            var s = Sections[i];
            if (string.Equals(s.Id, id, StringComparison.Ordinal)) return (null, i);
            var kids = s.ChildList;
            for (int j = 0; j < kids.Count; j++)
                if (string.Equals(kids[j].Id, id, StringComparison.Ordinal)) return (s, j);
        }
        return (null, -1);
    }
}

/// <summary>The glyph NAME whitelist. Lives here (not app-side with the codepoint map) because the reducer
/// validates <c>SidebarItemSpec.IconOverride</c> against it and this model may not reference the engine's icon
/// font. The app-side icon map translates these names to real glyphs and re-exports this list as the picker order.</summary>
public static class SidebarIconNames
{
    /// <summary>Ordered, stable — this IS the icon-picker order in the property panel.</summary>
    public static readonly string[] Allowed =
    [
        "MusicNote","Heart","Album","Contact","RadioTower","Folder","FolderOpen","Home","Search","Clock",
        "Star","FavoriteStar","Tag","Headphones","Microphone","Movie","Picture","Queue","Shuffle","Link",
        "Grid","List","Pin","Settings","Code","Globe","Device","Friends","Equalizer","Download",
    ];

    public static bool IsAllowed(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var all = Allowed;
        for (int i = 0; i < all.Length; i++)
            if (string.Equals(all[i], name, StringComparison.Ordinal)) return true;
        return false;
    }
}

/// <summary>Per-kind facts the reducer, the templates, the palette and the property panel all need in ONE place, so
/// a per-kind rule is never restated.</summary>
public static class SidebarSectionKinds
{
    /// <summary>The highest kind THIS build understands. A greater value round-trips untouched and renders as nothing.</summary>
    public const byte MaxKnown = (byte)SidebarSectionKind.Extension;

    public static bool IsKnown(SidebarSectionKind kind) => (byte)kind <= MaxKnown;

    /// <summary>The kind's default section title loc key (null for Divider, which has no title). JumpBackIn's
    /// default follows its recents source.</summary>
    public static string? DefaultTitleLocKey(SidebarSectionKind kind,
        SidebarRecentsSource recents = SidebarRecentsSource.Visited) => kind switch
    {
        SidebarSectionKind.Pinned => "sidebar.pinned",
        SidebarSectionKind.JumpBackIn => recents == SidebarRecentsSource.Played
            ? "sidebar.section.recentlyPlayed" : "sidebar.section.jumpBackIn",
        SidebarSectionKind.CollectionShortcuts => "sidebar.yourLibrary",
        SidebarSectionKind.PlaylistTree => "sidebar.playlists",
        SidebarSectionKind.EntityList => "sidebar.section.entityList",
        SidebarSectionKind.StaticLinks => "sidebar.section.staticLinks",
        SidebarSectionKind.CustomGroup => "sidebar.section.group",
        SidebarSectionKind.Header => "sidebar.section.header",
        SidebarSectionKind.Divider => null,
        SidebarSectionKind.EntityEmbed => "sidebar.section.entityEmbed",
        SidebarSectionKind.NewReleases => "sidebar.section.newReleases",
        SidebarSectionKind.Concerts => "sidebar.section.concerts",
        // A contributed section's REAL title comes from the contribution manifest, which this model cannot see;
        // this is the neutral fallback shown until the registry resolves the ref (or when it cannot).
        SidebarSectionKind.Extension => "sidebar.section.extension",
        _ => null,
    };

    /// <summary>The add-section palette's label / one-line description keys.</summary>
    public static string? PaletteNameLocKey(SidebarSectionKind kind) => kind switch
    {
        SidebarSectionKind.Pinned => "sidebar.section.pinned",
        SidebarSectionKind.JumpBackIn => "sidebar.section.jumpBackIn",
        SidebarSectionKind.CollectionShortcuts => "sidebar.section.shortcuts",
        SidebarSectionKind.PlaylistTree => "sidebar.section.playlistTree",
        SidebarSectionKind.EntityList => "sidebar.section.entityList",
        SidebarSectionKind.StaticLinks => "sidebar.section.staticLinks",
        SidebarSectionKind.CustomGroup => "sidebar.section.group",
        SidebarSectionKind.Header => "sidebar.section.header",
        SidebarSectionKind.Divider => "sidebar.section.divider",
        SidebarSectionKind.EntityEmbed => "sidebar.section.entityEmbed",
        SidebarSectionKind.NewReleases => "sidebar.section.newReleases",
        SidebarSectionKind.Concerts => "sidebar.section.concerts",
        SidebarSectionKind.Extension => "sidebar.section.extension",
        _ => null,
    };

    public static string? PaletteDescriptionLocKey(SidebarSectionKind kind)
        => PaletteNameLocKey(kind) is { } name ? name + "Sub" : null;

    /// <summary>The Display preset a freshly-added section of this kind is seeded with.</summary>
    public static SidebarDisplayOptions DefaultDisplay(SidebarSectionKind kind) => kind switch
    {
        SidebarSectionKind.CollectionShortcuts => SidebarDisplayOptions.Shortcuts,
        // Defect 8: StaticLinks forbids CountBadges below, so its seed must not carry it.
        SidebarSectionKind.StaticLinks => SidebarDisplayOptions.Links,
        // "top-N" feeds ship with their spec'd N so a freshly added section is immediately sane.
        SidebarSectionKind.NewReleases => SidebarDisplayOptions.Entities with { MaxItems = 4 },
        SidebarSectionKind.Concerts => SidebarDisplayOptions.Entities with { MaxItems = 3 },
        // A contribution is a feed too: ship a bounded default so a queue/top-tracks section cannot flood the pane
        // before the inspector's schema-generated controls are touched.
        SidebarSectionKind.Extension => SidebarDisplayOptions.Entities with { MaxItems = 10 },
        _ => SidebarDisplayOptions.Entities,
    };

    /// <summary>Resolve a section's effective empty treatment. Actionable source states win when the author left
    /// the field at Default; an explicit authored choice remains authoritative.</summary>
    public static SidebarEmptyBehavior EmptyBehaviorFor(SidebarSectionKind kind,
        SidebarEmptyBehavior authored = SidebarEmptyBehavior.Default, bool actionable = false)
    {
        if (authored != SidebarEmptyBehavior.Default) return authored;
        if (actionable) return SidebarEmptyBehavior.ActionCard;
        return kind switch
        {
            SidebarSectionKind.Pinned => SidebarEmptyBehavior.CompactHint,
            // R3.1.6 (user decision): a dynamic feed that resolved to nothing STAYS VISIBLE with a quiet hint.
            // HideBody read as a bug (the header remained while the body silently vanished); CompactHint is a
            // 32-DIP tertiary line naming the state per kind. An explicit authored choice still wins above.
            SidebarSectionKind.JumpBackIn or SidebarSectionKind.EntityList or SidebarSectionKind.NewReleases
                => SidebarEmptyBehavior.CompactHint,
            SidebarSectionKind.Concerts or SidebarSectionKind.Extension => SidebarEmptyBehavior.ActionCard,
            _ => SidebarEmptyBehavior.CompactHint,
        };
    }

    /// <summary>Kinds whose Items list is meaningful. Pinned accepts items as an OVERRIDE side-table (alias / icon /
    /// hidden for a pin), not as the pin list itself — the pin set and order live outside this document.</summary>
    public static bool AcceptsItems(SidebarSectionKind kind) => kind switch
    {
        SidebarSectionKind.Pinned => true,               // override side-table
        SidebarSectionKind.CollectionShortcuts => true,
        SidebarSectionKind.StaticLinks => true,
        SidebarSectionKind.CustomGroup => true,
        SidebarSectionKind.EntityEmbed => true,          // exactly one
        SidebarSectionKind.Extension => false,           // rows come from the contribution, never a hand-list
        _ => false,
    };

    /// <summary>Kinds whose <see cref="SidebarSectionSpec.Query"/> is a LIBRARY query — the only ones for which the
    /// include/exclude uri sets mean anything. A query surviving on any other kind is hand-edited or half-migrated;
    /// the reducer strips its uri sets when it next touches the section.</summary>
    public static bool SupportsLibraryQuery(SidebarSectionKind kind)
        => kind is SidebarSectionKind.EntityList or SidebarSectionKind.PlaylistTree;

    /// <summary>The query a section MEANS when its compact document form carries null. PlaylistTree deliberately
    /// differs from EntityList: a null tree preserves the backend rootlist order instead of silently becoming Recents.</summary>
    public static SidebarEntityQuery EffectiveQuery(SidebarSectionKind kind, SidebarEntityQuery? query)
        => query ?? (kind == SidebarSectionKind.PlaylistTree
            ? SidebarEntityQuery.PlaylistTreeSourceOrder
            : SidebarEntityQuery.Default);

    /// <summary>Kinds that cannot exist without a <see cref="SidebarExtensionRef"/> (AddSection rejects
    /// ExtensionRefMissing without one).</summary>
    public static bool RequiresExtensionRef(SidebarSectionKind kind) => kind == SidebarSectionKind.Extension;

    /// <summary>EntityEmbed is the single-item kind: exactly one spotlighted entity.</summary>
    public static int ItemCapacity(SidebarSectionKind kind)
        => kind == SidebarSectionKind.EntityEmbed ? 1 : SidebarLayoutReducer.MaxItemsPerSection;

    /// <summary>Which display fields the property panel SHOWS for a kind — and therefore the only ones
    /// SetDisplayOption accepts (an inapplicable field is a NoChange, never a silent write).</summary>
    public static bool AllowsDisplayField(SidebarSectionKind kind, SidebarDisplayField field)
    {
        if (!IsKnown(kind)) return false;

        // The three kind-scoped fields added by the extended catalog are hidden everywhere else.
        switch (field)
        {
            case SidebarDisplayField.InlineControls: return kind == SidebarSectionKind.EntityList;
            case SidebarDisplayField.PlayButton: return kind == SidebarSectionKind.EntityEmbed;
            case SidebarDisplayField.RecentsSource: return kind == SidebarSectionKind.JumpBackIn;
            case SidebarDisplayField.EmptyBehavior:
                return kind is not (SidebarSectionKind.Header or SidebarSectionKind.Divider);
        }

        return kind switch
        {
            // Header/Divider are pure chrome: only "show in the collapsed rail" means anything.
            SidebarSectionKind.Header or SidebarSectionKind.Divider => field == SidebarDisplayField.ShowInRail,

            SidebarSectionKind.JumpBackIn => field != SidebarDisplayField.CountBadges,

            SidebarSectionKind.CollectionShortcuts => field is SidebarDisplayField.Density
                or SidebarDisplayField.CountBadges or SidebarDisplayField.CollapsedByDefault
                or SidebarDisplayField.ShowInRail,

            SidebarSectionKind.PlaylistTree => field != SidebarDisplayField.MaxItems,  // the tree is never truncated

            SidebarSectionKind.StaticLinks => field is SidebarDisplayField.Density
                or SidebarDisplayField.CollapsedByDefault or SidebarDisplayField.ShowInRail,

            // A card, not a list.
            SidebarSectionKind.EntityEmbed => field is not (SidebarDisplayField.Presentation
                or SidebarDisplayField.CountBadges or SidebarDisplayField.MaxItems or SidebarDisplayField.GridColumns),

            // A releases feed has no meaningful single rail tile, so ShowInRail is forced off too.
            SidebarSectionKind.NewReleases => field is not (SidebarDisplayField.CountBadges
                or SidebarDisplayField.ShowInRail),

            // Always a list of event rows.
            SidebarSectionKind.Concerts => field is not (SidebarDisplayField.Presentation
                or SidebarDisplayField.CountBadges or SidebarDisplayField.GridColumns),

            // A contributed section owns its own presentation: everything else the inspector shows for it is
            // GENERATED from the source's config schema (edited through SetExtensionConfig), so the shared surface
            // is deliberately just the four host-owned fields.
            SidebarSectionKind.Extension => field is SidebarDisplayField.Density
                or SidebarDisplayField.CollapsedByDefault or SidebarDisplayField.ShowInRail
                or SidebarDisplayField.MaxItems,

            _ => true,   // Pinned / EntityList / CustomGroup: everything applies
        };
    }

    /// <summary>A section may only nest inside a CustomGroup, and a CustomGroup may never nest (depth 1, hard).</summary>
    public static bool IsNestable(SidebarSectionKind kind) => kind != SidebarSectionKind.CustomGroup;

    /// <summary>Kinds whose CONTENT and ORDER live in a shared store outside the document, so the section spec is a
    /// VIEW of that store rather than a container of its own rows: Pinned (its Items are an override side-table,
    /// never the pin list) and PlaylistTree (the backend rootlist, plus V3's local order overlay).
    /// <para>DEFECT 9 — this is why DuplicateSection refuses them. Two Pinned sections would render the same store
    /// twice AND both commit reorders into it; two PlaylistTrees would do the same to the rootlist order. A clone of
    /// a store-backed section is a second WRITER onto one list, which no amount of fresh ids can separate.</para></summary>
    public static bool IsStoreBacked(SidebarSectionKind kind)
        => kind is SidebarSectionKind.Pinned or SidebarSectionKind.PlaylistTree;
}

/// <summary>Id minting. "sec_"/"itm_" + 8 lowercase hex; uniqueness within a document is enforced by the reducer,
/// which re-rolls on the (astronomically rare) collision.</summary>
public static class SidebarIds
{
    public const string SectionPrefix = "sec_";
    public const string ItemPrefix = "itm_";

    /// <summary>The SENTINEL section id addressing <see cref="SidebarCustomLayout.TopBar"/> from the three
    /// item-scoped commands (SetItemLabel/SetItemIcon/SetItemAction). Can never collide with a real section id:
    /// every minted one starts with <see cref="SectionPrefix"/>. Structural edits use the dedicated
    /// AddTopBarItem/MoveTopBarItem/RemoveTopBarItem commands instead — the band has its own cap and none of the
    /// section-kind rules AddItem/MoveItem carry.</summary>
    public const string TopBarSection = "topbar";

    /// <summary>The stable id of the built-in default top-bar Home shortcut.</summary>
    public const string TopBarHomeItem = ItemPrefix + "00000001";

    // Issue #85 (H4) — the five library-destination shortcuts DefaultTopBar once seeded alongside Home. Fixed ids,
    // never minted, for the same reason TopBarHomeItem is: a fresh id per call would break remove-by-id, the
    // per-tile menu keys and every "is this still exactly the default" equality check.
    public const string TopBarLikedItem = ItemPrefix + "00000002";
    public const string TopBarAlbumsItem = ItemPrefix + "00000003";
    public const string TopBarArtistsItem = ItemPrefix + "00000004";
    public const string TopBarPodcastsItem = ItemPrefix + "00000005";
    public const string TopBarLocalItem = ItemPrefix + "00000006";

    public static bool IsTopBar(string? sectionId)
        => string.Equals(sectionId, TopBarSection, StringComparison.Ordinal);

    public static string NewSection() => SectionPrefix + Hex8();
    public static string NewItem() => ItemPrefix + Hex8();

    static string Hex8() => Random.Shared.NextInt64(0L, 0x1_0000_0000L).ToString("x8",
        System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Menu-row label hygiene — pure, engine-free, and therefore directly testable. A context-menu row label
/// grows to fit its text with no trimming of its own, so a long DYNAMIC label (an interpolated playlist name) would
/// otherwise clip: it widens the whole flyout, and every other row with it. The fix is minting the label pre-clipped
/// before it reaches the loc format string.</summary>
public static class MenuLabel
{
    /// <summary>The default clip width for an interpolated entity name inside a menu label, in characters. Sized
    /// against the 250-DIP context-menu minimum: ~28 characters of 14px UI text plus the surrounding verb fills that
    /// column without widening it.</summary>
    public const int NameChars = 28;

    /// <summary>Clip <paramref name="name"/> to <paramref name="max"/> characters, ending in a single ellipsis.
    /// Shorter names (and a null/empty one) come back untouched — the ellipsis appears only when something was
    /// actually dropped.</summary>
    public static string Clip(string? name, int max = NameChars)
    {
        if (name is not { Length: > 0 }) return "";
        if (max < 1) return "…";
        if (name.Length <= max) return name;
        // Trim the trailing space the cut usually lands on, so the result is "Late night…" not "Late night …".
        return string.Concat(name.AsSpan(0, max - 1).TrimEnd(), "…");
    }
}

/// <summary>Sentinel-aware item addressing, in one place. Two item lists live on <see cref="SidebarCustomLayout"/>:
/// a SECTION's Items, and the flat global TopBar band, edited by two disjoint command families on purpose (the
/// band has its own cap and none of the per-KIND item rules). Since the band ALSO renders as an ordinary section
/// (the sentinel <see cref="SidebarIds.TopBarSection"/>), which family applies is decided HERE once, rather than
/// at each call site where divergence would be a silent rejection.</summary>
public static class SidebarItemCommands
{
    /// <summary>Insert an item into a section's list — or into the shell's shortcut band when the section id is the
    /// sentinel. <paramref name="index"/> is clamped by the reducer in both families.</summary>
    public static SidebarCommand Add(string sectionId, SidebarItemSpec item, int index)
        => SidebarIds.IsTopBar(sectionId)
            ? new AddTopBarItem(item, index)
            : new AddItem(sectionId, item, index);

    /// <summary>Reorder WITHIN one list. <paramref name="toIndex"/> is interpreted AFTER the removal in both
    /// families, so the two arms are genuinely interchangeable.
    /// <para>There is deliberately no cross-section form here: MoveItem can move between two sections, but the band
    /// is not a section and no command moves an item across that boundary. A caller that wants "drag out of the
    /// band into a section" must compose Remove + Add and own the two-step undo.</para></summary>
    public static SidebarCommand Move(string sectionId, int fromIndex, int toIndex)
        => SidebarIds.IsTopBar(sectionId)
            ? new MoveTopBarItem(fromIndex, toIndex)
            : new MoveItem(sectionId, fromIndex, sectionId, toIndex);

    /// <summary>Drop an item by id. Removing the band's last shortcut leaves an EMPTY list, never null — the
    /// reducer owns that distinction.</summary>
    public static SidebarCommand Remove(string sectionId, string itemId)
        => SidebarIds.IsTopBar(sectionId)
            ? new RemoveTopBarItem(itemId)
            : new RemoveItem(sectionId, itemId);

    /// <summary>The item list a section id addresses: the band for the sentinel, the section's own items otherwise,
    /// and an EMPTY list for an id the document does not contain. The read-side twin of the three factories, so a
    /// panel that edits through <see cref="Remove"/> reads through the same rule.</summary>
    public static IReadOnlyList<SidebarItemSpec> ItemsIn(SidebarCustomLayout? layout, string? sectionId)
    {
        if (layout is null || sectionId is null or { Length: 0 }) return Array.Empty<SidebarItemSpec>();
        return SidebarIds.IsTopBar(sectionId)
            ? layout.EffectiveTopBar
            : layout.Find(sectionId)?.ItemList ?? Array.Empty<SidebarItemSpec>();
    }

    /// <summary>Locate an item by id inside whichever list <paramref name="sectionId"/> addresses, or -1.</summary>
    public static SidebarItemSpec? FindItem(SidebarCustomLayout? layout, string? sectionId, string? itemId)
    {
        if (itemId is null or { Length: 0 }) return null;
        var items = ItemsIn(layout, sectionId);
        for (int i = 0; i < items.Count; i++)
            if (string.Equals(items[i].Id, itemId, StringComparison.Ordinal)) return items[i];
        return null;
    }
}

/// <summary>Structural comparison for the layout document: the payload records carry <c>IReadOnlyList&lt;T&gt;</c>
/// members, so a positional record's synthesized Equals compares those by REFERENCE and two documents that
/// round-tripped through JSON are never <c>==</c> even when every field matches.
/// <para>Equal asks "is this the same document?"; EqualIgnoringIds asks "is this the same SHAPE?" (skip a
/// confirmation when the user has no edits to lose). FirstDifference gives a path to the first mismatch, for test
/// failure messages.</para></summary>
public static class SidebarLayoutCompare
{
    public static bool Equal(SidebarCustomLayout? a, SidebarCustomLayout? b) => Compare(a, b, ids: true) is null;

    public static bool EqualIgnoringIds(SidebarCustomLayout? a, SidebarCustomLayout? b)
        => Compare(a, b, ids: false) is null;

    /// <summary>Template-pristine comparison: template identity + section shape, modulo generated ids. The global
    /// top bar is intentionally excluded because templates preserve it and top-bar-only edits are not sections at risk.</summary>
    public static bool EqualTemplateSectionsIgnoringIds(SidebarCustomLayout? a, SidebarCustomLayout? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return string.Equals(a.TemplateId, b.TemplateId, StringComparison.Ordinal)
               && CompareSections(a.Sections, b.Sections, ids: false, "sections") is null;
    }

    /// <summary>A dotted path to the first structural difference ("sections[2].display.maxItems"), or null when the
    /// two documents match. For assertion messages and the devtools inspector — never for control flow.</summary>
    public static string? FirstDifference(SidebarCustomLayout? a, SidebarCustomLayout? b, bool ignoreIds = false)
        => Compare(a, b, ids: !ignoreIds);

    public static bool Equal(SidebarSectionSpec? a, SidebarSectionSpec? b)
        => CompareSection(a, b, ids: true, path: "section") is null;

    public static bool EqualIgnoringIds(SidebarSectionSpec? a, SidebarSectionSpec? b)
        => CompareSection(a, b, ids: false, path: "section") is null;

    static string? Compare(SidebarCustomLayout? a, SidebarCustomLayout? b, bool ids)
    {
        if (ReferenceEquals(a, b)) return null;
        if (a is null || b is null) return "layout";
        if (!string.Equals(a.TemplateId, b.TemplateId, StringComparison.Ordinal)) return "templateId";
        var band = CompareTopBar(a.TopBar, b.TopBar, ids);
        if (band is not null) return band;
        return CompareSections(a.Sections, b.Sections, ids, "sections");
    }

    /// <summary>The shell top-bar band. null (never customized) and [] (emptied on purpose) are DIFFERENT documents
    /// — they render differently, so the diff must not collapse them.</summary>
    static string? CompareTopBar(IReadOnlyList<SidebarItemSpec>? a, IReadOnlyList<SidebarItemSpec>? b, bool ids)
    {
        if (a is null || b is null) return a is null && b is null ? null : "topBar";
        if (a.Count != b.Count) return "topBar.count";
        for (int i = 0; i < a.Count; i++)
        {
            var diff = CompareItem(a[i], b[i], ids, "topBar[" + i.ToString(Culture) + "]");
            if (diff is not null) return diff;
        }
        return null;
    }

    static string? CompareSections(IReadOnlyList<SidebarSectionSpec> a, IReadOnlyList<SidebarSectionSpec> b,
        bool ids, string path)
    {
        if (a.Count != b.Count) return path + ".count";
        for (int i = 0; i < a.Count; i++)
        {
            var diff = CompareSection(a[i], b[i], ids, path + "[" + i.ToString(Culture) + "]");
            if (diff is not null) return diff;
        }
        return null;
    }

    static string? CompareSection(SidebarSectionSpec? a, SidebarSectionSpec? b, bool ids, string path)
    {
        if (ReferenceEquals(a, b)) return null;
        if (a is null || b is null) return path;
        if (ids && !string.Equals(a.Id, b.Id, StringComparison.Ordinal)) return path + ".id";
        if (a.Kind != b.Kind) return path + ".kind";
        if (!string.Equals(a.Title, b.Title, StringComparison.Ordinal)) return path + ".title";
        if (!string.Equals(a.TitleLocKey, b.TitleLocKey, StringComparison.Ordinal)) return path + ".titleLocKey";
        if (a.Hidden != b.Hidden) return path + ".hidden";
        if (a.Collapsed != b.Collapsed) return path + ".collapsed";
        if (a.Opts != b.Opts) return path + ".display";                       // record equality: all scalars
        // SidebarEntityQuery and SidebarExtensionRef declare their OWN Equals (uri sets element-wise, the config
        // JsonElement by raw text), so both of these are real CONTENT comparisons.
        if (SidebarSectionKinds.EffectiveQuery(a.Kind, a.Query) !=
            SidebarSectionKinds.EffectiveQuery(b.Kind, b.Query))
            return path + ".query";
        if (a.Extension != b.Extension) return path + ".extension";

        var ai = a.ItemList; var bi = b.ItemList;
        if (ai.Count != bi.Count) return path + ".items.count";
        for (int i = 0; i < ai.Count; i++)
        {
            var diff = CompareItem(ai[i], bi[i], ids, path + ".items[" + i.ToString(Culture) + "]");
            if (diff is not null) return diff;
        }

        return CompareSections(a.ChildList, b.ChildList, ids, path + ".children");
    }

    static string? CompareItem(SidebarItemSpec a, SidebarItemSpec b, bool ids, string path)
    {
        if (ids && !string.Equals(a.Id, b.Id, StringComparison.Ordinal)) return path + ".id";
        if (a.Target != b.Target) return path + ".target";
        if (!string.Equals(a.Key, b.Key, StringComparison.Ordinal)) return path + ".key";
        if (a.EntityKind != b.EntityKind) return path + ".entityKind";
        if (!string.Equals(a.LabelOverride, b.LabelOverride, StringComparison.Ordinal)) return path + ".label";
        if (!string.Equals(a.IconOverride, b.IconOverride, StringComparison.Ordinal)) return path + ".icon";
        if (!string.Equals(a.FallbackTitle, b.FallbackTitle, StringComparison.Ordinal)) return path + ".fallbackTitle";
        if (!string.Equals(a.FallbackImageUrl, b.FallbackImageUrl, StringComparison.Ordinal))
            return path + ".fallbackImage";
        if (a.Hidden != b.Hidden) return path + ".hidden";
        if (a.Action != b.Action) return path + ".action";   // SidebarActionBinding declares content equality too
        return null;
    }

    /// <summary>Every section and item id in the document, in document order — the "ids are unique" assertion's
    /// input and the customizer's "which section did that command touch?" trace.</summary>
    public static List<string> AllIds(SidebarCustomLayout layout)
    {
        var ids = new List<string>();
        for (int i = 0; i < layout.Sections.Count; i++) Collect(layout.Sections[i], ids);
        return ids;
    }

    static void Collect(SidebarSectionSpec s, List<string> ids)
    {
        ids.Add(s.Id);
        var items = s.ItemList;
        for (int i = 0; i < items.Count; i++) ids.Add(items[i].Id);
        var kids = s.ChildList;
        for (int i = 0; i < kids.Count; i++) Collect(kids[i], ids);
    }

    static readonly System.Globalization.CultureInfo Culture = System.Globalization.CultureInfo.InvariantCulture;
}
// ── SIDEBAR COMMAND SET, UNDO RING, SHORTCUTS BAND, TEMPLATES ─────────────────────────────────────────────────────
//
// The custom-sidebar COMMAND SET is the only way a SidebarCustomLayout changes. Reducing a command yields a
// SidebarCommandResult; when Changed the caller pushes the pre-image onto SidebarUndo, clears redo, writes the
// document signal, and persists. Undo is a PRE-IMAGE SNAPSHOT, not an inverse command: the document and everything
// under it are immutable records, so an edit rebuilds only the spine (O(depth)) and structurally shares the rest —
// which is also why ApplyTemplate and ResetLayout are ordinary single-step undoable commands with no special
// machinery. This part also carries the shell top-bar band (a section materialized at render time, never
// persisted) and the five seed templates the customizer resets to.

/// <summary>Undo/redo label loc keys, one per command shape, kept as consts so each key is spelled exactly once.</summary>
public static class SidebarUndoLabels
{
    public const string AddSection = "sidebar.customizer.undo.addSection";
    public const string RemoveSection = "sidebar.customizer.undo.removeSection";
    public const string DuplicateSection = "sidebar.customizer.undo.duplicateSection";
    public const string RenameSection = "sidebar.customizer.undo.renameSection";
    public const string HideSection = "sidebar.customizer.undo.hideSection";
    public const string ShowSection = "sidebar.customizer.undo.showSection";
    public const string CollapseSection = "sidebar.customizer.undo.collapseSection";
    public const string ExpandSection = "sidebar.customizer.undo.expandSection";
    public const string MoveSection = "sidebar.customizer.undo.moveSection";
    public const string AddItem = "sidebar.customizer.undo.addItem";
    public const string MoveItem = "sidebar.customizer.undo.moveItem";
    public const string RemoveItem = "sidebar.customizer.undo.removeItem";
    public const string RelabelItem = "sidebar.customizer.undo.relabelItem";
    public const string ReiconItem = "sidebar.customizer.undo.reiconItem";
    public const string SetOption = "sidebar.customizer.undo.setOption";
    public const string SetQuery = "sidebar.customizer.undo.setQuery";
    // LAYOUT V2.
    public const string SetExtensionConfig = "sidebar.customizer.undo.setExtensionConfig";
    public const string SetItemAction = "sidebar.customizer.undo.setItemAction";
    // The shell TOP BAR band (one global list on the same document, so it rides the same undo ring).
    public const string AddTopBarItem = "sidebar.customizer.undo.addTopBarItem";
    public const string MoveTopBarItem = "sidebar.customizer.undo.moveTopBarItem";
    public const string RemoveTopBarItem = "sidebar.customizer.undo.removeTopBarItem";
    public const string ApplyTemplate = "sidebar.customizer.undo.applyTemplate";
    public const string Reset = "sidebar.customizer.undo.reset";
}

public abstract record SidebarCommand
{
    /// <summary>Drives the undo/redo tooltip ("Undo: Add section") and the a11y announcement.</summary>
    public abstract string LabelLocKey { get; }
}

/// <summary>Inserts a fresh section of <paramref name="Kind"/> at <paramref name="Index"/> under <paramref name="ParentId"/>
/// (null = top level). <paramref name="Item"/> is an optional seed member so a picker-driven add is one undoable step
/// instead of AddSection+AddItem; omitting it is legal for every kind. <paramref name="Extension"/> is REQUIRED for
/// <see cref="SidebarSectionKind.Extension"/> and ignored for every other kind.</summary>
public sealed record AddSection(SidebarSectionKind Kind, int Index, string? ParentId = null,
    SidebarItemSpec? Item = null, SidebarExtensionRef? Extension = null) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.AddSection;
}

public sealed record RemoveSection(string SectionId) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.RemoveSection;
}

/// <summary>Deep-clones the section (and children) with fresh ids for every section and item, inserted immediately
/// after the original. <paramref name="TitleOverride"/> carries the localized "{name} (copy)" title the customizer
/// formats; when null the clone keeps the original's Title/TitleLocKey verbatim.</summary>
public sealed record DuplicateSection(string SectionId, string? TitleOverride = null) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.DuplicateSection;
}

public sealed record RenameSection(string SectionId, string? Title) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.RenameSection;
}

public sealed record SetSectionHidden(string SectionId, bool Hidden) : SidebarCommand
{
    public override string LabelLocKey => Hidden ? SidebarUndoLabels.HideSection : SidebarUndoLabels.ShowSection;
}

public sealed record SetSectionCollapsed(string SectionId, bool Collapsed) : SidebarCommand
{
    public override string LabelLocKey =>
        Collapsed ? SidebarUndoLabels.CollapseSection : SidebarUndoLabels.ExpandSection;
}

public sealed record MoveSection(string SectionId, string? NewParentId, int NewIndex) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.MoveSection;
}

public sealed record AddItem(string SectionId, SidebarItemSpec Item, int Index) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.AddItem;
}

public sealed record MoveItem(string FromSectionId, int FromIndex, string ToSectionId, int ToIndex) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.MoveItem;
}

public sealed record RemoveItem(string SectionId, string ItemId) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.RemoveItem;
}

public sealed record SetItemLabel(string SectionId, string ItemId, string? Label) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.RelabelItem;
}

public sealed record SetItemIcon(string SectionId, string ItemId, string? IconName) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.ReiconItem;
}

public sealed record SetDisplayOption(string SectionId, SidebarDisplayField Field, int Value) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.SetOption;
}

public sealed record SetQuery(string SectionId, SidebarEntityQuery Query) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.SetQuery;
}

/// <summary>LAYOUT V2 — replaces an Extension section's opaque contribution config. Never interpreted by the reducer:
/// only size-checked (&gt; <see cref="SidebarExtensionRef.MaxConfigBytes"/> ⇒ <see cref="SidebarRejectReason.ConfigTooLarge"/>)
/// and cloned so the document owns it. On a non-Extension section this is a NoChange; on an Extension section with no
/// ref it is ExtensionRefMissing.</summary>
public sealed record SetExtensionConfig(string SectionId, JsonElement Config) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.SetExtensionConfig;
}

/// <summary>LAYOUT V2 — binds (or, with <paramref name="Binding"/> null, unbinds) an item's action. Orthogonal to
/// <see cref="SidebarItemSpec.Target"/> on purpose: the picker sets Target = Action when it creates the item, and this
/// command only ever rewrites the binding. A malformed binding (blank provider/action id) is a NoChange, not a silent
/// clear.</summary>
public sealed record SetItemAction(string SectionId, string ItemId, SidebarActionBinding? Binding) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.SetItemAction;
}

// ── the shell TOP BAR band ────────────────────────────────────────────────────────────────────────────────────────
// Three dedicated commands rather than AddItem/MoveItem/RemoveItem against a sentinel section: the band is a flat
// global list with its own cap (SidebarLayoutReducer.MaxTopBarItems), and the item commands' section arms carry
// per-kind rules that mean nothing here.

/// <summary>Inserts a shortcut into the shell's customizable top-bar band at <paramref name="Index"/> (clamped).
/// Reduced against <see cref="SidebarCustomLayout.EffectiveTopBar"/>, so the first add to a never-customized band
/// materializes the built-in default alongside the new item rather than silently discarding Home.</summary>
public sealed record AddTopBarItem(SidebarItemSpec Item, int Index) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.AddTopBarItem;
}

/// <summary>Reorders the band. Indices are into <see cref="SidebarCustomLayout.EffectiveTopBar"/>; <paramref name="ToIndex"/>
/// is interpreted AFTER the removal (the standard reorder contract, same as <c>MoveItem</c>).</summary>
public sealed record MoveTopBarItem(int FromIndex, int ToIndex) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.MoveTopBarItem;
}

/// <summary>Drops a shortcut from the band. Removing the last one leaves an EMPTY list, never null — "the user
/// emptied the band" and "the user never customized it" are different states (null re-renders the built-in Home).</summary>
public sealed record RemoveTopBarItem(string ItemId) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.RemoveTopBarItem;
}

public sealed record ApplyTemplate(string TemplateId) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.ApplyTemplate;
}

public sealed record ResetLayout() : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.Reset;
}

/// <summary>The reducer's verdict. <c>Changed == false</c> ⇒ the caller pushes NOTHING to undo and does NOT autosave.</summary>
public readonly record struct SidebarCommandResult(
    SidebarCustomLayout Layout,   // the new document (== input when !Changed)
    bool Changed,                 // false ⇒ no undo push, no save, no signal write
    SidebarRejectReason Reason)   // why nothing changed (diagnostics + a customizer inline message)
{
    public static SidebarCommandResult Ok(SidebarCustomLayout layout)
        => new(layout, true, SidebarRejectReason.None);

    public static SidebarCommandResult Reject(SidebarCustomLayout layout, SidebarRejectReason reason)
        => new(layout, false, reason);
}

/// <summary>Why a command changed nothing. Append only — the customizer maps these to inline messages and tests pin
/// the values.</summary>
public enum SidebarRejectReason : byte
{
    None = 0, UnknownSection, UnknownItem, NestingTooDeep, KindNotNestable,
    KindDoesNotAcceptItems, DuplicateItem, NoChange, UnknownTemplate, InvalidIcon, SectionCapReached,
    // LAYOUT V2.
    ConfigTooLarge,        // an extension config (or action arguments) over SidebarExtensionRef.MaxConfigBytes
    ExtensionRefMissing,   // an Extension section without an addressable SidebarExtensionRef
    KindNotDuplicable,     // DuplicateSection on a store-backed kind (SidebarSectionKinds.IsStoreBacked)
}

// ── UNDO RING ────────────────────────────────────────────────────────────────────────────────────────────────────
// The customizer's 50-step undo/redo, PRE-IMAGE SNAPSHOTS rather than inverse commands (see the file banner): keeping
// 50 whole documents costs 50 spines, not 50 copies. Pure and engine-free — the owner pairs every accepted command
// with a Push, but this type has no idea a signal, a file or a UI thread exists.

/// <summary>One undo step: the document as it was BEFORE the command, plus the command's label key.</summary>
public readonly record struct SidebarUndoEntry(SidebarCustomLayout Before, string LabelLocKey);

public sealed class SidebarUndo
{
    /// <summary>Undo depth. The oldest entry is evicted silently once full.</summary>
    public const int Capacity = 50;

    readonly SidebarUndoEntry[] _undo = new SidebarUndoEntry[Capacity];
    int _undoHead;                                     // next write slot
    int _undoCount;

    readonly SidebarUndoEntry[] _redo = new SidebarUndoEntry[Capacity];
    int _redoHead;
    int _redoCount;

    public int UndoDepth => _undoCount;
    public int RedoDepth => _redoCount;
    public bool CanUndo => _undoCount > 0;
    public bool CanRedo => _redoCount > 0;

    /// <summary>The label of the step Undo would take ("Add section"), for the tooltip and the a11y announcement.</summary>
    public string? UndoLabelLocKey => _undoCount > 0 ? Peek(_undo, _undoHead).LabelLocKey : null;
    public string? RedoLabelLocKey => _redoCount > 0 ? Peek(_redo, _redoHead).LabelLocKey : null;

    /// <summary>Records an ACCEPTED command's pre-image and clears redo. A rejected command (Changed == false) must
    /// never reach here.</summary>
    public void Push(SidebarCustomLayout before, string? labelLocKey)
    {
        ArgumentNullException.ThrowIfNull(before);
        PushInto(_undo, ref _undoHead, ref _undoCount, new SidebarUndoEntry(before, labelLocKey ?? string.Empty));
        _redoHead = 0;
        _redoCount = 0;
    }

    public void Push(SidebarCustomLayout before, SidebarCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Push(before, command.LabelLocKey);
    }

    /// <summary>Steps back: <paramref name="restored"/> is the pre-image, and <paramref name="current"/> becomes the
    /// redo step. False (no state change) when there is nothing to undo — the 51st undo is a clean no-op.</summary>
    public bool TryUndo(SidebarCustomLayout current, out SidebarCustomLayout restored, out string labelLocKey)
        => Step(current, _undo, ref _undoHead, ref _undoCount, _redo, ref _redoHead, ref _redoCount,
            out restored, out labelLocKey);

    public bool TryRedo(SidebarCustomLayout current, out SidebarCustomLayout restored, out string labelLocKey)
        => Step(current, _redo, ref _redoHead, ref _redoCount, _undo, ref _undoHead, ref _undoCount,
            out restored, out labelLocKey);

    /// <summary>Drops both stacks — used when the document is replaced from outside the command stream (a reload
    /// after a corrupt file, an account switch), where a pre-image from the old document would be nonsense.</summary>
    public void Clear()
    {
        Array.Clear(_undo);
        Array.Clear(_redo);
        _undoHead = _undoCount = _redoHead = _redoCount = 0;
    }

    static bool Step(SidebarCustomLayout current,
        SidebarUndoEntry[] from, ref int fromHead, ref int fromCount,
        SidebarUndoEntry[] to, ref int toHead, ref int toCount,
        out SidebarCustomLayout restored, out string labelLocKey)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (fromCount == 0)
        {
            restored = current;
            labelLocKey = string.Empty;
            return false;
        }

        fromHead = (fromHead - 1 + Capacity) % Capacity;
        var entry = from[fromHead];
        from[fromHead] = default;
        fromCount--;

        PushInto(to, ref toHead, ref toCount, new SidebarUndoEntry(current, entry.LabelLocKey));
        restored = entry.Before;
        labelLocKey = entry.LabelLocKey;
        return true;
    }

    static void PushInto(SidebarUndoEntry[] ring, ref int head, ref int count, SidebarUndoEntry entry)
    {
        ring[head] = entry;
        head = (head + 1) % Capacity;
        if (count < Capacity) count++;   // full ⇒ the oldest entry is overwritten, silently
    }

    static SidebarUndoEntry Peek(SidebarUndoEntry[] ring, int head) => ring[(head - 1 + Capacity) % Capacity];
}

// ── SHELL TOP-BAR BAND (materialized section, never persisted) ─────────────────────────────────────────────────────
// The band renders through the ordinary section pipeline instead of a bespoke component: `From` mints a section
// carrying the sentinel id SidebarIds.TopBarSection (never colliding with a real minted id) and it is prepended only
// to the document HANDED TO THE RENDERER, never to the persisted document — the reducer has no arm for the sentinel.
// The underlying list is still SidebarCustomLayout.TopBar/EffectiveTopBar, still mutated only through the three
// TopBarItem commands above, still capped at SidebarLayoutReducer.MaxTopBarItems, same undo ring, same autosave.
public static class SidebarShortcutsSection
{
    /// <summary>The section's localized title key. Deliberately NOT <c>sidebar.section.shortcuts</c> (already the
    /// CollectionShortcuts palette name) — the band's own keys live under <c>sidebar.topbar.*</c>.</summary>
    public const string TitleLocKey = "sidebar.topbar.title";

    /// <summary>Does the band render at all? An empty list means the user emptied it on purpose (no header, no rows,
    /// no rail tiles). Null never reaches here from a live pane, but is accepted for a headless caller.</summary>
    public static bool Renders([NotNullWhen(true)] IReadOnlyList<SidebarItemSpec>? topBar) => topBar is { Count: > 0 };

    /// <summary>The synthesized section. Kind = StaticLinks because that is exactly what the band is — a
    /// hand-authored list of routes/entities/tracks/bound actions — so the planner, rail, reorder and selection all
    /// serve it with no new code. Display uses the StaticLinks preset (not CollectionShortcuts): that preset forbids
    /// CountBadges, so a band default the user could never see or change is avoided. Callers must gate on
    /// <see cref="Renders"/> first; this never returns null.</summary>
    public static SidebarSectionSpec From(IReadOnlyList<SidebarItemSpec> topBar)
    {
        ArgumentNullException.ThrowIfNull(topBar);
        return new SidebarSectionSpec(
            Id: SidebarIds.TopBarSection,
            Kind: SidebarSectionKind.StaticLinks,
            Title: null,
            TitleLocKey: TitleLocKey,
            Hidden: false,
            // NOT collapsible: the band has no persisted Collapsed bit and no section-scoped command that could write
            // one, so a `true` here could never be undone.
            Collapsed: false,
            Display: SidebarDisplayOptions.Links with { ShowInRail = true },
            Items: topBar);
    }

    /// <summary>The document a PANE renders: <paramref name="document"/> with the band prepended as its first
    /// section. Returns the input UNCHANGED when the band is empty. This is a RENDER-PATH projection — the result
    /// must never be dispatched, saved or compared against the persisted document.</summary>
    public static SidebarCustomLayout Prepend(SidebarCustomLayout document, IReadOnlyList<SidebarItemSpec>? topBar)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Renders(topBar)) return document;

        var sections = new List<SidebarSectionSpec>(document.Sections.Count + 1) { From(topBar) };
        sections.AddRange(document.Sections);
        return document with { Sections = sections };
    }

    /// <summary>The five fixed library destinations, in presentation order — the ONE owner of that list, so the
    /// top-bar seed, the Classic dedupe and Library V3's destination strip cannot disagree.</summary>
    public static readonly string[] LibraryDestinations = ["liked", "albums", "artists", "podcasts", "local"];

    /// <summary>Is this route one of <see cref="LibraryDestinations"/>?</summary>
    public static bool IsLibraryDestination(string? routeKey)
    {
        if (string.IsNullOrEmpty(routeKey)) return false;
        for (int i = 0; i < LibraryDestinations.Length; i++)
            if (string.Equals(LibraryDestinations[i], routeKey, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Is <paramref name="routeKey"/> already a shortcut in the band? Only <see cref="SidebarItemTarget.Route"/>
    /// items count: an Entity item that happens to map onto the same destination is a different row.</summary>
    public static bool ContainsRoute(IReadOnlyList<SidebarItemSpec>? topBar, string routeKey)
    {
        if (topBar is null || string.IsNullOrEmpty(routeKey)) return false;
        for (int i = 0; i < topBar.Count; i++)
        {
            var item = topBar[i];
            if (item is null || item.Hidden) continue;
            if (item.Target == SidebarItemTarget.Route &&
                string.Equals(item.Key, routeKey, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

// ── TEMPLATES ────────────────────────────────────────────────────────────────────────────────────────────────────
// The five seed layouts. A template is a FUNCTION, not a stored document: Build mints fresh ids per call, so applying
// the same template twice yields two documents that differ only by id. Titles are authored as TitleLocKey (never a
// literal), so a template-seeded sidebar follows the UI culture until the user renames a section.
public static class SidebarTemplates
{
    public const string Curated = "curated";
    public const string ClassicInspired = "classic";
    public const string V3Inspired = "library";
    public const string Minimal = "minimal";
    public const string Blank = "blank";

    /// <summary>Palette order — this IS the order the customizer's template list renders.</summary>
    public static readonly string[] All = [Curated, ClassicInspired, V3Inspired, Minimal, Blank];

    public static bool IsKnown(string? templateId)
    {
        if (string.IsNullOrEmpty(templateId)) return false;
        for (int i = 0; i < All.Length; i++)
            if (string.Equals(All[i], templateId, StringComparison.Ordinal)) return true;
        return false;
    }

    public static string NameLocKey(string? templateId) => "sidebar.template." + Normalize(templateId);
    public static string DescriptionLocKey(string? templateId) => "sidebar.template." + Normalize(templateId) + "Sub";

    static string Normalize(string? templateId) => IsKnown(templateId) ? templateId! : Curated;

    /// <summary>Builds a fresh layout. Ids are newly generated per call. An unknown id yields Wavee Curated (and
    /// stamps <c>TemplateId = "curated"</c>), which is also what ResetLayout leans on to heal a hand-edited document.</summary>
    public static SidebarCustomLayout Build(string? templateId) => Normalize(templateId) switch
    {
        ClassicInspired => BuildClassicInspired(),
        V3Inspired => BuildV3Inspired(),
        Minimal => BuildMinimal(),
        Blank => new SidebarCustomLayout(Blank, Array.Empty<SidebarSectionSpec>()),
        _ => BuildCurated(),
    };

    // Wavee Curated — the fresh-install default. Liked Songs leads (the destination people open most); order is
    // user-reorderable so this is a default, not a constraint. Jump back in ships as recently played, top 4.
    static SidebarCustomLayout BuildCurated() => new(Curated,
    [
        Section(SidebarSectionKind.Pinned, "sidebar.pinned", SidebarDisplayOptions.Entities),
        Divider(),
        Section(SidebarSectionKind.JumpBackIn, "sidebar.section.recentlyPlayed",
            SidebarDisplayOptions.Entities with
            {
                Presentation = SidebarPresentation.Grid,
                GridColumns = 2,
                Artwork = true,
                Subtitles = false,
                MaxItems = 4,
                ShowInRail = false,
                Recents = SidebarRecentsSource.Played,
            }),
        Divider(),
        // The Shortcuts preset (Cozy + Subtitles:false) is the 40-DIP glyph row — pixel-identical to Classic's
        // shortcuts row. No density override: Cozy's 32-DIP art column already matches every other content row.
        Section(SidebarSectionKind.CollectionShortcuts, "sidebar.yourLibrary",
            SidebarDisplayOptions.Shortcuts,
        [
            Route("liked", "Heart"),
            Route("albums", "Album"),
            Route("artists", "Contact"),
            Route("podcasts", "RadioTower"),
            Route("local", "Folder"),
        ]),
        Divider(),
        Section(SidebarSectionKind.PlaylistTree, "sidebar.playlists", SidebarDisplayOptions.Entities),
    ]);

    // Classic-inspired — today's WaveeSidebar IA inside the Custom renderer. Classic's rail has no pin tiles.
    static SidebarCustomLayout BuildClassicInspired() => new(ClassicInspired,
    [
        Section(SidebarSectionKind.Pinned, "sidebar.pinned",
            SidebarDisplayOptions.Entities with { ShowInRail = false }),
        Section(SidebarSectionKind.CollectionShortcuts, "sidebar.yourLibrary",
            SidebarDisplayOptions.Shortcuts,
        [
            Route("albums", "Album"),
            Route("artists", "Contact"),
            Route("liked", "Heart"),
            Route("podcasts", "RadioTower"),
            Route("local", "Folder"),
        ]),
        Divider(),
        // Subtitles = the song-count caption.
        Section(SidebarSectionKind.PlaylistTree, "sidebar.playlists", SidebarDisplayOptions.Entities),
        Divider(),
        // Mirrors today's flat DevToolsRow, deliberately header-less. `Links`, not `Shortcuts`: a StaticLinks
        // section forbids CountBadges, so seeding it from the CollectionShortcuts preset would carry a dead flag.
        Section(SidebarSectionKind.StaticLinks, null, SidebarDisplayOptions.Links,
        [
            Route("api-console", "Code"),
        ]),
    ]);

    // Library-inspired — the V3 unified list embedded as a section. InlineControls = the self-contained, fully
    // customizable "Your Library" component (chips + sort/view flyout pinned under the header).
    static SidebarCustomLayout BuildV3Inspired() => new(V3Inspired,
    [
        Section(SidebarSectionKind.Pinned, "sidebar.pinned", SidebarDisplayOptions.Entities),
        Divider(),
        Section(SidebarSectionKind.EntityList, "sidebar.yourLibrary",
            SidebarDisplayOptions.Entities with { Subtitles = true, InlineControls = true },
            query: SidebarEntityQuery.Default),
        Divider(),
        Section(SidebarSectionKind.StaticLinks, null, SidebarDisplayOptions.Links,
        [
            Route("home", "Home"),
            Route("search", "Search"),
        ]),
    ]);

    static SidebarCustomLayout BuildMinimal() => new(Minimal,
    [
        Section(SidebarSectionKind.CollectionShortcuts, "sidebar.yourLibrary",
            SidebarDisplayOptions.Shortcuts with { CountBadges = false, Density = SidebarDensity.Compact },
        [
            Route("liked", "Heart"),
            Route("albums", "Album"),
            Route("artists", "Contact"),
        ]),
        Divider(),
        Section(SidebarSectionKind.PlaylistTree, "sidebar.playlists",
            SidebarDisplayOptions.Entities with
            {
                Artwork = false, Subtitles = false, Density = SidebarDensity.Compact,
            }),
    ]);

    static SidebarSectionSpec Section(SidebarSectionKind kind, string? titleLocKey, SidebarDisplayOptions display,
        IReadOnlyList<SidebarItemSpec>? items = null, SidebarEntityQuery? query = null)
        => new(SidebarIds.NewSection(), kind,
            Title: null,
            TitleLocKey: titleLocKey,
            Hidden: false,
            Collapsed: display.CollapsedByDefault,
            Display: display,
            Items: items,
            Query: query,
            Children: null);

    static SidebarSectionSpec Divider()
        => new(SidebarIds.NewSection(), SidebarSectionKind.Divider);

    static SidebarItemSpec Route(string routeKey, string iconName)
        => new(SidebarIds.NewItem(), SidebarItemTarget.Route, routeKey, IconOverride: iconName);
}
// ── SIDEBAR LAYOUT REDUCER ────────────────────────────────────────────────────────────────────────────────────────
// The PURE custom-sidebar reducer. One entry point, one verdict, zero side effects: Apply never mutates its input
// (every edit rebuilds only the spine and structurally shares the rest), never touches disk, never localizes, never
// reaches for a service. Rejections are DATA, not exceptions: !Changed carries a SidebarRejectReason the customizer
// surfaces inline, and the caller uses it to skip the undo push + the save entirely.

public static class SidebarLayoutReducer
{
    public const int MaxSections = 40;        // top level + children combined
    public const int MaxItemsPerSection = 500;
    public const int MaxTitleLength = 60;

    /// <summary>How many shortcuts the shell's TOP BAR band may hold. Small on purpose and enforced HERE (never in the
    /// UI): the band shares one 48-DIP row with the fixed chrome and the centred omnibar, so past this the omnibar
    /// starts losing its usable width. Over-cap is a <see cref="SidebarRejectReason.SectionCapReached"/> rejection,
    /// never a truncation.</summary>
    public const int MaxTopBarItems = 6;

    /// <summary>How many uris an include/exclude set may carry (LAYOUT V2). Same reasoning as the item cap: the sets
    /// exist so "only these artists" is a query instead of a hand-list — past this it IS a hand-list, and the document
    /// budget (2 MiB) is the next wall. Over-long sets are TRUNCATED by normalization, never rejected.</summary>
    public const int MaxUrisPerSet = 500;

    /// <summary>Reduce without a pin list — stale <c>Pinned</c> overrides are left alone (only pruned when the caller
    /// can prove a key is no longer pinned).</summary>
    public static SidebarCommandResult Apply(SidebarCustomLayout layout, SidebarCommand command)
        => Apply(layout, command, null);

    /// <summary>Reduce with the live pin key set (<see cref="SidebarItemSpec.Key"/> values, i.e. pinned uris). A
    /// <c>Pinned</c> section's override entries whose key is no longer pinned are pruned when — and only when — a
    /// command touches that section (never eagerly, so an accidental unpin+repin keeps the alias).</summary>
    public static SidebarCommandResult Apply(SidebarCustomLayout layout, SidebarCommand command,
        IReadOnlySet<string>? pinnedKeys)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(command);

        return command switch
        {
            AddSection c => DoAddSection(layout, c, pinnedKeys),
            RemoveSection c => DoRemoveSection(layout, c),
            DuplicateSection c => DoDuplicateSection(layout, c),
            RenameSection c => DoRenameSection(layout, c, pinnedKeys),
            SetSectionHidden c => DoSetHidden(layout, c, pinnedKeys),
            SetSectionCollapsed c => DoSetCollapsed(layout, c, pinnedKeys),
            MoveSection c => DoMoveSection(layout, c, pinnedKeys),
            AddItem c => DoAddItem(layout, c, pinnedKeys),
            MoveItem c => DoMoveItem(layout, c, pinnedKeys),
            RemoveItem c => DoRemoveItem(layout, c, pinnedKeys),
            SetItemLabel c => DoSetItemLabel(layout, c, pinnedKeys),
            SetItemIcon c => DoSetItemIcon(layout, c, pinnedKeys),
            SetDisplayOption c => DoSetDisplayOption(layout, c, pinnedKeys),
            SetQuery c => DoSetQuery(layout, c, pinnedKeys),
            SetExtensionConfig c => DoSetExtensionConfig(layout, c, pinnedKeys),
            SetItemAction c => DoSetItemAction(layout, c, pinnedKeys),
            AddTopBarItem c => DoAddTopBarItem(layout, c),
            MoveTopBarItem c => DoMoveTopBarItem(layout, c),
            RemoveTopBarItem c => DoRemoveTopBarItem(layout, c),
            ApplyTemplate c => DoApplyTemplate(layout, c),
            // A template / reset REPLACES the sidebar sections and PRESERVES the top bar: a template is a sidebar-
            // section preset, and silently reinstating Home in the shell chrome (or dropping the user's own shortcuts)
            // because they tried a different sidebar layout would be a data-shaped surprise. Carrying `null` through
            // keeps "never customized" meaning the built-in default.
            ResetLayout => SidebarCommandResult.Ok(
                SidebarTemplates.Build(layout.TemplateId) with { TopBar = layout.TopBar }),
            _ => SidebarCommandResult.Reject(layout, SidebarRejectReason.NoChange),
        };
    }

    // ── AddSection ───────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarCommandResult DoAddSection(SidebarCustomLayout l, AddSection c, IReadOnlySet<string>? pins)
    {
        // A kind this build does not understand can only arrive through the document (where it round-trips
        // untouched); it is never something the palette can add.
        if (!SidebarSectionKinds.IsKnown(c.Kind)) return Rej(l, SidebarRejectReason.NoChange);

        int parentTop = -1;
        if (c.ParentId is { Length: > 0 } pid)
        {
            if (!TryLocate(l, pid, out int pt, out int pc)) return Rej(l, SidebarRejectReason.UnknownSection);
            if (!SidebarSectionKinds.IsNestable(c.Kind)) return Rej(l, SidebarRejectReason.NestingTooDeep);
            if (pc >= 0) return Rej(l, SidebarRejectReason.NestingTooDeep);   // the parent is itself a child
            if (l.Sections[pt].Kind != SidebarSectionKind.CustomGroup)
                return Rej(l, SidebarRejectReason.KindNotNestable);
            parentTop = pt;
        }

        if (l.SectionCount >= MaxSections) return Rej(l, SidebarRejectReason.SectionCapReached);

        // LAYOUT V2: a contributed section is defined by its ref. The palette always has one (the user picked a
        // contribution); a ref-less Extension section could only render the "Manage extension" placeholder forever.
        SidebarExtensionRef? extension = null;
        if (SidebarSectionKinds.RequiresExtensionRef(c.Kind))
        {
            if (c.Extension is not { IsWellFormed: true } incoming)
                return Rej(l, SidebarRejectReason.ExtensionRefMissing);
            if (incoming.ConfigByteCount > SidebarExtensionRef.MaxConfigBytes)
                return Rej(l, SidebarRejectReason.ConfigTooLarge);
            extension = NormalizeRef(incoming);
        }

        IReadOnlyList<SidebarItemSpec>? items = null;
        if (c.Item is { } seed)
        {
            if (!SidebarSectionKinds.AcceptsItems(c.Kind)) return Rej(l, SidebarRejectReason.KindDoesNotAcceptItems);
            if (c.Kind == SidebarSectionKind.EntityEmbed && seed.Target != SidebarItemTarget.Entity)
                return Rej(l, SidebarRejectReason.KindDoesNotAcceptItems);
            if (seed.IconOverride is not null && !SidebarIconNames.IsAllowed(seed.IconOverride))
                return Rej(l, SidebarRejectReason.InvalidIcon);
            items = new[] { NormalizeItem(seed with { Id = SidebarIds.NewItem() }) };
        }

        var display = SidebarSectionKinds.DefaultDisplay(c.Kind);
        var spec = new SidebarSectionSpec(
            Id: FreshSectionId(l),
            Kind: c.Kind,
            Title: null,
            TitleLocKey: SidebarSectionKinds.DefaultTitleLocKey(c.Kind, display.Recents),
            Hidden: false,
            Collapsed: display.CollapsedByDefault,
            Display: display,
            Items: items,
            Query: c.Kind == SidebarSectionKind.EntityList ? SidebarEntityQuery.Default : null,
            Children: null,
            Extension: extension);

        var tops = new List<SidebarSectionSpec>(l.Sections);
        if (parentTop >= 0)
        {
            var parent = tops[parentTop];
            var kids = new List<SidebarSectionSpec>(parent.ChildList);
            kids.Insert(Math.Clamp(c.Index, 0, kids.Count), spec);
            tops[parentTop] = parent with { Children = kids };
        }
        else
        {
            tops.Insert(Math.Clamp(c.Index, 0, tops.Count), spec);
        }
        return SidebarCommandResult.Ok(l with { Sections = tops });
    }

    // ── RemoveSection / DuplicateSection ────────────────────────────────────────────────────────────────────────

    static SidebarCommandResult DoRemoveSection(SidebarCustomLayout l, RemoveSection c)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);

        var tops = new List<SidebarSectionSpec>(l.Sections);
        if (child < 0)
        {
            tops.RemoveAt(top);                    // removing a group removes its children with it
        }
        else
        {
            var p = tops[top];
            var kids = new List<SidebarSectionSpec>(p.ChildList);
            kids.RemoveAt(child);
            tops[top] = p with { Children = kids.Count == 0 ? null : kids };
        }
        return SidebarCommandResult.Ok(l with { Sections = tops });
    }

    static SidebarCommandResult DoDuplicateSection(SidebarCustomLayout l, DuplicateSection c)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var src = Get(l.Sections, top, child);

        // DEFECT 9 — a STORE-BACKED section cannot be duplicated. Its rows and their order live in a shared store (the
        // pin store, the rootlist), not in the spec, so a clone is a second WRITER onto one list: two Pinned sections
        // render the same pins and BOTH commit their reorders into the same store, so a drag in the copy silently
        // reshuffles the original. Fresh ids cannot separate them — the id never bound the section to the store, the
        // KIND did. Refused, not repaired: see SidebarSectionKinds.IsStoreBacked. A GROUP is refused when any child is
        // store-backed, for the same reason one level down.
        if (SidebarSectionKinds.IsStoreBacked(src.Kind)) return Rej(l, SidebarRejectReason.KindNotDuplicable);
        var srcKids = src.ChildList;
        for (int i = 0; i < srcKids.Count; i++)
            if (SidebarSectionKinds.IsStoreBacked(srcKids[i].Kind))
                return Rej(l, SidebarRejectReason.KindNotDuplicable);

        if (l.SectionCount + 1 + src.ChildList.Count > MaxSections)
            return Rej(l, SidebarRejectReason.SectionCapReached);

        var used = CollectIds(l);
        var clone = CloneWithFreshIds(src, used);
        // DEFECT 10 — an AUTHORED TitleLocKey survives the copy. RenameSection(null) reverts to the KIND DEFAULT, not
        // to whatever key the section carried, so:
        //   * TitleLocKey != null (template/kind-authored, culture-following) — a literal here would be UNRECOVERABLE
        //     (clearing the rename lands on the kind default and the key is gone for good), so the copy KEEPS the key
        //     and takes no literal — it keeps following the culture until the user renames it, one click, reversible.
        //   * TitleLocKey == null (a user rename, or no authored title) — nothing localized is lost and the literal is
        //     fully recoverable, so the caller's "{name} (copy)" lands verbatim.
        if (c.TitleOverride is { } t && clone.TitleLocKey is null)
        {
            var title = Shorten(t);
            clone = clone with { Title = title, TitleLocKey = null };
        }

        var tops = new List<SidebarSectionSpec>(l.Sections);
        if (child < 0)
        {
            tops.Insert(top + 1, clone);
        }
        else
        {
            var p = tops[top];
            var kids = new List<SidebarSectionSpec>(p.ChildList);
            kids.Insert(child + 1, clone);
            tops[top] = p with { Children = kids };
        }
        return SidebarCommandResult.Ok(l with { Sections = tops });
    }

    // ── Section scalars ─────────────────────────────────────────────────────────────────────────────────────────

    static SidebarCommandResult DoRenameSection(SidebarCustomLayout l, RenameSection c, IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);

        string? title = Shorten(c.Title);
        // Clearing a rename reverts to the localized kind default.
        string? locKey = title is null
            ? SidebarSectionKinds.DefaultTitleLocKey(s.Kind, s.Opts.Recents)
            : null;

        if (string.Equals(title, s.Title, StringComparison.Ordinal) &&
            string.Equals(locKey, s.TitleLocKey, StringComparison.Ordinal))
            return Rej(l, SidebarRejectReason.NoChange);

        return SidebarCommandResult.Ok(
            Replace(l, top, child, s with { Title = title, TitleLocKey = locKey }, pins));
    }

    static SidebarCommandResult DoSetHidden(SidebarCustomLayout l, SetSectionHidden c, IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        if (s.Hidden == c.Hidden) return Rej(l, SidebarRejectReason.NoChange);
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Hidden = c.Hidden }, pins));
    }

    static SidebarCommandResult DoSetCollapsed(SidebarCustomLayout l, SetSectionCollapsed c, IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        if (s.Collapsed == c.Collapsed) return Rej(l, SidebarRejectReason.NoChange);
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Collapsed = c.Collapsed }, pins));
    }

    static SidebarCommandResult DoMoveSection(SidebarCustomLayout l, MoveSection c, IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var moving = Get(l.Sections, top, child);

        int destTop = -1;
        if (c.NewParentId is { Length: > 0 } pid)
        {
            if (string.Equals(pid, c.SectionId, StringComparison.Ordinal))
                return Rej(l, SidebarRejectReason.NestingTooDeep);                 // into itself
            if (!TryLocate(l, pid, out int pt, out int pc)) return Rej(l, SidebarRejectReason.UnknownSection);
            if (pc >= 0) return Rej(l, SidebarRejectReason.NestingTooDeep);         // the target is itself a child
            if (!SidebarSectionKinds.IsNestable(moving.Kind))
                return Rej(l, SidebarRejectReason.NestingTooDeep);                  // a group may never nest…
            if (IsAncestorOf(l, c.SectionId, pid))
                return Rej(l, SidebarRejectReason.NestingTooDeep);                  // …nor into its own child
            if (l.Sections[pt].Kind != SidebarSectionKind.CustomGroup)
                return Rej(l, SidebarRejectReason.KindNotNestable);
            destTop = pt;
        }

        var tops = new List<SidebarSectionSpec>(l.Sections);

        // Remove first — NewIndex is interpreted AFTER removal (the standard Reorderable.OnReorder contract).
        if (child < 0)
        {
            tops.RemoveAt(top);
        }
        else
        {
            var p = tops[top];
            var kids = new List<SidebarSectionSpec>(p.ChildList);
            kids.RemoveAt(child);
            tops[top] = p with { Children = kids.Count == 0 ? null : kids };
        }

        int dt = destTop;
        if (child < 0 && dt > top) dt--;   // the removal shifted the destination group left

        var inserted = NormalizeSection(moving, pins);
        if (dt < 0)
        {
            tops.Insert(Math.Clamp(c.NewIndex, 0, tops.Count), inserted);
        }
        else
        {
            var p = tops[dt];
            var kids = new List<SidebarSectionSpec>(p.ChildList);
            kids.Insert(Math.Clamp(c.NewIndex, 0, kids.Count), inserted);
            tops[dt] = p with { Children = kids };
        }

        if (SameArrangement(l.Sections, tops)) return Rej(l, SidebarRejectReason.NoChange);
        return SidebarCommandResult.Ok(l with { Sections = tops });
    }

    // ── Items ────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarCommandResult DoAddItem(SidebarCustomLayout l, AddItem c, IReadOnlySet<string>? pins)
    {
        if (c.Item is null) return Rej(l, SidebarRejectReason.NoChange);
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);

        if (!SidebarSectionKinds.AcceptsItems(s.Kind)) return Rej(l, SidebarRejectReason.KindDoesNotAcceptItems);
        if (s.Kind == SidebarSectionKind.EntityEmbed && c.Item.Target != SidebarItemTarget.Entity)
            return Rej(l, SidebarRejectReason.KindDoesNotAcceptItems);
        if (c.Item.IconOverride is not null && !SidebarIconNames.IsAllowed(c.Item.IconOverride))
            return Rej(l, SidebarRejectReason.InvalidIcon);

        var items = new List<SidebarItemSpec>(s.ItemList);

        // EntityEmbed is the single-item kind: a second add RETARGETS the spotlight rather than stacking.
        if (s.Kind == SidebarSectionKind.EntityEmbed)
        {
            var one = NormalizeItem(c.Item with { Id = FreshItemId(l, c.Item.Id) });
            if (items.Count == 1 && ItemsEquivalent(items[0], one)) return Rej(l, SidebarRejectReason.NoChange);
            return SidebarCommandResult.Ok(
                Replace(l, top, child, s with { Items = new[] { one } }, pins));
        }

        for (int i = 0; i < items.Count; i++)
            if (items[i].Target == c.Item.Target && string.Equals(items[i].Key, c.Item.Key, StringComparison.Ordinal))
                return Rej(l, SidebarRejectReason.DuplicateItem);

        if (items.Count >= MaxItemsPerSection) return Rej(l, SidebarRejectReason.SectionCapReached);

        var item = NormalizeItem(c.Item with { Id = FreshItemId(l, c.Item.Id) });
        items.Insert(Math.Clamp(c.Index, 0, items.Count), item);
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Items = items }, pins));
    }

    static SidebarCommandResult DoMoveItem(SidebarCustomLayout l, MoveItem c, IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.FromSectionId, out int ft, out int fc)) return Rej(l, SidebarRejectReason.UnknownSection);
        if (!TryLocate(l, c.ToSectionId, out int tt, out int tc)) return Rej(l, SidebarRejectReason.UnknownSection);

        var src = Get(l.Sections, ft, fc);   // ("src", not "from" — `from … with` trips the query-expression parser)
        var to = Get(l.Sections, tt, tc);
        if (!SidebarSectionKinds.AcceptsItems(src.Kind) || !SidebarSectionKinds.AcceptsItems(to.Kind))
            return Rej(l, SidebarRejectReason.KindDoesNotAcceptItems);
        if (c.FromIndex < 0 || c.FromIndex >= src.ItemList.Count) return Rej(l, SidebarRejectReason.UnknownItem);

        var moving = src.ItemList[c.FromIndex];
        bool same = string.Equals(c.FromSectionId, c.ToSectionId, StringComparison.Ordinal);

        if (same)
        {
            var items = new List<SidebarItemSpec>(src.ItemList);
            items.RemoveAt(c.FromIndex);
            int at = Math.Clamp(c.ToIndex, 0, items.Count);
            if (at == c.FromIndex) return Rej(l, SidebarRejectReason.NoChange);
            items.Insert(at, moving);
            return SidebarCommandResult.Ok(Replace(l, ft, fc, src with { Items = items }, pins));
        }

        if (to.Kind == SidebarSectionKind.EntityEmbed && moving.Target != SidebarItemTarget.Entity)
            return Rej(l, SidebarRejectReason.KindDoesNotAcceptItems);
        for (int i = 0; i < to.ItemList.Count; i++)
            if (to.ItemList[i].Target == moving.Target &&
                string.Equals(to.ItemList[i].Key, moving.Key, StringComparison.Ordinal))
                return Rej(l, SidebarRejectReason.DuplicateItem);
        if (to.ItemList.Count >= SidebarSectionKinds.ItemCapacity(to.Kind))
            return Rej(l, SidebarRejectReason.SectionCapReached);

        var srcItems = new List<SidebarItemSpec>(src.ItemList);
        srcItems.RemoveAt(c.FromIndex);
        var dstItems = new List<SidebarItemSpec>(to.ItemList);
        dstItems.Insert(Math.Clamp(c.ToIndex, 0, dstItems.Count), moving);

        var tops = new List<SidebarSectionSpec>(l.Sections);
        Set(tops, ft, fc, NormalizeSection(src with { Items = srcItems.Count == 0 ? null : srcItems }, pins));
        // Re-locate the destination: Set rebuilt the parent record, not the indices, so (tt, tc) still address it.
        Set(tops, tt, tc, NormalizeSection(Get(tops, tt, tc) with { Items = dstItems }, pins));
        return SidebarCommandResult.Ok(l with { Sections = tops });
    }

    static SidebarCommandResult DoRemoveItem(SidebarCustomLayout l, RemoveItem c, IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        int at = IndexOfItem(s, c.ItemId);
        if (at < 0) return Rej(l, SidebarRejectReason.UnknownItem);

        var items = new List<SidebarItemSpec>(s.ItemList);
        items.RemoveAt(at);
        return SidebarCommandResult.Ok(
            Replace(l, top, child, s with { Items = items.Count == 0 ? null : items }, pins));
    }

    static SidebarCommandResult DoSetItemLabel(SidebarCustomLayout l, SetItemLabel c, IReadOnlySet<string>? pins)
    {
        if (SidebarIds.IsTopBar(c.SectionId))
        {
            int bandAt = TopBarIndexOf(l, c.ItemId);
            if (bandAt < 0) return Rej(l, SidebarRejectReason.UnknownItem);
            var tile = l.EffectiveTopBar[bandAt];
            string? alias = Shorten(c.Label);
            if (string.Equals(alias, tile.LabelOverride, StringComparison.Ordinal))
                return Rej(l, SidebarRejectReason.NoChange);
            return TopBarReplace(l, bandAt, tile with { LabelOverride = alias });
        }
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        int at = IndexOfItem(s, c.ItemId);
        if (at < 0) return Rej(l, SidebarRejectReason.UnknownItem);

        string? label = Shorten(c.Label);
        if (string.Equals(label, s.ItemList[at].LabelOverride, StringComparison.Ordinal))
            return Rej(l, SidebarRejectReason.NoChange);

        var items = new List<SidebarItemSpec>(s.ItemList);
        items[at] = items[at] with { LabelOverride = label };
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Items = items }, pins));
    }

    static SidebarCommandResult DoSetItemIcon(SidebarCustomLayout l, SetItemIcon c, IReadOnlySet<string>? pins)
    {
        if (SidebarIds.IsTopBar(c.SectionId))
        {
            int bandAt = TopBarIndexOf(l, c.ItemId);
            if (bandAt < 0) return Rej(l, SidebarRejectReason.UnknownItem);
            var tile = l.EffectiveTopBar[bandAt];
            string? mark = c.IconName is { Length: > 0 } ? c.IconName : null;
            if (mark is not null && !SidebarIconNames.IsAllowed(mark)) return Rej(l, SidebarRejectReason.InvalidIcon);
            if (string.Equals(mark, tile.IconOverride, StringComparison.Ordinal))
                return Rej(l, SidebarRejectReason.NoChange);
            return TopBarReplace(l, bandAt, tile with { IconOverride = mark });
        }
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        int at = IndexOfItem(s, c.ItemId);
        if (at < 0) return Rej(l, SidebarRejectReason.UnknownItem);

        string? icon = c.IconName is { Length: > 0 } ? c.IconName : null;
        if (icon is not null && !SidebarIconNames.IsAllowed(icon)) return Rej(l, SidebarRejectReason.InvalidIcon);
        if (string.Equals(icon, s.ItemList[at].IconOverride, StringComparison.Ordinal))
            return Rej(l, SidebarRejectReason.NoChange);

        var items = new List<SidebarItemSpec>(s.ItemList);
        items[at] = items[at] with { IconOverride = icon };
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Items = items }, pins));
    }

    // ── Display + query ──────────────────────────────────────────────────────────────────────────────────────────

    static SidebarCommandResult DoSetDisplayOption(SidebarCustomLayout l, SetDisplayOption c,
        IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        if (!SidebarSectionKinds.AllowsDisplayField(s.Kind, c.Field)) return Rej(l, SidebarRejectReason.NoChange);

        var opts = WithField(s.Opts, c.Field, c.Value);
        if (opts == s.Opts) return Rej(l, SidebarRejectReason.NoChange);

        var next = s with { Display = opts };
        // A JumpBackIn section that still wears its localized kind default retargets it when the recents source flips
        // ("Jump back in" <-> "Recently played"); an explicit rename (Title != null) is never touched.
        if (c.Field == SidebarDisplayField.RecentsSource && next.Title is null && next.TitleLocKey is not null)
            next = next with { TitleLocKey = SidebarSectionKinds.DefaultTitleLocKey(next.Kind, opts.Recents) };

        // Setting CollapsedByDefault must NOT change the live Collapsed state.
        return SidebarCommandResult.Ok(Replace(l, top, child, next, pins));
    }

    static SidebarCommandResult DoSetQuery(SidebarCustomLayout l, SetQuery c, IReadOnlySet<string>? pins)
    {
        if (c.Query is null) return Rej(l, SidebarRejectReason.NoChange);
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        // Only library-query kinds have a query; the closed reject-reason enum has no dedicated code, so every other
        // section reads as NoChange.
        if (!SidebarSectionKinds.SupportsLibraryQuery(s.Kind)) return Rej(l, SidebarRejectReason.NoChange);

        var q = RepairQuery(c.Query, s.Kind);
        if (q == SidebarSectionKinds.EffectiveQuery(s.Kind, s.Query)) return Rej(l, SidebarRejectReason.NoChange);
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Query = q }, pins));
    }

    // ── extension config / action bindings (LAYOUT V2) ──────────────────────────────────────────────────────────

    static SidebarCommandResult DoSetExtensionConfig(SidebarCustomLayout l, SetExtensionConfig c,
        IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        // Only an Extension section has a config; the closed reject-reason enum reads this as NoChange, like SetQuery
        // on a non-EntityList section.
        if (!s.IsExtension) return Rej(l, SidebarRejectReason.NoChange);
        if (s.Extension is not { } current) return Rej(l, SidebarRejectReason.ExtensionRefMissing);

        if (SidebarJson.ByteCount(c.Config) > SidebarExtensionRef.MaxConfigBytes)
            return Rej(l, SidebarRejectReason.ConfigTooLarge);
        if (SidebarJson.Same(current.Config, c.Config)) return Rej(l, SidebarRejectReason.NoChange);

        // Own the element: the caller's JsonDocument may be pooled/disposed, and the document must stay serializable.
        var next = current with { Config = SidebarJson.Own(c.Config) };
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Extension = next }, pins));
    }

    static SidebarCommandResult DoSetItemAction(SidebarCustomLayout l, SetItemAction c, IReadOnlySet<string>? pins)
    {
        // The binding validation is identical for a section item and a top-bar tile, so it runs BEFORE the addressing
        // split.
        SidebarActionBinding? binding = null;
        if (c.Binding is { } incoming)
        {
            // A blank provider/action id is not a "clear" — the caller asked for a binding it cannot address.
            if (NormalizeBinding(incoming) is not { } normalized) return Rej(l, SidebarRejectReason.NoChange);
            if (normalized.ArgumentsByteCount > SidebarExtensionRef.MaxConfigBytes)
                return Rej(l, SidebarRejectReason.ConfigTooLarge);
            binding = normalized;
        }

        if (SidebarIds.IsTopBar(c.SectionId))
        {
            int bandAt = TopBarIndexOf(l, c.ItemId);
            if (bandAt < 0) return Rej(l, SidebarRejectReason.UnknownItem);
            var tile = l.EffectiveTopBar[bandAt];
            if (binding == tile.Action) return Rej(l, SidebarRejectReason.NoChange);
            return TopBarReplace(l, bandAt, tile with { Action = binding });
        }

        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        int at = IndexOfItem(s, c.ItemId);
        if (at < 0) return Rej(l, SidebarRejectReason.UnknownItem);

        if (binding == s.ItemList[at].Action) return Rej(l, SidebarRejectReason.NoChange);

        var items = new List<SidebarItemSpec>(s.ItemList);
        items[at] = items[at] with { Action = binding };
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Items = items }, pins));
    }

    static SidebarCommandResult DoApplyTemplate(SidebarCustomLayout l, ApplyTemplate c)
    {
        if (!SidebarTemplates.IsKnown(c.TemplateId)) return Rej(l, SidebarRejectReason.UnknownTemplate);
        // PRESERVE the top bar — see the ResetLayout arm in Apply for why a template must not touch shell chrome.
        return SidebarCommandResult.Ok(SidebarTemplates.Build(c.TemplateId) with { TopBar = l.TopBar });
    }

    // ── the shell TOP BAR band ───────────────────────────────────────────────────────────────────────────────────
    // Every arm reduces against EffectiveTopBar, so the first edit to a never-customized band materializes the
    // built-in default (Home) instead of starting from nothing — what the user sees IS what they are editing.

    static SidebarCommandResult DoAddTopBarItem(SidebarCustomLayout l, AddTopBarItem c)
    {
        if (c.Item is null) return Rej(l, SidebarRejectReason.NoChange);
        if (c.Item.IconOverride is not null && !SidebarIconNames.IsAllowed(c.Item.IconOverride))
            return Rej(l, SidebarRejectReason.InvalidIcon);

        var band = l.EffectiveTopBar;
        for (int i = 0; i < band.Count; i++)
            if (band[i].Target == c.Item.Target && string.Equals(band[i].Key, c.Item.Key, StringComparison.Ordinal))
                return Rej(l, SidebarRejectReason.DuplicateItem);
        if (band.Count >= MaxTopBarItems) return Rej(l, SidebarRejectReason.SectionCapReached);

        var items = new List<SidebarItemSpec>(band);
        items.Insert(Math.Clamp(c.Index, 0, items.Count), NormalizeItem(c.Item with { Id = FreshItemId(l, c.Item.Id) }));
        return SidebarCommandResult.Ok(l with { TopBar = items });
    }

    static SidebarCommandResult DoMoveTopBarItem(SidebarCustomLayout l, MoveTopBarItem c)
    {
        var band = l.EffectiveTopBar;
        if (c.FromIndex < 0 || c.FromIndex >= band.Count) return Rej(l, SidebarRejectReason.UnknownItem);

        var items = new List<SidebarItemSpec>(band);
        var moving = items[c.FromIndex];
        items.RemoveAt(c.FromIndex);
        int at = Math.Clamp(c.ToIndex, 0, items.Count);
        if (at == c.FromIndex) return Rej(l, SidebarRejectReason.NoChange);
        items.Insert(at, moving);
        return SidebarCommandResult.Ok(l with { TopBar = items });
    }

    static SidebarCommandResult DoRemoveTopBarItem(SidebarCustomLayout l, RemoveTopBarItem c)
    {
        var band = l.EffectiveTopBar;
        int at = IndexOfItem(band, c.ItemId);
        if (at < 0) return Rej(l, SidebarRejectReason.UnknownItem);

        var items = new List<SidebarItemSpec>(band);
        items.RemoveAt(at);
        // An EMPTY list, never null: null would re-render the built-in Home the user just removed.
        return SidebarCommandResult.Ok(l with { TopBar = items });
    }

    /// <summary>Locate a tile in the effective band, or -1 — the shared head of the three item-PROPERTY commands when
    /// they are addressed at <see cref="SidebarIds.TopBarSection"/>.</summary>
    static int TopBarIndexOf(SidebarCustomLayout l, string? itemId)
        => itemId is { Length: > 0 } id ? IndexOfItem(l.EffectiveTopBar, id) : -1;

    /// <summary>Rewrite one tile in place. The band is copied here (never mutated), exactly like a section's item list.</summary>
    static SidebarCommandResult TopBarReplace(SidebarCustomLayout l, int at, SidebarItemSpec item)
    {
        var items = new List<SidebarItemSpec>(l.EffectiveTopBar);
        items[at] = item;
        return SidebarCommandResult.Ok(l with { TopBar = items });
    }

    /// <summary>Legality repair for a library-query kind, never a rejection (SetQuery).</summary>
    public static SidebarEntityQuery RepairQuery(SidebarEntityQuery q)
        => RepairQuery(q, SidebarSectionKind.EntityList);

    /// <summary>Legality repair against the OWNING kind (LAYOUT V2). The scalar repairs are unconditional; the
    /// include/exclude uri sets are normalized (trim, drop blanks, dedupe ordinal, truncate to
    /// <see cref="MaxUrisPerSet"/>, empty ⇒ null) and survive ONLY on a library-query-capable kind — a query that
    /// ended up on some other kind (a hand-edited document, a kind changed by a newer build) loses its uri sets
    /// rather than carrying a filter nothing applies.</summary>
    public static SidebarEntityQuery RepairQuery(SidebarEntityQuery q, SidebarSectionKind kind)
    {
        var kinds = kind == SidebarSectionKind.PlaylistTree
            ? SidebarEntityKinds.Playlists
            : q.Kinds == SidebarEntityKinds.None ? SidebarEntityKinds.All : q.Kinds;
        var sort = q.Sort;
        if (sort == SidebarSortMode.CustomOrder && kinds != SidebarEntityKinds.Playlists)
            sort = SidebarSortMode.Alphabetical;
        var qual = q.Qualifier;
        if (qual != SidebarPlaylistQualifier.Any && (kinds & SidebarEntityKinds.Playlists) == 0)
            qual = SidebarPlaylistQualifier.Any;

        bool queryable = SidebarSectionKinds.SupportsLibraryQuery(kind);
        var include = queryable ? NormalizeUris(q.IncludeUris) : null;
        var exclude = queryable ? NormalizeUris(q.ExcludeUris) : null;

        return q with
        {
            Kinds = kinds, Sort = sort, Qualifier = qual, IncludeUris = include, ExcludeUris = exclude,
        };
    }

    /// <summary>Trim, drop blanks, dedupe (ordinal, first wins), truncate to <see cref="MaxUrisPerSet"/>; an empty
    /// result is null so the wire never carries <c>[]</c>. Returns the SAME instance when nothing needed changing.</summary>
    public static IReadOnlyList<string>? NormalizeUris(IReadOnlyList<string>? uris)
    {
        if (uris is null || uris.Count == 0) return null;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<string>? kept = null;                     // stays null while every entry so far survived UNCHANGED
        for (int i = 0; i < uris.Count; i++)
        {
            string raw = uris[i] ?? "";
            string one = raw.Trim();
            bool keep = one.Length > 0 && seen.Count < MaxUrisPerSet && seen.Add(one);
            if (keep && kept is null && string.Equals(one, raw, StringComparison.Ordinal)) continue;
            kept ??= Prefix(uris, i);
            if (keep) kept.Add(one);
        }

        if (kept is null) return uris;                 // already canonical — keep the caller's instance
        return kept.Count == 0 ? null : kept;

        static List<string> Prefix(IReadOnlyList<string> src, int count)
        {
            var list = new List<string>(src.Count);
            for (int j = 0; j < count; j++) list.Add(src[j]);
            return list;
        }
    }

    /// <summary>Per-field clamp (SetDisplayOption). Bools encode 0/1; every numeric field has a hard range.</summary>
    public static SidebarDisplayOptions WithField(SidebarDisplayOptions o, SidebarDisplayField f, int v) => f switch
    {
        SidebarDisplayField.Density => o with { Density = (SidebarDensity)Math.Clamp(v, 0, 2) },
        SidebarDisplayField.Presentation => o with { Presentation = (SidebarPresentation)Math.Clamp(v, 0, 1) },
        SidebarDisplayField.Artwork => o with { Artwork = v != 0 },
        SidebarDisplayField.Subtitles => o with { Subtitles = v != 0 },
        SidebarDisplayField.CountBadges => o with { CountBadges = v != 0 },
        SidebarDisplayField.CollapsedByDefault => o with { CollapsedByDefault = v != 0 },
        SidebarDisplayField.ShowInRail => o with { ShowInRail = v != 0 },
        SidebarDisplayField.MaxItems => o with { MaxItems = Math.Clamp(v, 0, MaxItemsPerSection) },
        SidebarDisplayField.GridColumns => o with { GridColumns = Math.Clamp(v, 2, 4) },
        SidebarDisplayField.InlineControls => o with { InlineControls = v != 0 },
        SidebarDisplayField.PlayButton => o with { PlayButton = v != 0 },
        SidebarDisplayField.RecentsSource => o with { Recents = (SidebarRecentsSource)Math.Clamp(v, 0, 1) },
        SidebarDisplayField.EmptyBehavior => o with { EmptyBehavior = (SidebarEmptyBehavior)Math.Clamp(v, 0, 3) },
        _ => o,
    };

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarCommandResult Rej(SidebarCustomLayout l, SidebarRejectReason r)
        => SidebarCommandResult.Reject(l, r);

    static bool TryLocate(SidebarCustomLayout l, string? id, out int top, out int child)
    {
        top = -1; child = -1;
        if (id is null or { Length: 0 }) return false;
        for (int i = 0; i < l.Sections.Count; i++)
        {
            var s = l.Sections[i];
            if (string.Equals(s.Id, id, StringComparison.Ordinal)) { top = i; child = -1; return true; }
            var kids = s.ChildList;
            for (int j = 0; j < kids.Count; j++)
                if (string.Equals(kids[j].Id, id, StringComparison.Ordinal)) { top = i; child = j; return true; }
        }
        return false;
    }

    static SidebarSectionSpec Get(IReadOnlyList<SidebarSectionSpec> tops, int top, int child)
        => child < 0 ? tops[top] : tops[top].ChildList[child];

    static void Set(List<SidebarSectionSpec> tops, int top, int child, SidebarSectionSpec spec)
    {
        if (child < 0) { tops[top] = spec; return; }
        var p = tops[top];
        var kids = new List<SidebarSectionSpec>(p.ChildList);
        kids[child] = spec;
        tops[top] = p with { Children = kids };
    }

    static SidebarCustomLayout Replace(SidebarCustomLayout l, int top, int child, SidebarSectionSpec spec,
        IReadOnlySet<string>? pins)
    {
        var tops = new List<SidebarSectionSpec>(l.Sections);
        Set(tops, top, child, NormalizeSection(spec, pins));
        return l with { Sections = tops };
    }

    /// <summary>Invariants re-established every time a command TOUCHES a section: EntityEmbed keeps exactly one item
    /// (a hand-edited document may carry more), and a Pinned section's override entries whose key is no longer pinned
    /// are pruned — lazily, only here, so an accidental unpin+repin keeps the alias.</summary>
    static SidebarSectionSpec NormalizeSection(SidebarSectionSpec s, IReadOnlySet<string>? pins)
    {
        if (s.Kind == SidebarSectionKind.EntityEmbed && s.ItemList.Count > 1)
            s = s with { Items = new[] { s.ItemList[0] } };

        // LAYOUT V2: the include/exclude uri sets only mean something on a library-query kind. A query that ended up
        // somewhere else (hand-edited document, a kind a newer build changed) keeps its scalars but loses the uri sets.
        if (s.Query is { } q && (q.HasIncludeSet || q.HasExcludeSet) &&
            !SidebarSectionKinds.SupportsLibraryQuery(s.Kind))
            s = s with { Query = q with { IncludeUris = null, ExcludeUris = null } };

        if (s.Kind == SidebarSectionKind.Pinned && pins is not null && s.Items is { Count: > 0 } items)
        {
            List<SidebarItemSpec>? keep = null;
            for (int i = 0; i < items.Count; i++)
            {
                if (pins.Contains(items[i].Key)) { keep?.Add(items[i]); continue; }
                if (keep is null)
                {
                    keep = new List<SidebarItemSpec>(items.Count);
                    for (int j = 0; j < i; j++) keep.Add(items[j]);
                }
            }
            if (keep is not null) s = s with { Items = keep.Count == 0 ? null : keep };
        }
        return s;
    }

    static SidebarItemSpec NormalizeItem(SidebarItemSpec i)
    {
        var label = Shorten(i.LabelOverride);
        var icon = i.IconOverride is { Length: > 0 } ? i.IconOverride : null;
        // LAYOUT V2: an action binding that arrived with the item goes through the same normalization SetItemAction
        // uses (trimmed ids, mode-consistent target key, an owned arguments element). A malformed one is dropped, not
        // kept: the item itself is still legal, and SetItemAction is how a real binding lands.
        var action = i.Action is { } b ? NormalizeBinding(b) : null;
        return string.Equals(label, i.LabelOverride, StringComparison.Ordinal) &&
               string.Equals(icon, i.IconOverride, StringComparison.Ordinal) && action == i.Action
            ? i
            : i with { LabelOverride = label, IconOverride = icon, Action = action };
    }

    /// <summary>LAYOUT V2 — trim the two ids and take ownership of the config element. Nothing about the config's
    /// SHAPE is validated here: only the contributing source knows its schema, and an unknown member must round-trip.</summary>
    static SidebarExtensionRef NormalizeRef(SidebarExtensionRef r)
        => new(r.ExtensionId.Trim(), r.ContributionId.Trim(), r.SchemaVersion, SidebarJson.Own(r.Config));

    /// <summary>LAYOUT V2 — trim the ids, clear a target key the mode cannot use, own the arguments element. Null for
    /// a binding that cannot address an action (blank provider or action id), which the caller turns into a NoChange.</summary>
    static SidebarActionBinding? NormalizeBinding(SidebarActionBinding b)
    {
        string provider = (b.ProviderId ?? "").Trim();
        string action = (b.ActionId ?? "").Trim();
        if (provider.Length == 0 || action.Length == 0) return null;

        string? key = b.TargetKey is { } k && k.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        if (!b.RequiresTargetKey) key = null;      // a leftover key from a previous mode is noise, never data

        return new SidebarActionBinding(provider, action, b.TargetMode, key, SidebarJson.Own(b.Arguments));
    }

    static bool ItemsEquivalent(SidebarItemSpec a, SidebarItemSpec b)
        => a.Target == b.Target && string.Equals(a.Key, b.Key, StringComparison.Ordinal) &&
           a.EntityKind == b.EntityKind &&
           string.Equals(a.LabelOverride, b.LabelOverride, StringComparison.Ordinal) &&
           string.Equals(a.IconOverride, b.IconOverride, StringComparison.Ordinal) &&
           a.Hidden == b.Hidden && a.Action == b.Action;

    static int IndexOfItem(SidebarSectionSpec s, string itemId) => IndexOfItem(s.ItemList, itemId);

    static int IndexOfItem(IReadOnlyList<SidebarItemSpec> items, string itemId)
    {
        for (int i = 0; i < items.Count; i++)
            if (string.Equals(items[i].Id, itemId, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Trim, normalize "" to null, truncate to 60 chars (truncated, never rejected).</summary>
    static string? Shorten(string? s)
    {
        if (s is null) return null;
        var t = s.Trim();
        if (t.Length == 0) return null;
        return t.Length <= MaxTitleLength ? t : t[..MaxTitleLength];
    }

    static bool IsAncestorOf(SidebarCustomLayout l, string sectionId, string candidateChildId)
    {
        var s = l.Find(sectionId);
        if (s is null) return false;
        var kids = s.ChildList;
        for (int i = 0; i < kids.Count; i++)
            if (string.Equals(kids[i].Id, candidateChildId, StringComparison.Ordinal)) return true;
        return false;
    }

    static bool SameArrangement(IReadOnlyList<SidebarSectionSpec> a, IReadOnlyList<SidebarSectionSpec> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal)) return false;
            var ka = a[i].ChildList; var kb = b[i].ChildList;
            if (ka.Count != kb.Count) return false;
            for (int j = 0; j < ka.Count; j++)
                if (!string.Equals(ka[j].Id, kb[j].Id, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    // ── Ids ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static HashSet<string> CollectIds(SidebarCustomLayout l)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < l.Sections.Count; i++) CollectIds(l.Sections[i], set);
        // The top-bar band shares the document's item-id space, and the built-in default's id is RESERVED even when
        // the band was emptied — otherwise a freshly minted id could collide with the Home tile a later add
        // re-materializes.
        var band = l.EffectiveTopBar;
        for (int i = 0; i < band.Count; i++) set.Add(band[i].Id);
        set.Add(SidebarIds.TopBarHomeItem);
        return set;
    }

    static void CollectIds(SidebarSectionSpec s, HashSet<string> set)
    {
        set.Add(s.Id);
        var items = s.ItemList;
        for (int i = 0; i < items.Count; i++) set.Add(items[i].Id);
        var kids = s.ChildList;
        for (int i = 0; i < kids.Count; i++) CollectIds(kids[i], set);
    }

    static string FreshSectionId(SidebarCustomLayout l)
    {
        var used = CollectIds(l);
        string id;
        do { id = SidebarIds.NewSection(); } while (used.Contains(id));
        return id;
    }

    /// <summary>Keeps <paramref name="incoming"/> when it is well-formed and unique; regenerates on a collision (or
    /// when the caller left it blank).</summary>
    static string FreshItemId(SidebarCustomLayout l, string? incoming = null)
    {
        var used = CollectIds(l);
        if (incoming is { Length: > 0 } && !used.Contains(incoming)) return incoming;
        string id;
        do { id = SidebarIds.NewItem(); } while (used.Contains(id));
        return id;
    }

    static SidebarSectionSpec CloneWithFreshIds(SidebarSectionSpec s, HashSet<string> used)
    {
        IReadOnlyList<SidebarItemSpec>? items = null;
        if (s.Items is { Count: > 0 })
        {
            var copy = new SidebarItemSpec[s.Items.Count];
            for (int i = 0; i < copy.Length; i++)
                copy[i] = s.Items[i] with { Id = Mint(used, section: false) };
            items = copy;
        }

        IReadOnlyList<SidebarSectionSpec>? kids = null;
        if (s.Children is { Count: > 0 })
        {
            var copy = new SidebarSectionSpec[s.Children.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = CloneWithFreshIds(s.Children[i], used);
            kids = copy;
        }

        return s with { Id = Mint(used, section: true), Items = items, Children = kids };
    }

    static string Mint(HashSet<string> used, bool section)
    {
        string id;
        do { id = section ? SidebarIds.NewSection() : SidebarIds.NewItem(); } while (!used.Add(id));
        return id;
    }
}
// ── SIDEBAR-LAYOUT.JSON: WIRE, MIGRATIONS, DEFAULTS ───────────────────────────────────────────────────────────────────
// The versioned wire DTOs, the AOT source-generated JSON context, the model⇄wire translation, the version ladder and
// the built-in documents for `sidebar-layout.json`. Everything here goes through `SidebarLayoutJsonCtx` — never
// reflection-based JSON. Nothing in this section throws on an unknown or missing member: an unrecognized section kind
// round-trips untouched at its original index, and unknown members anywhere survive via `[JsonExtensionData]`,
// re-attached by owning id through `SidebarWireCarry`. The DTOs are deliberately separate from the live records
// (`SidebarCustomLayout` & co.): the wire shape is flat, nullable-tolerant and versioned, so a shape change is an
// upgrade, never a silent data loss.

public sealed class SidebarLayoutDocDto
{
    public int Version { get; set; }                         // REQUIRED. 2 = this schema (v1 upgrades by IDENTITY —
                                                               // see SidebarLayoutMigrations). 0/absent is not accepted.
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
/// frozen forever in <see cref="SidebarLayoutWire.TryLegacyPinKind"/> — kept on write so a build predating the
/// <c>SidebarPinKind</c>/<c>SidebarEntryKind</c> unification does not lose the pin on downgrade. <see cref="EntityKind"/>
/// is the preferred string form and is read first; <c>Kind</c> is consulted only when it is absent. Both are written on
/// every save.</summary>
public sealed class SidebarPinDto
{
    public string? Id { get; set; }                           // REQUIRED — the stable pin id
    public int Kind { get; set; }                              // LEGACY — see SidebarLayoutWire.TryLegacyPinKind
    public string? EntityKind { get; set; }                    // "playlist"|"album"|"artist"|"show"|"playlistFolder"|
                                                                // "appRoute"|"track" — see SidebarLayoutWire.PinKindName
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

public readonly record struct SidebarFirstSeenDto(string Id, long Ms);

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

// ── model ⇄ wire ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The opaque forward-compat carry from <see cref="SidebarLayoutWire.ReadCurated"/>: unrecognized-kind
/// sections and unknown members this build must re-emit untouched. Hand it back to
/// <see cref="SidebarLayoutWire.WriteCurated"/> on every snapshot; pass <see cref="Empty"/> for a fresh layout.</summary>
public sealed class SidebarWireCarry
{
    /// <summary>The carry for a document never loaded from disk (a template build / a fresh install).</summary>
    public static readonly SidebarWireCarry Empty = new();

    internal readonly Dictionary<string, SidebarSectionDto> Raw = new(StringComparer.Ordinal);
    internal readonly List<KeyValuePair<int, SidebarSectionDto>> Unknown = new();
    /// <summary>Raw top-bar tiles by item id, so an unknown member on a tile a newer build wrote survives too.</summary>
    internal readonly Dictionary<string, SidebarItemDto> RawTopBar = new(StringComparer.Ordinal);

    /// <summary>Unknown members on the curated payload itself; <c>WriteCurated</c> rebuilds it from scratch and would
    /// otherwise drop them.</summary>
    internal Dictionary<string, JsonElement>? CuratedExtra;

    /// <summary>Unknown members on the document envelope (siblings of <c>pins</c>/<c>v3</c>/<c>curated</c>/<c>topBar</c>).</summary>
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

/// <summary>The result of reading the curated payload: the typed layout plus the carry that makes the next write
/// lossless.</summary>
public readonly record struct SidebarCuratedRead(SidebarCustomLayout Layout, SidebarWireCarry Carry);

/// <summary>The one translation layer between the persisted DTOs and the live records. Every enum has an explicit
/// STRING form here (values are persisted — never rename one), and every unknown string degrades to the nearest safe
/// default rather than throwing.</summary>
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

    /// <summary>Parse a wire kind. False for a kind this build does not know — the caller preserves the raw section
    /// blob instead of dropping it.</summary>
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

    /// <summary>v2 — the action-binding target mode. An unknown mode string degrades to
    /// <see cref="SidebarActionTargetMode.None"/> without throwing: the row renders visible-but-disabled instead of
    /// taking the wrong target.</summary>
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

    // ── pin kind (SidebarPin.Kind, a SidebarEntryKind — folded from the deleted SidebarPinKind 2026-08-19) ────────────
    // NOT the same enum as SidebarEntityKind above (a curated section item's target-kind restriction, untouched by this
    // unification) — but close enough in meaning that the wire reuses the same lower-camel spellings for the five kinds
    // they share, plus "appRoute" for the bare-route family only a pin can address, and "track" for degrade-explicitly
    // symmetry only (SidebarPinId.IsPinnable refuses a track pin at creation, so writing it is unreachable).

    /// <summary>The preferred wire string for <see cref="SidebarPinDto.EntityKind"/>. A future <see cref="SidebarEntryKind"/>
    /// member left off this switch falls through to the same default as an unknown prefix ("appRoute"), which is exactly
    /// what the exhaustiveness test asserts on.</summary>
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

    /// <summary>Parse <see cref="SidebarPinDto.EntityKind"/>. False for an unrecognized string — the caller preserves
    /// or drops; there is no in-place default here to hide a future kind behind.</summary>
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

    /// <summary>FROZEN LEGACY TABLE — the exact byte numbering of the deleted <c>SidebarPinKind</c> enum
    /// (<c>Route=0, Playlist=1, Album=2, Artist=3, Show=4, Folder=5</c>), the only place that numbering may still exist.
    /// Never edit these values: their sole job is decoding a <see cref="SidebarPinDto.Kind"/> int written by a build
    /// that predates the SidebarPinKind/SidebarEntryKind unification.</summary>
    static readonly SidebarEntryKind[] LegacyPinKindTable =
    [
        SidebarEntryKind.AppRoute,   // 0 = the old SidebarPinKind.Route
        SidebarEntryKind.Playlist,   // 1
        SidebarEntryKind.Album,      // 2
        SidebarEntryKind.Artist,     // 3
        SidebarEntryKind.Show,       // 4
        SidebarEntryKind.Folder,     // 5
    ];

    /// <summary>Decode a legacy <see cref="SidebarPinDto.Kind"/> int. False for a value outside 0..5 — same
    /// preserve-or-drop contract as <see cref="TryParsePinKind"/>.</summary>
    public static bool TryLegacyPinKind(int legacy, out SidebarEntryKind kind)
    {
        if ((uint)legacy < (uint)LegacyPinKindTable.Length) { kind = LegacyPinKindTable[legacy]; return true; }
        kind = default;
        return false;
    }

    /// <summary>The inverse of <see cref="LegacyPinKindTable"/> — written alongside <see cref="PinKindName"/> so a
    /// downgraded build (which only reads the legacy int) still sees the pin. There is no legacy slot for
    /// <see cref="SidebarEntryKind.Track"/>; it writes the old Route slot only because some int must be written, never
    /// because a track pin can reach this arm.</summary>
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

    /// <summary>The entity-kind flag set as a stable string array — a flag set is never a number on the wire: adding a
    /// kind must not renumber the others.</summary>
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
                _ => SidebarEntityKinds.None,   // a kind this build doesn't know — ignored for the QUERY, kept in Extra
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
                // top-level opaque blob instead — the depth-1 model has nowhere else to put it.
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

    /// <summary>v2 — the action binding. An id-less payload yields null; an unknown target-mode string degrades to
    /// None. Never throws.</summary>
    static SidebarActionBinding? ReadAction(SidebarActionDto? a)
    {
        if (a is null) return null;
        string provider = a.ProviderId ?? "";
        string action = a.ActionId ?? "";
        if (provider.Length == 0 || action.Length == 0) return null;
        return new SidebarActionBinding(provider, action, ParseTargetMode(a.TargetMode),
            string.IsNullOrEmpty(a.TargetKey) ? null : a.TargetKey, SidebarJson.Own(a.Arguments));
    }

    /// <summary>Apply the present display fields over <see cref="SidebarDisplayOptions.Default"/>. Returns null when
    /// the payload carried no options at all, so <c>SidebarSectionSpec.Display == null</c> keeps its "== Default"
    /// meaning.</summary>
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

    /// <summary>Drop null/blank entries; an empty result is null. Order and duplicates are the reducer's business —
    /// reading stays faithful to the file.</summary>
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

    // ── the shell top bar band (envelope-level, one global list) ─────────────────────────────────────────────────────

    /// <summary>Project the persisted band onto the model. <c>null</c> (member absent) stays null — "never
    /// customized", which the model resolves to <c>SidebarCustomLayout.DefaultTopBar</c>. An empty array stays an
    /// empty list: "the user removed every shortcut" is a real state and must not silently restore Home.</summary>
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

    /// <summary>Serialize the band back. <c>null</c> ⇒ null (omitted by <c>WhenWritingNull</c>); an empty list ⇒
    /// <c>[]</c>, which must be written so the emptied state survives.</summary>
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
        // v2: an unknown member on the extension payload itself (a future ref field, not a config member — the config
        // is opaque and travels whole) survives the model hop the same way.
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

    /// <summary>v2 — always writes both ids and the schema version (a ref with a missing id is what "unbound" looks
    /// like on the wire, and losing it would silently delete the section's identity). The config is emitted verbatim;
    /// an empty <c>{}</c> is still written, because "the section has a config object" is itself information.</summary>
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

    /// <summary>Write only the fields that DIFFER from the default so the round trip is exact but a default-valued
    /// section costs no bytes. Returns null when nothing differs.</summary>
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
        // An all-default Display still has to survive as NON-null (the section explicitly carried options), so emit
        // the one cheapest discriminating field rather than collapsing it to null and changing the model on the way back.
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

// ── the version ladder ───────────────────────────────────────────────────────────────────────────────────────────────
// v1 → v2 (the extension-ready shape) is an IDENTITY step: v2 only adds optional members (a section's `extension`
// object, an item's `action` object, a query's `includeUris`/`excludeUris`) and one new kind/target string, so every v1
// document is already a valid v2 document. Existing layouts look and behave identically.
//
// Contract for every future arm:
//   • It runs IN MEMORY on a freshly deserialized document, BEFORE anything reads it. The file on disk is untouched
//     until the next ordinary commit — so an upgrade never destroys a document an older build might still open.
//   • It must be TOTAL: never throw, never return null. An unmappable member degrades to the new shape's default.
//   • It must PRESERVE unknown members (the [JsonExtensionData] carry) — never rebuild a DTO from scratch when
//     mutating it in place will do.
//   • It steps ONE version at a time (the while loop below), so v0→v3 is v1→v2 then v2→v3, each arm tested in isolation.
public static class SidebarLayoutMigrations
{
    /// <summary>Bring a loaded document up to <see cref="SidebarLayoutStore.CurrentVersion"/>. Never throws; the
    /// returned instance is the same object (mutated in place) so the extension-data carry survives.</summary>
    public static SidebarLayoutDocDto Upgrade(SidebarLayoutDocDto doc)
    {
        if (doc is null) return new SidebarLayoutDocDto { Version = SidebarLayoutStore.CurrentVersion };

        int guard = 0;
        while (doc.Version < SidebarLayoutStore.CurrentVersion && guard++ < 32)
        {
            switch (doc.Version)
            {
                case 1: MigrateV1ToV2(doc); doc.Version = 2; break;
                // case 2: MigrateV2ToV3(doc); doc.Version = 3; break;
                default:
                    // No arm for this version: stamp the current version rather than spinning. Reaching here means the
                    // ladder has a hole, which is a coding error, not a user-data problem — the document is still usable.
                    doc.Version = SidebarLayoutStore.CurrentVersion;
                    break;
            }
        }

        if (doc.Version > SidebarLayoutStore.CurrentVersion) doc.Version = SidebarLayoutStore.CurrentVersion;
        return doc;
    }

    /// <summary>v1 → v2: IDENTITY. Every v2 addition is an optional member plus two new enum strings ("extension",
    /// "action"), so a v1 document is already well-formed v2 and its absent members read as the model defaults.
    /// Deliberately a named no-op rather than a missing arm: the ladder stays explicit, the caller keeps the SAME
    /// instance (so the extension-data carry lives), and a real v2 → v3 arm has an obvious shape to copy.</summary>
    static void MigrateV1ToV2(SidebarLayoutDocDto doc)
    {
        _ = doc;
    }
}

// ── the built-in documents ───────────────────────────────────────────────────────────────────────────────────────────
// This owns ENVELOPES, not content. Every section list comes from SidebarTemplates — the single source of truth for
// what a template contains — and this only wraps one into a versioned SidebarLayoutDocDto.
//
// The Curated document is also the CORRUPT-FILE FALLBACK: on any non-None load fault the service loads
// CuratedLayout() in memory, leaves the unreadable file untouched and suppresses writes.
public static class SidebarLayoutDefaults
{
    /// <summary>The fresh-install / corrupt-fallback layout: the "Wavee Curated" template. A NEW instance with fresh
    /// section ids per call — never share one across two documents.</summary>
    public static SidebarCustomLayout CuratedLayout() => SidebarTemplates.Build(SidebarTemplates.Curated);

    /// <summary>A named template's layout; an unknown id yields Curated (SidebarTemplates.Build's own contract).</summary>
    public static SidebarCustomLayout LayoutOf(string? templateId) =>
        SidebarTemplates.Build(string.IsNullOrEmpty(templateId) ? SidebarTemplates.Curated : templateId!);

    /// <summary>An empty v1 envelope: no pins, no V3 overlay, no curated payload. What the very first commit of an
    /// install that has not opened the customizer writes.</summary>
    public static SidebarLayoutDocDto EmptyDocument() => new() { Version = SidebarLayoutStore.CurrentVersion };

    /// <summary>The fresh-install document: a v1 envelope carrying the Wavee Curated template as the curated payload.</summary>
    public static SidebarLayoutDocDto CuratedDocument() => Document(CuratedLayout());

    /// <summary>A v1 envelope carrying <paramref name="templateId"/>'s sections.</summary>
    public static SidebarLayoutDocDto DocumentOf(string? templateId) => Document(LayoutOf(templateId));

    /// <summary>Wrap an already-built layout in a v1 envelope. <c>UpdatedAtMs</c>/<c>AppVersion</c> are stamped by
    /// <see cref="SidebarLayoutStore.Commit"/>, not here — a default document that was never written must not claim to
    /// have been.</summary>
    public static SidebarLayoutDocDto Document(SidebarCustomLayout layout) => new()
    {
        Version = SidebarLayoutStore.CurrentVersion,
        Curated = SidebarLayoutWire.WriteCurated(layout, SidebarWireCarry.Empty),
        // The shell top-bar band is an ENVELOPE member, so wrapping a layout has to carry it too. A template layout
        // has none (null ⇒ the built-in default ⇒ the member is omitted), so this is a no-op for every built-in
        // document — it exists so wrapping an EDITED layout can never silently drop the user's band.
        TopBar = SidebarLayoutWire.WriteTopBar(layout.TopBar, SidebarWireCarry.Empty),
    };
}
