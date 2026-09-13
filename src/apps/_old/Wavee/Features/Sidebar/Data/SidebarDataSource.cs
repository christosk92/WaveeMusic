using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Wavee;

// The SIDEBAR DATA-SOURCE CONTRACT (M1, extension platform §"Data-source and contribution registry").
//
// ONE interface for first-party and (later) third-party row producers. `wavee.library` is registered through exactly the
// same call an external extension will use — that is the whole point of the milestone: first-party is literally the
// trusted extension "wavee", so M3's sandboxed host has nothing new to invent.
//
// ENGINE-FREE BY CONSTRUCTION (System + System.Text.Json + Wavee.Core only), like every other file in this folder: the
// folder is source-included by src/apps/Wavee.Tests (`Features\Sidebar\Data\*.cs`), so the tests drive the REAL contract,
// the REAL mappers and the REAL contribution resolution. Nothing here may reference Signal<T>, Element, Icons, Loc or Tok.
// That is also why HEALTH is a plain property + a plain `Changed` event rather than a Signal<T>: the concrete adapters
// (Data/Sources/*.cs — NOT source-included, the glob is one level deep) hold the engine-bound services and raise Changed
// on the UI thread.
//
// THREADING: UI thread only, unsynchronized — the same discipline as SidebarPreferences / LibraryStore's detail caches.
// An adapter that completes an async fetch MUST marshal through the binder's `post` before touching State or raising
// Changed.

/// <summary>What a source yields, so a renderer knows whether a row NAVIGATES (entity/route), PLAYS (track) or opens an
/// external surface (event). Declarative — the customizer shows it, the renderer switches on it.</summary>
public enum SidebarSourceItemType : byte { Entity = 0, Track = 1, Event = 2, Route = 3, Mixed = 4 }

/// <summary>Which of the sidebar's filter facets a source honours. A facet the source does NOT declare is not offered by
/// the customizer, rather than offered and silently ignored.</summary>
[Flags]
public enum SidebarSourceFilters : byte
{
    None = 0,
    Kinds = 1,               // SidebarEntityQuery.Kinds
    Qualifier = 2,           // By you / By Spotify / Mixed
    Search = 4,              // the library-only search text
    IncludeExcludeUris = 8,  // SidebarEntityQuery.IncludeUris / ExcludeUris ("only these artists")
}

/// <summary>Which sort modes a source can serve itself. A source that declares only <see cref="SourceOrder"/> is served
/// in the order it produced (a feed: newest first / soonest first) and the customizer hides the sort control.</summary>
[Flags]
public enum SidebarSourceSorts : byte
{
    None = 0,
    SourceOrder = 1,
    Recents = 2,
    RecentlyAdded = 4,
    Alphabetical = 8,
    Creator = 16,
    CustomOrder = 32,
    All = SourceOrder | Recents | RecentlyAdded | Alphabetical | Creator | CustomOrder,
}

/// <summary>How much a source can be asked for at once. <see cref="TopN"/> = the whole (small) list every time — a feed;
/// <see cref="Paged"/> = <see cref="SidebarSourceRequest.Page"/> is honoured (the shape M3's external lists must use).</summary>
public enum SidebarSourcePaging : byte { None = 0, TopN = 1, Paged = 2 }

/// <summary>The property-control families the customizer can generate from a schema. Deliberately semantic (never a raw
/// colour / pixel / duration — the third-party discipline in the platform doc).</summary>
public enum SidebarConfigFieldKind : byte { String = 0, Int = 1, Bool = 2, EntityUri = 3, Enum = 4, UriList = 5 }

/// <summary>One generated property control. <paramref name="LabelLocKey"/> is a loc KEY (never a literal), so an
/// extension's own property panel follows the UI culture like everything else.</summary>
public sealed record SidebarConfigField(
    string Key,
    SidebarConfigFieldKind Kind,
    string LabelLocKey,
    bool Required = false,
    string? DefaultJson = null,
    int Min = 0,
    int Max = 0,
    IReadOnlyList<string>? EnumValues = null);

