using System.Text.Json;

namespace Wavee.Core.Sidebar;

// The Mode C ("Wavee Curated") custom-sidebar PAYLOAD MODEL — the document the customizer edits, the reducer rewrites,
// the row planner projects and the versioned sidebar-layout.json carries. Deliberately in Wavee.Core, not Wavee:
//   * framework-neutral (no FluentGpu type in any shape — glyph *names* live here, glyph *codepoints* app-side),
//   * Wavee.Tests exercises it without dragging FluentGpu.WindowsApi in,
//   * it sits beside the existing Library/Sidebar.cs IA types (PlaylistSummary / PlaylistNode / LibraryStats).
//
// NO POLYMORPHIC JSON. A section is a single closed record discriminated by a Kind byte with per-kind-optional fields —
// never an abstract hierarchy with [JsonDerivedType]. The document must survive AOT source-gen serialization with zero
// reflection risk, must round-trip a section kind a FUTURE build introduced (unknown kinds deserialize into the record
// and are preserved on the next save — a newer-build layout opened by an older build must not lose data), and the
// property panel wants one uniform shape to edit. Commands (SidebarLayoutCommands.cs) *are* a record hierarchy — they
// are in-memory only and never serialized.

/// <summary>What a section IS. Values are persisted — append only, never renumber.
/// An unknown (future) value must round-trip untouched and render as a skipped section.</summary>
public enum SidebarSectionKind : byte
{
    Pinned              = 0,  // the shared, unlimited pin store, in pin order
    JumpBackIn          = 1,  // recency from HistoryStore (visited) or the play log (played), deduped by uri
    CollectionShortcuts = 2,  // fixed app destinations: liked / albums / artists / podcasts / local
    PlaylistTree        = 3,  // the folder-aware rootlist tree (recursive PlaylistFolder)
    EntityList          = 4,  // a dynamic query over SidebarLibraryEntry (the V3 projection)
    StaticLinks         = 5,  // hand-picked app routes (home / search / history / settings / api-console)
    CustomGroup         = 6,  // a user-named group of hand-picked entities/routes/tracks; ONE level of nesting
    Header              = 7,  // a text-only group label (no rows)
    Divider             = 8,  // a 1px rule + 8-DIP lead-in (the existing Section(rule:true) chrome)
    EntityEmbed         = 9,  // ONE spotlighted entity (playlist/album/artist/show) as a hero card with play
    NewReleases         = 10, // top-N new releases from followed artists (ISidebarNewReleasesSource adapter)
    Concerts            = 11, // top-N upcoming concerts near the user (ISidebarConcertsSource adapter)
    Extension           = 12, // LAYOUT V2: a CONTRIBUTED section. SidebarExtensionRef names the extension + contribution
                              // and carries its opaque config; the rows come from the resolved data source, never from a
                              // hand-authored Items list. First-party dynamic feeds (artist top tracks, queue) are
                              // Extension sections with ExtensionId "wavee" — the contribution path proves itself.
}

public enum SidebarDensity : byte { Compact = 0, Cozy = 1, Comfortable = 2 }

public enum SidebarPresentation : byte { List = 0, Grid = 1 }

/// <summary>What an item points at. Route/Entity NAVIGATE on click; Track PLAYS on click (tracks have no detail route);
/// Action INVOKES its <see cref="SidebarItemSpec.Action"/> binding through the action registry (LAYOUT V2).
/// Track items are legal in CustomGroup/StaticLinks item lists; the PIN store still excludes tracks (locked decision 4
/// is unchanged).</summary>
public enum SidebarItemTarget : byte { Route = 0, Entity = 1, Track = 2, Action = 3 }

/// <summary>How a bound action gets its target at invoke time. <c>FixedEntity</c>/<c>FixedTrack</c> read
/// <see cref="SidebarActionBinding.TargetKey"/>; <c>NowPlaying</c>/<c>ActiveRoute</c> read the live app state; <c>None</c>
/// is a context-free action. Persisted as a STRING on the wire — append only, never renumber.</summary>
public enum SidebarActionTargetMode : byte { None, FixedEntity, FixedTrack, NowPlaying, ActiveRoute }

