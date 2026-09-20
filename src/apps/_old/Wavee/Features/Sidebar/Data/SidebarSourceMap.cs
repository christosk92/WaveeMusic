using System;
using System.Collections.Generic;
using System.Text;
using Wavee.Core;

namespace Wavee;

// The PURE mappers every first-party data-source adapter is built out of: domain record → SidebarLibraryEntry, plus the
// service-health → SidebarSourceState translation.
//
// WHY THEY LIVE HERE AND NOT IN THE ADAPTERS: the adapters (Data/Sources/*.cs) hold engine-bound services (LibraryStore,
// PlaybackBridge, the switchable Spotify services) and are therefore untestable without a window. Everything that can be
// WRONG — the id/route join, the sort stamp, the artist join, the offline/pending/error verdict — is here instead, in the
// source-included, engine-free half, so SidebarDataSourceTests drives the real rules.
//
// ALLOCATION: every mapper appends into a caller-owned list and allocates no string (an entry's Name/Creator/Id are
// existing strings or, for the joined-artist case, the ONE shared builder below — the same discipline as
// SidebarProjection).

/// <summary>
/// The engine-free shape of one "recently played" row: the CONTEXT the user pressed play on (album / playlist / artist /
/// show / the Liked-Songs route), or the track itself when a play had no context.
///
/// <para>Why it exists rather than using <c>PlayLogStore.PlayLogContext</c> directly: the store is engine-bound (it owns a
/// <c>Signal</c> and a debounced disk write) and this whole folder is source-included by the test assembly. The binder maps
/// the store's rows onto this POD — a per-rebuild copy of at most a few tens of rows — and every DECISION about them stays
/// here, where the tests can reach it.</para>
/// </summary>
public readonly record struct SidebarPlayedContext(string Uri, SidebarEntryKind Kind, long PlayedAtMs)
{
    /// <summary>True when this is a bare track play (no container to open) and must render as a track row.</summary>
    public bool IsTrack => Kind == SidebarEntryKind.Track;
}

public static class SidebarSourceMap
{
    // UI-thread only, like SidebarProjection's own join buffer.
    static readonly StringBuilder s_join = new(64);

    /// <summary>A service feed's state → the planner's source state. <c>Offline</c> maps to <b>Ready</b> on purpose: an
    /// offline feed is EMPTY, not broken, and an empty feed must render its empty caption rather than a permanent
    /// skeleton (locked behaviour: "null/offline ⇒ Empty state, never throw").</summary>
    public static SidebarSourceState FromFeedState(NotificationFeedState state) => state switch
    {
        NotificationFeedState.Idle or NotificationFeedState.Loading => SidebarSourceState.Pending,
        NotificationFeedState.Error => SidebarSourceState.Error,
        _ => SidebarSourceState.Ready,   // Populated / Empty / Offline
    };

    // ── tracks (queue, now playing, artist top tracks) ────────────────────────────────────────────────────────────────