/// <summary>A source's configuration schema. <paramref name="Version"/> is compared against
/// <c>SidebarExtensionRef.SchemaVersion</c>: a document authored by a NEWER schema than this build understands resolves
/// to <see cref="SidebarContributionAvailability.Incompatible"/> and keeps its spec — it is never rewritten or dropped.</summary>
public sealed record SidebarConfigSchema(int Version, IReadOnlyList<SidebarConfigField> Fields)
{
    public static readonly SidebarConfigSchema None = new(1, Array.Empty<SidebarConfigField>());

    public SidebarConfigField? Find(string key)
    {
        for (int i = 0; i < Fields.Count; i++)
            if (string.Equals(Fields[i].Key, key, StringComparison.Ordinal)) return Fields[i];
        return null;
    }
}

/// <summary>An OPAQUE section configuration (<c>SidebarExtensionRef.Config</c>) with typed, never-throwing readers. A
/// wrong-typed, absent or already-disposed element yields the fallback: a hand-edited (or future-build) document must
/// degrade to a default, never crash the sidebar.</summary>
public readonly record struct SidebarSourceConfig(JsonElement Value)
{
    public static readonly SidebarSourceConfig Empty = default;

    public bool IsObject
    {
        get { try { return Value.ValueKind == JsonValueKind.Object; } catch (Exception) { return false; } }
    }

    public string? Str(string key, string? fallback = null)
    {
        if (!TryProp(key, out var p)) return fallback;
        try { return p.ValueKind == JsonValueKind.String ? p.GetString() : fallback; }
        catch (Exception) { return fallback; }
    }

    public int Int(string key, int fallback = 0)
    {
        if (!TryProp(key, out var p)) return fallback;
        try { return p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v) ? v : fallback; }
        catch (Exception) { return fallback; }
    }

    public bool Bool(string key, bool fallback = false)
    {
        if (!TryProp(key, out var p)) return fallback;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    /// <summary>A string array property, appended into <paramref name="into"/> (caller-owned — no per-read allocation for
    /// the common absent case). Returns how many were appended.</summary>
    public int Strings(string key, List<string> into)
    {
        if (!TryProp(key, out var p)) return 0;
        try
        {
            if (p.ValueKind != JsonValueKind.Array) return 0;
            int n = 0;
            foreach (var item in p.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                if (item.GetString() is { Length: > 0 } s) { into.Add(s); n++; }
            }
            return n;
        }
        catch (Exception) { return 0; }
    }

    bool TryProp(string key, out JsonElement prop)
    {
        prop = default;
        try
        {
            if (Value.ValueKind != JsonValueKind.Object) return false;
            return Value.TryGetProperty(key, out prop);
        }
        catch (Exception) { return false; }   // a JsonElement whose document was disposed
    }
}

/// <summary>What the binder asks a source for. A POD passed by <c>in</c> — a fill is on the rebuild path, and the
/// rebuild path allocates nothing.</summary>
public readonly record struct SidebarSourceRequest(
    SidebarSourceConfig Config,
    int MaxItems = 0,            // 0 = the source's own natural bound
    string? Search = null,       // already trimmed/normalized by the binder (SidebarSearch.Normalize), never per row
    int Page = 0)                // Paged sources only
{
    public static readonly SidebarSourceRequest Default = new(SidebarSourceConfig.Empty);
}

/// <summary>Whether a section's contribution is being served LIVE, from the last-good snapshot, or not at all. The
/// non-Live-non-Cached values are exactly the platform doc's failure matrix; the planner turns them into ONE actionable
/// <c>SidebarRowKind.PromptRow</c> ("Manage extension") and the section KEEPS its spec.</summary>
public enum SidebarContributionAvailability : byte
{
    Live = 0,
    Cached = 1,         // the source failed; the binder replayed its last-good slice (M3's stale badge)
    Missing = 2,        // no such extension / contribution in the registry
    Disabled = 3,       // registered but turned off by the user or the host
    Incompatible = 4,   // the document's SchemaVersion is newer than this build's schema
}

/// <summary>
/// A registered sidebar row producer. Registered through <c>IWaveeExtensionRegistrar.RegisterDataSource</c> — the SAME
/// call an external extension uses.
///
/// <para><b>The one registry.</b> There is deliberately no <c>SidebarDataSourceRegistry</c> here: the action registry
/// (<c>WaveeExtensionRegistry</c>) is the single registry for every contribution kind, and the binder resolves sources
/// through it. This file owns only the CONTRACT and the pure resolution rules.</para>
/// </summary>
public interface ISidebarDataSource
{
    /// <summary>The namespaced stable id — <c>extensionId + "." + contributionId</c> (e.g. <c>wavee.library</c>,
    /// <c>wavee.artist.topTracks</c>). Built and parsed in ONE place: <see cref="SidebarContributions"/>.</summary>
    string Id { get; }