/// <summary>Null-safe <see cref="JsonElement"/> plumbing for the two v2 payload records. A <c>default(JsonElement)</c>
/// has <see cref="JsonValueKind.Undefined"/> and THROWS from <c>GetRawText()</c>/<c>Clone()</c>, so every path through
/// the model goes through here. Nothing in this class throws.</summary>
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

    /// <summary>The element re-emitted COMPACTLY — the canonical identity of a config/arguments payload, and "" for
    /// Undefined.
    /// <para><c>GetRawText()</c> is deliberately NOT used here: it returns the ORIGINAL source span, so a config read back
    /// out of the (indented, user-inspectable) document would never string-compare equal to the one that wrote it, and
    /// every load would look like an edit. Re-emitting through a writer normalizes insignificant whitespace and is a fixed
    /// point across further write/parse cycles.</para>
    /// <para>Property ORDER is significant: a reorder is a real change to the document, and treating it as one keeps this
    /// cheap and keeps <c>GetHashCode</c> honest.</para></summary>
    public static string Canonical(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Undefined) return "";
        var buffer = Write(e);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string? Canonical(JsonElement? e) => e is { } v ? Canonical(v) : null;

    /// <summary>UTF-8 byte length of the compact serialized form — what the 64 KiB config cap counts. (The document on
    /// disk is written indented, so its bytes are larger; the cap is on the PAYLOAD, measured the same way everywhere.)</summary>
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

/// <summary>LAYOUT V2 — which extension contribution a <see cref="SidebarSectionKind.Extension"/> section renders, plus
/// the contribution's own opaque configuration.
/// <para><paramref name="SchemaVersion"/> is the CONFIG's schema version as declared by the contributing source, so a
/// source can migrate its own config without a document migration. <paramref name="Config"/> is never interpreted here:
/// an unknown extension id or an unknown config member round-trips untouched (the persistence carry policy), and runtime
/// extension state and secrets NEVER enter this record.</para></summary>
public sealed record SidebarExtensionRef(string ExtensionId, string ContributionId, int SchemaVersion, JsonElement Config)
{
    /// <summary>The per-section configuration cap. Enforced by the reducer (<c>SetExtensionConfig</c> ⇒
    /// <see cref="SidebarRejectReason.ConfigTooLarge"/>) and re-checked at save time against a hand-edited document.</summary>
    public const int MaxConfigBytes = 64 * 1024;

    /// <summary>A ref with an empty <c>{}</c> config — what the palette adds before the inspector edits anything.</summary>
    public static SidebarExtensionRef For(string extensionId, string contributionId, int schemaVersion = 1)
        => new(extensionId, contributionId, schemaVersion, SidebarJson.EmptyObject);

    public int ConfigByteCount => SidebarJson.ByteCount(Config);

    /// <summary>False for a ref that cannot address a contribution (either id blank) — the reducer rejects those.</summary>
    public bool IsWellFormed => !string.IsNullOrEmpty(ExtensionId) && !string.IsNullOrEmpty(ContributionId);

    /// <summary>The registry lookup key ("wavee/artist.topTracks"), so no caller invents its own concatenation.</summary>
    public string ContributionKey => ExtensionId + "/" + ContributionId;

    // JsonElement has NO content equality (the synthesized comparison hits ValueType.Equals, i.e. the backing document by
    // reference), so a config that round-tripped through JSON would never compare equal to the one it came from. Both
    // members are declared by hand and compare the CANONICAL JSON instead (SidebarJson.Same).
    public bool Equals(SidebarExtensionRef? other)
        => other is not null &&
           string.Equals(ExtensionId, other.ExtensionId, StringComparison.Ordinal) &&
           string.Equals(ContributionId, other.ContributionId, StringComparison.Ordinal) &&
           SchemaVersion == other.SchemaVersion &&
           SidebarJson.Same(Config, other.Config);

