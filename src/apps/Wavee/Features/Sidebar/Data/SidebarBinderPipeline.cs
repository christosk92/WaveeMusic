using System;
using System.Collections.Generic;
using Wavee.Core;
using Wavee.Core.Catalog;
using Wavee.Core.Sidebar;

namespace Wavee;

/// <summary>Required-change C — the pure half of <c>SidebarProjectionBinder.RebindScope</c>'s account gate. A same-
/// account session merely CONFIRMING itself (<c>CatalogScope.ContextKnown</c> flipping false→true, e.g. a "which
/// account is this?" prompt resolving to the account the binder already assumed) must not tear down and re-acquire
/// the library query and every pin query — that used to run on ANY scope publication, including a bare
/// <c>ContextKnown</c> flip, which is expensive (a fresh fetch of the whole library + re-subscribing N pin queries)
/// for zero actual change in what data is being asked for. The three fields that DO identify a different backing
/// account/session are <c>Provider</c>, <c>ProviderAccount</c> and <c>StorageAccount</c>; <c>Locale</c>/<c>Market</c>/
/// <c>Catalogue</c>/<c>Tier</c>/<c>ExplicitFilter</c> reshape a query's RESULTS (the query definitions already read
/// them per-call) but never its IDENTITY, so a change restricted to those does not need a fresh handle either — only
/// the account-identity fields force one.</summary>
public static class ScopeRebindRules
{
    /// <summary>True when <paramref name="next"/> names a different backing account/session than
    /// <paramref name="previous"/> — the library query and every pin query must be disposed and re-acquired against
    /// it. False for an identical scope, and false for a scope that differs ONLY in <c>ContextKnown</c> (or any of
    /// the result-shaping fields) — those keep the existing handles.</summary>
    public static bool RequiresReacquire(CatalogScope previous, CatalogScope next)
        => !string.Equals(previous.Provider, next.Provider, StringComparison.Ordinal)
           || !string.Equals(previous.ProviderAccount, next.ProviderAccount, StringComparison.Ordinal)
           || !string.Equals(previous.StorageAccount, next.StorageAccount, StringComparison.Ordinal);
}

// The PURE half of the projection binder (F.7.5–F.7.10 + M1's contribution resolution).
//
// SidebarProjectionBinder (Features/Sidebar/SidebarProjectionBinder.cs) is the impure orchestrator: it holds the stores,
// the signals and the UI-thread marshaller. Everything it DECIDES lives here instead — the rebuild trigger fold, the
// filter/qualifier/search compaction, the sort + pins-first shaping, and the resolution of an Extension section to a row
// slice (including the missing/disabled/incompatible placeholder verdict and the last-good snapshot replay).
//
// Engine-free (System + Wavee.Core only), source-included by src/apps/Wavee.Tests — so SidebarProjectionBinderTests
// drives the REAL rebuild rules rather than a copy of them.
//
// ALLOCATION: every entry point takes caller-owned buffers and reuses them; no LINQ, no closures, index loops only.

