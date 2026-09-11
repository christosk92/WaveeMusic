using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Backend.Playlists;
using Wavee.Core;

namespace Wavee.Backend;

// Effective library projection, published after durability by LibraryReplicaCoordinator.
// CatalogRepository owns metadata.

public enum SyncState { Confirmed, Pending, Failed }

public readonly record struct StoreChange(string Uri, bool IsBulk = false, CollectionKind? Kind = null)   // struct → no heap alloc per Bump, no boxing through SimpleSubject<StoreChange>
{
    public static readonly StoreChange Bulk = new("", true);   // one signal for a bulk load; subscribers re-read
}

/// <summary>One rootlist row in the queryable spine: a playlist uri or a start/end-group marker (Kind 0=item, 1=start, 2=end).</summary>
/// <summary>One rootlist row. <paramref name="AddedAtMs"/> is the row's server ADD timestamp (playlist4
/// <c>ItemAttributes.timestamp</c>, unix ms; 0 = not captured yet): a folder RENAME has to re-send the marker's
/// ORIGINAL create timestamp, so it has to survive the round trip through the store.</summary>
public readonly record struct RootlistEntry(int Position, int Kind, string Uri, string? GroupName, int Depth, long AddedAtMs = 0);

/// <summary>One library-set member with its server add timestamp (unix ms; 0 = unknown) — the Liked-songs/collections
/// default order (added-date descending) reads this; <see cref="IStore.SavedUris"/> stays the unordered fast path.</summary>
public readonly record struct SavedItem(string Uri, long AddedAtMs);

/// <summary>One user-attached LOCAL video override: "when this playable plays, show THIS file instead of whatever video
/// the source would serve". Keyed by the exact playable uri (any namespace — track, episode, a future local file), so the
/// override system is source-agnostic like the rest of the playable seam. The file is LINKED, never copied: <see
/// cref="Path"/> is an absolute path and a missing file is a fall-through at play time, not a broken record.
/// <para><see cref="Id"/> is the first 16 hex chars of SHA-256 over the case-folded normalized path — the stable identity
/// the video source key (<c>"local:video:" + Id</c>) is built from, which is what every surface/host keys its remount on.
/// <see cref="SizeBytes"/>/<see cref="MTimeUnix"/> are STALENESS HINTS only (multi-GB files are never hashed);
/// <see cref="DurationMs"/> is 0 until the media engine reports the file's real duration.</para></summary>
public readonly record struct VideoOverride(
    string Uri, string Path, string Id, long DurationMs, long SizeBytes, long MTimeUnix, long AddedAtUnix)
{
    /// <summary>The store <see cref="IStore.Changes"/> sentinel a roster-level change (any attach/replace/remove) bumps,
    /// alongside the per-uri bump — so a "what have I attached?" view can subscribe without watching every uri.</summary>
    public const string ChangeKey = "video-overrides";

    /// <summary>The <c>PopOutVideoSource.Key</c> namespace prefix an override resolves to. Never a routed uri (the bare
    /// <c>local:</c> namespace is already claimed by LocalSource, and the playing uri publishes verbatim to Connect).</summary>
    public const string SourceKeyPrefix = "local:video:";

    /// <summary>The stable video-source key for this override (<c>"local:video:" + Id</c>).</summary>
    public string SourceKey => SourceKeyPrefix + Id;
}

public interface IStore
{
    // library sets (collections) + per-item sync state (+ the server add timestamp; 0 = unknown → preserve existing)
    void SetSaved(string setId, string uri, bool saved, SyncState sync);
    void SetSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs);
    bool IsSaved(string setId, string uri);
    IReadOnlyList<string> SavedUris(string setId);
    IReadOnlyList<SavedItem> SavedItems(string setId);
    // ordered playlist membership + the rootlist (the queryable lists the catalog joins onto the shared entities at read)
    void SetMembership(string playlistUri, IReadOnlyList<PlaylistMember> rows, byte[]? baseRev);
    /// <summary>True when a playlist has a known membership baseline, including a valid empty playlist.</summary>
    bool HasMembership(string playlistUri);
    IReadOnlyList<PlaylistMember> Membership(string playlistUri);
    byte[]? PlaylistRevision(string playlistUri);
    void SetRootlist(IReadOnlyList<RootlistEntry> entries);
    /// <summary>Set the rootlist AND its opaque revision. The 1-arg overload preserves the stored revision (header
    /// hydration must not wipe it); this overload sets it (null clears). See §2.6.</summary>
    void SetRootlist(IReadOnlyList<RootlistEntry> entries, byte[]? rev);
    byte[]? RootlistRevision();
    IReadOnlyList<RootlistEntry> Rootlist();
    // reactivity
    long Version(string uri);
    void Bump(string uri, CollectionKind? kind = null);
    IObservable<StoreChange> Changes { get; }
    /// <summary>Coalesce a burst of writes (e.g. a 10k-entity metadata sync) into ONE change signal, not one per entity.</summary>
    IDisposable BeginBulk();

}