    // The config is deliberately NOT hashed: equality REQUIRES both ids and the schema version to match, so this stays a
    // valid hash (equal values hash equally) without re-serializing a payload on every lookup.
    public override int GetHashCode() => HashCode.Combine(
        ExtensionId is null ? 0 : StringComparer.Ordinal.GetHashCode(ExtensionId),
        ContributionId is null ? 0 : StringComparer.Ordinal.GetHashCode(ContributionId),
        SchemaVersion);
}

/// <summary>LAYOUT V2 — a persisted action binding for a <see cref="SidebarItemTarget.Action"/> item.
/// <para><paramref name="ProviderId"/>/<paramref name="ActionId"/> are the registry's namespaced stable key (the
/// first-party provider is literally <c>"wavee"</c>, e.g. <c>wavee</c> + <c>play</c>). The layout NEVER stores an
/// <c>ActionId</c> enum value — the enum stays internal to the app and descriptors wrap it, so a binding written by a
/// newer build (or an extension that is currently missing) round-trips untouched and simply renders disabled.</para></summary>
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
    /// silently dropped (the platform doc's unavailable-target rule).</summary>
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

/// <summary>The entity family of an item — needed to render a correct placeholder row (and pick the right art shape:
/// circular for Artist, rounded-square otherwise) BEFORE the entity resolves.</summary>
public enum SidebarEntityKind : byte
{
    None = 0, Playlist = 1, Album = 2, Artist = 3, Show = 4, PlaylistFolder = 5, Track = 6,
}

[Flags]
public enum SidebarEntityKinds : byte
{
    None = 0, Playlists = 1, Albums = 2, Artists = 4, Shows = 8, All = Playlists | Albums | Artists | Shows,
}

/// <summary>Shared with Mode B: the same five sort modes the V3 list offers.
/// CustomOrder is only meaningful when Kinds == Playlists (locked decision 10).</summary>
public enum SidebarSortMode : byte { Recents = 0, RecentlyAdded = 1, Alphabetical = 2, Creator = 3, CustomOrder = 4 }

/// <summary>Playlist qualifier chips. Any = no qualifier filter.</summary>
public enum SidebarPlaylistQualifier : byte { Any = 0, ByYou = 1, BySpotify = 2, Mixed = 3 }

/// <summary>Where a JumpBackIn section's recency comes from: Visited = navigation history (HistoryStore, today's
/// behaviour); Played = the local play log (PlayLogStore — actual playback).</summary>
public enum SidebarRecentsSource : byte { Visited = 0, Played = 1 }

/// <summary>How an authored section occupies the pane when its live source contributes no rows. Default resolves
/// through <see cref="SidebarSectionKinds.EmptyBehaviorFor"/> so existing documents inherit the product default without
/// a migration. Values are persisted; append only.</summary>
public enum SidebarEmptyBehavior : byte
{
    Default = 0,
    HideBody = 1,
    CompactHint = 2,
    ActionCard = 3,
}

/// <summary>The property-panel edit surface for display options: one command carries (field, int value), so the panel
/// needs no per-field command type. Bools encode 0/1.</summary>
public enum SidebarDisplayField : byte
{
    Density = 0, Presentation = 1, Artwork = 2, Subtitles = 3, CountBadges = 4,
    CollapsedByDefault = 5, ShowInRail = 6, MaxItems = 7, GridColumns = 8,
    InlineControls = 9,   // EntityList only: a compact filter/sort row rendered atop the section
    PlayButton = 10,      // EntityEmbed only: the hover/focus play affordance on the hero card
    RecentsSource = 11,   // JumpBackIn only: Visited | Played (encodes the SidebarRecentsSource byte)
    EmptyBehavior = 12,   // content sections: Default | HideBody | CompactHint | ActionCard
}