/// <summary>
/// Everything a rebuild depends on, folded to one comparable value. The binder peeks these (never subscribes) and
/// rebuilds iff the fold moved, so a redundant pump render or an external <c>Sync()</c> costs one struct compare.
///
/// <para><b>LibraryEpoch</b> is a REFERENCE-identity fold of the <c>SidebarLibraryQuery</c> node's projected value (see
/// <see cref="LibraryEpochOf"/>), not its <c>QuerySnapshot.Revision</c> counter: <c>LibraryDefinition.Read</c>
/// (<c>CatalogQueryDefinitions.cs</c>) publishes a NEW <c>LibraryQuerySnapshot</c> instance every time it actually
/// re-projects, so instance identity is an exact content epoch — a rename inside a same-length list moves it, which a
/// Count-based epoch would miss, while a Revision-based epoch would ALSO move on changes the projection never sees
/// (an unrelated identity commit that only touches the query's Resources dictionary).</para>
/// </summary>
public readonly record struct SidebarBinderTriggers(
    int LibraryEpoch = 0,
    int PinsVersion = 0,
    int HistoryVersion = 0,
    int PlayLogRevision = 0,
    int LayoutVersion = 0,
    int FolderVersion = 0,
    int OrderVersion = 0,
    int CultureEpoch = 0,
    int V3State = 0,        // packed filter | qualifier | sort | desc | design
    int SearchHash = 0,
    int SourceEpoch = 0,    // bumped by the binder when any registered source raises Changed
    long PlaybackEpoch = 0) // queue revision + now-playing identity
{
    /// <summary>Pack the V3 view state (+ the active design) into one lane. Ints, not the enums, because that is how the
    /// preferences store them.</summary>
    public static int PackV3(int design, int filter, int qualifier, int sort, bool descending)
        => (design & 0xF) | ((filter & 0xFF) << 4) | ((qualifier & 0xFF) << 12)
         | ((sort & 0xFF) << 20) | (descending ? 1 << 28 : 0);

    /// <summary>The <see cref="LibraryEpoch"/> lane: VALUE identity, not <c>QuerySnapshot&lt;T&gt;.Revision</c>. A
    /// <c>QueryService.Node</c> bumps Revision on ANY <c>changed</c> — a Resources-dictionary delta from an unrelated
    /// identity commit included — but only replaces <c>Value</c> when the projected value actually differs (an
    /// activity/error-only pass reuses the exact same <c>LibraryQuerySnapshot</c> instance); folding Revision therefore
    /// forces a full sidebar rebuild for changes the projection never sees.
    ///
    /// <para>Folds <c>LibraryQuerySnapshot.GetHashCode()</c> — the record's own (structural once agent B lands: two
    /// snapshots with equal per-collection content fold identically even across DISTINCT instances, so an
    /// activity-only publication that reprojects a fresh-but-equal snapshot no longer moves this epoch either. Before
    /// that lands, the record's default field-wise hash is still strictly better than an identity hash: it already
    /// folds <c>LibraryStats</c> (a plain value record) structurally, and two snapshots that share a <c>Value</c>
    /// reference — the exact same-instance case the old identity hash existed for — still fold identically.</para></summary>
    public static int LibraryEpochOf(QuerySnapshot<LibraryQuerySnapshot>? snapshot)
        => snapshot is null ? 0 : HashCode.Combine(snapshot.Value.GetHashCode(), snapshot.Status);

    /// <summary>A 64-bit avalanche of every lane — the pump's <c>DepKey</c> and the binder's change gate. Deterministic:
    /// same lanes ⇒ same fold, so the gate can never depend on allocation addresses beyond the epochs above.</summary>
    public long Fold()
    {
        unchecked
        {
            ulong h = 1469598103934665603UL;
            h = Mix(h, (uint)LibraryEpoch);
            h = Mix(h, (uint)PinsVersion);
            h = Mix(h, (uint)HistoryVersion);
            h = Mix(h, (uint)PlayLogRevision);
            h = Mix(h, (uint)LayoutVersion);
            h = Mix(h, (uint)FolderVersion);
            h = Mix(h, (uint)OrderVersion);
            h = Mix(h, (uint)CultureEpoch);
            h = Mix(h, (uint)V3State);
            h = Mix(h, (uint)SearchHash);
            h = Mix(h, (uint)SourceEpoch);
            h = Mix(h, (uint)PlaybackEpoch);
            h = Mix(h, (uint)(PlaybackEpoch >> 32));
            return (long)h;
        }
    }

    static ulong Mix(ulong h, uint v)
    {
        unchecked
        {
            h ^= v;
            h *= 1099511628211UL;
            return h;
        }
    }
}

/// <summary>The Library-V3 view state a rebuild shapes the published entry list with. Ints are cast at the binder's read
/// site (the preferences store them as ints).</summary>
public readonly record struct SidebarV3Query(
    SidebarV3Filter Filter = SidebarV3Filter.All,
    SidebarV3Qualifier Qualifier = SidebarV3Qualifier.Any,
    SidebarV3Sort Sort = SidebarV3Sort.Recents,
    bool Descending = true,
    string? Search = null,
    bool QualifiersAvailable = false);

/// <summary>What one shaping pass produced: how many rows were published and how long the leading pin band is
/// (<c>SidebarEntries.PinCount</c>).</summary>
public readonly record struct SidebarEntriesShape(int Count, int PinCount);

