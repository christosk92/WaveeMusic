using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Wavee.Core;

/// <summary>User-created playlists — the Mutations facet's playlist edits (docs/plans/wavee/architecture.md §2 "Playlists, mutations
/// &amp; folders"). An in-process catalog source owning <c>wavee:playlist:*</c> that holds the session's created
/// playlists + their tracks (snapshots), so a created / added-to playlist appears in the sidebar list and opens through
/// the shared detail surface; <see cref="ResolveContext"/> lets the player play it. Every membership gets its own stable
/// row uid, so duplicate tracks remain independently movable.</summary>
public sealed class UserPlaylistSource : ICatalogSource
{
    sealed class Entry { public string Name = ""; public readonly List<Track> Tracks = new(); }
    readonly Dictionary<string, Entry> _playlists = new();
    readonly SimpleSubject<int> _changed = new(0);
    int _seq, _version;
    long _itemSeq;

    public string Id => "user-playlists";
    // `wavee:playlist:*` is exactly EntityProviders.User (hydration-facade-design.md §1.1).
    public bool Owns(string uri) => EntityUri.Parse(uri).Provider == EntityProviders.User;
    public SourceCapabilities Capabilities => SourceCapabilities.Catalog | SourceCapabilities.Mutations;

    /// <summary>When false, <see cref="GetPlaylistsAsync"/> returns empty — the real backend lists only synced
    /// <c>spotify:playlist:*</c> rows; session-only <c>wavee:playlist:*</c> stubs must not appear in pickers/sidebar.</summary>
    public bool ExposeInCatalog { get; set; } = true;

    /// <summary>Bumps on every create / add — the bridge mirrors it so the sidebar re-reads the playlist list.</summary>
    public IObservable<int> PlaylistsChanged => _changed;

    public string CreatePlaylist(string name)
    {
        string uri = "wavee:playlist:" + (++_seq);
        _playlists[uri] = new Entry { Name = string.IsNullOrWhiteSpace(name) ? "New Playlist" : name };
        Bump();
        return uri;
    }

    public void AddTrack(string playlistUri, Track track)
    {
        if (_playlists.TryGetValue(playlistUri, out var e))
        { e.Tracks.Add(Stamp(track)); Bump(); }
    }

    public void InsertTracks(string playlistUri, IReadOnlyList<Track> tracks, int atIndex)
    {
        if (!_playlists.TryGetValue(playlistUri, out var e)) return;
        int at = Math.Clamp(atIndex, 0, e.Tracks.Count);
        for (int i = 0; i < tracks.Count; i++) e.Tracks.Insert(at + i, Stamp(tracks[i]));
        if (tracks.Count > 0) Bump();
    }

    public void RemoveRows(string playlistUri, IReadOnlyList<PlaylistRowRef> rows)
    {
        if (!_playlists.TryGetValue(playlistUri, out var e) || rows.Count == 0) return;
        var indices = ResolveRows(e.Tracks, rows);
        for (int i = indices.Count - 1; i >= 0; i--) e.Tracks.RemoveAt(indices[i]);
        if (indices.Count > 0) Bump();
    }

    public void MoveRows(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, int toIndex)
    {
        if (!_playlists.TryGetValue(playlistUri, out var e) || rows.Count == 0) return;
        var indices = ResolveRows(e.Tracks, rows);
        if (indices.Count == 0) return;
        var moved = new List<Track>(indices.Count);
        for (int i = 0; i < indices.Count; i++) moved.Add(e.Tracks[indices[i]]);
        int removedBefore = 0;
        for (int i = 0; i < indices.Count; i++) if (indices[i] < toIndex) removedBefore++;
        for (int i = indices.Count - 1; i >= 0; i--) e.Tracks.RemoveAt(indices[i]);
        int at = Math.Clamp(toIndex - removedBefore, 0, e.Tracks.Count);
        e.Tracks.InsertRange(at, moved);
        Bump();
    }

    Track Stamp(Track track) => track with { ContextUid = "wavee-row:" + (++_itemSeq) };

    static List<int> ResolveRows(IReadOnlyList<Track> tracks, IReadOnlyList<PlaylistRowRef> rows)
    {
        var indices = new SortedSet<int>();
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            int found = -1;
            if (row.ItemId.Length > 0)
                for (int i = 0; i < tracks.Count; i++)
                    if (string.Equals(tracks[i].ContextUid, row.ItemId, StringComparison.Ordinal)) { found = i; break; }
            if (found < 0 && (uint)row.Index < (uint)tracks.Count
                && string.Equals(tracks[row.Index].Uri, row.Uri, StringComparison.Ordinal)) found = row.Index;
            if (found >= 0) indices.Add(found);
        }
        return new List<int>(indices);
    }

    /// <summary>Ensure at least one user playlist exists (for a no-picker "add to playlist") and return its uri + name.</summary>
    public (string Uri, string Name) DefaultPlaylist()
    {
        var first = _playlists.FirstOrDefault();
        if (first.Key is { } uri) return (uri, first.Value.Name);
        string created = CreatePlaylist("My Playlist");
        return (created, _playlists[created].Name);
    }

    /// <summary>Player context resolver: a user playlist's tracks (null if not one of ours) — wired into the fake player.</summary>
    public IReadOnlyList<Track>? ResolveContext(string contextUri) =>
        _playlists.TryGetValue(contextUri, out var e) ? e.Tracks.ToArray() : null;

    void Bump() { _version++; _changed.OnNext(_version); }

    // ── ICatalogSource: only the playlist reads are non-empty ──
    public Task<Playlist?> GetPlaylistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
    {
        if (!_playlists.TryGetValue(uri, out var e)) return Task.FromResult<Playlist?>(null);
        return Task.FromResult<Playlist?>(new Playlist("up", uri, e.Name, "Created on this device", "You", null,
            e.Tracks.Count, e.Tracks.ToArray(),
            Owner: new Owner("you", "You", null),
            Capabilities: new PlaylistCapabilities(true, true, true, false, true), Source: "user"));
    }

    public Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        if (!ExposeInCatalog) return Task.FromResult<IReadOnlyList<PlaylistSummary>>(System.Array.Empty<PlaylistSummary>());
        var list = _playlists.Select(kv => new PlaylistSummary(kv.Key, kv.Value.Name, "You", kv.Value.Tracks.Count, null, CanEdit: true, IsOwner: true)).ToList();
        return Task.FromResult<IReadOnlyList<PlaylistSummary>>(list);
    }

    public async IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var t = _playlists.TryGetValue(contextUri, out var e) ? e.Tracks.ToArray() : System.Array.Empty<Track>();
        yield return new TrackPage(t, t.Length, t.Length);
    }

    public Task<Album?> GetAlbumAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default) => Task.FromResult<Album?>(null);
    public Task<Artist?> GetArtistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default) => Task.FromResult<Artist?>(null);
    public Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LibraryItem>>(System.Array.Empty<LibraryItem>());
    public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Album>>(System.Array.Empty<Album>());
    public Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Artist>>(System.Array.Empty<Artist>());
    public Task<IReadOnlyList<Track>> GetLikedSongsAsync(HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Track>>(System.Array.Empty<Track>());
    public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default) => Task.FromResult(SearchResults.Empty);
    public Task<HomeContribution> GetHomeAsync(CancellationToken ct = default) => Task.FromResult(new HomeContribution(System.Array.Empty<HomeGroup>(), 60));
    public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new LibraryStats(0, 0, 0, 0));
}