/// <summary>Per-section presentation. Every field is user-editable from the property panel; every field has a default
/// that makes a freshly-added section immediately usable.</summary>
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

    /// <summary>Icon-only rows for <see cref="SidebarSectionKind.CollectionShortcuts"/>: no artwork, no subtitle, and
    /// the quiet count badge ON — a fixed collection destination (liked / albums / artists / podcasts) is exactly the
    /// row for which a count exists, and <c>AllowsDisplayField(CollectionShortcuts, CountBadges)</c> lets the user turn
    /// it off.</summary>
    public static readonly SidebarDisplayOptions Shortcuts =
        new(Density: SidebarDensity.Cozy, Artwork: false, Subtitles: false, CountBadges: true);

    /// <summary>The same icon-only rows for <see cref="SidebarSectionKind.StaticLinks"/> — with counts OFF.
    ///
    /// <para>DEFECT 8. StaticLinks used to share <see cref="Shortcuts"/> verbatim, so every freshly added Links section
    /// was seeded <c>CountBadges = true</c> while <c>AllowsDisplayField(StaticLinks, CountBadges)</c> says false: the
    /// property panel never showed the field and <c>SetDisplayOption</c> rejected it as a NoChange, so the flag was a
    /// default the user could neither see nor undo — yet the renderer honours it (<c>SidebarPaneSlot.CountBadge</c>
    /// keys off the ROUTE, not the section kind), so a hand-picked "liked" link silently grew a number a hand-picked
    /// "home" link could not. One preset per kind is the fix; the two presets differ in exactly one field.</para></summary>
    public static readonly SidebarDisplayOptions Links = Shortcuts with { CountBadges = false };

    /// <summary>Artwork rows with a creator/type subtitle (PlaylistTree / EntityList / Pinned).</summary>
    public static readonly SidebarDisplayOptions Entities = new();
}

/// <summary>One hand-placed row. Display-only overrides NEVER mutate the entity: LabelOverride is a local alias, not a
/// rename (a Spotify rename goes through ContainerActions.RenamePlaylist and is a different action entirely).
///
/// MISSING-ENTITY RETENTION: FallbackTitle/FallbackImageUrl are stamped every time the item successfully resolves
/// against the live projection. When the entity later disappears (unfollowed elsewhere, offline cold cache, account
/// switch), the row still renders with its last-known title + art, dimmed, with an "Unavailable" affordance — the item
/// is NEVER auto-removed. Only an explicit RemoveItem command deletes it.</summary>
public sealed record SidebarItemSpec(
    string Id,                        // "itm_" + 8 lowercase hex; stable for the item's life; never reused
    SidebarItemTarget Target,
    string Key,                       // Route -> a route name ("liked","albums","home",...); Entity/Track -> a spotify: uri
    SidebarEntityKind EntityKind = SidebarEntityKind.None,
    string? LabelOverride = null,      // trimmed; "" is normalized to null by the reducer
    string? IconOverride = null,       // an Icons.* NAME (e.g. "Heart"), validated against SidebarIconNames.Allowed
    string? FallbackTitle = null,
    string? FallbackImageUrl = null,
    bool Hidden = false,
    SidebarActionBinding? Action = null)   // LAYOUT V2: set for Target == Action; null for a navigate/play item
{
    /// <summary>An action row whose binding is present and addressable. A bound-but-unresolvable row still RENDERS
    /// (disabled, with a reason) — this only says whether the invoke path can run.</summary>
    public bool HasRunnableAction => Target == SidebarItemTarget.Action && Action is { IsResolvable: true };
}