public static class SidebarBinderPipeline
{
    /// <summary>
    /// Shape the unified projection into the list a V3/Classic surface renders: FILTER (kinds → qualifier → search), then
    /// SORT, then the pins-first partition — in that order, which is the one F.7.9 fixes.
    ///
    /// <para><paramref name="all"/> is the full source-order projection (every kind), so ONE
    /// <c>SidebarProjection.Build</c> serves both this list and the Curated planner's <c>Library</c> slice.
    /// <paramref name="into"/> and <paramref name="scratch"/> are caller-owned and reused; <paramref name="into"/> is
    /// cleared first.</para>
    ///
    /// <para>A persisted qualifier other than <c>Any</c> is treated as <c>Any</c> whenever the data does not support the
    /// chips (<c>QualifiersAvailable == false</c>) — a stale preference can never hide the whole list. A persisted
    /// <c>Custom</c> sort outside the Playlists filter falls back to Alphabetical FOR DISPLAY, leaving the preference
    /// untouched (<c>SidebarSort.Effective</c>).</para>
    /// </summary>
    public static SidebarEntriesShape Project(
        IReadOnlyList<SidebarLibraryEntry>? all,
        List<SidebarLibraryEntry> into,
        List<SidebarLibraryEntry> scratch,
        in SidebarV3Query query,
        IReadOnlyList<SidebarPin>? pins = null,
        IReadOnlyList<string>? customOrder = null)
    {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(scratch);
        into.Clear();
        if (all is null || all.Count == 0) return new SidebarEntriesShape(0, 0);

        var kinds = SidebarEntryKinds.From(query.Filter);
        for (int i = 0; i < all.Count; i++)
            if (SidebarEntryKinds.Has(kinds, all[i].Kind)) into.Add(all[i]);

        return Shape(into, scratch, in query, pins, customOrder);
    }

    /// <summary>
    /// The IN-PLACE half of <see cref="Project"/>, for a list that already holds exactly the KINDS the filter wants (the
    /// binder builds the projection with the filter's kind mask directly, so there is no copy at all): compact by
    /// qualifier + search, then sort, then partition pins to the front.
    /// </summary>
    public static SidebarEntriesShape Shape(
        List<SidebarLibraryEntry> list,
        List<SidebarLibraryEntry> scratch,
        in SidebarV3Query query,
        IReadOnlyList<SidebarPin>? pins = null,
        IReadOnlyList<string>? customOrder = null)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(scratch);

        string search = SidebarSearch.Normalize(query.Search);
        bool searching = search.Length > 0;
        byte qualifier = query.QualifiersAvailable ? (byte)query.Qualifier : (byte)0;

        if (searching || qualifier != 0)
        {
            int write = 0;
            for (int read = 0; read < list.Count; read++)
            {
                var e = list[read];
                // Searching FLATTENS: matching leaves only, no folder chrome (a folder is a container, not a result) —
                // the same rule SidebarRowPlanner applies to a PlaylistTree section.
                if (searching && e.Kind == SidebarEntryKind.Folder) continue;
                if (qualifier != 0 && (e.Kind != SidebarEntryKind.Playlist || !e.MatchesQualifier(qualifier))) continue;
                if (searching && !SidebarSearch.Matches(in e, search)) continue;
                list[write++] = e;
            }
            if (write < list.Count) list.RemoveRange(write, list.Count - write);
        }

