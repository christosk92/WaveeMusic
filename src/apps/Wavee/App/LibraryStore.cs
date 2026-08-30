using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The root-level library cache (docs/plans/wavee/architecture.md §3 cache-first instant paint; §6 "a LibraryBridge" for delta
/// streams). A sibling of <see cref="LibraryBridge"/>: built in <see cref="Services"/>, <see cref="Activate"/>-d once at
/// the app root (which never unmounts). It holds the cached collections as engine <see cref="Loadable{T}"/> signals so
/// every page READS already-loaded data — ZERO refetch on navigation, instant paint — and it processes source deltas
/// (saves / playlist edits / future collection deltas) EVEN WHEN NO PAGE IS MOUNTED. Per-entity detail caches make the
/// library master-detail right pane + detail revisits instant (A→B→A). Provided once via <see cref="Slot"/>.
/// </summary>
public sealed class LibraryStore
{
    public static readonly Context<LibraryStore?> Slot = new(null);

    readonly IMusicLibrary _lib;
    readonly IMutationSource _mut;
    readonly UserPlaylistSource _pls;
    readonly ICollectionEvents? _events;
    readonly List<IDisposable> _subs = [];
    Action<Action> _post = static a => a();
    bool _active;

    // ── cached collections: each Loadable allocated ONCE here, reused by every page forever ──
    public Loadable<IReadOnlyList<Album>> Albums { get; } = Loadable<IReadOnlyList<Album>>.Pending(Array.Empty<Album>());
    public Loadable<IReadOnlyList<Artist>> Artists { get; } = Loadable<IReadOnlyList<Artist>>.Pending(Array.Empty<Artist>());
    public Loadable<IReadOnlyList<PlaylistSummary>> Playlists { get; } = Loadable<IReadOnlyList<PlaylistSummary>>.Pending(Array.Empty<PlaylistSummary>());
    public Loadable<IReadOnlyList<Track>> Liked { get; } = Loadable<IReadOnlyList<Track>>.Pending(Array.Empty<Track>());
    public Loadable<IReadOnlyList<Show>> Shows { get; } = Loadable<IReadOnlyList<Show>>.Pending(Array.Empty<Show>());
    public Loadable<LibraryStats> Stats { get; } = Loadable<LibraryStats>.Pending(new LibraryStats(0, 0, 0, 0));

    /// <summary>The folder-capable playlist TREE. The flat <see cref="Playlists"/> cell stays — Menus/AddTo, the Classic
    /// sidebar list and every other pre-folder consumer read it — so this is an ADDITIVE sibling, not a replacement.
    /// Same in-place Refresh discipline: a rootlist delta swaps the tree without a skeleton flash.</summary>
    public Loadable<IReadOnlyList<PlaylistNode>> PlaylistTree { get; } =
        Loadable<IReadOnlyList<PlaylistNode>>.Pending(Array.Empty<PlaylistNode>());

    /// <summary>uri → added-at (unix ms) for the timestamped saved collections (albums / artists / shows). The Store HAS
    /// these stamps (SavedItem.AddedAtMs) but the Album/Artist/Show records have nowhere to carry them, so they ride this
    /// side-channel instead of a breaking record change. Absent uri ⇒ unknown (the sidebar's sort falls back honestly).</summary>
    public Loadable<IReadOnlyDictionary<string, long>> AddedAt { get; } =
        Loadable<IReadOnlyDictionary<string, long>>.Pending(EmptyAddedAt);

    /// <summary>The shared empty added-at map (also the Pending seed, so a reader never null-checks).</summary>
    public static IReadOnlyDictionary<string, long> EmptyAddedAt => SidebarTree.NoAddedAt;

    bool _albumsLoaded, _artistsLoaded, _playlistsLoaded, _likedLoaded, _showsLoaded, _statsLoaded;
    bool _treeLoaded, _addedAtLoaded;

    // ── per-entity detail caches (master-detail right pane + detail revisits) ──
    readonly Dictionary<string, Loadable<DetailModel>> _details = new(StringComparer.Ordinal);
    readonly Dictionary<string, Loadable<Artist>> _artistDetails = new(StringComparer.Ordinal);
    // Bound the detail caches so a long browsing session cannot accumulate them without limit (each entry is a full
    // DetailModel / Artist — tens of KB). A miss simply re-fetches (self-healing), so an LRU cap is safe. The MRU key sits
    // at the END of the recency list; on overflow the least-recently-used entry is dropped. MemoryGovernor sheds further
    // under real memory pressure via ShedDetails. (All access is UI-thread — same context that mutates the dictionaries.)
    const int DetailCacheCap = 48;
    readonly List<string> _detailLru = new();
    readonly List<string> _artistLru = new();

    public LibraryStore(IMusicLibrary lib, IMutationSource mut, UserPlaylistSource pls, ICollectionEvents? events)
    {
        _lib = lib; _mut = mut; _pls = pls; _events = events;
    }

    // ── lazy one-shot warmers (idempotent — first access OR the eager warm at Activate) ──
    public void EnsureAlbums() { if (_albumsLoaded) return; _albumsLoaded = true; Fill(Albums, _lib.GetAlbumsAsync); }
    public void EnsureArtists() { if (_artistsLoaded) return; _artistsLoaded = true; Fill(Artists, _lib.GetArtistsAsync); }
    public void EnsurePlaylists() { if (_playlistsLoaded) return; _playlistsLoaded = true; Fill(Playlists, _lib.GetPlaylistsAsync); }
    public void EnsureLiked() { if (_likedLoaded) return; _likedLoaded = true; Fill(Liked, ct => _lib.GetLikedSongsAsync(ct: ct)); }
    public void EnsureShows() { if (_showsLoaded) return; _showsLoaded = true; Fill(Shows, _lib.GetShowsAsync); }
    public void EnsureStats() { if (_statsLoaded) return; _statsLoaded = true; Fill(Stats, _lib.GetStatsAsync); }
    /// <summary>Idempotent warmer for the folder-capable tree (mirrors <see cref="EnsurePlaylists"/>).</summary>
    public void EnsurePlaylistTree() { if (_treeLoaded) return; _treeLoaded = true; Fill(PlaylistTree, _lib.GetPlaylistTreeAsync); }
    /// <summary>Idempotent warmer for the added-at side-channel (a cheap local read of the saved sets).</summary>
    public void EnsureAddedAt() { if (_addedAtLoaded) return; _addedAtLoaded = true; Fill(AddedAt, _lib.GetLibraryAddedAtAsync); }
    /// <summary>Eager warm of the cheap collections (Liked is large + lazy). Synthetic data is synchronous → Ready next frame.
    /// The tree + added-at cells are cheap local reads too, so a V3/Curated sidebar paints from warm cells on its first
    /// frame exactly as Classic does today.</summary>
    public void WarmCheap() { EnsureAlbums(); EnsureArtists(); EnsurePlaylists(); EnsurePlaylistTree(); EnsureAddedAt(); EnsureShows(); EnsureStats(); }

    void Fill<T>(Loadable<T> cell, Func<CancellationToken, Task<T>> read)
    {
        _ = Run();
        async Task Run()
        {
            try { var v = await read(default).ConfigureAwait(false); _post(() => cell.SetReady(v)); }
            catch (Exception e) { _post(() => cell.SetFailed(e)); }
        }
    }

    /// <summary>Re-read WITHOUT flipping to Pending — keep the old value visible and swap in place (the cache-first
    /// "+ background refresh" path: no skeleton flash on a delta).</summary>
    void Refresh<T>(Loadable<T> cell, Func<CancellationToken, Task<T>> read)
    {
        _ = Run();
        async Task Run()
        {
            try { var v = await read(default).ConfigureAwait(false); _post(() => cell.SetReady(v)); }
            catch { /* keep last-good on a refresh failure */ }
        }
    }

    // ── off-page freshness: subscribe the source deltas at the root (always alive) ──
    public void Activate(Action<Action> post)
    {
        if (_active) return;
        _active = true;
        _post = post;

        _subs.Add(_mut.SavedChanged.Subscribe(_ => post(() =>
        {
            if (_likedLoaded) Refresh(Liked, ct => _lib.GetLikedSongsAsync(ct: ct));
            if (_statsLoaded) Refresh(Stats, _lib.GetStatsAsync);    // the liked count lives in stats
            if (_addedAtLoaded) Refresh(AddedAt, _lib.GetLibraryAddedAtAsync);   // a save/unsave moves a "recently added" stamp
            _details.Remove("liked"); _detailLru.Remove("liked");    // the liked track SET changed → reload its detail fresh
        })));
        _subs.Add(_pls.PlaylistsChanged.Subscribe(_ => post(() =>
        {
            if (_playlistsLoaded) Refresh(Playlists, _lib.GetPlaylistsAsync);
            if (_treeLoaded) Refresh(PlaylistTree, _lib.GetPlaylistTreeAsync);   // the rootlist delta reshapes the tree too
            if (_statsLoaded) Refresh(Stats, _lib.GetStatsAsync);
            InvalidateWhere(k => k.Contains("playlist", StringComparison.Ordinal));
        })));
        if (_events is not null)
            _subs.Add(_events.CollectionsChanged.Subscribe(k => post(() => OnCollectionsChanged(k))));

        WarmCheap();   // eager: by the time the user clicks Albums/Artists/Podcasts the signal is already Ready
    }

    void OnCollectionsChanged(CollectionKind kind)
    {
        switch (kind)
        {
            case CollectionKind.Albums when _albumsLoaded: Refresh(Albums, _lib.GetAlbumsAsync); break;
            case CollectionKind.Artists when _artistsLoaded: Refresh(Artists, _lib.GetArtistsAsync); break;
            case CollectionKind.Shows when _showsLoaded: Refresh(Shows, _lib.GetShowsAsync); break;
            case CollectionKind.Playlists when _playlistsLoaded: Refresh(Playlists, _lib.GetPlaylistsAsync); break;
            case CollectionKind.Liked when _likedLoaded: Refresh(Liked, ct => _lib.GetLikedSongsAsync(ct: ct)); break;
        }
        // The tree rides the Playlists arm (the rootlist is what changed); the added-at map rides EVERY kind, because any
        // save/unsave in any collection moves a stamp.
        if (kind == CollectionKind.Playlists && _treeLoaded) Refresh(PlaylistTree, _lib.GetPlaylistTreeAsync);
        if (_addedAtLoaded) Refresh(AddedAt, _lib.GetLibraryAddedAtAsync);
        if (_statsLoaded) Refresh(Stats, _lib.GetStatsAsync);
    }

    // ── per-entity detail caches (instant A→B→A; the loader is injected by the caller) ──
    public Loadable<DetailModel> Detail(string routeKey, Func<CancellationToken, Task<DetailModel>> load, DetailModel seed)
    {
        if (_details.TryGetValue(routeKey, out var l)) { Touch(_detailLru, routeKey); return l; }   // hit → already Ready → instant
        l = Loadable<DetailModel>.Pending(seed);
        _details[routeKey] = l;
        Touch(_detailLru, routeKey);
        EvictOldest(_details, _detailLru, DetailCacheCap);
        Fill(l, load);
        return l;
    }

    public Loadable<Artist> ArtistDetail(string uri, Func<CancellationToken, Task<Artist>> load, Artist seed)
    {
        if (_artistDetails.TryGetValue(uri, out var l)) { Touch(_artistLru, uri); return l; }
        l = Loadable<Artist>.Pending(seed);
        _artistDetails[uri] = l;
        Touch(_artistLru, uri);
        EvictOldest(_artistDetails, _artistLru, DetailCacheCap);
        Fill(l, load);
        return l;
    }

    // Recency: move key to the MRU end. O(n) but n ≤ cap (small), all on the UI thread.
    static void Touch(List<string> lru, string key) { lru.Remove(key); lru.Add(key); }
    static void EvictOldest<T>(Dictionary<string, T> map, List<string> lru, int cap)
    {
        while (lru.Count > cap) { string oldest = lru[0]; lru.RemoveAt(0); map.Remove(oldest); }
    }

    /// <summary>MemoryGovernor shed hook — trim the per-entity detail caches to <paramref name="keep"/> most-recent each,
    /// returning an approximate bytes-freed estimate. Safe under pressure: a dropped detail re-fetches on its next open.</summary>
    public long ShedDetails(int keep)
    {
        int before = _details.Count + _artistDetails.Count;
        EvictOldest(_details, _detailLru, keep);
        EvictOldest(_artistDetails, _artistLru, keep);
        return (long)(before - (_details.Count + _artistDetails.Count)) * 24_000;   // rough: a detail model averages tens of KB
    }

    /// <summary>Resident per-entity detail-cache entry count (census attribution): the master-detail models + the
    /// standalone-artist models. UI-thread read (the census runs on the frame thread that mutates these).</summary>
    public int DetailCount => _details.Count + _artistDetails.Count;

    /// <summary>Pin every uri reachable from a cached detail model so the entity evictor never sheds the working set a
    /// still-cached page will re-render: each detail's context/album/playlist uri + its track rows + its billed artists,
    /// and each cached artist's own uri + its top-track / top-album child uris. On-demand (called inside the shed
    /// callback); reads the Loadable values with Peek (no signal subscription). UI-thread, like the caches themselves.</summary>
    public void CollectPinnedUris(ISet<string> pins)
    {
        foreach (var cell in _details.Values)
        {
            var m = cell.Value.Peek();                     // the seed (or the loaded model) — never null
            if (m.ContextUri is { Length: > 0 } cu) pins.Add(cu);
            var tracks = m.Tracks;
            for (int i = 0; i < tracks.Count; i++) if (tracks[i].Uri is { Length: > 0 } tu) pins.Add(tu);
            var artists = m.Artists;
            for (int i = 0; i < artists.Count; i++) if (artists[i].Uri is { Length: > 0 } au) pins.Add(au);
        }
        foreach (var (uri, cell) in _artistDetails)
        {
            pins.Add(uri);
            var a = cell.Value.Peek();
            if (a.TopTracks is { } top) for (int i = 0; i < top.Count; i++) if (top[i].Uri is { Length: > 0 } tu) pins.Add(tu);
            if (a.TopAlbums is { } albums) for (int i = 0; i < albums.Count; i++) if (albums[i].Uri is { Length: > 0 } bu) pins.Add(bu);
        }
    }

    void InvalidateWhere(Func<string, bool> match)
    {
        List<string>? keys = null;
        foreach (var k in _details.Keys) if (match(k)) (keys ??= new()).Add(k);
        if (keys is not null) foreach (var k in keys) { _details.Remove(k); _detailLru.Remove(k); }
    }
}