/// <summary>A dynamic section: "the library, filtered and sorted". This is the SAME shape Mode B's filter/sort bar
/// produces, so a Custom EntityList section and the V3 list share one projection implementation.
/// Qualifier applies only when Kinds == Playlists; Sort == CustomOrder is only honoured when Kinds == Playlists
/// (the reducer rewrites an illegal combination to the nearest legal one rather than rejecting the edit).
/// <para>LAYOUT V2 adds IncludeUris/ExcludeUris: an allow/deny set over entity uris, so "only these artists" is a QUERY,
/// not a hand-maintained item list. Semantics: a non-empty IncludeUris restricts the result to exactly those uris (still
/// filtered by Kinds and still sorted); ExcludeUris drops uris from whatever remains (exclude wins at projection time).
/// Both normalize to null when empty — the reducer never stores an empty list.</para></summary>
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

    /// <summary>The effective query for a PlaylistTree whose persisted Query is null: playlist leaves in the rootlist's
    /// authored order. Null stays the compact wire representation; this value exists so reducers, comparers and editors
    /// can reason about that meaning without accidentally substituting <see cref="Default"/> (Recents).</summary>
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

    // The two uri sets are IReadOnlyList members, which a synthesized record Equals compares by REFERENCE — so a query
    // that round-tripped through JSON would never equal the one it came from, and the reducer's NoChange detection (and
    // SidebarLayoutCompare) would report phantom edits. Both members are therefore declared by hand.
    public bool Equals(SidebarEntityQuery? other)
        => other is not null && Kinds == other.Kinds && Sort == other.Sort && Descending == other.Descending &&
           Qualifier == other.Qualifier &&
           SameUris(IncludeUris, other.IncludeUris) && SameUris(ExcludeUris, other.ExcludeUris);

    // Counts only, deliberately: the hash must never distinguish two values Equals calls equal, and it stays O(1).
    public override int GetHashCode() => HashCode.Combine(
        (byte)Kinds, (byte)Sort, Descending, (byte)Qualifier, IncludeUris?.Count ?? 0, ExcludeUris?.Count ?? 0);
}

public sealed record SidebarSectionSpec(
    string Id,                                       // "sec_" + 8 lowercase hex; never reused within a document
    SidebarSectionKind Kind,
    string? Title = null,                            // a USER title (rename); null = use TitleLocKey/kind default
    string? TitleLocKey = null,                      // set for template-authored titles so they follow the UI culture;
                                                     // RenameSection sets Title and CLEARS TitleLocKey
    bool Hidden = false,                             // authored "off" — contributes no rows and no rail tiles
    bool Collapsed = false,                          // the LIVE collapse state (persisted with the doc, per decision 3)
    SidebarDisplayOptions? Display = null,           // null == SidebarDisplayOptions.Default (keeps JSON small)
    IReadOnlyList<SidebarItemSpec>? Items = null,    // Pinned overrides / StaticLinks / CustomGroup members
    SidebarEntityQuery? Query = null,                // EntityList / PlaylistTree; null tree query = rootlist source order
    IReadOnlyList<SidebarSectionSpec>? Children = null, // CustomGroup only; depth 1 — a child may not have Children
    SidebarExtensionRef? Extension = null)           // LAYOUT V2: Extension only — which contribution renders here
{
    public SidebarDisplayOptions Opts => Display ?? SidebarDisplayOptions.Default;
    public IReadOnlyList<SidebarItemSpec> ItemList => Items ?? Array.Empty<SidebarItemSpec>();
    public IReadOnlyList<SidebarSectionSpec> ChildList => Children ?? Array.Empty<SidebarSectionSpec>();

    /// <summary>True for a kind this build does not know — it renders as nothing and round-trips untouched.</summary>
    public bool IsUnknownKind => !SidebarSectionKinds.IsKnown(Kind);

    /// <summary>A contributed section (LAYOUT V2). Its rows come from the resolved data source, not from ItemList.</summary>
    public bool IsExtension => Kind == SidebarSectionKind.Extension;

    /// <summary>An Extension section whose ref is missing or unaddressable — a hand-edited or half-migrated document.
    /// It keeps its spec and renders the "Manage extension" placeholder row; it is never auto-removed.</summary>
    public bool IsUnboundExtension => IsExtension && Extension is not { IsWellFormed: true };
}