    /// <summary>The schema the customizer generates property controls from.</summary>
    SidebarConfigSchema ConfigSchema { get; }

    SidebarSourceItemType ItemType { get; }
    SidebarSourceFilters SupportedFilters { get; }
    SidebarSourceSorts SupportedSorts { get; }
    SidebarSourcePaging Paging { get; }

    /// <summary>The health signal, as a UI-thread property (see the threading note at the top of this file). The binder
    /// surfaces it verbatim as the planner's <see cref="SidebarSourceState"/>.</summary>
    SidebarSourceState State { get; }

    /// <summary>Why the source is not Ready, as a loc KEY (null when Ready or when there is nothing useful to say).</summary>
    string? StateDetailLocKey { get; }

    /// <summary>True when the degraded state is ACTIONABLE rather than empty — Concerts with no location is the canonical
    /// case. The planner then draws one <c>PromptRow</c> instead of an empty caption.</summary>
    bool NeedsPrompt { get; }

    /// <summary>Kick any warm/refresh this source needs. Idempotent and non-blocking; called by the binder on start and
    /// whenever a section's configuration changes. Must never throw.</summary>
    void EnsureFresh(in SidebarSourceRequest request);

    /// <summary>APPEND this source's current rows to <paramref name="into"/> and return how many were appended. Called on
    /// the rebuild path: no LINQ, no closures, no per-row allocation — and never a blocking wait (a source that has not
    /// resolved yet returns 0 with <see cref="State"/> == Pending).</summary>
    int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request);

    /// <summary>Raised on the UI thread after this source's rows or <see cref="State"/> changed. The binder subscribes
    /// once at start and rebuilds the projection.</summary>
    event Action? Changed;
}

/// <summary>Convenience base: the <see cref="ISidebarDataSource.Changed"/> plumbing, the health fields and sane declared
/// capabilities, so an adapter is a mapper plus one <c>Fill</c>. Engine-free like the interface.</summary>
public abstract class SidebarDataSourceBase : ISidebarDataSource
{
    protected SidebarDataSourceBase(string id) => Id = id;

    public string Id { get; }
    public virtual SidebarConfigSchema ConfigSchema => SidebarConfigSchema.None;
    public virtual SidebarSourceItemType ItemType => SidebarSourceItemType.Entity;
    public virtual SidebarSourceFilters SupportedFilters => SidebarSourceFilters.None;
    public virtual SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;
    public virtual SidebarSourcePaging Paging => SidebarSourcePaging.TopN;

    public SidebarSourceState State { get; protected set; } = SidebarSourceState.Ready;
    public string? StateDetailLocKey { get; protected set; }
    public bool NeedsPrompt { get; protected set; }

    public event Action? Changed;

    public virtual void EnsureFresh(in SidebarSourceRequest request) { }

    public abstract int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request);

    /// <summary>Publish a health change + notify. UI thread only. No-op notify when nothing moved, so a poll-shaped
    /// adapter cannot spin the binder.</summary>
    protected void SetHealth(SidebarSourceState state, string? detailLocKey = null, bool needsPrompt = false)
    {
        if (State == state && NeedsPrompt == needsPrompt
            && string.Equals(StateDetailLocKey, detailLocKey, StringComparison.Ordinal)) return;
        State = state;
        StateDetailLocKey = detailLocKey;
        NeedsPrompt = needsPrompt;
        Raise();
    }

    /// <summary>Publish a health change WITHOUT notifying — the only setter a <see cref="Fill"/> may use.
    ///
    /// <para>This is how a source configured differently per section reports per-section health honestly: the contribution
    /// resolver reads <see cref="State"/>/<see cref="NeedsPrompt"/> IMMEDIATELY after the <c>Fill</c> that produced the
    /// slice, so a source keyed by (say) artist uri can set the verdict for the row set it just produced. Raising Changed
    /// from inside a rebuild would instead re-enter the binder.</para></summary>
    protected void SetHealthQuiet(SidebarSourceState state, string? detailLocKey = null, bool needsPrompt = false)
    {
        State = state;
        StateDetailLocKey = detailLocKey;
        NeedsPrompt = needsPrompt;
    }

    /// <summary>Notify the binder that the ROWS changed (health unchanged). UI thread only, and NEVER from
    /// <see cref="Fill"/>.</summary>
    protected void Raise() => Changed?.Invoke();
}