    /// <summary>A track row. <c>Kind == Track</c>: it PLAYS on activation and has no detail route (<c>RouteKey</c> is
    /// null) and no pin (<c>SidebarPinId.FromEntry</c> refuses it) — locked decision 4 keeps tracks unpinnable.
    /// <paramref name="order"/> is the position in its feed and doubles as the tiebreak, so a queue never reshuffles.</summary>
    public static SidebarLibraryEntry FromTrack(Track t, int order, long stampMs = 0) =>
        new(t.Uri, SidebarEntryKind.Track, t.Uri, t.Title, JoinArtists(t.Artists),
            t.Image, null,
            ChildCount: 0, AddedAtMs: 0,
            SortStamp: stampMs,
            LastVisitedTicksUtc: 0,
            SourceOrder: order, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        {
            FolderId = "", FolderName = "",
            FirstArtistName = t.Artists is { Count: > 0 } ? t.Artists[0].Name : "",
        };

    /// <summary>Append up to <paramref name="max"/> tracks. Deduped by uri (a queue legitimately repeats a track, but the
    /// sidebar's row key must stay unique or the reconciler collapses the rows).</summary>
    public static int Tracks(IReadOnlyList<Track>? tracks, List<SidebarLibraryEntry> into, int max)
    {
        if (tracks is null || tracks.Count == 0 || max <= 0) return 0;
        int n = 0;
        for (int i = 0; i < tracks.Count && n < max; i++)
        {
            var t = tracks[i];
            if (t.Uri.Length == 0 || ContainsId(into, t.Uri)) continue;
            into.Add(FromTrack(t, n));
            n++;
        }
        return n;
    }

    // ── recently PLAYED (PlayLogStore) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The play log's context-first rows, resolved against the library projection. A context the projection
    /// knows becomes its real entry (art, creator, count) stamped with the PLAY time; one it does not know is still
    /// emitted — with an EMPTY <c>Name</c>, which is the surface's "unavailable, render dimmed from the uri" signal.
    /// Dropping it instead would silently hide most plays, because editorial playlists are not in your library.</summary>
    /// <param name="contexts">Newest-first, already deduped (<c>PlayLogStore.RecentContexts</c>, mapped onto the
    /// engine-free <see cref="SidebarPlayedContext"/> by the binder).</param>
    /// <param name="byId">The projection's id/uri → entry index (see <see cref="SidebarSourceIndex"/>).</param>
    public static int Played(IReadOnlyList<SidebarPlayedContext>? contexts, SidebarSourceIndex byId,
                             List<SidebarLibraryEntry> into, int max)
    {
        if (contexts is null || contexts.Count == 0 || max <= 0) return 0;
        int n = 0;
        for (int i = 0; i < contexts.Count && n < max; i++)
        {
            var c = contexts[i];
            if (c.Uri.Length == 0) continue;

            if (c.IsTrack)
            {
                // A bare track play: no container to open, so it is a Track row keyed by the track uri.
                if (ContainsId(into, c.Uri)) continue;
                into.Add(new SidebarLibraryEntry(
                    c.Uri, SidebarEntryKind.Track, c.Uri, "", "", null, null,
                    ChildCount: 0, AddedAtMs: 0, SortStamp: c.PlayedAtMs, LastVisitedTicksUtc: 0,
                    SourceOrder: n, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
                { FolderId = "", FolderName = "", FirstArtistName = "" });
                n++;
                continue;
            }

            string? id = SidebarPinId.FromUri(c.Uri);
            if (id is null || ContainsId(into, id)) continue;   // a context shape this build cannot open — never a crash

            if (byId.TryGet(id, out var known))
            {
                // Recency for a PLAYED feed is the play time, not the visit time — overwrite the stamp so the feed's own
                // order is total and independent of navigation history.
                into.Add(known with { SortStamp = c.PlayedAtMs, SourceOrder = n });
                n++;
                continue;
            }

            into.Add(new SidebarLibraryEntry(
                id, c.Kind, c.Uri, "", "", null, null,
                ChildCount: 0, AddedAtMs: 0, SortStamp: c.PlayedAtMs, LastVisitedTicksUtc: 0,
                SourceOrder: n, Depth: 0, Circular: c.Kind == SidebarEntryKind.Artist,
                Flavor: SidebarPlaylistFlavor.None)
            { FolderId = "", FolderName = "", FirstArtistName = "" });
            n++;
        }
        return n;
    }

    // ── recently VISITED (HistoryStore) ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The navigation log's newest-first distinct route keys, resolved against the projection. Generic accessors
    /// keep <c>HistoryEntry</c> (engine-bound) out of this layer — the <see cref="SidebarRecency.Build{T}"/> precedent.
    /// Pass STATIC lambdas. An app route that is not an entity (home / search / settings) resolves to a
    /// <see cref="SidebarLibraryEntry.ForRoute"/> row, whose label the surface takes from <c>ShellNav.Dest</c>.</summary>
    /// <param name="entriesOldestFirst">HistoryStore's own order.</param>
    public static int Visited<T>(IReadOnlyList<T>? entriesOldestFirst, Func<T, string> keyOf, Func<T, long> ticksUtcOf,
                                 SidebarSourceIndex byId, List<SidebarLibraryEntry> into, int max)
    {
        if (entriesOldestFirst is null || entriesOldestFirst.Count == 0 || max <= 0) return 0;
        int n = 0;
        for (int i = entriesOldestFirst.Count - 1; i >= 0 && n < max; i--)
        {
            string key = keyOf(entriesOldestFirst[i]);
            if (string.IsNullOrEmpty(key) || ContainsId(into, key)) continue;   // newest wins — we walk backwards
            long ticks = ticksUtcOf(entriesOldestFirst[i]);

            if (byId.TryGet(key, out var known)) into.Add(known with { LastVisitedTicksUtc = ticks, SourceOrder = n });
            else into.Add(SidebarLibraryEntry.ForRoute(key, "", n, ticks));
            n++;
        }
        return n;
    }

    // ── new releases (What's New) ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Followed-artist new releases, newest first. An album release becomes an <c>album:</c> entry, an episode a
    /// <c>show:</c>-family entry — both real navigable ids, so the row opens the normal detail route and is pinnable
    /// exactly like the library's own copy would be.</summary>
    public static int NewReleases(IReadOnlyList<NewReleaseNotification>? items, SidebarSourceIndex byId,
                                  List<SidebarLibraryEntry> into, int max)
    {
        if (items is null || items.Count == 0 || max <= 0) return 0;
        int n = 0;
        for (int i = 0; i < items.Count && n < max; i++)
        {
            var r = items[i];
            string? id = SidebarPinId.FromUri(r.Uri);
            if (id is null || ContainsId(into, id)) continue;

            if (byId.TryGet(id, out var known))
            {
                into.Add(known with { SortStamp = r.Timestamp, SourceOrder = n });
                n++;
                continue;
            }

            var kind = r.Kind == NewReleaseKind.Episode ? SidebarEntryKind.Show : SidebarEntryKind.Album;
            into.Add(new SidebarLibraryEntry(
                id, kind, r.Uri, r.Name, r.CreatorName,
                string.IsNullOrEmpty(r.ImageUrl) ? null : new Image(r.ImageUrl), null,
                ChildCount: 0, AddedAtMs: 0, SortStamp: r.Timestamp, LastVisitedTicksUtc: 0,
                SourceOrder: n, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
            { FolderId = "", FolderName = "", FirstArtistName = "" });
            n++;
        }
        return n;
    }

    // ── concerts ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One upcoming event. Modelled exactly as <c>SidebarProjectionInput.Concerts</c> documents it: Name = the
    /// event title, Creator = the venue, SortStamp = the event's epoch-ms. <c>Kind == AppRoute</c> with the concert
    /// DETAIL ROUTE as its id, so the row navigates and pins through the ordinary durable-route seam.</summary>
    /// <param name="routeKey">The caller passes <c>ConcertRoutes.Detail(uri)</c>; the prefix is owned there, not here.</param>
    public static SidebarLibraryEntry FromEvent(string routeKey, string? title, string? venue, long whenMs,
                                                Image? image, int order)
        => new(routeKey, SidebarEntryKind.AppRoute, "", title ?? "", venue ?? "",
               image, null, ChildCount: 0, AddedAtMs: 0, SortStamp: whenMs, LastVisitedTicksUtc: 0,
               SourceOrder: order, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { FolderId = "", FolderName = "", FirstArtistName = "" };

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Linear duplicate check. O(n²) on purpose: every caller is a top-N feed (tens of rows at most), so a
    /// HashSet per rebuild would allocate more than it saves.</summary>
    static bool ContainsId(List<SidebarLibraryEntry> list, string id)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Id, id, StringComparison.Ordinal)) return true;
        return false;
    }

    // "A, B, C" capped at three, then "…" (SidebarProjection's own join contract, kept identical so a queue row and a
    // library row read the same). One artist returns the source string with no allocation.
    static string JoinArtists(IReadOnlyList<ArtistRef>? artists)
    {
        if (artists is null || artists.Count == 0) return "";
        if (artists.Count == 1) return artists[0].Name;
        s_join.Clear();
        int n = artists.Count < 3 ? artists.Count : 3;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) s_join.Append(", ");
            s_join.Append(artists[i].Name);
        }
        if (artists.Count > n) s_join.Append('…');
        return s_join.ToString();
    }
}