        SidebarSort.Apply(list, SidebarSort.Effective(query.Sort, query.Filter), query.Descending, customOrder);
        int band = SidebarProjection.PinsFirst(list, pins, scratch);
        return new SidebarEntriesShape(list.Count, band);
    }

    /// <summary>
    /// Build the row for a pin the live projection does NOT know — an editorial/Spotify-owned playlist (or any other
    /// entity) never saved to the user's own library/rootlist, so it never appears in the binder's <c>_index</c> (built
    /// solely from <c>LibraryStore</c>'s rootlist tree + Albums/Artists/Shows). Root cause of the "blank cover + 0
    /// songs" defect: the OLD code stopped here and rendered the pin's bare display cache forever.
    ///
    /// <para>This still renders the pin's own offline display cache (F.5.4's offline-first contract — an unresolved
    /// pin must never disappear, and must render something the instant it is drawn), but OVERLAYS
    /// <paramref name="hydrated"/> — Cover / ChildCount / Creator resolved by the binder through the same catalog
    /// façade the detail page uses (<c>SidebarProjectionBinder.RequestPinHydration</c>) — once it lands. Pure: the
    /// binder is the only impure caller (it owns the async fetch + the per-pin cache), so this merge is what a headless
    /// test drives to pin the "art/count arrive once hydrated" contract without a network or an engine.</para>
    /// </summary>
    /// <summary>The V3 list is built from the LIBRARY (<c>SidebarProjection.Build</c> over the store's rootlist, albums,
    /// artists and shows), so a pin the library does not contain — the Liked Songs route pin, an editorial playlist never
    /// saved, a pinned page — is simply not in it, and <c>PinsFirst</c> cannot move what is absent: Classic showed those
    /// pins (its band reads the resolved pin rows directly) while every V3 lens silently dropped them. Append the unlisted
    /// pin rows the lens admits (<see cref="SidebarEntryKinds.Admits"/>), in pin order, skipping an id the list already
    /// carries; <c>PinsFirst</c> then partitions them into the band like any other pin. Returns how many were added.</summary>
    public static int AppendUnlistedPins(List<SidebarLibraryEntry> list, IReadOnlyList<SidebarLibraryEntry> unlisted,
                                         SidebarEntryKindMask kinds)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (unlisted is null || unlisted.Count == 0) return 0;
        int added = 0;
        for (int i = 0; i < unlisted.Count; i++)
        {
            var e = unlisted[i];
            if (e.Id.Length == 0 || !SidebarEntryKinds.Admits(kinds, in e) || ContainsId(list, e.Id)) continue;
            list.Add(e);
            added++;
        }
        return added;
    }

    static bool ContainsId(List<SidebarLibraryEntry> list, string id)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Id, id, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>A pin's live header card, PARTIALLY resolved fields included: a cover that landed before the name (or
    /// the reverse) must not be thrown away, because <see cref="ResolveUnlistedPin"/> already merges whichever of
    /// (name, cover, count, creator) resolved over the pin's base entry field-by-field — the bug this used to have
    /// (problem 2) was returning null, and therefore nothing at all, whenever the name specifically had not
    /// resolved, discarding a cover that HAD. Null only once nothing has resolved (name AND cover both unknown) —
    /// the base entry alone still renders in that case, per F.5.4's offline-first contract. Moved here (out of
    /// <c>SidebarProjectionBinder</c>, which is engine-bound and not source-included in Wavee.Tests) so the merge
    /// this feeds is testable end to end without the engine.</summary>
    public static SidebarLibraryEntry? PinEntry(SidebarEntryKind kind, string name, Image? cover, int count, string creator)
        => name.Length == 0 && cover is null ? null : new("", kind, "", name, creator, cover, null,
            ChildCount: count, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: kind == SidebarEntryKind.Artist, Flavor: SidebarPlaylistFlavor.None);

    public static SidebarLibraryEntry ResolveUnlistedPin(SidebarPin pin, int sourceOrder, SidebarLibraryEntry? current)
    {
        var baseEntry = new SidebarLibraryEntry(
            pin.Id, pin.Kind, pin.Uri, pin.Name, "", null, null,
            ChildCount: 0, AddedAtMs: pin.AddedAtMs, SortStamp: pin.AddedAtMs, LastVisitedTicksUtc: 0,
            SourceOrder: sourceOrder, Depth: 0, Circular: pin.Kind == SidebarEntryKind.Artist,
            Flavor: SidebarPlaylistFlavor.None)
        { IsPinned = true, FolderId = "", FolderName = "", FirstArtistName = "" };

        if (current is not { } h) return baseEntry;
        return baseEntry with
        {
            Name = h.Name.Length > 0 ? h.Name : baseEntry.Name,
            Cover = h.Cover ?? baseEntry.Cover,
            ChildCount = h.ChildCount,
            Creator = h.Creator.Length > 0 ? h.Creator : baseEntry.Creator,
        };
    }

    /// <summary>A pin whose entity IS in the caller's library index (found via <c>SidebarProjectionBinder._index</c>,
    /// the pin's own kind/uri already resolved through the ordinary library projection) still owes the SAME "an
    /// UNRESOLVED pin renders from its own display cache" contract <see cref="ResolveUnlistedPin"/> gives an
    /// UNLISTED pin — before this the listed branch used <paramref name="entry"/> completely as-is, so a playlist
    /// whose cover/child-count/subtitle had already resolved through the library's replica/mosaic path (which does
    /// not wait on the <c>PlaylistHeader</c> facet — see <c>CatalogReadView.Playlist</c>'s <c>MosaicCover</c>) but
    /// whose <c>PlaylistHeader</c> facet had not yet been demanded (e.g. its row briefly fell outside the pane's
    /// realized window, or landed before the header round-trip finished) rendered a correct mosaic/subtitle beside a
    /// permanently blank name: <paramref name="entry"/>.Name is empty and nothing here falls back to the pin's own
    /// cached <see cref="SidebarPin.Name"/>, unlike <see cref="ResolveUnlistedPin"/>'s baseEntry. This restores that
    /// fallback for the listed branch too — once the library entry's own name resolves it always wins.</summary>
    public static SidebarLibraryEntry ResolveListedPin(SidebarPin pin, int sourceOrder, SidebarLibraryEntry entry)
    {
        var display = entry.Name.Length > 0 ? entry : entry with { Name = pin.Name };
        return display with { IsPinned = true, SourceOrder = sourceOrder };
    }

    /// <summary>
    /// Resolve EVERY <c>SidebarSectionKind.Extension</c> section in the document (top level + one nesting level) into a
    /// row slice, appending their rows into the one shared <paramref name="entries"/> pool.
    ///
    /// <para>The planner stays PURE: the binder resolves contributions, the planner only reads the slice table. A section
    /// whose contribution is missing / disabled / schema-incompatible KEEPS its spec and gets a slice whose availability
    /// says why — the planner turns that into one actionable "Manage extension" <c>PromptRow</c>.</para>
    /// </summary>
    /// <param name="cache">The per-contribution last-good snapshot (M3's stale-badge seam). A source that fails after
    /// having served rows replays its snapshot as <see cref="SidebarContributionAvailability.Cached"/> rather than
    /// blanking a populated section.</param>
    public static void ResolveExtensions(
        SidebarCustomLayout? layout,
        ISidebarContributionHost? host,
        List<SidebarLibraryEntry> entries,
        SidebarExtensionSlices slices,
        SidebarContributionCache? cache = null,
        string? search = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(slices);
        entries.Clear();
        slices.Clear();
        if (layout is null) return;

        var sections = layout.Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (s.Kind == SidebarSectionKind.Extension) slices.Set(s.Id, Resolve(s, host, entries, cache, search));
            var kids = s.ChildList;
            for (int j = 0; j < kids.Count; j++)
            {
                var k = kids[j];
                if (k.Kind == SidebarSectionKind.Extension) slices.Set(k.Id, Resolve(k, host, entries, cache, search));
            }
        }
    }

    /// <summary>Resolve ONE extension section. Never throws: a contributed source that throws is reported as an Error
    /// slice (with its last-good snapshot replayed when there is one), because one bad extension may not take the sidebar
    /// down.</summary>
    public static SidebarSectionSlice Resolve(
        SidebarSectionSpec section,
        ISidebarContributionHost? host,
        List<SidebarLibraryEntry> entries,
        SidebarContributionCache? cache = null,
        string? search = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(entries);

        var xref = section.Extension;
        if (xref is null) return Unavailable(SidebarContributionAvailability.Missing);

        string sourceId = SidebarContributions.SourceId(xref.ExtensionId, xref.ContributionId);
        if (sourceId.Length == 0) return Unavailable(SidebarContributionAvailability.Missing);

        ISidebarDataSource? source = null;
        var availability = SidebarContributionAvailability.Missing;
        if (host is not null) source = host.Resolve(sourceId, out availability);
        if (source is null)
            return Unavailable(availability == SidebarContributionAvailability.Live
                ? SidebarContributionAvailability.Missing : availability);

        // A document authored against a NEWER config schema than this build knows: keep the section, say so, change
        // nothing. (The reverse — an older document against a newer schema — is the source's own defaulting problem.)
        if (xref.SchemaVersion > source.ConfigSchema.Version)
            return Unavailable(SidebarContributionAvailability.Incompatible);

        int start = entries.Count;
        var request = new SidebarSourceRequest(new SidebarSourceConfig(xref.Config), section.Opts.MaxItems, search);
        int count;
        var state = SidebarSourceState.Ready;
        bool prompt = false;
        try
        {
            // The section's config is how a source LEARNS what to fetch (which artist, which radius), so the warm kick
            // rides the same call. A source must never notify from here (SetHealthQuiet only) — see the base class.
            source.EnsureFresh(request);
            count = source.Fill(entries, request);
            if (count < 0) count = 0;
            if (entries.Count - start != count) count = Math.Max(0, entries.Count - start);
            state = source.State;
            prompt = source.NeedsPrompt;
        }
        catch (Exception)
        {
            if (entries.Count > start) entries.RemoveRange(start, entries.Count - start);   // no partial fill leaks
            count = 0;
            state = SidebarSourceState.Error;
        }

        if (count == 0 && state == SidebarSourceState.Error && cache is not null)
        {
            int replayed = cache.TryReplay(sourceId, entries);
            if (replayed > 0)
                return new SidebarSectionSlice(start, replayed, SidebarSourceState.Ready,
                                               SidebarContributionAvailability.Cached);
        }

        if (count > 0) cache?.Store(sourceId, entries, start, count);
        return new SidebarSectionSlice(start, count, state, SidebarContributionAvailability.Live, prompt);
    }

    // A slice that contributes no rows and renders the "Manage extension" prompt. State is Error, not Ready: the section
    // is not empty, it is unresolved — and a surface that reads state must not present it as "you have nothing here".
    static SidebarSectionSlice Unavailable(SidebarContributionAvailability availability)
        => new(0, 0, SidebarSourceState.Error, availability);
}