/// <summary>The host that resolves a contribution id to a source. First-party today (<c>WaveeExtensionRegistry</c>);
/// M3's sandboxed extension host implements the same two lines. An interface (not a delegate) so the availability verdict
/// travels WITH the lookup — "registered but disabled" and "never registered" are different rows.</summary>
public interface ISidebarContributionHost
{
    /// <summary>Resolve <paramref name="sourceId"/>. A null result MUST come with a non-Live
    /// <paramref name="availability"/> explaining why.</summary>
    ISidebarDataSource? Resolve(string sourceId, out SidebarContributionAvailability availability);
}

/// <summary>The first-party contribution ids + the ONE place a source id is composed or split. New UI must never
/// <c>switch</c> on an extension id (the forward-compat guardrail) — it resolves through the host.</summary>
public static class SidebarContributions
{
    /// <summary>The trusted first-party extension id. First-party is literally an extension named "wavee".
    ///
    /// <para>SINGLE-OWNER NOTE: this MUST equal <c>WaveeExtensionKey.FirstPartyPublisher</c>, which is the platform-wide
    /// owner of the literal. It is repeated here rather than referenced because this folder is source-included by
    /// <c>src/apps/Wavee.Tests</c> and <c>Actions/Extensibility/</c> is not — see the file header. The one-line fix is to
    /// make that constant alias THIS one (Actions/ can see this type; the reverse is what the test build forbids).</para></summary>
    public const string WaveeExtensionId = "wavee";

    public const string Library = "wavee.library";
    public const string HistoryVisited = "wavee.history.visited";
    public const string HistoryPlayed = "wavee.history.played";
    public const string PlaylistTree = "wavee.playlistTree";
    public const string ArtistTopTracks = "wavee.artist.topTracks";
    public const string NewReleases = "wavee.newReleases";
    public const string Concerts = "wavee.concerts";
    public const string Queue = "wavee.queue";
    public const string NowPlaying = "wavee.nowPlaying";

    /// <summary>Every first-party source id, in registration order. The customizer's Extensions palette group reads it.</summary>
    public static readonly string[] FirstParty =
    [
        Library, HistoryVisited, HistoryPlayed, PlaylistTree, ArtistTopTracks,
        NewReleases, Concerts, Queue, NowPlaying,
    ];

    /// <summary><c>extensionId + "." + contributionId</c>. Empty when either half is missing — an unresolvable id, which
    /// the caller renders as <see cref="SidebarContributionAvailability.Missing"/>.
    ///
    /// <para>A <paramref name="contributionId"/> that is ALREADY fully qualified is taken as-is rather than
    /// double-prefixed, matching <c>WaveeExtensionKey.Compose</c>: a hand-edited or older document may legitimately have
    /// stored <c>"wavee.library"</c> in the contribution slot, and <c>"wavee.wavee.library"</c> would resolve to
    /// nothing.</para></summary>
    public static string SourceId(string? extensionId, string? contributionId)
    {
        if (string.IsNullOrEmpty(contributionId)) return "";
        if (string.IsNullOrEmpty(extensionId)) return "";
        if (contributionId!.Length > extensionId!.Length
            && contributionId[extensionId.Length] == '.'
            && contributionId.StartsWith(extensionId, StringComparison.Ordinal)) return contributionId;
        return extensionId + "." + contributionId;
    }

    /// <summary>The contribution half of a first-party source id ("library", "artist.topTracks", …).</summary>
    public static string ContributionOf(string sourceId)
    {
        int dot = sourceId.IndexOf('.');
        return dot < 0 || dot + 1 >= sourceId.Length ? "" : sourceId[(dot + 1)..];
    }

    public static bool IsFirstParty(string? sourceId)
    {
        if (sourceId is null) return false;
        for (int i = 0; i < FirstParty.Length; i++)
            if (string.Equals(FirstParty[i], sourceId, StringComparison.Ordinal)) return true;
        return false;
    }
}