/// <summary>
/// id/uri → projected entry, built ONCE per rebuild off the unified projection and shared by every feed adapter (and by
/// the planner as <c>SidebarProjectionInput.ByUri</c>). A class, not a dictionary alias, so the map is REUSED across
/// rebuilds — <see cref="Rebuild"/> clears and refills the same storage.
///
/// Both keys live in one map on purpose: entry ids (<c>pl:spotify:playlist:…</c>) and bare uris
/// (<c>spotify:playlist:…</c>) are disjoint namespaces, and a hand-placed item is keyed by uri while a feed row is keyed
/// by id — one lookup serves both without a second dictionary.
/// </summary>
public sealed class SidebarSourceIndex
{
    /// <summary>The shared empty index (a headless test, a source with no projection to join against).</summary>
    public static readonly SidebarSourceIndex Empty = new();

    readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);
    IReadOnlyList<SidebarLibraryEntry> _entries = Array.Empty<SidebarLibraryEntry>();

    public int Count => _byKey.Count;

    /// <summary>Point the index at a freshly built projection. The list is ALIASED (not copied), so it must stay alive and
    /// unmodified until the next rebuild — exactly the binder's buffer lifetime.</summary>
    public void Rebuild(IReadOnlyList<SidebarLibraryEntry> entries)
    {
        _entries = entries;
        _byKey.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Id.Length > 0) _byKey.TryAdd(e.Id, i);
            if (e.Uri.Length > 0) _byKey.TryAdd(e.Uri, i);
        }
    }

    public bool TryGet(string? key, out SidebarLibraryEntry entry)
    {
        if (key is { Length: > 0 } && _byKey.TryGetValue(key, out int i) && (uint)i < (uint)_entries.Count)
        {
            entry = _entries[i];
            return true;
        }
        entry = default;
        return false;
    }

    /// <summary>The <c>IReadOnlyDictionary&lt;string, SidebarLibraryEntry&gt;</c> face the planner's <c>ByUri</c> wants,
    /// without materialising a second map. Allocated once per binder, not per rebuild.</summary>
    public IReadOnlyDictionary<string, SidebarLibraryEntry> AsLookup() => _view ??= new View(this);
    View? _view;

    sealed class View(SidebarSourceIndex owner) : IReadOnlyDictionary<string, SidebarLibraryEntry>
    {
        public bool TryGetValue(string key, out SidebarLibraryEntry value) => owner.TryGet(key, out value);
        public bool ContainsKey(string key) => owner._byKey.ContainsKey(key);
        public SidebarLibraryEntry this[string key] =>
            owner.TryGet(key, out var v) ? v : throw new KeyNotFoundException(key);
        public int Count => owner._byKey.Count;
        public IEnumerable<string> Keys => owner._byKey.Keys;

        public IEnumerable<SidebarLibraryEntry> Values
        {
            get { foreach (var kv in owner._byKey) yield return owner._entries[kv.Value]; }
        }

        public IEnumerator<KeyValuePair<string, SidebarLibraryEntry>> GetEnumerator()
        {
            foreach (var kv in owner._byKey)
                yield return new KeyValuePair<string, SidebarLibraryEntry>(kv.Key, owner._entries[kv.Value]);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