/// <summary>
/// The sidebar's contribution lookup: source id → source, plus a per-source enable flag. Filled by
/// <c>WaveeBuiltInDataSources.RegisterAll</c> with the SAME instances it registers into the platform's
/// <c>WaveeExtensionRegistry</c>, so the two can never drift.
///
/// <para>It exists as its own (engine-free, source-included) type for two reasons: the resolution rules are then unit
/// tested against the real host, and M3's sandboxed host can replace it wholesale by implementing
/// <see cref="ISidebarContributionHost"/> — the binder and the planner never learn which one they are talking to, and
/// nothing ever <c>switch</c>es on an extension id.</para>
/// </summary>
public sealed class SidebarDataSourceTable : ISidebarContributionHost
{
    readonly Dictionary<string, ISidebarDataSource> _sources = new(StringComparer.Ordinal);
    readonly HashSet<string> _disabled = new(StringComparer.Ordinal);

    public int Count => _sources.Count;

    /// <summary>Register (or replace) a source under its own <see cref="ISidebarDataSource.Id"/>.</summary>
    public void Add(ISidebarDataSource? source)
    {
        if (source is null || string.IsNullOrEmpty(source.Id)) return;
        _sources[source.Id] = source;
    }

    /// <summary>Turn a registered contribution off without unregistering it — the honest <c>Disabled</c> row (the section
    /// keeps its spec and says "Manage extension") rather than a section that silently vanishes.</summary>
    public void SetEnabled(string sourceId, bool enabled)
    {
        if (string.IsNullOrEmpty(sourceId)) return;
        if (enabled) _disabled.Remove(sourceId);
        else _disabled.Add(sourceId);
    }