public sealed class InMemoryStore : IStore
{
    readonly object _gate = new();
    readonly Dictionary<string, long> _versions = new();
    readonly Dictionary<(string set, string uri), (SyncState Sync, long AddedAt)> _saved = new();
    readonly Dictionary<string, HashSet<string>> _savedBySet = new();   // set → uris, so SavedUris is O(set), not O(all-saved)
    readonly Dictionary<string, (IReadOnlyList<PlaylistMember> Rows, byte[]? Rev)> _membership = new();
    IReadOnlyList<RootlistEntry> _rootlist = Array.Empty<RootlistEntry>();
    byte[]? _rootlistRev;
    readonly SimpleSubject<StoreChange> _changes = new();
    public IObservable<StoreChange> Changes => _changes;

    public void SetSaved(string setId, string uri, bool saved, SyncState sync) => SetSavedCore(setId, uri, saved, sync, 0);
    public void SetSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs) => SetSavedCore(setId, uri, saved, sync, addedAtMs);

    /// <summary>The SetSaved core with no-op elision (§7.4): returns whether the write actually changed the store. A save
    /// that repeats the SAME (set,uri,SyncState) — or an unsave of an already-absent (set,uri) — writes nothing and does
    /// NOT Bump/emit, turning every idempotent echo/delta-overlap into literal silence. A same-key write with a DIFFERENT
    /// SyncState (Pending→Confirmed) still writes + bumps. <paramref name="addedAtMs"/> 0 preserves the existing add
    /// timestamp; a non-zero refinement of an otherwise-identical row updates the timestamp silently (metadata, not a
    /// state change). The change decision
    /// is made under _gate; the Bump (emit) fires outside it (the cardinal rule).</summary>
    internal bool SetSavedCore(string setId, string uri, bool saved, SyncState sync, long addedAtMs)
    {
        bool changed;
        lock (_gate)
        {
            bool present = _saved.TryGetValue((setId, uri), out var cur);
            if (saved)
            {
                changed = !present || cur.Sync != sync;   // new, or a state transition (e.g. Pending→Confirmed)
                long at = addedAtMs != 0 ? addedAtMs : (present ? cur.AddedAt : 0);
                if (changed || (present && at != cur.AddedAt))
                {
                    _saved[(setId, uri)] = (sync, at);
                    if (!_savedBySet.TryGetValue(setId, out var set)) _savedBySet[setId] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(uri);
                }
            }
            else
            {
                changed = present;                    // no-op when already absent
                if (changed)
                {
                    _saved.Remove((setId, uri));
                    if (_savedBySet.TryGetValue(setId, out var set)) set.Remove(uri);
                }
            }
        }
        if (changed) Bump(uri, KindForSet(setId));
        return changed;
    }

    public bool IsSaved(string setId, string uri)
    {
        lock (_gate) return _saved.ContainsKey((setId, uri));
    }

    /// <summary>The library sets that have at least one member (the warm-set + pin-gate enumeration seam). A handful of
    /// ids ("liked"/"albums"/"artists"/"shows"/"episodes"/…), so a copy is free.</summary>
    public IReadOnlyList<string> SavedSetIds()
    {
        lock (_gate) return new List<string>(_savedBySet.Keys);
    }

    /// <summary>True when <paramref name="uri"/> is a member of ANY library set — the in-memory mirror of
    /// <c>SELECT 1 FROM collection_items WHERE item_uri=?</c>, which is the first leg of the cold-write pin gate. O(number
    /// of sets), not O(saved), because each set is its own hash set.</summary>
    public bool IsSavedAnywhere(string uri)
    {
        lock (_gate)
        {
            foreach (var kv in _savedBySet) if (kv.Value.Contains(uri)) return true;
            return false;
        }
    }

    public IReadOnlyList<string> SavedUris(string setId)
    {
        lock (_gate) return _savedBySet.TryGetValue(setId, out var set) ? new List<string>(set) : new List<string>();
    }

    public IReadOnlyList<SavedItem> SavedItems(string setId)
    {
        lock (_gate)
        {
            if (!_savedBySet.TryGetValue(setId, out var set)) return Array.Empty<SavedItem>();
            var list = new List<SavedItem>(set.Count);
            foreach (var uri in set)
                list.Add(new SavedItem(uri, _saved.TryGetValue((setId, uri), out var v) ? v.AddedAt : 0));
            return list;
        }
    }

    public void SetMembership(string playlistUri, IReadOnlyList<PlaylistMember> rows, byte[]? baseRev)
    {
        lock (_gate) _membership[playlistUri] = (rows, baseRev);
        Bump(playlistUri);
    }

    public IReadOnlyList<PlaylistMember> Membership(string playlistUri)
    {
        lock (_gate) return _membership.TryGetValue(playlistUri, out var m) ? m.Rows : Array.Empty<PlaylistMember>();
    }

    public bool HasMembership(string playlistUri)
    {
        lock (_gate) return _membership.ContainsKey(playlistUri);
    }

    /// <summary>Drop a resident membership baseline (the WARM-tier evictor calls this); the cold tier keeps it, so the
    /// next access rehydrates it.</summary>
    public void EvictMembership(string playlistUri) { lock (_gate) _membership.Remove(playlistUri); }
    public int ResidentMembershipCount { get { lock (_gate) return _membership.Count; } }

    public byte[]? PlaylistRevision(string playlistUri)
    {
        lock (_gate) return _membership.TryGetValue(playlistUri, out var m) ? m.Rev : null;
    }

    // Row-only projection changes preserve the stored protocol revision.
    public void SetRootlist(IReadOnlyList<RootlistEntry> entries)
    {
        lock (_gate) _rootlist = entries;
        Bump("rootlist");
    }

    // 2-arg: set the rootlist AND its revision (null clears).
    public void SetRootlist(IReadOnlyList<RootlistEntry> entries, byte[]? rev)
    {
        lock (_gate) { _rootlist = entries; _rootlistRev = rev; }
        Bump("rootlist");
    }

    public byte[]? RootlistRevision() { lock (_gate) return _rootlistRev; }

    public IReadOnlyList<RootlistEntry> Rootlist()
    {
        lock (_gate) return _rootlist;
    }

    public long Version(string uri)
    {
        lock (_gate) return _versions.TryGetValue(uri, out var v) ? v : 0;
    }

    public void Bump(string uri, CollectionKind? kind = null)
    {
        bool suppressed;
        lock (_gate) { _versions[uri] = _versions.TryGetValue(uri, out var v) ? v + 1 : 1; suppressed = _bulkDepth > 0; }
        if (!suppressed) _changes.OnNext(new StoreChange(uri, Kind: kind));   // during a bulk the per-uri signals are coalesced
    }

    static CollectionKind? KindForSet(string setId) => setId switch
    {
        "albums" => CollectionKind.Albums,
        "artists" => CollectionKind.Artists,
        "shows" or "episodes" => CollectionKind.Shows,
        "playlists" => CollectionKind.Playlists,
        "liked" => CollectionKind.Liked,
        "pins" => CollectionKind.Pins,
        _ => null,
    };

    int _bulkDepth;

    /// <summary>Opens a bulk scope: per-URI change signals are suppressed until the outermost scope closes, then ONE
    /// StoreChange.Bulk fires (subscribers full-recompute). NOTE — suppression is store-wide: a concurrent unrelated write
    /// (e.g. a user save) during a bulk sync is also folded into that single Bulk signal rather than emitting its own
    /// per-URI change. Correct (the Bulk recompute covers it), just coarser; acceptable since bulk syncs are short.</summary>
    public IDisposable BeginBulk()
    {
        lock (_gate) _bulkDepth++;
        return new BulkScope(this);
    }

    void EndBulk()
    {
        bool fire;
        lock (_gate) fire = --_bulkDepth == 0;
        if (fire) _changes.OnNext(StoreChange.Bulk);
    }

    sealed class BulkScope(InMemoryStore store) : IDisposable
    {
        bool _done;
        public void Dispose() { if (_done) return; _done = true; store.EndBulk(); }
    }
}