/// <summary>The payload the Foundation document envelope carries for Mode C.
/// <para><paramref name="TopBar"/> is the SHELL TOP BAR's customizable shortcut band — ONE global list, not a per-design
/// one, and deliberately part of THIS record even though it renders nowhere near the sidebar pane: undo is a pre-image
/// snapshot of the whole <see cref="SidebarCustomLayout"/> (<c>SidebarUndo</c>), so a top-bar list living OUTSIDE it would
/// be invisible to undo/redo, to the reducer's rejection contract and to <c>SidebarLayoutCompare</c>. Null means "never
/// customized" and resolves to <see cref="DefaultTopBar"/> through <see cref="EffectiveTopBar"/>; an EMPTY list means the
/// user emptied the band on purpose (Home is genuinely removable) and renders nothing. Templates are sidebar-section
/// presets, so <c>ApplyTemplate</c>/<c>ResetLayout</c> PRESERVE it (see the reducer).</para></summary>
public sealed record SidebarCustomLayout(
    string TemplateId,                               // the template this layout was seeded from — drives "Reset to template"
    IReadOnlyList<SidebarSectionSpec> Sections,
    IReadOnlyList<SidebarItemSpec>? TopBar = null)   // null == never customized == DefaultTopBar; [] == emptied on purpose
{
    public static readonly SidebarCustomLayout Empty = new(SidebarTemplates.Blank, Array.Empty<SidebarSectionSpec>());

    /// <summary>The built-in top-bar band: the Home route shortcut, which is exactly what the shell hard-coded before the
    /// band became customizable. A FIXED item id (never minted) so the band is stable across reads — a fresh id per call
    /// would break remove-by-id, the per-tile menu keys and every equality check.</summary>
    public static readonly IReadOnlyList<SidebarItemSpec> DefaultTopBar = new SidebarItemSpec[]
    {
        new(SidebarIds.TopBarHomeItem, SidebarItemTarget.Route, "home", IconOverride: "Home"),
    };

    /// <summary>What the shell actually renders: the authored band, or the built-in default when it was never customized.
    /// The ONE place the null ⇒ default rule lives — no call site may re-derive it.</summary>
    public IReadOnlyList<SidebarItemSpec> EffectiveTopBar => TopBar ?? DefaultTopBar;

    /// <summary>Top-level + child sections. The reducer's cap (SidebarLayoutReducer.MaxSections) counts this.</summary>
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

    /// <summary>Locates a section: parent == null means the section is top-level at Index; Index == -1 means not found.</summary>
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

/// <summary>The glyph NAME whitelist. Lives in Core (not app-side with the codepoint map) because the reducer validates
/// SidebarItemSpec.IconOverride against it and Wavee.Core may not reference FluentGpu.Controls.Icons. The app-side
/// <c>SidebarIcons</c> maps these names to real glyphs and re-exports this list as the icon-picker order.</summary>
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

/// <summary>Per-kind facts the reducer, the templates, the palette and the property panel all need in ONE place, so a
/// per-kind rule is never restated (the drift the ownership discipline exists to catch).</summary>
public static class SidebarSectionKinds
{
    /// <summary>The highest kind THIS build understands. A greater value round-trips untouched and renders as nothing.</summary>
    public const byte MaxKnown = (byte)SidebarSectionKind.Extension;

    public static bool IsKnown(SidebarSectionKind kind) => (byte)kind <= MaxKnown;

    /// <summary>The kind's default section title loc key (null for Divider, which has no title). JumpBackIn's default
    /// follows its recents source: a "played" section reads "Recently played", a "visited" one "Jump back in".</summary>
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
        // A contributed section's REAL title comes from the contribution manifest, which Wavee.Core cannot see; this is
        // the neutral fallback the customizer shows until the registry resolves the ref (or when it cannot).
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

    /// <summary>The Display preset a freshly-added section of this kind is seeded with (§C3.3 AddSection).</summary>
    public static SidebarDisplayOptions DefaultDisplay(SidebarSectionKind kind) => kind switch
    {
        SidebarSectionKind.CollectionShortcuts => SidebarDisplayOptions.Shortcuts,
        // Defect 8: StaticLinks forbids CountBadges below, so its seed must not carry it — see
        // SidebarDisplayOptions.Links for the whole story.
        SidebarSectionKind.StaticLinks => SidebarDisplayOptions.Links,
        // "top-N" feeds ship with their spec'd N so a freshly added section is immediately sane.
        SidebarSectionKind.NewReleases => SidebarDisplayOptions.Entities with { MaxItems = 4 },
        SidebarSectionKind.Concerts => SidebarDisplayOptions.Entities with { MaxItems = 3 },
        // A contribution is a feed too: ship a bounded default so a queue / top-tracks section cannot flood the pane
        // before the inspector's schema-generated controls are touched. MaxItems is one of the four fields it exposes.
        SidebarSectionKind.Extension => SidebarDisplayOptions.Entities with { MaxItems = 10 },
        _ => SidebarDisplayOptions.Entities,
    };

    /// <summary>Resolve a section's effective empty treatment. Actionable source states always win when the author left
    /// the field at Default; an explicit authored choice remains authoritative.</summary>
    public static SidebarEmptyBehavior EmptyBehaviorFor(SidebarSectionKind kind,
        SidebarEmptyBehavior authored = SidebarEmptyBehavior.Default, bool actionable = false)
    {
        if (authored != SidebarEmptyBehavior.Default) return authored;
        if (actionable) return SidebarEmptyBehavior.ActionCard;
        return kind switch
        {
            SidebarSectionKind.Pinned => SidebarEmptyBehavior.CompactHint,
            // R3.1.6 (user decision): a dynamic feed that resolved to nothing STAYS VISIBLE with a quiet hint. HideBody
            // was the old default and it read as a bug — the section header remained while its body silently vanished, so
            // "Recently played" looked broken rather than empty. CompactHint is a 32-DIP 11f tertiary line that names the
            // state per kind ("Play something and it'll show up here"), which is both honest and self-explaining. An
            // explicit authored choice still wins above (this switch is only reached at Default).
            SidebarSectionKind.JumpBackIn or SidebarSectionKind.EntityList or SidebarSectionKind.NewReleases
                => SidebarEmptyBehavior.CompactHint,
            SidebarSectionKind.Concerts or SidebarSectionKind.Extension => SidebarEmptyBehavior.ActionCard,
            _ => SidebarEmptyBehavior.CompactHint,
        };
    }

    /// <summary>Kinds whose Items list is meaningful. Pinned accepts items as an OVERRIDE side-table (alias / icon /
    /// hidden for a pin), not as the pin list itself — the pin set and order live in SidebarPreferences (decision 4).</summary>
    public static bool AcceptsItems(SidebarSectionKind kind) => kind switch
    {
        SidebarSectionKind.Pinned => true,               // override side-table
        SidebarSectionKind.CollectionShortcuts => true,
        SidebarSectionKind.StaticLinks => true,
        SidebarSectionKind.CustomGroup => true,
        SidebarSectionKind.EntityEmbed => true,          // exactly one
        SidebarSectionKind.Extension => false,           // rows come from the contribution, never from a hand-list
        _ => false,
    };

    /// <summary>Kinds whose <see cref="SidebarSectionSpec.Query"/> is a LIBRARY query — the only ones for which the
    /// include/exclude uri sets mean anything. A query (and its uri sets) that survives on any other kind is a
    /// hand-edited or half-migrated document; the reducer strips the uri sets when it next touches the section.</summary>
    public static bool SupportsLibraryQuery(SidebarSectionKind kind)
        => kind is SidebarSectionKind.EntityList or SidebarSectionKind.PlaylistTree;

    /// <summary>The query a section MEANS when its compact document form carries null. PlaylistTree deliberately differs
    /// from EntityList: a null tree preserves the backend rootlist order instead of silently becoming Recents.</summary>
    public static SidebarEntityQuery EffectiveQuery(SidebarSectionKind kind, SidebarEntityQuery? query)
        => query ?? (kind == SidebarSectionKind.PlaylistTree
            ? SidebarEntityQuery.PlaylistTreeSourceOrder
            : SidebarEntityQuery.Default);

    /// <summary>Kinds that cannot exist without a <see cref="SidebarExtensionRef"/> (AddSection rejects
    /// <see cref="SidebarRejectReason.ExtensionRefMissing"/> without one).</summary>
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

            // A contributed section owns its own presentation: everything else the inspector shows for it is GENERATED
            // from the source's config schema (and edited through SetExtensionConfig), so the shared display surface is
            // deliberately just the four host-owned fields.
            SidebarSectionKind.Extension => field is SidebarDisplayField.Density
                or SidebarDisplayField.CollapsedByDefault or SidebarDisplayField.ShowInRail
                or SidebarDisplayField.MaxItems,

            _ => true,   // Pinned / EntityList / CustomGroup: everything applies
        };
    }

    /// <summary>A section may only nest inside a CustomGroup, and a CustomGroup may never nest (depth 1, hard).</summary>
    public static bool IsNestable(SidebarSectionKind kind) => kind != SidebarSectionKind.CustomGroup;

    /// <summary>Kinds whose CONTENT and ORDER live in a shared store outside the document, so the section spec is a VIEW
    /// of that store rather than a container of its own rows: <c>Pinned</c> (the pin store in
    /// <c>SidebarPreferences.Pins</c> — its Items are an override side-table, never the pin list) and
    /// <c>PlaylistTree</c> (the backend rootlist, plus V3's local order overlay).
    ///
    /// <para>DEFECT 9 — this is why <c>DuplicateSection</c> refuses them. Two Pinned sections would render the same
    /// store twice AND both commit reorders into it, so dragging in the copy would silently reshuffle the original;
    /// two PlaylistTrees would do the same to the rootlist order. A clone of a store-backed section is not a second
    /// list, it is a second WRITER onto one list, which no amount of fresh ids can separate. Refusing is the minimal
    /// correct semantics: the alternative — "de-target" the copy — would need a per-section store binding the wire has
    /// no room for.</para></summary>
    public static bool IsStoreBacked(SidebarSectionKind kind)
        => kind is SidebarSectionKind.Pinned or SidebarSectionKind.PlaylistTree;
}