    public bool IsEnabled(string sourceId) => !_disabled.Contains(sourceId);

    public ISidebarDataSource? Resolve(string sourceId, out SidebarContributionAvailability availability)
    {
        if (!_sources.TryGetValue(sourceId, out var source))
        {
            availability = SidebarContributionAvailability.Missing;
            return null;
        }
        if (_disabled.Contains(sourceId))
        {
            availability = SidebarContributionAvailability.Disabled;
            return null;
        }
        availability = SidebarContributionAvailability.Live;
        return source;
    }

    /// <summary>Every registered source, for the binder's lifecycle attach + the customizer's palette. Order is
    /// unspecified — <c>SidebarContributions.FirstParty</c> is the ordered first-party list.</summary>
    public IEnumerable<ISidebarDataSource> All => _sources.Values;

    public SidebarSourceState StateOf(string sourceId)
        => _sources.TryGetValue(sourceId, out var s) ? s.State : SidebarSourceState.Error;
}

/// <summary>sectionId → slice, reused across rebuilds. The table the planner reads through
/// <c>SidebarProjectionInput.ExtensionSlices</c>.</summary>
public sealed class SidebarExtensionSlices : ISidebarSectionSlices
{
    readonly Dictionary<string, SidebarSectionSlice> _slices = new(StringComparer.Ordinal);