/// <summary>Id minting. "sec_"/"itm_" + 8 lowercase hex; uniqueness within a document is enforced by the reducer
/// (SidebarLayoutReducer.FreshSectionId / FreshItemId), which re-rolls on the (astronomically rare) collision.</summary>
public static class SidebarIds
{
    public const string SectionPrefix = "sec_";
    public const string ItemPrefix = "itm_";

    /// <summary>The SENTINEL section id that addresses <see cref="SidebarCustomLayout.TopBar"/> from the three item-scoped
    /// commands (<c>SetItemLabel</c> / <c>SetItemIcon</c> / <c>SetItemAction</c>), so the customizer's existing per-item
    /// property controls edit a top-bar tile with no second command family. It can never collide with a real section id:
    /// every minted section id starts with <see cref="SectionPrefix"/>. Structural edits use the dedicated
    /// <c>AddTopBarItem</c>/<c>MoveTopBarItem</c>/<c>RemoveTopBarItem</c> commands instead — the band is a flat list with
    /// its own cap, and reusing <c>AddItem</c>/<c>MoveItem</c> would drag section-kind rules (AcceptsItems, EntityEmbed's
    /// single-item arm, the Pinned override prune) onto something that is not a section.</summary>
    public const string TopBarSection = "topbar";

    /// <summary>The stable id of the built-in default top-bar Home shortcut (<see cref="SidebarCustomLayout.DefaultTopBar"/>).</summary>
    public const string TopBarHomeItem = ItemPrefix + "00000001";

    public static bool IsTopBar(string? sectionId)
        => string.Equals(sectionId, TopBarSection, StringComparison.Ordinal);

    public static string NewSection() => SectionPrefix + Hex8();
    public static string NewItem() => ItemPrefix + Hex8();

    static string Hex8() => Random.Shared.NextInt64(0L, 0x1_0000_0000L).ToString("x8",
        System.Globalization.CultureInfo.InvariantCulture);
}