    public int Count => _slices.Count;

    public void Clear() => _slices.Clear();

    public void Set(string sectionId, SidebarSectionSlice slice)
    {
        if (!string.IsNullOrEmpty(sectionId)) _slices[sectionId] = slice;
    }

    public bool TryGet(string sectionId, out SidebarSectionSlice slice) => _slices.TryGetValue(sectionId, out slice);

    /// <summary>The availability a surface shows as a badge / placeholder reason. <c>Missing</c> for an unknown section id
    /// (nothing was resolved for it), which is the same row an unresolved contribution draws.</summary>
    public SidebarContributionAvailability AvailabilityOf(string sectionId)
        => _slices.TryGetValue(sectionId, out var s) ? s.Availability : SidebarContributionAvailability.Missing;
}

/// <summary>
/// The per-contribution LAST-GOOD snapshot — the cached-snapshot seam M3's host needs for its stale badge, implemented
/// now so nothing has to be re-plumbed later. First-party sources are always live, so in M1 this only fires when a
/// contributed source that HAD rows starts failing.
///
/// Bounded by construction: one list per contribution id, each capped at <see cref="PerSourceCap"/> rows.
/// </summary>
public sealed class SidebarContributionCache
{
    /// <summary>Snapshot cap per contribution. A sidebar section is a top-N surface; a stale replay of more than this is
    /// not a UI, it is a leak.</summary>
    public const int PerSourceCap = 200;

    readonly Dictionary<string, List<SidebarLibraryEntry>> _snapshots = new(StringComparer.Ordinal);

    public int Count => _snapshots.Count;

    /// <summary>Whether a contribution has a replayable snapshot (the "stale, not empty" test).</summary>
    public bool Has(string sourceId) => _snapshots.TryGetValue(sourceId, out var s) && s.Count > 0;

    /// <summary>Copy <paramref name="count"/> rows starting at <paramref name="start"/> into this contribution's snapshot,
    /// replacing whatever was there. Reuses the stored list, so a steady stream of successful fills allocates nothing.</summary>
    public void Store(string sourceId, IReadOnlyList<SidebarLibraryEntry> entries, int start, int count)
    {
        if (string.IsNullOrEmpty(sourceId) || count <= 0) return;
        if (start < 0 || start + count > entries.Count) return;
        if (!_snapshots.TryGetValue(sourceId, out var snap)) _snapshots[sourceId] = snap = new List<SidebarLibraryEntry>(count);
        snap.Clear();
        int n = count < PerSourceCap ? count : PerSourceCap;
        for (int i = 0; i < n; i++) snap.Add(entries[start + i]);
    }

    /// <summary>Append this contribution's snapshot to <paramref name="into"/> and return how many rows were replayed
    /// (0 when there is nothing cached).</summary>
    public int TryReplay(string sourceId, List<SidebarLibraryEntry> into)
    {
        if (string.IsNullOrEmpty(sourceId) || !_snapshots.TryGetValue(sourceId, out var snap) || snap.Count == 0) return 0;
        for (int i = 0; i < snap.Count; i++) into.Add(snap[i]);
        return snap.Count;
    }

    /// <summary>Drop a contribution's snapshot (its extension was uninstalled / its config changed shape).</summary>
    public void Forget(string sourceId) => _snapshots.Remove(sourceId);

    public void Clear() => _snapshots.Clear();
}
